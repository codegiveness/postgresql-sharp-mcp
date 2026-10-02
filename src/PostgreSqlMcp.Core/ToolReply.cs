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
            return Error(database, code, ex.MessageText, ex.SqlState, ex.Hint);
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

    public static CallToolResult Success(object value, string database = "")
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.Length > _maxBytes)
            return Error(database, "result_too_large", "Result exceeds the configured byte budget. Request a smaller limit, focused section or shorter SQL projection.");
        using var doc = JsonDocument.Parse(bytes);
        return Result(doc.RootElement.Clone(), false);
    }

    public static CallToolResult Error(string database, string code, string message, string? sqlState = null, string? hint = null)
    {
        // Diagnostics are bounded too; SQL values can appear in PostgreSQL messages.
        var value = JsonSerializer.SerializeToElement(new
        {
            database = Clip(database, 128), error = new { code, message = Clip(message, 768), sql_state = sqlState, hint = hint is null ? null : Clip(hint, 384) }
        }, JsonOptions);
        return Result(value, true);
    }
    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max] + "… [truncated]";
    private static CallToolResult Result(JsonElement value, bool error) => new()
    {
        IsError = error, StructuredContent = value,
        Content = [new TextContentBlock { Text = value.GetRawText() }]
    };
}
