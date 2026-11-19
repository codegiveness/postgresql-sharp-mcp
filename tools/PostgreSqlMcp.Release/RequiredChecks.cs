// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>One check run as the GitHub REST API reports it; <see cref="App"/> is the slug of the app that created it.</summary>
internal sealed record CheckRun(long Id, string Name, string Status, string? Conclusion, string? App);

/// <summary>
/// The checks a release commit must have passed. <see cref="Commit"/> checks also run on pushes to main and are judged on the release
/// commit itself; <see cref="PullRequest"/> checks exist only for pull requests, so they are judged on the head of the pull request that
/// merged the release commit. Every check must come from <see cref="App"/>: a check run is just a name, and any installed app may create one.
/// </summary>
internal sealed record RequiredCheckPolicy(string App, IReadOnlyList<string> Commit, IReadOnlyList<string> PullRequest)
{
    public static RequiredCheckPolicy Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("The required-checks policy must be a JSON object.");
        foreach (var property in root.EnumerateObject())
            if (property.Name is not ("app" or "commit" or "pullRequest"))
                throw new InvalidOperationException($"The required-checks policy has an unknown property '{property.Name}'.");
        string app = root.TryGetProperty("app", out var slug) && slug.ValueKind == JsonValueKind.String ? slug.GetString()! : "";
        if (app.Length == 0) throw new InvalidOperationException("The required-checks policy must name the app that reports the checks.");
        var commit = Names(root, "commit");
        var pullRequest = Names(root, "pullRequest");
        if (commit.Count == 0) throw new InvalidOperationException("The required-checks policy must require at least one commit check.");
        var duplicate = commit.Concat(pullRequest).GroupBy(name => name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"The required-checks policy lists '{duplicate.Key}' more than once.");
        return new RequiredCheckPolicy(app, commit, pullRequest);
    }

    private static List<string> Names(JsonElement root, string property)
    {
        var names = new List<string>();
        if (!root.TryGetProperty(property, out var array)) return names;
        if (array.ValueKind != JsonValueKind.Array) throw new InvalidOperationException($"The required-checks policy property '{property}' must be an array of names.");
        foreach (var item in array.EnumerateArray())
        {
            string name = item.ValueKind == JsonValueKind.String ? item.GetString()! : "";
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim())
                throw new InvalidOperationException($"The required-checks policy property '{property}' must contain non-empty names without surrounding whitespace.");
            names.Add(name);
        }
        return names;
    }
}

internal static class RequiredChecks
{
    public static readonly string PolicyFile = Path.Combine(".github", "required-checks.json");

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Judges each required name by its newest run from <paramref name="app"/>; an earlier attempt is superseded by a re-run.
    /// Missing and unfinished checks are pending, finished checks without a passing conclusion have failed.
    /// </summary>
    public static (IReadOnlyList<string> Pending, IReadOnlyList<string> Failed) Evaluate(string app, IEnumerable<string> required, IEnumerable<CheckRun> runs)
    {
        var newest = runs.Where(run => run.App == app).GroupBy(run => run.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.MaxBy(run => run.Id)!, StringComparer.Ordinal);
        var pending = new List<string>();
        var failed = new List<string>();
        foreach (string name in required)
        {
            if (!newest.TryGetValue(name, out var run)) pending.Add($"{name} (not reported by {app})");
            else if (run.Status != "completed") pending.Add($"{name} ({run.Status})");
            else if (run.Conclusion is not ("success" or "skipped" or "neutral")) failed.Add($"{name} ({run.Conclusion ?? "no conclusion"})");
        }
        return (pending, failed);
    }

