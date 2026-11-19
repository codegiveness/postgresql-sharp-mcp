// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Formats.Tar;
using System.IO.Compression;

namespace PostgreSqlMcp.Verify.Tests;

// ReleaseArchive lives in the Release tool and is compiled into the verifier from the same source file.
public sealed class ReleaseArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"postgresql-sharp-mcp-archive-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Tree(string name, DateTime modified)
    {
        string directory = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(directory, "lib", "nested"));
        foreach ((string relative, string content) in new[] { ("z.txt", "last"), ("a.txt", "first"), ("lib/nested/m.txt", "middle"), ("lib/b.bin", "binary") })
        {
            string path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(path, modified);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        return directory;
    }

    [Fact]
    public void Entry_names_use_forward_slashes_relative_to_the_root_in_ordinal_order()
    {
        string tree = Tree("names", DateTime.UtcNow);

        Assert.Equal(["a.txt", "lib/b.bin", "lib/nested/m.txt", "z.txt"], ReleaseArchive.OrderedFiles(tree).Select(path => ReleaseArchive.EntryName(tree, path)));
    }

    [Fact]
    public void Zip_bytes_depend_only_on_names_contents_and_modes_not_on_file_timestamps()
    {
        string first = Tree("zip-a", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        string second = Tree("zip-b", new DateTime(2026, 6, 6, 6, 6, 6, DateTimeKind.Utc));
        string firstZip = Path.Combine(_root, "a.zip");
        string secondZip = Path.Combine(_root, "b.zip");

        ReleaseArchive.CreateZip(first, firstZip);
        ReleaseArchive.CreateZip(second, secondZip);

        Assert.Equal(File.ReadAllBytes(firstZip), File.ReadAllBytes(secondZip));
        using ZipArchive zip = ZipFile.OpenRead(firstZip);
        Assert.Equal(["a.txt", "lib/b.bin", "lib/nested/m.txt", "z.txt"], zip.Entries.Select(entry => entry.FullName));
        Assert.All(zip.Entries, entry => Assert.Equal(ReleaseArchive.ZipTimestamp.DateTime, entry.LastWriteTime.DateTime));
    }

    [Fact]
    public void Tar_gz_bytes_depend_only_on_names_contents_and_modes_not_on_file_timestamps()
    {
        string first = Tree("tar-a", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        string second = Tree("tar-b", new DateTime(2026, 6, 6, 6, 6, 6, DateTimeKind.Utc));
        string firstTar = Path.Combine(_root, "a.tar.gz");
        string secondTar = Path.Combine(_root, "b.tar.gz");

        ReleaseArchive.CreateTarGz(first, firstTar);
        ReleaseArchive.CreateTarGz(second, secondTar);

        Assert.Equal(File.ReadAllBytes(firstTar), File.ReadAllBytes(secondTar));
        using var gzip = new GZipStream(File.OpenRead(firstTar), CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var names = new List<string>();
        while (reader.GetNextEntry() is { } entry)
        {
            names.Add(entry.Name);
            Assert.Equal(DateTimeOffset.UnixEpoch, entry.ModificationTime);
            Assert.Equal(0, entry.Uid);
            Assert.Equal(0, entry.Gid);
            Assert.Equal(TarEntryType.RegularFile, entry.EntryType);
        }
        Assert.Equal(["a.txt", "lib/b.bin", "lib/nested/m.txt", "z.txt"], names);
    }

    [Fact]
    public void Changed_content_changes_the_archive()
    {
        string tree = Tree("changed", DateTime.UtcNow);
        string before = Path.Combine(_root, "before.zip");
        string after = Path.Combine(_root, "after.zip");
        ReleaseArchive.CreateZip(tree, before);

        File.WriteAllText(Path.Combine(tree, "a.txt"), "edited");
        ReleaseArchive.CreateZip(tree, after);

        Assert.NotEqual(File.ReadAllBytes(before), File.ReadAllBytes(after));
    }

    [Fact]
    public void Unix_modes_are_recorded_in_both_formats()
    {
        if (OperatingSystem.IsWindows()) return;
        string tree = Tree("modes", DateTime.UtcNow);
        File.SetUnixFileMode(Path.Combine(tree, "lib", "b.bin"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        string archive = Path.Combine(_root, "modes.tar.gz");

        ReleaseArchive.CreateTarGz(tree, archive);

        using var gzip = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var modes = new Dictionary<string, UnixFileMode>();
        while (reader.GetNextEntry() is { } entry) modes[entry.Name] = entry.Mode;
        Assert.Equal((UnixFileMode)0b111_101_101, modes["lib/b.bin"]);
        Assert.Equal((UnixFileMode)0b110_100_100, modes["a.txt"]);
    }
}
