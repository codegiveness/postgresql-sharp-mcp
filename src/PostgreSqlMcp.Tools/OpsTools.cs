using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Npgsql;
using PostgreSqlMcp.Core;

namespace PostgreSqlMcp.Tools;

[McpServerToolType]
public sealed class OpsTools(SqlExecutor executor, ServerOptions options)
{
    [McpServerTool(Name = "analyze_db_health", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Current-database health summary or focused vacuum, index, constraints, sequences, replication, or blocking evidence. Results are paged; no changes are made.")]
    public Task<CallToolResult> AnalyzeDbHealth(CancellationToken ct,
        [Description(ParameterText.Database)] string database,
        [Description("summary (default), vacuum, index, constraints, sequences, replication, or blocking.")] string section = "summary",
        [Description(ParameterText.SchemaFilter + " Applies to vacuum, index, constraints and sequences.")] string? schema = null,
        [Description(ParameterText.Limit)] int? limit = null,
        [Description(ParameterText.Offset)] int offset = 0,
        [Description(ParameterText.Target)] string? target = null) =>
        ToolReply.Run(database, async () =>
        {
            string sql = section.ToLowerInvariant() switch
            {
                "summary" => SummarySql,
                "vacuum" => VacuumSql,
                "index" => IndexSql,
                "constraints" => ConstraintsSql,
                "sequences" => SequencesSql,
                "replication" => ReplicationSql,
                "blocking" => BlockingSql,
                _ => throw new ToolException("invalid_section", "section must be summary, vacuum, index, constraints, sequences, replication, or blocking.")
            };
            QueryPage page = await executor.QueryAsync(database, sql, Filters(schema), limit, offset, ct: ct, target: target);
            return new
            {
                database, section, result = page,
                notes = section.ToLowerInvariant() switch
                {
                    "summary" => "Buffer hit ratio is cumulative since stats_reset, not a latency metric. Connection counts and transaction ages are limited to this database; hidden sessions may have unavailable fields.",
                    "vacuum" => "Dead tuples and live tuples are estimates. Assess vacuum/analyze history, transaction age and workload before changing autovacuum. Freeze age thresholds are configured, not fixed advice.",
                    "index" => IndexNotes,
                    "constraints" => "Unvalidated constraints still enforce new changes. Review existing-data validation separately; this tool never validates or changes constraints.",
                    "sequences" => "Sequence last_value can be unavailable without privileges. Cached values and cycling affect exhaustion estimates; sequence values are not row counts.",
                    "replication" => "Only logical slots tied to this database are shown. Physical replication is server-wide and deliberately omitted. An inactive slot alone is not proof of a fault; retained WAL can affect server disk space.",
                    _ => "Only blocked sessions in this database are listed. Blocking PIDs can belong to other databases, but their query text and identity are not disclosed. Statistics visibility depends on privileges."
                }
            };
        });

    [McpServerTool(Name = "get_top_queries", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Rank pg_stat_statements for the current database only. Requires an installed, preloaded extension; query text clipping is explicit.")]
    public Task<CallToolResult> GetTopQueries(CancellationToken ct,
        [Description(ParameterText.Database)] string database,
        [Description("total_time (default), mean_time, calls, rows, or reads; descending.")] string order_by = "total_time",
        [Description("Page size; default 10, at most max_rows.")] int? limit = null,
        [Description(ParameterText.Offset)] int offset = 0,
        [Description(ParameterText.Target)] string? target = null) =>
        ToolReply.Run(database, async () =>
        {
            string order = order_by.ToLowerInvariant() switch
            {
                "total_time" => "s.total_exec_time",
                "mean_time" => "s.mean_exec_time",
                "calls" => "s.calls",
                "rows" => "s.rows",
                "reads" => "s.shared_blks_read",
                _ => throw new ToolException("invalid_order", "order_by must be total_time, mean_time, calls, rows, or reads.")
            };
            return await executor.WithSessionAsync<object>(database, async (session, token) =>
            {
                QueryPage extension = await session.QueryAsync("SELECT n.nspname FROM pg_catalog.pg_extension e JOIN pg_catalog.pg_namespace n ON n.oid=e.extnamespace WHERE e.extname='pg_stat_statements'", limit: 1, ct: token);
                if (extension.Rows.Count == 0)
                    throw new ToolException("extension_missing", "pg_stat_statements is not installed in this database. Ask the administrator to configure shared_preload_libraries and install the extension; this tool does not install extensions.");
                if (extension.ClippedCells.Count != 0)
                    throw new ToolException("metadata_truncated", "The cell limit is too small to resolve the extension schema safely.");
                string ns = QuoteIdentifier((string)extension.Rows[0][0]!);
                string sql = $"""
                    SELECT s.userid, s.queryid, s.toplevel, s.calls, s.total_exec_time AS total_time_ms,
                           s.mean_exec_time AS mean_time_ms, s.min_exec_time AS min_time_ms,
                           s.max_exec_time AS max_time_ms, s.rows, s.shared_blks_hit, s.shared_blks_read,
                           s.shared_blks_written, s.temp_blks_read, s.temp_blks_written,
                           left(s.query, @text_limit) AS query_text,
                           length(s.query) > @text_limit AS query_text_clipped,
                           s.query IS NULL OR s.query = '<insufficient privilege>' AS query_text_unavailable
                    FROM {ns}.pg_stat_statements s
                    WHERE s.dbid = (SELECT oid FROM pg_catalog.pg_database WHERE datname = current_database())
                    ORDER BY {order} DESC NULLS LAST, s.userid, s.queryid, s.toplevel
                    """;
                try
                {
                    QueryPage page = await session.QueryAsync(sql,
                        new Dictionary<string, object?> { ["text_limit"] = options.MaxCellChars }, limit ?? Math.Min(10, options.MaxRows), offset, token);
                    return new { database, order_by, result = page, notes = "Statistics are cumulative and can reset or entries can be evicted. PostgreSQL hides other users' query text without appropriate privileges. Pagination re-executes against changing statistics, not a snapshot." };
                }
                catch (PostgresException ex) when (ex.SqlState == "55000")
                {
                    throw new ToolException("extension_not_ready", "pg_stat_statements is installed but not initialized (SQLSTATE 55000). Ask the administrator to configure shared_preload_libraries and restart PostgreSQL; this tool does not change server configuration.");
                }
            }, ct: ct, target: target);
        });

    [McpServerTool(Name = "analyze_indexes", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Paged current-database index validity, usage, size and structurally duplicate evidence. Recommendations are contextual, never fabricated missing-index predictions.")]
    public Task<CallToolResult> AnalyzeIndexes(CancellationToken ct,
        [Description(ParameterText.Database)] string database,
        [Description(ParameterText.SchemaFilter)] string? schema = null,
        [Description("Exact, case-sensitive table name; omitted means all tables.")] string? table = null,
        [Description(ParameterText.Limit)] int? limit = null,
        [Description(ParameterText.Offset)] int offset = 0,
        [Description(ParameterText.Target)] string? target = null) =>
        ToolReply.Run(database, async () =>
        {
            QueryPage page = await executor.QueryAsync(database, IndexSql, Filters(schema, table), limit, offset, ct: ct, target: target);
            return new { database, result = page, notes = IndexNotes,
                evaluation = "Use explain_query on the actual workload. Supply candidate CREATE INDEX statements there to compare real planner costs through an already-installed hypopg extension. No indexes are created by these tools." };
        });

    private const string IndexNotes = "Zero scans since stats_reset are evidence, not permission to drop an index. Primary/unique/exclusion constraints, foreign-key workloads, replicas and infrequent queries matter. Duplicate evidence compares full catalog structure, not workload interchangeability. PostgreSQL has no missing-index DMV; sequential scans can be optimal. Costs are planner estimates, not measured speedups.";

    private static Dictionary<string, object?> Filters(string? schema, string? table = null) =>
        new() { ["schema"] = schema, ["table"] = table };

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private const string SummarySql = """
        SELECT d.datname AS database_name, pg_database_size(d.datid) AS size_bytes,
               d.numbackends AS connections, d.xact_commit, d.xact_rollback,
               d.blks_hit, d.blks_read,
               round(100.0*d.blks_hit/nullif(d.blks_hit+d.blks_read,0),2) AS buffer_hit_percent,
               d.deadlocks, d.temp_files, d.temp_bytes, d.stats_reset,
               age(db.datfrozenxid) AS database_freeze_age,
               current_setting('autovacuum_freeze_max_age')::bigint AS configured_freeze_max_age,
               (SELECT max(clock_timestamp()-a.xact_start) FROM pg_stat_activity a WHERE a.datid=d.datid) AS oldest_transaction_age,
               (SELECT count(*) FROM pg_stat_activity a WHERE a.datid=d.datid AND a.state='idle in transaction') AS idle_in_transaction,
               (SELECT count(*) FROM pg_stat_activity a WHERE a.datid=d.datid AND cardinality(pg_blocking_pids(a.pid))>0) AS blocked_sessions
        FROM pg_stat_database d JOIN pg_database db ON db.oid=d.datid
        WHERE d.datname=current_database()
        ORDER BY d.datname
        """;

    private const string VacuumSql = """
        SELECT s.schemaname, s.relname AS table_name, s.n_live_tup, s.n_dead_tup,
               round(100.0*s.n_dead_tup/nullif(s.n_live_tup+s.n_dead_tup,0),2) AS estimated_dead_percent,
               s.n_mod_since_analyze, s.last_vacuum, s.last_autovacuum, s.last_analyze, s.last_autoanalyze,
               age(c.relfrozenxid) AS freeze_age, array_to_string(c.reloptions, ', ') AS reloptions,
               current_setting('autovacuum_freeze_max_age')::bigint AS configured_freeze_max_age,
               age(c.relfrozenxid)>=current_setting('autovacuum_freeze_max_age')::bigint AS freeze_age_at_configured_threshold,
               c.reltuples<0 AS has_never_been_analyzed
        FROM pg_stat_user_tables s JOIN pg_class c ON c.oid=s.relid
        WHERE (@schema::text IS NULL OR s.schemaname=@schema)
        ORDER BY s.schemaname, s.relname, s.relid
        """;

    private const string IndexSql = """
        SELECT n.nspname AS schema_name, t.relname AS table_name, c.relname AS index_name,
               am.amname AS access_method, pg_relation_size(c.oid) AS size_bytes,
               i.indisvalid, i.indisready, i.indislive, i.indisprimary, i.indisunique,
               EXISTS (SELECT 1 FROM pg_constraint con WHERE con.conindid=i.indexrelid) AS supports_constraint,
               s.idx_scan, s.idx_tup_read, s.idx_tup_fetch,
               ts.seq_scan AS table_seq_scans, ts.n_live_tup AS estimated_table_rows,
               d.stats_reset, pg_get_indexdef(c.oid) AS definition,
               dup.duplicate_count, dup.example_duplicate,
               CASE WHEN NOT i.indisvalid OR NOT i.indisready OR NOT i.indislive
                    THEN 'Review invalid/not-ready/not-live index and failed or ongoing index build; do not assume it can serve queries.'
                    WHEN dup.duplicate_count>0 THEN 'Review structurally identical peers, constraint dependencies and workload before considering consolidation.'
                    WHEN s.idx_scan=0 AND NOT i.indisunique AND NOT i.indisprimary
                    THEN 'No scans recorded; review stats reset time and complete workload before considering removal.'
                    ELSE 'Evaluate against query plans and write overhead; no missing-index inference.' END AS recommendation
        FROM pg_index i
        JOIN pg_class c ON c.oid=i.indexrelid
        JOIN pg_class t ON t.oid=i.indrelid
        JOIN pg_namespace n ON n.oid=t.relnamespace
        JOIN pg_am am ON am.oid=c.relam
        LEFT JOIN pg_stat_user_indexes s ON s.indexrelid=c.oid
        LEFT JOIN pg_stat_user_tables ts ON ts.relid=t.oid
        LEFT JOIN pg_stat_database d ON d.datname=current_database()
        LEFT JOIN LATERAL (
            SELECT count(*) AS duplicate_count, min(pc.relname) AS example_duplicate
            FROM pg_index p JOIN pg_class pc ON pc.oid=p.indexrelid
            WHERE p.indrelid=i.indrelid AND p.indexrelid<>i.indexrelid
              AND p.indisvalid AND p.indisready AND p.indislive
              AND p.indisunique=i.indisunique AND p.indisexclusion=i.indisexclusion
              AND p.indnkeyatts=i.indnkeyatts AND p.indnatts=i.indnatts
              AND p.indkey=i.indkey AND p.indcollation=i.indcollation
              AND p.indclass=i.indclass AND p.indoption=i.indoption
              AND p.indexprs::text IS NOT DISTINCT FROM i.indexprs::text
              AND p.indpred::text IS NOT DISTINCT FROM i.indpred::text
              AND (to_jsonb(p)->'indnullsnotdistinct') IS NOT DISTINCT FROM (to_jsonb(i)->'indnullsnotdistinct')
              AND pc.relam=c.relam AND pc.reltablespace=c.reltablespace
              AND pc.reloptions IS NOT DISTINCT FROM c.reloptions
        ) dup ON true
        WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_toast%'
          AND (@schema::text IS NULL OR n.nspname=@schema)
          AND (@table::text IS NULL OR t.relname=@table)
        ORDER BY n.nspname, t.relname, c.relname, c.oid
        """;

    private const string ConstraintsSql = """
        SELECT n.nspname AS schema_name, t.relname AS table_name, con.conname AS constraint_name,
               con.contype AS constraint_type, con.convalidated, con.condeferrable, con.condeferred,
               pg_get_constraintdef(con.oid) AS definition,
               CASE WHEN NOT con.convalidated THEN 'Existing rows have not been validated.' ELSE NULL END AS warning
        FROM pg_constraint con JOIN pg_class t ON t.oid=con.conrelid
        JOIN pg_namespace n ON n.oid=t.relnamespace
        WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_toast%'
          AND (@schema::text IS NULL OR n.nspname=@schema)
        ORDER BY n.nspname, t.relname, con.conname, con.oid
        """;

    private const string SequencesSql = """
        SELECT s.schemaname, s.sequencename, s.data_type::text, s.start_value, s.min_value,
               s.max_value, s.increment_by, s.cycle, s.cache_size, s.last_value,
               CASE WHEN s.last_value IS NULL THEN NULL
                    WHEN s.increment_by>0 THEN (s.max_value::numeric-s.last_value)/s.increment_by
                    ELSE (s.last_value::numeric-s.min_value)/abs(s.increment_by::numeric) END AS estimated_steps_remaining,
               CASE WHEN s.last_value IS NULL THEN 'Value unavailable: check privileges or unused sequence.'
                    WHEN s.cycle THEN 'Sequence cycles; confirm wraparound is acceptable for consumers.'
                    WHEN (s.increment_by>0 AND s.last_value>=s.max_value)
                      OR (s.increment_by<0 AND s.last_value<=s.min_value) THEN 'Sequence is at its limit; cached values may still remain.'
                    ELSE NULL END AS warning
        FROM pg_sequences s
        WHERE s.schemaname NOT IN ('pg_catalog','information_schema')
          AND (@schema::text IS NULL OR s.schemaname=@schema)
        ORDER BY s.schemaname, s.sequencename
        """;

    private const string ReplicationSql = """
        SELECT slot_name, plugin, slot_type, active, temporary, restart_lsn::text,
               confirmed_flush_lsn::text, catalog_xmin::text,
               CASE WHEN pg_is_in_recovery() THEN NULL
                    ELSE pg_wal_lsn_diff(pg_current_wal_lsn(),restart_lsn) END AS retained_wal_bytes,
               CASE WHEN NOT active THEN 'Inactive database-local logical slot; assess consumer and WAL retention.' ELSE NULL END AS warning
        FROM pg_replication_slots
        WHERE database=current_database()
        ORDER BY slot_name
        """;

    private const string BlockingSql = """
        SELECT a.pid AS blocked_pid, a.state, a.wait_event_type, a.wait_event,
               clock_timestamp()-a.xact_start AS transaction_age,
               clock_timestamp()-a.query_start AS query_age,
               pg_blocking_pids(a.pid)::text AS blocking_pids
        FROM pg_stat_activity a
        WHERE a.datname=current_database() AND cardinality(pg_blocking_pids(a.pid))>0
        ORDER BY a.xact_start NULLS LAST, a.pid
        """;
}
