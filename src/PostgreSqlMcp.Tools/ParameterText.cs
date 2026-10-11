// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

namespace PostgreSqlMcp.Tools;

/// <summary>Parameter descriptions shared by several tools; one wording per concept, kept short because every tools/list repeats them.</summary>
internal static class ParameterText
{
    public const string Database = "Database name from list_databases, or a profile alias.";
    public const string Target = "Connection profile; if omitted, an alias's own profile, else the default.";
    public const string Limit = "Page size; default 100, at most max_rows.";
    public const string Offset = "next_offset from the previous page.";
    public const string IncludeSystem = "Include pg_catalog, information_schema and pg_* schemas.";
    public const string Sql = "One SQL statement.";
    public const string SchemaFilter = "Exact schema name; all if omitted.";
    public const string Values = "Values for $1..$n placeholders, in order: strings, numbers, booleans or null (max 256). Never interpolated; PostgreSQL infers each type from context like a quoted literal, so cast when ambiguous ($1::int, $1::jsonb).";
}
