# Security assurance case

This document argues why postgresql-sharp-mcp meets its security requirements, and says plainly where it does not. It contains the security requirements (what you can and cannot expect), the threat model, the trust boundaries, an argument that secure design principles were applied, an argument that common implementation weaknesses are countered, and the residual risks. Each claim points to code, documentation or a check you can rerun.

**Status.** Drafted on 2026-10-11 against release 0.5.0 (commit `64c55c7`) by an AI coding agent working for the maintainer, from the source and the project's documents. It is a self-assessment, not an independent audit or certification. The [review record](#review-record) shows whether the maintainer has reviewed it. Report errors through the process in [SECURITY.md](../SECURITY.md#reporting-a-vulnerability). Component descriptions are in [docs/architecture.md](architecture.md).

## 1. Security requirements

The software is a local stdio MCP server that exposes PostgreSQL operations using one configured database login. These are the requirements it is meant to meet (R) and the expectations it deliberately does not meet (N).

| ID | Requirement |
|---|---|
| R1 | **PostgreSQL is the authorization boundary.** The server never gives a caller more than the configured role's privileges, and never accepts credentials, hosts or TLS settings from tool arguments. |
| R2 | **Read-only by default.** SQL runs in a server-owned read-only transaction unless the operator leaves the default unrestricted mode and the caller explicitly sets `read_only=false`. Restricted mode refuses writes. Failures never commit. |
| R3 | **Database isolation.** Each call uses exactly one physical database selected for that call. With a database lock, the server opens no connection outside the lock and refuses to start when PostgreSQL grants do not enforce it. |
| R4 | **Credentials and data stay out of diagnostics.** Logs and errors contain no connection strings, passwords, SQL values or row data beyond what an authorized, successful result returns. stdout carries only MCP JSON-RPC. |
| R5 | **Bounded resource use.** Result size, statement duration, concurrency and connection counts are bounded, so one call cannot exhaust the host through the server's own behavior. |
| R6 | **Inputs are validated.** Every tool argument, option and configuration value is checked against an allowlist, length or range, and values reach PostgreSQL only as bound parameters or fixed SQL text. |
| R7 | **Transport security is verified by default.** An omitted `SSL Mode` becomes `VerifyFull` for non-loopback hosts and `Disable` for loopback; an explicit `SSL Mode` is the operator's opt-out and is warned about on remote hosts. TLS and authentication otherwise come from Npgsql and .NET. |
| R8 | **Released artifacts are verifiable.** Each release has SHA-256 checksums, an SBOM and signed build provenance, built from locked dependencies by pinned automation. |
| R9 | **Development is gated.** Changes reach `main` through pull requests with passing build, test, static analysis, dependency, secret and container checks. No secrets are stored in the repository. |

