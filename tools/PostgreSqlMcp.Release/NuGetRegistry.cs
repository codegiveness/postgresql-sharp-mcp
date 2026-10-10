using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

internal static class NuGetRegistry
{
    private const string FlatContainer = "https://api.nuget.org/v3-flatcontainer/";

    // nuget.org countersigns every accepted package by adding this entry, so the registry's .nupkg bytes
    // never equal the locally built file. Every other entry must match exactly.
    private const string RepositorySignatureEntry = ".signature.p7s";

    public static async Task<bool> HasVersionAsync(HttpClient http, string id, string version)
    {
        using HttpResponseMessage response = await http.GetAsync($"{FlatContainer}{id}/index.json");
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("versions").EnumerateArray()
            .Any(listed => string.Equals(listed.GetString(), version, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task DownloadAsync(HttpClient http, string id, string version, string destination)
    {
        string lower = version.ToLowerInvariant();
        using HttpResponseMessage response = await http.GetAsync($"{FlatContainer}{id}/{lower}/{id}.{lower}.nupkg", HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var file = File.Create(destination);
        await response.Content.CopyToAsync(file);
    }

    /// <summary>Differences between two packages' payloads (entry names and SHA-256 of contents), ignoring the repository signature.</summary>
    public static IReadOnlyList<string> ComparePayload(string localPackage, string registryPackage)
    {
        Dictionary<string, string> local = Payload(localPackage), registry = Payload(registryPackage);
        var differences = new List<string>();
        foreach (string name in local.Keys.Order(StringComparer.Ordinal))
            if (!registry.TryGetValue(name, out string? hash)) differences.Add($"'{name}' is missing from the registry package");
            else if (hash != local[name]) differences.Add($"'{name}' differs");
        foreach (string name in registry.Keys.Except(local.Keys).Order(StringComparer.Ordinal))
            differences.Add($"'{name}' is only in the registry package");
        return differences;
    }

    private static Dictionary<string, string> Payload(string package)
    {
        using ZipArchive zip = ZipFile.OpenRead(package);
        var payload = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (entry.FullName == RepositorySignatureEntry || entry.FullName.EndsWith('/')) continue;
            using Stream content = entry.Open();
            if (!payload.TryAdd(entry.FullName, Convert.ToHexStringLower(SHA256.HashData(content))))
                throw new InvalidOperationException($"Package contains duplicate entry '{entry.FullName}'.");
        }
        return payload;
    }
}
