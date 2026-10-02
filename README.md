# postgresql-sharp-mcp

[![CI](https://github.com/codegiveness/postgresql-sharp-mcp/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/codegiveness/postgresql-sharp-mcp/actions/workflows/ci.yml)
[![CodeQL](https://github.com/codegiveness/postgresql-sharp-mcp/actions/workflows/codeql.yml/badge.svg?branch=main)](https://github.com/codegiveness/postgresql-sharp-mcp/actions/workflows/codeql.yml)
[![Dependency audit](https://github.com/codegiveness/postgresql-sharp-mcp/actions/workflows/security.yml/badge.svg?branch=main)](https://github.com/codegiveness/postgresql-sharp-mcp/actions/workflows/security.yml)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/codegiveness/postgresql-sharp-mcp/badge)](https://scorecard.dev/viewer/?uri=github.com/codegiveness/postgresql-sharp-mcp)
[![.NET](https://img.shields.io/badge/.NET-10-blue)](https://dotnet.microsoft.com/)
[![SBOM](https://img.shields.io/badge/SBOM-CycloneDX-blue)](docs/security-posture.md#supply-chain-evidence)
[![Security Policy](https://img.shields.io/badge/Security-Policy-blue)](SECURITY.md)
[![GitHub release](https://img.shields.io/github/v/release/codegiveness/postgresql-sharp-mcp)](https://github.com/codegiveness/postgresql-sharp-mcp/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A PostgreSQL MCP server built with C#/.NET 10 and Npgsql. One stdio server exposes nine tools for SQL, schema discovery, query plans, index analysis and database health. Every database-dependent call selects an explicitly configured target; there is no process-wide current database or fallback connection.

**Read-only by default.** Use least-privileged PostgreSQL roles and keep credentials in a protected configuration file, not tool arguments. Read-only transactions are not a sandbox for privileged functions or external side effects. See [SECURITY.md](SECURITY.md) for the trust boundaries.

The server, npm installation helper, packaging, verification and release automation are C#/.NET. No maintained JavaScript, Python or Bash implementation is required. npm itself requires Node.js for installation; the installed server runs directly as a .NET executable, without a Node process. Workflow badges and Scorecard report checks and practices, not certifications or profile achievements. See [Security posture](docs/security-posture.md) for supply-chain evidence and limits.

## Quick start

### 1. Install or run

**GitHub release archives:** download a self-contained archive from [GitHub Releases](https://github.com/codegiveness/postgresql-sharp-mcp/releases) for installation without registry access.

Choose `postgresql-sharp-mcp-<RID>.tar.gz` on Linux/macOS or `postgresql-sharp-mcp-win-x64.zip` on Windows for releases from 0.2.0. Older releases use `.tar.gz` on Windows too.

| Platform | RID |
|---|---|
| Linux x64 / ARM64 | `linux-x64` / `linux-arm64` |
| macOS Intel / Apple Silicon | `osx-x64` / `osx-arm64` |
| Windows x64 | `win-x64` |

Extract the archive into a dedicated directory. On Linux/macOS, run `./PostgreSqlMcp --version`; on Windows, run `.\PostgreSqlMcp.exe --version`. Self-contained archives do not require a separate .NET runtime, but still require platform-native libraries. GSS/Kerberos connections on Debian/Ubuntu require `libgssapi-krb5-2`; see [Npgsql security](https://www.npgsql.org/doc/security.html) for authentication and TLS configuration.

**npm and NuGet alternatives:** these commands require the package to be available in the corresponding registry. If a package is unavailable, use a GitHub release archive or the [source build instructions](CONTRIBUTING.md).

Using npm requires **Node.js 22 or newer** for npm/npx installation and the **.NET 10 runtime** with `dotnet` on `PATH` during installation. The C# `postinstall` selects a bundled native apphost; it never downloads binaries or a runtime. npm 12 blocks unapproved dependency lifecycle scripts: approve only this package with `--allow-scripts` for npx/global installation, as below. For a custom .NET installation, also set `DOTNET_ROOT` to its installation directory so the native apphost can locate it:

```bash
npx -y --allow-scripts=@codegiveness/postgresql-sharp-mcp @codegiveness/postgresql-sharp-mcp --version
```

Or install the .NET tool using the **.NET 10 SDK**:

```bash
dotnet tool install --global codegiveness.postgresql-sharp-mcp
postgresql-sharp-mcp --version
```

Both packages run the same framework-dependent .NET server. After npm installation, `postgresql-sharp-mcp` starts the native apphost directly; no JavaScript launcher or Node child process relays MCP or signals. The internal npm executable has an `.exe` filename on every OS, but the public command remains `postgresql-sharp-mcp`. Keep .NET available when launching either package. To avoid npm and Node altogether, use the NuGet tool or a self-contained release archive.

### 2. Configure database targets

Create a JSON file outside the repository, readable only by the server's OS user or an appropriately restricted group. Replace every `<...>` placeholder with your PostgreSQL connection details:

```json
{
  "tenant_a": "Host=<database-host>;Port=<database-port>;Database=<database-name>;Username=<database-role>;Password=<database-password>;SSL Mode=VerifyFull",
  "tenant_b": "Host=<other-host>;Port=<other-port>;Database=<other-database>;Username=<other-role>;Password=<other-password>;SSL Mode=VerifyFull"
}
```

Each target requires an explicit `Host` and `Database`. Choose authentication and TLS settings appropriate to your deployment; `VerifyFull` requires a trusted server certificate and matching hostname. Do not commit the populated file or make it world-readable.

The `database` tool argument is the **exact, case-sensitive alias** from this object, not an arbitrary PostgreSQL database name. Targets may use independent hosts and credentials. Restart the server after changing targets or credentials.

### 3. Add a stdio MCP client entry

Adapt this example to your client's configuration schema. Only a configuration-file path goes into the client entry:

For an extracted release archive:

```json
{
  "mcpServers": {
    "postgresql": {
      "command": "<absolute-path-to-extracted-PostgreSqlMcp-executable>",
      "env": {
        "POSTGRES_TARGETS_FILE": "<absolute-path-to-protected-targets-file>"
      }
    }
  }
}
```

Use `PostgreSqlMcp.exe` on Windows. With the npm package, use:

```json
{
  "mcpServers": {
    "postgresql": {
      "command": "npx",
      "args": ["-y", "--allow-scripts=@codegiveness/postgresql-sharp-mcp", "@codegiveness/postgresql-sharp-mcp"],
      "env": {
        "POSTGRES_TARGETS_FILE": "<absolute-path-to-protected-targets-file>"
      }
    }
  }
}
```

For the globally installed .NET tool, use `"command": "postgresql-sharp-mcp"` and omit `args`. If the MCP client does not inherit the tool directory on `PATH`, use the absolute executable path.

**Windows:** escape backslashes in JSON paths. npm exposes `npx.cmd`; clients that cannot launch command scripts directly can use `"command": "cmd"` with `"args": ["/d", "/c", "npx", "-y", "--allow-scripts=@codegiveness/postgresql-sharp-mcp", "@codegiveness/postgresql-sharp-mcp"]`, or use the installed .NET tool executable instead.

### 4. Validate and connect

For an extracted release archive on Linux/macOS:

```bash
./PostgreSqlMcp --targets-file "<absolute-path-to-protected-targets-file>" --validate
```

On Windows, use `.\PostgreSqlMcp.exe` with the same arguments. With npm:

```bash
npx -y --allow-scripts=@codegiveness/postgresql-sharp-mcp @codegiveness/postgresql-sharp-mcp --targets-file "<absolute-path-to-protected-targets-file>" --validate
```

With the .NET tool, use the same arguments after `postgresql-sharp-mcp`. `--validate` opens each configured target, reports its actual `current_database()` or a sanitized error to **stderr**, and exits 0 only if all targets work. It does not start MCP.

Restart your MCP client, call `list_databases`, then try:

```json
{"name":"execute_sql","arguments":{"database":"tenant_a","sql":"SELECT current_database()","limit":1}}
```

Normal operation reserves stdout for MCP JSON-RPC and logs to stderr. `list_databases` reports allowlisted aliases, access mode and limits without opening a connection; a listed alias does not guarantee that its database is reachable.

## Tools

Except `list_databases`, every tool **requires `database`**. Query and metadata page defaults are `min(100, POSTGRES_MAX_ROWS)`; top-query pages default to `min(10, POSTGRES_MAX_ROWS)`.

| Tool | Capability | Options |
|---|---|---|
| `list_databases` | Configured aliases and limits; no database roundtrip | `limit`, `offset` |
| `list_schemas` | Schemas with USAGE privilege | literal `prefix`, `include_system`, page |
| `list_objects` | Tables, views, materialized views, sequences, functions, procedures and extensions | `schema`, `type`, literal `search`, `include_system`, page |
| `get_object_details` | One object's metadata section | `schema`, `name`, `section`: columns/constraints/indexes/triggers/definition/parameters; `type`, `identity_arguments`, page |
| `execute_sql` | One SQL statement; bounded results or explicitly enabled writes | `sql`, `read_only`, page |
| `explain_query` | Estimated/actual JSON plan and compact major-node summary | `sql`, `format`: summary/json, `analyze`, optional HypoPG `indexes` |
| `analyze_indexes` | Index size, usage, validity, constraints and structural duplicate evidence | `schema`, `table`, page |
| `get_top_queries` | Current-database `pg_stat_statements` workload | `order_by`: total_time/mean_time/calls/rows/reads, page |
| `analyze_db_health` | Summary or focused PostgreSQL health evidence | `section`: summary/vacuum/index/constraints/sequences/replication/blocking; `schema` where applicable, page |

Routine overloads require the exact `identity_arguments` from `list_objects`, including parameter names; an empty string selects zero arguments. Use `type` to disambiguate relation/routine name collisions. Discovery filters by role privileges; missing or hidden objects return an error. Table definitions are structural fragments, not a round-trip DDL export.

Each call resolves its own immutable target and leases a connection from its pool. Calls against different targets can run concurrently. An invalid alias, denied login or failed connection returns an error for that target; it never falls back to another database.

Focused inspection:

```json
{"name":"list_objects","arguments":{"database":"tenant_a","schema":"public","type":"table","search":"order","limit":20}}
```

```json
{"name":"get_object_details","arguments":{"database":"tenant_a","schema":"public","name":"orders","section":"indexes","limit":10}}
```

### Plans and optional extensions

No extensions are installed automatically. An authorized administrator must configure extensions in the databases where they are needed:

- **`pg_stat_statements`:** install the server extension package, add it to `shared_preload_libraries` without removing existing entries, restart PostgreSQL, and run `CREATE EXTENSION pg_stat_statements` in each selected database. It is needed only for workload statistics.
- **HypoPG:** install the extension package matching the PostgreSQL server major version, then run `CREATE EXTENSION hypopg` in the selected database. HypoPG itself does not require preload or a restart. It is needed only for hypothetical `indexes` in `explain_query`.

Extension schemas are discovered and quoted rather than assumed to be `public`. Missing or unready extensions produce explicit errors. Check availability and installation before changing a database:

```sql
SELECT name, default_version, installed_version
FROM pg_available_extensions
WHERE name IN ('hypopg', 'pg_stat_statements');
```

Example what-if call:

```json
{"name":"explain_query","arguments":{"database":"tenant_a","sql":"SELECT * FROM public.orders WHERE customer=42","indexes":["CREATE INDEX ON public.orders(customer)"]}}
```

Hypothetical candidates are passed to HypoPG, **not executed as permanent DDL**. The tool compares baseline and combined-candidate planner costs in one session, cleans hypothetical indexes after success or failure, and clears the pool if cleanup fails. At most 16 candidates are accepted; `analyze=true` cannot be combined with hypothetical indexes. Independent candidate commands are batched into one database round trip.

Planner costs are estimates, not measured speedups or automatic recommendations. The summary ranks at most eight nodes by inclusive subtree cost, reports omitted nodes and does not sum overlapping costs. Health checks likewise report catalog/statistics evidence rather than universal severity thresholds. Sequence estimates depend on increment direction, caching and cycling. Replication reports database-local logical slots, not server-wide physical replication. Blocking PIDs are textual PostgreSQL arrays; another database's blocking query text is omitted.

Workload text is filtered by current database OID even for roles with server-wide monitoring privileges. Tracked statements may include transaction/session setup when `pg_stat_statements.track_utility` is enabled. High call counts alone do not identify expensive application queries; choose a relevant time, I/O or row ranking.

## Bounded results and pagination

Successful results provide a JSON object in MCP `structuredContent` and a compact JSON text compatibility block. Consume one representation rather than inserting both into model context. Column names/types appear once and rows use positional arrays, preserving duplicate column names:

```json
{
  "database":"tenant_a",
  "columns":[{"name":"id","type":"integer"},{"name":"customer","type":"integer"}],
  "rows":[[1,42],[2,17]],
  "offset":0,
  "next_offset":2,
  "truncated":true,
  "truncation_reason":"row_limit",
  "clipped_cells":[]
}
```

Metadata sections wrap the page in `page`; health/index/workload tools use `result`. Optional null envelope fields are omitted; SQL NULL remains null. DML without `RETURNING` includes `rows_affected` when known; DDL returns an empty rowset. `bytea` values are base64 strings with their PostgreSQL type retained.

- Repeat the **same read-only operation and filters** with `next_offset`. SELECT/WITH/VALUES/TABLE reads use PostgreSQL `LIMIT limit+1 OFFSET offset`; other read-only statement types use streaming row bounds.
- Pages re-execute SQL and **do not share a snapshot**. Use a stable unique `ORDER BY`; use keyset SQL for changing datasets or deep pagination. Offsets are capped at 1,000,000.
- `truncation_reason` is `row_limit` or `byte_limit`. The next offset advances by returned rows, so a byte-truncated row is not skipped.
- `clipped_cells` identifies zero-based returned-row/column coordinates. Text is bounded by UTF-16 characters without splitting surrogate pairs; binary uses a corresponding base64 budget. Remaining byte space may shorten cells further. Cell clipping is distinct from row truncation.
- Retrieve long values deliberately with SQL projections such as `substring(large_text FROM 257 FOR 256)` or `substring(binary_column FROM 193 FOR 192)`. PostgreSQL text substring positions count Unicode characters, not JSON escape bytes.
- Results allow at most 128 columns. Arrays and unbounded provider-specific composite mappings require slicing or casting in SQL. Calendar-month intervals and numbers outside .NET's scalar range return `unsupported_result_value` with projection guidance.
- A row, plan or envelope that cannot fit produces an error requesting a narrower projection/section, smaller limit or operator-approved larger budget. Complete planner JSON must fit even in summary mode; it is not silently cut into invalid JSON.
- **Writes are never paginated or replayed.** A truncated `RETURNING` response has no next offset; the full statement still commits once. Inspect committed data separately. No automatic query retries are performed.

`POSTGRES_MAX_RESULT_BYTES` bounds each payload representation, not the complete JSON-RPC envelope. Structured JSON plus compatibility text increases wire size. Actual context/token usage depends on the client and model; no cross-server efficiency claim is made.

A local 0.2.0 restricted-mode `tools/list` smoke run returned nine tools: **6,133 UTF-8 bytes** for the compact tool array and **6,178 bytes** for the newline-terminated JSON-RPC response. These are discovery bytes, not model-token counts, query timings or a comparison with an “average MCP.” Client/model tokenization and result selection determine context cost.

Operation errors set MCP `isError=true` and include target, error code and PostgreSQL SQLSTATE when available. PostgreSQL-provided messages, hints and details are withheld because they may contain sensitive values; fixed SQLSTATE-specific summaries provide guidance. Protocol/SDK argument-validation errors use the SDK envelope.

## Access and resource boundaries

Restricted mode is the default. Each operation owns a transaction with `SET TRANSACTION READ ONLY`. Client SQL is lexically limited to one statement and cannot issue transaction/session control. The statement-boundary lexer handles comments and PostgreSQL quoting; it is **not a SQL authorization AST**.

For authorized writes, start with `POSTGRES_ACCESS_MODE=unrestricted` or `--access-mode unrestricted`, then explicitly pass `read_only=false` to `execute_sql`. The server commits once on success and rolls back on failure. Default calls remain read-only even in unrestricted mode. `explain_query` and metadata/operations tools remain read-only; tool annotations advertise potentially destructive SQL execution in unrestricted mode.

Transaction/session controls, COPY, DO, CALL, PREPARE and VACUUM are unsupported in SQL tools; use an administrative client. Routine metadata inspection is supported and functions can be queried with SELECT.

**Never use a superuser role.** Read-only transactions do not sandbox PostgreSQL functions, SECURITY DEFINER routines, foreign servers/dblink, external side effects, session settings or privileged monitoring. The target allowlist restricts configured connections, not capabilities granted to the role. PostgreSQL must enforce CONNECT, schema/table/column privileges and RLS. Use separate credentials for tenants requiring separate authorization. Treat SQL results as untrusted data, not agent instructions. The server exposes stdio, not an authenticated remote transport.

Resource management:

- Lazy, thread-safe `NpgsqlDataSource` per effective normalized connection string; identical targets share a pool. Listing or rejecting aliases opens no connection.
- 1–32 target aliases, with aliases × configured pool size at most 256. Default pool maximum is 8 physical connections, minimum 0.
- Idle connections above the minimum are pruned after 60 seconds with a 10-second interval; physical lifetime is 1,800 seconds. Data sources are disposed on shutdown; operations dispose connections, readers and transactions.
- Default concurrency is 16 database operations per process. Excess calls queue with cancellation; the whole-operation deadline includes queue/pool waits. Statement, lock and command timeouts are also applied.
- Pool limits, reset-on-close, enlistment, multiplexing, application name and logging-safety settings are server-owned. Input enabling `No Reset On Close` or multiplexing is rejected. Credentials and TLS remain operator-controlled.
- Metadata is queried on demand, without a cache or per-operation preflight. This avoids stale privilege/schema results and cross-target cache leakage.

A Linux/.NET 10.0.12 stress review exercised 650 successful text/binary queries, 30 PostgreSQL errors, 30 JSON plans, three deadlines and post-timeout recovery. Post-full-GC managed heap was 5.41–5.44 MB across the last three snapshots; file descriptors stayed at 159, and shutdown left no MCP database sessions. The unchanged baseline also stabilized. These finite measurements do not prove that every workload is leak-free: GC/array-pool retention and working set are different measurements, and result limits are not a process-memory ceiling.

An extended run added 3,200 successful queries, 50 errors, 50 JSON plans and one deadline. Its last three post-full-GC snapshots were 5.49, 5.50 and 5.50 MB, with 160 file descriptors throughout those snapshots. This longer sample supports stabilization after warm-up, not an absolute no-leak guarantee.

For one synthetic 32-row response over 2,000 warmed serialization calls, allocated bytes per response fell from 36,304 to 27,736 (23.6%) after replacing the temporary JSON document/clone round trip with `SerializeToElement`. This is an allocation measurement, not a throughput or model-token claim.

SDK/provider payload logging is disabled even at debug/trace levels; host diagnostics stay on stderr. Query results, metadata and workload text may still contain sensitive data readable by the role. This is not general-purpose data redaction. See [SECURITY.md](SECURITY.md).

## Configuration reference

Choose targets JSON/file **or** a base connection string plus database allowlist; do not combine them. `POSTGRES_TARGETS` takes precedence over the file. CLI flags override corresponding environment variables, except that `POSTGRES_CONNECTION_STRING` overrides `--connection-string`. Configuration changes require a restart.

| Environment | Default / bounds |
|---|---|
| `POSTGRES_TARGETS` | JSON alias-to-connection-string object |
| `POSTGRES_TARGETS_FILE` | Protected JSON file; `--targets-file` supported |
| `POSTGRES_CONNECTION_STRING` + `POSTGRES_DATABASES` | Base Npgsql string + explicit JSON array; `--connection-string` and `--databases` supported |
| `POSTGRES_ACCESS_MODE` | restricted; `--access-mode` supported |
| `POSTGRES_QUERY_TIMEOUT` | 30 seconds; 1–600; `--query-timeout` supported |
| `POSTGRES_MAX_ROWS` | 1000; 1–5000 |
| `POSTGRES_MAX_RESULT_BYTES` | 65536; 4096–1048576 |
| `POSTGRES_MAX_CELL_CHARS` | 4096; 1–16384 |
| `POSTGRES_POOL_SIZE` | 8; 1–32; aliases × size ≤256 |
| `POSTGRES_MAX_CONCURRENT_CALLS` | 16; 1–64 |
| `POSTGRES_LOG_LEVEL` | warning; trace/debug/information/warning/error/critical/none; `--log-level` supported |

With a base connection string and explicit `POSTGRES_DATABASES` JSON array, each allowlisted name becomes an alias and replaces any `Database` in the base string. It is never a default or fallback target. Prefer a protected targets file for independent credentials and to avoid exposing secrets in process arguments.

Framework-dependent packages require the .NET 10 runtime. Self-contained executables still require native OS libraries. For example, Debian/Ubuntu GSS/Kerberos support uses `libgssapi-krb5-2`; install the platform's appropriate library if that authentication is needed. Password fallback does not verify Kerberos support. The container includes this dependency. See [Npgsql security and encryption](https://www.npgsql.org/doc/security.html).

## Development and licensing

See [CONTRIBUTING.md](CONTRIBUTING.md) for source builds, container usage, artifact installation, verification and contribution guidelines. The integration fixture uses PostgreSQL 17; this is a verification target, not a guarantee for every PostgreSQL version, platform or MCP client.

Technical references: [Npgsql data sources](https://www.npgsql.org/doc/basic-usage.html), [pool parameters](https://www.npgsql.org/doc/connection-string-parameters.html), [sequential access](https://www.npgsql.org/doc/performance.html), [C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk), [PostgreSQL EXPLAIN](https://www.postgresql.org/docs/current/sql-explain.html), [read-only transactions](https://www.postgresql.org/docs/current/sql-set-transaction.html), [pg_stat_statements](https://www.postgresql.org/docs/current/pgstatstatements.html) and [HypoPG](https://hypopg.readthedocs.io/en/latest/usage.html).

Project source is MIT, copyright codegiveness. Dependency licenses remain separate: Npgsql uses the PostgreSQL license; the MCP SDK uses Apache-2.0. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and bundled `LICENSES/` for versions, attribution and exact dependency terms.
