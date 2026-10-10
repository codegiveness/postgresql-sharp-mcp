using System.Diagnostics;

// Shared by the tool projects through <Compile Include=... Link=...>; this file is the only home of
// process spawning, output draining, deadlines and process-tree cleanup. Keep it free of tool-specific policy.

/// <summary>What the runner does with a child's stdout/stderr. Every call site must choose one.</summary>
internal sealed class OutputPolicy
{
    private enum Mode { Inherit, Capture, Suppress, Redact }

    private readonly Mode mode;
    private readonly string[] secrets;

    private OutputPolicy(Mode mode, string[] secrets) => (this.mode, this.secrets) = (mode, secrets);

    /// <summary>The child writes straight to this process's console; the runner never sees the output.</summary>
    public static OutputPolicy Inherit { get; } = new(Mode.Inherit, []);

    /// <summary>The raw output is returned to the caller, which decides whether it is safe to show. Nothing is echoed.</summary>
    public static OutputPolicy Capture { get; } = new(Mode.Capture, []);

    /// <summary>The output is drained and discarded; results carry empty strings.</summary>
    public static OutputPolicy Suppress { get; } = new(Mode.Suppress, []);

    /// <summary>
    /// The output is echoed to this process's stdout/stderr after exit with every occurrence of the given
    /// values replaced by <c>***</c>; results carry the redacted text. Only the supplied values are removed,
    /// so this is not a guarantee that arbitrary secrets cannot appear.
    /// </summary>
    public static OutputPolicy Redact(IEnumerable<string?> secrets) =>
        new(Mode.Redact, [.. secrets.Where(secret => !string.IsNullOrEmpty(secret)).Select(secret => secret!).Distinct(StringComparer.Ordinal)
            .OrderByDescending(secret => secret.Length)]);

    internal bool Redirects => mode != Mode.Inherit;
    internal bool Keeps => mode is Mode.Capture or Mode.Redact;

    internal string Apply(string text)
    {
        if (mode != Mode.Redact) return text;
        foreach (string secret in secrets) text = text.Replace(secret, "***", StringComparison.Ordinal);
        return text;
    }

    internal bool Echoes => mode == Mode.Redact;
}

/// <summary>One child process invocation. <see cref="Timeout"/> and <see cref="Output"/> are required so each call states them.</summary>
internal sealed record ProcessSpec(string File, IEnumerable<string> Arguments)
{
    public required TimeSpan Timeout { get; init; }
    public required OutputPolicy Output { get; init; }
    public string? WorkingDirectory { get; init; }
    /// <summary>Overrides applied to the inherited (or cleared) environment; a null value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }
    public bool ClearEnvironment { get; init; }
    /// <summary>Redirects stdin. Without <see cref="Input"/> the runner just closes it, so the child sees end of input.</summary>
    public bool RedirectInput { get; init; }
    public string? Input { get; init; }
}

internal sealed record ProcessResult(int ExitCode, string Output, string Error);

internal static class ProcessRunner
{
    /// <summary>
    /// Starts the process with the redirections implied by the spec. The caller owns the process; use this directly only
    /// for long-lived children it reads itself, otherwise <see cref="RunAsync"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A Windows .cmd/.bat command contains a shell metacharacter.</exception>
    public static Process Start(ProcessSpec spec)
    {
        var info = new ProcessStartInfo(spec.File)
        {
            UseShellExecute = false,
            RedirectStandardInput = spec.RedirectInput || spec.Input is not null,
            RedirectStandardOutput = spec.Output.Redirects,
            RedirectStandardError = spec.Output.Redirects
        };
        if (spec.WorkingDirectory is not null) info.WorkingDirectory = spec.WorkingDirectory;
        string[] arguments = [.. spec.Arguments];
        if (OperatingSystem.IsWindows() && Path.GetExtension(spec.File) is ".cmd" or ".bat")
        {
            string[] words = [spec.File, .. arguments];
            if (!words.All(word => !word.Contains('"') && !word.Contains('%') && !word.Contains('!') && !word.Contains('\r') && !word.Contains('\n')))
                throw new ArgumentException("Unsupported metacharacter in Windows command path or argument.");
            info.FileName = System.Environment.GetEnvironmentVariable("ComSpec")
                ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), "cmd.exe");
            info.Arguments = "/d /s /c \"" + string.Join(" ", words.Select(word => "\"" + word + "\"")) + "\"";
        }
        else
            foreach (string argument in arguments) info.ArgumentList.Add(argument);
        if (spec.ClearEnvironment) info.Environment.Clear();
        if (spec.Environment is not null)
            foreach (var (key, value) in spec.Environment)
                if (value is null) info.Environment.Remove(key);
                else info.Environment[key] = value;
        var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new IOException("Unable to start the subprocess.");
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs to completion under one deadline that covers stdin, exit and the output drains, then kills the owned process
    /// tree and observes both drains before returning or failing.
    /// </summary>
    /// <exception cref="TimeoutException">The deadline expired.</exception>
    public static async Task<ProcessResult> RunAsync(ProcessSpec spec)
    {
        using Process process = Start(spec);
        using var deadline = new CancellationTokenSource(spec.Timeout);
        Task<string> output = Drain(process, spec.Output, standardError: false, deadline.Token);
        Task<string> error = Drain(process, spec.Output, standardError: true, deadline.Token);
        Task drains = Task.WhenAll(output, error);
        try
        {
            if (process.StartInfo.RedirectStandardInput)
            {
                if (spec.Input is not null) await process.StandardInput.WriteAsync(spec.Input.AsMemory(), deadline.Token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(deadline.Token);
            await drains;
            string text = spec.Output.Apply(output.Result), diagnostics = spec.Output.Apply(error.Result);
            if (spec.Output.Echoes)
            {
                Console.Write(text);
                Console.Error.Write(diagnostics);
            }
            return new ProcessResult(process.ExitCode, text, diagnostics);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Subprocess exceeded its timeout.");
        }
        finally
        {
            try { await KillTreeAsync(process); }
            finally
            {
                await deadline.CancelAsync();
                // Observe both drains before disposing their streams, without masking the command failure.
                await drains.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    /// <summary>Kills the process and its descendants and waits for it; a no-op once it has exited.</summary>
    public static async Task KillTreeAsync(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (process.HasExited) { }
        await process.WaitForExitAsync();
    }

    private static Task<string> Drain(Process process, OutputPolicy policy, bool standardError, CancellationToken token)
    {
        if (!policy.Redirects) return Task.FromResult("");
        StreamReader reader = standardError ? process.StandardError : process.StandardOutput;
        return policy.Keeps ? reader.ReadToEndAsync(token) : Discard(reader, token);
    }

    private static async Task<string> Discard(StreamReader reader, CancellationToken token)
    {
        await reader.BaseStream.CopyToAsync(Stream.Null, token);
        return "";
    }
}
