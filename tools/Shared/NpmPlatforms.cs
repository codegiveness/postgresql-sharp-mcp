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

    /// <summary>Printed when the installer did not run or failed; npm install shows it, npx hides lifecycle output.</summary>
    public const string MissingPlatformMessage =
        "postgresql-sharp-mcp: setup did not complete. If npm reported " + InstallerCommand + " as not found or not recognized, " +
        "npm did not install the platform package for this machine: reinstall without --omit=optional and without an omit=optional npm setting, " +
        "and delete a package-lock.json created on another OS. Platform packages exist for Windows x64, macOS x64 and arm64, and glibc Linux x64 and arm64. " +
        "See https://github.com/codegiveness/postgresql-sharp-mcp#if-setup-does-not-work";

    /// <summary>
    /// The main package's only script. The fallback is limited to echo and exit so the same line runs under sh and cmd.exe;
    /// the message must stay free of quotes, parentheses and the characters % ^ &amp; | &lt; &gt; that either shell would interpret.
    /// </summary>
    public const string PostinstallScript = InstallerCommand + " || (echo " + MissingPlatformMessage + " 1>&2 && exit 1)";

    /// <summary>Directory inside a platform package that holds the self-contained server and installer.</summary>
    public const string RuntimeDirectory = "runtime";

    public string PackageName(string mainPackage) => $"{mainPackage}-{Os}-{Cpu}";

    public string Executable(string name) => Rid.StartsWith("win-", StringComparison.Ordinal) ? name + ".exe" : name;

    /// <summary>File name npm pack would give the package tarball.</summary>
    public static string TarballName(string package, string version) =>
        package.Replace("@", "", StringComparison.Ordinal).Replace('/', '-') + "-" + version + ".tgz";
}
