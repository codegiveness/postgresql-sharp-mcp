// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PostgreSqlMcp.Core;
using PostgreSqlMcp.Tools;

if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
{
    Console.WriteLine("""
        postgresql-sharp-mcp — database-agnostic PostgreSQL MCP over stdio; the MCP client launches it.
        Prefer environment variables to CLI values: arguments can expose secrets. A CLI value overrides its
        environment variable except where noted.

        Connection source (set exactly one; a connection string cannot be combined with targets JSON/file):
          POSTGRES_CONNECTION_STRING   Npgsql key=value string. Without a database lock it creates profile "primary"
                                       (Database defaults to postgres); with --databases each locked name is a target
                                       alias of this one login profile, with Database set to that name.
          --connection-string STR      Same, but POSTGRES_CONNECTION_STRING wins when both are set.
          POSTGRES_TARGETS             JSON object: profile -> Npgsql string. Each needs Host and Database; names are
                                       1..128 characters without control characters. Wins over a targets file.
          POSTGRES_TARGETS_FILE        Path to a targets JSON file with the same format.
          --targets-file PATH          Same; overrides POSTGRES_TARGETS_FILE.

        TLS (SSL Mode inside each connection string):
          Omitted                      SSL Mode=Prefer for every host: TLS when the server offers it, plaintext otherwise;
                                       the server certificate is not verified. A non-loopback host gets a [warning] on
                                       stderr with hardening advice; startup continues. Exception: a string with Root
                                       Certificate=<CA file> but no SSL Mode gets SSL Mode=VerifyFull.
          Explicit (any value)         Used unchanged. To verify the server, set SSL Mode=VerifyFull (certificate chain
                                       and host name checked before any credential is sent; add Root Certificate=<CA
                                       file> for a private CA).
          Failures                     tls_verification_failed (VerifyCA/VerifyFull: untrusted CA or host name mismatch)
                                       and tls_unavailable (VerifyCA/VerifyFull/Require: server offers no TLS) name the
                                       profile and the fix; no plaintext fallback.

        Options:
          --databases JSON             POSTGRES_DATABASES. Database lock: JSON array of 1+ unique physical names
                                       (1..63 UTF-8 bytes, no NUL), with a connection string or targets. Tools reject
                                       every other database before connecting. Startup also checks whether PostgreSQL
                                       grants confine the login to the list (see --database-lock-mode).
          --database-lock-mode MODE    POSTGRES_DATABASE_LOCK_MODE: advisory (default) or strict. advisory prints grant
                                       findings as warnings and starts; strict refuses to start on any finding and
                                       requires a database lock.
          --require-database-lock      POSTGRES_REQUIRE_DATABASE_LOCK=true (default false) also works. Refuse to
                                       start unless a database lock is configured.
          --access-mode MODE           POSTGRES_ACCESS_MODE: unrestricted (default) or restricted.
          --query-timeout SECONDS      POSTGRES_QUERY_TIMEOUT: 1..600 (default 30).
          --log-level LEVEL            POSTGRES_LOG_LEVEL: trace|debug|information|warning|error|critical|none
                                       (default warning).
          --validate                   Check each seed connection (and the database lock) and exit; diagnostics and
                                       privilege/TLS/lock warnings on stderr. Exit status 1 only for a failed connection
                                       or, with --database-lock-mode strict, a lock finding.
          --version                    Print executable version.
          -h, --help                   Print this help.

        Environment only (default; bounds):
          POSTGRES_MAX_ROWS 1000; 1..5000          POSTGRES_MAX_RESULT_BYTES 65536; 4096..1048576
          POSTGRES_MAX_CELL_CHARS 4096; 1..16384   POSTGRES_MAX_CONCURRENT_CALLS 16; 1..64
          POSTGRES_POOL_SIZE 8; 1..32 (targets profiles x pool size must not exceed 256; a connection string is one)

        Discover accessible databases with list_databases; select a physical name per call with the tool's
        database argument. Optional tool target selects a profile; the default is the profile named primary if it
        exists, otherwise the first profile name in ordinal order.
        """);
    return 0;
}
if (args.Contains("--version", StringComparer.Ordinal))
{
    Console.WriteLine("postgresql-sharp-mcp " + Assembly.GetExecutingAssembly().GetName().Version);
    return 0;
}
ServerOptions options;
try { options = ServerOptions.Parse(args); }
catch (ToolException ex) { Console.Error.WriteLine($"[startup] {ex.Message}"); return 1; }
if (!Enum.TryParse<LogLevel>(options.LogLevel, true, out var logLevel) || !Enum.IsDefined(logLevel))
{ Console.Error.WriteLine("[startup] Invalid log level."); return 1; }
ToolReply.Configure(options);
// Warnings never block startup: they name configurations whose real exposure exceeds what tools advertise.
foreach (string warning in SecurityWarnings.Tls(options)) Console.Error.WriteLine($"[warning] {warning}");
static async Task<bool> CheckDatabaseLockAsync(ServerOptions options, DatabaseRegistry registry, SqlExecutor executor)
{
    if (registry.AllowedDatabaseNames is null) return true;
    IReadOnlyList<string> findings = await DatabaseLockCheck.VerifyAsync(registry, executor);
    if (findings.Count == 0)
    {
        Console.Error.WriteLine("[startup] Database lock verified: PostgreSQL grants confine every configured login to POSTGRES_DATABASES.");
        return true;
    }
    string prefix = options.StrictDatabaseLock ? "[startup]   " : "[warning] database lock (advisory): ";
    if (options.StrictDatabaseLock)
        Console.Error.WriteLine("[startup] Refusing to start: the PostgreSQL check for the configured database lock (POSTGRES_DATABASES) failed and POSTGRES_DATABASE_LOCK_MODE is strict.");
    foreach (string finding in findings.Take(32)) Console.Error.WriteLine(prefix + finding);
    if (findings.Count > 32) Console.Error.WriteLine($"{prefix}... {findings.Count - 32} more findings.");
    if (!options.StrictDatabaseLock)
    {
        // In-process enforcement still rejects every database outside the list; only PostgreSQL-side confinement is missing.
        Console.Error.WriteLine("[warning] database lock (advisory): PostgreSQL does not confine the configured login to POSTGRES_DATABASES (or the check " +
            "could not confirm it), so SQL can still reach other databases. Tools still reject databases outside the list. To refuse startup " +
            "instead, set POSTGRES_DATABASE_LOCK_MODE=strict after hardening the role (SECURITY.md, Database lock hardening).");
        return true;
    }
    Console.Error.WriteLine("[startup] Harden the role as described in SECURITY.md (Database lock hardening), then retry.");
    return false;
}
if (options.Validate)
{
    await using var registry = new DatabaseRegistry(options);
    using var executor = new SqlExecutor(options, registry);
    bool ok = true;
    foreach (string target in registry.TargetNames)
    {
        var result = await ToolReply.Run(target, async () =>
        {
            // Validate through the database the server would actually open, never a bootstrap database outside the lock.
            // The report stays keyed by profile alias; its row names the database actually reached.
            string database = registry.GetDiscoveryDatabase(target);
            QueryPage page = await executor.QueryAsync(database, "SELECT current_database() AS database", limit: 1, target: target);
            return page with { Database = target };
        });
        Console.Error.WriteLine(result.StructuredContent?.GetRawText());
        ok &= result.IsError != true;
    }
    foreach (string warning in await SecurityWarnings.RolesAsync(registry, executor))
        Console.Error.WriteLine($"[warning] {warning}");
    ok &= await CheckDatabaseLockAsync(options, registry, executor);
    return ok ? 0 : 1;
}
{
    // The serving registry stays lazy; the check uses its own short-lived pools.
    await using var registry = new DatabaseRegistry(options);
    using var executor = new SqlExecutor(options, registry);
    if (!await CheckDatabaseLockAsync(options, registry, executor)) return 1;
}
// Disable default file watchers/providers; stdout belongs exclusively to MCP.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
builder.Logging.SetMinimumLevel(logLevel).AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
// SDK diagnostics include complete JSON-RPC bodies, including SQL, parameters and results.
// Provider diagnostics may also contain SQL or server-controlled exception text.
builder.Logging.AddFilter("ModelContextProtocol", LogLevel.None).AddFilter("Npgsql", LogLevel.None);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<DatabaseRegistry>();
builder.Services.AddSingleton<SqlExecutor>();
builder.Services.AddPostgreSqlTools(options);
builder.Services.AddMcpServer(o => o.ServerInstructions = ToolRegistration.Instructions(options)).WithStdioServerTransport();
using var host = builder.Build();
await host.RunAsync();
return 0;
