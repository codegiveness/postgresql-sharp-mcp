using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace PostgreSqlMcp.Verify;

/// <summary>Offline regressions for release re-run safety and archive reproducibility; no network, no publication.</summary>
internal static class ReleaseRegressions
{
    public static void Run()
    {
        AssetPlanning().GetAwaiter().GetResult();
        ZipReproducibility();
        NuGetPayloadComparison();
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

    private static void ZipReproducibility()
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
}
