// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PostgreSqlMcp.Verify;

/// <summary>Offline regressions for release re-run safety and archive reproducibility; no network, no publication.</summary>
internal static class ReleaseRegressions
{
    public static void Run()
    {
        AssetPlanning().GetAwaiter().GetResult();
        ArchiveReproducibility();
        NuGetPayloadComparison();
        ToolPackageNames();
        SbomReproducibility();
        TagGrammar();
        VersionConsistency();
        RequiredCheckEvaluation();
        RequiredCheckPolling().GetAwaiter().GetResult();
        PackReproducibility().GetAwaiter().GetResult();
    }

    // The pointer package names the RID packages, so a registry must hold them first: publication order is every RID package, then the pointer.
    private static void ToolPackageNames()
    {
        string[] names = [.. NuGetTool.FileNames("tool", "1.2.3")];
        Check.That(names.Length == 6 && names[^1] == "tool.1.2.3.nupkg" && names[..^1].All(name => Regex.IsMatch(name, @"\Atool\.(win|linux|osx)-(x64|arm64)\.1\.2\.3\.nupkg\z")),
            "The .NET tool must be one pointer package, published last, plus one package per RID.");
        Check.That(names.Distinct().Count() == names.Length && NuGetTool.PackageIds("tool").Count() == names.Length, "Tool package ids must be unique and match the file names.");
    }

