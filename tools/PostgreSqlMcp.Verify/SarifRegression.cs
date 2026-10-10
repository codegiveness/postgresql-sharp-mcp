using System.Text.Json;
using System.Text.Json.Nodes;

namespace PostgreSqlMcp.Verify;

internal static class SarifRegression
{
    private const string NewHighFindings = "rejected new high-severity findings";

    public static void Run()
    {
        using TemporaryDirectory temporary = new();
        string baseline = Directory.CreateDirectory(Path.Combine(temporary.Path, "baseline")).FullName;
        string candidate = Directory.CreateDirectory(Path.Combine(temporary.Path, "candidate")).FullName;
        string baselineFile = Path.Combine(baseline, "csharp.sarif");
        string candidateFile = Path.Combine(candidate, "csharp.sarif");

        void Compare(string description, JsonObject before, JsonObject after, string? rejection)
        {
            File.WriteAllText(baselineFile, before.ToJsonString());
            File.WriteAllText(candidateFile, after.ToJsonString());
            AssertGate(baseline, candidate, description, rejection);
        }

        Compare("new finding exactly at the CVSS boundary", Report(), Report(Finding()), NewHighFindings);
        Compare("unchanged high finding", Report(Finding()), Report(Finding()), rejection: null);
        Compare("stable fingerprint survives file and line movement", Report(Finding()), Report(Finding(line: 90, path: "moved.cs")), rejection: null);
        Compare("new low severity finding", Report(), Report(Finding(severity: "6.9")), rejection: null);
        Compare("severity escalation is a new high finding", Report(Finding(severity: "6.9")), Report(Finding()), NewHighFindings);
        Compare("different fingerprint is not a rule-wide suppression", Report(Finding()), Report(Finding(fingerprint: "new")), NewHighFindings);
        Compare("duplicate candidate finding consumes baseline once", Report(Finding()), Report(Finding(), Finding()), NewHighFindings);
        Compare("location fallback accepts unchanged location", Report(Finding(fingerprint: null)), Report(Finding(fingerprint: null)), rejection: null);
        Compare("location fallback rejects different file", Report(Finding(fingerprint: null)), Report(Finding(fingerprint: null, path: "other.cs")), NewHighFindings);
        Compare("location fallback rejects shifted line", Report(Finding(fingerprint: null)), Report(Finding(fingerprint: null, line: 11)), NewHighFindings);

        JsonObject differentRule = Report(Finding());
        differentRule["runs"]![0]!["tool"]!["driver"]!["rules"]![0]!["id"] = "cs/other-rule";
        differentRule["runs"]![0]!["results"]![0]!["ruleId"] = "cs/other-rule";
        Compare("fingerprint cannot suppress another rule", Report(Finding()), differentRule, NewHighFindings);
        JsonObject differentTool = Report(Finding());
        differentTool["runs"]![0]!["tool"]!["driver"]!["name"] = "Other scanner";
        Compare("tool inventory mismatch", Report(Finding()), differentTool, "tool inventories differ");

        JsonObject resultSeverity = Finding(severity: "1");
        resultSeverity["properties"] = new JsonObject { ["security-severity"] = "9.8" };
        Compare("result severity convention", Report(), Report(resultSeverity), NewHighFindings);
        JsonObject ruleSeverity = Report(Finding(severity: "1"));
        ruleSeverity["runs"]![0]!["tool"]!["driver"]!["rules"]![0]!["properties"] = new JsonObject { ["security-severity"] = "8.1" };
        Compare("rule severity cannot be lowered by result severity", Report(), ruleSeverity, NewHighFindings);
        JsonObject namedSeverity = Finding(severity: "1");
        namedSeverity["properties"] = new JsonObject { ["severity"] = "high" };
        Compare("named high severity convention", Report(), Report(namedSeverity), NewHighFindings);
        JsonObject errorLevel = Finding(severity: "1");
        errorLevel["level"] = "error";
        Compare("error level convention", Report(), Report(errorLevel), NewHighFindings);
        Compare("malformed severity fails closed", Report(), Report(Finding(severity: "not-a-score")), "Invalid SARIF security severity");

        JsonObject failedInvocation = Report();
        failedInvocation["runs"]![0]!["invocations"]![0]!["executionSuccessful"] = false;
        Compare("failed invocation is not clean", Report(), failedInvocation, "invocation failed or omitted execution status");
        JsonObject missingInvocation = Report();
        missingInvocation["runs"]![0]!.AsObject().Remove("invocations");
        Compare("missing invocation evidence", Report(), missingInvocation, "required array is missing or malformed");
        JsonObject notification = Report();
        notification["runs"]![0]!["invocations"]![0]!["toolExecutionNotifications"] = new JsonArray(new JsonObject { ["level"] = "error", ["message"] = new JsonObject { ["text"] = "sensitive-notification" } });
        Compare("error notification despite success flag", Report(), notification, "reported an error notification");
        JsonObject missingIdentity = Finding(fingerprint: null);
        missingIdentity.Remove("locations");
        Compare("high finding without safe matching identity", Report(), Report(missingIdentity), "neither a fingerprint nor a precise location");

        File.WriteAllText(candidateFile, "{");
        AssertGate(baseline, candidate, "malformed JSON", "JSON payload", typeof(JsonException));
        File.Delete(candidateFile);
        AssertGate(baseline, candidate, "missing reports", "contains no reports");
        AssertGate(baseline, Path.Combine(temporary.Path, "absent"), "missing directory", "directory is missing");
        File.WriteAllText(candidateFile, Report().ToJsonString());
        File.WriteAllText(Path.Combine(baseline, "actions.sarif"), Report().ToJsonString());
        AssertGate(baseline, candidate, "missing language report", "tool inventories differ");
        File.Delete(Path.Combine(baseline, "actions.sarif"));

        JsonObject unsafeReport = Report(Finding(path: "file\n::error::injected\u001b.cs"));
        File.WriteAllText(baselineFile, Report().ToJsonString());
        File.WriteAllText(candidateFile, unsafeReport.ToJsonString());
        using StringWriter diagnostics = new();
        VerificationException? unsafeRejection = null;
        try { SarifGate.Run(baseline, candidate, diagnostics); }
        catch (VerificationException ex) { unsafeRejection = ex; }
        Check.That(unsafeRejection?.Message.Contains(NewHighFindings, StringComparison.Ordinal) == true, "SARIF regression failed: unsafe finding path was not rejected as a new high finding.");
        string output = diagnostics.ToString();
        Check.That(output.Contains("new=1", StringComparison.Ordinal) && output.Contains("line=10", StringComparison.Ordinal), "SARIF diagnostics must identify count and location.");
        Check.Confidential(output, "sensitive-finding-message", "sensitive-source-snippet", "\n::error::injected", "\u001b");
        Console.WriteLine("SARIF regression checks passed: severity deltas, fingerprints, precise locations, tool/rule identity, multiplicity, fail-closed reports and safe diagnostics.");
    }

    // A rejection must come from the intended gate check: the exception type and a fixed message fragment both have to match.
    private static void AssertGate(string baseline, string candidate, string description, string? rejection, Type? exceptionType = null)
    {
        Exception? thrown = null;
        try { SarifGate.Run(baseline, candidate, TextWriter.Null); }
        catch (Exception ex) { thrown = ex; }
        if (rejection is null)
        {
            Check.That(thrown is null, $"SARIF regression failed: {description} was rejected unexpectedly ({thrown?.GetType().Name}).");
            return;
        }
        Check.That(thrown is not null, $"SARIF regression failed: {description} was not rejected.");
        Check.That((exceptionType ?? typeof(VerificationException)).IsInstanceOfType(thrown), $"SARIF regression failed: {description} was rejected with {thrown!.GetType().Name}.");
        Check.That(thrown!.Message.Contains(rejection, StringComparison.Ordinal), $"SARIF regression failed: {description} was rejected for a different reason.");
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
