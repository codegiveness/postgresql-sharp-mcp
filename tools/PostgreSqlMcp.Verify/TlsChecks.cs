// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;

namespace PostgreSqlMcp.Verify;

/// <summary>
/// TLS defaults and opt-ins, against a disposable TLS-enabled PostgreSQL whose certificate is signed by a throwaway CA and names
/// the container's non-loopback bridge address. Proves that an omitted SSL Mode connects with Prefer (TLS when offered, plaintext
/// against a server without TLS) and is advised about for non-loopback hosts, that an explicit VerifyFull verifies the server
/// before any credential is sent, and that every explicit SSL Mode is used unchanged.
/// </summary>
internal static class TlsChecks
{
    private const string Failed = "tls_verification_failed";
    private const string VerifyFull = ";SSL Mode=VerifyFull";

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
            // A Root Certificate without SSL Mode opts into VerifyFull (Npgsql's Prefer would ignore the CA).
            ["bridge_root_only"] = bridge + root,
            ["bridge_verify"] = bridge + VerifyFull,
            ["bridge_ca"] = bridge + root + VerifyFull,
            ["bridge_require"] = bridge + ";SSL Mode=Require",
            ["loopback_default"] = loopback,
            ["loopback_disable"] = loopback + ";SSL Mode=Disable",
            // Trusted chain via the inferred VerifyFull, but the certificate does not name "localhost": only host name verification can reject it.
            ["name_mismatch"] = $"Host=localhost;Port={tls.LoopbackPort};{login}{root}",
            // A multi-host string is local only if every host is: only one with a remote host is advised about.
            ["multi_local"] = $"Host=127.0.0.1,localhost;Port={tls.LoopbackPort};{login}",
            ["multi_mixed"] = $"Host=127.0.0.1:{tls.LoopbackPort},{tls.BridgeAddress}:5432;{login}"
        };
        var served = new Dictionary<string, string>(environment) { ["POSTGRES_TARGETS"] = JsonSerializer.Serialize(targets) };
        await using (var client = await McpClient.StartAsync(command, served))
        {
            foreach (string alias in new[] { "bridge_verify", "name_mismatch" })
            {
                JsonNode failure = await client.FailsAsync("execute_sql", new { database = alias, sql = "SELECT 1" }, Failed);
                string message = failure["error"]!["message"].Text();
                Check.That(message.Contains($"profile {alias}", StringComparison.Ordinal) && message.Contains("Root Certificate", StringComparison.Ordinal)
                    && message.Contains("SSL Mode=Require", StringComparison.Ordinal), "The certificate failure did not name the profile and both remedies.");
                Check.Confidential(message, "reader-disposable");
            }
            // With VerifyFull the login must never reach a server whose certificate was not verified: the server saw no user at all.
            // Checked before any successful connection, because the Prefer profiles below do log in.
            Check.That(!(await tls.LogAsync()).Contains("mcp_reader", StringComparison.Ordinal),
                "A login was sent to the server before its certificate was verified.");
            var expected = new Dictionary<string, string>
            {
                ["bridge_default"] = "tls", ["bridge_root_only"] = "tls", ["bridge_ca"] = "tls", ["bridge_require"] = "tls",
                ["loopback_default"] = "tls", ["loopback_disable"] = "plaintext", ["multi_local"] = "tls"
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
            string[] warnings = startup.Split('\n').Where(line => line.StartsWith("[warning] profile", StringComparison.Ordinal)).ToArray();
            Check.That(warnings.Any(line => line.StartsWith("[warning] profile bridge_default: SSL Mode=Prefer", StringComparison.Ordinal)
                    && line.Contains("SSL Mode=VerifyFull", StringComparison.Ordinal)),
                "The Prefer default to a non-loopback host was not advised about with the VerifyFull remedy.");
            Check.That(warnings.Any(line => line.StartsWith("[warning] profile bridge_require: SSL Mode=Require", StringComparison.Ordinal))
                    && warnings.Any(line => line.StartsWith("[warning] profile multi_mixed: SSL Mode=Prefer", StringComparison.Ordinal)),
                "An unverified SSL Mode on a non-loopback host was not warned about.");
            Check.That(new[] { "bridge_root_only", "bridge_verify", "bridge_ca", "loopback", "name_mismatch", "multi_local" }.All(name => !warnings.Any(line => line.Contains(name, StringComparison.Ordinal))),
                "A verified or loopback-only profile was reported as unverified.");
            Check.Confidential(startup, "reader-disposable");
        }
        Console.WriteLine("PASS TLS defaults: unset SSL Mode uses Prefer with advice for non-loopback hosts, explicit VerifyFull verifies before sending credentials, explicit modes are honored");

        // --validate: the same failures and successes as operator-facing diagnostics, on stderr only.
        ProcessResult rejected = await Processes.RunAsync(command.With("--validate"), Single(environment, "bridge_verify", bridge + VerifyFull), expected: 1);
        Check.That(rejected.Output.Length == 0, "Certificate failure contaminated protocol stdout.");
        Check.That(rejected.Error.Contains(Failed, StringComparison.Ordinal) && rejected.Error.Contains("profile bridge_verify", StringComparison.Ordinal)
            && rejected.Error.Contains("SSL Mode=Require", StringComparison.Ordinal), "--validate did not report the certificate failure with its remedies.");
        Check.Confidential(rejected.Error, "reader-disposable");
        ProcessResult trusted = await Processes.RunAsync(command.With("--validate"), Single(environment, "bridge_ca", bridge + root + VerifyFull));
        Check.That(trusted.Output.Length == 0 && !trusted.Error.Contains("[warning]", StringComparison.Ordinal), "A verified connection was warned about or wrote to stdout.");
        ProcessResult defaulted = await Processes.RunAsync(command.With("--validate"), Single(environment, "bridge_default", bridge));
        Check.That(defaulted.Output.Length == 0 && defaulted.Error.Contains("[warning] profile bridge_default: SSL Mode=Prefer", StringComparison.Ordinal),
            "An omitted SSL Mode did not connect with the Prefer advice.");
        ProcessResult optedOut = await Processes.RunAsync(command.With("--validate"), Single(environment, "bridge_require", bridge + ";SSL Mode=Require"));
        Check.That(optedOut.Output.Length == 0 && optedOut.Error.Contains("[warning] profile bridge_require: SSL Mode=Require", StringComparison.Ordinal),
            "An explicit SSL Mode=Require did not connect with the unverified-TLS warning.");
        Check.Confidential(trusted.Error + defaulted.Error + optedOut.Error, "reader-disposable");
        // The base connection string gets the same default, and keeps an explicit SSL Mode unchanged.
        var baseString = new Dictionary<string, string>(environment) { ["POSTGRES_CONNECTION_STRING"] = bridge };
        baseString.Remove("POSTGRES_TARGETS");
        ProcessResult baseDefault = await Processes.RunAsync(command.With("--validate"), baseString);
        Check.That(baseDefault.Output.Length == 0 && baseDefault.Error.Contains("SSL Mode=Prefer", StringComparison.Ordinal),
            "POSTGRES_CONNECTION_STRING without SSL Mode did not connect with Prefer.");
        baseString["POSTGRES_CONNECTION_STRING"] = bridge + VerifyFull;
        ProcessResult baseVerified = await Processes.RunAsync(command.With("--validate"), baseString, expected: 1);
        Check.That(baseVerified.Output.Length == 0 && baseVerified.Error.Contains(Failed, StringComparison.Ordinal),
            "An explicit SSL Mode=VerifyFull in POSTGRES_CONNECTION_STRING was not used unchanged.");
        Console.WriteLine("PASS --validate connects with the Prefer default, reports certificate failures for explicit VerifyFull on stderr with remedies and honors explicit SSL Mode");

        // A server without TLS: the default connects in plaintext; an explicit verifying mode never falls back.
        await tls.DisableServerTlsAsync();
        ProcessResult plaintextDefault = await Processes.RunAsync(command.With("--validate"), Single(environment, "bridge_default", bridge));
        Check.That(plaintextDefault.Output.Length == 0 && plaintextDefault.Error.Contains("[warning] profile bridge_default: SSL Mode=Prefer", StringComparison.Ordinal),
            "A server without TLS was not reached by default.");
        ProcessResult plaintextRefused = await Processes.RunAsync(command.With("--validate"), Single(environment, "bridge_verify", bridge + VerifyFull), expected: 1);
        Check.That(plaintextRefused.Output.Length == 0 && plaintextRefused.Error.Contains("tls_unavailable", StringComparison.Ordinal)
            && plaintextRefused.Error.Contains("SSL Mode=Disable", StringComparison.Ordinal), "An explicit VerifyFull to a non-TLS server was not refused with the TLS-unavailable error.");
        ProcessResult plaintext = await Processes.RunAsync(command.With("--validate"), Single(environment, "bridge_disable", bridge + ";SSL Mode=Disable"));
        Check.That(plaintext.Output.Length == 0 && plaintext.Error.Contains("[warning] profile bridge_disable: SSL Mode=Disable", StringComparison.Ordinal),
            "An explicit SSL Mode=Disable did not connect with the unencrypted-connection warning.");
        Check.Confidential(plaintextDefault.Error + plaintextRefused.Error + plaintext.Error, "reader-disposable");
        Console.WriteLine("PASS non-TLS server: reached by default, explicit VerifyFull refused without plaintext fallback, explicit SSL Mode=Disable honored");
    }

    private static Dictionary<string, string> Single(Dictionary<string, string> environment, string alias, string connection) =>
        new(environment) { ["POSTGRES_TARGETS"] = JsonSerializer.Serialize(new Dictionary<string, string> { [alias] = connection }) };
}
