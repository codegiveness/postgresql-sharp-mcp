using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PostgreSqlMcp.Core;

namespace PostgreSqlMcp.Tools;

[McpServerToolType]
public sealed class DatabaseTools
{
    private readonly SqlExecutor _executor;
    private readonly ServerOptions _options;
    private readonly DatabaseRegistry _registry;

    public DatabaseTools(SqlExecutor executor, ServerOptions options, DatabaseRegistry registry)
    {
        _executor = executor;
        _options = options;
        _registry = registry;
    }

    [McpServerTool(Name = "list_databases", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Page configured target aliases and server limits without connecting or exposing credentials. Pass a target as database to other tools.")]
    public Task<CallToolResult> ListDatabases(CancellationToken ct, int? limit = null, int offset = 0) =>
        ToolReply.Run("", () =>
        {
            ct.ThrowIfCancellationRequested();
            ValidatePage(limit, offset);
            string[] targets = _registry.TargetNames.Skip(offset).Take(limit ?? Math.Min(100, _options.MaxRows)).ToArray();
            bool truncated = offset + targets.Length < _registry.TargetNames.Length;
            return Task.FromResult<object>(new
            {
                targets, offset, next_offset = truncated ? (int?)(offset + targets.Length) : null,
                truncated, truncation_reason = truncated ? "row_limit" : null,
                access_mode = _options.Unrestricted ? "unrestricted" : "restricted",
                limits = new { max_rows = _options.MaxRows, max_result_bytes = _options.MaxResultBytes,
                    max_cell_chars = _options.MaxCellChars, query_timeout_seconds = _options.QueryTimeout }
            });
        });

    [McpServerTool(Name = "list_schemas", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Page schemas with USAGE privilege on an explicit target; system schemas excluded by default.")]
    public Task<CallToolResult> ListSchemas(
        CancellationToken ct,
        [Description("Configured database target alias; required.")] string database,
        [Description("Optional literal, case-sensitive schema name prefix.")] string? prefix = null,
        int? limit = null,
        int offset = 0,
        bool include_system = false) => ToolReply.Run(database, async () =>
        {
            ValidatePage(limit, offset);
            return await _executor.QueryAsync(database, SchemasSql, new Dictionary<string, object?>
            {
                ["prefix"] = prefix,
                ["include_system"] = include_system
            }, limit, offset, ct: ct).ConfigureAwait(false);
        });

    [McpServerTool(Name = "list_objects", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Page visible objects by schema/type/literal name filter. System objects excluded by default; identity_arguments distinguish overloaded routines.")]
    public Task<CallToolResult> ListObjects(
        CancellationToken ct,
        string database,
        [Description("Exact schema name, or null for all visible schemas.")] string? schema = null,
        [Description("table, view, materialized_view, sequence, function, procedure, or extension; null for all.")] string? type = null,
        [Description("Literal, case-sensitive substring of the object name; not a SQL LIKE pattern.")] string? search = null,
        int? limit = null,
        int offset = 0,
        bool include_system = false) => ToolReply.Run(database, async () =>
        {
            ValidatePage(limit, offset);
            string? objectType = NormalizeType(type);
            return await _executor.QueryAsync(database, ObjectCatalogSql + ListObjectsSql, new Dictionary<string, object?>
            {
                ["schema"] = schema,
                ["type"] = objectType,
                ["search"] = search,
                ["include_system"] = include_system
            }, limit, offset, ct: ct).ConfigureAwait(false);
        });

    [McpServerTool(Name = "get_object_details", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect one object section: columns, constraints, indexes, triggers, definition, parameters. Resolve ambiguity with type/identity_arguments. Table definitions are structural fragments. Clipping is explicit.")]
    public Task<CallToolResult> GetObjectDetails(
        CancellationToken ct,
        string database,
        string schema,
        string name,
        string section = "columns",
        int? limit = null,
        int offset = 0,
        [Description("Exact pg_get_function_identity_arguments text from list_objects, including argument names. Empty string selects a zero-argument routine.")] string? identity_arguments = null,
        [Description("Optional object type, useful when a relation and routine share a name.")] string? type = null,
        bool include_system = false) => ToolReply.Run(database, async () =>
        {
            ValidatePage(limit, offset);
            if (string.IsNullOrEmpty(schema) || string.IsNullOrEmpty(name))
                throw new ToolException("invalid_object", "schema and name must be nonempty exact PostgreSQL identifiers.");
            string selectedSection = section?.Trim().ToLowerInvariant() ?? "";
            if (selectedSection is not ("columns" or "constraints" or "indexes" or "triggers" or "definition" or "parameters"))
                throw new ToolException("invalid_section", "Choose columns, constraints, indexes, triggers, definition, or parameters.");
            string? objectType = NormalizeType(type);
            return await _executor.WithSessionAsync<object>(database, async (session, token) =>
            {
                QueryPage lookup = await session.QueryAsync(ObjectCatalogSql + LookupSql, new Dictionary<string, object?>
                {
                    ["schema"] = schema,
                    ["name"] = name,
                    ["type"] = objectType,
                    ["identity_arguments"] = identity_arguments,
                    ["include_system"] = include_system
                }, limit: 1, ct: token).ConfigureAwait(false);
                if (lookup.Rows.Count == 0)
                    throw new ToolException("object_not_found", "Object not found, excluded as a system object, or not visible with the current role's privileges.");
                if (lookup.ClippedCells.Count != 0)
                    throw new ToolException("metadata_truncated", "The result cell limit is too small to resolve object metadata safely.");
                object?[] row = lookup.Rows[0];
                if (Convert.ToInt64(row[2], CultureInfo.InvariantCulture) != 1)
                    throw new ToolException("ambiguous_object", "More than one visible object matches. Use list_objects and supply type and the exact routine identity_arguments to select one overload.");
                long objectId = Convert.ToInt64(row[0], CultureInfo.InvariantCulture);
                string resolvedType = (string)row[1]!;
                string sql = SelectDetailsSql(resolvedType, selectedSection);
                QueryPage page = await session.QueryAsync(sql, new Dictionary<string, object?> { ["object_id"] = objectId }, limit, offset, token).ConfigureAwait(false);
                return new { database, schema, name, type = resolvedType, identity_arguments, section = selectedSection, page };
            }, ct: ct).ConfigureAwait(false);
        });

    private void ValidatePage(int? limit, int offset)
    {
        if (limit < 1 || limit > _options.MaxRows)
            throw new ToolException("invalid_limit", $"limit must be between 1 and {_options.MaxRows}.");
        if (offset < 0 || offset > 1000000)
            throw new ToolException("invalid_offset", "offset must be 0..1000000.");
    }

    private static string? NormalizeType(string? type)
    {
        if (type is null) return null;
        string value = type.Trim().ToLowerInvariant().Replace(' ', '_');
        return value is "table" or "view" or "materialized_view" or "sequence" or "function" or "procedure" or "extension"
            ? value
            : throw new ToolException("invalid_type", "Choose table, view, materialized_view, sequence, function, procedure, or extension.");
    }

    private static string SelectDetailsSql(string type, string section)
    {
        bool relation = type is "table" or "view" or "materialized_view";
        bool routine = type is "function" or "procedure";
        return section switch
        {
            "columns" when relation => ColumnsSql,
            "constraints" when relation => ConstraintsSql,
            "indexes" when relation => IndexesSql,
            "triggers" when relation => TriggersSql,
            "parameters" when routine => ParametersSql,
            "definition" when routine => RoutineDefinitionSql,
            "definition" when type is "view" or "materialized_view" => ViewDefinitionSql,
            "definition" when type == "table" => TableDefinitionSql,
            "definition" when type == "sequence" => SequenceDefinitionSql,
            "definition" when type == "extension" => ExtensionDefinitionSql,
            _ => throw new ToolException("unsupported_section", $"Section '{section}' does not apply to object type '{type}'.")
        };
    }

    private const string SchemasSql = """
        SELECT n.nspname AS schema_name, n.oid::bigint AS schema_id,
               pg_catalog.pg_get_userbyid(n.nspowner) AS owner,
               pg_catalog.has_schema_privilege(n.oid, 'CREATE') AS can_create
        FROM pg_catalog.pg_namespace n
        WHERE pg_catalog.has_schema_privilege(n.oid, 'USAGE')
          AND (@include_system OR (pg_catalog.left(n.nspname, 3) <> 'pg_' AND n.nspname <> 'information_schema'))
          AND (CAST(@prefix AS text) IS NULL OR pg_catalog.left(n.nspname, pg_catalog.length(CAST(@prefix AS text))) = CAST(@prefix AS text))
        ORDER BY n.nspname, n.oid
        """;

    // Each branch uses the privileges appropriate to its catalog object; no cross-database names or dynamic identifiers.
    private const string ObjectCatalogSql = """
        WITH objects AS (
            SELECT c.oid::bigint AS object_id, n.nspname::text AS schema_name, c.relname::text AS name,
                   CASE c.relkind WHEN 'v' THEN 'view' WHEN 'm' THEN 'materialized_view'
                        WHEN 'S' THEN 'sequence' ELSE 'table' END AS object_type,
                   NULL::text AS identity_arguments, c.relowner AS owner_id
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p', 'f', 'v', 'm', 'S')
              AND pg_catalog.has_schema_privilege(n.oid, 'USAGE')
              AND CASE WHEN c.relkind = 'S' THEN pg_catalog.has_sequence_privilege(c.oid, 'USAGE,SELECT,UPDATE')
                       ELSE pg_catalog.has_table_privilege(c.oid, 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                            OR pg_catalog.has_any_column_privilege(c.oid, 'SELECT,INSERT,UPDATE,REFERENCES') END
            UNION ALL
            SELECT p.oid::bigint, n.nspname::text, p.proname::text,
                   CASE WHEN p.prokind = 'p' THEN 'procedure' ELSE 'function' END,
                   pg_catalog.pg_get_function_identity_arguments(p.oid), p.proowner
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
            WHERE p.prokind IN ('f', 'p', 'w')
              AND pg_catalog.has_schema_privilege(n.oid, 'USAGE')
              AND pg_catalog.has_function_privilege(p.oid, 'EXECUTE')
            UNION ALL
            SELECT e.oid::bigint, n.nspname::text, e.extname::text, 'extension', NULL::text, e.extowner
            FROM pg_catalog.pg_extension e
            JOIN pg_catalog.pg_namespace n ON n.oid = e.extnamespace
            WHERE pg_catalog.has_schema_privilege(n.oid, 'USAGE')
        )
        """;

    private const string ListObjectsSql = """
        SELECT o.schema_name, o.name, o.object_type AS type, o.object_id, o.identity_arguments,
               pg_catalog.pg_get_userbyid(o.owner_id) AS owner
        FROM objects o
        WHERE (@include_system OR (pg_catalog.left(o.schema_name, 3) <> 'pg_' AND o.schema_name <> 'information_schema'))
          AND (CAST(@schema AS text) IS NULL OR o.schema_name = CAST(@schema AS text))
          AND (CAST(@type AS text) IS NULL OR o.object_type = CAST(@type AS text))
          AND (CAST(@search AS text) IS NULL OR pg_catalog.strpos(o.name, CAST(@search AS text)) > 0)
        ORDER BY o.schema_name, o.name, o.object_type, o.object_id
        """;

    // Count before limiting so ambiguity detection remains correct even with MaxRows=1.
    private const string LookupSql = """
        SELECT o.object_id, o.object_type, pg_catalog.count(*) OVER () AS matches
        FROM objects o
        WHERE o.schema_name = CAST(@schema AS text) AND o.name = CAST(@name AS text)
          AND (@include_system OR (pg_catalog.left(o.schema_name, 3) <> 'pg_' AND o.schema_name <> 'information_schema'))
          AND (CAST(@type AS text) IS NULL OR o.object_type = CAST(@type AS text))
          AND (CAST(@identity_arguments AS text) IS NULL OR o.identity_arguments = CAST(@identity_arguments AS text))
        ORDER BY o.object_type, o.object_id
        LIMIT 1
        """;

    private const string ColumnsSql = """
        SELECT a.attnum AS ordinal, a.attname AS name,
               pg_catalog.format_type(a.atttypid, a.atttypmod) AS data_type,
               NOT a.attnotnull AS nullable, NULLIF(a.attidentity, '')::text AS identity_kind,
               NULLIF(a.attgenerated, '')::text AS generated_kind,
               pg_catalog.pg_get_expr(d.adbin, d.adrelid) AS default_or_generation_expression,
               CASE WHEN a.attcollation <> 0 THEN a.attcollation::pg_catalog.regcollation::text END AS collation,
               pg_catalog.col_description(a.attrelid, a.attnum) AS comment
        FROM pg_catalog.pg_attribute a
        LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
        WHERE a.attrelid = CAST(@object_id AS oid) AND a.attnum > 0 AND NOT a.attisdropped
          AND (pg_catalog.has_table_privilege(a.attrelid, 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
               OR pg_catalog.has_column_privilege(a.attrelid, a.attnum, 'SELECT,INSERT,UPDATE,REFERENCES'))
        ORDER BY a.attnum
        """;

    private const string ConstraintsSql = """
        SELECT c.conname AS name, c.contype::text AS type,
               pg_catalog.pg_get_constraintdef(c.oid, true) AS definition,
               c.convalidated AS validated, c.condeferrable AS deferrable,
               c.condeferred AS initially_deferred, c.conislocal AS is_local,
               CASE WHEN c.confrelid <> 0 THEN c.confrelid::pg_catalog.regclass::text END AS referenced_relation
        FROM pg_catalog.pg_constraint c
        WHERE c.conrelid = CAST(@object_id AS oid)
        ORDER BY c.conname, c.oid
        """;

    private const string IndexesSql = """
        SELECT x.relname AS name, am.amname AS access_method,
               i.indisprimary AS is_primary, i.indisunique AS is_unique,
               i.indisvalid AS is_valid, i.indisready AS is_ready,
               i.indisreplident AS is_replica_identity,
               pg_catalog.pg_get_indexdef(i.indexrelid) AS definition,
               pg_catalog.pg_get_expr(i.indpred, i.indrelid) AS predicate
        FROM pg_catalog.pg_index i
        JOIN pg_catalog.pg_class x ON x.oid = i.indexrelid
        JOIN pg_catalog.pg_am am ON am.oid = x.relam
        WHERE i.indrelid = CAST(@object_id AS oid)
        ORDER BY x.relname, x.oid
        """;

    private const string TriggersSql = """
        SELECT t.tgname AS name, t.tgenabled::text AS enabled_mode,
               pg_catalog.pg_get_triggerdef(t.oid, true) AS definition,
               t.tgfoid::pg_catalog.regprocedure::text AS function
        FROM pg_catalog.pg_trigger t
        WHERE t.tgrelid = CAST(@object_id AS oid) AND NOT t.tgisinternal
        ORDER BY t.tgname, t.oid
        """;

    private const string ParametersSql = """
        SELECT args.ordinality AS ordinal, p.proargnames[args.ordinality::int] AS name,
               CASE COALESCE(p.proargmodes[args.ordinality::int], 'i'::"char")
                   WHEN 'i' THEN 'in' WHEN 'o' THEN 'out' WHEN 'b' THEN 'inout'
                   WHEN 'v' THEN 'variadic' WHEN 't' THEN 'table' END AS mode,
               pg_catalog.format_type(args.type_oid, NULL) AS data_type,
               pg_catalog.pg_get_function_result(p.oid) AS routine_result,
               p.proretset AS returns_set
        FROM pg_catalog.pg_proc p
        CROSS JOIN LATERAL pg_catalog.unnest(COALESCE(p.proallargtypes, p.proargtypes::oid[])) WITH ORDINALITY AS args(type_oid, ordinality)
        WHERE p.oid = CAST(@object_id AS oid)
        ORDER BY args.ordinality
        """;

    private const string RoutineDefinitionSql = """
        SELECT pg_catalog.pg_get_functiondef(p.oid) AS definition,
               pg_catalog.pg_get_function_identity_arguments(p.oid) AS identity_arguments,
               pg_catalog.pg_get_function_arguments(p.oid) AS arguments,
               pg_catalog.pg_get_function_result(p.oid) AS result,
               l.lanname AS language, p.prosecdef AS security_definer,
               p.provolatile::text AS volatility, p.proparallel::text AS parallel_safety
        FROM pg_catalog.pg_proc p
        JOIN pg_catalog.pg_language l ON l.oid = p.prolang
        WHERE p.oid = CAST(@object_id AS oid)
        ORDER BY p.oid
        """;

    private const string ViewDefinitionSql = """
        SELECT pg_catalog.pg_get_viewdef(c.oid, true) AS definition,
               c.relkind = 'm' AS is_materialized, c.relispopulated AS populated,
               pg_catalog.obj_description(c.oid, 'pg_class') AS comment
        FROM pg_catalog.pg_class c
        WHERE c.oid = CAST(@object_id AS oid)
        ORDER BY c.oid
        """;

    private const string TableDefinitionSql = """
        SELECT parts.section, parts.name, parts.definition
        FROM (
            SELECT 0 AS sort_group, 0::bigint AS sort_id, 'relation'::text AS section, c.relname::text AS name,
                   pg_catalog.format('kind=%s; persistence=%s; row_security=%s; force_row_security=%s',
                          c.relkind, c.relpersistence, c.relrowsecurity, c.relforcerowsecurity) AS definition
            FROM pg_catalog.pg_class c WHERE c.oid = CAST(@object_id AS oid)
            UNION ALL
            SELECT 1, a.attnum::bigint, 'column', a.attname::text,
                   pg_catalog.format('%I %s%s%s%s', a.attname, pg_catalog.format_type(a.atttypid, a.atttypmod),
                       CASE WHEN a.attcollation <> 0 THEN pg_catalog.format(' COLLATE %s', a.attcollation::pg_catalog.regcollation) ELSE '' END,
                       CASE WHEN a.attidentity = 'a' THEN ' GENERATED ALWAYS AS IDENTITY'
                            WHEN a.attidentity = 'd' THEN ' GENERATED BY DEFAULT AS IDENTITY'
                            WHEN a.attgenerated = 's' THEN pg_catalog.format(' GENERATED ALWAYS AS (%s) STORED', pg_catalog.pg_get_expr(d.adbin, d.adrelid))
                            WHEN a.attgenerated = 'v' THEN pg_catalog.format(' GENERATED ALWAYS AS (%s) VIRTUAL', pg_catalog.pg_get_expr(d.adbin, d.adrelid))
                            WHEN d.adbin IS NOT NULL THEN ' DEFAULT ' || pg_catalog.pg_get_expr(d.adbin, d.adrelid) ELSE '' END,
                       CASE WHEN a.attnotnull THEN ' NOT NULL' ELSE '' END)
            FROM pg_catalog.pg_attribute a
            LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE a.attrelid = CAST(@object_id AS oid) AND a.attnum > 0 AND NOT a.attisdropped
              AND (pg_catalog.has_table_privilege(a.attrelid, 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                   OR pg_catalog.has_column_privilege(a.attrelid, a.attnum, 'SELECT,INSERT,UPDATE,REFERENCES'))
            UNION ALL
            SELECT 2, c.oid::bigint, 'constraint', c.conname::text, pg_catalog.pg_get_constraintdef(c.oid, true)
            FROM pg_catalog.pg_constraint c WHERE c.conrelid = CAST(@object_id AS oid)
            UNION ALL
            SELECT 3, c.oid::bigint, 'partition_key', c.relname::text, pg_catalog.pg_get_partkeydef(c.oid)
            FROM pg_catalog.pg_class c WHERE c.oid = CAST(@object_id AS oid) AND c.relkind = 'p'
            UNION ALL
            SELECT 4, c.oid::bigint, 'partition_bound', c.relname::text, pg_catalog.pg_get_expr(c.relpartbound, c.oid)
            FROM pg_catalog.pg_class c WHERE c.oid = CAST(@object_id AS oid) AND c.relispartition
            UNION ALL
            SELECT 5, i.inhseqno::bigint, 'parent', i.inhparent::pg_catalog.regclass::text, i.inhparent::pg_catalog.regclass::text
            FROM pg_catalog.pg_inherits i WHERE i.inhrelid = CAST(@object_id AS oid)
        ) parts
        ORDER BY parts.sort_group, parts.sort_id, parts.name
        """;

    private const string SequenceDefinitionSql = """
        SELECT pg_catalog.format_type(s.seqtypid, NULL) AS data_type, s.seqstart AS start_value,
               s.seqincrement AS increment, s.seqmin AS minimum_value, s.seqmax AS maximum_value,
               s.seqcache AS cache, s.seqcycle AS cycle,
               pg_catalog.obj_description(s.seqrelid, 'pg_class') AS comment
        FROM pg_catalog.pg_sequence s
        WHERE s.seqrelid = CAST(@object_id AS oid)
        ORDER BY s.seqrelid
        """;

    private const string ExtensionDefinitionSql = """
        SELECT e.extname AS name, e.extversion AS version, n.nspname AS schema_name,
               e.extrelocatable AS relocatable, pg_catalog.obj_description(e.oid, 'pg_extension') AS comment
        FROM pg_catalog.pg_extension e
        JOIN pg_catalog.pg_namespace n ON n.oid = e.extnamespace
        WHERE e.oid = CAST(@object_id AS oid)
        ORDER BY e.oid
        """;
}
