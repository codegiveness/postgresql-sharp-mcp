// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;

namespace PostgreSqlMcp.Verify.Tests;

// Fail-closed handling: anything that is not clearly a successful, well-formed, comparable scan is an error, never a clean result.
public sealed partial class SarifGateTests
{
    private string RejectedCandidate(JsonObject candidateRun) => Rejected(Report(Run([])), Report(candidateRun)).Message;

    [Fact]
    public void A_missing_report_directory_is_an_error()
    {
        Write(Baseline, "scan.sarif", Report(Run([])));

        VerificationException ex = Assert.Throws<VerificationException>(() => SarifGate.Run(Baseline, Candidate, TextWriter.Null));

        Assert.Equal("SARIF report directory is missing.", ex.Message);
    }

    [Fact]
    public void A_directory_without_sarif_files_is_an_error()
    {
        Write(Baseline, "scan.sarif", Report(Run([])));
        Write(Candidate, "notes.txt", "not a report");

        VerificationException ex = Assert.Throws<VerificationException>(() => SarifGate.Run(Baseline, Candidate, TextWriter.Null));

        Assert.Equal("SARIF report directory contains no reports.", ex.Message);
    }

    [Fact]
    public void Reports_in_subdirectories_are_read()
    {
        Write(Path.Combine(Baseline, "nested", "deeper"), "scan.sarif", Report(Run([Result(fingerprint: "f")])));
        Write(Path.Combine(Candidate, "nested", "deeper"), "scan.sarif", Report(Run([Result(fingerprint: "f")])));
        var output = new StringWriter();

        SarifGate.Run(Baseline, Candidate, output);

        Assert.Contains("baseline=1, candidate=1, new=0.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Baseline_and_candidate_must_have_scanned_with_the_same_tools_and_files()
    {
        Assert.Equal("Baseline and candidate SARIF tool inventories differ.",
            Rejected(Report(Run([], version: "1.0")), Report(Run([], version: "2.0"))).Message);

        Write(Baseline, "scan.sarif", Report(Run([])));
        Write(Candidate, "renamed.sarif", Report(Run([])));
        Assert.Equal("Baseline and candidate SARIF tool inventories differ.",
            Assert.Throws<VerificationException>(() => SarifGate.Run(Baseline, Candidate, TextWriter.Null)).Message);
    }

    [Fact]
    public void An_unsupported_sarif_version_is_rejected() =>
        Assert.Equal("Unsupported SARIF report version.",
            Rejected(Report(Run([])), Report(Run([])).Replace("2.1.0", "2.0.0", StringComparison.Ordinal)).Message);

    [Fact]
    public void A_report_without_runs_is_rejected() =>
        Assert.Equal("SARIF report contains no runs.", Rejected(Report(Run([])), """{"version":"2.1.0","runs":[]}""").Message);

    [Fact]
    public void A_run_without_invocation_evidence_is_rejected()
    {
        JsonObject run = Run([]);
        run["invocations"] = new JsonArray();

        Assert.Equal("SARIF run has no successful invocation evidence.", RejectedCandidate(run));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void An_invocation_that_failed_or_omits_its_status_is_rejected(bool? successful) =>
        Assert.Equal("SARIF invocation failed or omitted execution status.", RejectedCandidate(Run([], successful: successful)));

    [Fact]
    public void An_error_notification_is_a_failed_scan_even_when_the_invocation_claims_success() =>
        Assert.Equal("SARIF invocation reported an error notification.",
            RejectedCandidate(Run([], notifications: new JsonArray(new JsonObject { ["level"] = "error" }))));

    [Fact]
    public void Warning_notifications_do_not_fail_the_scan()
    {
        string output = Compare(Report(Run([])), Report(Run([], notifications: new JsonArray(new JsonObject { ["level"] = "warning" }))));

        Assert.Contains("new=0.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_tool_run_twice_in_one_report_is_rejected() =>
        Assert.Equal("SARIF report contains duplicate tool runs.", Rejected(Report(Run([])), Report(Run([]), Run([]))).Message);

    [Fact]
    public void Malformed_json_is_an_error_not_a_clean_result()
    {
        Write(Baseline, "scan.sarif", Report(Run([])));
        Write(Candidate, "scan.sarif", "{ truncated");

        Assert.ThrowsAny<Exception>(() => SarifGate.Run(Baseline, Candidate, TextWriter.Null));
    }

    [Fact]
    public void A_rule_index_outside_the_rule_list_is_rejected() =>
        Assert.Equal("Invalid SARIF rule index.", RejectedCandidate(Run([Result(ruleIndex: 7, fingerprint: "f")])));

    [Fact]
    public void A_rule_id_that_disagrees_with_its_index_is_rejected() =>
        Assert.Equal("SARIF rule identity and index disagree.", RejectedCandidate(Run([Result(rule: "R2", ruleIndex: 0, fingerprint: "f")])));

    [Fact]
    public void A_rule_id_without_a_matching_rule_is_rejected() =>
        Assert.Equal("SARIF result does not resolve to a rule.", RejectedCandidate(Run([Result(rule: "unknown", ruleIndex: null, fingerprint: "f")])));

    [Fact]
    public void A_rule_id_that_matches_two_rules_is_ambiguous() =>
        Assert.Equal("Ambiguous SARIF rule identity.",
            RejectedCandidate(Run([Result(ruleIndex: null, fingerprint: "f")], rules: [new JsonObject { ["id"] = "R1" }, new JsonObject { ["id"] = "R1" }])));

    [Fact]
    public void A_rule_can_be_found_by_id_without_an_index()
    {
        string output = Compare(Report(Run([])), Report(Run([Result(rule: "R2", ruleIndex: null, level: "warning", fingerprint: "f")])));

        Assert.Contains("candidate=0", output, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_level_is_rejected() =>
        Assert.Equal("Invalid SARIF finding level.", RejectedCandidate(Run([Result(level: "catastrophic", fingerprint: "f")])));

    [Theory]
    [InlineData("11")]
    [InlineData("-1")]
    [InlineData("high")]
    public void An_out_of_range_security_severity_is_rejected(string value)
    {
        JsonObject rule = new() { ["id"] = "R1", ["properties"] = new JsonObject { ["security-severity"] = value } };

        Assert.Equal("Invalid SARIF security severity.", RejectedCandidate(Run([Result(level: "note", fingerprint: "f")], rules: [rule])));
    }

    [Theory]
    [InlineData("critical", true)]
    [InlineData("high", true)]
    [InlineData("error", true)]
    [InlineData("medium", false)]
    [InlineData("low", false)]
    public void Named_severities_are_mapped_to_scores(string severity, bool high)
    {
        JsonObject rule = new() { ["id"] = "R1", ["properties"] = new JsonObject { ["problem.severity"] = severity } };

        string output = Output(Report(Run([], rules: [rule])), Report(Run([Result(level: "warning", fingerprint: "f")], rules: [rule])));

        Assert.Contains($"candidate={(high ? 1 : 0)}, new={(high ? 1 : 0)}.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_named_severity_is_rejected()
    {
        JsonObject rule = new() { ["id"] = "R1", ["properties"] = new JsonObject { ["problem.severity"] = "apocalyptic" } };

        Assert.Equal("Invalid SARIF named severity.", RejectedCandidate(Run([Result(level: "note", fingerprint: "f")], rules: [rule])));
    }

    [Fact]
    public void Rule_ids_are_escaped_and_bounded_before_they_reach_the_log()
    {
        string hostile = "evil\n::error::injected" + new string('x', 300);
        JsonObject rule = new() { ["id"] = hostile };

        (_, string output) = Rejected(Report(Run([], rules: [rule])), Report(Run([Result(rule: hostile, fingerprint: "f")], rules: [rule])));

        string line = Assert.Single(output.ReplaceLineEndings("\n").Split('\n'), text => text.StartsWith("SARIF new high finding:", StringComparison.Ordinal));
        Assert.Contains("evil\\u000a::error::injected", line, StringComparison.Ordinal);
        Assert.Contains("...", line, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 241), line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_result_without_locations_is_reported_as_unavailable()
    {
        JsonObject noLocation = new() { ["ruleId"] = "R1", ["ruleIndex"] = 0, ["level"] = "error", ["partialFingerprints"] = new JsonObject { ["k"] = "v" } };

        (_, string output) = Rejected(Report(Run([])), Report(Run([noLocation])));

        // Text is escaped to a safe alphabet, which includes the parentheses.
        Assert.Contains("file=\\u0028unavailable\\u0029, line=0.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_fingerprint_value_is_rejected()
    {
        JsonObject bad = Result(fingerprint: "x");
        bad["partialFingerprints"] = new JsonObject { ["k"] = "" };

        Assert.Equal("Invalid SARIF fingerprint value.", RejectedCandidate(Run([bad])));
    }
}
