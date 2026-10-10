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
    public static void Configure(ServerOptions options) => _maxBytes = options.MaxResultBytes;

    public static async Task<CallToolResult> Run(string database, Func<Task<object>> action)
    {
        try { return Success(await action().ConfigureAwait(false), database); }
        catch (ToolException ex) { return Error(database, ex.Code, ex.Message); }
        catch (PostgresException ex)
        {
            string code = ex.SqlState == "57014" ? "timeout" : "postgresql_error";
            // MessageText and Hint can contain values even when IncludeErrorDetail is disabled; only Diagnostics() passes on safe parts.
            return Error(database, code, PostgreSqlMessage(ex.SqlState), ex.SqlState, Diagnostics(ex));
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

    private static string PostgreSqlMessage(string sqlState) => sqlState switch
    {
        "57014" => "PostgreSQL canceled the operation (statement timeout or cancellation).",
        "42501" => "The configured PostgreSQL role does not have permission for this operation.",
        "25006" => "PostgreSQL rejected this operation in a read-only transaction.",
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
        _ => "PostgreSQL rejected the operation. Use the SQLSTATE to investigate; server-provided message and hint are withheld because they may contain sensitive values."
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
    /// </summary>
    public static bool MarkStatementPosition(PostgresException ex, int prefixLength, int statementLength)
    {
        int position = ex.Position - prefixLength;
        if (position >= 1 && position <= statementLength) ex.Data[StatementPositionKey] = position;
        return false;
    }

    // Class 42 (syntax error or access rule violation) text raised directly by the statement's own parse/analysis or
    // privilege checks names only tokens and catalog objects from that statement. Errors raised inside a routine
    // (Where is set) can carry arbitrary text, and other classes can echo data values, so they stay withheld.
    private static Diagnostic? Diagnostics(PostgresException ex)
    {
        int? position = ex.Data[StatementPositionKey] as int?;
        bool statementText = ex.SqlState.StartsWith("42", StringComparison.Ordinal) && string.IsNullOrEmpty(ex.Where);
        string? constraint = ex.SqlState.StartsWith("23", StringComparison.Ordinal) ? ex.ConstraintName : null;
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
