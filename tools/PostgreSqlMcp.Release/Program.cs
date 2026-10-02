using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

return await Release.RunAsync(args);

internal static class Release
{
    private static readonly string[] Rids = ["linux-x64", "linux-arm64", "win-x64", "osx-x64", "osx-arm64"];

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new InvalidOperationException("Expected metadata, archives, sbom, github, npm, or require-secret command.");
            string root = FindRoot();
            string command = args[0];
            string tag = Environment.GetEnvironmentVariable("RELEASE_TAG") ?? "";
            var metadata = ReadMetadata(root);
            switch (command)
            {
                case "metadata":
                    if (!Regex.IsMatch(tag, @"^v[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$", RegexOptions.CultureInvariant) || tag != "v" + metadata.Version)
                        throw new InvalidOperationException("Release tag must match the application and npm package versions.");
                    await OutputAsync("tag", tag);
                    await OutputAsync("version", metadata.Version);
                    await OutputAsync("npm_name", metadata.NpmName);
                    Console.WriteLine($"Verified release {tag} and both package versions.");
                    break;
                case "archives":
                    await ArchivesAsync(root);
                    break;
                case "sbom":
                    await RunAsync("dotnet", ["tool", "restore"], root);
                    await RunAsync("dotnet", ["dotnet-CycloneDX", "src/PostgreSqlMcp/PostgreSqlMcp.csproj", "--exclude-dev", "--disable-package-restore", "--output-format", "Json", "--output", "artifacts/sbom", "--filename", "bom.json", "--set-name", "codegiveness.postgresql-sharp-mcp", "--set-version", metadata.Version], root);
                    using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "artifacts/sbom/bom.json"))))
                    {
                        if (document.RootElement.GetProperty("bomFormat").GetString() != "CycloneDX" || document.RootElement.GetProperty("components").GetArrayLength() == 0)
                            throw new InvalidOperationException("Generated SBOM lacks application dependencies.");
                    }
                    await ChecksumsAsync(root);
                    break;
                case "github":
                    if (tag != "v" + metadata.Version) throw new InvalidOperationException("Release tag/version mismatch.");
                    await PublishGitHubAsync(root, tag);
                    break;
                case "npm":
                    await PublishNpmAsync(root, metadata.NpmName, metadata.Version);
                    break;
                case "require-secret":
                    if (args.Length != 2 || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(args[1])))
                        throw new InvalidOperationException("The required publishing environment credential is missing.");
                    break;
                default:
                    throw new InvalidOperationException("Unknown release command.");
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Release operation failed: {error.Message}");
            return 1;
        }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "postgresql-sharp-mcp.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run release automation from the repository.");
    }

    private static (string Version, string NpmName) ReadMetadata(string root)
    {
        var project = XDocument.Load(Path.Combine(root, "src/PostgreSqlMcp/PostgreSqlMcp.csproj"));
        string version = project.Descendants("Version").Single().Value;
        using var npm = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "npm/package.json")));
        if (npm.RootElement.GetProperty("version").GetString() != version) throw new InvalidOperationException("Application and npm versions differ.");
        return (version, npm.RootElement.GetProperty("name").GetString()!);
    }

    private static async Task ArchivesAsync(string root)
    {
        string output = Path.Combine(root, "artifacts/archives");
        Directory.CreateDirectory(output);
        foreach (string rid in Rids)
        {
            string temporary = Path.Combine(Path.GetTempPath(), "postgresql-release-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try
            {
                await RunAsync("dotnet", ["publish", "src/PostgreSqlMcp/PostgreSqlMcp.csproj", "-c", "Release", "-r", rid, "--self-contained", "-p:RestoreLockedMode=true", "-o", temporary], root);
                string archive = Path.Combine(output, "postgresql-sharp-mcp-" + rid + (rid == "win-x64" ? ".zip" : ".tar.gz"));
                if (File.Exists(archive)) File.Delete(archive);
                if (rid == "win-x64") ZipFile.CreateFromDirectory(temporary, archive);
                else
                {
                    await using var file = File.Create(archive);
                    await using var gzip = new GZipStream(file, CompressionLevel.Optimal);
                    using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);
                    foreach (string path in Directory.EnumerateFiles(temporary, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                    {
                        using var data = File.OpenRead(path);
                        var entry = new PaxTarEntry(TarEntryType.RegularFile, Path.GetRelativePath(temporary, path).Replace(Path.DirectorySeparatorChar, '/'))
                        {
                            Uid = 0, Gid = 0, UserName = "", GroupName = "",
                            ModificationTime = DateTimeOffset.UnixEpoch,
                            Mode = OperatingSystem.IsWindows()
                                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                                : File.GetUnixFileMode(path),
                            DataStream = data
                        };
                        tar.WriteEntry(entry);
                    }
                }
            }
            finally { Directory.Delete(temporary, true); }
        }
    }

    private static async Task ChecksumsAsync(string root)
    {
        var files = ArtifactFiles(root).Order(StringComparer.Ordinal).ToArray();
        var checksums = new List<string>(files.Length);
        foreach (string file in files)
        {
            await using var stream = File.OpenRead(file);
            string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
            checksums.Add(hash + "  " + Path.GetFileName(file));
        }
        await File.WriteAllLinesAsync(Path.Combine(root, "artifacts/SHA256SUMS"), checksums);
    }

    private static string[] ArtifactFiles(string root)
    {
        var metadata = ReadMetadata(root);
        string[] archives = Rids.Select(rid => Path.Combine(root, "artifacts/archives", "postgresql-sharp-mcp-" + rid + (rid == "win-x64" ? ".zip" : ".tar.gz"))).ToArray();
        string nuget = Path.Combine(root, "artifacts/packages", "codegiveness.postgresql-sharp-mcp." + metadata.Version + ".nupkg");
        string npm = Path.Combine(root, "artifacts/packages", metadata.NpmName.Replace("@", "").Replace("/", "-") + "-" + metadata.Version + ".tgz");
        string sbom = Path.Combine(root, "artifacts/sbom/bom.json");
        string[] files = [.. archives, nuget, npm, sbom];
        if (files.Any(file => !File.Exists(file)))
            throw new InvalidOperationException("Expected five archives, the current NuGet/npm packages and the application SBOM.");
        return files;
    }

    private static async Task PublishGitHubAsync(string root, string tag)
    {
        string repo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? throw new InvalidOperationException("GITHUB_REPOSITORY is required.");
        string[] files = ArtifactFiles(root);
        var lookup = await CaptureAsync("gh", ["api", $"repos/{repo}/releases/tags/{tag}"], root);
        if (lookup.ExitCode != 0)
        {
            // Only a genuine 404 means absent; authorization/network errors must not create a second release.
            if (!lookup.Error.Contains("HTTP 404", StringComparison.Ordinal)) throw new InvalidOperationException("GitHub release lookup failed.");
            var create = new List<string> { "release", "create", tag, "--repo", repo, "--verify-tag", "--title", tag, "--generate-notes" };
            if (tag.Contains('-')) create.Add("--prerelease");
            await RunAsync("gh", create, root);
        }
        await RunAsync("gh", ["release", "upload", tag, "--repo", repo, "--clobber", .. files, Path.Combine(root, "artifacts/SHA256SUMS")], root);
        await SummaryAsync($"GitHub Release {tag}: archives, packages, checksums and application SBOM uploaded.\n");
    }

    private static async Task PublishNpmAsync(string root, string name, string version)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NODE_AUTH_TOKEN"))) throw new InvalidOperationException("NPM_TOKEN is missing.");
        var lookup = await CaptureAsync("npm", ["view", name + "@" + version, "version", "--json", "--registry", "https://registry.npmjs.org/"], root);
        if (lookup.ExitCode == 0)
        {
            using var found = JsonDocument.Parse(lookup.Output);
            if (found.RootElement.GetString() != version) throw new InvalidOperationException("Unexpected npm registry version response.");
            await SummaryAsync($"npm {version} already exists; immutable version was not republished.\n");
            return;
        }
        bool absent = false;
        try
        {
            using var error = JsonDocument.Parse(lookup.Output);
            absent = error.RootElement.GetProperty("error").GetProperty("code").GetString() == "E404";
        }
        catch (JsonException) { }
        if (!absent) throw new InvalidOperationException("npm lookup failed; check registry availability and publishing-token scope.");
        string package = Path.Combine(root, "artifacts/packages", name.Replace("@", "").Replace("/", "-") + "-" + version + ".tgz");
        string configuration = Path.Combine(Path.GetTempPath(), "postgresql-publish-" + Guid.NewGuid().ToString("N") + ".npmrc");
        try
        {
            await File.WriteAllTextAsync(configuration, "//registry.npmjs.org/:_authToken=${NODE_AUTH_TOKEN}\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(configuration, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await RunAsync("npm", ["publish", package, "--ignore-scripts", "--access", "public", "--provenance", "--registry", "https://registry.npmjs.org/", "--tag", version.Contains('-') ? "next" : "latest"], root,
                new Dictionary<string, string> { ["NPM_CONFIG_USERCONFIG"] = configuration });
            await SummaryAsync($"Published npm {version} with provenance requested; verify the actual registry version and provenance.\n");
        }
        finally { File.Delete(configuration); }
    }

    private static async Task OutputAsync(string name, string value)
    {
        string? output = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
        if (output is not null) await File.AppendAllTextAsync(output, name + "=" + value + "\n");
    }

    private static async Task SummaryAsync(string text)
    {
        string? summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (summary is not null) await File.AppendAllTextAsync(summary, text);
        Console.Write(text);
    }

    private static async Task RunAsync(string executable, IEnumerable<string> args, string root, IReadOnlyDictionary<string, string>? environment = null)
    {
        var result = await CaptureAsync(executable, args, root, environment);
        Console.Write(result.Output);
        Console.Error.Write(result.Error);
        if (result.ExitCode != 0) throw new InvalidOperationException(executable + " failed; see redacted runner diagnostics.");
    }

    private static async Task<(int ExitCode, string Output, string Error)> CaptureAsync(string executable, IEnumerable<string> args, string root, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in args) start.ArgumentList.Add(argument);
        if (environment is not null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start release tool.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw; }
        return (process.ExitCode, await output, await error);
    }
}
