// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Collections;
using System.Globalization;
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
            if (args.Length == 0) throw new InvalidOperationException("Expected validate-tag, metadata, context, archives, sbom, github, npm, nuget, or require-secret command.");
            string command = args[0];
            string tag = Environment.GetEnvironmentVariable("RELEASE_TAG") ?? "";
            if (command == "require-secret")
            {
                if (args.Length != 2 || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(args[1])))
                    throw new InvalidOperationException("The required publishing environment credential is missing.");
                return 0;
            }
            bool publication = command is "github" or "npm" or "nuget";
            string root = publication ? Environment.CurrentDirectory : RepositoryRoot.Find() ?? throw new InvalidOperationException("Run release automation from the repository.");
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
                    string bom = Path.Combine(root, "artifacts/sbom/bom.json");
                    using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(bom)))
                    {
                        if (document.RootElement.GetProperty("bomFormat").GetString() != "CycloneDX" || document.RootElement.GetProperty("components").GetArrayLength() == 0)
                            throw new InvalidOperationException("Generated SBOM lacks application dependencies.");
                    }
                    // The checked-out commit's time (not the wall clock) keeps the SBOM byte-identical across rebuilds of the same source.
                    var commitTime = await CaptureAsync("git", ["log", "-1", "--format=%ct", "HEAD"], root);
                    if (commitTime.ExitCode != 0 || !long.TryParse(commitTime.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long commitSeconds))
                        throw new InvalidOperationException("Cannot read the checked-out commit time for the SBOM timestamp.");
                    await File.WriteAllTextAsync(bom, SbomNormalizer.Normalize(await File.ReadAllTextAsync(bom), DateTimeOffset.FromUnixTimeSeconds(commitSeconds)));
                    await ChecksumsAsync(root);
                    break;
                case "github":
                    if (tag != "v" + metadata.Version) throw new InvalidOperationException("Release tag/version mismatch.");
                    await PublishGitHubAsync(root, tag, metadata);
                    break;
                case "npm":
                    await StageNpmAsync(root, metadata.NpmName, metadata.Version, args.Length == 4);
                    break;
                case "nuget":
                    await PublishNuGetAsync(root, metadata.Version);
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
                if (rid == "win-x64") ReleaseArchive.CreateZip(temporary, archive);
                else ReleaseArchive.CreateTarGz(temporary, archive);
            }
            finally { Directory.Delete(temporary, true); }
        }
    }

    private static async Task ChecksumsAsync(string root)
    {
        var files = ArtifactFiles(root, ReadMetadata(root)).Order(StringComparer.Ordinal).ToArray();
        var checksums = new List<string>(files.Length);
        foreach (string file in files)
            checksums.Add(await Sha256Async(file) + "  " + Path.GetFileName(file));
        await File.WriteAllLinesAsync(Path.Combine(root, "artifacts/SHA256SUMS"), checksums);
    }

    private static async Task<string> Sha256Async(string file)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }

    private static string[] ArtifactFiles(string root, (string Version, string NpmName) metadata)
    {
        string[] archives = Rids.Select(rid => Path.Combine(root, "artifacts/archives", "postgresql-sharp-mcp-" + rid + (rid == "win-x64" ? ".zip" : ".tar.gz"))).ToArray();
        string nuget = Path.Combine(root, "artifacts/packages", "codegiveness.postgresql-sharp-mcp." + metadata.Version + ".nupkg");
        // Platform packages first: the main package's optionalDependencies point at them.
        string[] npm = NpmPackages(metadata.NpmName).Select(name => Path.Combine(root, "artifacts/packages", NpmPlatform.TarballName(name, metadata.Version))).ToArray();
        string sbom = Path.Combine(root, "artifacts/sbom/bom.json");
        string[] files = [.. archives, nuget, .. npm, sbom];
        if (files.Any(file => !File.Exists(file)))
            throw new InvalidOperationException("Expected five archives, the current NuGet package, the npm package and its five platform packages, and the application SBOM.");
        return files;
    }

    private static IEnumerable<string> NpmPackages(string main) => NpmPlatform.All.Select(platform => platform.PackageName(main)).Append(main);

    private static async Task PublishGitHubAsync(string root, string tag, (string Version, string NpmName) metadata)
    {
        string repo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? throw new InvalidOperationException("GITHUB_REPOSITORY is required.");
        string[] files = [.. ArtifactFiles(root, metadata), Path.Combine(root, "artifacts/SHA256SUMS")];
        string provenance = await ProvenanceBundleAsync(root, files);
        var local = new List<LocalAsset>();
        foreach (string file in files.Append(provenance))
            local.Add(new LocalAsset(Path.GetFileName(file), file, await Sha256Async(file)));
        var lookup = await CaptureAsync("gh", ["api", $"repos/{repo}/releases/tags/{tag}"], root);
        IReadOnlyList<RemoteAsset> existing = [];
        if (lookup.ExitCode == 0) existing = ReleaseAssets.Parse(lookup.Output);
        else
        {
            // Only a genuine 404 means absent; authorization/network errors must not create a second release.
            if (!lookup.Error.Contains("HTTP 404", StringComparison.Ordinal)) throw new InvalidOperationException("GitHub release lookup failed.");
            var create = new List<string> { "release", "create", tag, "--repo", repo, "--verify-tag", "--title", tag, "--generate-notes" };
            if (tag.Contains('-')) create.Add("--prerelease");
            await RunAsync("gh", create, root);
        }
        // A re-run never replaces a published asset: existing ones must be byte-identical, only missing ones are uploaded.
        var missing = await ReleaseAssets.PlanUploadsAsync(local, existing, async name =>
        {
            string directory = Directory.CreateTempSubdirectory("postgresql-release-asset-").FullName;
            try
            {
                await RunAsync("gh", ["release", "download", tag, "--repo", repo, "--pattern", name, "--dir", directory], root);
                return await Sha256Async(Path.Combine(directory, name));
            }
            finally { Directory.Delete(directory, true); }
        });
        if (missing.Count > 0)
            await RunAsync("gh", ["release", "upload", tag, "--repo", repo, .. missing.Select(asset => asset.Path)], root);
        await SummaryAsync($"GitHub Release {tag}: uploaded {missing.Count} missing asset(s); {local.Count - missing.Count} existing asset(s) already matched the built SHA-256 digests.\n");
    }

    private static async Task PublishNuGetAsync(string root, string version)
    {
        const string PackageId = "codegiveness.postgresql-sharp-mcp";
        string apiKey = Environment.GetEnvironmentVariable("NUGET_API_KEY") ?? "";
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("NUGET_API_KEY is required.");
        string package = Path.Combine(root, "artifacts/packages", PackageId + "." + version + ".nupkg");
        if (!File.Exists(package)) throw new InvalidOperationException("The current NuGet package is missing.");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        if (await NuGetRegistry.HasVersionAsync(http, PackageId, version))
        {
            // Versions are immutable: a duplicate is success only if it is this exact payload.
            string registry = Path.Combine(Directory.CreateTempSubdirectory("postgresql-nuget-").FullName, "registry.nupkg");
            try
            {
                await NuGetRegistry.DownloadAsync(http, PackageId, version, registry);
                var differences = NuGetRegistry.ComparePayload(package, registry);
                if (differences.Count > 0)
                    throw new InvalidOperationException($"NuGet {version} already exists with a different payload ({string.Join("; ", differences.Take(5))}); refusing to treat it as published.");
            }
            finally { Directory.Delete(Path.GetDirectoryName(registry)!, true); }
            await SummaryAsync($"NuGet {version} already exists with an identical payload (repository signature ignored); not republished.\n");
            return;
        }
        await RunAsync("dotnet", ["nuget", "push", package, "--source", "https://api.nuget.org/v3/index.json", "--api-key", apiKey], root);
        await SummaryAsync($"NuGet {version} pushed.\n");
    }

    // The attest job's Sigstore bundle is published so provenance can be verified from the release itself.
    // It must be one SLSA provenance statement whose subjects are exactly the uploaded assets.
    private static async Task<string> ProvenanceBundleAsync(string root, string[] files)
    {
        string directory = Path.Combine(root, "provenance");
        string[] bundles = Directory.Exists(directory) ? Directory.GetFiles(directory) : [];
        if (bundles.Length != 1) throw new InvalidOperationException("Expected exactly one attestation bundle in provenance/.");
        using var bundle = JsonDocument.Parse(await File.ReadAllTextAsync(bundles[0]));
        var envelope = bundle.RootElement.GetProperty("dsseEnvelope");
        if (!(bundle.RootElement.GetProperty("mediaType").GetString() ?? "").StartsWith("application/vnd.dev.sigstore.bundle", StringComparison.Ordinal) ||
            envelope.GetProperty("payloadType").GetString() != "application/vnd.in-toto+json")
            throw new InvalidOperationException("Attestation bundle is not a Sigstore in-toto bundle.");
        using var statement = JsonDocument.Parse(Convert.FromBase64String(envelope.GetProperty("payload").GetString() ?? ""));
        if (statement.RootElement.GetProperty("predicateType").GetString() != "https://slsa.dev/provenance/v1")
            throw new InvalidOperationException("Attestation bundle does not contain SLSA provenance.");
        var attested = statement.RootElement.GetProperty("subject").EnumerateArray()
            .Select(subject => subject.GetProperty("name").GetString() + "  " + subject.GetProperty("digest").GetProperty("sha256").GetString())
            .Order(StringComparer.Ordinal);
        var published = new List<string>(files.Length);
        foreach (string file in files)
            published.Add(Path.GetFileName(file) + "  " + await Sha256Async(file));
        if (!attested.SequenceEqual(published.Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("Provenance subjects do not match the release assets.");
        string asset = Path.Combine(root, "artifacts/provenance.sigstore.json");
        File.Copy(bundles[0], asset, overwrite: true);
        return asset;
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
            // Platform packages are staged before the main package; a failure stops before the main package is staged.
            foreach (string package in NpmPackages(name))
                await StageNpmPackageAsync(root, package, version, dryRun, isolated, environment, configuration);
            if (!dryRun)
                await SummaryAsync("Approve the staged platform packages before the main package, so it never resolves missing optional dependencies.\n");
        }
        finally { Directory.Delete(isolated, recursive: true); }
    }

    private static async Task StageNpmPackageAsync(string root, string name, string version, bool dryRun, string isolated,
        Dictionary<string, string?> environment, string[] configuration)
    {
        var lookup = await CaptureAsync("npm", ["view", name + "@" + version, "version", "--json",
            "--registry", "https://registry.npmjs.org/", .. configuration], isolated, environment);
        if (lookup.ExitCode == 0)
        {
            using var found = JsonDocument.Parse(lookup.Output);
            // npm <= 11 prints a JSON string; npm 12 prints a one-element array for an exact version.
            JsonElement reported = found.RootElement;
            if (reported.ValueKind == JsonValueKind.Array && reported.GetArrayLength() == 1) reported = reported[0];
            if (reported.ValueKind != JsonValueKind.String || reported.GetString() != version)
                throw new InvalidOperationException($"Unexpected npm registry version response for {name}.");
            await SummaryAsync($"npm {name}@{version} already exists; immutable version was not republished or staged.\n");
            return;
        }
        bool absent = false;
        try
        {
            using var error = JsonDocument.Parse(lookup.Output);
            absent = error.RootElement.GetProperty("error").GetProperty("code").GetString() == "E404";
        }
        catch (JsonException) { }
        if (!absent) throw new InvalidOperationException($"npm lookup failed for {name}; check registry availability.");
        string package = Path.GetFullPath(Path.Combine(root, "artifacts/packages", NpmPlatform.TarballName(name, version)));
        var arguments = new List<string> { "stage", "publish", package, "--ignore-scripts", "--access", "public",
            "--provenance", "--registry", "https://registry.npmjs.org/", "--tag", version.Contains('-') ? "next" : "latest" };
        arguments.AddRange(configuration);
        if (dryRun) arguments.Add("--dry-run");
        await RunAsync("npm", arguments, isolated, environment);
        await SummaryAsync(dryRun
            ? $"npm {name}@{version} staging dry-run completed; no stage was submitted and nothing was published.\n"
            : $"npm {name}@{version} staged with provenance requested, pending human Windows 2FA approval; not publicly published.\n");
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

    // Child output is echoed (CI needs build/publish diagnostics) but with the values of credential-looking
    // environment variables replaced, so a tool that echoes its token cannot leak it into logs.
    private static async Task RunAsync(string executable, IEnumerable<string> args, string root, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var result = await ProcessRunner.RunAsync(Spec(executable, args, root, environment, OutputPolicy.Redact(CredentialValues())));
        if (result.ExitCode != 0) throw new InvalidOperationException($"{executable} failed (exit {result.ExitCode}); its output above has known credential values redacted.");
    }

    // Callers parse the output, which is never echoed.
    private static Task<ProcessResult> CaptureAsync(string executable, IEnumerable<string> args, string root, IReadOnlyDictionary<string, string?>? environment = null) =>
        ProcessRunner.RunAsync(Spec(executable, args, root, environment, OutputPolicy.Capture));

    private static ProcessSpec Spec(string executable, IEnumerable<string> args, string root, IReadOnlyDictionary<string, string?>? environment, OutputPolicy output) =>
        new(executable, args) { WorkingDirectory = root, Environment = environment, Timeout = TimeSpan.FromMinutes(15), Output = output };

    private static IEnumerable<string?> CredentialValues() => Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
        .Where(variable => new[] { "TOKEN", "SECRET", "PASSWORD", "KEY", "CREDENTIAL" }.Any(marker => ((string)variable.Key).Contains(marker, StringComparison.OrdinalIgnoreCase)))
        .Select(variable => (string?)variable.Value)
        .Where(value => value?.Length >= 8);
}
