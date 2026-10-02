using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PostgreSqlMcp.Verify;

internal static class SarifGate
{
    private sealed record Finding(string Identity, string Rule, string File, int Line);
    private sealed record Reports(HashSet<string> Tools, List<Finding> High);

    public static void Run(string baselineDirectory, string candidateDirectory, TextWriter output)
    {
        Reports baseline = ReadReports(baselineDirectory);
        Reports candidate = ReadReports(candidateDirectory);
        Require(baseline.Tools.SetEquals(candidate.Tools), "Baseline and candidate SARIF tool inventories differ.");
        Dictionary<string, int> remaining = new(StringComparer.Ordinal);
        foreach (Finding finding in baseline.High)
            remaining[finding.Identity] = remaining.GetValueOrDefault(finding.Identity) + 1;
        List<Finding> added = [];
        foreach (Finding finding in candidate.High)
        {
            if (remaining.GetValueOrDefault(finding.Identity) > 0)
                remaining[finding.Identity]--;
            else
                added.Add(finding);
        }
        output.WriteLine($"SARIF high findings (CVSS >= 7 or error/high/critical severity): baseline={baseline.High.Count}, candidate={candidate.High.Count}, new={added.Count}.");
        foreach (Finding finding in added)
            output.WriteLine($"SARIF new high finding: rule={Safe(finding.Rule)}, file={Safe(finding.File)}, line={finding.Line}.");
        Require(added.Count == 0, "SARIF gate rejected new high-severity findings.");
    }

