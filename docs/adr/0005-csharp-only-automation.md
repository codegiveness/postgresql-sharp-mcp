# 0005: All maintained automation is C#/.NET

## Status

Accepted

## Context

A repository that ships a C# server also needs build, verification, release, fuzzing and installer logic. Mixing JavaScript, Python and Bash launchers would add runtimes to audit, pin and review in every contributor environment, and the npm installer must not require Node to run the installed server ([CHANGELOG 0.3.3](../../CHANGELOG.md)).

## Decision

All maintained implementation, build, verification and automation code is C#/.NET: the tool projects under `tools/` (`Build`, `Verify`, `Release`, `Fuzz`, `NpmInstall`, plus `Shared` source). JSON for npm and workflow/container configuration may invoke external package or runner tools, and workflows may call `dotnet run --project tools/...` and pinned third-party actions. No new JavaScript, Python, Bash or PowerShell launchers or scripts are added. The npm CLI's Node prerequisite is installation-only, not the server runtime.

## Alternatives considered

- **Shell or Python helper scripts**: shorter to write, but each is another interpreter and style to review, pin and test on Windows, macOS and Linux.
- **A Node shim for the npm entry point**: rejected for 0.3.3, which uses a checksum-pinned, unmodified upstream OS runner only during installation and keeps the installed server native.

## Consequences

- Automation shares the repository's compiler settings (nullable, warnings as errors), SPDX headers and locked NuGet restore.
- Shared helpers such as `tools/Shared/ProcessRunner.cs` and `RepositoryRoot.cs` replaced seven and three duplicated copies ([CHANGELOG 0.5.0](../../CHANGELOG.md), "Tooling").
- Some tasks (YAML, `package.json`, Dockerfile) remain non-C# by nature and are limited to configuration.

## Evidence

- [CLAUDE.md](../../CLAUDE.md) "Implementation and evidence" and [AGENTS.md](../../AGENTS.md) "Key constraints".
- [CHANGELOG 0.3.3](../../CHANGELOG.md) ("keep all maintained implementation/build/verification code in C#") and [CHANGELOG 0.5.0](../../CHANGELOG.md) "Tooling" (shared runner); the layout in [AGENTS.md](../../AGENTS.md).
