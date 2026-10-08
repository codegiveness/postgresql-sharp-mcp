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
    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
    var payload = AppContext.BaseDirectory;
    var source = Path.Combine(payload, OperatingSystem.IsWindows() ? "PostgreSqlMcp.exe" : "PostgreSqlMcp");
    if (!File.Exists(source))
        throw new InvalidDataException($"The packaged server executable is missing for {platform}-{architecture}.");

    foreach (var name in new[] { "PostgreSqlMcp.dll", "PostgreSqlMcp.deps.json", "PostgreSqlMcp.runtimeconfig.json" })
        if (!File.Exists(Path.Combine(payload, name)))
            throw new InvalidDataException($"The package is incomplete: {name} is missing from the selected runtime.");

    var bin = Path.Combine(root, "bin");
    Directory.CreateDirectory(bin);
    foreach (var file in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories))
    {
        if (Path.GetFileName(file).StartsWith("PostgreSqlMcp.NpmInstall", StringComparison.Ordinal) || file == source)
            continue;
        var destination = Path.Combine(bin, Path.GetRelativePath(payload, file));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(file, destination, overwrite: true);
    }

    // npm links bins before postinstall. Replace the real seed executable in place;
    // Unix symlinks and Windows cmd-shims continue to target this exact path.
    var executable = Path.Combine(bin, "postgresql-sharp-mcp.exe");
    File.Copy(source, executable, overwrite: true);
    if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

    // Exercise the installed self-contained command, including its bundled runtime.
    var start = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    start.ArgumentList.Add("--version");
    using var process = new Process { StartInfo = start };
    if (!process.Start()) throw new IOException("Unable to start the installed executable.");
    using var reading = new CancellationTokenSource();
    var stdout = process.StandardOutput.ReadToEndAsync(reading.Token);
    var stderr = process.StandardError.ReadToEndAsync(reading.Token);
    Task drains = Task.WhenAll(stdout, stderr);
    try
    {
        await process.WaitForExitAsync();
        await drains;
        var diagnostics = stderr.Result;
        if (process.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(diagnostics))
                Console.Error.Write(diagnostics);
            throw new InvalidOperationException("The bundled executable could not start. Check that this OS supports the packaged .NET runtime and its native library prerequisites.");
        }
    }
    finally
    {
        try
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync();
            }
        }
        finally
        {
            await reading.CancelAsync();
            await drains.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
    return 0;
}
catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or
    PlatformNotSupportedException or InvalidOperationException or System.ComponentModel.Win32Exception)
{
    Console.Error.WriteLine($"postgresql-sharp-mcp: {exception.Message}");
    return 1;
}
