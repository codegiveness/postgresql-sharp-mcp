// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using Npgsql;

namespace PostgreSqlMcp.Core.Tests;

/// <summary>Target and database-lock resolution; it never opens a connection, so no server is needed.</summary>
public sealed class DatabaseRegistryTests
{
    private const string Reader = "Host=db.example.test;Username=reader;Password=synthetic-pw";

    private static ServerOptions Options(Dictionary<string, string> targets, string[]? allowed = null, int poolSize = 8) => new()
    {
        Targets = new ReadOnlyDictionary<string, string>(targets),
        AllowedDatabases = allowed,
        PoolSize = poolSize
    };

    private static ToolException InvalidTarget(Action action)
    {
        ToolException ex = Assert.Throws<ToolException>(action);
        Assert.Equal("invalid_target", ex.Code);
        return ex;
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData("a\0b", false)]
    public void Database_names_are_nonempty_without_nul(string name, bool valid) => Assert.Equal(valid, DatabaseRegistry.IsDatabaseName(name));

    [Fact]
    public void Database_name_limit_is_63_utf8_bytes()
    {
        Assert.True(DatabaseRegistry.IsDatabaseName(new string('a', 63)));
        Assert.False(DatabaseRegistry.IsDatabaseName(new string('a', 64)));
        Assert.True(DatabaseRegistry.IsDatabaseName(new string('é', 31) + "a"));
        Assert.False(DatabaseRegistry.IsDatabaseName(new string('é', 32)));
    }

    [Fact]
    public async Task Target_names_are_ordinal_sorted_and_primary_is_the_default()
    {
        await using var registry = new DatabaseRegistry(Options(new() { ["zeta"] = Reader + ";Database=z", ["primary"] = Reader + ";Database=p", ["Alpha"] = Reader + ";Database=a" }));

        Assert.Equal(["Alpha", "primary", "zeta"], registry.TargetNames);
        Assert.Equal("primary", registry.DefaultTarget);
    }

    [Fact]
    public async Task Without_a_primary_target_the_first_ordinal_name_is_the_default()
    {
        await using var registry = new DatabaseRegistry(Options(new() { ["b"] = Reader + ";Database=b", ["a"] = Reader + ";Database=a" }));

        Assert.Equal("a", registry.DefaultTarget);
    }

    [Fact]
    public async Task An_alias_resolves_to_its_own_profile_and_bootstrap_database()
    {
        await using var registry = new DatabaseRegistry(Options(new() { ["reporting"] = Reader + ";Database=report_db", ["primary"] = Reader + ";Database=main_db" }));

        DatabaseRegistry.DatabaseSelection selection = registry.Resolve("reporting", null, out string profile);

        Assert.Equal("reporting", profile);
        Assert.Equal("report_db", selection.Database);
    }

    [Fact]
    public async Task A_database_name_that_is_not_an_alias_uses_the_default_profile()
    {
        await using var registry = new DatabaseRegistry(Options(new() { ["primary"] = Reader + ";Database=main_db" }));

        DatabaseRegistry.DatabaseSelection selection = registry.Resolve("other_db", null, out string profile);

        Assert.Equal("primary", profile);
        Assert.Equal("other_db", selection.Database);
    }

    [Fact]
    public async Task An_explicit_target_selects_the_profile_and_keeps_the_requested_database()
    {
        await using var registry = new DatabaseRegistry(Options(new() { ["primary"] = Reader + ";Database=a", ["secondary"] = Reader + ";Username=other;Database=b" }));

        DatabaseRegistry.DatabaseSelection selection = registry.Resolve("a", "secondary", out string profile);

        Assert.Equal("secondary", profile);
        Assert.Equal("a", selection.Database);
        Assert.Equal("other", new NpgsqlConnectionStringBuilder(selection.ConnectionString).Username);
    }

