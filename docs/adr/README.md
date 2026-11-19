# Architecture decision records

An ADR records one decision that was hard to reverse or easy to forget: the context, the choice, the alternatives and the consequences. [CONTEXT.md](../../CONTEXT.md) holds the vocabulary the ADRs use.

ADRs are numbered sequentially and never deleted. Do not rewrite an accepted ADR to change its decision: write a new one that supersedes it, and set the old one's status to `Superseded by ADR-NNNN`. Small factual corrections (a broken link, a wrong date) are fine.

Records 0001-0005 record decisions that were **already made and shipped**; their evidence is the cited commits and [CHANGELOG.md](../../CHANGELOG.md) entries. Nothing in them is a new policy. ADR 0006 records a decision taken for release 0.7.3 and is `Accepted`, not yet shipped.

| ADR | Title | Status |
| --- | --- | --- |
| [0001](0001-manual-dispatch-releases-with-staged-npm.md) | Releases are manual-dispatch only; npm is staged and approved | Accepted |
| [0002](0002-raw-sarif-fail-closed-codeql-gate.md) | CodeQL pull-request gate compares raw SARIF and fails closed | Accepted |
| [0003](0003-prefer-tls-and-advisory-database-lock-defaults.md) | Beginner-friendly defaults: `SSL Mode=Prefer` and an advisory database lock | Accepted |
| [0004](0004-self-contained-rid-specific-nuget-tool.md) | The NuGet .NET tool is self-contained and RID-specific | Accepted |
| [0005](0005-csharp-only-automation.md) | All maintained automation is C#/.NET | Accepted |
| [0006](0006-unit-tests-and-zero-diagnostics-analyzer-policy.md) | Add unit tests and a zero-diagnostics analyzer policy | Accepted (lands in 0.7.3) |

## Writing a new ADR

1. Copy [template.md](template.md) to `NNNN-short-title.md` with the next number, and add a row to the table above.
2. Fill in every section. Cite commits, pull requests, CHANGELOG entries, files or measurements; if you cannot source a claim, leave it out.
3. Open the ADR in the same pull request as the change it describes (or before it, as `Proposed`), and set the status to `Accepted` when the maintainer agrees.
4. If the decision replaces an earlier one, edit only the earlier ADR's status line.
