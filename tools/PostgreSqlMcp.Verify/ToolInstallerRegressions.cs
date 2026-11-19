// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace PostgreSqlMcp.Verify;

/// <summary>Offline proof that the pinned-tool installer rejects tampered or hostile archives and URLs.</summary>
internal static class ToolInstallerRegressions
{
    private const string Member = "bin/tool";
    private static readonly byte[] Payload = "#!/bin/sh\nexit 0\n"u8.ToArray();

    public static async Task RunAsync()
    {
        using var temporary = new TemporaryDirectory();
        int index = 0;
        string Write(Action<TarWriter> entries)
        {
            string archive = Path.Combine(temporary.Path, $"archive-{index++}.tar.gz");
            using FileStream file = File.Create(archive);
            using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
            using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true)) entries(writer);
            return archive;
        }
        static PaxTarEntry Regular(string name) => new(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Payload) };
        static PinnedTool PinnedTo(string archive) => new("fixture", "0", "https://example.invalid/fixture.tar.gz",
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archive))), Member);
        async Task Rejects(string description, string archive, PinnedTool tool, string expected)
        {
            string output = Path.Combine(temporary.Path, $"out-{index++}");
            try { await PinnedTools.InstallArchiveAsync(tool, archive, output); }
            catch (VerificationException exception)
            {
                Check.That(exception.Message.Contains(expected, StringComparison.Ordinal), $"{description}: unexpected rejection: {exception.Message}");
                Check.That(!Directory.Exists(output), $"{description}: output was created despite rejection.");
                return;
            }
            throw new VerificationException($"{description}: archive was accepted.");
        }

        string valid = Write(writer => writer.WriteEntry(Regular(Member)));
        string installed = await PinnedTools.InstallArchiveAsync(PinnedTo(valid), valid, Path.Combine(temporary.Path, "installed"));
        Check.That(File.ReadAllBytes(installed).AsSpan().SequenceEqual(Payload), "Valid archive: extracted content differs.");
        if (!OperatingSystem.IsWindows())
            Check.That(File.GetUnixFileMode(installed).HasFlag(UnixFileMode.UserExecute), "Valid archive: executable bit missing.");

        // Corrupted expected hash: one flipped hex digit must fail closed before any extraction.
        PinnedTool exact = PinnedTo(valid);
        char first = exact.Sha256[0] == '0' ? '1' : '0';
        await Rejects("corrupted expected hash", valid, exact with { Sha256 = first + exact.Sha256[1..] }, "SHA-256 verification failed");
        await Rejects("empty expected hash", valid, exact with { Sha256 = "" }, "SHA-256 verification failed");

        string traversal = Write(writer => { writer.WriteEntry(Regular(Member)); writer.WriteEntry(Regular("../escape")); });
        await Rejects("parent traversal", traversal, PinnedTo(traversal), "unsafe path");
        string absolute = Write(writer => { writer.WriteEntry(Regular(Member)); writer.WriteEntry(Regular("/tmp/escape")); });
        await Rejects("absolute path", absolute, PinnedTo(absolute), "unsafe path");
        string symlink = Write(writer =>
        {
            writer.WriteEntry(Regular(Member));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "/etc/passwd" });
        });
        await Rejects("symbolic link", symlink, PinnedTo(symlink), "link or special entry");
        string hardlink = Write(writer =>
        {
            writer.WriteEntry(Regular(Member));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, "hard") { LinkName = Member });
        });
        await Rejects("hard link", hardlink, PinnedTo(hardlink), "link or special entry");
        string linkedMember = Write(writer => writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, Member) { LinkName = "/bin/sh" }));
        await Rejects("member is a link", linkedMember, PinnedTo(linkedMember), "link or special entry");
        string duplicate = Write(writer => { writer.WriteEntry(Regular(Member)); writer.WriteEntry(Regular(Member)); });
        await Rejects("duplicate member", duplicate, PinnedTo(duplicate), "duplicated");
        string missing = Write(writer => writer.WriteEntry(Regular("other/tool")));
        await Rejects("missing member", missing, PinnedTo(missing), "does not contain");
        string directory = Write(writer => writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, Member)));
        await Rejects("member is a directory", directory, PinnedTo(directory), "not a regular file");

        PinnedTool insecure = exact with { Url = "http://example.invalid/fixture.tar.gz" };
        bool refused = false;
        try { await PinnedTools.InstallAsync(insecure, Path.Combine(temporary.Path, "insecure")); }
        catch (VerificationException exception) { refused = exception.Message.Contains("HTTPS", StringComparison.Ordinal); }
        Check.That(refused, "A plain HTTP pinned URL was not refused.");
        Console.WriteLine("Tool installer regressions passed: hash tampering, traversal, links, duplicate/missing members and non-HTTPS URLs are rejected.");
    }
}
