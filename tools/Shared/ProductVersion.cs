// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

/// <summary>
/// The application version lives in PostgreSqlMcp.csproj &lt;Version&gt;. npm/package.json, its platform optionalDependencies and
/// server.json repeat it, and every release tag is v&lt;Version&gt;. This release line stays on 0.x.
/// </summary>
internal static partial class ProductVersion
{
    public static readonly string ProjectFile = Path.Combine("src", "PostgreSqlMcp", "PostgreSqlMcp.csproj");
    public static readonly string NpmManifest = Path.Combine("npm", "package.json");
    public const string ServerManifest = "server.json";

    // The tag grammar the release workflow has always accepted: <major>.<minor>.<patch> with an optional prerelease; no build metadata.
    private const string TagPattern = @"\Av[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?\z";

    public static bool IsReleaseTag(string tag) => ReleaseTag().IsMatch(tag);

    [GeneratedRegex(TagPattern, RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseTag();

    public static string Version(XDocument project) => project.Descendants("Version").Single().Value;

    public static string ReadProject(string root) => Version(XDocument.Load(Path.Combine(root, ProjectFile)));

    /// <summary>Every disagreement between the version sources and every 0.x policy violation; empty when consistent.</summary>
    public static IReadOnlyList<string> Inconsistencies(string root)
    {
        var problems = new List<string>();
        string version = ReadProject(root);
        if (!IsReleaseTag("v" + version))
        {
            problems.Add($"{ProjectFile} Version '{version}' is not <major>.<minor>.<patch>[-prerelease].");
            return problems;
        }
        string major = version[..version.IndexOf('.', StringComparison.Ordinal)];
        if (major != "0")
            problems.Add($"{ProjectFile} Version {version} has major {major}; this release line stays on 0.x.");

        using (var npm = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, NpmManifest))))
        {
            Expect(problems, NpmManifest + " version", Text(npm.RootElement, "version"), version);
            if (npm.RootElement.TryGetProperty("optionalDependencies", out JsonElement dependencies) && dependencies.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty dependency in dependencies.EnumerateObject())
                    Expect(problems, $"{NpmManifest} optionalDependencies {dependency.Name}", dependency.Value.ValueKind == JsonValueKind.String ? dependency.Value.GetString() : null, version);
            else
                problems.Add($"{NpmManifest} has no optionalDependencies object listing the platform packages.");
        }

        string server = Path.Combine(root, ServerManifest);
        if (!File.Exists(server))
        {
            problems.Add($"{ServerManifest} is missing.");
            return problems;
        }
        using var registry = JsonDocument.Parse(File.ReadAllText(server));
        Expect(problems, ServerManifest + " version", Text(registry.RootElement, "version"), version);
        if (registry.RootElement.TryGetProperty("packages", out JsonElement packages) && packages.ValueKind == JsonValueKind.Array && packages.GetArrayLength() > 0)
        {
            int index = 0;
            foreach (JsonElement package in packages.EnumerateArray())
            {
                string label = $"{ServerManifest} packages[{index.ToString(CultureInfo.InvariantCulture)}]" +
                    (package.ValueKind == JsonValueKind.Object && Text(package, "identifier") is { } identifier ? $" ({identifier})" : "") + " version";
                Expect(problems, label, package.ValueKind == JsonValueKind.Object ? Text(package, "version") : null, version);
                index++;
            }
        }
        else
            problems.Add($"{ServerManifest} has no packages array.");
        return problems;
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void Expect(List<string> problems, string source, string? actual, string version)
    {
        if (actual != version)
            problems.Add($"{source} is {(actual is null ? "missing" : "'" + actual + "'")}; expected {version} from {ProjectFile}.");
    }
}
