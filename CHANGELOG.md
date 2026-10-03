# Changelog

## Unreleased

- Simplify the main README to one release-archive installation and three setup steps: install/register, enter one connection string, then validate and launch the client. Explain that targets files are optional connection profiles; keep advanced configuration and technical reference collapsed outside the primary path.
- Fix the shared Bash/Zsh hidden prompt by using `printf` and `IFS= read -r -s` instead of Bash-only `read -p`. Clear stale connection settings and export only successful input; verify valid, empty, EOF and partial input, special characters, inherited restricted mode and actual CLI/MCP database discovery against disposable PostgreSQL.
- Use `Read-Host -AsSecureString` and `NetworkCredential` for a Windows PowerShell 5.1-compatible prompt without requiring PowerShell 7.1's `-MaskInput`. Exercise the documented block through Linux PowerShell in a terminal; native Windows execution remains unverified.

## 0.3.1

- Publish [GitHub Release v0.3.1](https://github.com/codegiveness/postgresql-sharp-mcp/releases/tag/v0.3.1) from merged main commit `4f0cf2c40f41b80a7e1ff34102ad8b4641dc71a6`: five self-contained archives, npm/NuGet package files, the application SBOM and checksums. Release run [37085312914](https://github.com/codegiveness/postgresql-sharp-mcp/actions/runs/37085312914) passed packaging, installed-distribution verification, attestation and GitHub publication; npm and NuGet registry publication failed. This is not a successful all-target registry release.
- Verify downloaded GitHub assets: all eight manifest checksums and all nine attestations passed with repository/workflow/source identity enforced. Exercise the Linux x64 archive and installed GitHub npm/NuGet package assets through actual CLI/MCP discovery, new-database selection, read-only rejection, explicit write commit/cleanup and shutdown against disposable PostgreSQL; this does not prove registry installation.

- Make **0.3.1** the environment-first setup follow-on: use masked Bash/PowerShell input to set `POSTGRES_CONNECTION_STRING`, inherit it when launching the MCP client, and keep credentials and mandatory targets files out of the primary client configuration. Existing discovery works without a JSON file; retain protected profiles as an optional alternative.
- Explain process-only lifetime, child-process inheritance, full client restart, mutually exclusive file/environment configuration and plaintext storage/inspection risks. Do not treat environment variables as encrypted secret storage or put secrets in shell history, persistent profiles or literal MCP JSON.
- Document optional secret-free prompt functions in Bash `.bashrc` and Zsh `.zshrc`, their login/startup-file behavior and shell-specific input syntax. Prompt explicitly in the launching shell; do not persist credentials or invoke secret prompts automatically from rc files.
- Exercise environment-only live discovery, physical selection, read-only/write boundaries and installed npm/NuGet entrypoints against disposable PostgreSQL. Align npm/NuGet metadata at patch version 0.3.1; the package files are available as GitHub Release assets, not verified registry publications.
- Include the 0.3.0 database-agnostic PostgreSQL selection contract: `list_databases` queries the live accessible-database catalog; configured connections are bootstrap profiles, not mandatory per-database entries. Select physical names per call, optionally choose a `target` profile, and discover newly created/granted databases without changing the protected file or restarting. Preserve optional explicit database allowlists and PostgreSQL permissions.
- **Breaking:** omitted access mode now defaults to `unrestricted`; explicit `restricted` remains effective. SQL calls still default to read-only, and writes require `read_only=false`. The `list_databases` payload now contains a paged `databases` result instead of an alias-only `targets` list. Existing 0.2.0 binaries require an upgrade; registry availability must be verified separately.
- Bound dynamic data-source retention to `256 / POSTGRES_POOL_SIZE` pools: equivalent normalized profiles reuse sources, idle pools are evicted, active pools retain ownership, and capacity waits share the original operation deadline. Preserve per-call database isolation, transaction boundaries and no fallback or write replay.
- Exercise immutable single-seed files, live creation/CONNECT revocation, concurrent database switching, punctuation-safe names, optional targets, explicit allowlists, default unrestricted writes, pool churn/capacity cancellation and disposal against disposable PostgreSQL. Install built npm/NuGet artifacts and exercise live discovery, physical selection and write commit/cleanup through their actual entrypoints.
- Keep the package fixture's exact `writer-disposable` synthetic password exception scoped to `tools/PostgreSqlMcp.Verify/Packages.cs`; other passwords and paths remain subject to scanning.
- Add explicit `packages --installation-only` verification for native Windows/macOS runners without a Docker database service; retain actual installed CLI/MCP/unavailable-endpoint checks and full live discovery/write package verification on Linux.
- Make unavailable-endpoint package verification portable by owning a listening loopback socket and rejecting each accepted handshake; do not rely on bound non-listening sockets producing immediate connection refusals on every OS.
- Rewrite first-time setup around one self-contained release path, explicit file locations and naming, a single `primary` target, complete Linux/macOS/Windows MCP configurations, client reload, connectivity checkpoints and troubleshooting. Keep alternative install methods separate and preserve existing installations. Extend the existing secret-scanner exception only to the exact new README password placeholder.
- Document verified local NuGet artifact installation when registry publication is unavailable, including checksum/attestation checks, an isolated local feed and installed MCP verification. Clarify protected conversion of PostgreSQL URI secrets without claiming registry availability.
- Add a NuGet-only manual release target that skips npm and GitHub Release publication while preserving main/tag validation, package verification, attestation and failure/cancellation gates. Keep the default all-target release behavior.
- Verify Best Practices enrollment as project 15155 (19%, not passing); add evidence-backed owner-review proposals and correct stale enrollment, secret-feature and Scorecard findings documentation without claiming hosted changes.
- Display the live OpenSSF Best Practices badge in README, including its in-progress state; distinguish the saved owner self-assessment from local answer proposals and security certification.
- Harden solo-maintainer release execution: require a manual main-branch dispatch and an existing tag reachable from main; compile trusted orchestration in read-only preflight and use digest-checked immutable artifacts in checkout-free attestation/publishing jobs. Replace source discovery in publishers with an explicit publication context.
- Preserve zero required approvals while enabling resolved review conversations, enforced full-SHA Actions pins, a restricted action-publisher allowlist and owner approval for external fork workflows. Restrict the release environment to main; document unchanged optional secret controls and that hosted badge answers still require owner authentication.
- Replace the coverage-guided SQL fuzz Bash launcher with checksum-verified, deadline-bounded C# orchestration and fail-closed seed replay; keep portable corpus names and repair the asynchronous FsCheck verifier entrypoint. Document distinct PostgreSQL property and lexer fuzzing layers.

- Update artifact uploads to the verified Node 24-based action; link the enabled private vulnerability-reporting route and clarify observed registry-account prerequisites and Scorecard measurement limits.
- Pin stable SDK 10.0.401 and SourceLink 10.0.401; align compatible transitive dependencies with current stable NuGet versions, including AI abstractions 10.10.1, and regenerate portable/five-RID lock graphs.
- Bound subprocess stdin/output-drain lifetimes with the command deadline, and MCP verifier request writes/response waits with one cancelable deadline. Bound asynchronous stdin close after a failed write; observe canceled readers before disposal, close failed-start/socket resources, and wait for the fixture's final TCP server. Add blocked-input/inherited-pipe and failed-MCP-writer cleanup regression checks.
- Modernize result serialization, generated regex properties, asynchronous PostgreSQL text reads, `ReadAtLeastAsync`-based bounded binary reads and pooled plan scratch buffers. Remove redundant archive-scanning copies and use modern temporary-directory creation.
- Document measured allocation/retained-heap behavior and the limits of a finite leak review; keep C# 14 SDK defaults and preserve MCP/package contracts.
- Add fixed-seed, real-PostgreSQL SQL fuzz/property scenarios and a fail-closed C# SARIF severity-delta comparator with meaningful regression coverage.
- Upgrade runtime OpenSSL packages to address CVE-2026-84782 in the pinned base image; retain non-root execution and native GSS support.
- Enable Dependabot vulnerability alerts and security-update PRs; add checksum-pinned full-history Gitleaks and real-image Trivy gates with narrow fixture exceptions and no broad vulnerability ignores.
- Scan C# and GitHub Actions with CodeQL; use real C# builds after observing no-build extraction errors, and gate PR severity deltas using raw base/candidate reports and the trusted-base comparator.

## 0.2.0

- Replace the JavaScript npm launcher with a .NET installation helper and bundled native apphosts; the installed server runs without a Node process. Installation requires .NET 10 and package-specific lifecycle approval on npm 12; custom installations require `DOTNET_ROOT`.
- Replace Python/Bash packaging and MCP smoke tooling with C#/.NET CLIs while preserving real PostgreSQL integration, security, failure, concurrency and package-installation coverage.
- Keep staged project paths relative to the publish working directory so aliased macOS temporary paths preserve transitive references.
- Commit portable and per-platform NuGet dependency locks; pin Docker base manifests and update Actions to verified current commits.
- Add dedicated C# CodeQL `security-extended` analysis, read-only PR SARIF analysis, OpenSSF Scorecard and cross-platform npm/NuGet installation jobs.
- Add application CycloneDX SBOMs, release SHA-256 manifests, build attestations and npm provenance requests; use a ZIP archive on Windows.
- Strip archive owner/group identities, retain executable permissions, and select current-version artifacts when older local packages coexist.
- Document supply-chain evidence, platform/prerequisite changes and confidential owner-authorized publishing credential reuse.

## 0.1.0

- Bundle the framework-dependent application in an npm package with a no-download Node launcher; require Node.js 22 or newer and the .NET 10 runtime.
- Add isolated npm/NuGet package installation and MCP smoke checks, including package-version consistency and dependency notices.
- Include the native GSS/Kerberos library in the non-root container runtime while retaining operator-controlled authentication and TLS settings.
- Preserve PostgreSQL SQLSTATE with fixed diagnostic summaries instead of potentially sensitive server messages, hints and details; disable SDK/provider payload logging at every log level.
- Sanitize unknown CLI arguments and target-file access diagnostics.
- Batch independent HypoPG candidate commands into one database round trip while preserving same-session plan comparisons and failure cleanup.
- Expand PostgreSQL integration coverage for RLS, failing-write rollback, multiple hypothetical candidates, error confidentiality, logging and configuration failures.
- Add dependency auditing, trusted-branch CodeQL, Dependabot and commit-pinned workflow actions with least-privilege permissions.
- Add verified tag/manual release automation for GitHub artifacts and separate npm/NuGet publication jobs.
- Provide registry-first installation guidance, protected-file MCP configuration examples and concise security, performance and contribution documentation.
- Normalize release assembly and symbol paths so distributed binaries do not expose build-machine workspace paths.

- PostgreSQL MCP server using C#/.NET 10, Npgsql and the official C# MCP SDK.
- Nine stdio tools with mandatory per-operation target aliases, allowlisted connections and no mutable current database or fallback.
- Lazy bounded Npgsql pools, normalized target reuse, operation concurrency/timeouts, idle pruning and shutdown disposal.
- Compact column metadata and row arrays, server-side read-only pagination, byte/cell bounds, explicit continuation/clipping and focused metadata sections.
- Actionable conversion errors for calendar-month intervals and out-of-range numeric values, with SQL projection guidance.
- Restricted transactions by default and explicitly enabled writes with committed DML/DDL, rollback on failure and no mutation replay pagination.
- Native estimated/actual plans, optional same-session HypoPG comparison/cleanup, database-filtered workload statistics, index evidence and focused health checks.
- .NET tool, container and self-contained packaging with real multi-database MCP integration verification.
