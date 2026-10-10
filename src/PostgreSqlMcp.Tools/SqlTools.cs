using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PostgreSqlMcp.Core;

namespace PostgreSqlMcp.Tools;

[McpServerToolType]
public sealed class SqlTools(SqlExecutor executor)
{
    // Annotations and the access-mode part of the description are set by ToolRegistration from the enforced configuration.
    [McpServerTool(Name = "execute_sql", OpenWorld = false)]
    [Description("Run one SQL statement on a physical database or configured profile alias. Optional target selects a connection profile. Returns bounded column/row arrays; for read-only SQL, next_offset re-executes the statement for the next page, so use a stable ORDER BY.")]
    public Task<CallToolResult> ExecuteSql(CancellationToken ct,
        [Description(ParameterText.Database)] string database,
        [Description(ParameterText.Sql)] string sql,
        [Description(ParameterText.Limit)] int? limit = null,
        [Description(ParameterText.Offset + " Read-only statements only.")] int offset = 0,
        [Description("true (default): READ ONLY transaction, always rolled back. false: commit a write; refused in restricted access mode.")] bool read_only = true,
        [Description(ParameterText.Target)] string? target = null) =>
        ToolReply.Run(database, async () => await executor.QueryAsync(database, sql, limit: limit, offset: offset, readOnly: read_only, ct: ct, target: target).ConfigureAwait(false));
}
