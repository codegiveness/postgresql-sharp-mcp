# postgresql-sharp-mcp

A C#/.NET 10 PostgreSQL MCP server using Npgsql. **One stdio server, nine shared tools, explicit database targeting on every database-dependent call.** No process-wide current database, no connection strings supplied by agents, no fallback target.

Built from the architecture and tool surface of [codegiveness/mssql-mcp](https://github.com/codegiveness/mssql-mcp), adapted to PostgreSQL. [crystaldba/postgres-mcp](https://github.com/crystaldba/postgres-mcp) informed capabilities, not implementation code.

## Build and run

Requires .NET 10 SDK to build; .NET 10 runtime to run the framework-dependent executable. PostgreSQL 17 is exercised by the integration smoke; other PostgreSQL versions are not currently verified.

```bash
git clone https://github.com/codegiveness/postgresql-sharp-mcp.git
cd postgresql-sharp-mcp
dotnet build postgresql-sharp-mcp.slnx -c Release
dotnet src/PostgreSqlMcp/bin/Release/net10.0/PostgreSqlMcp.dll --help
```

Choose **one** configuration form below. Restart after changing targets or credentials. Secrets belong in a protected local file/environment, never in tool arguments or committed files.

### Targets with independent credentials

Create `postgres.targets.local.json` outside version control; restrict file access to the server's OS user:

```json
{
  "tenant_a": "Host=localhost;Port=5432;Database=tenant_a;Username=mcp_a;Password=replace-me;SSL Mode=VerifyFull",
  "tenant_b": "Host=localhost;Port=5432;Database=tenant_b;Username=mcp_b;Password=replace-me;SSL Mode=VerifyFull"
}
```

`database` in a tool call means the **exact, case-sensitive alias** from this object, not an arbitrary PostgreSQL database name. Each connection string must contain an explicit `Host` and `Database`. Targets can use different hosts/users, but still share one tool set. For a local non-TLS development cluster, use the connection's appropriate SSL setting rather than copying production TLS settings blindly.

```bash
export POSTGRES_TARGETS_FILE=/absolute/path/postgres.targets.local.json
dotnet src/PostgreSqlMcp/bin/Release/net10.0/PostgreSqlMcp.dll --validate
dotnet src/PostgreSqlMcp/bin/Release/net10.0/PostgreSqlMcp.dll
```

`--validate` opens each configured target, returns its actual `current_database()` to **stderr**, and exits 0 only if all targets work. It does not start MCP. Normal operation reserves stdout exclusively for MCP JSON-RPC; logs go to stderr.

Alternatively set `POSTGRES_TARGETS` to the JSON object itself; it takes precedence over the file. The file path can also be passed as `--targets-file PATH`.

### Several databases using the same credentials

```bash
export POSTGRES_CONNECTION_STRING='Host=localhost;Port=5432;Username=mcp_reader;Password=replace-me'
export POSTGRES_DATABASES='["tenant_a","tenant_b"]'
dotnet src/PostgreSqlMcp/bin/Release/net10.0/PostgreSqlMcp.dll
```

This explicitly allows only `tenant_a` and `tenant_b`, using their names as target aliases. The configured base string's `Database`, if present, is replaced by each allowlisted name; it is never a default or fallback. PostgreSQL still enforces authentication, `CONNECT`, schema/table/column privileges, and RLS separately in each database. Do not combine base/allowlist configuration with targets JSON/file.

### One MCP client entry

```json
{
  "mcpServers": {
    "postgresql": {
      "command": "dotnet",
      "args": ["/absolute/path/postgresql-sharp-mcp/src/PostgreSqlMcp/bin/Release/net10.0/PostgreSqlMcp.dll"],
      "env": {
        "POSTGRES_TARGETS_FILE": "/absolute/path/postgres.targets.local.json"
      }
    }
  }
}
```

On Windows, use Windows paths with JSON-escaped backslashes. No duplicate server entry is needed when adding a target to the configuration. Ask the agent to call `list_databases` first; that lists allowlisted aliases, access mode and response limits, without connecting or exposing credentials. A configured alias is not a guarantee that its connection is currently accessible.

## Tools

Except `list_databases`, every tool **requires `database`**. No target-selection/state-changing tool exists. All limits/offsets are optional; query/metadata page defaults are `min(100, POSTGRES_MAX_ROWS)`, top queries defaults to `min(10, POSTGRES_MAX_ROWS)`.

| Tool | Capability | Focus/options |
|---|---|---|
| `list_databases` | Configured aliases and limits; no database roundtrip | `limit`, `offset` |
| `list_schemas` | Schemas with USAGE privilege | literal `prefix`, `include_system`, page |
| `list_objects` | Tables (foreign/partitioned included), views, materialized views, sequences, functions, procedures, extensions | `schema`, `type`, literal `search`, `include_system`, page |
| `get_object_details` | One object's metadata section | `schema`, `name`, `section`: columns/constraints/indexes/triggers/definition/parameters; `type`, `identity_arguments`, page |
| `execute_sql` | One SQL statement; bounded results or committed write | `sql`, `read_only`, page |
| `explain_query` | Native estimated/actual JSON plan and compact major-node summary | `sql`, `format`: summary/json, `analyze`, optional HypoPG `indexes` |
| `analyze_indexes` | Index size/usage/validity/constraint and structural duplicate evidence | `schema`, `table`, page |
| `get_top_queries` | Current-database `pg_stat_statements` workload | `order_by`: total_time/mean_time/calls/rows/reads, page |
| `analyze_db_health` | Summary or focused PostgreSQL health evidence | `section`: summary/vacuum/index/constraints/sequences/replication/blocking; `schema` where applicable, page |

Routine overloads must be disambiguated using the exact `identity_arguments` from `list_objects`, including parameter names; empty string selects zero arguments. Relation/routine name collisions additionally support `type`. Object discovery filters by role privileges; missing/hidden objects return an explicit error. Table definitions are **structural fragments**, not a pretend round-trip DDL export.

Health checks report measured catalog/statistics evidence, not universal severity thresholds. Sequence remaining-step calculations account for increment direction; cached values/cycling matter. Replication shows database-local logical slots, not unrelated server-wide physical replication. Blocking PIDs are textual PostgreSQL arrays; another database's blocking query text is not included. Workload query text is filtered by current database OID even if credentials have server-wide monitoring privileges.

### Two-target usage

MCP `tools/call` parameter examples:

```json
{"name":"execute_sql","arguments":{"database":"tenant_a","sql":"SELECT current_database(), id, customer FROM public.orders ORDER BY id","limit":20}}
```

```json
{"name":"execute_sql","arguments":{"database":"tenant_b","sql":"SELECT current_database(), id, customer FROM public.orders ORDER BY id","limit":20}}
```

These can run concurrently. Each call resolves its own immutable connection target and leases a connection from that target's pool. An invalid alias, nonexistent database, denied login or failed connection returns an error for that target, never a query against another database.

Focused inspection:

```json
{"name":"list_objects","arguments":{"database":"tenant_a","schema":"public","type":"table","search":"order","limit":20}}
```

```json
{"name":"get_object_details","arguments":{"database":"tenant_a","schema":"public","name":"orders","section":"indexes","limit":10}}
```

### Plans and optional extensions

No extensions are installed automatically. An administrator must install `pg_stat_statements` in each database where workload statistics are desired and preload it via PostgreSQL `shared_preload_libraries` (restart required). Extension schemas are discovered and quoted, not assumed to be `public`. Missing/unready extensions produce useful errors.

For HypoPG what-if analysis, the administrator must install the HypoPG extension package and `CREATE EXTENSION hypopg` in the selected database. Candidates are parameters to HypoPG, **never executed as permanent DDL**:

```json
{"name":"explain_query","arguments":{"database":"tenant_a","sql":"SELECT * FROM public.orders WHERE customer=42","indexes":["CREATE INDEX ON public.orders(customer)"]}}
```

The tool compares baseline and combined-candidate planner costs in the same session, cleans connection-local hypothetical indexes after success/failure, and clears the pool if cleanup fails. Up to 16 candidates; `analyze=true` cannot be combined with hypothetical indexes. Cost reductions are estimates, not measured speedups. Summary ranks at most eight nodes by inclusive subtree cost, reports omitted node count, and does not sum overlapping costs.

This implementation preserves all nine useful SQL Server tool categories. PostgreSQL has **no missing-index DMV**: index evidence, real query plans and optional what-if evaluation replace that engine-specific behavior. It does not copy the upstream Python project's automatic candidate-generation/Anytime search or LLM-based index optimizer. It also does not reproduce SQL Server VLF/fragmentation metrics, npm platform-distribution infrastructure, or legacy SSE; the reused transport is local stdio, with .NET tool, container and self-contained packaging.

## Compact bounded results

Successful results are a JSON object in MCP `structuredContent` with a compact JSON text compatibility block. Clients should consume one representation, not insert both copies into model context. Column names/types are sent once; row values are positional arrays, preserving duplicate column names without loss:

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

Metadata sections wrap the same page in `page`; health/index/workload tools use `result`. Null optional envelope fields are omitted; SQL NULL stays null in rows. DML without `RETURNING` includes `rows_affected` when known; DDL returns an empty rowset on success. `bytea` values are base64 strings with their PostgreSQL type retained.

- `next_offset`: call the **same read-only operation and filters** again with that offset. Read-only SELECT/WITH/VALUES/TABLE queries are wrapped with PostgreSQL `LIMIT limit+1 OFFSET offset`, so the server does not retrieve an unlimited rowset just to discard it. The extra row distinguishes an exact page from truncation.
- Pages re-execute SQL and **do not share a snapshot**. Use a stable unique `ORDER BY`; for a changing dataset or deep pagination, use keyset SQL (`WHERE id > last_seen_id ORDER BY id`). Offsets are bounded at 1,000,000. Other read-only statement types are streamed with local row bounds rather than a SELECT wrapper.
- `truncation_reason` is `row_limit` or `byte_limit`. The next offset advances by **returned** rows, so a byte-truncated row is not lost.
- `clipped_cells` identifies zero-based returned-row/column coordinates whose values were shortened. Text is streamed up to the character budget (UTF-16 characters; no split surrogate pair); binary is streamed to a corresponding base64 budget. Low remaining byte space can further shorten a cell, explicitly marked. Cell clipping is distinct from row truncation.
- Retrieve long values deliberately with SQL projections, e.g. `substring(large_text FROM 257 FOR 256)` or `substring(pg_get_functiondef(oid) FROM 257 FOR 256)`. PostgreSQL substring indexes Unicode characters, so choose offsets for the actual SQL value, not a count of JSON escape bytes. Binary values can use `substring(binary_column FROM 193 FOR 192)`.
- At most 128 columns. Arrays and unbounded provider-specific composite mappings are rejected with projection guidance: slice/cast them to text in SQL. This avoids allocating a whole huge array before applying a response cap.
- A row/plan/envelope that cannot fit returns an explicit error asking for narrower projection/section, smaller limit or an operator-approved larger byte budget. Complete planner JSON must fit even for summary; oversized plans are not silently cut into invalid JSON.
- Writes are never paginated/replayed. A truncated `RETURNING` response has **no next offset**; the full statement still commits once. Inspect committed data separately. No automatic query retries are performed, so mutation/side-effect replay is not hidden.

`POSTGRES_MAX_RESULT_BYTES` bounds each JSON payload representation, not the complete JSON-RPC envelope; compatibility text plus structured JSON increases wire size. Errors set MCP `isError=true` and include target, error code, PostgreSQL SQLSTATE/hint when available. Messages/hints are bounded explicitly. Connection configuration secrets and PostgreSQL detail fields are not returned.

## Access and resource boundaries

Restricted is default. Each operation owns a transaction with `SET TRANSACTION READ ONLY`; client SQL is lexically limited to one statement and cannot issue transaction/session control. Comments, quoted identifiers, E-strings, dollar quotes and nested comments are handled. This is a **statement-boundary lexer, not a SQL authorization AST**.

For authorized writes, start with `POSTGRES_ACCESS_MODE=unrestricted` (or `--access-mode unrestricted`) and explicitly pass `read_only=false` to `execute_sql`. Statements run in a server-owned transaction, commit once on success, and rollback on failure. Default calls remain read-only even in unrestricted mode. Tool annotations correctly advertise potentially destructive execution in unrestricted mode. `explain_query` and metadata/operations tools remain read-only.

Transaction/session controls, COPY, DO, CALL, PREPARE and VACUUM are intentionally unsupported in SQL tools; use an administrative client. Routine metadata inspection is supported; functions can be queried with SELECT. SQL capable of leaving the server-owned transaction is not enabled just to mimic unrestricted SQL Server batches.

**Use least-privileged database roles, never a superuser.** Read-only transactions prevent ordinary persistent writes but are not a sandbox for PostgreSQL functions, SECURITY DEFINER routines, external side effects, foreign servers/dblink, session settings or privileged monitoring. The allowlist controls which configured connection can be opened; it does not revoke capabilities already granted to that PostgreSQL role. Restrict those privileges in PostgreSQL, and configure per-tenant credentials when tenants require separate authorization. SQL results are untrusted database content, not agent instructions. Keep remote MCP transport disabled unless separately designed/authenticated.

Pooling/lifetime:

- Lazy, thread-safe `NpgsqlDataSource` per configured effective connection string; identical normalized aliases share a pool. Listing targets or rejecting unknown targets does not create a pool or open a connection.
- 1–32 configured aliases; configured aliases × pool size must be at most 256. Default maximum 8 physical connections per pool, minimum 0. No arbitrary target creation or unbounded cache.
- Idle connections above minimum are pruned after 60 seconds (10-second pruning interval); physical lifetime is 1,800 seconds. Data sources are disposed at host shutdown, and each connection/reader/transaction is disposed on every path.
- Default 16 concurrent database operations per process; excess calls queue with cancellation. The whole-operation deadline includes queue/pool wait; statement/lock and command timeouts are also set.
- Pool limits, reset-on-close, enlistment, multiplexing, application name, error-detail and parameter logging safety settings are server-owned. `No Reset On Close=true` and multiplexing input are rejected. Other credentials/TLS settings remain operator-controlled. No SQL/parameter/credential logging is added.
- No metadata cache: focused catalogs are read only when requested, avoiding stale schema/privilege cache results and cross-target cache leakage. There is no preflight query on every normal operation.

### Configuration reference

CLI overrides environment except that `POSTGRES_CONNECTION_STRING` overrides its CLI counterpart. Targets JSON overrides the targets file. Modes are lower-case `restricted` or `unrestricted`.

| Environment | Default / bounds |
|---|---|
| `POSTGRES_TARGETS` | JSON alias-to-connection-string object |
| `POSTGRES_TARGETS_FILE` | Protected JSON file; `--targets-file` supported |
| `POSTGRES_CONNECTION_STRING` + `POSTGRES_DATABASES` | Base Npgsql string + explicit JSON array; corresponding CLI flags supported |
| `POSTGRES_ACCESS_MODE` | restricted; `--access-mode` supported |
| `POSTGRES_QUERY_TIMEOUT` | 30 seconds; 1–600; `--query-timeout` supported |
| `POSTGRES_MAX_ROWS` | 1000; 1–5000 |
| `POSTGRES_MAX_RESULT_BYTES` | 65536; 4096–1048576 |
| `POSTGRES_MAX_CELL_CHARS` | 4096; 1–16384 |
| `POSTGRES_POOL_SIZE` | 8; 1–32; aliases × size <=256 |
| `POSTGRES_MAX_CONCURRENT_CALLS` | 16; 1–64 |
| `POSTGRES_LOG_LEVEL` | warning; trace/debug/information/warning/error/critical/none; `--log-level` supported |

## Packaging

Build/install a .NET tool locally (no NuGet.org publication is assumed):

```bash
dotnet pack src/PostgreSqlMcp/PostgreSqlMcp.csproj -c Release -o artifacts
dotnet tool install --tool-path ./artifacts/tools --add-source ./artifacts codegiveness.postgresql-sharp-mcp
./artifacts/tools/postgresql-sharp-mcp --help
```

Self-contained Linux executable:

```bash
dotnet publish src/PostgreSqlMcp/PostgreSqlMcp.csproj -c Release -r linux-x64 --self-contained -o artifacts/linux-x64
./artifacts/linux-x64/PostgreSqlMcp --version
```

Container:

```bash
docker build -t postgresql-sharp-mcp .
docker run --rm -i --mount type=bind,src=/absolute/path/postgres.targets.local.json,dst=/run/postgres.targets.json,readonly \
  -e POSTGRES_TARGETS_FILE=/run/postgres.targets.json postgresql-sharp-mcp
```

Use a database hostname reachable from the container; `localhost` inside it is not the host. Ensure the non-root runtime user can read the mounted targets file; use an appropriate group/UID or secret mount rather than making credentials world-readable.

CI runs real MCP smoke calls against disposable PostgreSQL, then packs the .NET tool. The release workflow runs smoke verification before creating self-contained Linux/Windows/macOS artifacts and a .NET tool package for an explicitly pushed `v*` tag. Cross-platform binaries are not claimed runtime-tested here. No public NuGet/npm registry package or container registry image is automatically published.

## Verification

```bash
bash scripts/verify.sh
```

Requires Docker, Python 3 and .NET SDK plus package/network access. Creates its own PostgreSQL 17 cluster with `pg_stat_statements` preload and HypoPG; destroys it on exit. It never changes operator databases. Fixture credentials are disposable, not production examples.

The consumer smoke initializes the actual MCP stdio server, checks tools, queries two databases with different sentinels concurrently, verifies pool bounds/alias reuse, pages row/byte-limited results, checks text/binary/Unicode clipping, privileges and invalid/denied/missing/unreachable targets, exercises focused metadata, all health sections, extension failures, plans/HypoPG cleanup, and explicit committed writes/DDL/RETURNING. It also checks preflight validation and base-string allowlists. Tests operate through MCP, not mocked provider wiring.

## Design references and licensing

- [Npgsql official basic usage/data sources](https://www.npgsql.org/doc/basic-usage.html), [pool parameters](https://www.npgsql.org/doc/connection-string-parameters.html), [performance/sequential access](https://www.npgsql.org/doc/performance.html).
- [Official C# MCP server documentation](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/getting-started.md), [tools/structured results](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/tools/tools.md).
- [PostgreSQL EXPLAIN](https://www.postgresql.org/docs/current/sql-explain.html), [read-only transactions](https://www.postgresql.org/docs/current/sql-set-transaction.html), [pg_stat_statements](https://www.postgresql.org/docs/current/pgstatstatements.html), [HypoPG](https://hypopg.readthedocs.io/en/latest/usage.html).

Project source is MIT, copyright codegiveness. Dependency licenses are separate: Npgsql 10.0.3 uses the PostgreSQL license; MCP SDK 2.2.0 declares Apache-2.0. Both capability references use MIT. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and bundled `LICENSES/` for exact dependency terms; no Python upstream source is bundled.
