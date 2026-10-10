using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PostgreSqlMcp.Core;

namespace PostgreSqlMcp.Tools;

/// <summary>
/// Registers the MCP tools with annotations, descriptions and server instructions derived from the enforced
/// configuration (access mode and database lock), so what a client is told matches what the server does.
/// </summary>
public static class ToolRegistration
{
    public static IServiceCollection AddPostgreSqlTools(this IServiceCollection services, ServerOptions options)
    {
        Add<DatabaseTools>(services, options);
        Add<SqlTools>(services, options);
        Add<PlanTools>(services, options);
        Add<OpsTools>(services, options);
        return services;
    }

    public static string Instructions(ServerOptions options) =>
        "PostgreSQL server tools. Discover with list_databases, then list_schemas, list_objects and get_object_details " +
        "before writing SQL; every database tool takes an explicit database. " +
        (options.Unrestricted
            ? "Access mode: unrestricted. SQL runs in a READ ONLY transaction unless execute_sql sets read_only=false, which commits the statement. "
            : "Access mode: restricted. All SQL runs in READ ONLY transactions; writes are refused. ") +
        (options.AllowedDatabases is null
            ? "No database lock: any database the login can CONNECT to may be selected. "
            : "Database lock: only the databases returned by list_databases can be selected; others are rejected. ") +
        $"Results are bounded to {options.MaxRows} rows and {options.MaxResultBytes} bytes per call; statements time out after {options.QueryTimeout}s. " +
        "PostgreSQL privileges and row-level security decide what is visible and allowed.";

    private static void Add<T>(IServiceCollection services, ServerOptions options) where T : class
    {
        foreach (var method in typeof(T).GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
            if (attribute is null) continue;
            bool execute = attribute.Name == "execute_sql";
            string? description = Describe(attribute.Name!, method.GetCustomAttribute<DescriptionAttribute>()?.Description, options);
            // Only execute_sql can write, and only in unrestricted mode; these options override attribute hints.
            services.AddSingleton(sp => McpServerTool.Create(method,
                r => ActivatorUtilities.CreateInstance(r.Services!, typeof(T)),
                new McpServerToolCreateOptions
                {
                    Services = sp, Description = description,
                    ReadOnly = !execute || !options.Unrestricted,
                    Destructive = execute && options.Unrestricted, Idempotent = !execute, OpenWorld = false
                }));
        }
    }

    private static string? Describe(string tool, string? description, ServerOptions options)
    {
        bool locked = options.AllowedDatabases is not null;
        return tool switch
        {
            "execute_sql" => description + (options.Unrestricted
                ? " Runs in a READ ONLY transaction that is rolled back unless read_only=false; this server allows writes, so read_only=false commits DML/DDL in one transaction. Never replay a truncated write."
                : " This server is in restricted access mode: every statement runs in a READ ONLY transaction and read_only=false is refused.")
                + (locked ? " Database lock: only databases returned by list_databases can be selected." : ""),
            "list_databases" when locked =>
                "List the databases allowed by this server's database lock that the login can CONNECT to, from the live catalog, with paging and is_current. Optional target selects a configured connection profile. Databases outside the lock are never listed and are rejected by every tool; allowing another requires an operator configuration change.",
            _ => description
        };
    }
}
