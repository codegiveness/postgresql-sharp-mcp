// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Xml.Linq;

namespace PostgreSqlMcp.Verify;

/// <summary>
/// Measures how much of the shipped server the integration scenarios execute, with the FLOSS coverlet console tool
/// pinned in .config/dotnet-tools.json. Coverlet instruments the built server assemblies in place, runs
/// <c>integration --no-build</c> as its target (every scenario launches the real server as a child process), and
/// restores the originals afterwards. Each server process appends its hits when it exits, so the report merges all of them.
/// </summary>
internal static class Coverage
{
    // Statement coverage here is coverlet's line coverage: one unit per IL sequence point with a source line, which is how it counts statements.
    private static readonly string[] Assemblies = ["PostgreSqlMcp", "PostgreSqlMcp.Core", "PostgreSqlMcp.Tools"];
    private const string CoberturaFile = "coverage.cobertura.xml";

    public static async Task RunAsync(string root, string[] options)
    {
        string output = Path.Combine(root, "artifacts", "coverage");
        string? threshold = null;
        for (int i = 0; i < options.Length;)
        {
            string option = options[i++];
            Check.That(i < options.Length, "Missing coverage option value.");
            string value = options[i++];
            switch (option)
            {
                case "--output": output = Path.GetFullPath(value); break;
                case "--threshold":
                    Check.That(double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent) && percent is > 0 and <= 100,
                        "--threshold must be a line coverage percentage above 0 and at most 100.");
                    threshold = percent.ToString(CultureInfo.InvariantCulture);
                    break;
                default: throw new VerificationException("Unknown coverage option.");
            }
        }

        string solution = Path.Combine(root, "postgresql-sharp-mcp.slnx");
        // The server output directory holds all three assemblies (project references are copied locally), so one directory is instrumented.
        string server = Path.Combine(root, "src", "PostgreSqlMcp", "bin", "Release", "net10.0");
        await Run(new("dotnet", "build", solution, "-c", "Release"), root, TimeSpan.FromMinutes(5));
        await Run(new("dotnet", "tool", "restore"), root, TimeSpan.FromMinutes(5));
        if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        Directory.CreateDirectory(output);

        string[] arguments =
        [
            "tool", "run", "coverlet", server,
            "--target", "dotnet",
            "--targetargs", $"\"{typeof(Coverage).Assembly.Location}\" integration --no-build",
            "--format", "cobertura", "--output", output + Path.DirectorySeparatorChar,
            // Release builds map sources to deterministic /_/ paths; without this coverlet finds no files on disk and skips every module.
            "--exclude-assemblies-without-sources", "None",
            // The only exclusion: compiler/source-generator output under obj (the [GeneratedRegex] implementations), which nobody writes or reviews.
            "--exclude-by-file", "**/obj/**",
            "--include", .. Assemblies.Select(name => $"[{name}]*")
        ];
        if (threshold is not null) arguments = [.. arguments, "--threshold", threshold, "--threshold-type", "line", "--threshold-stat", "total"];
        ProcessResult result = await Run(new("dotnet", arguments), root, TimeSpan.FromMinutes(45), expected: null);
        // Coverlet writes the report before it evaluates --threshold, so a threshold failure still leaves the artifact to inspect.
        string report = Path.Combine(output, CoberturaFile);
        Check.That(File.Exists(report), "Coverage run produced no Cobertura report.");
        Summarize(report);
        Check.That(result.ExitCode == 0, $"Coverage run failed with status {result.ExitCode}: a scenario failed or line coverage is below the requested threshold.");
        Console.WriteLine($"Coverage report: {output}");
    }

    private static async Task<ProcessResult> Run(Command command, string directory, TimeSpan timeout, int? expected = 0)
    {
        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(new ProcessSpec(command.File, command.Arguments)
            {
                Timeout = timeout, Output = OutputPolicy.Inherit, WorkingDirectory = directory
            });
        }
        catch (TimeoutException) { throw new VerificationException($"{command.File} {command.Arguments[0]} exceeded its deadline."); }
        if (expected is not null)
            Check.That(result.ExitCode == expected, $"{command.File} {command.Arguments[0]} exited with status {result.ExitCode}; expected {expected}.");
        return result;
    }

    /// <summary>Lists the files with the most unexecuted lines and their wholly unexecuted methods, so the next scenario targets real gaps.</summary>
    private static void Summarize(string cobertura)
    {
        XDocument report = XDocument.Load(cobertura);
        foreach (string name in Assemblies)
            Check.That(report.Descendants("package").Where(package => (string?)package.Attribute("name") == name)
                .Descendants("line").Any(line => (int)line.Attribute("hits")! > 0),
                $"The coverage report has no executed lines for {name}: it was not instrumented or no server process flushed its hits.");
        var files = new Dictionary<string, (HashSet<int> Lines, HashSet<int> Covered, SortedSet<string> Dead)>(StringComparer.Ordinal);
        foreach (XElement type in report.Descendants("class"))
        {
            string file = (string?)type.Attribute("filename") ?? "?";
            if (!files.TryGetValue(file, out var entry)) files[file] = entry = ([], [], []);
            foreach (XElement method in type.Descendants("method"))
            {
                var lines = method.Elements("lines").Elements("line").ToArray();
                if (lines.Length > 0 && lines.All(line => (int?)line.Attribute("hits") == 0))
                    entry.Dead.Add($"{(string?)type.Attribute("name")}.{(string?)method.Attribute("name")}");
            }
            foreach (XElement line in type.Elements("lines").Elements("line"))
            {
                int number = (int)line.Attribute("number")!;
                entry.Lines.Add(number);
                if ((int)line.Attribute("hits")! > 0) entry.Covered.Add(number);
            }
        }
        Console.WriteLine("Least-covered files (unexecuted lines of lines):");
        foreach (var (file, entry) in files.OrderByDescending(pair => pair.Value.Lines.Count - pair.Value.Covered.Count).Take(10))
        {
            int missed = entry.Lines.Count - entry.Covered.Count;
            if (missed == 0) break;
            Console.WriteLine($"  {file}: {missed} of {entry.Lines.Count}");
            foreach (string method in entry.Dead.Take(8)) Console.WriteLine($"    never run: {method}");
            if (entry.Dead.Count > 8) Console.WriteLine($"    ... {entry.Dead.Count - 8} more never-run methods");
        }
    }
}