    /// <summary>
    /// Returns once every required check passed. A failure ends it immediately; pending checks are re-evaluated every
    /// <paramref name="interval"/> until <paramref name="wait"/> has elapsed, then count as failed, so nothing missing is ever treated as green.
    /// </summary>
    public static async Task RequireAsync(string description, string app, IReadOnlyList<string> required,
        Func<Task<IReadOnlyList<CheckRun>>> fetch, TimeSpan wait, TimeSpan interval)
    {
        if (required.Count == 0) return;
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var (pending, failed) = Evaluate(app, required, await fetch());
            if (failed.Count > 0)
                throw new InvalidOperationException($"Required checks failed on {description}: {string.Join(", ", failed)}.");
            if (pending.Count == 0)
            {
                Console.WriteLine($"All {required.Count} required checks passed on {description}.");
                return;
            }
            if (clock.Elapsed >= wait)
                throw new InvalidOperationException($"Required checks are not green on {description}: {string.Join(", ", pending)}.");
            Console.WriteLine($"Waiting for checks on {description}: {string.Join(", ", pending)}");
            await Task.Delay(interval);
        }
    }

    /// <summary>
    /// <c>required-checks --sha &lt;commit&gt; [--policy-file path] [--wait-minutes n]</c>. Reads GH_TOKEN (needs checks: read and pull-requests: read)
    /// and GITHUB_REPOSITORY from the environment; the token is only ever sent to api.github.com.
    /// </summary>
    public static async Task RunAsync(string root, string[] args)
    {
        string? sha = null, policyFile = null;
        int waitMinutes = 0;
        for (int i = 1; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new InvalidOperationException("Every required-checks option needs a value.");
            switch (args[i])
            {
                case "--sha": sha = args[i + 1]; break;
                case "--policy-file": policyFile = Path.GetFullPath(args[i + 1]); break;
                case "--wait-minutes":
                    if (!int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out waitMinutes) || waitMinutes > 120)
                        throw new InvalidOperationException("--wait-minutes must be a whole number from 0 to 120.");
                    break;
                default: throw new InvalidOperationException("Usage: required-checks --sha <commit> [--policy-file path] [--wait-minutes 0-120]");
            }
        }
        if (sha is null || !Regex.IsMatch(sha, "\\A[0-9a-f]{40}\\z", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("--sha must be a full lowercase 40-character commit hash.");
        string repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "";
        if (!Regex.IsMatch(repository, "\\A[A-Za-z0-9._-]+/[A-Za-z0-9._-]+\\z", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("GITHUB_REPOSITORY must be <owner>/<repo>.");
        string token = Environment.GetEnvironmentVariable("GH_TOKEN") ?? "";
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("GH_TOKEN is required to read check runs.");

        var policy = RequiredCheckPolicy.Parse(await File.ReadAllTextAsync(policyFile ?? Path.Combine(root, PolicyFile)));
        using var http = GitHubChecks.CreateClient(token);
        var github = new GitHubChecks(http, repository);
        TimeSpan wait = TimeSpan.FromMinutes(waitMinutes);
        await RequireAsync($"commit {sha}", policy.App, policy.Commit, () => github.CheckRunsAsync(sha), wait, PollInterval);
        if (policy.PullRequest.Count > 0)
        {
            string head = await github.MergedPullRequestHeadAsync(sha);
            // Squash-merging an up-to-date branch yields the head's exact tree. A different tree means the release commit holds content the PR checks never saw.
            string releaseTree = await github.TreeAsync(sha), headTree = await github.TreeAsync(head);
            if (releaseTree != headTree)
                throw new InvalidOperationException($"Commit {sha} (tree {releaseTree}) does not match the merged pull request head {head} (tree {headTree}); pull-request checks do not cover it.");
            await RequireAsync($"the merged pull request head {head}", policy.App, policy.PullRequest, () => github.CheckRunsAsync(head), wait, PollInterval);
        }
    }
}

/// <summary>The two GitHub REST reads the gate needs; any failed request fails the gate rather than being treated as "no checks".</summary>
internal sealed class GitHubChecks(HttpClient http, string repository)
{
    private const int PageSize = 100;
    private const int MaxPages = 50;

    public static HttpClient CreateClient(string token)
    {
        var http = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("postgresql-sharp-mcp-release", "1"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    public async Task<IReadOnlyList<CheckRun>> CheckRunsAsync(string sha)
    {
        var runs = new List<CheckRun>();
        for (int page = 1; page <= MaxPages; page++)
        {
            using var document = await GetAsync($"repos/{repository}/commits/{sha}/check-runs?per_page={PageSize}&page={page.ToString(CultureInfo.InvariantCulture)}");
            var root = document.RootElement;
            var batch = root.GetProperty("check_runs");
            foreach (var run in batch.EnumerateArray())
                runs.Add(new CheckRun(run.GetProperty("id").GetInt64(), run.GetProperty("name").GetString() ?? "", run.GetProperty("status").GetString() ?? "",
                    run.GetProperty("conclusion").GetString(),
                    run.TryGetProperty("app", out var app) && app.ValueKind == JsonValueKind.Object ? app.GetProperty("slug").GetString() : null));
            if (runs.Count >= root.GetProperty("total_count").GetInt32() || batch.GetArrayLength() == 0) return runs;
        }
        throw new InvalidOperationException($"Commit {sha} reports more than {MaxPages * PageSize} check runs; refusing to judge a partial list.");
    }

    /// <summary>The head commit of the one merged pull request into main whose merge commit is <paramref name="sha"/>.</summary>
    public async Task<string> MergedPullRequestHeadAsync(string sha)
    {
        using var document = await GetAsync($"repos/{repository}/commits/{sha}/pulls?per_page={PageSize}");
        var merged = document.RootElement.EnumerateArray()
            .Where(pull => pull.GetProperty("merged_at").ValueKind == JsonValueKind.String &&
                           pull.GetProperty("merge_commit_sha").GetString() == sha &&
                           pull.GetProperty("base").GetProperty("ref").GetString() == "main")
            .ToArray();
        if (merged.Length != 1)
            throw new InvalidOperationException($"Commit {sha} must be the merge commit of exactly one pull request into main, found {merged.Length}; pull-request checks cannot be judged.");
        return merged[0].GetProperty("head").GetProperty("sha").GetString() ?? throw new InvalidOperationException("The merged pull request has no head commit.");
    }

    public async Task<string> TreeAsync(string commit)
    {
        using var document = await GetAsync($"repos/{repository}/git/commits/{commit}");
        return document.RootElement.GetProperty("tree").GetProperty("sha").GetString() ?? throw new InvalidOperationException($"Commit {commit} has no tree.");
    }

    private async Task<JsonDocument> GetAsync(string path)
    {
        using var response = await http.GetAsync(path);
        // The body is not echoed: error responses can repeat request details.
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub API request failed with HTTP {(int)response.StatusCode} for {path.Split('?')[0]}.");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
