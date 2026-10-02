# postgresql-sharp-mcp

[![CI](https://github.com/codegiveness/postgresql-sharp-mcp/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/codegiveness/postgresql-sharp-mcp/actions/workflows/ci.yml)
[![CodeQL](https://github.com/codegiveness/postgresql-sharp-mcp/actions/workflows/codeql.yml/badge.svg?branch=main)](https://github.com/codegiveness/postgresql-sharp-mcp/actions/workflows/codeql.yml)
[![Dependency audit](https://github.com/codegiveness/postgresql-sharp-mcp/actions/workflows/security.yml/badge.svg?branch=main)](https://github.com/codegiveness/postgresql-sharp-mcp/actions/workflows/security.yml)
[![Secret scanning](https://github.com/codegiveness/postgresql-sharp-mcp/actions/workflows/secrets.yml/badge.svg?branch=main)](https://github.com/codegiveness/postgresql-sharp-mcp/actions/workflows/secrets.yml)
[![Container security](https://github.com/codegiveness/postgresql-sharp-mcp/actions/workflows/container-security.yml/badge.svg?branch=main)](https://github.com/codegiveness/postgresql-sharp-mcp/actions/workflows/container-security.yml)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/codegiveness/postgresql-sharp-mcp/badge)](https://scorecard.dev/viewer/?uri=github.com/codegiveness/postgresql-sharp-mcp)
[![OpenSSF Best Practices](https://www.bestpractices.dev/projects/15155/badge.svg)](https://www.bestpractices.dev/en/projects/15155)
[![.NET](https://img.shields.io/badge/.NET-10-blue)](https://dotnet.microsoft.com/)
[![SBOM](https://img.shields.io/badge/SBOM-CycloneDX-blue)](docs/security-posture.md#supply-chain-evidence)
[![Security Policy](https://img.shields.io/badge/Security-Policy-blue)](SECURITY.md)
[![GitHub release](https://img.shields.io/github/v/release/codegiveness/postgresql-sharp-mcp)](https://github.com/codegiveness/postgresql-sharp-mcp/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A PostgreSQL MCP server built with C#/.NET 10 and Npgsql. One stdio server exposes nine tools for SQL, schema discovery, query plans, index analysis and database health. Every database-dependent call selects an explicitly configured target; there is no process-wide current database or fallback connection.

**Read-only by default.** Use least-privileged PostgreSQL roles and keep credentials in a protected configuration file, not tool arguments. Read-only transactions are not a sandbox for privileged functions or external side effects. See [SECURITY.md](SECURITY.md) for the trust boundaries.

The server, npm installation helper, packaging, verification and release automation are C#/.NET. No maintained JavaScript, Python or Bash implementation is required. npm itself requires Node.js for installation; the installed server runs directly as a .NET executable, without a Node process. Workflow badges and Scorecard report checks and practices, not certifications or profile achievements. Best Practices shows the saved owner self-assessment, which may still be in progress. See [Security posture](docs/security-posture.md) for supply-chain evidence and limits.

## Quick start

**Use the release archive for your first setup.** It includes the .NET runtime: no .NET SDK, npm, Node.js or Docker installation is needed for this route. You still need an existing PostgreSQL database and an MCP client. This app does not create a database or automatically edit your client's configuration.

You will download the app and create **two different JSON files**:

| Item | Purpose | Where it goes |
|---|---|---|
| `PostgreSqlMcp` (`PostgreSqlMcp.exe` on Windows) | The server your MCP client launches | `postgresql-mcp/app/` inside your home folder |
| `targets.json` | Your PostgreSQL connection details, including credentials | `postgresql-mcp/targets.json` inside your home folder |
| Your client's MCP configuration, such as OMP's `mcp.json` | Tells the client which executable to launch and which targets file to read | The MCP client's configuration location, **not** the app folder |

Follow steps 1–4 in order. Use the same paths throughout.

**Already installed?** Keep your existing executable and targets file. Skip the download, then use their actual paths in steps 3–4; you do not need to move or rename working files.

### 1. Install or run

Open [the latest release](https://github.com/codegiveness/postgresql-sharp-mcp/releases/latest) and download **one** archive:

| Your computer | Archive to download |
|---|---|
| Linux, Intel/AMD 64-bit | `postgresql-sharp-mcp-linux-x64.tar.gz` |
| Linux, ARM64 | `postgresql-sharp-mcp-linux-arm64.tar.gz` |
| Mac, Apple Silicon (M-series) | `postgresql-sharp-mcp-osx-arm64.tar.gz` |
| Mac, Intel | `postgresql-sharp-mcp-osx-x64.tar.gz` |
| Windows, Intel/AMD 64-bit | `postgresql-sharp-mcp-win-x64.zip` |

Also download `SHA256SUMS` from **that same release**. Before extracting, run the checksum command for your OS below and compare its hash with the entry for your archive in `SHA256SUMS` (ignore uppercase/lowercase differences). Stop if the hashes differ.

These commands assume the archive is in your `Downloads` folder. Change that location if you saved it elsewhere.

**Linux x64:**

```bash
sha256sum "$HOME/Downloads/postgresql-sharp-mcp-linux-x64.tar.gz"
```

If the hash matches, extract and check the executable:

```bash
mkdir -p "$HOME/postgresql-mcp/app"
tar -xzf "$HOME/Downloads/postgresql-sharp-mcp-linux-x64.tar.gz" -C "$HOME/postgresql-mcp/app"
"$HOME/postgresql-mcp/app/PostgreSqlMcp" --version
```

For Linux ARM64, replace the archive filename in both commands with `postgresql-sharp-mcp-linux-arm64.tar.gz`.

**macOS, Apple Silicon:**

```bash
shasum -a 256 "$HOME/Downloads/postgresql-sharp-mcp-osx-arm64.tar.gz"
```

If the hash matches, extract and check the executable:

```bash
mkdir -p "$HOME/postgresql-mcp/app"
tar -xzf "$HOME/Downloads/postgresql-sharp-mcp-osx-arm64.tar.gz" -C "$HOME/postgresql-mcp/app"
"$HOME/postgresql-mcp/app/PostgreSqlMcp" --version
```

For an Intel Mac, replace the archive filename in both commands with `postgresql-sharp-mcp-osx-x64.tar.gz`.

**Windows:** open PowerShell and run:

```powershell
Get-FileHash "$HOME\Downloads\postgresql-sharp-mcp-win-x64.zip" -Algorithm SHA256
```

If the hash matches, extract and check the executable:

```powershell
New-Item -ItemType Directory -Force "$HOME\postgresql-mcp\app" | Out-Null
Expand-Archive -Path "$HOME\Downloads\postgresql-sharp-mcp-win-x64.zip" -DestinationPath "$HOME\postgresql-mcp\app"
& "$HOME\postgresql-mcp\app\PostgreSqlMcp.exe" --version
```

**Checkpoint:** you should see `postgresql-sharp-mcp` followed by a version number. Keep all extracted files together; do not copy just the executable.

Your app folder should now look like this:

```text
postgresql-mcp/
  app/
    PostgreSqlMcp         (PostgreSqlMcp.exe on Windows)
    ...other release files...
```

On Debian/Ubuntu, a missing GSS/Kerberos native library may require `libgssapi-krb5-2`. Other authentication/TLS requirements are described in [Npgsql security](https://www.npgsql.org/doc/security.html).

### 2. Configure database targets

Create a file named **`targets.json`**, next to the `app` folder—not inside it. Use a plain-text editor, not a word processor.

On Linux/macOS, create the empty file with private permissions **before** pasting credentials:

```bash
chmod 700 "$HOME/postgresql-mcp"
touch "$HOME/postgresql-mcp/targets.json"
chmod 600 "$HOME/postgresql-mcp/targets.json"
```

On Windows, save it as `targets.json` in your home folder's `postgresql-mcp` directory. In Notepad's Save As dialog choose **All files**, so it is not accidentally saved as `targets.json.txt`. Keep the folder private to your account and administrators; do not put it in a shared or publicly synced folder.

Paste this whole JSON object into `targets.json`, then replace each `<...>` placeholder with your database's actual connection details. Remove the angle brackets too:

```json
{
  "primary": "Host=<YOUR_HOST>;Port=5432;Database=<YOUR_DATABASE>;Username=<YOUR_DATABASE_USER>;Password=<YOUR_PASSWORD>;SSL Mode=VerifyFull"
}
```

Also change `Port=5432` if your provider uses another port:

| Field | What to enter |
|---|---|
| `Host` | The database hostname supplied by your provider, or `127.0.0.1` for a database on this same computer |
| `Port` | PostgreSQL's port, usually `5432`; use your provider's port if different |
| `Database` | Your real PostgreSQL database name |
| `Username` | A PostgreSQL login role allowed to access that database, preferably read-only—not necessarily your computer's username |
| `Password` | That PostgreSQL role's password |
| `SSL Mode` | `VerifyFull` for a remote TLS-enabled database; see the local-only exception below |

**`primary` is just the name you will use in MCP tool calls.** It does not need to match `Database`. Leave it as `primary` for this guide.

For a PostgreSQL server on **this same computer** that does not support TLS, use `Host=127.0.0.1` and replace `SSL Mode=VerifyFull` with `SSL Mode=Disable`. Do not disable TLS to work around a remote server's certificate error. For a remote server needing a provider CA certificate, retain `VerifyFull` and add `Root Certificate=<absolute-path-to-provider-CA-file>`; follow your provider's [TLS requirements](https://www.npgsql.org/doc/security.html).

If your provider gives you a `postgres://` or `postgresql://` URL, it cannot be pasted here unchanged. Map its hostname, port, database, username and password to the fields above, decode URL-escaped values, and preserve its required TLS/authentication options. Prefer the provider's **.NET/Npgsql connection string** when available.

<details>
<summary>My password contains a semicolon, quote or backslash</summary>

There are two layers of escaping: the connection string, then JSON. A password containing `;` must be quoted inside the connection string. For example, the **synthetic** password `sample;value` is written as `Password=\"sample;value\"` inside the JSON string:

```json
{
  "primary": "Host=<YOUR_HOST>;Port=5432;Database=<YOUR_DATABASE>;Username=<YOUR_DATABASE_USER>;Password=\"sample;value\";SSL Mode=VerifyFull"
}
```

Replace that sample password with your own; keep the surrounding `\"` markers. Inside a quoted connection-string value, double a literal double quote, then escape each quote for JSON. JSON backslashes must also be doubled. If your provider can export an Npgsql connection string, prefer that over constructing one by hand; still escape it as a JSON string.

</details>

**File naming:** `targets.json` is our simple convention, not a required magic name. `targets-0.2.0.json` also works, but a version number is unnecessary. The name and location must exactly match `POSTGRES_TARGETS_FILE` in step 3. Do not commit this file or paste its contents into chat or public bug reports.

**Checkpoint:** your file should contain one JSON object with a `primary` key and a populated connection string. No trailing comma after that entry.

### 3. Add a stdio MCP client entry

**This step is required. Installing the app alone does not make its tools appear in your client.** The client starts the server for you; you do not need to leave a separate server terminal running.

For **OMP**, edit or create `~/.omp/agent/mcp.json` on Linux/macOS, or `%USERPROFILE%\.omp\agent\mcp.json` on Windows. If you use a named OMP profile, edit that profile's MCP configuration instead. For another MCP client, open that client's MCP/server configuration; the examples below use the `mcpServers` format, not every client's schema.

If the file already contains other servers, add only the `"postgresql": { ... }` entry **inside its existing `mcpServers` object**. Preserve the other entries and separate adjacent entries with a comma. Do not add a second `mcpServers` object.

For a new configuration file, use the whole example for your OS below. Replace **`YOUR_USER` in both paths** with your home-folder name. On Linux/macOS, `echo "$HOME"` shows your home path; on Windows, `$HOME` in PowerShell shows it. If your home is elsewhere, replace the entire example home prefix with that actual path. Use full absolute paths in JSON, not literal `~`, `$HOME` or `%USERPROFILE%`.

<details open>
<summary>Linux: complete MCP configuration</summary>

```json
{
  "mcpServers": {
    "postgresql": {
      "type": "stdio",
      "command": "/home/YOUR_USER/postgresql-mcp/app/PostgreSqlMcp",
      "timeout": 30000,
      "env": {
        "POSTGRES_TARGETS_FILE": "/home/YOUR_USER/postgresql-mcp/targets.json",
        "POSTGRES_ACCESS_MODE": "restricted",
        "POSTGRES_QUERY_TIMEOUT": "10"
      }
    }
  }
}
```

</details>

<details>
<summary>macOS: complete MCP configuration</summary>

```json
{
  "mcpServers": {
    "postgresql": {
      "type": "stdio",
      "command": "/Users/YOUR_USER/postgresql-mcp/app/PostgreSqlMcp",
      "timeout": 30000,
      "env": {
        "POSTGRES_TARGETS_FILE": "/Users/YOUR_USER/postgresql-mcp/targets.json",
        "POSTGRES_ACCESS_MODE": "restricted",
        "POSTGRES_QUERY_TIMEOUT": "10"
      }
    }
  }
}
```

</details>

<details>
<summary>Windows: complete MCP configuration</summary>

```json
{
  "mcpServers": {
    "postgresql": {
      "type": "stdio",
      "command": "C:\\Users\\YOUR_USER\\postgresql-mcp\\app\\PostgreSqlMcp.exe",
      "timeout": 30000,
      "env": {
        "POSTGRES_TARGETS_FILE": "C:\\Users\\YOUR_USER\\postgresql-mcp\\targets.json",
        "POSTGRES_ACCESS_MODE": "restricted",
        "POSTGRES_QUERY_TIMEOUT": "10"
      }
    }
  }
}
```

</details>

`command` points to the **executable**, not its folder or downloaded archive. `POSTGRES_TARGETS_FILE` points to your **credentials file**, not the client's `mcp.json`. `restricted` keeps this guide read-only. OMP's `timeout` is in milliseconds (`30000` = 30 seconds); `POSTGRES_QUERY_TIMEOUT` is in seconds (`10` = 10 seconds). Other clients may use different timeout settings.

**Checkpoint:** save the client configuration. Neither path contains `YOUR_USER`, and both files exist at those exact paths. Credentials belong only in `targets.json`.

### 4. Validate and connect

First check database connectivity, before troubleshooting the MCP client.

**Linux / macOS:**

```bash
"$HOME/postgresql-mcp/app/PostgreSqlMcp" --targets-file "$HOME/postgresql-mcp/targets.json" --access-mode restricted --validate
```

**Windows PowerShell:**

```powershell
& "$HOME\postgresql-mcp\app\PostgreSqlMcp.exe" --targets-file "$HOME\postgresql-mcp\targets.json" --access-mode restricted --validate
```

**Checkpoint:** the command should exit successfully and print a result with your actual database name. That output is on **stderr** and can contain private database identity; keep it private. `--validate` performs a read-only check and exits. It does **not** register the server or start an ongoing MCP session.

Now reload the client's MCP configuration: in **OMP, run `/mcp reload`**. In other clients, use their server reload control or fully quit and reopen the client.

Call `list_databases` with no arguments. It should list **`primary`**. Then call:

```json
{"name":"execute_sql","arguments":{"database":"primary","sql":"SELECT 1 AS connection_ok","limit":1}}
```

**You are ready when the SQL call succeeds with one row containing `1`.** In OMP you can ask: “Use the PostgreSQL MCP server to run `SELECT 1 AS connection_ok` against database alias `primary`.” No extensions or application tables are needed for this check.

`list_databases` lists configured aliases without opening a database connection, so seeing `primary` alone is not proof of connectivity. After changing credentials or targets, reload/restart the server again.

### If setup does not work

| What you see | What to check |
|---|---|
| Executable not found / spawn error | `command` must be the full executable path, including `.exe` on Windows. Extract the archive first and keep its files together. |
| Wrong architecture / cannot execute binary | Download the archive matching your OS and CPU from step 1. |
| Invalid targets configuration / file not found | Check the exact `POSTGRES_TARGETS_FILE` path, JSON quotes/commas, and that the file is not `targets.json.txt`. Do not paste a PostgreSQL URI as the connection string. |
| Connection refused, timeout or authentication error | Run step 4's `--validate` command. Check host, port, database, login/password, network/VPN and the database's access rules. Installation does not grant database access. |
| Certificate validation error | Use the provider's correct hostname and CA certificate. Do not disable TLS for a remote database. |
| No PostgreSQL tools in the client | Check the client's configuration location/schema, save it, then reload MCP or fully restart the client. |
| Unknown database alias | Use `"database": "primary"`, matching the key in `targets.json`, not the real PostgreSQL database name. |
| Server seems to wait silently when run without `--validate` | Normal: stdio mode waits for MCP messages from a client. Use `--validate` for a terminal connectivity check; let the client launch normal mode. |

Normal operation reserves stdout for MCP JSON-RPC; diagnostics go to stderr. Keep real credentials, SQL and database results out of public troubleshooting reports.

### Other installation methods

Finish the same targets-file and client-configuration steps above with whichever executable you install. **Choose one install method; do not install all of them.**

**NuGet/.NET tool:** requires the **.NET 10 SDK** to install, the **.NET 10 runtime** to run, and the package to be available on NuGet.org:

```bash
dotnet tool install --global codegiveness.postgresql-sharp-mcp
postgresql-sharp-mcp --version
```

In the client configuration, replace `command` with the installed tool's absolute path: normally `~/.dotnet/tools/postgresql-sharp-mcp` on Linux/macOS or `%USERPROFILE%\.dotnet\tools\postgresql-sharp-mcp.exe` on Windows. Expand the home path before putting it in JSON. For a custom `--tool-path` installation, use that directory's executable instead; a path containing a version such as `tools/0.2.0/` is valid but not required. Keep the .NET runtime available to the client; nonstandard installations may require `DOTNET_ROOT`.

If the package is unavailable on NuGet.org, use the recommended release archive or [install a verified release `.nupkg` locally](CONTRIBUTING.md#install-a-verified-release-artifact). Local artifact installation does not establish registry publication. Do not point the MCP client directly at a `.nupkg`.

**npm/npx:** requires **Node.js 22 or newer**, the **.NET 10 runtime** with `dotnet` on `PATH` during installation, and the package to be available in npm:

```bash
npx -y --allow-scripts=@codegiveness/postgresql-sharp-mcp @codegiveness/postgresql-sharp-mcp --version
```

Use `"command": "npx"` with `"args": ["-y", "--allow-scripts=@codegiveness/postgresql-sharp-mcp", "@codegiveness/postgresql-sharp-mcp"]` in place of the archive executable; keep the same `env` object from step 3. npm 12 requires this package's lifecycle script approval. On Windows, clients unable to launch `npx.cmd` directly can use `"command": "cmd"` and `"args": ["/d", "/c", "npx", "-y", "--allow-scripts=@codegiveness/postgresql-sharp-mcp", "@codegiveness/postgresql-sharp-mcp"]`.

The C# installer uses bundled native apphosts; it does not download a runtime or binaries. After installation the server runs directly as .NET, without a JavaScript launcher or Node child process. Custom .NET installations also need an appropriate `DOTNET_ROOT`.

**Build from source:** follow [CONTRIBUTING.md](CONTRIBUTING.md#build-from-source), then use `dotnet` as `command` and the absolute path to `PostgreSqlMcp.dll` as its first `args` item. Keep the same targets-file environment settings.

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
{"name":"list_objects","arguments":{"database":"primary","schema":"public","type":"table","search":"order","limit":20}}
```

```json
{"name":"get_object_details","arguments":{"database":"primary","schema":"public","name":"orders","section":"indexes","limit":10}}
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
{"name":"explain_query","arguments":{"database":"primary","sql":"SELECT * FROM public.orders WHERE customer=42","indexes":["CREATE INDEX ON public.orders(customer)"]}}
```

Hypothetical candidates are passed to HypoPG, **not executed as permanent DDL**. The tool compares baseline and combined-candidate planner costs in one session, cleans hypothetical indexes after success or failure, and clears the pool if cleanup fails. At most 16 candidates are accepted; `analyze=true` cannot be combined with hypothetical indexes. Independent candidate commands are batched into one database round trip.

Planner costs are estimates, not measured speedups or automatic recommendations. The summary ranks at most eight nodes by inclusive subtree cost, reports omitted nodes and does not sum overlapping costs. Health checks likewise report catalog/statistics evidence rather than universal severity thresholds. Sequence estimates depend on increment direction, caching and cycling. Replication reports database-local logical slots, not server-wide physical replication. Blocking PIDs are textual PostgreSQL arrays; another database's blocking query text is omitted.

Workload text is filtered by current database OID even for roles with server-wide monitoring privileges. Tracked statements may include transaction/session setup when `pg_stat_statements.track_utility` is enabled. High call counts alone do not identify expensive application queries; choose a relevant time, I/O or row ranking.

## Bounded results and pagination

Successful results provide a JSON object in MCP `structuredContent` and a compact JSON text compatibility block. Consume one representation rather than inserting both into model context. Column names/types appear once and rows use positional arrays, preserving duplicate column names:

```json
{
  "database":"primary",
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
