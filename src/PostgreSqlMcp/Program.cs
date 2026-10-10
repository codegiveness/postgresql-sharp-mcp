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
        postgresql-sharp-mcp — database-agnostic PostgreSQL MCP over stdio; the MCP client launches it.
        Prefer environment variables to CLI values: arguments can expose secrets. A CLI value overrides its
        environment variable except where noted.

        Connection source (set exactly one; a connection string cannot be combined with targets JSON/file):
          POSTGRES_CONNECTION_STRING   Npgsql key=value string; creates profile "primary". Database defaults to postgres.
          --connection-string STR      Same, but POSTGRES_CONNECTION_STRING wins when both are set.
          POSTGRES_TARGETS             JSON object: profile -> Npgsql string. Each needs Host and Database; names are
                                       1..128 characters without control characters. Wins over a targets file.
          POSTGRES_TARGETS_FILE        Path to a targets JSON file with the same format.
          --targets-file PATH          Same; overrides POSTGRES_TARGETS_FILE.

        Options:
          --databases JSON             POSTGRES_DATABASES. Database lock: JSON array of 1+ unique physical names
                                       (1..63 UTF-8 bytes, no NUL), with a connection string or targets. Startup then
                                       verifies PostgreSQL grants enforce the lock.
          --require-database-lock      POSTGRES_REQUIRE_DATABASE_LOCK=true (default false) also works. Refuse to
                                       start unless a database lock is configured.
          --access-mode MODE           POSTGRES_ACCESS_MODE: unrestricted (default) or restricted.
          --query-timeout SECONDS      POSTGRES_QUERY_TIMEOUT: 1..600 (default 30).
          --log-level LEVEL            POSTGRES_LOG_LEVEL: trace|debug|information|warning|error|critical|none
                                       (default warning).
          --validate                   Check each seed connection (and the database lock) and exit; diagnostics on stderr.
          --version                    Print executable version.
          -h, --help                   Print this help.

        Environment only (default; bounds):
          POSTGRES_MAX_ROWS 1000; 1..5000          POSTGRES_MAX_RESULT_BYTES 65536; 4096..1048576
          POSTGRES_MAX_CELL_CHARS 4096; 1..16384   POSTGRES_MAX_CONCURRENT_CALLS 16; 1..64
          POSTGRES_POOL_SIZE 8; 1..32 (profiles x pool size must not exceed 256)

        Discover accessible databases with list_databases; select a physical name per call with the tool's
        database argument. Optional tool target selects a profile; default is primary or the first ordinal alias.
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

static void AddTools<T>(IServiceCollection services, bool unrestricted) where T : class
{
    foreach (var method in typeof(T).GetMethods(BindingFlags.Instance | BindingFlags.Public))
    {
        var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
        if (attribute is null) continue;
        bool execute = attribute.Name == "execute_sql";
        services.AddSingleton(sp => McpServerTool.Create(method,
            r => ActivatorUtilities.CreateInstance(r.Services!, typeof(T)),
            new McpServerToolCreateOptions
            {
                Services = sp, ReadOnly = !execute || !unrestricted,
                Destructive = execute && unrestricted, Idempotent = !execute, OpenWorld = false
            }));
    }
}
AddTools<DatabaseTools>(builder.Services, options.Unrestricted);
AddTools<SqlTools>(builder.Services, options.Unrestricted);
AddTools<PlanTools>(builder.Services, options.Unrestricted);
AddTools<OpsTools>(builder.Services, options.Unrestricted);
builder.Services.AddMcpServer().WithStdioServerTransport();
using var host = builder.Build();
await host.RunAsync();
return 0;
