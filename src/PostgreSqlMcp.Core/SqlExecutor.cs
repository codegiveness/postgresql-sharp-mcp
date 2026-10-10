using System.Buffers;
using System.Data;
using System.Text.Json;
using Npgsql;

namespace PostgreSqlMcp.Core;

public sealed class SqlExecutor(ServerOptions options, DatabaseRegistry registry) : IDisposable
{
    private readonly SemaphoreSlim _calls = new(options.MaxConcurrentCalls);

    public Task<QueryPage> QueryAsync(string database, string sql, IReadOnlyDictionary<string, object?>? parameters = null,
        int? limit = null, int offset = 0, bool readOnly = true, CancellationToken ct = default, string? target = null) =>
        WithSessionAsync(database, (session, token) => session.QueryAsync(sql, parameters, limit, offset, token), readOnly, ct, target);

    public async Task<T> WithSessionAsync<T>(string database, Func<SqlSession, CancellationToken, Task<T>> action,
        bool readOnly = true, CancellationToken ct = default, string? target = null)
    {
        DatabaseRegistry.DatabaseSelection selection = registry.Resolve(database, target);
        if (!readOnly && !options.Unrestricted) throw new ToolException("read_only", "Writes require server access-mode unrestricted and read_only=false.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.QueryTimeout));
        CancellationToken token = deadline.Token;
        bool entered = false;
        DatabaseRegistry.SourceEntry? source = null;
        try
        {
            await _calls.WaitAsync(token).ConfigureAwait(false); entered = true;
            source = await registry.AcquireAsync(selection, token).ConfigureAwait(false);
            await using var connection = await source.Source.OpenConnectionAsync(token).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            // Control statements are fixed server text. User SQL cannot end this transaction. The session sends them
            // in the same batch as its first statement, so setup costs no extra network round trip.
            var session = new SqlSession(database, connection, transaction, options, readOnly,
            [
                readOnly ? "SET TRANSACTION READ ONLY" : "SET TRANSACTION READ WRITE",
                "SET LOCAL standard_conforming_strings=on",
                $"SET LOCAL statement_timeout='{options.QueryTimeout * 1000}ms'",
                $"SET LOCAL lock_timeout='{options.QueryTimeout * 1000}ms'"
            ]);
            T result = await action(session, token).ConfigureAwait(false);
            if (readOnly) await transaction.RollbackAsync(token).ConfigureAwait(false);
            else await transaction.CommitAsync(token).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new ToolException("timeout", $"Operation exceeded {options.QueryTimeout}s (including pool/queue wait)."); }
        finally
        {
            try
            {
                if (source is not null) await registry.ReleaseAsync(source).ConfigureAwait(false);
            }
            finally { if (entered) _calls.Release(); }
        }
    }

    public void Dispose() => _calls.Dispose();
}

/// <summary>
/// One server-owned transaction. The connection, transaction and batches are not exposed: every statement goes through
/// <see cref="ExecuteReaderAsync"/>, which builds the batch itself and prepends the pending transaction setup
/// (READ ONLY/READ WRITE and timeouts) to the first one, so no caller can run SQL before the transaction mode is set.
/// </summary>
public sealed class SqlSession
{
    private readonly string database;
    private readonly NpgsqlConnection connection;
    private readonly NpgsqlTransaction transaction;
    private readonly ServerOptions options;
    private readonly bool readOnly;
    private string[]? pendingSetup;

    internal SqlSession(string database, NpgsqlConnection connection, NpgsqlTransaction transaction,
        ServerOptions options, bool readOnly, string[] setup)
    {
        this.database = database;
        this.connection = connection;
        this.transaction = transaction;
        this.options = options;
        this.readOnly = readOnly;
        pendingSetup = setup;
    }

