// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

namespace PostgreSqlMcp.Verify.Tests;

// ReleaseAssets lives in the Release tool and is compiled into the verifier from the same source file.
public sealed class ReleaseAssetsTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static LocalAsset Local(string name, string sha = Digest) => new(name, "/artifacts/" + name, sha);

    private static Task<string> NoRemoteHash(string name) => throw new InvalidOperationException($"Unexpected download of {name}.");

    [Fact]
    public void Release_json_is_parsed_into_remote_assets()
    {
        IReadOnlyList<RemoteAsset> assets = ReleaseAssets.Parse("""
            {"assets":[
              {"name":"a.zip","state":"uploaded","digest":"sha256:abc"},
              {"name":"b.zip","state":"starter"},
              {"name":"c.zip","state":"uploaded","digest":null}]}
            """);

        Assert.Equal([new RemoteAsset("a.zip", "uploaded", "sha256:abc"), new RemoteAsset("b.zip", "starter", null), new RemoteAsset("c.zip", "uploaded", null)], assets);
    }

    [Fact]
    public async Task Assets_not_on_the_release_are_uploaded()
    {
        IReadOnlyList<LocalAsset> plan = await ReleaseAssets.PlanUploadsAsync([Local("new.zip"), Local("old.zip")], [new("old.zip", "uploaded", "sha256:" + Digest)], NoRemoteHash);

        Assert.Equal(["new.zip"], plan.Select(asset => asset.Name));
    }

    [Fact]
    public async Task An_identical_existing_asset_is_skipped_whatever_the_digest_case()
    {
        IReadOnlyList<LocalAsset> plan = await ReleaseAssets.PlanUploadsAsync([Local("a.zip")], [new("a.zip", "uploaded", "SHA256:" + Digest.ToUpperInvariant())], NoRemoteHash);

        Assert.Empty(plan);
    }

    [Fact]
    public async Task A_different_existing_asset_is_never_replaced()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ReleaseAssets.PlanUploadsAsync([Local("a.zip")], [new("a.zip", "uploaded", "sha256:" + new string('f', 64))], NoRemoteHash));

        Assert.Contains("already exists with different content", ex.Message, StringComparison.Ordinal);
        Assert.Contains("refusing to replace a published asset", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_incomplete_upload_must_be_deleted_deliberately()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ReleaseAssets.PlanUploadsAsync([Local("a.zip")], [new("a.zip", "starter", null)], NoRemoteHash));

        Assert.Contains("is in state 'starter', not a completed upload", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("md5:abc")]
    public async Task Without_a_sha256_api_digest_the_remote_asset_is_hashed_instead(string? digest)
    {
        var requested = new List<string>();
        Task<string> Hash(string name)
        {
            requested.Add(name);
            return Task.FromResult(Digest);
        }

        IReadOnlyList<LocalAsset> plan = await ReleaseAssets.PlanUploadsAsync([Local("a.zip")], [new("a.zip", "uploaded", digest)], Hash);

        Assert.Empty(plan);
        Assert.Equal(["a.zip"], requested);
    }

    [Fact]
    public async Task A_hashed_remote_asset_that_differs_is_rejected() => await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                                   ReleaseAssets.PlanUploadsAsync([Local("a.zip")], [new("a.zip", "uploaded", null)], _ => Task.FromResult(new string('e', 64))));
}
