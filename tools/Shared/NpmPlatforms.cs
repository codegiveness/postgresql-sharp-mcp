// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

/// <summary>
/// One npm platform package per supported runtime identifier. The main npm package lists every one in
/// optionalDependencies; npm installs only the package whose os/cpu/libc fields match the machine.
/// </summary>
internal sealed record NpmPlatform(string Rid, string Os, string Cpu, string? Libc)
{
    public static readonly NpmPlatform[] All =
    [
        new("win-x64", "win32", "x64", null),
        new("linux-x64", "linux", "x64", "glibc"),
        new("linux-arm64", "linux", "arm64", "glibc"),
        new("osx-x64", "darwin", "x64", null),
        new("osx-arm64", "darwin", "arm64", null)
    ];

    /// <summary>Bin of every platform package; the main package's postinstall runs it by name from npm's PATH.</summary>
    public const string InstallerCommand = "postgresql-sharp-mcp-npm-install";

    /// <summary>Directory inside a platform package that holds the self-contained server and installer.</summary>
    public const string RuntimeDirectory = "runtime";

    public string PackageName(string mainPackage) => $"{mainPackage}-{Os}-{Cpu}";

    public string Executable(string name) => Rid.StartsWith("win-", StringComparison.Ordinal) ? name + ".exe" : name;

    /// <summary>File name npm pack would give the package tarball.</summary>
    public static string TarballName(string package, string version) =>
        package.Replace("@", "", StringComparison.Ordinal).Replace('/', '-') + "-" + version + ".tgz";
}
