// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;

namespace PostgreSqlMcp.Verify;

/// <summary>
/// Verified TLS by default, against a disposable TLS-enabled PostgreSQL whose certificate is signed by a throwaway CA and names
/// the container's non-loopback bridge address. Proves that an omitted SSL Mode verifies the server before any credential is
/// sent, that the operator's explicit choices are honored, and that loopback uses no unverified TLS.
/// </summary>
internal static class TlsChecks
{
    private const string Failed = "tls_verification_failed";

    /// <param name="login">The fixture's disposable reader login (user, password, database), without host or port.</param>
    public static async Task RunAsync(Command command, Dictionary<string, string> environment, string login)
    {
        await using var tls = new TlsFixture();
        await tls.StartAsync();
        string bridge = $"Host={tls.BridgeAddress};Port=5432;{login}";
        string loopback = $"Host=127.0.0.1;Port={tls.LoopbackPort};{login}";
        string root = $";Root Certificate={tls.CaFile}";
        var targets = new Dictionary<string, string>
        {
            ["bridge_default"] = bridge,
            ["bridge_ca"] = bridge + root,
            ["bridge_require"] = bridge + ";SSL Mode=Require",
            ["loopback_default"] = loopback,
            ["loopback_ca"] = loopback + root,
            ["loopback_require"] = loopback + ";SSL Mode=Require",
            // Trusted chain, but the certificate does not name "localhost": only host name verification can reject it.
            ["name_mismatch"] = $"Host=localhost;Port={tls.LoopbackPort};{login}{root}",
            // A multi-host string is local only if every host is: all-loopback uses no TLS, one remote host makes it verified.
            ["multi_local"] = $"Host=127.0.0.1,localhost;Port={tls.LoopbackPort};{login}",
            ["multi_mixed"] = $"Host=127.0.0.1:{tls.LoopbackPort},{tls.BridgeAddress}:5432;{login}"
        };
        var served = new Dictionary<string, string>(environment) { ["POSTGRES_TARGETS"] = JsonSerializer.Serialize(targets) };
        await using (var client = await McpClient.StartAsync(command, served))
        {
            foreach (string alias in new[] { "bridge_default", "name_mismatch" })
            {
                JsonNode failure = await client.FailsAsync("execute_sql", new { database = alias, sql = "SELECT 1" }, Failed);
                string message = failure["error"]!["message"].Text();
                Check.That(message.Contains($"profile {alias}", StringComparison.Ordinal) && message.Contains("Root Certificate", StringComparison.Ordinal)
                    && message.Contains("SSL Mode=Require", StringComparison.Ordinal), "The certificate failure did not name the profile and both remedies.");
                Check.Confidential(message, "reader-disposable");
            }
            await client.FailsAsync("execute_sql", new { database = "multi_mixed", sql = "SELECT 1" });
            // The login must never reach a server whose certificate was not verified: the server saw no user at all.
            Check.That(!(await tls.LogAsync()).Contains("mcp_reader", StringComparison.Ordinal),
                "A login was sent to the server before its certificate was verified.");
            var expected = new Dictionary<string, string>
            {
                ["bridge_ca"] = "tls", ["bridge_require"] = "tls", ["loopback_default"] = "plaintext", ["loopback_ca"] = "tls", ["loopback_require"] = "tls",
                ["multi_local"] = "plaintext"
            };
            foreach (var (alias, transport) in expected)
            {
                JsonNode page = await client.OkAsync("execute_sql", new
                {
                    database = alias,
                    sql = "SELECT CASE WHEN ssl THEN 'tls' ELSE 'plaintext' END AS transport FROM pg_stat_ssl WHERE pid = pg_backend_pid()"
                });
                Check.That(Check.Rows(page).Single()["transport"].Text() == transport, $"Profile {alias} did not use {transport}.");
            }
            await client.StopAsync();
            string startup = client.StandardError;
            Check.That(startup.Contains("[warning] profile bridge_require: SSL Mode=Require", StringComparison.Ordinal),
                "An explicit unverified SSL Mode on a non-loopback host was not warned about.");
            Check.That(new[] { "bridge_default", "bridge_ca", "loopback", "name_mismatch" }.All(name => !startup.Contains($"profile {name}", StringComparison.Ordinal)),
                "A verified or loopback profile was reported as unverified.");
            Check.Confidential(startup, "reader-disposable");
        }
        Console.WriteLine("PASS verified TLS by default: unset SSL Mode verifies non-loopback hosts before sending credentials, explicit modes are honored, loopback uses no TLS");

        // --validate: the same failures and successes as operator-facing diagnostics, on stderr only.
        ProcessResult rejected = await Processes.RunAsync(command.With("--validate"), Single(environment, "bridge_default", bridge), expected: 1);
        Check.That(rejected.Output.Length == 0, "Certificate failure contaminated protocol stdout.");
        Check.That(rejected.Error.Contains(Failed, StringComparison.Ordinal) && rejected.Error.Contains("profile bridge_default", StringComparison.Ordinal)
            && rejected.Error.Contains("SSL Mode=Require", StringComparison.Ordinal), "--validate did not report the certificate failure with its remedies.");
        Check.Confidential(rejected.Error, "reader-disposable");
        ProcessResult trusted = await Processes.RunAsync(command.With("--validate"), Single(environment, "bridge_ca", bridge + root));
        Check.That(trusted.Output.Length == 0 && !trusted.Error.Contains("[warning]", StringComparison.Ordinal), "A verified connection was warned about or wrote to stdout.");
        ProcessResult optedOut = await Processes.RunAsync(command.With("--validate"), Single(environment, "bridge_require", bridge + ";SSL Mode=Require"));
        Check.That(optedOut.Output.Length == 0 && optedOut.Error.Contains("[warning] profile bridge_require: SSL Mode=Require", StringComparison.Ordinal),
            "An explicit SSL Mode=Require did not connect with the unverified-TLS warning.");
        Check.Confidential(trusted.Error + optedOut.Error, "reader-disposable");
        // Default for the base connection string, not only for targets JSON.
        var baseString = new Dictionary<string, string>(environment) { ["POSTGRES_CONNECTION_STRING"] = bridge };
        baseString.Remove("POSTGRES_TARGETS");
        ProcessResult baseRejected = await Processes.RunAsync(command.With("--validate"), baseString, expected: 1);
        Check.That(baseRejected.Output.Length == 0 && baseRejected.Error.Contains(Failed, StringComparison.Ordinal),
            "POSTGRES_CONNECTION_STRING did not default to verified TLS.");
        Console.WriteLine("PASS --validate reports certificate failures on stderr with remedies and honors explicit SSL Mode");

        // A server without TLS: the default must not silently fall back to plaintext, and Disable is the explicit opt-out.
        await tls.DisableServerTlsAsync();
        ProcessResult plaintextRefused = await Processes.RunAsync(command.With("--validate"), Single(environment, "bridge_default", bridge), expected: 1);
        Check.That(plaintextRefused.Output.Length == 0 && plaintextRefused.Error.Contains("tls_unavailable", StringComparison.Ordinal)
            && plaintextRefused.Error.Contains("SSL Mode=Disable", StringComparison.Ordinal), "A non-TLS server was not refused with the TLS-unavailable error.");
        ProcessResult plaintext = await Processes.RunAsync(command.With("--validate"), Single(environment, "bridge_disable", bridge + ";SSL Mode=Disable"));
        Check.That(plaintext.Output.Length == 0 && plaintext.Error.Contains("[warning] profile bridge_disable: SSL Mode=Disable", StringComparison.Ordinal),
            "An explicit SSL Mode=Disable did not connect with the unverified-TLS warning.");
        Check.Confidential(plaintextRefused.Error + plaintext.Error, "reader-disposable");
        Console.WriteLine("PASS non-TLS server: refused by default without plaintext fallback, explicit SSL Mode=Disable honored");
    }

    private static Dictionary<string, string> Single(Dictionary<string, string> environment, string alias, string connection) =>
        new(environment) { ["POSTGRES_TARGETS"] = JsonSerializer.Serialize(new Dictionary<string, string> { [alias] = connection }) };
}
