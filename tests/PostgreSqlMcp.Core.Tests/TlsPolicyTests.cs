// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Security.Authentication;
using Npgsql;

namespace PostgreSqlMcp.Core.Tests;

public sealed class TlsPolicyTests
{
    private const string TlsRefusedByServer = "SSL connection requested. No SSL enabled connection from this host is configured.";

    private static SslMode Applied(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        TlsPolicy.ApplyDefault(builder);
        return builder.SslMode;
    }

    [Fact]
    public void Without_ssl_mode_the_default_is_prefer() =>
        Assert.Equal(SslMode.Prefer, Applied("Host=db.example.test;Database=app"));

    [Fact]
    public void A_root_certificate_without_ssl_mode_selects_verify_full() =>
        Assert.Equal(SslMode.VerifyFull, Applied("Host=db.example.test;Database=app;Root Certificate=/etc/ssl/synthetic-ca.pem"));

    [Fact]
    public void An_empty_root_certificate_does_not_count_as_a_ca() =>
        Assert.Equal(SslMode.Prefer, Applied("Host=db.example.test;Database=app;Root Certificate="));

    [Theory]
    [InlineData("SSL Mode=Disable", SslMode.Disable)]
    [InlineData("SSL Mode=Allow", SslMode.Allow)]
    [InlineData("SSL Mode=Prefer", SslMode.Prefer)]
    [InlineData("SSL Mode=Require", SslMode.Require)]
    [InlineData("SSL Mode=VerifyCA", SslMode.VerifyCA)]
    [InlineData("SSL Mode=VerifyFull", SslMode.VerifyFull)]
    [InlineData("SslMode=Require", SslMode.Require)]
    [InlineData("sslmode=disable", SslMode.Disable)]
    public void An_explicit_ssl_mode_is_kept_whatever_its_spelling(string setting, SslMode expected) =>
        Assert.Equal(expected, Applied($"Host=db.example.test;Database=app;{setting}"));

    [Fact]
    public void An_explicit_ssl_mode_wins_over_a_root_certificate() =>
        Assert.Equal(SslMode.Require, Applied("Host=db.example.test;Database=app;SSL Mode=Require;Root Certificate=/etc/ssl/synthetic-ca.pem"));

    [Fact]
    public void The_default_is_written_into_the_connection_string()
    {
        var builder = new NpgsqlConnectionStringBuilder("Host=db.example.test;Database=app");

        TlsPolicy.ApplyDefault(builder);

        Assert.Equal(SslMode.Prefer, new NpgsqlConnectionStringBuilder(builder.ConnectionString).SslMode);
        Assert.Contains("SSL Mode=Prefer", builder.ConnectionString, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("LocalHost")]
    [InlineData("127.0.0.1")]
    [InlineData("127.8.9.10")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    [InlineData("[::1]:5432")]
    [InlineData("localhost:5433")]
    [InlineData("127.0.0.1:5432")]
    [InlineData("/var/run/postgresql")]
    [InlineData("@abstract-socket")]
    [InlineData("localhost,127.0.0.1,/tmp")]
    [InlineData(" localhost , /tmp ")]
    public void Loopback_and_socket_hosts_are_local(string? host) => Assert.True(TlsPolicy.IsLocalHost(host));

    [Theory]
    [InlineData("db.example.test")]
    [InlineData("db.example.test:5432")]
    [InlineData("10.0.0.5")]
    [InlineData("0.0.0.0")]
    [InlineData("localhost.example.test")]
    [InlineData("127.0.0.1.example.test")]
    [InlineData("[2001:db8::1]")]
    [InlineData("[2001:db8::1]:5432")]
    [InlineData("2001:db8::1")]
    [InlineData("localhost,db.example.test")]
    [InlineData("db.example.test,/tmp")]
    public void Any_non_local_entry_makes_the_whole_host_value_remote(string host) => Assert.False(TlsPolicy.IsLocalHost(host));

    [Theory]
    [InlineData(SslMode.VerifyFull)]
    [InlineData(SslMode.VerifyCA)]
    public void Verification_failure_during_the_handshake_is_mapped_to_a_fixed_actionable_error(SslMode mode)
    {
        var failure = new NpgsqlException("provider text with Password=synthetic-secret-value", new AuthenticationException("untrusted root"));

        ToolException? error = TlsPolicy.Explain(failure, "reporting", $"Host=h;Database=d;Password=synthetic-secret-value;SSL Mode={mode}");

        Assert.NotNull(error);
        Assert.Equal("tls_verification_failed", error.Code);
        Assert.Contains("profile reporting", error.Message, StringComparison.Ordinal);
        Assert.Contains("Root Certificate=<CA file>", error.Message, StringComparison.Ordinal);
        Assert.Contains("SSL Mode=Require", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-secret-value", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("untrusted root", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SslMode.Require)]
    [InlineData(SslMode.VerifyCA)]
    [InlineData(SslMode.VerifyFull)]
    public void A_server_without_tls_is_reported_when_the_mode_demands_tls(SslMode mode)
    {
        var failure = new NpgsqlException(TlsRefusedByServer);

        ToolException? error = TlsPolicy.Explain(failure, "reporting", $"Host=h;Database=d;Password=synthetic-secret-value;SSL Mode={mode}");

        Assert.NotNull(error);
        Assert.Equal("tls_unavailable", error.Code);
        Assert.Contains($"SSL Mode={mode} requires it", error.Message, StringComparison.Ordinal);
        Assert.Contains("SSL Mode=Disable", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-secret-value", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SSL Mode=Prefer", true)]
    [InlineData("SSL Mode=Allow", true)]
    [InlineData("SSL Mode=Disable", true)]
    [InlineData("SSL Mode=Require", false)]
    [InlineData("SSL Mode=VerifyFull", false)]
    public void Other_failures_are_left_for_the_generic_error_path(string setting, bool authenticationFailure)
    {
        NpgsqlException failure = authenticationFailure
            ? new("other", new AuthenticationException("untrusted root"))
            : new("something else entirely", new IOException("reset"));

        Assert.Null(TlsPolicy.Explain(failure, "p", $"Host=h;Database=d;{setting}"));
    }

    [Theory]
    [InlineData("SSL Mode=Disable")]
    [InlineData("SSL Mode=Prefer")]
    [InlineData("SSL Mode=Allow")]
    public void The_no_tls_message_is_only_for_modes_that_require_tls(string setting) =>
        Assert.Null(TlsPolicy.Explain(new NpgsqlException(TlsRefusedByServer), "p", $"Host=h;Database=d;{setting}"));

    [Fact]
    public void The_no_tls_message_requires_the_exact_provider_prefix_and_no_inner_exception()
    {
        const string connectionString = "Host=h;Database=d;SSL Mode=Require";

        Assert.Null(TlsPolicy.Explain(new NpgsqlException("connection refused"), "p", connectionString));
        Assert.Null(TlsPolicy.Explain(new NpgsqlException(TlsRefusedByServer, new IOException("x")), "p", connectionString));
    }
}
