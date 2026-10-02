using System.Text.Json;
using System.Text.Json.Nodes;

namespace PostgreSqlMcp.Verify;

internal static class SarifRegression
{
    public static void Run()
    {
        using TemporaryDirectory temporary = new();
        string baseline = Directory.CreateDirectory(Path.Combine(temporary.Path, "baseline")).FullName;
        string candidate = Directory.CreateDirectory(Path.Combine(temporary.Path, "candidate")).FullName;
        string baselineFile = Path.Combine(baseline, "csharp.sarif");
        string candidateFile = Path.Combine(candidate, "csharp.sarif");

        void Compare(string description, JsonObject before, JsonObject after, bool rejected)
        {
            File.WriteAllText(baselineFile, before.ToJsonString());
            File.WriteAllText(candidateFile, after.ToJsonString());
            AssertGate(baseline, candidate, rejected, description);
        }

        Compare("new finding exactly at the CVSS boundary", Report(), Report(Finding()), rejected: true);
        Compare("unchanged high finding", Report(Finding()), Report(Finding()), rejected: false);
        Compare("stable fingerprint survives file and line movement", Report(Finding()), Report(Finding(line: 90, path: "moved.cs")), rejected: false);
        Compare("new low severity finding", Report(), Report(Finding(severity: "6.9")), rejected: false);
        Compare("severity escalation is a new high finding", Report(Finding(severity: "6.9")), Report(Finding()), rejected: true);
        Compare("different fingerprint is not a rule-wide suppression", Report(Finding()), Report(Finding(fingerprint: "new")), rejected: true);
        Compare("duplicate candidate finding consumes baseline once", Report(Finding()), Report(Finding(), Finding()), rejected: true);
        Compare("location fallback accepts unchanged location", Report(Finding(fingerprint: null)), Report(Finding(fingerprint: null)), rejected: false);
        Compare("location fallback rejects different file", Report(Finding(fingerprint: null)), Report(Finding(fingerprint: null, path: "other.cs")), rejected: true);
        Compare("location fallback rejects shifted line", Report(Finding(fingerprint: null)), Report(Finding(fingerprint: null, line: 11)), rejected: true);

        JsonObject differentRule = Report(Finding());
        differentRule["runs"]![0]!["tool"]!["driver"]!["rules"]![0]!["id"] = "cs/other-rule";
        differentRule["runs"]![0]!["results"]![0]!["ruleId"] = "cs/other-rule";
        Compare("fingerprint cannot suppress another rule", Report(Finding()), differentRule, rejected: true);
        JsonObject differentTool = Report(Finding());
        differentTool["runs"]![0]!["tool"]!["driver"]!["name"] = "Other scanner";
        Compare("tool inventory mismatch", Report(Finding()), differentTool, rejected: true);

        JsonObject resultSeverity = Finding(severity: "1");
        resultSeverity["properties"] = new JsonObject { ["security-severity"] = "9.8" };
        Compare("result severity convention", Report(), Report(resultSeverity), rejected: true);
        JsonObject ruleSeverity = Report(Finding(severity: "1"));
        ruleSeverity["runs"]![0]!["tool"]!["driver"]!["rules"]![0]!["properties"] = new JsonObject { ["security-severity"] = "8.1" };
        Compare("rule severity cannot be lowered by result severity", Report(), ruleSeverity, rejected: true);
        JsonObject namedSeverity = Finding(severity: "1");
        namedSeverity["properties"] = new JsonObject { ["severity"] = "high" };
        Compare("named high severity convention", Report(), Report(namedSeverity), rejected: true);
        JsonObject errorLevel = Finding(severity: "1");
        errorLevel["level"] = "error";
        Compare("error level convention", Report(), Report(errorLevel), rejected: true);
        Compare("malformed severity fails closed", Report(), Report(Finding(severity: "not-a-score")), rejected: true);

        JsonObject failedInvocation = Report();
        failedInvocation["runs"]![0]!["invocations"]![0]!["executionSuccessful"] = false;
        Compare("failed invocation is not clean", Report(), failedInvocation, rejected: true);
        JsonObject missingInvocation = Report();
        missingInvocation["runs"]![0]!.AsObject().Remove("invocations");
        Compare("missing invocation evidence", Report(), missingInvocation, rejected: true);
        JsonObject notification = Report();
        notification["runs"]![0]!["invocations"]![0]!["toolExecutionNotifications"] = new JsonArray(new JsonObject { ["level"] = "error", ["message"] = new JsonObject { ["text"] = "sensitive-notification" } });
        Compare("error notification despite success flag", Report(), notification, rejected: true);
        JsonObject missingIdentity = Finding(fingerprint: null);
        missingIdentity.Remove("locations");
        Compare("high finding without safe matching identity", Report(), Report(missingIdentity), rejected: true);

        File.WriteAllText(candidateFile, "{");
        AssertGate(baseline, candidate, rejected: true, "malformed JSON");
        File.Delete(candidateFile);
        AssertGate(baseline, candidate, rejected: true, "missing reports");
        AssertGate(baseline, Path.Combine(temporary.Path, "absent"), rejected: true, "missing directory");
        File.WriteAllText(candidateFile, Report().ToJsonString());
        File.WriteAllText(Path.Combine(baseline, "actions.sarif"), Report().ToJsonString());
        AssertGate(baseline, candidate, rejected: true, "missing language report");
        File.Delete(Path.Combine(baseline, "actions.sarif"));

        JsonObject unsafeReport = Report(Finding(path: "file\n::error::injected\u001b.cs"));
        File.WriteAllText(baselineFile, Report().ToJsonString());
        File.WriteAllText(candidateFile, unsafeReport.ToJsonString());
        using StringWriter diagnostics = new();
        try { SarifGate.Run(baseline, candidate, diagnostics); }
        catch (VerificationException) { }
        string output = diagnostics.ToString();
        Check.That(output.Contains("new=1", StringComparison.Ordinal) && output.Contains("line=10", StringComparison.Ordinal), "SARIF diagnostics must identify count and location.");
        Check.Confidential(output, "sensitive-finding-message", "sensitive-source-snippet", "\n::error::injected", "\u001b");
        Console.WriteLine("SARIF regression checks passed: severity deltas, fingerprints, precise locations, tool/rule identity, multiplicity, fail-closed reports and safe diagnostics.");
    }

