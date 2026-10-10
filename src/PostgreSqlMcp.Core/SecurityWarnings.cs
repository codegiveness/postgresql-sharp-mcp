using System.Net;
using Npgsql;

namespace PostgreSqlMcp.Core;

/// <summary>
/// Non-fatal operator warnings for configurations whose enforced behavior is weaker than the advertised one:
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

    /// <summary>Profiles that reach a non-local host without certificate verification (Npgsql's default SSL Mode is Prefer).</summary>
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
            if (host.Split(',').All(IsLocal)) continue;
            var key = (host, builder.Port, builder.Username, builder.SslMode);
            if (!groups.TryGetValue(key, out List<string>? aliases)) groups[key] = aliases = [];
            aliases.Add(alias);
        }
        var warnings = new List<string>();
        foreach (var ((_, _, _, mode), aliases) in groups)
        {
            string names = aliases.Count == 1 ? $"profile {aliases[0]}"
                : $"profiles {string.Join(", ", aliases.Take(5))}{(aliases.Count > 5 ? $" and {aliases.Count - 5} more" : "")}";
            warnings.Add($"{names}: SSL Mode={mode} to a non-local host does not verify the server certificate, " +
                "so a network attacker can impersonate the server. Use SSL Mode=VerifyFull with the provider's CA certificate.");
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
                    await using var command = new NpgsqlCommand(RoleSql, session.Connection, session.Transaction);
                    await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
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

    private static bool IsLocal(string entry)
    {
        string host = entry.Trim();
        if (host.Length == 0 || host[0] is '/' or '@') return true; // Unix-domain socket directory or abstract socket.
        if (host.StartsWith('['))
        {
            int close = host.IndexOf(']');
            host = close > 0 ? host[1..close] : host;
        }
        else if (host.Count(c => c == ':') == 1) host = host[..host.IndexOf(':')]; // host:port
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address));
    }
}
