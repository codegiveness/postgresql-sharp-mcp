using System.Buffers;
using System.ComponentModel;
using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Npgsql;
using PostgreSqlMcp.Core;

namespace PostgreSqlMcp.Tools;

[McpServerToolType]
public sealed partial class PlanTools(SqlExecutor executor, ServerOptions options)
{
    [McpServerTool(Name = "explain_query", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Real PostgreSQL JSON plan or compact summary. Optional ANALYZE executes inside READ ONLY. Supplied CREATE INDEX candidates use installed HypoPG in one session, compare estimated costs, and are always cleaned up; never permanent DDL.")]
    public Task<CallToolResult> ExplainQuery(CancellationToken ct, string database, string sql,
        string format = "summary", bool analyze = false, string[]? indexes = null, string? target = null) =>
        ToolReply.Run(database, async () =>
        {
            string statement = SqlGuard.Validate(sql);
            bool json = format.Equals("json", StringComparison.OrdinalIgnoreCase);
            if (!json && !format.Equals("summary", StringComparison.OrdinalIgnoreCase))
                throw new ToolException("invalid_format", "format must be summary or json.");
            string[] candidates = ValidateCandidates(indexes);
            if (analyze && candidates.Length != 0)
                throw new ToolException("invalid_options", "HypoPG indexes only work with estimated EXPLAIN, not ANALYZE. Set analyze=false to evaluate candidates.");
            return await executor.WithSessionAsync<object>(database, async (session, token) =>
            {
                if (candidates.Length == 0)
                {
                    using JsonDocument plan = await ReadPlan(session, statement, analyze, token);
                    return new
                    {
                        database, analyzed = analyze, format,
                        plan = json ? (object)plan.RootElement.Clone() : Summarize(plan.RootElement),
                        notes = analyze
                            ? "ANALYZE executed the query in a READ ONLY transaction. Read-only transactions prevent database writes, not external side effects of privileged functions. Actual node times/rows are per-loop; planner costs are not milliseconds."
                            : "Estimated plan only; costs are relative planner units, not measured runtime. Summary includes a bounded selection of nodes; use format=json for the bounded complete plan."
                    };
                }

                QueryPage extension = await session.QueryAsync("SELECT n.nspname FROM pg_catalog.pg_extension e JOIN pg_catalog.pg_namespace n ON n.oid=e.extnamespace WHERE e.extname='hypopg'", limit: 1, ct: token);
                if (extension.Rows.Count == 0)
                    throw new ToolException("extension_missing", "HypoPG is not installed in this database. Ask an administrator to install it before supplying index candidates. This tool does not install extensions or create permanent indexes.");
                if (extension.ClippedCells.Count != 0)
                    throw new ToolException("metadata_truncated", "The cell limit is too small to resolve the extension schema safely.");
                string ns = QuoteIdentifier((string)extension.Rows[0][0]!);
                string reset = $"SELECT {ns}.hypopg_reset()";
                // Hypothetical indexes are connection-local memory, not transactional objects.
                // A savepoint makes cleanup possible even after a planner/parser error aborts the transaction.
                await ExecuteControl(session, "SAVEPOINT mcp_hypopg", token);
                bool failed = false;
                try
                {
                    await ExecuteControl(session, reset, token);
                    using JsonDocument baseline = await ReadPlan(session, statement, false, token);
                    var hypotheticalIndexes = new List<HypotheticalIndex>();
                    // Candidates are independent; send one batch rather than one round trip per index.
                    await using var batch = session.CreateBatch();
                    foreach (string candidate in candidates)
                    {
                        var command = new NpgsqlBatchCommand($"SELECT indexrelid::bigint, indexname FROM {ns}.hypopg_create_index(@candidate)");
                        command.Parameters.AddWithValue("candidate", candidate);
                        batch.BatchCommands.Add(command);
                    }
                    await using (var reader = await session.ExecuteReaderAsync(batch, CommandBehavior.SequentialAccess, token))
                    for (int candidate = 0; candidate < candidates.Length; candidate++)
                    {
                        int created = 0;
                        while (await reader.ReadAsync(token))
                        {
                            if (++created > 1)
                                throw new ToolException("invalid_index", "Each candidate must describe exactly one CREATE INDEX statement.");
                            long oid = reader.GetInt64(0);
                            string name = reader.GetString(1);
                            if (name.Length > options.MaxCellChars)
                                throw new ToolException("result_too_large", "Hypothetical index name exceeds the result cell limit; simplify the candidate.");
                            hypotheticalIndexes.Add(new(candidate, oid, name));
                        }
                        if (created != 1)
                            throw new ToolException("invalid_index", $"Candidate {candidate} did not create a hypothetical index. Supply a supported CREATE INDEX definition.");
                        if (candidate + 1 < candidates.Length && !await reader.NextResultAsync(token))
                            throw new ToolException("invalid_index", "HypoPG did not return a result for each candidate.");
                    }
                    using JsonDocument evaluated = await ReadPlan(session, statement, false, token);
                    PlanSummary before = Summarize(baseline.RootElement);
                    PlanSummary after = Summarize(evaluated.RootElement);
                    return new
                    {
                        database, analyzed = false, format,
                        baseline = json ? (object)baseline.RootElement.Clone() : before,
                        with_indexes = json ? (object)evaluated.RootElement.Clone() : after,
                        hypothetical_indexes = hypotheticalIndexes,
                        comparison = new
                        {
                            baseline_total_cost = before.TotalCost,
                            with_indexes_total_cost = after.TotalCost,
                            estimated_cost_reduction_percent = before.TotalCost > 0
                                ? (double?)((before.TotalCost - after.TotalCost) / before.TotalCost * 100) : null
                        },
                        notes = "Candidates are evaluated together, not individually. Hypothetical indexes affect estimated plans only; cost reduction is not a measured speedup. Inspect chosen index names, statistics, write overhead and storage before creating an actual index. No permanent index was created."
                    };
                }
                catch
                {
                    failed = true;
                    throw;
                }
                finally
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        if (failed)
                            await ExecuteControl(session, "ROLLBACK TO SAVEPOINT mcp_hypopg", cleanup.Token, 5);
                        await ExecuteControl(session, reset, cleanup.Token, 5);
                        await ExecuteControl(session, "RELEASE SAVEPOINT mcp_hypopg", cleanup.Token, 5);
                    }
                    catch
                    {
                        // Clearing marks checked-out connectors for disposal on return to the pool.
                        session.ClearPool();
                        if (!failed)
                            throw new ToolException("hypopg_cleanup_failed", "Hypothetical index cleanup failed. The connection pool was cleared so this session cannot leak candidates to later calls.");
                    }
                }
            }, ct: ct, target: target);
        });

