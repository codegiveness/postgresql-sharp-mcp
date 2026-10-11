# Architecture

postgresql-sharp-mcp is a [Model Context Protocol](https://modelcontextprotocol.io/) server written in C#/.NET 10. An MCP client (an AI assistant or coding agent) starts it as a local child process and talks to it over **stdio**. The server turns nine tool calls into bounded PostgreSQL queries and returns structured JSON. This document describes the high-level design; [SECURITY.md](../SECURITY.md) states the guarantees and limits and [docs/assurance-case.md](assurance-case.md) argues why they hold. Names below are source files; read the code for details.

Checked against the source of release 0.5.0 (commit `64c55c7`) on 2026-10-11.

## Context

```text
 MCP client (assistant/agent)
        |  JSON-RPC over stdin/stdout
        v
 postgresql-sharp-mcp (one process)  -- diagnostics on stderr only
        |  Npgsql (PostgreSQL wire protocol, TLS as configured by the operator)
        v
 PostgreSQL server(s): the authorization boundary
```

- The server has no network listener, no HTTP client, no telemetry and no update check. Its only outbound connections are the PostgreSQL connections the operator configures.
- stdout carries MCP JSON-RPC and nothing else. Logs and warnings go to stderr, and the MCP SDK's and Npgsql's own log categories are disabled so request/response bodies cannot be logged.
- There is no MCP-level authentication or per-tool authorization. Whoever controls the client controls the server; PostgreSQL roles decide what the configured login may do.

## Components

Three projects keep one dependency direction, `Core <- Tools <- App`:

| Project | Responsibility |
|---|---|
| `src/PostgreSqlMcp.Core` | Configuration, database and SQL behavior, error shaping. Knows nothing about MCP. |
| `src/PostgreSqlMcp.Tools` | The nine MCP tools and their descriptions. Calls Core only. |
| `src/PostgreSqlMcp` | The executable: command-line options, `--validate`, startup checks, dependency wiring, stdio transport. |

### Core

| File | Role |
|---|---|
| `ServerOptions.cs` | Parses command-line options, `POSTGRES_*` environment variables, targets JSON/file and the database lock into one immutable options object, enforcing every bound and rejecting conflicting credential sources. |
| `DatabaseRegistry.cs` | Holds connection profiles. Normalizes each connection string and overrides the settings the server must own (pooling, timeouts, no multiplexing, no error detail, no parameter logging, application name). Enforces the database lock and resolves `target`/`database`. Keeps a bounded least-recently-used cache of Npgsql data sources. |
| `SqlExecutor.cs` | `SqlExecutor` gates each call (access mode, concurrency limit, one deadline covering queue, connect and execution) and owns the transaction lifecycle. `SqlSession` is the only route to SQL: it validates the statement, batches transaction setup with the first statement, wraps paging and reads results through row, byte, cell and column limits. |
| `SqlGuard.cs` | A statement-boundary lexer. Accepts exactly one statement, understands comments, quoted text and dollar quoting, and rejects transaction/session control and some unsupported commands. It is not an authorization parser or a sandbox. |
| `SqlParameters.cs` | Binds up to 256 JSON scalars to `$1..$n` as text of unknown type. Values are never interpolated into SQL. |
| `ToolReply.cs` | The result envelope and error mapping. Errors keep the SQLSTATE and a fixed summary; PostgreSQL text is forwarded only where it provably comes from the caller's own statement. Caps the serialized result size. |
| `QueryPage.cs` | Result records: columns, rows, truncation, `next_offset`. |
| `DatabaseLockCheck.cs` | At startup and in `--validate`, proves in PostgreSQL that a locked login cannot reach other databases (superuser, server-file roles, `CREATEROLE`/`CREATEDB`, owned or connectable databases, `dblink`/`postgres_fdw`, server-file function grants). |
| `SecurityWarnings.cs` | Non-blocking stderr warnings: non-loopback hosts without certificate verification, and privileged role capabilities. |

### Tools

`ToolRegistration.cs` registers the public methods of four classes and sets the MCP annotations (read-only/destructive) and mode-dependent descriptions and server instructions from the configured access mode and lock.

| Class | Tools |
|---|---|
| `DatabaseTools` | `list_databases`, `list_schemas`, `list_objects`, `get_object_details` (catalog queries; every user-controlled name is a bound parameter, and sections/types are fixed allowlists) |
| `SqlTools` | `execute_sql` |
| `PlanTools` | `explain_query` (plans, optional `EXPLAIN ANALYZE`, optional HypoPG hypothetical indexes under a savepoint with guaranteed cleanup) |
| `OpsTools` | `analyze_db_health`, `get_top_queries`, `analyze_indexes` |

### App

`Program.cs` prints `--help`/`--version`, parses options, optionally runs `--validate` (a read-only connectivity query plus the privilege/TLS warnings and the lock check), and otherwise runs the lock check, registers the services and starts the MCP SDK on stdio. Configuration errors exit with a fixed message on stderr.

## Request flow

1. The client sends `tools/call` on stdin; the MCP SDK dispatches it to the tool method.
2. The tool method runs inside `ToolReply.Run`, which converts every outcome into the JSON envelope.
3. The tool calls `SqlExecutor`. It resolves `target` and `database` through `DatabaseRegistry`, which rejects anything outside the database lock, and refuses writes in restricted mode.
4. The call waits for a concurrency slot within its deadline, leases a pooled connection for exactly that physical database and begins a transaction.
5. The first batch sets `SET TRANSACTION READ ONLY` (or `READ WRITE` for an explicit write in unrestricted mode) and the statement and lock timeouts, then runs the single validated statement.
6. The reader enforces row, byte, cell and column budgets. Read-only calls roll back; an explicit write commits only after successful execution and result reading.
7. `ToolReply` serializes the page, checks the byte cap and writes the response to stdout.

Each call independently resolves its database. There is no shared current database, no fallback after a failed selection and no connection string accepted from tool arguments.

## Configuration and credentials

A base connection string from the environment creates the `primary` profile; optional targets JSON/file defines up to 32 independent profiles; an optional database lock (`POSTGRES_DATABASES`) restricts which physical databases may ever be opened. Combining a base connection string with targets is rejected rather than silently preferring one. Credentials come from the environment, an owner-protected file or the command line, never from tool arguments, and can be replaced without recompiling. See the [README configuration reference](../README.md#configuration-reference).

## Build, packaging and verification

- `tools/PostgreSqlMcp.Build` builds the npm packages (one main package plus five platform packages) and archives; `tools/PostgreSqlMcp.NpmInstall` is the installer those packages run; `tools/PostgreSqlMcp.Release` publishes and reconciles GitHub, npm and NuGet artifacts; `tools/Shared` holds the common process runner. All are C#.
- `tools/PostgreSqlMcp.Verify` is the test harness: a disposable PostgreSQL fixture (Docker), a stdio MCP client and 19 live scenario groups, plus offline groups for subprocess handling, the SARIF gate and release logic. `tools/PostgreSqlMcp.Fuzz` is the SharpFuzz/libFuzzer campaign over `SqlGuard`.
- CI (`.github/workflows/`) runs the verifier, package installation on Linux/Windows/macOS, CodeQL, dependency audit, secret scanning, container scanning, fuzzing and Scorecard. The release workflow builds, attests and publishes ([security posture](security-posture.md#supply-chain-evidence)).

## Key properties and where they live

| Property | Mechanism |
|---|---|
| PostgreSQL is the authority | The server sets read-only transactions and limits; roles, grants and RLS decide access |
| Bounded resources | Row/byte/cell/column limits, per-call deadline, concurrency limit, pool and cache bounds |
| No data in diagnostics | Fixed error summaries, no SDK/Npgsql logging, no parameter logging, stdout reserved |
| Fail closed | Invalid configuration, conflicting credential sources, lock violations and unverifiable lock checks stop startup or the call |
| Memory safety | Managed C#; no `unsafe` code or native interop in `src/` |
