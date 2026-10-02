using System.Diagnostics;
using System.Globalization;

namespace PostgreSqlMcp.Verify;

internal static class ResourceChecks
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temporary = new TemporaryDirectory();
        string pidFile = Path.Combine(temporary.Path, "child.pid");
        string quotedPath = "'" + pidFile.Replace("'", "'\"'\"'") + "'";
        try
        {
            // More input than a pipe can hold: the child intentionally never reads it.
            Task<ProcessResult> blocked = Processes.RunAsync(new("/bin/sh", "-c",
                $"echo $$ > {quotedPath}; exec sleep 30"), input: new string('x', 1_048_576), timeout: 2);
            await ExpectDeadlineAsync(blocked);
            int pid = int.Parse(await File.ReadAllTextAsync(pidFile), CultureInfo.InvariantCulture);
            Check.That(!IsRunning(pid), "Timed-out stdin writer left its child running.");
        }
        finally { await TerminateAsync(pidFile); }

        File.Delete(pidFile);
        try
        {
            // The parent exits, but its descendant retains both redirected output pipes.
            Task<ProcessResult> inherited = Processes.RunAsync(new("/bin/sh", "-c",
                $"sleep 30 & echo $! > {quotedPath}; exit 0"), timeout: 2);
            await ExpectDeadlineAsync(inherited);
        }
        finally
        {
            // An already-orphaned descendant is no longer in Process.Kill's owned tree.
            await TerminateAsync(pidFile);
        }

        File.Delete(pidFile);
        McpClient? client = null;
        try
        {
            string initialized = """{"jsonrpc":"2.0","id":1,"result":{"serverInfo":{"name":"blocked-input","version":"1"}}}""";
            client = await McpClient.StartAsync(new("/bin/sh", "-c",
                $"echo $$ > {quotedPath}; read -r request; printf '%s\\n' '{initialized}'; read -r notification; exec sleep 60"),
                Processes.CleanEnvironment());
            await ExpectDeadlineAsync(client.RequestAsync("tools/call",
                new { payload = new string('x', 1_048_576) }), 35);
            McpClient connected = client;
            // Keep the harness responsive even if stdin disposal regresses to a synchronous flush.
            Task disposing = Task.Run(async () => await connected.DisposeAsync());
            await ExpectDeadlineAsync(disposing, 15);
            client = null;
            int pid = int.Parse(await File.ReadAllTextAsync(pidFile), CultureInfo.InvariantCulture);
            Check.That(!IsRunning(pid), "Failed MCP writer cleanup left its child running.");
        }
        finally
        {
            await TerminateAsync(pidFile);
            if (client is not null) await client.DisposeAsync();
        }
        Console.WriteLine("PASS MCP request deadline covers blocked stdin; failed-writer disposal is bounded and cleans up its child");
        Console.WriteLine("PASS subprocess deadlines cover blocked stdin and inherited output pipes; owned child cleanup verified");
    }

    private static async Task ExpectDeadlineAsync(Task operation, int timeout = 10)
    {
        try { await operation.WaitAsync(TimeSpan.FromSeconds(timeout)); }
        catch (TimeoutException) when (operation.IsFaulted && operation.Exception?.InnerException is TimeoutException)
        { return; }
        throw new VerificationException("Blocked subprocess did not fail with its own deadline.");
    }

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task TerminateAsync(string pidFile)
    {
        if (!File.Exists(pidFile)) return;
        int pid = int.Parse(await File.ReadAllTextAsync(pidFile), CultureInfo.InvariantCulture);
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync();
            }
        }
        catch (ArgumentException) { }
    }
}
