using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using Npgsql;

namespace PostgreSqlMcp.Core;

public static class ToolReply
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private static int _maxBytes = 65536;
    private static bool _unrestricted = true;
    public static void Configure(ServerOptions options)
    {
        _maxBytes = options.MaxResultBytes;
        _unrestricted = options.Unrestricted;
    }

    public static async Task<CallToolResult> Run(string database, Func<Task<object>> action)
    {
        try { return Success(await action().ConfigureAwait(false), database); }
        catch (ToolException ex) { return Error(database, ex.Code, ex.Message); }
        catch (PostgresException ex)
        {
            string code = ex.SqlState == "57014" ? "timeout" : "postgresql_error";
            // MessageText and Hint can contain values even when IncludeErrorDetail is disabled; only Diagnostics() passes on safe parts.
            Diagnostic? diagnostic = Diagnostics(ex);
            return Error(database, code, PostgreSqlMessage(ex.SqlState, diagnostic?.ServerMessage is not null), ex.SqlState, diagnostic);
        }
        catch (OperationCanceledException) { throw; }
        catch (NpgsqlException ex) when (ex.InnerException is TimeoutException)
        { return Error(database, "timeout", "Connection or command timed out; no fallback was used."); }
        catch (NpgsqlException)
        { return Error(database, "connection_error", "Cannot connect to or communicate with the requested target. Check configured credentials, network and database. No fallback was used."); }
        catch (TimeoutException) { return Error(database, "timeout", "Operation timed out; no fallback was used."); }
        catch (Exception)
        { return Error(database, "internal_error", "Unexpected server failure; no fallback was used."); }
    }

    private static string PostgreSqlMessage(string sqlState, bool serverTextReturned) => sqlState switch
    {
        "57014" => "PostgreSQL canceled the operation (statement timeout or cancellation).",
        "42501" => "The configured PostgreSQL role does not have permission for this operation.",
        // Tell the agent whether retrying as a write can work on this server at all.
        "25006" => _unrestricted
            ? "PostgreSQL rejected a write in a READ ONLY transaction. If a write is intended, retry execute_sql with read_only=false; the target database must also accept writes (not a standby)."
            : "PostgreSQL rejected a write in a READ ONLY transaction. This server is in restricted access mode and refuses writes.",
        "25001" => "PostgreSQL rejected a transaction setting change after the transaction became active.",
        "28P01" or "28000" => "PostgreSQL authentication failed. Check the configured credentials and authentication policy.",
        "3D000" => "The configured PostgreSQL database does not exist.",
        "42601" => "PostgreSQL rejected the SQL syntax. Check the statement using the returned SQLSTATE.",
        "42P01" => "A referenced relation does not exist or is not visible in the current search path.",
        "42703" => "A referenced column does not exist.",
        "42883" => "No matching PostgreSQL function or operator was found. Check argument types and search path.",
        "22P02" => "A value has invalid syntax for its PostgreSQL type. Check input values and casts.",
        "22003" => "A numeric value is outside the PostgreSQL type's range.",
        "22012" => "PostgreSQL rejected division by zero.",
        "23502" => "The operation violates a NOT NULL constraint.",
        "23503" => "The operation violates a foreign key constraint.",
        "23505" => "The operation violates a unique constraint.",
        "23514" => "The operation violates a check constraint.",
        "40001" => "PostgreSQL could not serialize the transaction.",
        "40P01" => "PostgreSQL detected a deadlock and aborted the transaction.",
        "55P03" => "PostgreSQL could not acquire a required lock.",
        "53300" => "PostgreSQL has no available connection slots.",
        "57P01" or "57P02" or "57P03" => "PostgreSQL is shutting down or is not ready to accept connections.",
        _ => serverTextReturned
            ? "PostgreSQL rejected the operation. See server_message and the SQLSTATE."
            : "PostgreSQL rejected the operation. Use the SQLSTATE to investigate; server-provided message and hint are withheld because they may contain sensitive values."
    };

    public static CallToolResult Success(object value, string database = "")
    {
        JsonElement element = JsonSerializer.SerializeToElement(value, JsonOptions);
        string text = element.GetRawText();
        if (Encoding.UTF8.GetByteCount(text) > _maxBytes)
            return Error(database, "result_too_large", "Result exceeds the configured byte budget. Request a smaller limit, focused section or shorter SQL projection.");
        return Result(element, false, text);
    }

    private const string StatementPositionKey = "postgresql-sharp-mcp:statement-position";

    /// <summary>
    /// Exception filter for code that sends a caller's statement inside server-owned text: records PostgreSQL's
    /// 1-based cursor position relative to the caller's statement. Always returns false, so nothing is caught.
    /// PostgreSQL counts positions in characters, so the bound is the statement's code-point count, not its UTF-16 length.
    /// </summary>
    public static bool MarkStatementPosition(PostgresException ex, int prefixLength, string statement)
    {
        int position = ex.Position - prefixLength;
        if (position >= 1 && position <= statement.EnumerateRunes().Count()) ex.Data[StatementPositionKey] = position;
        return false;
    }

    // Forwarded server text must be anchored to the caller's statement. Class 42 (syntax error or access rule violation)
    // text is forwarded only when PostgreSQL located it inside that statement (position) or it is a privilege error (42501,
    // which names the denied object), and only when neither a routine context (Where) nor an internal query (InternalQuery,
    // e.g. SPI inside query_to_xml) is set. Errors raised while executing, such as current_setting(col), col::regclass or
    // col::regrole, carry no statement position and echo row values, so they stay withheld; so does every other class.
    // The constraint name of class 23 errors is subject to the same routine/internal-query exclusion.
    private static Diagnostic? Diagnostics(PostgresException ex)
    {
        int? position = ex.Data[StatementPositionKey] as int?;
        bool direct = string.IsNullOrEmpty(ex.Where) && string.IsNullOrEmpty(ex.InternalQuery);
        bool statementText = direct && ex.SqlState.StartsWith("42", StringComparison.Ordinal) && (position is not null || ex.SqlState == "42501");
        string? constraint = direct && ex.SqlState.StartsWith("23", StringComparison.Ordinal) ? ex.ConstraintName : null;
        if (position is null && !statementText && constraint is null) return null;
        return new(position, statementText ? Clip(ex.MessageText, 512) : null,
            statementText && ex.Hint is { Length: > 0 } hint ? Clip(hint, 512) : null, constraint is null ? null : Clip(constraint, 128));
    }

    private sealed record Diagnostic(int? Position, string? ServerMessage, string? ServerHint, string? Constraint);

    public static CallToolResult Error(string database, string code, string message, string? sqlState = null)
        => Error(database, code, message, sqlState, null);

    private static CallToolResult Error(string database, string code, string message, string? sqlState, Diagnostic? diagnostic)
    {
        // Bound diagnostics; provider-controlled text is limited to the fields Diagnostics() allows.
        var value = JsonSerializer.SerializeToElement(new
        {
            database = Clip(database, 128),
            error = new
            {
                code, message = Clip(message, 768), sql_state = sqlState, position = diagnostic?.Position,
                server_message = diagnostic?.ServerMessage, server_hint = diagnostic?.ServerHint, constraint = diagnostic?.Constraint
            }
        }, JsonOptions);
        return Result(value, true);
    }
    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max] + "… [truncated]";
    private static CallToolResult Result(JsonElement value, bool error, string? text = null) => new()
    {
        IsError = error, StructuredContent = value,
        Content = [new TextContentBlock { Text = text ?? value.GetRawText() }]
    };
}
