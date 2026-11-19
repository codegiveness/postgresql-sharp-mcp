# 0006: Add unit tests and a zero-diagnostics analyzer policy

## Status

Accepted (lands in 0.7.3; not yet in a released version at the time of writing)

## Context

Today the repository's behavioral evidence comes from the verifier (`tools/PostgreSqlMcp.Verify`), whose `integration` command needs a Docker PostgreSQL fixture, and from the offline `sarif-regressions` and `release-regressions` commands. There is no unit-test project, so pure logic such as the SQL guard, option parsing and TLS policy is only exercised through Docker-backed scenarios. Style enforcement is limited to the rules in [`.editorconfig`](../../.editorconfig), checked by `dotnet format style ... --severity warn` in CI ([CONTRIBUTING](../../CONTRIBUTING.md#coding-standards)). The sibling project `mssql-mcp` adopted `AnalysisLevel=latest-recommended` and a unit-test layer in its ADRs 0013 and 0020 (it chose suppressing six rules through `NoWarn`; this project does not copy that).

## Decision

For release 0.7.3, as repository engineering with no change to MCP tools, the SQL guard, defaults, CLI options or outputs:

- Add unit tests that run without Docker through `dotnet test --solution postgresql-sharp-mcp.slnx -c Release`. The Docker-backed `integration` command stays the evidence for database behavior.
- Enable `AnalysisLevel=latest-all` and `EnforceCodeStyleInBuild=true`, set `dotnet_analyzer_diagnostic.severity = warning` in `.editorconfig` with warnings as errors, so the build fails on **any** analyzer diagnostic, including those that are info or hidden by default. Roslyn language server diagnostics on changed files must also be 0 at every severity. The full `dotnet format postgresql-sharp-mcp.slnx --verify-no-changes --no-restore` replaces the style-only `--severity warn` check.
- Fix diagnostics in code rather than suppress them. A rule that does not fit the codebase is disabled explicitly in `.editorconfig` with a one-line reason; any call-site suppression is narrow, commented and reviewed, as CONTRIBUTING already requires.

## Alternatives considered

- **Keep the integration verifier as the only test layer**: leaves pure logic dependent on Docker and slow feedback.
- **Enable the analyzer level and suppress high-volume rules through `NoWarn`** (the sibling's ADR 0020 approach): rejected here in favor of a zero-diagnostics target.

## Consequences

- Contributors run a Docker-free test command and a full format check before pushing; the pre-push checklist in [CONTRIBUTING.md](../../CONTRIBUTING.md#verify-behavior) and [AGENTS.md](../../AGENTS.md) lists both.
- New code must be idiomatic enough to pass the strict analyzer level (file-scoped namespaces, invariant-culture formatting, no unused usings).
- Unverified at the time of writing: the exact diagnostic count the analyzer pass will find and the size of the resulting sweep; this ADR records the policy, not that it is already green.

## Evidence

- [`.editorconfig`](../../.editorconfig), the `dotnet format style` step in [ci.yml](../../.github/workflows/ci.yml), and the absence of a test project in [postgresql-sharp-mcp.slnx](../../postgresql-sharp-mcp.slnx) at 0.7.2.
- Sibling references: `docs/adr/0013-testing-strategy.md` and `docs/adr/0020-roslyn-analyzer-strategy-hybrid-suppression.md` in [codegiveness/mssql-mcp](https://github.com/codegiveness/mssql-mcp).
- No commit exists for this decision yet; link it here when the 0.7.3 change merges.
