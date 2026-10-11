// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using Npgsql;

namespace PostgreSqlMcp.Core;

/// <summary>
/// Non-fatal operator advice for configurations whose enforced behavior is weaker than a hardened one:
/// unverified TLS to remote hosts, and logins whose privileges reach past READ ONLY transactions.
/// Messages name profiles and roles only, never connection strings or credentials.
/// </summary>
public static class SecurityWarnings
{
    // Each capability can cause effects that SET TRANSACTION READ ONLY and restricted mode do not prevent.
    // A superuser is a "member" of every role, so its other rows would only repeat the first finding.
    private const string RoleSql = """
        WITH super AS (
          SELECT r.rolname FROM pg_catalog.pg_roles r
          WHERE r.rolsuper AND pg_catalog.pg_has_role(session_user, r.oid, 'MEMBER')
        )
        SELECT warning FROM (
          SELECT 1, 'login is or can SET ROLE to superuser ' || quote_ident(rolname) FROM super
          UNION ALL
          SELECT 2, 'login is a member of ' || r.rolname FROM pg_catalog.pg_roles r
          WHERE r.rolname IN ('pg_execute_server_program', 'pg_write_server_files', 'pg_read_server_files', 'pg_signal_backend', 'pg_checkpoint')
            AND pg_catalog.pg_has_role(session_user, r.oid, 'MEMBER')
          UNION ALL
          SELECT 3, 'role ' || quote_ident(r.rolname) || ' has REPLICATION' FROM pg_catalog.pg_roles r
          WHERE r.rolreplication AND pg_catalog.pg_has_role(session_user, r.oid, 'MEMBER')
          UNION ALL
          SELECT 4, 'role ' || quote_ident(r.rolname) || ' has BYPASSRLS' FROM pg_catalog.pg_roles r
          WHERE r.rolbypassrls AND pg_catalog.pg_has_role(session_user, r.oid, 'MEMBER')
        ) AS warnings(severity, warning)
        WHERE severity = 1 OR NOT EXISTS (SELECT 1 FROM super)
        ORDER BY severity, warning COLLATE "C"
        """;

    /// <summary>Profiles that reach a non-local host without certificate verification (any SSL Mode other than VerifyCA/VerifyFull, including the Prefer default).</summary>
    public static IReadOnlyList<string> Tls(ServerOptions options)
    {
        // One line per distinct server login (host, port, user, mode) naming its profiles: lock aliases of one connection
        // string share a line, while profiles that reach different ports or users are each named.
        var groups = new Dictionary<(string Host, int Port, string? User, SslMode Mode), List<string>>();
        foreach (var (alias, connectionString) in options.Targets.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (builder.SslMode is SslMode.VerifyCA or SslMode.VerifyFull) continue;
            string host = builder.Host ?? "";
            if (TlsPolicy.IsLocalHost(host)) continue;
            var key = (host, builder.Port, builder.Username, builder.SslMode);
            if (!groups.TryGetValue(key, out List<string>? aliases)) groups[key] = aliases = [];
            aliases.Add(alias);
        }
        var warnings = new List<string>();
        foreach (var ((_, _, _, mode), aliases) in groups)
        {
            string names = aliases.Count == 1 ? $"profile {aliases[0]}"
                : $"profiles {string.Join(", ", aliases.Take(5))}{(aliases.Count > 5 ? $" and {aliases.Count - 5} more" : "")}";
            string effect = mode switch
            {
                SslMode.Disable => "does not encrypt the connection",
                SslMode.Require => "encrypts without verifying the server certificate",
                SslMode.Allow => "encrypts only if the server requires TLS and never verifies the server certificate",
                _ => "encrypts only if the server offers TLS and never verifies the server certificate"
            };
            warnings.Add($"{names}: SSL Mode={mode} to a non-local host {effect}, so someone on the network path could read the traffic or impersonate the server. " +
                "This is fine on a trusted network; to harden, set SSL Mode=VerifyFull in the connection string (add Root Certificate=<CA file> for a private CA).");
        }
        return warnings;
    }

    /// <summary>Privileged capabilities of each distinct login, checked through the database the server would open.</summary>
    public static async Task<IReadOnlyList<string>> RolesAsync(DatabaseRegistry registry, SqlExecutor executor, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        foreach (var (target, _) in registry.LoginTargets())
        {
            try
            {
                List<string> found = await executor.WithSessionAsync(registry.GetDiscoveryDatabase(target), async (session, token) =>
                {
                    await using SessionReader result = await session.ExecuteReaderAsync([new NpgsqlBatchCommand(RoleSql)],
                        System.Data.CommandBehavior.Default, token).ConfigureAwait(false);
                    NpgsqlDataReader reader = result.Reader;
                    var values = new List<string>();
                    while (await reader.ReadAsync(token).ConfigureAwait(false)) values.Add(reader.GetString(0));
                    return values;
                }, ct: ct, target: target).ConfigureAwait(false);
                foreach (string warning in found)
                    warnings.Add($"target {target}: {warning}. READ ONLY transactions and restricted mode do not stop its privileged functions or side effects; use a least-privileged role.");
            }
            // Connectivity is reported by --validate itself; any failure here only means no warning, never a crash.
            catch (Exception) when (!ct.IsCancellationRequested) { }
        }
        return warnings;
    }
}
