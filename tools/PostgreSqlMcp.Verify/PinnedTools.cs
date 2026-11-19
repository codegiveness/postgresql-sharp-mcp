// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PostgreSqlMcp.Verify;

/// <summary>An upstream Linux x64 release archive and the one executable taken from it.</summary>
internal sealed record PinnedTool(string Name, string Version, string Url, string Sha256, string Member);

/// <summary>Installs CI scanners from content-pinned upstream release archives; any mismatch fails closed.</summary>
internal static class PinnedTools
{
    // Review URL and hash together on every update. Hash sources: gitleaks, trivy and actionlint from their
    // release checksum files; zizmor and shellcheck publish none, so the GitHub release asset digest (for
    // zizmor also its release provenance attestation subject). Each was confirmed by downloading the asset.
    public static readonly IReadOnlyList<PinnedTool> All =
    [
        new("gitleaks", "8.30.1", "https://github.com/gitleaks/gitleaks/releases/download/v8.30.1/gitleaks_8.30.1_linux_x64.tar.gz",
            "551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb", "gitleaks"),
        new("trivy", "0.76.1", "https://github.com/aquasecurity/trivy/releases/download/v0.76.1/trivy_0.76.1_Linux-64bit.tar.gz",
            "c7cbe3137fb8a48a17574bede3dd6beb16742d1e9772929940fc90082c39d9c0", "trivy"),
        new("zizmor", "1.32.0", "https://github.com/zizmorcore/zizmor/releases/download/v1.32.0/zizmor-x86_64-unknown-linux-gnu.tar.gz",
            "bb110cc765eca9cd7efb2e284a956be5974c60027b0970f16397d86208a9957b", "zizmor"),
        new("actionlint", "1.7.12", "https://github.com/rhysd/actionlint/releases/download/v1.7.12/actionlint_1.7.12_linux_amd64.tar.gz",
            "8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8", "actionlint"),
        new("shellcheck", "0.11.0", "https://github.com/koalaman/shellcheck/releases/download/v0.11.0/shellcheck-v0.11.0.linux.x86_64.tar.gz",
            "b7af85e41cc99489dcc21d66c6d5f3685138f06d34651e6d34b42ec6d54fe6f6", "shellcheck-v0.11.0/shellcheck")
    ];

    public static string Usage => $"install-tool <{string.Join('|', All.Select(tool => tool.Name))}> --output directory";

    public static PinnedTool Get(string name) =>
        All.SingleOrDefault(tool => tool.Name == name) ?? throw new VerificationException($"Unknown pinned tool. Usage: {Usage}");

    /// <summary>Downloads, verifies and extracts the tool into <paramref name="outputDirectory"/>; returns the executable path.</summary>
    public static async Task<string> InstallAsync(PinnedTool tool, string outputDirectory)
    {
        var url = new Uri(tool.Url);
        Check.That(url.Scheme == Uri.UriSchemeHttps, "Pinned tool URLs must use HTTPS.");
        Check.That(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64,
            "Pinned tool archives are Linux x64 builds (the CI runner platform).");
        using var staging = new TemporaryDirectory();
        string archive = Path.Combine(staging.Path, "release.tar.gz");
        await DownloadAsync(url, archive);
        return await InstallArchiveAsync(tool, archive, outputDirectory);
    }

    /// <summary>Fail-closed: the archive is hashed before any byte of it is decompressed or parsed.</summary>
    internal static async Task<string> InstallArchiveAsync(PinnedTool tool, string archive, string outputDirectory)
    {
        await using (FileStream stream = File.OpenRead(archive))
            Check.That(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream)) == tool.Sha256,
                $"SHA-256 verification failed for {tool.Name} {tool.Version}; refusing to install it.");
        using var staging = new TemporaryDirectory();
        string executable = Path.Combine(staging.Path, tool.Name);
        await using (var tar = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress))
            ExtractMember(tar, tool.Member, executable);
        Directory.CreateDirectory(outputDirectory);
        string target = Path.Combine(Path.GetFullPath(outputDirectory), tool.Name);
        File.Move(executable, target, overwrite: true);
        return target;
    }

    private static async Task DownloadAsync(Uri url, string destination)
    {
        // HttpClient refuses HTTPS-to-HTTP redirects; the final check keeps that guarantee explicit.
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        Check.That(response.IsSuccessStatusCode, $"Pinned tool download failed with HTTP {(int)response.StatusCode}.");
        Check.That(response.RequestMessage?.RequestUri?.Scheme == Uri.UriSchemeHttps, "Pinned tool download left HTTPS.");
        await using Stream input = await response.Content.ReadAsStreamAsync();
        await using FileStream output = File.Create(destination);
        await input.CopyToAsync(output);
    }

    /// <summary>
    /// Extracts exactly one regular-file member. The whole archive must be plain relative data: any absolute or
    /// parent-traversing name, link or device entry rejects it, even when it is not the member being extracted.
    /// </summary>
    internal static void ExtractMember(Stream tar, string member, string destination)
    {
        using var reader = new TarReader(tar);
        bool found = false;
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes) continue;
            string name = entry.Name;
            Check.That(name.Length > 0 && name is not ['/', ..] && !name.Contains('\\', StringComparison.Ordinal) && !name.Split('/').Contains(".."),
                "Pinned tool archive contains an unsafe path.");
            Check.That(entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory,
                "Pinned tool archive contains a link or special entry.");
            if (name != member) continue;
            Check.That(!found && entry.EntryType != TarEntryType.Directory && entry.DataStream is not null,
                "Pinned tool archive member is duplicated or not a regular file.");
            using (FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write))
                entry.DataStream!.CopyTo(output);
            found = true;
        }
        Check.That(found, "Pinned tool archive does not contain the expected executable.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
