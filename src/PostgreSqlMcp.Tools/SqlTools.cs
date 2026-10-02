using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PostgreSqlMcp.Core;

namespace PostgreSqlMcp.Tools;

[McpServerToolType]
public sealed class SqlTools(SqlExecutor executor)
{
    [McpServerTool(Name = "execute_sql", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = false)]
    [Description("Run one SQL statement on an explicit target. Bounded column/row arrays; next_offset re-executes read-only SQL. Use stable ORDER BY. Writes need unrestricted mode and read_only=false; never replay a truncated write.")]
    public Task<CallToolResult> ExecuteSql(CancellationToken ct, string database, string sql,
        int? limit = null, int offset = 0, bool read_only = true) =>
        ToolReply.Run(database, async () => await executor.QueryAsync(database, sql, limit: limit, offset: offset, readOnly: read_only, ct: ct).ConfigureAwait(false));
}
