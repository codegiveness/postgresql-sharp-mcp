// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PostgreSqlMcp.Core;

namespace PostgreSqlMcp.Tools.Tests;

/// <summary>
/// What an MCP client is told about each tool must match what the server enforces. Tools are built from the real registration
/// code; they are only described here, never invoked, so no database or service graph is involved.
/// </summary>
public sealed class ToolRegistrationTests
{
    private const string Connection = "Host=db.example.test;Database=app;Username=reader;Password=synthetic-pw";

    private static readonly string[] ReadOnlyTools =
        ["list_databases", "list_schemas", "list_objects", "get_object_details", "analyze_db_health", "get_top_queries", "analyze_indexes", "explain_query"];

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static ServerOptions Options(bool unrestricted = true, string[]? lockedTo = null, bool strict = false) => new()
    {
        Targets = new ReadOnlyDictionary<string, string>(new Dictionary<string, string> { ["primary"] = Connection }),
        Unrestricted = unrestricted, AllowedDatabases = lockedTo, StrictDatabaseLock = strict
    };

    private static Dictionary<string, Tool> Describe(ServerOptions options)
    {
        var services = new ServiceCollection();
        services.AddPostgreSqlTools(options);
        return services.Where(descriptor => descriptor.ServiceType == typeof(McpServerTool))
            .Select(descriptor => ((McpServerTool)descriptor.ImplementationFactory!(new NoServices())).ProtocolTool)
            .ToDictionary(tool => tool.Name, StringComparer.Ordinal);
    }

