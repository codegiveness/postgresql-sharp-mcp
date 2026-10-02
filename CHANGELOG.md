# Changelog

## Unreleased

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
