# Guide for coding agents

This is a short, tool-neutral orientation for any coding agent working on this repository. The binding rules are in [CLAUDE.md](CLAUDE.md); read it before you change anything. The git and pull-request process is in [docs/git-workflow.md](docs/git-workflow.md).

## What this is

`postgresql-sharp-mcp` is a PostgreSQL [Model Context Protocol](https://modelcontextprotocol.io/) server written in C#/.NET. It lets AI assistants discover databases, inspect schemas, run SQL (read-only by default), explain query plans and check index and database health. It is distributed as an npm package, a .NET tool, a standalone binary and a container image. [README.md](README.md) describes installation and use; [docs/architecture.md](docs/architecture.md) describes the design.

## Layout

| Path | Contents |
| --- | --- |
| `src/PostgreSqlMcp.Core` | Configuration, SQL guard and database behavior |
| `src/PostgreSqlMcp.Tools` | The MCP tools |
| `src/PostgreSqlMcp` | App: stdio host and command line |
| `tools/PostgreSqlMcp.Build` | Builds npm and NuGet package artifacts |
| `tools/PostgreSqlMcp.Verify` | Verifier: integration, package, SARIF, release and coverage checks |
| `tools/PostgreSqlMcp.Release` | Release and publication tooling |
| `tools/PostgreSqlMcp.Fuzz` | Coverage-guided fuzz campaign for the SQL lexer |
| `tools/PostgreSqlMcp.NpmInstall` | Installer that ships inside the npm platform packages |
| `tools/Shared` | Source shared by the `tools/` projects (process runner, repository root) |
| `npm/` | Source `package.json` of the npm main package (built by the builder; do not install it directly) |
| `docs/` | Architecture, assurance, security posture, roadmap and the git workflow |

## Read first

Before changing anything, skim these in order. Each is short and states what you must not break.

1. [CLAUDE.md](CLAUDE.md): the binding rules.
2. [CONTEXT.md](CONTEXT.md): the project's vocabulary; use its preferred terms and avoid its listed synonyms.
3. [docs/adr/](docs/adr/README.md): decisions already made (releases, CodeQL gate, TLS and lock defaults, packaging, C#-only automation, test and analyzer policy). Do not reverse one silently; write a new ADR that supersedes it.
4. [docs/architecture.md](docs/architecture.md): components and request flow.
5. [docs/assurance-case.md](docs/assurance-case.md): the security claims your change must keep true.
6. [SECURITY.md](SECURITY.md): guarantees, hardening and the private vulnerability route.

## Build, test and verify

Prerequisites, versions and the full verification guide are in [CONTRIBUTING.md](CONTRIBUTING.md). The common commands, run from the repository root:

```bash
dotnet build postgresql-sharp-mcp.slnx -c Release
dotnet test --solution postgresql-sharp-mcp.slnx -c Release
dotnet format postgresql-sharp-mcp.slnx --verify-no-changes
dotnet run --project tools/PostgreSqlMcp.Verify -c Release -- integration
```

`integration` builds the application and runs real MCP calls against a disposable PostgreSQL Docker fixture, so it needs Docker. The unit tests need no Docker. Other checks, each described under [Verify behavior](CONTRIBUTING.md#verify-behavior) and [Build and install package artifacts](CONTRIBUTING.md#build-and-install-package-artifacts):

```bash
dotnet run --project tools/PostgreSqlMcp.Verify -c Release -- sarif-regressions
dotnet run --project tools/PostgreSqlMcp.Verify -c Release -- release-regressions
dotnet run --project tools/PostgreSqlMcp.Build -c Release -- package --output artifacts/packages
dotnet run --project tools/PostgreSqlMcp.Verify -c Release -- packages --artifacts artifacts/packages
```

Documentation-only changes need link and command review instead of database runs. Say which commands you ran and what they showed.

### Pre-push checklist

Run the rows your change touches before you push, and report each command and its result. A row marked "All changes" applies to every code change.

| Command | Expected result | When |
| --- | --- | --- |
| `dotnet build postgresql-sharp-mcp.slnx -c Release` | 0 warnings, 0 errors | All changes |
| `dotnet test --solution postgresql-sharp-mcp.slnx -c Release` | All unit tests pass; no Docker needed | All changes |
| `dotnet format postgresql-sharp-mcp.slnx --verify-no-changes --no-restore` | Exit 0, no files reformatted (full format, not style-only) | All changes |
| Roslyn LSP diagnostics on the changed files | 0 diagnostics at every severity, including hidden and info | All changes |
| `dotnet run --project tools/PostgreSqlMcp.Verify -c Release -- version` | Exit 0: versions in `PostgreSqlMcp.csproj`, `npm/package.json` (including `optionalDependencies`) and `server.json` (top level and every `packages[].version`) agree; major is 0 | Version or package metadata changes |
| `dotnet run --project tools/PostgreSqlMcp.Verify -c Release -- workflows` | Exit 0: actionlint and zizmor (offline) find nothing in `.github/workflows/` | Changes under `.github/workflows/` |
| `dotnet run --project tools/PostgreSqlMcp.Verify -c Release -- secret-detectors` | Exit 0: the custom `.gitleaks.toml` rules fire and the exact README allowlist holds | Changes to secret scanning or its fixtures |
| `dotnet run --project tools/PostgreSqlMcp.Verify -c Release -- integration` | Exit 0 against the disposable Docker fixture | Runtime, SQL, configuration or tool changes (needs Docker) |
| `dotnet run --project tools/PostgreSqlMcp.Verify -c Release -- coverage` | Exit 0 at or above the coverage gate | Runtime changes (needs Docker) |
| `dotnet run --project tools/PostgreSqlMcp.Build -c Release -- package --output artifacts/packages`, then `dotnet run --project tools/PostgreSqlMcp.Verify -c Release -- packages --artifacts artifacts/packages` | Exit 0: the six NuGet packages, npm tarballs and archives install and run | Packaging, dependency or release changes |

CI runs these as jobs `unit-tests`, `version` (which also runs `release-regressions`), `workflow-audit` (`workflows` plus `install-tool-regressions`) and `gitleaks` (`secret-detectors`, then the history scan). Scanner tools are installed by `install-tool <name> --output <dir>` from SHA-256-pinned entries in `tools/PostgreSqlMcp.Verify/PinnedTools.cs`; bumping one means editing its row there, and `install-tool-regressions` proves tampered or unsafe archives are rejected. An intentional workflow pattern that a linter flags needs a per-line `# zizmor: ignore[rule]` comment with the reason. Expected results for the Docker-backed rows are those described in [Verify behavior](CONTRIBUTING.md#verify-behavior).

## Language server (LSP)

Roslyn LSP, `csharp-ls` and C# Dev Kit open `postgresql-sharp-mcp.slnx`. The analyzer policy is `AnalysisLevel=latest-all` with `EnforceCodeStyleInBuild=true`, and `.editorconfig` sets `dotnet_analyzer_diagnostic.severity = warning`, so the build fails on **any** analyzer diagnostic, including rules that are info or hidden by default. A rule that does not fit this codebase is disabled explicitly in `.editorconfig` with a one-line reason; do not suppress at the call site to get green. The diagnostics your editor or agent shows match the build, so also confirm 0 diagnostics at every severity on the changed files. Query the language server for definitions, references, hover and diagnostics instead of text searching C#.

## Key constraints

- Dependency direction is `Core <- Tools <- App`.
- MCP stdout carries JSON-RPC only; diagnostics go to stderr.
- All maintained implementation and automation is C#/.NET: no new JavaScript, Python or Bash scripts.
- NuGet lock files are restored in locked mode; regenerate them deliberately and never disable locking.
- Never commit credentials, real connection strings, customer SQL or database output. Test only against disposable databases you own.
- Remote actions (push, issues, pull requests, tags, releases, publication, settings changes) need explicit authorization; see [What agents may do](docs/git-workflow.md#what-agents-may-do).
- Coding style follows [Coding standards](CONTRIBUTING.md#coding-standards): every `.cs` file starts with the SPDX header, nullable references and warnings-as-errors are on.

## Where to read more

- Rules that bind agents: [CLAUDE.md](CLAUDE.md)
- Vocabulary: [CONTEXT.md](CONTEXT.md); decisions: [docs/adr/](docs/adr/README.md); documentation index: [docs/README.md](docs/README.md)
- Branches, worktrees, commits, pull requests, required checks, releases: [docs/git-workflow.md](docs/git-workflow.md)
- Build, verify, package and release details: [CONTRIBUTING.md](CONTRIBUTING.md)
- Vulnerability reports go through [SECURITY.md](SECURITY.md), never a public issue.
