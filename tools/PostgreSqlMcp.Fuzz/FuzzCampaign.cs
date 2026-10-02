using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal static class FuzzCampaign
{
    private const string BridgeUrl = "https://github.com/Metalnem/libfuzzer-dotnet/releases/download/v2025.05.02.0904/libfuzzer-dotnet-ubuntu";
    private const string BridgeSha256 = "c2c2a90d94c409a4af339a0d4f244e0442c5a5d249be0f1252ba07871f285958";

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            int seconds = 60;
            if (args.Length > 2 || (args.Length == 2 &&
                (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds is < 1 or > 600)))
                throw new ArgumentException("Usage: dotnet run --project tools/PostgreSqlMcp.Fuzz -c Release -p:RestoreLockedMode=true -- campaign [seconds: 1..600]");
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("The pinned native fuzzing bridge requires Linux x64 (Ubuntu 24.04 in CI).");

            string root = FindRoot();
            string project = Path.Combine(root, "tools", "PostgreSqlMcp.Fuzz");
            string work = Path.Combine(root, "artifacts", "fuzz");
            string corpus = Path.Combine(work, "corpus");
            string crashes = Path.Combine(work, "crashes");
            string target = Path.Combine(work, "target");
            Directory.CreateDirectory(corpus);
            Directory.CreateDirectory(crashes);
            // Never instrument stale published output a second time.
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.CreateDirectory(target);

            string bridge = Path.Combine(work, "libfuzzer-dotnet");
            await DownloadBridgeAsync(bridge);
            await RunProcessAsync(root, "dotnet", 180, "tool", "restore");
            await RunProcessAsync(root, "dotnet", 300, "publish", Path.Combine(project, "PostgreSqlMcp.Fuzz.csproj"),
                "--configuration", "Release", "--no-self-contained", "-p:RestoreLockedMode=true", "--output", target);
            await RunProcessAsync(root, "dotnet", 60, "tool", "run", "sharpfuzz", "--",
                Path.Combine(target, "PostgreSqlMcp.Core.dll"), "PostgreSqlMcp.Core.SqlGuard");

            string assembly = Path.Combine(target, "PostgreSqlMcp.Fuzz.dll");
            string[] seeds = Directory.GetFiles(Path.Combine(project, "corpus")).Order(StringComparer.Ordinal).ToArray();
            if (seeds.Length == 0) throw new InvalidOperationException("The checked-in fuzz regression corpus is empty.");
            foreach (string seed in seeds)
            {
                // Standalone mode uses the same callback and preserves unexpected exceptions.
                await RunProcessAsync(root, "dotnet", 30, assembly, seed);
                File.Copy(seed, Path.Combine(corpus, Path.GetFileName(seed)), overwrite: true);
            }
            Console.WriteLine($"Replayed {seeds.Length} checked-in SQL fuzz regressions; starting {seconds}s coverage-guided campaign.");
            await RunProcessAsync(root, bridge, seconds + 60, "--target_path=dotnet", $"--target_arg={assembly}",
                $"-max_total_time={seconds}", "-timeout=5", "-rss_limit_mb=512", "-max_len=262146",
                $"-dict={Path.Combine(project, "sql.dict")}", $"-artifact_prefix={crashes}{Path.DirectorySeparatorChar}", corpus);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception is ArgumentException or PlatformNotSupportedException
                ? exception.Message : $"Fuzz campaign failed ({exception.GetType().Name}); sensitive exception details suppressed.");
            return 1;
        }
    }

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "postgresql-sharp-mcp.slnx"))) return directory.FullName;
        throw new ArgumentException("Run the fuzz campaign from the repository or a child directory.");
    }

    private static async Task DownloadBridgeAsync(string destination)
    {
        string temporary = destination + ".download";
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            using var client = new HttpClient();
            using HttpResponseMessage response = await client.GetAsync(BridgeUrl, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            await using (Stream input = await response.Content.ReadAsStreamAsync(deadline.Token))
            await using (FileStream output = File.Create(temporary))
                await input.CopyToAsync(output, deadline.Token);
            await using (FileStream input = File.OpenRead(temporary))
            {
                byte[] hash = await SHA256.HashDataAsync(input, deadline.Token);
                if (!Convert.ToHexString(hash).Equals(BridgeSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Downloaded fuzzing bridge failed SHA-256 verification.");
            }
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task RunProcessAsync(string root, string executable, int timeoutSeconds, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = root, UseShellExecute = false };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("Unable to start fuzzing subprocess.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Fuzzing subprocess exited with status {process.ExitCode}.");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync();
            }
        }
    }
}
