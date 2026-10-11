// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Net;
using System.Security.Authentication;
using Npgsql;

namespace PostgreSqlMcp.Core;

/// <summary>
/// Default TLS behavior of connection profiles. A profile that does not set <c>SSL Mode</c> gets <c>SSL Mode=Prefer</c>, so a
/// first run works against a server with or without TLS: TLS is used when the server offers it, without verifying the server
/// certificate, and plaintext otherwise. A <c>Root Certificate</c> without <c>SSL Mode</c> selects <c>VerifyFull</c>, because
/// supplying a CA only makes sense for verification. <see cref="SecurityWarnings.Tls"/> advises verification for non-local
/// hosts. An explicit <c>SSL Mode</c> is kept unchanged.
/// </summary>
internal static class TlsPolicy
{
    private const string SslModeKeyword = "SSL Mode";

    /// <summary>Sets <c>SSL Mode</c> on <paramref name="builder"/> unless the operator set it.</summary>
    public static void ApplyDefault(NpgsqlConnectionStringBuilder builder)
    {
        // ContainsKey reports every valid keyword; Keys lists only those present in the string, under canonical names.
        if (builder.Keys.Cast<string>().Contains(SslModeKeyword, StringComparer.Ordinal)) return;
        // Set explicitly rather than relying on Npgsql's default, so the effective mode is visible to warnings and pools.
        // Npgsql's Prefer ignores Root Certificate, so a CA alone would otherwise silently verify nothing.
        builder.SslMode = string.IsNullOrEmpty(builder.RootCertificate) ? SslMode.Prefer : SslMode.VerifyFull;
    }

    /// <summary>True when every entry of a (possibly multi-host) <c>Host</c> value is loopback or a Unix-domain socket.</summary>
    public static bool IsLocalHost(string? host) => (host ?? "").Split(',').All(IsLocalEntry);

    private static bool IsLocalEntry(string entry)
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

    /// <summary>
    /// Turns a TLS-specific connection failure into a fixed, actionable error, or null for any other failure. The text names only
    /// the profile: provider messages and the connection string can carry credentials and never reach the caller.
    /// </summary>
    public static ToolException? Explain(NpgsqlException ex, string profile, string connectionString)
    {
        SslMode mode = new NpgsqlConnectionStringBuilder(connectionString).SslMode;
        // With VerifyCA/VerifyFull, Npgsql rejects an untrusted or mismatched certificate during the TLS handshake.
        if (mode is SslMode.VerifyCA or SslMode.VerifyFull && ex.InnerException is AuthenticationException)
            return new("tls_verification_failed",
                $"TLS certificate verification failed for profile {profile}: the server certificate is not signed by a trusted CA or does not match the host name. " +
                "Set Root Certificate=<CA file> in the profile's connection string, or, to opt out explicitly, SSL Mode=Require (encrypted but unverified).");
        // The server answered the TLS request with 'N': it offers no TLS, and Npgsql does not fall back to plaintext in these modes.
        if (mode is SslMode.Require or SslMode.VerifyCA or SslMode.VerifyFull && ex.InnerException is null && ex.Message.StartsWith("SSL connection requested", StringComparison.Ordinal))
            return new("tls_unavailable",
                $"The server for profile {profile} does not offer TLS, but SSL Mode={mode} requires it. Enable TLS on the server, or, to opt out explicitly, set SSL Mode=Disable (no encryption).");
        return null;
    }
}