    private string[] ValidateCandidates(string[]? indexes)
    {
        if (indexes is null || indexes.Length == 0)
            return [];
        if (indexes.Length > 16)
            throw new ToolException("invalid_index", "Supply at most 16 hypothetical index candidates per comparison.");
        var normalized = new string[indexes.Length];
        long bytes = 0;
        for (int i = 0; i < indexes.Length; i++)
        {
            if (indexes[i] is null)
                throw new ToolException("invalid_index", $"Candidate {i} must be a CREATE INDEX statement.");
            bytes += Encoding.UTF8.GetByteCount(indexes[i]);
            if (bytes > options.MaxResultBytes)
                throw new ToolException("invalid_index", "Combined candidate SQL exceeds the configured result byte limit; supply fewer/simpler candidates.");
            normalized[i] = SqlGuard.Validate(indexes[i]);
            if (!IndexDefinition.IsMatch(normalized[i]))
                throw new ToolException("invalid_index", $"Candidate {i} must start with CREATE INDEX or CREATE UNIQUE INDEX (no leading comments). HypoPG, not this server, parses the definition.");
        }
        return normalized;
    }

    [GeneratedRegex(@"\A\s*CREATE\s+(?:UNIQUE\s+)?INDEX\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IndexDefinition { get; }

    private async Task<JsonDocument> ReadPlan(SqlSession session, string sql, bool analyze, CancellationToken ct)
    {
        await using NpgsqlBatch batch = session.CreateBatch();
        batch.BatchCommands.Add(new NpgsqlBatchCommand(analyze
            ? "EXPLAIN (FORMAT JSON, ANALYZE TRUE, BUFFERS TRUE, VERBOSE FALSE) " + sql
            : "EXPLAIN (FORMAT JSON, VERBOSE FALSE) " + sql));
        // EXPLAIN returns one row; SingleRow is not used because the batch may begin with the session's setup commands.
        await using var reader = await session.ExecuteReaderAsync(batch, CommandBehavior.SequentialAccess, ct);
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(0))
            throw new ToolException("invalid_plan", "PostgreSQL returned no JSON plan.");
        using TextReader text = await reader.GetTextReaderAsync(0, ct);
        int max = options.MaxResultBytes;
        var builder = new StringBuilder(Math.Min(max, 4096));
        char[] buffer = ArrayPool<char>.Shared.Rent(Math.Min(4096, max));
        try
        {
            while (true)
            {
                int remaining = max - builder.Length;
                int read = await text.ReadAsync(buffer.AsMemory(0, remaining == 0 ? 1 : Math.Min(buffer.Length, remaining)), ct);
                if (read == 0)
                    break;
                if (builder.Length + read > max)
                    throw OversizedPlan();
                builder.Append(buffer, 0, read);
            }
        }
        finally { ArrayPool<char>.Shared.Return(buffer); }
        string value = builder.ToString();
        if (Encoding.UTF8.GetByteCount(value) > max)
            throw OversizedPlan();
        try
        {
            return JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 256 });
        }
        catch (JsonException)
        {
            throw new ToolException("invalid_plan", "Planner JSON is invalid or exceeds the supported nesting depth. Narrow the query, for example by explaining a smaller subquery.");
        }
    }

    private ToolException OversizedPlan() => new("plan_too_large",
        $"Planner JSON exceeds MaxResultBytes ({options.MaxResultBytes}); no plan was silently truncated. Narrow the query or explain a smaller subquery, or ask the operator to increase the byte limit. Summary also requires reading the bounded complete planner JSON.");

    private static Task ExecuteControl(SqlSession session, string sql, CancellationToken ct, int? timeout = null) =>
        session.ExecuteAsync(sql, ct, timeout);

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static PlanSummary Summarize(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Array || json.GetArrayLength() != 1 ||
            !json[0].TryGetProperty("Plan", out JsonElement root))
            throw new ToolException("invalid_plan", "PostgreSQL JSON did not contain one statement plan.");
        var top = new List<PlanNode>(8);
        var pending = new Stack<JsonElement>();
        pending.Push(root);
        int count = 0;
        while (pending.TryPop(out JsonElement node))
        {
            var detail = new PlanNode(++count, Text(node, "Node Type"), Text(node, "Schema"),
                Text(node, "Relation Name"), Text(node, "Index Name"), Number(node, "Startup Cost"),
                Number(node, "Total Cost"), Number(node, "Plan Rows"), Number(node, "Actual Rows"),
                Number(node, "Actual Loops"), Number(node, "Actual Total Time"),
                Number(node, "Rows Removed by Filter"), Number(node, "Shared Hit Blocks"),
                Number(node, "Shared Read Blocks"), Number(node, "Temp Written Blocks"),
                Text(node, "Sort Method"), Text(node, "Sort Space Type"));
            if (top.Count < 8)
                top.Add(detail);
            else
            {
                int smallest = 0;
                for (int i = 1; i < top.Count; i++)
                    if ((top[i].TotalCost ?? 0) < (top[smallest].TotalCost ?? 0)) smallest = i;
                if ((detail.TotalCost ?? 0) > (top[smallest].TotalCost ?? 0)) top[smallest] = detail;
            }
            if (node.TryGetProperty("Plans", out JsonElement children))
                for (int i = children.GetArrayLength() - 1; i >= 0; i--) pending.Push(children[i]);
        }
        top.Sort((a, b) =>
        {
            int cost = (b.TotalCost ?? 0).CompareTo(a.TotalCost ?? 0);
            return cost == 0 ? a.Ordinal.CompareTo(b.Ordinal) : cost;
        });
        return new(Number(root, "Total Cost") ?? 0, Number(root, "Startup Cost"), Number(root, "Plan Rows"),
            Number(root, "Plan Width"), Number(json[0], "Planning Time"), Number(json[0], "Execution Time"),
            count, count - top.Count, top,
            "Major nodes ranked by inclusive subtree cost; costs overlap and must not be summed. Actual timings/rows are per loop. Omitted nodes remain available in format=json.");
    }

    private static double? Number(JsonElement value, string key) =>
        value.TryGetProperty(key, out JsonElement item) && item.TryGetDouble(out double number) ? number : null;

    private static string? Text(JsonElement value, string key) =>
        value.TryGetProperty(key, out JsonElement item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    private readonly record struct HypotheticalIndex(int Candidate, long Oid, string Name);
    private sealed record PlanSummary(double TotalCost, double? StartupCost, double? EstimatedRows,
        double? PlanWidth, double? PlanningTimeMs, double? ExecutionTimeMs, int NodeCount,
        int OmittedNodes, List<PlanNode> MajorNodes, string Notes);
    private readonly record struct PlanNode(int Ordinal, string? NodeType, string? Schema, string? Relation,
        string? Index, double? StartupCost, double? TotalCost, double? EstimatedRows, double? ActualRows,
        double? ActualLoops, double? ActualTotalTimeMs, double? RowsRemovedByFilter,
        double? SharedHitBlocks, double? SharedReadBlocks, double? TempWrittenBlocks,
        string? SortMethod, string? SortSpaceType);
}
