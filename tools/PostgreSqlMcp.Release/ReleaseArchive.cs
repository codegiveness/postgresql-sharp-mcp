using System.IO.Compression;

internal static class ReleaseArchive
{
    // The ZIP/DOS timestamp cannot represent the Unix epoch used for tar entries; 1980-01-01 is its earliest value.
    public static readonly DateTimeOffset ZipTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static UnixFileMode EntryMode(string path) =>
        OperatingSystem.IsWindows()
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
            : File.GetUnixFileMode(path);

    public static IEnumerable<string> OrderedFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal);

    public static string EntryName(string directory, string path) =>
        Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>Writes a zip whose bytes depend only on file names, contents and modes, not on on-disk timestamps.</summary>
    public static void CreateZip(string directory, string archive)
    {
        using var file = File.Create(archive);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (string path in OrderedFiles(directory))
        {
            ZipArchiveEntry entry = zip.CreateEntry(EntryName(directory, path), CompressionLevel.Optimal);
            entry.LastWriteTime = ZipTimestamp;
            entry.ExternalAttributes = (int)EntryMode(path) << 16;
            using var source = File.OpenRead(path);
            using Stream target = entry.Open();
            source.CopyTo(target);
        }
    }
}
