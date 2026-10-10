using System.Text.Json;

/// <summary>One asset as reported by the GitHub Releases API.</summary>
internal sealed record RemoteAsset(string Name, string State, string? Digest);

/// <summary>A release asset built by this run: its upload name, path and SHA-256 (lowercase hex).</summary>
internal sealed record LocalAsset(string Name, string Path, string Sha256);

internal static class ReleaseAssets
{
    private const string Sha256Prefix = "sha256:";

    public static IReadOnlyList<RemoteAsset> Parse(string releaseJson)
    {
        using var document = JsonDocument.Parse(releaseJson);
        var assets = new List<RemoteAsset>();
        foreach (JsonElement asset in document.RootElement.GetProperty("assets").EnumerateArray())
        {
            string? digest = asset.TryGetProperty("digest", out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            assets.Add(new RemoteAsset(asset.GetProperty("name").GetString()!, asset.GetProperty("state").GetString() ?? "", digest));
        }
        return assets;
    }

    /// <summary>
    /// Returns the built assets that are not on the release yet. An existing asset must be completely
    /// uploaded and byte-identical (SHA-256) to the built one; otherwise this throws instead of replacing it.
    /// <paramref name="hashRemoteAsync"/> computes the digest of an existing asset whose API digest is
    /// absent (assets uploaded before GitHub reported digests) or not SHA-256.
    /// </summary>
    public static async Task<IReadOnlyList<LocalAsset>> PlanUploadsAsync(
        IReadOnlyList<LocalAsset> local, IReadOnlyList<RemoteAsset> remote, Func<string, Task<string>> hashRemoteAsync)
    {
        var existing = remote.ToDictionary(asset => asset.Name, StringComparer.Ordinal);
        var missing = new List<LocalAsset>();
        foreach (LocalAsset asset in local)
        {
            if (!existing.TryGetValue(asset.Name, out RemoteAsset? found))
            {
                missing.Add(asset);
                continue;
            }
            if (found.State != "uploaded")
                throw new InvalidOperationException($"Release asset '{asset.Name}' is in state '{found.State}', not a completed upload; delete it deliberately before re-running.");
            string actual = found.Digest is not null && found.Digest.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase)
                ? found.Digest[Sha256Prefix.Length..]
                : await hashRemoteAsync(asset.Name);
            if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Release asset '{asset.Name}' already exists with different content (existing sha256:{actual.ToLowerInvariant()}, built sha256:{asset.Sha256}); refusing to replace a published asset. Delete it deliberately if replacement is intended.");
        }
        return missing;
    }
}
