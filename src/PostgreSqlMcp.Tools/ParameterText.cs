namespace PostgreSqlMcp.Tools;

/// <summary>Parameter descriptions shared by several tools; one wording per concept, kept short because every tools/list repeats them.</summary>
internal static class ParameterText
{
    public const string Database = "Database name from list_databases, or a profile alias.";
    public const string Target = "Connection profile; if omitted, a profile alias given as database selects its profile, otherwise the default profile.";
    public const string Limit = "Page size; default 100, at most max_rows (see list_databases limits).";
    public const string Offset = "next_offset from the previous page.";
    public const string IncludeSystem = "Include pg_catalog, information_schema and pg_* schemas.";
    public const string Sql = "One SQL statement.";
    public const string SchemaFilter = "Exact schema name; all if omitted.";
}
