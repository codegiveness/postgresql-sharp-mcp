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
        SbomReproducibility();
        PackReproducibility().GetAwaiter().GetResult();
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
        string local = Package("local.nupkg", ("tool.nuspec", "spec"), ("tools/net10.0/any/app.dll", "binary"));

        Check.That(NuGetRegistry.ComparePayload(local, Package("same.nupkg", ("tools/net10.0/any/app.dll", "binary"), ("tool.nuspec", "spec"))).Count == 0,
            "Identical payloads in a different entry order must match.");
        Check.That(NuGetRegistry.ComparePayload(local, Package("signed.nupkg", ("tool.nuspec", "spec"), ("tools/net10.0/any/app.dll", "binary"), (".signature.p7s", "repository countersignature"))).Count == 0,
            "The nuget.org repository signature must not count as a payload difference.");
        Check.That(NuGetRegistry.ComparePayload(local, Package("changed.nupkg", ("tool.nuspec", "spec"), ("tools/net10.0/any/app.dll", "rebuilt"))) is [var changed] && changed.Contains("app.dll", StringComparison.Ordinal),
            "A rebuilt entry under the same version must be reported.");
        Check.That(NuGetRegistry.ComparePayload(local, Package("missing.nupkg", ("tool.nuspec", "spec"))).Count == 1,
            "An entry missing from the registry package must be reported.");
        Check.That(NuGetRegistry.ComparePayload(local, Package("extra.nupkg", ("tool.nuspec", "spec"), ("tools/net10.0/any/app.dll", "binary"), ("tools/extra.dll", "x"))).Count == 1,
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
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>Probe</PackageId><Version>1.0.0</Version></PropertyGroup></Project>");
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
}
