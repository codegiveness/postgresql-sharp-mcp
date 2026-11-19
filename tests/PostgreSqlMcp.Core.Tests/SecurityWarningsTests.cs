// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;

namespace PostgreSqlMcp.Core.Tests;

public sealed class SecurityWarningsTests
{
    private static ServerOptions Options(params (string Alias, string Connection)[] targets) => new()
    {
        Targets = new ReadOnlyDictionary<string, string>(targets.ToDictionary(pair => pair.Alias, pair => pair.Connection, StringComparer.Ordinal))
    };

    [Theory]
    [InlineData("Prefer", "encrypts only if the server offers TLS and never verifies the server certificate")]
    [InlineData("Allow", "encrypts only if the server requires TLS and never verifies the server certificate")]
    [InlineData("Require", "encrypts without verifying the server certificate")]
    [InlineData("Disable", "does not encrypt the connection")]
    public void Unverified_tls_to_a_remote_host_is_warned_about_with_the_effective_mode(string mode, string effect)
    {
        string warning = Assert.Single(SecurityWarnings.Tls(Options(("reporting", $"Host=db.example.test;Database=app;SSL Mode={mode}"))));

        Assert.StartsWith($"profile reporting: SSL Mode={mode} to a non-local host {effect}, ", warning, StringComparison.Ordinal);
        Assert.Contains("SSL Mode=VerifyFull", warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Host=localhost;Database=app;SSL Mode=Disable")]
    [InlineData("Host=127.0.0.1;Database=app")]
    [InlineData("Host=/var/run/postgresql;Database=app;SSL Mode=Disable")]
    [InlineData("Host=db.example.test;Database=app;SSL Mode=VerifyFull")]
    [InlineData("Host=db.example.test;Database=app;SSL Mode=VerifyCA;Root Certificate=/etc/ssl/synthetic-ca.pem")]
    public void Local_hosts_and_verified_connections_are_not_warned_about(string connection) =>
        Assert.Empty(SecurityWarnings.Tls(Options(("p", connection))));

    [Fact]
    public void A_missing_ssl_mode_is_reported_as_the_npgsql_default()
    {
        string warning = Assert.Single(SecurityWarnings.Tls(Options(("p", "Host=db.example.test;Database=app"))));

        Assert.Contains("SSL Mode=Prefer", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Aliases_of_one_login_share_a_single_line_in_ordinal_order()
    {
        const string connection = "Host=db.example.test;Database=app;Username=reader;SSL Mode=Require";

        string warning = Assert.Single(SecurityWarnings.Tls(Options(("beta", connection), ("alpha", connection), ("gamma", connection))));

        Assert.StartsWith("profiles alpha, beta, gamma: SSL Mode=Require", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void More_than_five_aliases_are_summarised()
    {
        (string, string)[] targets = [.. Enumerable.Range(1, 8).Select(i => ($"p{i}", "Host=db.example.test;Database=app;Username=reader;SSL Mode=Require"))];

        string warning = Assert.Single(SecurityWarnings.Tls(Options(targets)));

        Assert.StartsWith("profiles p1, p2, p3, p4, p5 and 3 more: ", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Different_ports_users_or_modes_are_separate_lines()
    {
        IReadOnlyList<string> warnings = SecurityWarnings.Tls(Options(
            ("a", "Host=db.example.test;Database=x;Username=u;SSL Mode=Require"),
            ("b", "Host=db.example.test;Database=x;Username=u;Port=5433;SSL Mode=Require"),
            ("c", "Host=db.example.test;Database=x;Username=other;SSL Mode=Require"),
            ("d", "Host=db.example.test;Database=x;Username=u;SSL Mode=Disable")));

        Assert.Equal(4, warnings.Count);
    }

    [Fact]
    public void Warnings_name_profiles_but_never_credentials_or_hosts()
    {
        string warning = Assert.Single(SecurityWarnings.Tls(Options(("p", "Host=secret-host.example.test;Database=app;Username=synthetic-user;Password=synthetic-secret-value"))));

        Assert.DoesNotContain("synthetic-secret-value", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-user", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-host", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void One_remote_entry_in_a_multi_host_value_is_enough_to_warn()
    {
        Assert.Single(SecurityWarnings.Tls(Options(("p", "Host=localhost,db.example.test;Database=app;SSL Mode=Require"))));
        Assert.Empty(SecurityWarnings.Tls(Options(("p", "Host=localhost,127.0.0.1;Database=app;SSL Mode=Require"))));
    }
}
