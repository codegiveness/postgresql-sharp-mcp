using System.Buffers;
using System.Data;
using System.Text.Json;
using Npgsql;

namespace PostgreSqlMcp.Core;

public sealed class SqlExecutor(ServerOptions options, DatabaseRegistry registry) : IDisposable
{
    private readonly SemaphoreSlim _calls = new(options.MaxConcurrentCalls);

    public Task<QueryPage> QueryAsync(string database, string sql, IReadOnlyDictionary<string, object?>? parameters = null,
        int? limit = null, int offset = 0, bool readOnly = true, CancellationToken ct = default, string? target = null, bool callerStatement = false) =>
        WithSessionAsync(database, (session, token) => session.QueryAsync(sql, parameters, limit, offset, token, callerStatement), readOnly, ct, target);

    public async Task<T> WithSessionAsync<T>(string database, Func<SqlSession, CancellationToken, Task<T>> action,
        bool readOnly = true, CancellationToken ct = default, string? target = null)
    {
        DatabaseRegistry.DatabaseSelection selection = registry.Resolve(database, target);
        if (!readOnly && !options.Unrestricted) throw new ToolException("read_only", "This server is in restricted access mode and refuses writes; use read_only=true.");
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
            // Control statements are fixed server text. User SQL cannot end this transaction.
            await using (var setup = new NpgsqlCommand($"SET TRANSACTION {(readOnly ? "READ ONLY" : "READ WRITE")}; SET LOCAL standard_conforming_strings=on; SET LOCAL statement_timeout='{options.QueryTimeout * 1000}ms'; SET LOCAL lock_timeout='{options.QueryTimeout * 1000}ms'", connection, transaction)
                { CommandTimeout = options.QueryTimeout })
                await setup.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            var session = new SqlSession(database, connection, transaction, options, readOnly);
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

public sealed class SqlSession(string database, NpgsqlConnection connection, NpgsqlTransaction transaction,
    ServerOptions options, bool readOnly)
{
    public NpgsqlConnection Connection { get; } = connection;
    public NpgsqlTransaction Transaction { get; } = transaction;

    /// <param name="callerStatement">The SQL came from the tool caller, so PostgreSQL error positions are reported relative to it.</param>
    public async Task<QueryPage> QueryAsync(string sql, IReadOnlyDictionary<string, object?>? parameters = null,
        int? limit = null, int offset = 0, CancellationToken ct = default, bool callerStatement = false)
    {
        sql = SqlGuard.Validate(sql, out string kind);
        string statement = sql;
        int prefixLength = 0;
        int rowLimit = limit ?? Math.Min(100, options.MaxRows);
        if (rowLimit < 1 || rowLimit > options.MaxRows) throw new ToolException("invalid_limit", $"limit must be 1..{options.MaxRows}.");
        if (offset < 0 || offset > 1000000) throw new ToolException("invalid_offset", "offset must be 0..1000000. Prefer SQL keyset pagination for deep pages.");
        if (!readOnly && offset != 0) throw new ToolException("invalid_offset", "Write operations cannot be re-executed for pagination.");
        bool serverPage = readOnly && kind is "SELECT" or "WITH" or "VALUES" or "TABLE";
        if (serverPage)
        {
            prefixLength = PagePrefix.Length;
            sql = $"{PagePrefix}{sql}\n) AS mcp_page LIMIT {rowLimit + 1} OFFSET {offset}";
        }
        try { return await ReadAsync(sql, parameters, rowLimit, offset, serverPage, ct).ConfigureAwait(false); }
        catch (PostgresException ex) when (callerStatement && ToolReply.MarkStatementPosition(ex, prefixLength, statement)) { throw; }
    }

    private const string PagePrefix = "SELECT * FROM (\n";

    private async Task<QueryPage> ReadAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, int rowLimit, int offset,
        bool serverPage, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, Connection, Transaction) { CommandTimeout = options.QueryTimeout };
        if (parameters is not null)
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
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
