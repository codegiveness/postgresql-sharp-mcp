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
        postgresql-sharp-mcp — PostgreSQL MCP over stdio
        --targets-file PATH      JSON object: target alias -> Npgsql connection string
        --connection-string STR  Base credentials (POSTGRES_CONNECTION_STRING overrides CLI)
        --databases JSON         Explicit database allowlist (JSON string array)
        --access-mode MODE       restricted (default) or unrestricted
        --query-timeout SECONDS  1..600 (default 30)
        --log-level LEVEL        trace|debug|information|warning|error|critical|none
        --validate               Check each configured target and exit; diagnostics on stderr
        --version                Print executable version
        Environment: POSTGRES_TARGETS or POSTGRES_TARGETS_FILE, or
          POSTGRES_CONNECTION_STRING + POSTGRES_DATABASES. Targets JSON wins over file.
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
if (options.Validate)
{
    await using var registry = new DatabaseRegistry(options);
    using var executor = new SqlExecutor(options, registry);
    bool ok = true;
    foreach (string target in registry.TargetNames)
    {
        var result = await ToolReply.Run(target, async () => await executor.QueryAsync(target, "SELECT current_database() AS database", limit: 1));
        Console.Error.WriteLine(result.StructuredContent?.GetRawText());
        ok &= result.IsError != true;
    }
    return ok ? 0 : 1;
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