| ID | Not expected |
|---|---|
| N1 | **A SQL sandbox.** The SQL guard is a statement-boundary lexer. Read-only transactions do not stop external effects of privileged routines or extensions, and `EXPLAIN ANALYZE` executes the query ([SECURITY.md](../SECURITY.md#postgresql-permissions-are-the-authority)). |
| N2 | **Protection against a malicious or compromised client.** There is no MCP authentication or per-tool authorization; whoever controls the client can use the login. |
| N3 | **Protection from over-privileged roles.** A superuser or a member of `pg_execute_server_program`, `pg_read_server_files` and similar roles can act beyond SQL reads. `--validate` warns; it does not prevent. |
| N4 | **Redaction of successful results.** Authorized results may contain sensitive data and are returned as-is within the limits. |
| N5 | **Verified TLS by default.** A connection string without `SSL Mode` verifies the server certificate chain and host name before any credential is sent (non-loopback hosts) or uses no TLS (loopback and Unix sockets); it never uses Npgsql's `Prefer`, which does not verify the server and can fall back to plaintext. Only an explicit `SSL Mode` changes this ([residual risks](#7-residual-risks-and-known-gaps)). |
| N6 | **Resilience against expensive queries or a total memory/CPU ceiling.** Limits bound the server's behavior, not what PostgreSQL does with an expensive statement. |
| N7 | **Defense against prompt injection.** The server cannot tell whether a tool call or returned data was influenced by hostile text. Read-only defaults and the database lock limit the consequences. |

## 2. Assets, actors and threats

**Assets:** data reachable by the configured role; the connection credentials; the integrity of the target databases; the availability of the host and database; the integrity of released artifacts and of the repository.

**Actors:** the operator who configures the server; the MCP client and, through it, an AI model that may be steered by untrusted text; other local users and processes on the host; a network attacker between server and PostgreSQL; a malicious or compromised dependency, build tool or contributor; a vulnerability reporter.

| ID | Threat | Countered by | Residual |
|---|---|---|---|
| T1 | A model, steered by hostile text, issues destructive or exfiltrating SQL | R2 default read-only transactions, restricted mode, role least privilege, row/byte limits | Authorized reads can still leak data to the client; unrestricted mode plus `read_only=false` allows writes the role permits |
| T2 | SQL written to escape the read-only transaction or change session state | `SqlGuard` rejects multiple statements and transaction/session control; PostgreSQL enforces the read-only transaction | `set_config()` and privileged routines are not blocked; the guard is not an authorization parser |
| T3 | Reaching other databases on the server than intended | R3 lock enforced in every path that opens a connection, plus startup proof in PostgreSQL | Startup snapshot: later grant changes are not re-verified until restart or `--validate` |
| T4 | Credential disclosure through logs, errors, process list or files | R4; configuration errors use fixed text; Npgsql error detail and parameter logging forced off; SDK logging disabled | Command-line connection strings appear in process arguments; environment and files are plaintext readable by privileged local users |
| T5 | Network attacker impersonates or sniffs the PostgreSQL connection | Omitted `SSL Mode` is `VerifyFull` for non-loopback hosts, so an unverifiable server receives no credential and there is no plaintext fallback; server warns on explicit unverified modes | An operator who sets `Prefer`, `Allow`, `Require` or `Disable` explicitly accepts impersonation or sniffing; a CA the operator trusts is trusted completely |
| T6 | Resource exhaustion through large results, long statements, many concurrent calls or pool growth | R5 limits and deadlines | PostgreSQL still executes expensive statements until the statement timeout |
| T7 | Malformed configuration or arguments cause misbehavior | R6 allowlists, bounds, fixed errors, rejection of conflicting sources | None known beyond the documented limits |
| T8 | A compromised dependency, action, build tool or release path ships malicious code | Locked NuGet restore, Dependabot, dependency audit, full-SHA-pinned actions, checksum-pinned downloaded tools, container scan, attested releases, OIDC publishing without long-lived tokens | Trust in upstream packages and in the maintainer's account; single maintainer |
| T9 | A malicious pull request runs code with secrets or write access | No `pull_request_target`/`workflow_run` execution of untrusted code, read-only default tokens, `persist-credentials: false`, fork workflows need owner approval | Maintainer error or deliberate workflow weakening is not prevented ([security posture](security-posture.md#development-controls)) |
| T10 | Loss of the maintainer's access stops fixes | Fork-ability, public procedures | Not mitigated: bus factor 1 ([governance](../GOVERNANCE.md#continuity-if-the-maintainer-is-unavailable)) |

## 3. Trust boundaries

```text
 B1  MCP client  <---stdio--->  server process        client is the trusted principal
 B2  server process  <---Npgsql/TLS--->  PostgreSQL     PostgreSQL decides authorization
 B3  server process  <--->  host OS (env, files, logs)  protected by OS permissions
 B4  maintainer/CI  --->  release artifacts  --->  users   verified by checksums and attestations
 B5  contributors  --->  repository and CI privileges   gated by PRs, checks and token scopes
```

- **B1.** Everything arriving on stdin is untrusted input from a trusted principal's session. It is validated, bounded and never turned into connection settings.
- **B2.** Data returned by PostgreSQL is untrusted: column counts, name lengths, types, cell sizes and plan depth are bounded before use; error text is gated.
- **B3.** Environment variables, command-line arguments and targets files are operator-controlled but parsed defensively with fixed diagnostics. The server does not check file permissions; the operator must protect the files.
- **B4.** Users should verify downloads against `SHA256SUMS` and attestations ([how](security-posture.md#supply-chain-evidence)).
- **B5.** Untrusted pull-request code runs only in read-only jobs; release, attestation and publishing jobs run from `main` and consume immutable artifacts.

## 4. Argument that the requirements are met

| Req. | Mechanism | Evidence |
|---|---|---|
| R1 | Connection strings come only from options parsed at startup (`ServerOptions.cs`). `DatabaseRegistry.cs` resolves `target`/`database` to an existing profile and physical name; tool arguments have no connection-string, host or credential parameter. | `Verify`: Discovery, Configuration and target-isolation scenarios (`tools/PostgreSqlMcp.Verify/Integration.cs`) |
| R2 | `SqlSession` is the only route to SQL and sends `SET TRANSACTION READ ONLY` in the first batch before the statement; restricted mode refuses `read_only=false`; read-only calls roll back, writes commit only after success. Setup and statement share one batch, so no caller can run SQL first. | `Verify`: Sql, Writes and rollback scenarios |
| R3 | The lock is checked in `Resolve`, again in the data-source cache before any pool exists, and for discovery. `DatabaseLockCheck.cs` probes PostgreSQL grants and refuses startup on any path around the lock. | `Verify`: `DatabaseLockChecks.cs` (connection-string and targets modes, refusals, no-fallback) |
| R4 | `ToolReply.cs` returns SQLSTATE plus fixed summaries; PostgreSQL text only for class-42 errors positioned inside the caller's statement or `42501`, and not from routines or internal queries. Npgsql `IncludeErrorDetail=false`, `LogParameters=false`; MCP and Npgsql log categories off; only `Program.cs` writes to stderr. | `Verify`: log-confidentiality and sanitized-error scenarios |
| R5 | Limits: SQL 131,072 characters; 256 parameters; rows 1,000 (max 5,000); result 64 KiB (max 1 MiB); cell 4,096 characters; 128 columns; offset 1,000,000; query timeout 30 s (max 600 s); 16 concurrent calls; pool size 8 and at most `floor(256 / pool size)` cached data sources; statement and lock timeouts set per transaction. | `Verify`: Boundaries, Pagination, Pools, PoolCapacity, Cancellation; `ResourceChecks.cs` |
| R6 | Allowlists for `order_by`, `section`, `type`, `format`, access mode; ranges for every numeric option; JSON scalars only for parameters; identifiers bound as parameters; only extension schema names are quoted into SQL text. `SqlGuard` is additionally fuzzed. | `Verify`: Parameters, FsCheck properties (`FuzzChecks.cs`); `tools/PostgreSqlMcp.Fuzz` with checked-in corpus |
| R7 | `TlsPolicy.cs` sets `SSL Mode` on every profile that omits it (`ServerOptions.Parse`), and `SqlExecutor` turns TLS failures into fixed `tls_verification_failed`/`tls_unavailable` errors; the code implements no cryptography. `SecurityWarnings.cs` warns for explicit unverified modes on non-loopback hosts. | `Verify`: TlsChecks scenarios against a disposable TLS container and a throwaway CA; SecurityWarnings scenarios |
| R8 | Release workflow builds locked dependencies into archives, npm and NuGet packages, a CycloneDX SBOM, `SHA256SUMS` and GitHub attestations; since 0.5.0 the attestation bundle is a release asset. | [Supply-chain evidence](security-posture.md#supply-chain-evidence); `release-regressions` verifier command |
| R9 | CI, CodeQL, dependency audit, Gitleaks, Trivy and fuzzing run on every PR and push to `main`; the `main` ruleset requires them. | Workflow runs on `64c55c7`, 2026-10-11: [CI](https://github.com/codegiveness/postgresql-sharp-mcp/actions/runs/38102103515), [CodeQL](https://github.com/codegiveness/postgresql-sharp-mcp/actions/runs/38102103505), [dependency audit](https://github.com/codegiveness/postgresql-sharp-mcp/actions/runs/38102103499), [secret scanning](https://github.com/codegiveness/postgresql-sharp-mcp/actions/runs/38102103514), [container security](https://github.com/codegiveness/postgresql-sharp-mcp/actions/runs/38102103513), [fuzzing](https://github.com/codegiveness/postgresql-sharp-mcp/actions/runs/38102103518) all succeeded |

A passing run is a point-in-time result, not proof that no vulnerability exists.

## 5. Secure design principles

The eight principles of [Saltzer and Schroeder](https://web.mit.edu/Saltzer/www/publications/protection/), and input validation:

| Principle | How it is applied |
|---|---|
| Economy of mechanism | About 2,400 lines of C# in `src/`, 17 files, three projects, one transport (stdio), no listener, no HTTP client, no plugin mechanism. |
| Fail-safe defaults | SQL calls default to `read_only=true`; invalid or conflicting configuration stops startup; failed database selection never falls back to another database; unknown options are rejected; TLS verification is the default for non-loopback hosts and a certificate failure never falls back to plaintext or unverified TLS ([N5](#1-security-requirements)). The one default that is not deny-by-default is **unrestricted access mode when the mode is omitted**, which is documented, and writes still require an explicit `read_only=false`. |
| Complete mediation | Every statement passes `SqlSession`, every connection passes `DatabaseRegistry` and its lock check, and every result passes the row/byte/cell budget and the response byte cap. PostgreSQL mediates object access on every statement. |
| Open design | The source, protocol, limits and weaknesses are public ([SECURITY.md](../SECURITY.md)); nothing relies on secrecy of the design. Secrets are only the operator's credentials, kept outside the repository. |
| Separation of privilege | Writing requires three independent conditions: unrestricted mode, an explicit `read_only=false` and PostgreSQL privileges. Releases need a main-branch dispatch plus a 2FA approval for npm. |
| Least privilege | The server asks for no more than a login's own rights and documents dedicated least-privileged roles; the database lock check refuses over-privileged logins. The container runs as a non-root user. Workflow tokens are read-only by default, with narrow job-level write scopes. |
| Least common mechanism | Calls share no current database or session state; connections are reset on close; pools are keyed by the full normalized connection settings including database and credentials. |
| Psychological acceptability | Defaults are safe to try (read-only transactions, bounded results); setup is three documented steps; errors say what to change without exposing data. |
| Input validation with allowlists | See R6. Denylists appear only in `SqlGuard`'s first-keyword rejection, which is a convenience on top of PostgreSQL's read-only enforcement, and it is documented as not an authorization mechanism. |
| Limited attack surface | Stdio only, nine tools, no file or process access from tool arguments, no server-side HTTP. |

## 6. Common implementation weaknesses countered

| Weakness | Status | How |
|---|---|---|
| [SQL injection (CWE-89)](https://cwe.mitre.org/data/definitions/89.html) | Countered for server-built SQL; user SQL is the product | Catalog and operations queries are fixed text with bound parameters; the extension schema is quoted with `QuoteIdentifier`; user-supplied `execute_sql`/`explain_query` text is the intended input and is confined by the read-only transaction, role grants and the guard, not by escaping. |
| [Improper input validation (CWE-20)](https://cwe.mitre.org/data/definitions/20.html) | Countered | R6; fixed diagnostics for parse failures. |
| [Sensitive information in error messages and logs (CWE-209](https://cwe.mitre.org/data/definitions/209.html), [CWE-532)](https://cwe.mitre.org/data/definitions/532.html) | Countered | R4; verified by confidentiality scenarios. |
| [Hard-coded credentials (CWE-798)](https://cwe.mitre.org/data/definitions/798.html) | Countered | None in the repository; Gitleaks with PostgreSQL URI/Npgsql rules scans all history; GitHub push protection is enabled. |
| [Cleartext storage or transmission of credentials (CWE-312](https://cwe.mitre.org/data/definitions/312.html), [CWE-319)](https://cwe.mitre.org/data/definitions/319.html) | Partly | The server never persists credentials. Environment values and files are plaintext by nature and documented as such; remote connections verify TLS unless the operator sets an explicit `SSL Mode` (see N5). |
| [Improper certificate validation (CWE-295)](https://cwe.mitre.org/data/definitions/295.html) | Countered by default | R7: chain and host name are verified unless the operator sets an explicit unverified `SSL Mode`, which is warned about. |
| [Uncontrolled resource consumption (CWE-400)](https://cwe.mitre.org/data/definitions/400.html) | Countered for the server; not for PostgreSQL | R5. |
| [OS command injection (CWE-78)](https://cwe.mitre.org/data/definitions/78.html) | Not applicable | `src/` starts no processes. Build and release tools start processes through one shared runner with explicit arguments and timeouts. |
| [Path traversal (CWE-22)](https://cwe.mitre.org/data/definitions/22.html) | Not applicable | The only file read is the operator-supplied targets file path; no tool argument names a file. |
| [Unsafe deserialization (CWE-502)](https://cwe.mitre.org/data/definitions/502.html) | Countered | Input is parsed only with `System.Text.Json` document parsing (nested plans are depth-limited); nothing is deserialized into polymorphic or type-name-driven objects. |
| [Buffer and integer errors (CWE-119](https://cwe.mitre.org/data/definitions/119.html), [CWE-190)](https://cwe.mitre.org/data/definitions/190.html) | Countered | Managed memory-safe C#, no `unsafe` or native interop in `src/`; numeric options are range-checked before use. |
| [Time-of-check/time-of-use (CWE-367)](https://cwe.mitre.org/data/definitions/367.html) | Partly | The lock is checked when a database is resolved and again before any pool is created. The PostgreSQL grant proof is a startup snapshot (see T3). |
| [Use of vulnerable components (CWE-1395)](https://cwe.mitre.org/data/definitions/1395.html) | Monitored | Locked restore, central versions, Dependabot alerts and updates, dependency audit with `NuGetAudit`, Trivy over the built container, pinned base-image digests. |
| [Download of code without integrity check (CWE-494)](https://cwe.mitre.org/data/definitions/494.html) | Countered | Tools downloaded in CI and the fuzz bridge are pinned by SHA-256; Actions are pinned by full commit SHA; the SDK is pinned in `global.json`; npm 12.2.0 is verified against its published SHA-512. |
| [Excessive privileges in automation (CWE-250)](https://cwe.mitre.org/data/definitions/250.html) | Countered | Workflow-level `permissions` are `contents: read` or empty; write scopes (`security-events`, `id-token`, `attestations`, `contents`) are granted per job for their single purpose. |

Memory-safety and undefined-behavior hardening come from the language: C# is memory-safe, nullable reference types are enabled and `TreatWarningsAsErrors` makes compiler warnings, including use of obsolete APIs, build failures. The project has no HTTP interface, so web hardening headers do not apply.

## 7. Residual risks and known gaps

These are recorded here rather than argued away. Items marked **gap** are the ones the project would change if it could.

1. **Operator opt-out of TLS verification.** An explicit `SSL Mode` of `Prefer`, `Allow`, `Require` or `Disable` is honored unchanged and, for a remote host, only warned about on stderr, so an on-path attacker can then impersonate PostgreSQL. A connection string that omits `SSL Mode` is verified by default (the release after 0.5.0). Loopback and Unix-socket profiles default to `Disable` because no network attacker is in play there; a loopback port forwarded to a remote server that requires TLS needs an explicit `SSL Mode`. Weak TLS protocol versions and cipher suites are whatever .NET and the PostgreSQL server negotiate.
2. **Legacy authentication.** Npgsql still answers a server's legacy MD5 request unless the operator sets `Require Auth` accordingly.
3. **Lock check is a snapshot.** Grants, roles and extensions changed after startup are enforced only by the in-process lock until the next start or `--validate`.
4. **The guard is a lexer.** Its first-keyword denylist does not cover `set_config()` or privileged routines. Isolation relies on PostgreSQL.
5. **Prompt injection** cannot be detected by the server (N7).
6. **Plaintext secrets at rest in process state.** Environment values, process arguments and targets files are exposed to sufficiently privileged local users; the server does not check targets-file permissions.
7. **Container image.** The Dockerfile installs a few runtime libraries without version pins; Trivy scans the built image on every run and fails on HIGH/CRITICAL findings.
8. **Coverage is incomplete.** The `integration` scenarios reach 89.41% of statements and 84.84% of branches in the shipped assemblies (coverlet, 2026-10-11, PostgreSQL 17); CI fails below 80% lines. The [coverage command](../CONTRIBUTING.md#measure-test-coverage) lists the files and methods no scenario reaches.
9. **Single maintainer.** Review is self-review; there is no independent approval and no continuity if the maintainer is unavailable ([governance](../GOVERNANCE.md#continuity-if-the-maintainer-is-unavailable), [security posture](security-posture.md)).

## 8. Re-verifying the evidence

```bash
dotnet build postgresql-sharp-mcp.slnx -c Release -p:RestoreLockedMode=true      # zero warnings
dotnet run --project tools/PostgreSqlMcp.Verify -c Release -- integration        # needs Docker
dotnet run --project tools/PostgreSqlMcp.Verify -c Release -- sarif-regressions
dotnet run --project tools/PostgreSqlMcp.Verify -c Release -- release-regressions
dotnet run --project tools/PostgreSqlMcp.Fuzz -c Release -p:RestoreLockedMode=true -- campaign 60
dotnet run --project tools/PostgreSqlMcp.Verify -c Release -- coverage --threshold 80  # needs Docker
```

Hosted-run claims are tied to the run URLs above and to the revision they ran on; check the current `main` results rather than assuming they still hold.

## 9. Keeping this document current

Update it in the same pull request as any change to a requirement, default, limit, trust boundary, workflow privilege or release path, and review it at least once a year. Record each review below.

### Review record

| Date | Reviewer | Scope | Outcome |
|---|---|---|---|
| 2026-10-11 | AI coding agent (draft author) | Source of 0.5.0 at `64c55c7`, workflows, project documents | Draft written; not a human review |
| _pending_ | Maintainer | Requirements, boundaries, residual risks | _Not yet recorded. The OpenSSF `security_review` criterion needs a human review recorded here_ |
