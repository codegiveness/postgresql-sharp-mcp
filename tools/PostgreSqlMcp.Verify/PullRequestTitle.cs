// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;

namespace PostgreSqlMcp.Verify;

/// <summary>
/// Checks that a pull-request title is a Conventional Commit header, because the squash commit on main is the title
/// (docs/git-workflow.md, Commits). Messages never repeat the title: it is untrusted, and Actions interprets some stdout lines.
/// </summary>
internal static partial class PullRequestTitle
{
    private static readonly string[] Types = ["feat", "fix", "docs", "ci", "build", "perf", "refactor", "test", "chore"];

    public static void Run(string? title)
    {
        string? problem = Problem(title);
        Check.That(problem is null, $"Pull-request title rejected: {problem}");
        Console.WriteLine("Pull-request title is a Conventional Commit header.");
    }

    /// <returns>Why the title is rejected, or null when it is valid.</returns>
    public static string? Problem(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "the title is empty (the semantic workflow passes it in PR_TITLE).";
        Match header = Header().Match(title);
        if (!header.Success) return "use '<type>(<optional scope>)<optional !>: <summary>'.";
        if (!Types.Contains(header.Groups["type"].Value, StringComparer.Ordinal)) return $"the type must be one of: {string.Join(", ", Types)}.";
        string summary = header.Groups["summary"].Value;
        if (char.IsUpper(summary[0])) return "the summary must start with a lowercase letter.";
        return summary is [.., '.'] ? "the summary must not end with a period." : null;
    }

    [GeneratedRegex(@"^(?<type>[a-z]+)(\([^()\r\n]+\))?!?: (?<summary>\S[^\r\n]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Header();
}