    private static Reports ReadReports(string directory)
    {
        Require(Directory.Exists(directory), "SARIF report directory is missing.");
        string[] files = Directory.GetFiles(directory, "*.sarif", SearchOption.AllDirectories);
        Require(files.Length > 0, "SARIF report directory contains no reports.");
        HashSet<string> tools = new(StringComparer.Ordinal);
        List<Finding> high = [];
        foreach (string file in files.Order(StringComparer.Ordinal))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(file));
            JsonElement root = document.RootElement;
            Require(Text(root, "version") == "2.1.0", "Unsupported SARIF report version.");
            JsonElement runs = Array(root, "runs");
            Require(runs.GetArrayLength() > 0, "SARIF report contains no runs.");
            foreach (JsonElement run in runs.EnumerateArray())
            {
                JsonElement invocations = Array(run, "invocations");
                Require(invocations.GetArrayLength() > 0, "SARIF run has no successful invocation evidence.");
                foreach (JsonElement invocation in invocations.EnumerateArray())
                {
                    Require(invocation.TryGetProperty("executionSuccessful", out JsonElement successful) && successful.ValueKind == JsonValueKind.True,
                        "SARIF invocation failed or omitted execution status.");
                    CheckNotifications(invocation, "toolExecutionNotifications");
                    CheckNotifications(invocation, "toolConfigurationNotifications");
                }
                JsonElement tool = run.GetProperty("tool");
                JsonElement driver = tool.GetProperty("driver");
                string driverIdentity = ToolIdentity(driver);
                Require(tools.Add(JsonSerializer.Serialize(new[] { Path.GetRelativePath(directory, file), driverIdentity })), "SARIF report contains duplicate tool runs.");
                foreach (JsonElement result in Array(run, "results").EnumerateArray())
                {
                    JsonElement component = driver;
                    if (result.TryGetProperty("rule", out JsonElement reference) && reference.TryGetProperty("toolComponent", out JsonElement componentReference))
                    {
                        int componentIndex = componentReference.GetProperty("index").GetInt32();
                        JsonElement extensions = Array(tool, "extensions");
                        Require(componentIndex >= 0 && componentIndex < extensions.GetArrayLength(), "Invalid SARIF rule component index.");
                        component = extensions[componentIndex];
                    }
                    string? ruleId = OptionalText(result, "ruleId");
                    int? ruleIndex = result.TryGetProperty("ruleIndex", out JsonElement index) ? index.GetInt32() : null;
                    if (result.TryGetProperty("rule", out JsonElement ruleReference))
                    {
                        ruleId ??= OptionalText(ruleReference, "id");
                        ruleIndex ??= ruleReference.TryGetProperty("index", out JsonElement nestedIndex) ? nestedIndex.GetInt32() : null;
                    }
                    JsonElement rules = Array(component, "rules");
                    JsonElement rule = default;
                    if (ruleIndex is int position)
                    {
                        Require(position >= 0 && position < rules.GetArrayLength(), "Invalid SARIF rule index.");
                        rule = rules[position];
                        Require(ruleId is null || ruleId == Text(rule, "id"), "SARIF rule identity and index disagree.");
                    }
                    else
                    {
                        foreach (JsonElement item in rules.EnumerateArray())
                            if (Text(item, "id") == ruleId)
                            {
                                Require(rule.ValueKind == JsonValueKind.Undefined, "Ambiguous SARIF rule identity.");
                                rule = item;
                            }
                    }
                    Require(rule.ValueKind == JsonValueKind.Object, "SARIF result does not resolve to a rule.");
                    ruleId = Text(rule, "id");
                    (string location, string path, int line) = Location(result);
                    string fingerprint = Fingerprints(result, "fingerprints");
                    string partialFingerprint = Fingerprints(result, "partialFingerprints");
                    if (fingerprint.Length == 0) fingerprint = partialFingerprint;
                    if (!IsHigh(result, rule)) continue;
                    Require(fingerprint.Length > 0 || location.Length > 0, "High SARIF finding has neither a fingerprint nor a precise location.");
                    // Length-safe JSON tuples prevent ambiguity from untrusted separators. Counts prevent one baseline result hiding multiple new results.
                    string identity = JsonSerializer.Serialize(new[] { driverIdentity, ToolIdentity(component), ruleId, fingerprint.Length > 0 ? "fingerprint" : "location", fingerprint.Length > 0 ? fingerprint : location });
                    high.Add(new Finding(identity, ruleId, path, line));
                }
            }
        }
        return new Reports(tools, high);
    }

    private static bool IsHigh(JsonElement result, JsonElement rule)
    {
        double score = Math.Max(Severity(result), Severity(rule));
        string level = OptionalText(result, "level") ?? (rule.TryGetProperty("defaultConfiguration", out JsonElement configuration) ? OptionalText(configuration, "level") : null) ?? "warning";
        Require(level is "none" or "note" or "warning" or "error", "Invalid SARIF finding level.");
        return score >= 7 || level == "error";
    }

    private static double Severity(JsonElement item)
    {
        if (!item.TryGetProperty("properties", out JsonElement properties)) return 0;
        Require(properties.ValueKind == JsonValueKind.Object, "Invalid SARIF properties.");
        double score = 0;
        foreach (string key in new[] { "security-severity", "securitySeverity" })
        {
            if (!properties.TryGetProperty(key, out JsonElement severity)) continue;
            double value = 0;
            bool parsed = severity.ValueKind == JsonValueKind.Number ? severity.TryGetDouble(out value) :
                severity.ValueKind == JsonValueKind.String && double.TryParse(severity.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            Require(parsed && double.IsFinite(value) && value >= 0 && value <= 10, "Invalid SARIF security severity.");
            score = Math.Max(score, value);
        }
        foreach (string key in new[] { "severity", "problem.severity" })
        {
            string? severity = OptionalText(properties, key);
            if (severity is null) continue;
            score = Math.Max(score, severity switch
            {
                "critical" => 9,
                "high" or "error" => 7,
                "medium" or "moderate" or "warning" => 4,
                "low" or "note" or "recommendation" or "none" => 0,
                _ => throw new VerificationException("Invalid SARIF named severity.")
            });
        }
        return score;
    }

    private static (string Identity, string File, int Line) Location(JsonElement result)
    {
        if (!result.TryGetProperty("locations", out JsonElement locations)) return ("", "(unavailable)", 0);
        Require(locations.ValueKind == JsonValueKind.Array, "Invalid SARIF locations.");
        if (locations.GetArrayLength() == 0 || !locations[0].TryGetProperty("physicalLocation", out JsonElement physical)) return ("", "(unavailable)", 0);
        if (!physical.TryGetProperty("artifactLocation", out JsonElement artifact)) return ("", "(unavailable)", 0);
        string path = Text(artifact, "uri");
        string baseId = OptionalText(artifact, "uriBaseId") ?? "";
        if (!physical.TryGetProperty("region", out JsonElement region) || !region.TryGetProperty("startLine", out JsonElement start)) return ("", path, 0);
        int line = start.GetInt32();
        int column = region.TryGetProperty("startColumn", out JsonElement startColumn) ? startColumn.GetInt32() : 0;
        int endLine = region.TryGetProperty("endLine", out JsonElement end) ? end.GetInt32() : line;
        int endColumn = region.TryGetProperty("endColumn", out JsonElement endCol) ? endCol.GetInt32() : 0;
        Require(line > 0 && column >= 0 && endLine >= line && endColumn >= 0, "Invalid SARIF finding region.");
        return (JsonSerializer.Serialize(new object[] { baseId, path, line, column, endLine, endColumn }), path, line);
    }

    private static string Fingerprints(JsonElement result, string property)
    {
        if (!result.TryGetProperty(property, out JsonElement fingerprints)) return "";
        Require(fingerprints.ValueKind == JsonValueKind.Object, "Invalid SARIF fingerprints.");
        SortedDictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (JsonProperty entry in fingerprints.EnumerateObject())
        {
            Require(entry.Value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(entry.Value.GetString()), "Invalid SARIF fingerprint value.");
            Require(values.TryAdd(entry.Name, entry.Value.GetString()!), "Duplicate SARIF fingerprint key.");
        }
        return values.Count == 0 ? "" : JsonSerializer.Serialize(values);
    }

    private static void CheckNotifications(JsonElement invocation, string name)
    {
        if (!invocation.TryGetProperty(name, out JsonElement notifications)) return;
        Require(notifications.ValueKind == JsonValueKind.Array, "Invalid SARIF invocation notifications.");
        foreach (JsonElement notification in notifications.EnumerateArray())
            Require(OptionalText(notification, "level") != "error", "SARIF invocation reported an error notification.");
    }

    private static string ToolIdentity(JsonElement component) => JsonSerializer.Serialize(new[] { Text(component, "name"), OptionalText(component, "semanticVersion") ?? OptionalText(component, "version") ?? "" });
    private static JsonElement Array(JsonElement item, string name)
    {
        Require(item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array, "SARIF required array is missing or malformed.");
        return value;
    }
    private static string Text(JsonElement item, string name) => OptionalText(item, name) is { Length: > 0 } text ? text : throw new VerificationException("SARIF required text is missing or malformed.");
    private static string? OptionalText(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out JsonElement value)) return null;
        Require(value.ValueKind == JsonValueKind.String, "Invalid SARIF text value.");
        return value.GetString();
    }
    private static string Safe(string text)
    {
        StringBuilder safe = new();
        foreach (char character in text.Take(240))
            if (char.IsAsciiLetterOrDigit(character) || character is '/' or '.' or '_' or '-' or ':') safe.Append(character);
            else safe.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
        if (text.Length > 240) safe.Append("...");
        return safe.ToString();
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new VerificationException(message);
    }
}
