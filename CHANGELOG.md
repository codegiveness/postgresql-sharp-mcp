# Changelog

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
