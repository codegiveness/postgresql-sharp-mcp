using Npgsql;

namespace PostgreSqlMcp.Core;

/// <summary>
/// Startup proof that PostgreSQL itself confines each configured login to the database lock.
/// Every probe runs through <see cref="SqlExecutor"/>, so the check only ever connects to locked databases.
/// </summary>
public static class DatabaseLockCheck
{
    // One row per finding, most severe first. pg_has_role(..., 'MEMBER') covers roles the login inherits or can SET ROLE to.
    private const string RoleSql = """
        SELECT finding FROM (
          SELECT 1, 'role ' || quote_ident(r.rolname) || ' is a superuser' FROM pg_catalog.pg_roles r
          WHERE r.rolsuper AND pg_catalog.pg_has_role(session_user, r.oid, 'MEMBER')
          UNION ALL
          SELECT 2, 'login is a member of ' || r.rolname FROM pg_catalog.pg_roles r
          WHERE r.rolname IN ('pg_read_server_files', 'pg_write_server_files', 'pg_execute_server_program')
            AND pg_catalog.pg_has_role(session_user, r.oid, 'MEMBER')
          UNION ALL
          SELECT 3, 'role ' || quote_ident(r.rolname) || ' has CREATEROLE' FROM pg_catalog.pg_roles r
          WHERE r.rolcreaterole AND pg_catalog.pg_has_role(session_user, r.oid, 'MEMBER')
          UNION ALL
          SELECT 4, 'role ' || quote_ident(r.rolname) || ' has CREATEDB' FROM pg_catalog.pg_roles r
          WHERE r.rolcreatedb AND pg_catalog.pg_has_role(session_user, r.oid, 'MEMBER')
          UNION ALL
          SELECT 5, 'login owns database ' || quote_ident(d.datname) || ' outside the lock' FROM pg_catalog.pg_database d
          WHERE pg_catalog.pg_has_role(session_user, d.datdba, 'MEMBER') AND NOT d.datname::text = ANY(@databases::text[])
          UNION ALL
          SELECT 6, 'login can connect to database ' || quote_ident(d.datname) || ' outside the lock' FROM pg_catalog.pg_database d
          WHERE d.datallowconn AND pg_catalog.has_database_privilege(session_user, d.oid, 'CONNECT')
            AND NOT d.datname::text = ANY(@databases::text[])
        ) AS findings(severity, finding)
        ORDER BY severity, finding COLLATE "C"
        """;

    private const string ReachableSql = """
        SELECT d.datname::text FROM pg_catalog.pg_database d
        WHERE d.datallowconn AND pg_catalog.has_database_privilege(session_user, d.oid, 'CONNECT')
          AND d.datname::text = ANY(@databases::text[])
        ORDER BY d.datname COLLATE "C"
        """;

    // Untrusted extensions: a non-superuser cannot install them, so installed objects are what matters.
    // Server-file functions: EXECUTE is revoked from PUBLIC, but an explicit grant lets a non-superuser read data-directory
    // files (including other databases' relation files) or write server files without any pg_*_server_files membership.
    // Function ACLs are per database, so this runs in every reachable locked database; absent signatures resolve to NULL.
    private const string PerDatabaseSql = """
        SELECT 'extension ' || e.extname || ' is installed' FROM pg_catalog.pg_extension e
        WHERE e.extname IN ('dblink', 'postgres_fdw')
        UNION ALL
        SELECT 'foreign server ' || quote_ident(s.srvname) || ' exists' FROM pg_catalog.pg_foreign_server s
        UNION ALL
        SELECT 'login can execute server-file function ' || f.oid::pg_catalog.regprocedure::text
        FROM (SELECT pg_catalog.to_regprocedure(signature) AS oid FROM pg_catalog.unnest(ARRAY[
            'pg_catalog.pg_read_file(text)', 'pg_catalog.pg_read_file(text,boolean)',
            'pg_catalog.pg_read_file(text,bigint,bigint)', 'pg_catalog.pg_read_file(text,bigint,bigint,boolean)',
            'pg_catalog.pg_read_binary_file(text)', 'pg_catalog.pg_read_binary_file(text,boolean)',
            'pg_catalog.pg_read_binary_file(text,bigint,bigint)', 'pg_catalog.pg_read_binary_file(text,bigint,bigint,boolean)',
            'pg_catalog.pg_ls_dir(text)', 'pg_catalog.pg_ls_dir(text,boolean,boolean)',
            'pg_catalog.lo_import(text)', 'pg_catalog.lo_import(text,oid)', 'pg_catalog.lo_export(oid,text)',
            'pg_catalog.pg_file_write(text,text,boolean)']) AS signature) f
        WHERE f.oid IS NOT NULL AND pg_catalog.has_function_privilege(session_user, f.oid, 'EXECUTE')
          -- Superusers execute everything and are already refused by the role check.
          AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles r WHERE r.rolsuper AND pg_catalog.pg_has_role(session_user, r.oid, 'MEMBER'))
        """;

    /// <summary>
    /// Returns human-readable findings; empty means every configured login is confined to the lock.
    /// Messages name profiles, roles and databases only, never connection strings or credentials.
    /// </summary>
    public static async Task<IReadOnlyList<string>> VerifyAsync(DatabaseRegistry registry, SqlExecutor executor, CancellationToken ct = default)
    {
        string[] locked = registry.AllowedDatabaseNames
            ?? throw new InvalidOperationException("The PostgreSQL lock check requires a configured database lock.");
        var parameters = new Dictionary<string, object?> { ["databases"] = locked };
        var findings = new List<string>();
        var checkedDatabases = new HashSet<(string Login, string Database)>();
        foreach (var (target, login) in registry.LoginTargets())
        {
            string discovery = registry.GetDiscoveryDatabase(target);
            try
            {
                foreach (string finding in await ReadTextAsync(executor, discovery, target, RoleSql, parameters, ct).ConfigureAwait(false))
                    findings.Add($"target {target}: {finding}");
                foreach (string database in await ReadTextAsync(executor, discovery, target, ReachableSql, parameters, ct).ConfigureAwait(false))
                {
                    if (!checkedDatabases.Add((login, database))) continue;
                    foreach (string finding in await ReadTextAsync(executor, database, target, PerDatabaseSql, null, ct).ConfigureAwait(false))
                        findings.Add($"target {target}, database {database}: {finding}");
                }
            }
            catch (ToolException ex) { findings.Add($"target {target}: lock check failed ({ex.Code}): {ex.Message}"); }
            catch (PostgresException ex) { findings.Add($"target {target}: lock check failed (SQLSTATE {ex.SqlState})."); }
            catch (NpgsqlException) { findings.Add($"target {target}: lock check could not connect or authenticate."); }
            catch (TimeoutException) { findings.Add($"target {target}: lock check timed out."); }
        }
        return findings;
    }

    private static Task<List<string>> ReadTextAsync(SqlExecutor executor, string database, string target, string sql,
        IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct) =>
        executor.WithSessionAsync(database, async (session, token) =>
        {
            await using var command = new NpgsqlCommand(sql, session.Connection, session.Transaction);
            if (parameters is not null)
                foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var values = new List<string>();
            while (await reader.ReadAsync(token).ConfigureAwait(false)) values.Add(reader.GetString(0));
            return values;
        }, ct: ct, target: target);
}
