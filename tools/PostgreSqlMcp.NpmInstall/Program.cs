// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;

try
{
    if (args.Length != 0)
        throw new ArgumentException("This installer takes no arguments.");

    Architecture processArchitecture = RuntimeInformation.ProcessArchitecture;
    string architecture = processArchitecture == Architecture.X64 ? "x64" :
        processArchitecture == Architecture.Arm64 ? "arm64" :
        throw new PlatformNotSupportedException("Only x64 and arm64 are supported.");
    string platform = OperatingSystem.IsWindows() ? "win" :
        OperatingSystem.IsMacOS() ? "osx" :
        OperatingSystem.IsLinux() && !RuntimeInformation.RuntimeIdentifier.StartsWith("linux-musl", StringComparison.Ordinal) ? "linux" :
        throw new PlatformNotSupportedException("Supported platforms are Windows x64, glibc Linux x64/arm64, and macOS x64/arm64.");
    // The main package's postinstall runs this from the matching platform package's bin link, with the main
    // package as working directory. The payload (this executable's directory) is that platform package's runtime.
    string root = Environment.CurrentDirectory;
    string payload = AppContext.BaseDirectory;
    if (!File.Exists(Path.Combine(root, "package.json")) || !File.Exists(Path.Combine(root, "bin", "postgresql-sharp-mcp.exe")))
        throw new InvalidOperationException("Run this installer only through the npm package's postinstall script.");
    string source = Path.Combine(payload, OperatingSystem.IsWindows() ? "PostgreSqlMcp.exe" : "PostgreSqlMcp");
    if (!File.Exists(source))
        throw new InvalidDataException($"The packaged server executable is missing for {platform}-{architecture}.");

    foreach (string name in new[] { "PostgreSqlMcp.dll", "PostgreSqlMcp.deps.json", "PostgreSqlMcp.runtimeconfig.json" })
        if (!File.Exists(Path.Combine(payload, name)))
            throw new InvalidDataException($"The package is incomplete: {name} is missing from the {platform}-{architecture} runtime.");

    string bin = Path.Combine(root, "bin");
    Directory.CreateDirectory(bin);
    foreach (string file in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories))
    {
        if (Path.GetFileName(file).StartsWith("PostgreSqlMcp.NpmInstall", StringComparison.Ordinal) || file == source)
            continue;
        string destination = Path.Combine(bin, Path.GetRelativePath(payload, file));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(file, destination, overwrite: true);
    }

    // npm links bins before postinstall. Replace the real seed executable in place;
    // Unix symlinks and Windows cmd-shims continue to target this exact path.
    string executable = Path.Combine(bin, "postgresql-sharp-mcp.exe");
    File.Copy(source, executable, overwrite: true);
    if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

    // Exercise the installed self-contained command, including its bundled runtime.
    ProcessResult result = await ProcessRunner.RunAsync(new ProcessSpec(executable, ["--version"])
    {
        Timeout = Timeout.InfiniteTimeSpan,
        Output = OutputPolicy.Capture
    });
    if (result.ExitCode != 0)
    {
        if (!string.IsNullOrWhiteSpace(result.Error))
            await Console.Error.WriteAsync(result.Error);
        throw new InvalidOperationException("The bundled executable could not start. Check that this OS supports the packaged .NET runtime and its native library prerequisites.");
    }
    return 0;
}
catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException or
    PlatformNotSupportedException or InvalidOperationException or System.ComponentModel.Win32Exception)
{
    await Console.Error.WriteLineAsync($"postgresql-sharp-mcp: {exception.Message}");
    return 1;
}
