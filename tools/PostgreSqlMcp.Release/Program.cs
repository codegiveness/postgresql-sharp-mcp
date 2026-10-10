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
    private const string TagPattern = @"\Av[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?\z";

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new InvalidOperationException("Expected validate-tag, metadata, context, archives, sbom, github, npm, or require-secret command.");
            string command = args[0];
            string tag = Environment.GetEnvironmentVariable("RELEASE_TAG") ?? "";
            if (command == "require-secret")
            {
                if (args.Length != 2 || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(args[1])))
                    throw new InvalidOperationException("The required publishing environment credential is missing.");
                return 0;
            }
            bool publication = command is "github" or "npm";
            string root = publication ? Environment.CurrentDirectory : FindRoot();
            if (command == "validate-tag")
            {
                await ValidateTagAsync(root, tag);
                return 0;
            }
            var metadata = publication ? ReadPublicationContext(args) : ReadMetadata(root);
            switch (command)
            {
                case "metadata":
                case "context":
                    if (!Regex.IsMatch(tag, TagPattern, RegexOptions.CultureInvariant) || tag != "v" + metadata.Version)
                        throw new InvalidOperationException("Release tag must match the application and npm package versions.");
                    if (command == "context")
                    {
                        await WritePublicationContextAsync(root, metadata.Version, metadata.NpmName);
                        Console.WriteLine("Wrote internal publication context.");
                        break;
                    }
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
                    await PublishGitHubAsync(root, tag, metadata);
                    break;
                case "npm":
                    await StageNpmAsync(root, metadata.NpmName, metadata.Version, args.Length == 4);
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

    private static async Task ValidateTagAsync(string root, string tag)
    {
        if (!Regex.IsMatch(tag, TagPattern, RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Release tag must be v<major>.<minor>.<patch> with an optional prerelease suffix.");
        var revision = await CaptureAsync("git", ["rev-parse", "--verify", "refs/tags/" + tag + "^{commit}"], root);
        if (revision.ExitCode != 0)
            throw new InvalidOperationException("Release tag must already exist and point to a commit.");
        string commit = revision.Output.Trim();
        var ancestry = await CaptureAsync("git", ["merge-base", "--is-ancestor", commit, "refs/remotes/origin/main"], root);
        if (ancestry.ExitCode != 0)
            throw new InvalidOperationException("Release tag must point to a commit on origin/main.");
        await OutputAsync("commit", commit);
        Console.WriteLine($"Verified release tag {tag} is on origin/main at {commit}.");
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

    private static async Task WritePublicationContextAsync(string root, string version, string npmName)
    {
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        await File.WriteAllTextAsync(Path.Combine(root, "artifacts/publication-context.json"),
            JsonSerializer.Serialize(new { version, npmName }));
    }

    private static (string Version, string NpmName) ReadPublicationContext(string[] args)
    {
        if (!(args.Length == 3 || (args.Length == 4 && args[0] == "npm" && args[3] == "--dry-run")) || args[1] != "--context")
            throw new InvalidOperationException("Publishing requires --context <publication-context.json>; npm also accepts --dry-run.");
        using var document = JsonDocument.Parse(File.ReadAllText(args[2]));
        string version = document.RootElement.GetProperty("version").GetString() ?? "";
        string name = document.RootElement.GetProperty("npmName").GetString() ?? "";
        if (!Regex.IsMatch("v" + version, TagPattern, RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(name, @"\A(?:@[a-z0-9][a-z0-9._-]*/)?[a-z0-9][a-z0-9._-]*\z", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Publication context must contain a valid release version and npm package name.");
        return (version, name);
    }

    private static async Task ArchivesAsync(string root)
    {
        string output = Path.Combine(root, "artifacts/archives");
        Directory.CreateDirectory(output);
        foreach (string rid in Rids)
        {
            string temporary = Directory.CreateTempSubdirectory("postgresql-release-").FullName;
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
        var files = ArtifactFiles(root, ReadMetadata(root)).Order(StringComparer.Ordinal).ToArray();
        var checksums = new List<string>(files.Length);
        foreach (string file in files)
        {
            await using var stream = File.OpenRead(file);
            string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
            checksums.Add(hash + "  " + Path.GetFileName(file));
        }
        await File.WriteAllLinesAsync(Path.Combine(root, "artifacts/SHA256SUMS"), checksums);
    }

    private static string[] ArtifactFiles(string root, (string Version, string NpmName) metadata)
    {
        string[] archives = Rids.Select(rid => Path.Combine(root, "artifacts/archives", "postgresql-sharp-mcp-" + rid + (rid == "win-x64" ? ".zip" : ".tar.gz"))).ToArray();
        string nuget = Path.Combine(root, "artifacts/packages", "codegiveness.postgresql-sharp-mcp." + metadata.Version + ".nupkg");
        string npm = Path.Combine(root, "artifacts/packages", metadata.NpmName.Replace("@", "").Replace("/", "-") + "-" + metadata.Version + ".tgz");
        string sbom = Path.Combine(root, "artifacts/sbom/bom.json");
        string[] files = [.. archives, nuget, npm, sbom];
        if (files.Any(file => !File.Exists(file)))
            throw new InvalidOperationException("Expected five archives, the current NuGet/npm packages and the application SBOM.");
        return files;
    }

    private static async Task PublishGitHubAsync(string root, string tag, (string Version, string NpmName) metadata)
    {
        string repo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? throw new InvalidOperationException("GITHUB_REPOSITORY is required.");
        string[] files = ArtifactFiles(root, metadata);
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

    private static async Task StageNpmAsync(string root, string name, string version, bool dryRun)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_URL")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_TOKEN")))
            throw new InvalidOperationException("npm staging requires GitHub Actions OIDC with id-token: write; local credentials are not supported.");

        // npm falls back to configured credentials when OIDC fails. Isolate all config sources
        // and remove inherited credentials so a failed exchange cannot stage with a static token.
        string isolated = Directory.CreateTempSubdirectory("postgresql-npm-oidc-").FullName;
        try
        {
            var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in Environment.GetEnvironmentVariables().Keys)
                if (key.StartsWith("NPM_CONFIG_", StringComparison.OrdinalIgnoreCase))
                    environment[key] = null;
            environment["NODE_AUTH_TOKEN"] = null;
            environment["NPM_TOKEN"] = null;
            environment["NPM_ID_TOKEN"] = null;
            string[] configuration = ["--prefix", isolated, "--userconfig", Path.Combine(isolated, "user.npmrc"),
                "--globalconfig", Path.Combine(isolated, "global.npmrc")];
            var lookup = await CaptureAsync("npm", ["view", name + "@" + version, "version", "--json",
                "--registry", "https://registry.npmjs.org/", .. configuration], isolated, environment);
            if (lookup.ExitCode == 0)
            {
                using var found = JsonDocument.Parse(lookup.Output);
                if (found.RootElement.GetString() != version) throw new InvalidOperationException("Unexpected npm registry version response.");
                await SummaryAsync($"npm {version} already exists; immutable version was not republished or staged.\n");
                return;
            }
            bool absent = false;
            try
            {
                using var error = JsonDocument.Parse(lookup.Output);
                absent = error.RootElement.GetProperty("error").GetProperty("code").GetString() == "E404";
            }
            catch (JsonException) { }
            if (!absent) throw new InvalidOperationException("npm lookup failed; check registry availability.");
            string package = Path.GetFullPath(Path.Combine(root, "artifacts/packages", name.Replace("@", "").Replace("/", "-") + "-" + version + ".tgz"));
            var arguments = new List<string> { "stage", "publish", package, "--ignore-scripts", "--access", "public",
                "--provenance", "--registry", "https://registry.npmjs.org/", "--tag", version.Contains('-') ? "next" : "latest" };
            arguments.AddRange(configuration);
            if (dryRun) arguments.Add("--dry-run");
            await RunAsync("npm", arguments, isolated, environment);
            await SummaryAsync(dryRun
                ? $"npm {version} staging dry-run completed; no stage was submitted and nothing was published.\n"
                : $"npm {version} staged with provenance requested, pending human Windows 2FA approval; not publicly published.\n");
        }
        finally { Directory.Delete(isolated, recursive: true); }
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

    private static async Task RunAsync(string executable, IEnumerable<string> args, string root, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var result = await CaptureAsync(executable, args, root, environment);
        Console.Write(result.Output);
        Console.Error.Write(result.Error);
        if (result.ExitCode != 0) throw new InvalidOperationException(executable + " failed; see redacted runner diagnostics.");
    }

    private static async Task<(int ExitCode, string Output, string Error)> CaptureAsync(string executable, IEnumerable<string> args, string root, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in args) start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var pair in environment)
                if (pair.Value is null) start.Environment.Remove(pair.Key);
                else start.Environment[pair.Key] = pair.Value;
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Unable to start release tool.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        Task<string> output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(deadline.Token);
        Task drains = Task.WhenAll(output, error);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            await drains;
            return (process.ExitCode, output.Result, error.Result);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    await process.WaitForExitAsync();
                }
            }
            finally
            {
                await deadline.CancelAsync();
                // Timed-out captures still own their drains until both have finished.
                await drains.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }
}