    [Fact]
    public async Task An_unknown_target_is_rejected_without_naming_it()
    {
        await using var registry = new DatabaseRegistry(Options(new() { ["primary"] = Reader + ";Database=a" }));

        ToolException ex = InvalidTarget(() => registry.Resolve("a", "nonexistent-synthetic", out _));

        Assert.DoesNotContain("nonexistent-synthetic", ex.Message, StringComparison.Ordinal);
        InvalidTarget(() => registry.GetDiscoveryDatabase("nonexistent-synthetic"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a\0b")]
    public async Task Empty_or_malformed_database_names_are_rejected(string database)
    {
        await using var registry = new DatabaseRegistry(Options(new() { ["primary"] = Reader + ";Database=a" }));

        InvalidTarget(() => registry.Resolve(database, null, out _));
    }

    [Fact]
    public async Task A_database_outside_the_lock_is_rejected_but_profile_aliases_inside_it_resolve()
    {
        await using var registry = new DatabaseRegistry(Options(
            new() { ["app"] = Reader + ";Database=app", ["audit"] = Reader + ";Database=audit" }, allowed: ["app", "audit"]));

        Assert.Equal("audit", registry.Resolve("audit", null, out _).Database);
        ToolException ex = InvalidTarget(() => registry.Resolve("postgres", null, out _));
        Assert.Contains("outside this server's configured database lock", ex.Message, StringComparison.Ordinal);
        InvalidTarget(() => registry.Resolve("postgres", "app", out _));
    }

    [Fact]
    public async Task An_alias_whose_database_is_outside_the_lock_is_rejected()
    {
        await using var registry = new DatabaseRegistry(Options(
            new() { ["bootstrap"] = Reader + ";Database=postgres" }, allowed: ["app"]));

        InvalidTarget(() => registry.Resolve("bootstrap", null, out _));
    }

    [Fact]
    public async Task Discovery_uses_the_bootstrap_database_or_the_first_locked_database_when_it_is_outside_the_lock()
    {
        await using var registry = new DatabaseRegistry(Options(
            new() { ["inside"] = Reader + ";Database=second", ["outside"] = Reader + ";Database=postgres" }, allowed: ["first", "second"]));

        Assert.Equal("second", registry.GetDiscoveryDatabase("inside"));
        Assert.Equal("first", registry.GetDiscoveryDatabase("outside"));
        Assert.Equal(["first", "second"], registry.AllowedDatabaseNames);
    }

    [Fact]
    public async Task Without_a_lock_every_valid_database_is_allowed()
    {
        await using var registry = new DatabaseRegistry(Options(new() { ["primary"] = Reader + ";Database=a" }));

        Assert.Null(registry.AllowedDatabaseNames);
        Assert.True(registry.Allows("anything"));
        Assert.Equal("a", registry.GetDiscoveryDatabase("primary"));
    }

    [Fact]
    public async Task Profiles_force_the_server_owned_connection_settings()
    {
        await using var registry = new DatabaseRegistry(Options(
            new() { ["primary"] = Reader + ";Database=a;Application Name=caller;Include Error Detail=true;Log Parameters=true;Command Timeout=999;Maximum Pool Size=500" },
            poolSize: 3));

        var builder = new NpgsqlConnectionStringBuilder(registry.Resolve("a", null, out _).ConnectionString);

        Assert.Equal("postgresql-sharp-mcp", builder.ApplicationName);
        Assert.False(builder.IncludeErrorDetail);
        Assert.False(builder.LogParameters);
        Assert.Equal(3, builder.MaxPoolSize);
        Assert.Equal(30, builder.CommandTimeout);
        Assert.True(builder.Pooling);
        Assert.False(builder.Multiplexing);
        Assert.False(builder.NoResetOnClose);
        Assert.Equal("reader", builder.Username);
    }

    [Fact]
    public async Task Profile_identity_does_not_depend_on_keyword_order()
    {
        await using var registry = new DatabaseRegistry(Options(new()
        {
            ["a"] = "Host=h.example.test;Username=u;Database=x",
            ["b"] = "Username=u;Database=y;Host=h.example.test"
        }));

        Assert.Equal(registry.Resolve("x", "a", out _).ConnectionString, registry.Resolve("y", "b", out _).ConnectionString);
        Assert.Single(registry.LoginTargets());
    }
}