    /// <summary>
    /// Runs <paramref name="commands"/> as one batch, preceded by the transaction setup if it has not reached the server yet.
    /// Dispose the result to release the reader and the batch.
    /// </summary>
    public async Task<SessionReader> ExecuteReaderAsync(IReadOnlyList<NpgsqlBatchCommand> commands, CommandBehavior behavior,
        CancellationToken ct, int? timeout = null)
    {
        var batch = new NpgsqlBatch(connection, transaction) { Timeout = timeout ?? options.QueryTimeout };
        // Each batch command is one extended-protocol statement, so the setup is one command per statement.
        string[]? setup = pendingSetup;
        if (setup is not null)
            foreach (string statement in setup) batch.BatchCommands.Add(new NpgsqlBatchCommand(statement));
        foreach (NpgsqlBatchCommand command in commands) batch.BatchCommands.Add(command);
        try
        {
            NpgsqlDataReader reader = await batch.ExecuteReaderAsync(behavior, ct).ConfigureAwait(false);
            pendingSetup = null;
            return new SessionReader(batch, reader);
        }
        catch (PostgresException) when (setup is not null)
        {
            // The server processed the batch: either the setup ran or the transaction is aborted. Otherwise
            // (cancelled before sending, I/O failure) the setup stays pending for any later statement.
            pendingSetup = null;
            await batch.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch
        {
            await batch.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Runs one fixed control statement and discards its result.</summary>
    public async Task ExecuteAsync(string sql, CancellationToken ct, int? timeout = null)
    {
        await using SessionReader result = await ExecuteReaderAsync([new NpgsqlBatchCommand(sql)], CommandBehavior.Default, ct, timeout).ConfigureAwait(false);
    }

    /// <summary>Marks this session's pooled connections for disposal instead of reuse.</summary>
    public void ClearPool() => NpgsqlConnection.ClearPool(connection);

    public async Task<QueryPage> QueryAsync(string sql, IReadOnlyDictionary<string, object?>? parameters = null,
        int? limit = null, int offset = 0, CancellationToken ct = default)
    {
        sql = SqlGuard.Validate(sql, out string kind);
        int rowLimit = limit ?? Math.Min(100, options.MaxRows);
        if (rowLimit < 1 || rowLimit > options.MaxRows) throw new ToolException("invalid_limit", $"limit must be 1..{options.MaxRows}.");
        if (offset < 0 || offset > 1000000) throw new ToolException("invalid_offset", "offset must be 0..1000000. Prefer SQL keyset pagination for deep pages.");
        if (!readOnly && offset != 0) throw new ToolException("invalid_offset", "Write operations cannot be re-executed for pagination.");
        bool serverPage = readOnly && kind is "SELECT" or "WITH" or "VALUES" or "TABLE";
        if (serverPage)
            sql = $"SELECT * FROM (\n{sql}\n) AS mcp_page LIMIT {rowLimit + 1} OFFSET {offset}";
        var command = new NpgsqlBatchCommand(sql);
        if (parameters is not null)
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await using SessionReader result = await ExecuteReaderAsync([command], CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        NpgsqlDataReader reader = result.Reader;
        if (reader.FieldCount > 128) throw new ToolException("result_too_wide", "Select at most 128 columns.");
        var columns = new ColumnInfo[reader.FieldCount];
        for (int col = 0; col < columns.Length; col++)
        {
            string name = reader.GetName(col), type = reader.GetDataTypeName(col);
            if (name.Length > 128 || type.Length > 128) throw new ToolException("result_too_wide", "Use shorter column aliases/types.");
            columns[col] = new(name, type);
            Type fieldType = reader.GetFieldType(col);
            if ((fieldType.IsArray && fieldType != typeof(byte[])) || type.EndsWith("[]", StringComparison.Ordinal))
                throw new ToolException("unsupported_result_type", "Slice/cast array columns to text in SQL to bound their size; array materialization is not supported.");
        }
        var rowBuffer = new ArrayBufferWriter<byte>();
        using var rowWriter = new Utf8JsonWriter(rowBuffer);
        JsonSerializer.Serialize(rowWriter, columns, ToolReply.JsonOptions);
        rowWriter.Flush();
        int budget = options.MaxResultBytes - 2048 - rowBuffer.WrittenCount;
        if (budget < 256) throw new ToolException("result_too_wide", "Column metadata exceeds the result budget; select fewer columns.");
        var rows = new List<object?[]>(Math.Min(rowLimit, 128));
        var clips = new List<CellClip>();
        int skipped = serverPage ? offset : 0;
        while (skipped < offset && await reader.ReadAsync(ct).ConfigureAwait(false)) skipped++;
        bool truncated = false; string? reason = null;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (rows.Count == rowLimit) { truncated = true; reason = "row_limit"; break; }
            var row = new object?[columns.Length];
            List<CellClip>? rowClips = null;
            for (int col = 0; col < row.Length; col++)
            {
                if (await reader.IsDBNullAsync(col, ct).ConfigureAwait(false)) continue;
                bool clipped = false;
                Type fieldType = reader.GetFieldType(col);
                if (fieldType == typeof(string))
                {
                    using TextReader text = await reader.GetTextReaderAsync(col, ct).ConfigureAwait(false);
                    int cap = Math.Min(options.MaxCellChars, Math.Max(1, budget / 6));
                    char[] buffer = ArrayPool<char>.Shared.Rent(cap + 1);
                    try
                    {
                        int count = await text.ReadBlockAsync(buffer.AsMemory(0, cap + 1), ct).ConfigureAwait(false);
                        clipped = count > cap;
                        int length = Math.Min(count, cap);
                        if (clipped && length > 0 && char.IsHighSurrogate(buffer[length - 1])) length--;
                        row[col] = new string(buffer, 0, length);
                    }
                    finally { ArrayPool<char>.Shared.Return(buffer); }
                }
                else if (fieldType == typeof(byte[]))
                {
                    using Stream stream = reader.GetStream(col);
                    int cap = Math.Min(options.MaxCellChars * 3 / 4, Math.Max(1, budget / 8));
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(cap + 1);
                    try
                    {
                        int count = await stream.ReadAtLeastAsync(buffer.AsMemory(0, cap + 1), cap + 1,
                            throwOnEndOfStream: false, cancellationToken: ct).ConfigureAwait(false);
                        clipped = count > cap; row[col] = Convert.ToBase64String(buffer, 0, Math.Min(count, cap));
                    }
                    finally { ArrayPool<byte>.Shared.Return(buffer); }
                }
                else
                {
                    // Only bounded scalar mappings. Composite/provider-specific values must be projected explicitly.
                    if (!fieldType.IsPrimitive && fieldType != typeof(decimal) && fieldType != typeof(Guid) && fieldType != typeof(DateTime)
                        && fieldType != typeof(DateTimeOffset) && fieldType != typeof(DateOnly) && fieldType != typeof(TimeOnly) && fieldType != typeof(TimeSpan))
                        throw new ToolException("unsupported_result_type", $"Cast {columns[col].Type} to bounded text in SQL.");
                    try { row[col] = reader.GetValue(col); }
                    catch (Exception ex) when (ex is InvalidCastException or NotSupportedException or OverflowException)
                    {
                        throw new ToolException("unsupported_result_value",
                            $"Column {col} ({columns[col].Type}) cannot fit its scalar .NET mapping. Cast to text or extract a bounded scalar in SQL.");
                    }
                }
                if (clipped) (rowClips ??= []).Add(new(rows.Count, col));
            }
            rowBuffer.Clear();
            rowWriter.Reset(rowBuffer);
            JsonSerializer.Serialize(rowWriter, row, ToolReply.JsonOptions);
            rowWriter.Flush();
            int bytes = rowBuffer.WrittenCount + 1 + (rowClips?.Count ?? 0) * 32;
            if (bytes > budget)
            {
                if (rows.Count == 0) throw new ToolException("result_too_wide", "One row exceeds the result budget. Select fewer columns or shorten values in SQL.");
                truncated = true; reason = "byte_limit"; break;
            }
            budget -= bytes; rows.Add(row);
            if (rowClips is not null) clips.AddRange(rowClips);
        }
        int? affected = !truncated && reader.RecordsAffected >= 0 ? reader.RecordsAffected : null;
        return new(database, columns, rows, affected, offset, truncated && readOnly ? offset + rows.Count : null,
            truncated, reason, clips);
    }
}

/// <summary>A result reader that owns the batch it reads from; disposing it releases both.</summary>
public sealed class SessionReader : IAsyncDisposable
{
    private readonly NpgsqlBatch batch;

    internal SessionReader(NpgsqlBatch batch, NpgsqlDataReader reader)
    {
        this.batch = batch;
        Reader = reader;
    }

    public NpgsqlDataReader Reader { get; }

    public async ValueTask DisposeAsync()
    {
        try { await Reader.DisposeAsync().ConfigureAwait(false); }
        finally { await batch.DisposeAsync().ConfigureAwait(false); }
    }
}
