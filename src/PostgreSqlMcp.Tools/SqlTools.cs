using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PostgreSqlMcp.Core;

namespace PostgreSqlMcp.Tools;

[McpServerToolType]
public sealed class SqlTools(SqlExecutor executor)
{
    [McpServerTool(Name = "execute_sql", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = false)]
    [Description("Run one SQL statement on a physical database or configured profile alias. Optional target selects a connection profile. Bounded column/row arrays; next_offset re-executes read-only SQL. Use stable ORDER BY. Pass values as parameters ($1..$n) instead of inlining literals. Writes need unrestricted mode (default) and read_only=false; never replay a truncated write.")]
    public Task<CallToolResult> ExecuteSql(CancellationToken ct, string database, string sql,
        int? limit = null, int offset = 0, bool read_only = true, string? target = null,
        [Description(ParameterDescriptions.Values)] JsonElement[]? parameters = null) =>
        ToolReply.Run(database, async () => await executor.QueryAsync(database, sql, limit: limit, offset: offset, readOnly: read_only, ct: ct, target: target,
            positional: SqlParameters.Normalize(parameters)).ConfigureAwait(false));
}

internal static class ParameterDescriptions
{
    public const string Values = "Values for $1..$n placeholders, in order: strings, numbers, booleans or null (max 256). Never interpolated; PostgreSQL infers each type from context like a quoted literal, so cast when ambiguous ($1::int, $1::jsonb).";
}
