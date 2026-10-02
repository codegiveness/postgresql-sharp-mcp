using System.Diagnostics;
using System.Runtime.InteropServices;

try
{
    if (args.Length != 0)
        throw new ArgumentException("This installer takes no arguments.");

    var architecture = RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException("Only x64 and arm64 are supported.")
    };
    var platform = OperatingSystem.IsWindows() ? "win" :
        OperatingSystem.IsMacOS() ? "osx" :
        OperatingSystem.IsLinux() && !RuntimeInformation.RuntimeIdentifier.StartsWith("linux-musl", StringComparison.Ordinal) ? "linux" :
        throw new PlatformNotSupportedException("Supported platforms are Windows x64, glibc Linux x64/arm64, and macOS x64/arm64.");
    var rid = $"{platform}-{architecture}";
    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    var source = Path.Combine(root, "apphosts", rid, OperatingSystem.IsWindows() ? "PostgreSqlMcp.exe" : "PostgreSqlMcp");
    if (!File.Exists(source))
        throw new PlatformNotSupportedException($"No packaged native executable is available for {rid}.");

    var payload = Path.Combine(root, "payload");
    foreach (var name in new[] { "PostgreSqlMcp.dll", "PostgreSqlMcp.deps.json", "PostgreSqlMcp.runtimeconfig.json" })
        if (!File.Exists(Path.Combine(payload, name)))
            throw new InvalidDataException($"The package is incomplete: payload/{name} is missing.");

    var bin = Path.Combine(root, "bin");
    Directory.CreateDirectory(bin);
    foreach (var file in Directory.EnumerateFiles(payload))
        File.Copy(file, Path.Combine(bin, Path.GetFileName(file)), overwrite: true);

    // npm links bins before postinstall. Replace the real seed executable in place;
    // Unix symlinks and Windows cmd-shims continue to target this exact path.
    var executable = Path.Combine(bin, "postgresql-sharp-mcp.exe");
    File.Copy(source, executable, overwrite: true);
    if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

    // Verify native runtime discovery too: finding dotnet on PATH alone does not
    // register a nonstandard runtime installation for a framework-dependent apphost.
    var start = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    start.ArgumentList.Add("--version");
    using var process = Process.Start(start) ?? throw new IOException("Unable to start the installed executable.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    await stdout;
    var diagnostics = await stderr;
    if (process.ExitCode != 0)
    {
        if (!string.IsNullOrWhiteSpace(diagnostics))
            Console.Error.Write(diagnostics);
        throw new InvalidOperationException("The .NET 10 runtime is required. For a nonstandard installation, set DOTNET_ROOT to its directory. This package never downloads a runtime.");
    }
    return 0;
}
catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or
    PlatformNotSupportedException or InvalidOperationException or System.ComponentModel.Win32Exception)
{
    Console.Error.WriteLine($"postgresql-sharp-mcp: {exception.Message}");
    return 1;
}
