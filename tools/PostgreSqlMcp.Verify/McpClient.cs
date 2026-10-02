using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PostgreSqlMcp.Verify;

internal sealed class McpClient : IAsyncDisposable
{
    private readonly Process process;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> pending = new();
    private readonly SemaphoreSlim writes = new(1);
    private readonly CancellationTokenSource reading = new();
    private readonly Task reader;
    private readonly Task<string> errors;
    private int nextId;
    private bool stopped;
    public string StandardError { get; private set; } = "";

    private McpClient(Command command, IReadOnlyDictionary<string, string> environment)
    {
        try { process = Processes.Start(command, environment); }
        catch
        {
            writes.Dispose();
            reading.Dispose();
            throw;
        }
        errors = process.StandardError.ReadToEndAsync(reading.Token);
        reader = ReadAsync();
    }

    public static async Task<McpClient> StartAsync(Command command, IReadOnlyDictionary<string, string> environment)
    {
        var client = new McpClient(command, environment);
        try
        {
            JsonNode initialized = await client.RequestAsync("initialize", new
            {
                protocolVersion = "2025-06-18", capabilities = new { },
                clientInfo = new { name = "postgresql-sharp-mcp-verifier", version = "1" }
            });
            Check.That(initialized["result"]?["serverInfo"] is not null, "MCP initialize did not return server information.");
            await client.SendAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private async Task ReadAsync()
    {
        Exception failure = new VerificationException("MCP process exited before responding.");
        try
        {
            while (await process.StandardOutput.ReadLineAsync(reading.Token) is { } line)
            {
                JsonNode message = JsonNode.Parse(line) ?? throw new VerificationException("Empty JSON-RPC response.");
                Check.That(message["jsonrpc"].Text() == "2.0", "Non-protocol output on MCP stdout.");
                if (message["id"] is JsonValue id && id.TryGetValue<int>(out int number) && pending.TryRemove(number, out var completion))
                    completion.TrySetResult(message);
            }
        }
        catch (OperationCanceledException) when (reading.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            failure = new VerificationException("Invalid MCP stdout or failed response reader; captured input suppressed.");
            throw failure;
        }
        finally
        {
            foreach (var completion in pending.Values) completion.TrySetException(failure);
        }
    }

    private async Task SendAsync(object message, CancellationToken ct = default)
    {
        await writes.WaitAsync(ct);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
        }
        finally { writes.Release(); }
    }

    public async Task<JsonNode> RequestAsync(string method, object parameters)
    {
        int id = Interlocked.Increment(ref nextId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(reading.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var completion = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, deadline.Token);
            return await completion.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !reading.IsCancellationRequested)
        { throw new TimeoutException("MCP request exceeded its timeout."); }
        finally { pending.TryRemove(id, out _); }
    }

    public async Task<(bool Error, JsonNode Data)> CallAsync(string tool, object? arguments = null)
    {
        JsonNode envelope = await RequestAsync("tools/call", new { name = tool, arguments = arguments ?? new { } });
        Check.That(envelope["result"] is not null, $"MCP tools/call envelope failed for {tool}.");
        JsonNode result = envelope["result"]!;
        string text = result["content"]![0]!["text"].Text();
        Check.That(Encoding.UTF8.GetByteCount(text) <= 4096, $"Tool response exceeded byte budget: {tool}.");
        JsonNode data = JsonNode.Parse(text)!;
        Check.That(JsonNode.DeepEquals(data, result["structuredContent"]), $"Tool text and structured content disagree: {tool}.");
        return (result["isError"]?.Flag() ?? false, data);
    }

    public async Task<JsonNode> OkAsync(string tool, object? arguments = null)
    {
        var (error, data) = await CallAsync(tool, arguments);
        Check.That(!error, $"Expected successful tool result: {tool}. Captured arguments/results suppressed.");
        return data;
    }

    public async Task<JsonNode> FailsAsync(string tool, object arguments, string? code = null, string? state = null)
    {
        var (error, data) = await CallAsync(tool, arguments);
        Check.That(error, $"Expected tool failure: {tool}.");
        if (code is not null) Check.That(data["error"]?["code"].Text() == code, $"Unexpected error code from {tool}; expected {code}.");
        if (state is not null) Check.That(data["error"]?["sql_state"].Text() == state, $"Unexpected SQLSTATE from {tool}; expected {state}.");
        JsonNode? target = JsonSerializer.SerializeToNode(arguments)?["database"];
        if (target is not null) Check.That(JsonNode.DeepEquals(target, data["database"]), "Error response lost requested target identity.");
        return data;
    }

    public async Task StopAsync(bool terminate = false)
    {
        if (stopped) return;
        Task inputClosed = Task.CompletedTask;
        try
        {
            if (terminate) Unix.Terminate(process);
            else
            {
                inputClosed = process.StandardInput.DisposeAsync().AsTask();
                await inputClosed.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await reader.WaitAsync(TimeSpan.FromSeconds(5));
            StandardError = await errors.WaitAsync(TimeSpan.FromSeconds(5));
            Check.That(process.ExitCode == 0 || (terminate && process.ExitCode is 143 or -15), "MCP process failed during shutdown.");
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
                stopped = true;
                await reading.CancelAsync();
                // Join both readers even when shutdown failed, preserving its original diagnostic.
                await Task.WhenAll(inputClosed, reader, errors).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); }
        finally
        {
            process.Dispose();
            reading.Dispose();
            writes.Dispose();
        }
    }
}