    private static LocalAsset Built(string name, string content) =>
        new(name, "/unused/" + name, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))));

    private static string Digest(LocalAsset asset) => "sha256:" + asset.Sha256;

    private static async Task Fails(string description, Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new VerificationException("Release regression did not fail closed: " + description);
    }

    private static async Task AssetPlanning()
    {
        LocalAsset a = Built("a.tar.gz", "alpha"), b = Built("b.zip", "bravo"), sums = Built("SHA256SUMS", "sums");
        LocalAsset[] local = [a, b, sums];
        Task<string> NoDownload(string name) => throw new VerificationException($"Unexpected download of {name}.");

        var none = await ReleaseAssets.PlanUploadsAsync(local, [], NoDownload);
        Check.That(none.Count == 3, "A fresh release must upload every built asset.");

        var partial = await ReleaseAssets.PlanUploadsAsync(local, [new("a.tar.gz", "uploaded", Digest(a))], NoDownload);
        Check.That(partial.Select(asset => asset.Name).SequenceEqual(["b.zip", "SHA256SUMS"]), "A re-run must upload only the missing assets.");

        var complete = await ReleaseAssets.PlanUploadsAsync(local,
            [new("a.tar.gz", "uploaded", Digest(a)), new("b.zip", "uploaded", "sha256:" + b.Sha256.ToUpperInvariant()), new("SHA256SUMS", "uploaded", Digest(sums))], NoDownload);
        Check.That(complete.Count == 0, "Identical existing assets must not be uploaded again.");

        await Fails("different bytes under an existing name", () => ReleaseAssets.PlanUploadsAsync(local, [new("b.zip", "uploaded", Digest(Built("b.zip", "other")))], NoDownload));
        await Fails("mismatch next to matching and missing assets", () => ReleaseAssets.PlanUploadsAsync(local,
            [new("a.tar.gz", "uploaded", Digest(a)), new("SHA256SUMS", "uploaded", Digest(Built("SHA256SUMS", "other")))], NoDownload));
        await Fails("incomplete upload", () => ReleaseAssets.PlanUploadsAsync(local, [new("a.tar.gz", "starter", Digest(a))], NoDownload));

        // Assets without a SHA-256 API digest are downloaded and hashed.
        var downloaded = new List<string>();
        Task<string> Download(string name) { downloaded.Add(name); return Task.FromResult(name == "a.tar.gz" ? a.Sha256 : Built(name, "tampered").Sha256); }
        var viaDownload = await ReleaseAssets.PlanUploadsAsync([a], [new("a.tar.gz", "uploaded", null)], Download);
        Check.That(viaDownload.Count == 0 && downloaded.SequenceEqual(["a.tar.gz"]), "A missing digest must fall back to hashing the downloaded asset.");
        await Fails("tampered asset without API digest", () => ReleaseAssets.PlanUploadsAsync([b], [new("b.zip", "uploaded", "sha1:abc")], Download));

        var parsed = ReleaseAssets.Parse("""{"assets":[{"name":"x","state":"uploaded","digest":"sha256:ab"},{"name":"y","state":"uploaded","digest":null},{"name":"z","state":"starter"}]}""");
        Check.That(parsed.Count == 3 && parsed[0].Digest == "sha256:ab" && parsed[1].Digest is null && parsed[2].State == "starter" && parsed[2].Digest is null,
            "GitHub release asset JSON was parsed incorrectly.");
    }

    private static void ArchiveReproducibility()
    {
        using TemporaryDirectory temporary = new();
        string source = Directory.CreateDirectory(Path.Combine(temporary.Path, "payload")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "zeta.txt"), "last");
        File.WriteAllText(Path.Combine(source, "alpha.txt"), "first");
        File.WriteAllText(Path.Combine(source, "sub", "nested.txt"), "nested");

        string first = Path.Combine(temporary.Path, "first.zip"), second = Path.Combine(temporary.Path, "second.zip");
        ReleaseArchive.CreateZip(source, first);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, new DateTime(2031, 5, 17, 3, 4, 6, DateTimeKind.Utc));
        ReleaseArchive.CreateZip(source, second);
        Check.That(File.ReadAllBytes(first).AsSpan().SequenceEqual(File.ReadAllBytes(second)),
            "Zip archives must not depend on on-disk modification times.");

        using (ZipArchive zip = ZipFile.OpenRead(first))
            Check.That(zip.Entries.Select(entry => entry.FullName).SequenceEqual(["alpha.txt", "sub/nested.txt", "zeta.txt"])
                && zip.Entries.All(entry => entry.LastWriteTime.DateTime == ReleaseArchive.ZipTimestamp.DateTime),
                "Zip entries must be sorted with the fixed timestamp.");

        File.WriteAllText(Path.Combine(source, "alpha.txt"), "changed");
        string third = Path.Combine(temporary.Path, "third.zip");
        ReleaseArchive.CreateZip(source, third);
        Check.That(!File.ReadAllBytes(first).AsSpan().SequenceEqual(File.ReadAllBytes(third)), "A changed payload must change the zip.");

        // tar.gz uses GNU format because .NET's PAX writer embeds the process ID in extended-header names, which no
        // single-process comparison can see; assert the format and the fixed metadata directly as well.
        string tarFirst = Path.Combine(temporary.Path, "first.tar.gz"), tarSecond = Path.Combine(temporary.Path, "second.tar.gz");
        File.WriteAllText(Path.Combine(source, "alpha.txt"), "first");
        ReleaseArchive.CreateTarGz(source, tarFirst);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, new DateTime(2032, 6, 18, 5, 6, 7, DateTimeKind.Utc));
        ReleaseArchive.CreateTarGz(source, tarSecond);
        Check.That(File.ReadAllBytes(tarFirst).AsSpan().SequenceEqual(File.ReadAllBytes(tarSecond)), "tar.gz archives must not depend on on-disk modification times.");
        using var tarFile = File.OpenRead(tarFirst);
        using var gunzip = new GZipStream(tarFile, CompressionMode.Decompress);
        using var memory = new MemoryStream();
        gunzip.CopyTo(memory);
        Check.That(!Encoding.ASCII.GetString(memory.ToArray()).Contains("PaxHeaders", StringComparison.Ordinal), "tar.gz archives must not contain process-ID-named PAX headers.");
        memory.Position = 0;
        var names = new List<string>();
        using (var reader = new TarReader(memory))
            while (reader.GetNextEntry() is { } entry)
            {
                names.Add(entry.Name);
                Check.That(entry.Format == TarEntryFormat.Gnu && entry.ModificationTime == DateTimeOffset.UnixEpoch && entry.Uid == 0 && entry.Gid == 0,
                    "tar.gz entries must use fixed GNU-format metadata.");
            }
        Check.That(names.SequenceEqual(["alpha.txt", "sub/nested.txt", "zeta.txt"]), "tar.gz entries must be sorted.");
    }

    private static void NuGetPayloadComparison()
    {
        using TemporaryDirectory temporary = new();
        string Package(string name, params (string Entry, string Content)[] entries)
        {
            string path = Path.Combine(temporary.Path, name);
            using var file = File.Create(path);
            using var zip = new ZipArchive(file, ZipArchiveMode.Create);
            foreach (var (entry, content) in entries)
                using (var writer = new StreamWriter(zip.CreateEntry(entry).Open(), Encoding.UTF8)) writer.Write(content);
            return path;
        }
        string local = Package("local.nupkg", ("tool.nuspec", "spec"), ("tools/net11.0/any/app.dll", "binary"));

        Check.That(NuGetRegistry.ComparePayload(local, Package("same.nupkg", ("tools/net11.0/any/app.dll", "binary"), ("tool.nuspec", "spec"))).Count == 0,
            "Identical payloads in a different entry order must match.");
        Check.That(NuGetRegistry.ComparePayload(local, Package("signed.nupkg", ("tool.nuspec", "spec"), ("tools/net11.0/any/app.dll", "binary"), (".signature.p7s", "repository countersignature"))).Count == 0,
            "The nuget.org repository signature must not count as a payload difference.");
        Check.That(NuGetRegistry.ComparePayload(local, Package("changed.nupkg", ("tool.nuspec", "spec"), ("tools/net11.0/any/app.dll", "rebuilt"))) is [var changed] && changed.Contains("app.dll", StringComparison.Ordinal),
            "A rebuilt entry under the same version must be reported.");
        Check.That(NuGetRegistry.ComparePayload(local, Package("missing.nupkg", ("tool.nuspec", "spec"))).Count == 1,
            "An entry missing from the registry package must be reported.");
        Check.That(NuGetRegistry.ComparePayload(local, Package("extra.nupkg", ("tool.nuspec", "spec"), ("tools/net11.0/any/app.dll", "binary"), ("tools/extra.dll", "x"))).Count == 1,
            "An entry only in the registry package must be reported.");
    }

    // dotnet-CycloneDX stamps a random serial and the wall clock; the release tool normalizes both from content and commit time.
    private static void SbomReproducibility()
    {
        static string Generated(string serial, string timestamp, string component) =>
            $$$"""{"bomFormat":"CycloneDX","specVersion":"1.7","serialNumber":"urn:uuid:{{{serial}}}","version":1,"metadata":{"timestamp":"{{{timestamp}}}","component":{"name":"app"}},"components":[{"name":"{{{component}}}"},{"name":"a"}]}""";
        var buildTime = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(2));

        string first = SbomNormalizer.Normalize(Generated(Guid.NewGuid().ToString(), "2026-10-11T02:38:39Z", "b"), buildTime);
        string second = SbomNormalizer.Normalize(Generated(Guid.NewGuid().ToString(), "2031-01-01T00:00:00Z", "b"), buildTime);
        Check.That(first == second, "SBOMs generated from the same content with different random serials and wall-clock times must normalize to identical bytes.");

        using JsonDocument document = JsonDocument.Parse(first);
        JsonElement root = document.RootElement;
        Check.That(root.EnumerateObject().Select(property => property.Name).SequenceEqual(["bomFormat", "specVersion", "serialNumber", "version", "metadata", "components"])
            && root.GetProperty("components").EnumerateArray().Select(component => component.GetProperty("name").GetString()).SequenceEqual(["b", "a"]),
            "SBOM normalization must keep the generated property and component order.");
        Check.That(root.GetProperty("metadata").GetProperty("timestamp").GetString() == "2026-03-04T03:06:07Z", "The SBOM timestamp must be the given build time in UTC.");
        string serial = root.GetProperty("serialNumber").GetString()!;
        Check.That(Regex.IsMatch(serial, "^urn:uuid:[0-9a-f]{8}-[0-9a-f]{4}-8[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", RegexOptions.CultureInvariant),
            "The SBOM serial number must be an RFC 4122 URN with a version 8 UUID.");

        string Serial(string json) => JsonDocument.Parse(json).RootElement.GetProperty("serialNumber").GetString()!;
        Check.That(Serial(SbomNormalizer.Normalize(Generated("x", "y", "other"), buildTime)) != serial, "Changed SBOM content must change the serial number.");
        Check.That(Serial(SbomNormalizer.Normalize(Generated("x", "y", "b"), buildTime.AddSeconds(1))) != serial, "A different build time must change the serial number.");
        try { SbomNormalizer.Normalize("""{"bomFormat":"CycloneDX","metadata":{"timestamp":"t"}}""", buildTime); }
        catch (InvalidOperationException) { return; }
        throw new VerificationException("An SBOM without a serialNumber must be rejected, not silently accepted.");
    }

    // Packs a dependency-free probe twice, more than the 2-second ZIP time resolution apart, with the property the package builder passes.
    private static async Task PackReproducibility()
    {
        using TemporaryDirectory temporary = new();
        string project = Directory.CreateDirectory(Path.Combine(temporary.Path, "probe")).FullName;
        File.WriteAllText(Path.Combine(project, "Probe.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><PackageId>Probe</PackageId><Version>1.0.0</Version></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(project, "Probe.cs"), "internal sealed class Probe { }");
        string dotnet = Processes.FindExecutable("dotnet");

        async Task<string> Pack(string output)
        {
            foreach (string folder in new[] { "bin", "obj" })
                if (Directory.Exists(Path.Combine(project, folder))) Directory.Delete(Path.Combine(project, folder), recursive: true);
            await Processes.RunAsync(new(dotnet, "pack", "Probe.csproj", "-c", "Release", "-o", output, "-p:DeterministicTimestamp=" + ReleaseArchive.ZipTimestamp.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")),
                timeout: 300, directory: project);
            return Path.Combine(output, "Probe.1.0.0.nupkg");
        }

        string first = await Pack(Path.Combine(temporary.Path, "first"));
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        string second = await Pack(Path.Combine(temporary.Path, "second"));
        Check.That(File.ReadAllBytes(first).AsSpan().SequenceEqual(File.ReadAllBytes(second)), "dotnet pack must produce identical .nupkg bytes with the fixed timestamp.");
        using ZipArchive package = ZipFile.OpenRead(first);
        Check.That(package.Entries.Count > 0 && package.Entries.All(entry => entry.LastWriteTime.DateTime == ReleaseArchive.ZipTimestamp.DateTime),
            "Every .nupkg entry must carry the fixed timestamp.");
    }

    private static void TagGrammar()
    {
        foreach (string valid in new[] { "v0.7.3", "v0.8.0-rc.1", "v10.20.30", "v0.7.3-alpha-2" })
            Check.That(ProductVersion.IsReleaseTag(valid), $"Release tag {valid} must be accepted.");
        foreach (string invalid in new[] { "", "0.7.3", "v0.7", "v0.7.3.1", "v0.7.3\n", " v0.7.3", "v0.7.3 ", "v0.7.3+build", "v0.7.3-", "v0.7.x", "V0.7.3", "refs/tags/v0.7.3", "v0.7.3-rc..1" })
            Check.That(!ProductVersion.IsReleaseTag(invalid), $"Release tag '{invalid.ReplaceLineEndings("\\n")}' must be rejected.");
    }

    // The application, npm and MCP registry manifests repeat one version; any disagreement, and any leave from the 0.x line, must be named.
    private static void VersionConsistency()
    {
        using TemporaryDirectory temporary = new();
        int counter = 0;
        string Tree(string project, string npm, string optional, string? server, string package)
        {
            string root = Directory.CreateDirectory(Path.Combine(temporary.Path, "tree" + ++counter)).FullName;
            Directory.CreateDirectory(Path.Combine(root, "src", "PostgreSqlMcp"));
            Directory.CreateDirectory(Path.Combine(root, "npm"));
            File.WriteAllText(Path.Combine(root, ProductVersion.ProjectFile), $"<Project><PropertyGroup><Version>{project}</Version></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root, ProductVersion.NpmManifest),
                $$$"""{"name":"@scope/app","version":"{{{npm}}}","optionalDependencies":{"@scope/app-linux-x64":"{{{optional}}}","@scope/app-win32-x64":"{{{project}}}"}}""");
            if (server is not null)
                File.WriteAllText(Path.Combine(root, ProductVersion.ServerManifest),
                    $$$"""{"version":"{{{server}}}","packages":[{"identifier":"@scope/app","version":"{{{package}}}"},{"identifier":"scope.app","version":"{{{project}}}"}]}""");
            return root;
        }
        IReadOnlyList<string> Problems(string root) => ProductVersion.Inconsistencies(root);
        void Names(string description, IReadOnlyList<string> problems, string expected) =>
            Check.That(problems.Count == 1 && problems[0].Contains(expected, StringComparison.Ordinal), $"{description} must be reported as exactly one problem naming '{expected}', got: {string.Join("; ", problems)}");

        Check.That(Problems(Tree("0.7.3", "0.7.3", "0.7.3", "0.7.3", "0.7.3")).Count == 0, "Consistent 0.x manifests must pass.");
        Check.That(Problems(Tree("0.8.0-rc.1", "0.8.0-rc.1", "0.8.0-rc.1", "0.8.0-rc.1", "0.8.0-rc.1")).Count == 0, "A consistent 0.x prerelease must pass.");
        Names("An npm version mismatch", Problems(Tree("0.7.3", "0.7.2", "0.7.3", "0.7.3", "0.7.3")), ProductVersion.NpmManifest + " version");
        Names("A platform optionalDependency mismatch", Problems(Tree("0.7.3", "0.7.3", "0.7.2", "0.7.3", "0.7.3")), "@scope/app-linux-x64");
        Names("A server.json version mismatch", Problems(Tree("0.7.3", "0.7.3", "0.7.3", "0.7.2", "0.7.3")), ProductVersion.ServerManifest + " version");
        Names("A server.json package version mismatch", Problems(Tree("0.7.3", "0.7.3", "0.7.3", "0.7.3", "0.7.2")), "packages[0] (@scope/app)");
        Names("A missing server.json", Problems(Tree("0.7.3", "0.7.3", "0.7.3", null, "0.7.3")), ProductVersion.ServerManifest + " is missing");

        var major = Problems(Tree("1.0.0", "1.0.0", "1.0.0", "1.0.0", "1.0.0"));
        Names("Major 1 with every manifest consistent", major, "stays on 0.x");
        Names("A malformed application version", Problems(Tree("0.7", "0.7", "0.7", "0.7", "0.7")), "is not <major>.<minor>.<patch>");
        Check.That(Problems(Tree("1.0.0", "0.9.0", "1.0.0", "1.0.0", "1.0.0")).Count == 2, "A mismatch and a major violation must both be reported, not just the first.");

        string noPackages = Tree("0.7.3", "0.7.3", "0.7.3", "0.7.3", "0.7.3");
        File.WriteAllText(Path.Combine(noPackages, ProductVersion.ServerManifest), """{"version":"0.7.3"}""");
        Names("A server.json without packages", Problems(noPackages), "has no packages array");
        string noOptional = Tree("0.7.3", "0.7.3", "0.7.3", "0.7.3", "0.7.3");
        File.WriteAllText(Path.Combine(noOptional, ProductVersion.NpmManifest), """{"name":"@scope/app","version":"0.7.3"}""");
        Names("An npm manifest without optionalDependencies", Problems(noOptional), "optionalDependencies");

        string? repository = RepositoryRoot.Find();
        if (repository is not null && File.Exists(Path.Combine(repository, ProductVersion.ServerManifest)))
            Check.That(Problems(repository).Count == 0, "The repository's own manifests must be consistent.");
    }

    private static CheckRun Run(long id, string name, string status, string? conclusion, string app = "github-actions") => new(id, name, status, conclusion, app);

    private static void RequiredCheckEvaluation()
    {
        string[] required = ["build", "lint"];
        (IReadOnlyList<string> Pending, IReadOnlyList<string> Failed) Evaluate(params CheckRun[] runs) => RequiredChecks.Evaluate("github-actions", required, runs);

        var (greenPending, greenFailed) = Evaluate(Run(1, "build", "completed", "success"), Run(2, "lint", "completed", "success"), Run(3, "unrelated", "completed", "failure"));
        Check.That(greenPending.Count == 0 && greenFailed.Count == 0, "Success must pass and checks that are not required must be ignored.");
        var (lenientPending, lenientFailed) = Evaluate(Run(1, "build", "completed", "skipped"), Run(2, "lint", "completed", "neutral"));
        Check.That(lenientPending.Count == 0 && lenientFailed.Count == 0, "Skipped and neutral conclusions must pass.");

        Check.That(Evaluate(Run(1, "build", "completed", "success")) is { Pending: [var missing], Failed: [] } && missing.StartsWith("lint ", StringComparison.Ordinal),
            "A required check that never reported must be pending, never green.");
        foreach (string conclusion in new[] { "failure", "cancelled", "timed_out", "action_required", "stale", "startup_failure" })
            Check.That(Evaluate(Run(1, "build", "completed", "success"), Run(2, "lint", "completed", conclusion)) is { Failed: [var failure] } && failure.Contains(conclusion, StringComparison.Ordinal),
                $"Conclusion {conclusion} must fail the gate.");
        Check.That(Evaluate(Run(1, "build", "completed", "success"), Run(2, "lint", "completed", null)).Failed.Count == 1, "A completed run without a conclusion must fail.");
        foreach (string status in new[] { "queued", "in_progress", "waiting", "pending" })
            Check.That(Evaluate(Run(1, "build", "completed", "success"), Run(2, "lint", status, null)) is { Pending.Count: 1, Failed.Count: 0 }, $"Status {status} must be pending.");

        Check.That(Evaluate(Run(1, "build", "completed", "failure"), Run(5, "build", "completed", "success"), Run(2, "lint", "completed", "success")) is { Pending: [], Failed: [] },
            "A successful re-run must supersede an earlier failure.");
        Check.That(Evaluate(Run(1, "build", "completed", "success"), Run(5, "build", "completed", "failure"), Run(2, "lint", "completed", "success")).Failed.Count == 1,
            "A failed re-run must supersede an earlier success.");
        Check.That(Evaluate(Run(1, "build", "completed", "success"), Run(5, "build", "in_progress", null), Run(2, "lint", "completed", "success")).Pending.Count == 1,
            "A newer unfinished re-run must make the check pending, not green.");
        Check.That(Evaluate(Run(1, "build", "completed", "success", "other-app"), Run(2, "lint", "completed", "success")).Pending.Count == 1,
            "A success reported under the same name by a different app must not satisfy the check.");
        Check.That(Evaluate(Run(1, "build", "completed", "failure"), Run(9, "build", "completed", "success", "other-app"), Run(2, "lint", "completed", "success")).Failed.Count == 1,
            "A newer run by a different app must not supersede the trusted app's failure.");

        string valid = """{"app":"github-actions","commit":["a","b"],"pullRequest":["c"]}""";
        Check.That(RequiredCheckPolicy.Parse(valid) is { App: "github-actions", Commit.Count: 2, PullRequest.Count: 1 }, "A valid required-checks policy must parse.");
        foreach (string invalid in new[]
        {
            "[]", """{"commit":["a"]}""", """{"app":"","commit":["a"]}""", """{"app":"x","commit":[]}""", """{"app":"x"}""",
            """{"app":"x","commit":["a","a"]}""", """{"app":"x","commit":["a"],"pullRequest":["a"]}""", """{"app":"x","commit":[" a"]}""",
            """{"app":"x","commit":[""]}""", """{"app":"x","commit":"a"}""", """{"app":"x","commit":["a"],"extra":1}""", """{"app":"x","commit":[1]}"""
        })
        {
            try { RequiredCheckPolicy.Parse(invalid); }
            catch (InvalidOperationException) { continue; }
            throw new VerificationException("A malformed required-checks policy was accepted: " + invalid);
        }
        string? repository = RepositoryRoot.Find();
        if (repository is not null)
            Check.That(RequiredCheckPolicy.Parse(File.ReadAllText(Path.Combine(repository, RequiredChecks.PolicyFile))).Commit.Count > 0, "The repository's required-checks policy must parse.");
    }

    private static async Task RequiredCheckPolling()
    {
        string[] required = ["build"];
        TimeSpan tick = TimeSpan.FromMilliseconds(2);
        int fetches = 0;
        Func<Task<IReadOnlyList<CheckRun>>> Sequence(params string?[] conclusions) => () =>
        {
            string? conclusion = conclusions[Math.Min(fetches++, conclusions.Length - 1)];
            IReadOnlyList<CheckRun> runs = [Run(1, "build", conclusion is null ? "in_progress" : "completed", conclusion)];
            return Task.FromResult(runs);
        };

        await RequiredChecks.RequireAsync("test", "github-actions", required, Sequence(null, null, "success"), TimeSpan.FromSeconds(30), tick);
        Check.That(fetches == 3, "Polling must continue until the pending check completes.");

        fetches = 0;
        await Fails("a failure while polling", () => RequiredChecks.RequireAsync("test", "github-actions", required, Sequence(null, "failure", "success"), TimeSpan.FromSeconds(30), tick));
        Check.That(fetches == 2, "A failure must end polling immediately.");

        fetches = 0;
        await Fails("a check that stays pending past the wait", () => RequiredChecks.RequireAsync("test", "github-actions", required, Sequence((string?)null), TimeSpan.FromMilliseconds(30), tick));
        Check.That(fetches > 1, "Polling must retry until the wait elapses.");

        fetches = 0;
        await Fails("a pending check without a wait", () => RequiredChecks.RequireAsync("test", "github-actions", required, Sequence((string?)null), TimeSpan.Zero, tick));
        Check.That(fetches == 1, "Without a wait there must be exactly one evaluation.");

        await Fails("a missing check", () => RequiredChecks.RequireAsync("test", "github-actions", required, () => Task.FromResult<IReadOnlyList<CheckRun>>([]), TimeSpan.Zero, tick));
    }
}