    private static void AssertGate(string baseline, string candidate, bool rejected, string description)
    {
        bool failed = false;
        try { SarifGate.Run(baseline, candidate, TextWriter.Null); }
        catch (Exception ex) when (ex is VerificationException or JsonException or InvalidOperationException or KeyNotFoundException) { failed = true; }
        Check.That(failed == rejected, $"SARIF regression failed: {description}.");
    }

    private static JsonObject Finding(string? fingerprint = "stable", int line = 10, string path = "src/File.cs", string severity = "7")
    {
        JsonObject result = JsonSerializer.SerializeToNode(new
        {
            ruleId = "cs/security-rule",
            ruleIndex = 0,
            properties = new Dictionary<string, string> { ["security-severity"] = severity },
            message = new { text = "sensitive-finding-message" },
            locations = new[] { new { physicalLocation = new { artifactLocation = new { uri = path }, region = new { startLine = line, startColumn = 1 }, contextRegion = new { snippet = new { text = "sensitive-source-snippet" } } } } }
        })!.AsObject();
        if (fingerprint is not null) result["partialFingerprints"] = new JsonObject { ["primaryLocationLineHash"] = fingerprint };
        return result;
    }

    private static JsonObject Report(params JsonObject[] results) => JsonSerializer.SerializeToNode(new
    {
        version = "2.1.0",
        runs = new[] { new
        {
            tool = new { driver = new { name = "CodeQL", version = "2.27.1", rules = new[] { new { id = "cs/security-rule", defaultConfiguration = new { level = "warning" } } } } },
            invocations = new[] { new { executionSuccessful = true } },
            results
        } }
    })!.AsObject();
}