    [Fact]
    public void All_nine_tools_are_registered_exactly_once()
    {
        var services = new ServiceCollection();
        services.AddPostgreSqlTools(Options());

        Assert.Equal(9, services.Count(descriptor => descriptor.ServiceType == typeof(McpServerTool)));
        string[] expected = [.. ReadOnlyTools, "execute_sql"];
        Assert.Equal(expected.Order(StringComparer.Ordinal), Describe(Options()).Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void In_unrestricted_mode_only_execute_sql_may_write_and_it_is_destructive_and_not_idempotent()
    {
        Dictionary<string, Tool> tools = Describe(Options(unrestricted: true));

        ToolAnnotations write = tools["execute_sql"].Annotations!;
        Assert.False(write.ReadOnlyHint);
        Assert.True(write.DestructiveHint);
        Assert.False(write.IdempotentHint);
        Assert.False(write.OpenWorldHint);
    }

    [Fact]
    public void In_restricted_mode_execute_sql_is_declared_read_only_and_not_destructive()
    {
        ToolAnnotations write = Describe(Options(unrestricted: false))["execute_sql"].Annotations!;

        Assert.True(write.ReadOnlyHint);
        Assert.False(write.DestructiveHint);
        Assert.False(write.IdempotentHint);
        Assert.False(write.OpenWorldHint);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_other_tool_is_read_only_idempotent_non_destructive_and_closed_world_in_both_modes(bool unrestricted)
    {
        Dictionary<string, Tool> tools = Describe(Options(unrestricted));

        foreach (string name in ReadOnlyTools)
        {
            ToolAnnotations annotations = tools[name].Annotations!;
            Assert.True(annotations.ReadOnlyHint, name);
            Assert.False(annotations.DestructiveHint, name);
            Assert.True(annotations.IdempotentHint, name);
            Assert.False(annotations.OpenWorldHint, name);
        }
    }

    [Fact]
    public void Execute_sql_description_states_the_access_mode()
    {
        string unrestricted = Describe(Options(unrestricted: true))["execute_sql"].Description!;
        string restricted = Describe(Options(unrestricted: false))["execute_sql"].Description!;

        Assert.Contains("this server allows writes, so read_only=false commits DML/DDL in one transaction", unrestricted, StringComparison.Ordinal);
        Assert.DoesNotContain("restricted access mode", unrestricted, StringComparison.Ordinal);
        Assert.Contains("This server is in restricted access mode", restricted, StringComparison.Ordinal);
        Assert.DoesNotContain("allows writes", restricted, StringComparison.Ordinal);
        Assert.StartsWith("Run one SQL statement", restricted, StringComparison.Ordinal);
    }

    [Fact]
    public void A_database_lock_is_added_to_the_execute_sql_description_with_its_mode()
    {
        string none = Describe(Options())["execute_sql"].Description!;
        string advisory = Describe(Options(lockedTo: ["app"]))["execute_sql"].Description!;
        string strict = Describe(Options(lockedTo: ["app"], strict: true))["execute_sql"].Description!;

        Assert.DoesNotContain("Database lock", none, StringComparison.Ordinal);
        Assert.Contains("Database lock: only databases returned by list_databases can be selected.", advisory, StringComparison.Ordinal);
        Assert.Contains("Lock mode advisory", advisory, StringComparison.Ordinal);
        Assert.Contains("Lock mode strict", strict, StringComparison.Ordinal);
        Assert.DoesNotContain("Lock mode advisory", strict, StringComparison.Ordinal);
    }

    [Fact]
    public void List_databases_describes_the_lock_only_when_one_is_configured()
    {
        string open = Describe(Options())["list_databases"].Description!;
        string locked = Describe(Options(lockedTo: ["app"]))["list_databases"].Description!;

        Assert.StartsWith("Discover accessible databases from the live PostgreSQL catalog", open, StringComparison.Ordinal);
        Assert.DoesNotContain("lock", open, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("List the databases allowed by this server's database lock", locked, StringComparison.Ordinal);
        Assert.EndsWith("Lock mode advisory: PostgreSQL grants were not required to confine the login, so SQL may still reach other databases where the login's privileges allow.", locked, StringComparison.Ordinal);
    }

    [Fact]
    public void Tools_without_a_mode_specific_description_are_unchanged_by_the_configuration()
    {
        Dictionary<string, Tool> plain = Describe(Options());
        Dictionary<string, Tool> locked = Describe(Options(unrestricted: false, lockedTo: ["app"], strict: true));

        foreach (string name in ReadOnlyTools.Where(name => name != "list_databases"))
            Assert.Equal(plain[name].Description, locked[name].Description);
    }

    private static string[] Required(Tool tool) =>
        tool.InputSchema.TryGetProperty("required", out JsonElement required) ? [.. required.EnumerateArray().Select(item => item.GetString()!)] : [];

    private static string[] Parameters(Tool tool) => [.. tool.InputSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name)];

    [Fact]
    public void Execute_sql_takes_database_and_sql_and_the_documented_options()
    {
        Tool tool = Describe(Options())["execute_sql"];

        Assert.Equal(["database", "sql"], Required(tool).Order(StringComparer.Ordinal));
        Assert.Equal(["database", "limit", "offset", "parameters", "read_only", "sql", "target"], Parameters(tool).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("ct", Parameters(tool));
    }

    [Theory]
    [InlineData("list_databases", new string[0])]
    [InlineData("list_schemas", new[] { "database" })]
    [InlineData("list_objects", new[] { "database" })]
    [InlineData("get_object_details", new[] { "database", "name", "schema" })]
    [InlineData("analyze_db_health", new[] { "database" })]
    [InlineData("get_top_queries", new[] { "database" })]
    [InlineData("analyze_indexes", new[] { "database" })]
    [InlineData("explain_query", new[] { "database", "sql" })]
    public void Required_arguments_per_tool(string name, string[] required) =>
        Assert.Equal(required, Required(Describe(Options())[name]).Order(StringComparer.Ordinal));

    [Fact]
    public void Every_tool_and_parameter_has_a_description()
    {
        foreach ((string name, Tool tool) in Describe(Options()))
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), name);
            foreach (JsonProperty property in tool.InputSchema.GetProperty("properties").EnumerateObject())
                Assert.True(property.Value.TryGetProperty("description", out JsonElement description) && !string.IsNullOrWhiteSpace(description.GetString()), $"{name}.{property.Name}");
        }
    }

    [Fact]
    public void Instructions_state_the_access_mode()
    {
        string unrestricted = ToolRegistration.Instructions(Options(unrestricted: true));
        string restricted = ToolRegistration.Instructions(Options(unrestricted: false));

        Assert.Contains("Access mode: unrestricted. SQL runs in a READ ONLY transaction unless execute_sql sets read_only=false, which commits the statement.", unrestricted, StringComparison.Ordinal);
        Assert.Contains("Access mode: restricted. All SQL runs in READ ONLY transactions; writes are refused.", restricted, StringComparison.Ordinal);
        Assert.DoesNotContain("unrestricted", restricted, StringComparison.Ordinal);
    }

    [Fact]
    public void Instructions_state_the_database_lock_and_never_overstate_advisory_mode()
    {
        string none = ToolRegistration.Instructions(Options());
        string advisory = ToolRegistration.Instructions(Options(lockedTo: ["app"]));
        string strict = ToolRegistration.Instructions(Options(lockedTo: ["app"], strict: true));

        Assert.Contains("No database lock: any database the login can CONNECT to may be selected.", none, StringComparison.Ordinal);
        Assert.Contains("Database lock: only the databases returned by list_databases can be selected; others are rejected.", advisory, StringComparison.Ordinal);
        Assert.Contains("Lock mode advisory: PostgreSQL grants were not required to confine the login", advisory, StringComparison.Ordinal);
        Assert.DoesNotContain("were verified to confine", advisory, StringComparison.Ordinal);
        Assert.Contains("Lock mode strict: at startup, PostgreSQL grants were verified to confine the login to these databases.", strict, StringComparison.Ordinal);
        Assert.DoesNotContain("No database lock", strict, StringComparison.Ordinal);
    }

    [Fact]
    public void Instructions_report_the_configured_bounds()
    {
        var options = new ServerOptions
        {
            Targets = new ReadOnlyDictionary<string, string>(new Dictionary<string, string> { ["primary"] = Connection }),
            MaxRows = 250, MaxResultBytes = 8192, QueryTimeout = 12
        };

        Assert.Contains("Results are bounded to 250 rows and 8192 bytes per call; statements time out after 12s.", ToolRegistration.Instructions(options), StringComparison.Ordinal);
    }

    [Fact]
    public void Instructions_end_with_the_privilege_notice_and_never_contain_connection_details()
    {
        string text = ToolRegistration.Instructions(Options(lockedTo: ["app"]));

        Assert.EndsWith("PostgreSQL privileges and row-level security decide what is visible and allowed.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-pw", text, StringComparison.Ordinal);
        Assert.DoesNotContain("db.example.test", text, StringComparison.Ordinal);
    }
}
