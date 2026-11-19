// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;

namespace PostgreSqlMcp.Verify.Tests;

/// <summary>
/// The SARIF comparator is the repository's fail-closed security gate: a new high-severity result, an unsuccessful scan or a
/// malformed report must fail it, and a fixed or unchanged result must not. Reports here are minimal synthetic documents.
/// </summary>
public sealed partial class SarifGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"postgresql-sharp-mcp-sarif-{Guid.NewGuid():N}");

    private string Baseline => Path.Combine(_root, "baseline");
    private string Candidate => Path.Combine(_root, "candidate");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static JsonObject Result(string rule = "R1", string file = "src/A.cs", int line = 10, string? level = "error", string? fingerprint = null, int? ruleIndex = 0)
    {
        var result = new JsonObject
        {
            ["ruleId"] = rule,
            ["locations"] = new JsonArray(new JsonObject
            {
                ["physicalLocation"] = new JsonObject
                {
                    ["artifactLocation"] = new JsonObject { ["uri"] = file },
                    ["region"] = new JsonObject { ["startLine"] = line }
                }
            })
        };
        if (level is not null) result["level"] = level;
        if (ruleIndex is not null) result["ruleIndex"] = ruleIndex;
        if (fingerprint is not null) result["partialFingerprints"] = new JsonObject { ["primaryLocationLineHash"] = fingerprint };
        return result;
    }

    private static JsonObject Run(JsonNode[] results, string version = "1.0", bool? successful = true, JsonObject[]? rules = null, JsonNode? notifications = null)
    {
        var invocation = new JsonObject();
        if (successful is not null) invocation["executionSuccessful"] = successful;
        if (notifications is not null) invocation["toolExecutionNotifications"] = notifications.DeepClone();
        return new JsonObject
        {
            ["tool"] = new JsonObject
            {
                ["driver"] = new JsonObject
                {
                    ["name"] = "SyntheticScanner",
                    ["semanticVersion"] = version,
                    ["rules"] = new JsonArray((rules ?? [new JsonObject { ["id"] = "R1" }, new JsonObject { ["id"] = "R2" }]).Select(rule => rule.DeepClone()).ToArray())
                }
            },
            ["invocations"] = new JsonArray(invocation),
            ["results"] = new JsonArray(results.Select(result => result.DeepClone()).ToArray())
        };
    }

    private static string Report(params JsonObject[] runs) =>
        new JsonObject { ["version"] = "2.1.0", ["runs"] = new JsonArray(runs.Select(run => (JsonNode)run).ToArray()) }.ToJsonString();

    private static void Write(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), content);
    }

    private string Compare(string baseline, string candidate)
    {
        Write(Baseline, "scan.sarif", baseline);
        Write(Candidate, "scan.sarif", candidate);
        using var output = new StringWriter();
        SarifGate.Run(Baseline, Candidate, output);
        return output.ToString();
    }

    /// <summary>Runs the gate and returns what it printed, whether or not it passed.</summary>
    private string Output(string baseline, string candidate)
    {
        Write(Baseline, "scan.sarif", baseline);
        Write(Candidate, "scan.sarif", candidate);
        using var output = new StringWriter();
        try { SarifGate.Run(Baseline, Candidate, output); }
        catch (VerificationException) { }
        return output.ToString();
    }

    private (string Message, string Output) Rejected(string baseline, string candidate)
    {
        Write(Baseline, "scan.sarif", baseline);
        Write(Candidate, "scan.sarif", candidate);
        using var output = new StringWriter();
        VerificationException ex = Assert.Throws<VerificationException>(() => SarifGate.Run(Baseline, Candidate, output));
        return (ex.Message, output.ToString());
    }

    [Fact]
    public void An_unchanged_high_finding_passes_and_is_counted()
    {
        string report = Report(Run([Result(fingerprint: "h1")]));

        Assert.Equal("SARIF high findings (CVSS >= 7 or error/high/critical severity): baseline=1, candidate=1, new=0.\n",
            Compare(report, report).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_new_high_finding_fails_and_is_listed_with_rule_file_and_line()
    {
        (string message, string output) = Rejected(
            Report(Run([Result(fingerprint: "h1")])),
            Report(Run([Result(fingerprint: "h1"), Result(rule: "R2", file: "src/B.cs", line: 20, ruleIndex: 1, fingerprint: "h2")])));

        Assert.Equal("SARIF gate rejected new high-severity findings.", message);
        Assert.Contains("baseline=1, candidate=2, new=1.", output, StringComparison.Ordinal);
        Assert.Contains("SARIF new high finding: rule=R2, file=src/B.cs, line=20.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fixed_finding_passes()
    {
        string output = Compare(Report(Run([Result(fingerprint: "h1"), Result(fingerprint: "h2")])), Report(Run([Result(fingerprint: "h1")])));

        Assert.Contains("baseline=2, candidate=1, new=0.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void One_baseline_result_cannot_hide_two_identical_candidate_results()
    {
        (string message, string output) = Rejected(
            Report(Run([Result(fingerprint: "same")])),
            Report(Run([Result(fingerprint: "same"), Result(fingerprint: "same", line: 99)])));

        Assert.Equal("SARIF gate rejected new high-severity findings.", message);
        Assert.Contains("new=1.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fingerprint_keeps_a_finding_the_same_when_its_line_moves()
    {
        string output = Compare(Report(Run([Result(line: 10, fingerprint: "stable")])), Report(Run([Result(line: 55, fingerprint: "stable")])));

        Assert.Contains("new=0.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_fingerprint_the_precise_location_is_the_identity()
    {
        (_, string output) = Rejected(Report(Run([Result(line: 10)])), Report(Run([Result(line: 11)])));

        Assert.Contains("new=1.", output, StringComparison.Ordinal);
        Assert.Contains("line=11.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_high_finding_with_neither_fingerprint_nor_location_is_rejected()
    {
        JsonObject bare = new() { ["ruleId"] = "R1", ["ruleIndex"] = 0, ["level"] = "error" };

        (string message, _) = Rejected(Report(Run([])), Report(Run([bare])));

        Assert.Equal("High SARIF finding has neither a fingerprint nor a precise location.", message);
    }

    [Theory]
    [InlineData("warning", null, 0)]
    [InlineData("note", null, 0)]
    [InlineData(null, null, 0)]
    [InlineData("warning", "6.9", 0)]
    [InlineData("warning", "7.0", 1)]
    [InlineData("note", "9.8", 1)]
    [InlineData("error", null, 1)]
    [InlineData("error", "1.0", 1)]
    public void Only_error_level_or_cvss_7_and_above_counts_as_high(string? level, string? securitySeverity, int expectedHigh)
    {
        JsonObject result = Result(level: level, fingerprint: "f");
        JsonObject rules = new() { ["id"] = "R1" };
        if (securitySeverity is not null) rules["properties"] = new JsonObject { ["security-severity"] = securitySeverity };

        string output = Output(Report(Run([], rules: [rules])), Report(Run([result], rules: [rules])));

        Assert.Contains($"candidate={expectedHigh}, new={expectedHigh}", output, StringComparison.Ordinal);
    }
}
