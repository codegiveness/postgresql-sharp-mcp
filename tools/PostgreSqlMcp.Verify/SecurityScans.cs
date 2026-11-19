// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;

namespace PostgreSqlMcp.Verify;

/// <summary>Repository security gates that run pinned third-party scanners: workflow audit and secret-detector self-test.</summary>
internal static class SecurityScans
{
    private const string DetectorConfig = ".gitleaks.toml";

    /// <summary>
    /// Audits every workflow with actionlint (including ShellCheck for run scripts) and zizmor. zizmor runs offline with the
    /// "regular" persona at every severity: fork pull requests have no token for online audits, and the auditor/pedantic
    /// personas only add style findings (unnamed jobs, uncommented permissions) rather than exploitable patterns.
    /// Intentional patterns need a per-line <c>zizmor: ignore[rule]</c> comment with a reason; there is no blanket configuration.
    /// </summary>
    public static async Task AuditWorkflowsAsync(string root)
    {
        string directory = Path.Combine(root, ".github", "workflows");
        string[] workflows = [.. Directory.EnumerateFiles(directory)
            .Where(file => Path.GetExtension(file) is ".yml" or ".yaml")
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];
        Check.That(workflows.Length > 0, "No workflows found to audit.");

        using var tools = new TemporaryDirectory();
        string actionlint = await PinnedTools.InstallAsync(PinnedTools.Get("actionlint"), tools.Path);
        string shellcheck = await PinnedTools.InstallAsync(PinnedTools.Get("shellcheck"), tools.Path);
        string zizmor = await PinnedTools.InstallAsync(PinnedTools.Get("zizmor"), tools.Path);

        ProcessResult lint = await Processes.RunAsync(
            new Command(actionlint, ["-shellcheck", shellcheck, "-no-color", .. workflows]) { WorkingDirectory = root }, expected: null);
        ProcessResult audit = await Processes.RunAsync(
            new Command(zizmor, ["--offline", "--strict-collection", "--persona=regular", "--quiet", "--no-progress", "--color=never",
                "--render-links=never", ".github/workflows"])
            { WorkingDirectory = root }, expected: null);
        // Workflow findings quote only workflow source, which is public repository content.
        Console.WriteLine($"actionlint {PinnedTools.Get("actionlint").Version} (exit {lint.ExitCode}):");
        Console.WriteLine(lint.Output + lint.Error);
        Console.WriteLine($"zizmor {PinnedTools.Get("zizmor").Version} (exit {audit.ExitCode}):");
        Console.WriteLine(audit.Output + audit.Error);
        Check.That(lint.ExitCode == 0 && audit.ExitCode == 0,
            $"Workflow audit failed (actionlint exit {lint.ExitCode}, zizmor exit {audit.ExitCode}).");
        Console.WriteLine($"Workflow audit passed for {workflows.Length} workflow(s).");
    }

    /// <summary>
    /// Proves the custom rules in .gitleaks.toml still fire and that their path-scoped allowlist stays exact. Fixtures live only in
    /// a temporary directory and are assembled at runtime from fragments, so this source never holds a credential-shaped literal.
    /// </summary>
    public static async Task VerifySecretDetectorsAsync(string root)
    {
        string config = Path.Combine(root, DetectorConfig);
        Check.That(File.Exists(config), $"{DetectorConfig} not found.");

        // Obviously synthetic, low-entropy and never a real value; built from fragments so no source line matches a rule.
        string key = "Pass" + "word";
        string host = "Ho" + "st";
        string server = "Ser" + "ver";
        string user = "User" + "name";
        string value = "synthetic-" + "fixture-" + "value";
        string placeholder = "<YOUR_" + "PASSWORD>";
        string scheme = "postgres" + "ql://";
        string npgsql = $"{host}=db.example.invalid;Port=5432;Database=app;{user}=svc_user;{key}={value}";
        string npgsqlPlaceholder = $"{host}=<YOUR_HOST>;Port=5432;Database=<YOUR_DATABASE>;{user}=<YOUR_DATABASE_USER>;{key}={placeholder}";

        // Relative path -> rule ids expected for exactly that file; empty means the file must stay clean.
        var fixtures = new Dictionary<string, (string Content, string[] Rules)>(StringComparer.Ordinal)
        {
            ["uri.txt"] = ($"{scheme}svc_user:{value}@db.example.invalid:5432/app", ["postgresql-uri-password"]),
            ["npgsql.txt"] = (npgsql, ["npgsql-keyword-password"]),
            ["sqlserver.txt"] = ($"{server}=db.example.invalid;Database=app;User Id=svc_user;{key}={value}", ["sqlserver-keyword-password"]),
            // The README placeholder allowlist names exactly README.md at the repository root and one exact match text.
            ["README.md"] = (npgsqlPlaceholder, []),
            ["docs/README.md"] = (npgsqlPlaceholder, ["npgsql-keyword-password"]),
            ["notes.txt"] = (npgsqlPlaceholder, ["npgsql-keyword-password"])
        };

        using var work = new TemporaryDirectory();
        string gitleaks = await PinnedTools.InstallAsync(PinnedTools.Get("gitleaks"), Path.Combine(work.Path, "bin"));
        string files = Path.Combine(work.Path, "files");
        foreach ((string name, (string content, string[] _)) in fixtures)
        {
            string path = Path.Combine(files, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content + "\n");
        }
        // Fixtures are scanned in place (no Git history needed); report paths are relative to the scanned directory.
        string report = Path.Combine(work.Path, "findings.json");
        ProcessResult result = await Processes.RunAsync(
            new Command(gitleaks, "dir", ".", "--config", config, "--redact=100", "--no-banner", "--no-color", "--log-level=error",
                "--ignore-gitleaks-allow", "--gitleaks-ignore-path", work.Path, "--exit-code", "23",
                "--report-format", "json", "--report-path", report)
            { WorkingDirectory = files },
            expected: null);
        // Raw scanner output is never shown: it could echo fixture lines.
        Check.That(result.ExitCode == 23, $"gitleaks exited with status {result.ExitCode}; expected 23 (findings present). Output suppressed.");
        string json = await File.ReadAllTextAsync(report);
        Check.That(!json.Contains(value, StringComparison.Ordinal), "gitleaks did not redact a synthetic fixture value.");

        var actual = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (JsonNode? finding in JsonNode.Parse(json).Array())
        {
            string file = finding!["File"].Text().Replace('\\', '/');
            if (!actual.TryGetValue(file, out SortedSet<string>? ids)) actual[file] = ids = [];
            ids.Add(finding["RuleID"].Text());
        }

        var problems = new List<string>();
        foreach ((string name, (string _, string[]? rules)) in fixtures)
        {
            string found = string.Join(",", actual.TryGetValue(name, out SortedSet<string>? ids) ? ids : []);
            string expected = string.Join(",", rules.Order(StringComparer.Ordinal));
            if (found != expected) problems.Add($"{name}: expected [{expected}] but gitleaks reported [{found}]");
        }
        problems.AddRange(actual.Keys.Where(name => !fixtures.ContainsKey(name)).Select(name => $"{name}: unexpected file reported"));
        Check.That(problems.Count == 0, "Secret detector self-test failed:\n" + string.Join("\n", problems));
        Console.WriteLine($"Secret detector self-test passed: {fixtures.Count(f => f.Value.Rules.Length > 0)} positive and "
            + $"{fixtures.Count(f => f.Value.Rules.Length == 0)} allowlisted fixture(s) behaved as expected.");
    }
}
