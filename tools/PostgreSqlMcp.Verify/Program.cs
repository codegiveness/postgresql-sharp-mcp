using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PostgreSqlMcp.Verify;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            string root = FindRoot();
            if (args is ["integration"])
                await Integration.RunAsync(root);
            else if (args is ["sarif", "--baseline", var baseline, "--candidate", var candidate])
                SarifGate.Run(baseline, candidate, Console.Out);
            else if (args is ["sarif-regressions"])
                SarifRegression.Run();
            else if (args.Length > 0 && args[0] == "packages")
            {
                string artifacts = Path.Combine(root, "artifacts", "packages");
                string? targetsFile = null;
                bool installationOnly = false;
                for (int i = 1; i < args.Length;)
                {
                    string option = args[i++];
                    if (option == "--installation-only")
                    {
                        installationOnly = true;
                        continue;
                    }
                    Check.That(i < args.Length, "Missing verifier option value.");
                    string value = args[i++];
                    switch (option)
                    {
                        case "--artifacts": artifacts = Path.GetFullPath(value); break;
                        case "--targets-file": targetsFile = Path.GetFullPath(value); break;
                        default: throw new VerificationException("Unknown verifier option.");
                    }
                }
                Check.That(!installationOnly || targetsFile is null, "--installation-only cannot be combined with --targets-file.");
                await Packages.RunAsync(root, artifacts, targetsFile, installationOnly);
            }
            else
                throw new VerificationException("Usage: integration | packages [--artifacts directory] [--targets-file disposable-targets.json | --installation-only] | sarif --baseline directory --candidate directory | sarif-regressions");
            return 0;
        }
        catch (Exception ex)
        {
            // Unexpected parser/provider/process exceptions can embed inputs, including credentials.
            Console.Error.WriteLine(ex is VerificationException ? ex.Message : $"Verification failed ({ex.GetType().Name}); sensitive exception details suppressed.");
            Console.Error.WriteLine(new StackTrace(ex, fNeedFileInfo: false));
            return 1;
        }
    }

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "postgresql-sharp-mcp.slnx"))) return directory.FullName;
        throw new VerificationException("Run the verifier from the repository or a child directory.");
    }
}

internal sealed class VerificationException(string message) : Exception(message)
{
}

internal static class Check
{
    public static void That(bool condition, string description)
    {
        if (!condition) throw new VerificationException(description);
    }

    public static void Equal(JsonNode? actual, object? expected, string description) =>
        That(JsonNode.DeepEquals(actual, JsonSerializer.SerializeToNode(expected)), description);

    public static string Text(this JsonNode? node) => node!.GetValue<string>();
    public static int Int(this JsonNode? node) => node!.GetValue<int>();
    public static long Long(this JsonNode? node) => node!.GetValue<long>();
    public static double Number(this JsonNode? node) => node!.GetValue<double>();
    public static bool Flag(this JsonNode? node) => node!.GetValue<bool>();
    public static JsonArray Array(this JsonNode? node) => node!.AsArray();
    public static IEnumerable<Dictionary<string, JsonNode?>> Rows(JsonNode page)
    {
        string[] names = page["columns"].Array().Select(c => c!["name"].Text()).ToArray();
        return page["rows"].Array().Select(row => names.Zip(row.Array()).ToDictionary(pair => pair.First, pair => pair.Second));
    }

    public static void Confidential(string text, params string[] secrets) =>
        That(secrets.All(secret => !text.Contains(secret, StringComparison.Ordinal)), "Diagnostic output exposed sensitive input or result data.");
}

internal sealed record Command(string File, params string[] Arguments)
{
    public string? WorkingDirectory { get; init; }
    public int InitializationTimeoutSeconds { get; init; } = 20;
    public Command With(params string[] arguments) => this with { Arguments = [.. Arguments, .. arguments] };
}

internal sealed record ProcessResult(int ExitCode, string Output, string Error);

internal static class Processes
{
    public static Dictionary<string, string> CleanEnvironment() => Environment.GetEnvironmentVariables()
        .Cast<System.Collections.DictionaryEntry>()
        .Where(entry => !((string)entry.Key).StartsWith("POSTGRES_", StringComparison.OrdinalIgnoreCase))
        .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static Process Start(Command command, IReadOnlyDictionary<string, string>? environment = null, string? directory = null)
    {
        var info = new ProcessStartInfo(command.File)
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = directory ?? command.WorkingDirectory ?? Environment.CurrentDirectory
        };
        if (OperatingSystem.IsWindows() && Path.GetExtension(command.File) is ".cmd" or ".bat")
        {
            string[] words = [command.File, .. command.Arguments];
            Check.That(words.All(word => !word.Contains('"') && !word.Contains('%') && !word.Contains('!') && !word.Contains('\r') && !word.Contains('\n')),
                "Unsupported metacharacter in Windows command path or argument.");
            info.FileName = Environment.GetEnvironmentVariable("ComSpec")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            info.Arguments = "/d /s /c \"" + string.Join(" ", words.Select(word => "\"" + word + "\"")) + "\"";
        }
        else
            foreach (string argument in command.Arguments) info.ArgumentList.Add(argument);
        if (environment is not null)
        {
            info.Environment.Clear();
            foreach (var (key, value) in environment) info.Environment[key] = value;
        }
        var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new VerificationException("Could not start verification subprocess.");
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public static async Task<ProcessResult> RunAsync(Command command, IReadOnlyDictionary<string, string>? environment = null,
        int? expected = 0, string? input = null, int timeout = 45, string? directory = null)
    {
        using Process process = Start(command, environment, directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        Task<string> output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(deadline.Token);
        Task drains = Task.WhenAll(output, error);
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            await drains;
            var result = new ProcessResult(process.ExitCode, output.Result, error.Result);
            if (expected is not null)
                Check.That(result.ExitCode == expected, $"{Path.GetFileName(command.File)} exited with status {result.ExitCode}; expected {expected}. Captured output suppressed to protect credentials.");
            return result;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Console.Error.WriteLine($"{Path.GetFileName(command.File)} exceeded its {timeout}-second timeout.");
            throw new TimeoutException("Verification subprocess exceeded its timeout.");
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
                await deadline.CancelAsync();
                // Observe both drains before disposing their streams, without masking the command failure.
                await drains.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    public static string FindExecutable(string name)
    {
        string[] suffixes = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat", ""] : [""];
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (string suffix in suffixes)
            {
                string file = Path.Combine(directory, name + suffix);
                if (File.Exists(file)) return Path.GetFullPath(file);
            }
        throw new VerificationException($"Required executable not found: {name}.");
    }
}

internal static class Unix
{
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
    [DllImport("libc")]
    private static extern uint geteuid();
    public static bool IsRoot => !OperatingSystem.IsWindows() && geteuid() == 0;
    public static void Terminate(Process process) => Check.That(kill(process.Id, 15) == 0, "Could not deliver SIGTERM to native MCP process.");
}

internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("postgresql-mcp-verify-").FullName;
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
