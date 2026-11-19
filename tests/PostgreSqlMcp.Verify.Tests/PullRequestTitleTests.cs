// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

namespace PostgreSqlMcp.Verify.Tests;

public sealed class PullRequestTitleTests
{
    [Theory]
    [InlineData("build: adopt sibling testing, analyzer, CI and release-gate practices (0.7.3)")]
    [InlineData("fix(guard): reject a second statement after a dollar-quoted body")]
    [InlineData("feat!: verify TLS by default")]
    [InlineData("build(deps): bump the github-actions group with 2 updates")]
    [InlineData("docs: explain 0.x versioning")]
    public void Accepts_conventional_headers(string title) => Assert.Null(PullRequestTitle.Problem(title));

    [Theory]
    [InlineData(null, "empty")]
    [InlineData("   ", "empty")]
    [InlineData("Update README", "<type>")]
    [InlineData("build:missing space", "<type>")]
    [InlineData("build: ", "<type>")]
    [InlineData("style: reformat", "type must be one of")]
    [InlineData("Build: capitalized type", "<type>")]
    [InlineData("fix: Capitalized summary", "lowercase")]
    [InlineData("fix: trailing period.", "period")]
    [InlineData("fix: first line\nsecond line", "<type>")]
    public void Rejects_with_a_reason_that_does_not_repeat_the_title(string? title, string reason)
    {
        string? problem = PullRequestTitle.Problem(title);
        Assert.NotNull(problem);
        Assert.Contains(reason, problem, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(title)) Assert.DoesNotContain(title.Trim(), problem, StringComparison.Ordinal);
    }
}
