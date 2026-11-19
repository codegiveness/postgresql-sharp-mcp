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

## Build, test and verify

Prerequisites, versions and the full verification guide are in [CONTRIBUTING.md](CONTRIBUTING.md). The common commands, run from the repository root:

```bash
dotnet build postgresql-sharp-mcp.slnx -c Release
dotnet run --project tools/PostgreSqlMcp.Verify -c Release -- integration
dotnet format style postgresql-sharp-mcp.slnx --verify-no-changes --no-restore --severity warn
```

`integration` builds the application and runs real MCP calls against a disposable PostgreSQL Docker fixture, so it needs Docker. Other checks, each described under [Verify behavior](CONTRIBUTING.md#verify-behavior) and [Build and install package artifacts](CONTRIBUTING.md#build-and-install-package-artifacts):

```bash
dotnet run --project tools/PostgreSqlMcp.Verify -c Release -- sarif-regressions
dotnet run --project tools/PostgreSqlMcp.Verify -c Release -- release-regressions
dotnet run --project tools/PostgreSqlMcp.Build -c Release -- package --output artifacts/packages
dotnet run --project tools/PostgreSqlMcp.Verify -c Release -- packages --artifacts artifacts/packages
```

Documentation-only changes need link and command review instead of database runs. Say which commands you ran and what they showed.

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
- Branches, worktrees, commits, pull requests, releases: [docs/git-workflow.md](docs/git-workflow.md)
- Build, verify, package and release details: [CONTRIBUTING.md](CONTRIBUTING.md)
- Vulnerability reports go through [SECURITY.md](SECURITY.md), never a public issue.
