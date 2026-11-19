# 0004: The NuGet .NET tool is self-contained and RID-specific

## Status

Accepted

## Context

Before 0.7.2 the NuGet .NET tool ran on the user's installed .NET runtime, while npm packages and standalone archives already bundled one (npm since 0.3.3). A user with no matching runtime, or an outdated one, could not run the tool; the 0.7.1 NuGet tool used "the installed .NET runtime", whose updates the user had to apply, while the bundled-runtime distributions did not depend on it ([CHANGELOG 0.7.1 and 0.7.2](../../CHANGELOG.md)).

## Decision

From 0.7.2, `codegiveness.postgresql-sharp-mcp` is a pointer package (`tools/any/any`, no code) listing five platform packages (`.win-x64`, `.linux-x64`, `.linux-arm64`, `.osx-x64`, `.osx-arm64`), each with a self-contained executable and the .NET runtime. `dotnet tool install` picks the package for the machine's RID. There is deliberately **no framework-dependent `any` fallback**: an unsupported platform fails to install instead of installing a tool that cannot run. The RIDs are those npm and the release archives already ship.

Packing restores the five RIDs only for `dotnet pack` (`_IsPacking`), with its own lock file `src/PostgreSqlMcp/packages.tool.lock.json`, so normal and per-RID restores keep lighter graphs. Pack-only metadata may be added to the project file, but the artifact set (the pointer plus five platform `.nupkg`, six in total) must stay exactly what the builder and verifier check.

## Alternatives considered

- **Keep the framework-dependent tool**: leaves the runtime and its security updates to the user.
- **Keep an `any` fallback package**: would install a tool that cannot run on unsupported platforms.

## Consequences

- Installing needs a .NET SDK 10 or newer (RID-specific tool support), but no runtime afterwards.
- On Windows the SDK installs a `postgresql-sharp-mcp.cmd` command, not an `.exe`; clients that start commands without a shell cannot launch it, so npx or the archive's `PostgreSqlMcp.exe` is recommended there.
- `tools/Shared/NuGetTool.cs` defines the package set for the builder, verifier and release tool.

## Evidence

- Commit `c42e653` (2026-11-19): "build: .NET 11, dependency refresh, self-contained .NET tool and security fixes (0.7.2) (#91)".
- [CHANGELOG 0.7.2](../../CHANGELOG.md), first four bullets; `ToolPackageRuntimeIdentifiers` and the `_IsPacking` property group in [`PostgreSqlMcp.csproj`](../../src/PostgreSqlMcp/PostgreSqlMcp.csproj).
