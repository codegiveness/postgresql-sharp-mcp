using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using PostgreSqlMcp.Core;
using PostgreSqlMcp.Tools;

if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
{
    Console.WriteLine("""
        postgresql-sharp-mcp — database-agnostic PostgreSQL MCP over stdio
        Recommended: inherit POSTGRES_CONNECTION_STRING from a prepared shell or secret manager.
        No targets JSON file is required for a single PostgreSQL server.
        --targets-file PATH      JSON object: connection profile -> Npgsql seed connection
        --connection-string STR  Optional CLI value; prefer environment input to avoid exposed secret arguments
        --databases JSON         Optional database lock (JSON string array); applies to connection-string and targets modes
        --require-database-lock  Refuse to start unless a database lock is configured
        --access-mode MODE       unrestricted (default) or restricted
        --query-timeout SECONDS  1..600 (default 30)
        --log-level LEVEL        trace|debug|information|warning|error|critical|none
        --validate               Check each configured seed connection (and the database lock) and exit; diagnostics on stderr
        --version                Print executable version
        Environment: POSTGRES_CONNECTION_STRING, POSTGRES_TARGETS or POSTGRES_TARGETS_FILE.
        POSTGRES_DATABASES locks every profile to the listed physical databases; startup then verifies
          PostgreSQL grants enforce the same lock. POSTGRES_REQUIRE_DATABASE_LOCK=true requires a lock.
        Targets JSON wins over file.
        Connection-string environment and targets JSON/file are mutually exclusive; unset stale profile settings.
        Discover accessible databases with list_databases; select a physical name per call.
        Optional tool target selects a profile; default is primary or the first ordinal alias.
        No USE statement, shared current database, or per-database file editing is required.
        Limits: POSTGRES_MAX_ROWS, POSTGRES_MAX_RESULT_BYTES, POSTGRES_MAX_CELL_CHARS,
          POSTGRES_POOL_SIZE, POSTGRES_MAX_CONCURRENT_CALLS.
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
static async Task<bool> CheckDatabaseLockAsync(DatabaseRegistry registry, SqlExecutor executor)
{
    if (registry.AllowedDatabaseNames is null) return true;
    IReadOnlyList<string> findings = await DatabaseLockCheck.VerifyAsync(registry, executor);
    if (findings.Count == 0)
    {
        Console.Error.WriteLine("[startup] Database lock verified: PostgreSQL grants confine every configured login to POSTGRES_DATABASES.");
        return true;
    }
    Console.Error.WriteLine("[startup] Refusing to start: the PostgreSQL check for the configured database lock (POSTGRES_DATABASES) failed.");
    foreach (string finding in findings.Take(32)) Console.Error.WriteLine($"[startup]   {finding}");
    if (findings.Count > 32) Console.Error.WriteLine($"[startup]   ... {findings.Count - 32} more findings.");
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
    ok &= await CheckDatabaseLockAsync(registry, executor);
    return ok ? 0 : 1;
}
{
    // The serving registry stays lazy; the check uses its own short-lived pools.
    await using var registry = new DatabaseRegistry(options);
    using var executor = new SqlExecutor(options, registry);
    if (!await CheckDatabaseLockAsync(registry, executor)) return 1;
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
