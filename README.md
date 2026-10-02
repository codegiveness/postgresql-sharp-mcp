# postgresql-sharp-mcp — database-agnostic PostgreSQL MCP

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

A PostgreSQL MCP server built with C#/.NET 10 and Npgsql. One stdio server exposes nine tools for SQL, schema discovery, query plans, index analysis and database health. Configure one connection profile, discover accessible PostgreSQL databases live, and select a physical database on each call without adding an alias for every tenant. This is PostgreSQL-only, not support for other database engines.

**Access mode defaults to unrestricted; SQL calls still default to `read_only=true`.** Writes require an explicit `read_only=false` on `execute_sql` and PostgreSQL permission. Opt into `restricted` mode to refuse those write requests. Use least-privileged PostgreSQL roles and keep credentials in a protected configuration file, not tool arguments. Read-only transactions are not a sandbox for privileged functions or external side effects. See [SECURITY.md](SECURITY.md) for the trust boundaries.

The server, npm installation helper, packaging, verification and release automation are C#/.NET. No maintained JavaScript, Python or Bash implementation is required. npm itself requires Node.js for installation; the installed server runs directly as a .NET executable, without a Node process. Workflow badges and Scorecard report checks and practices, not certifications or profile achievements. Best Practices shows the saved owner self-assessment, which may still be in progress. See [Security posture](docs/security-posture.md) for supply-chain evidence and limits.

## Quick start

**Version boundary:** automatic discovery and the unrestricted default described below require **0.3.0 or newer**. These changes are currently unreleased; the published 0.2.0 executable still uses configured database aliases and a restricted default. Until a 0.3.0 release is published, use a [source build from this revision](CONTRIBUTING.md#build-from-source) or a locally built and verified package. Changing the targets file does not upgrade an old executable.

**Use the release archive for your first setup.** It includes the .NET runtime: no .NET SDK, npm, Node.js or Docker installation is needed for this route. You still need an existing PostgreSQL database and an MCP client. This app does not create a database or automatically edit your client's configuration.

You will download the app and create **two different JSON files**:

| Item | Purpose | Where it goes |
|---|---|---|
| `PostgreSqlMcp` (`PostgreSqlMcp.exe` on Windows) | The server your MCP client launches | `postgresql-mcp/app/` inside your home folder |
| `targets.json` | Your PostgreSQL connection details, including credentials | `postgresql-mcp/targets.json` inside your home folder |
| Your client's MCP configuration, such as OMP's `mcp.json` | Tells the client which executable to launch and which targets file to read | The MCP client's configuration location, **not** the app folder |

Follow steps 1–4 in order. Use the same paths throughout.

**Already installed?** Keep your existing targets file and paths. If your executable is 0.2.0, upgrade the binary to use automatic discovery; no per-database entries or file rename are needed. With 0.3.0 or newer, skip the download and use your actual paths in steps 3–4.

**Breaking behavior:** a targets-file entry is now a connection profile/seed, not a database allowlist. The same credentials can select other physical databases on that PostgreSQL server. Omitted access mode now means `unrestricted`, and `list_databases` returns a live database page instead of configured aliases. Keep an existing explicit `POSTGRES_ACCESS_MODE=restricted` if you want to continue refusing write requests; it is not overridden by the new default. Review PostgreSQL grants before upgrading.

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

**Checkpoint:** you should see `postgresql-sharp-mcp` followed by **0.3.0 or newer** for this discovery workflow. If the available archive is still 0.2.0, stop and use the source/local-package route above instead. Keep all extracted files together; do not copy just the executable.

Your app folder should now look like this:

```text
postgresql-mcp/
  app/
    PostgreSqlMcp         (PostgreSqlMcp.exe on Windows)
    ...other release files...
```

On Debian/Ubuntu, a missing GSS/Kerberos native library may require `libgssapi-krb5-2`. Other authentication/TLS requirements are described in [Npgsql security](https://www.npgsql.org/doc/security.html).

### 2. Configure database targets

Create a file named **`targets.json`**, next to the `app` folder—not inside it. It will hold one connection profile used to discover and connect to databases. Use a plain-text editor, not a word processor.

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
| `Database` | An accessible bootstrap database on the server, such as your existing database or `postgres`; it is used for live discovery |
| `Username` | A PostgreSQL login role with CONNECT to the bootstrap and intended databases, preferably with read-only object privileges—not necessarily your computer's username |
| `Password` | That PostgreSQL role's password |
| `SSL Mode` | `VerifyFull` for a remote TLS-enabled database; see the local-only exception below |

**`primary` is a connection profile name, not a database allowlist.** Leave it as `primary` for this guide. `"database":"primary"` still selects its configured bootstrap database; `"database":"tenant_b"` selects the real `tenant_b` database using the same host, credentials and TLS settings. Newly accessible databases need no targets-file edits or server restart. See [database selection](#database-selection-and-live-discovery) for optional profiles and selection rules.

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
        "POSTGRES_QUERY_TIMEOUT": "10"
      }
    }
  }
}
```

</details>

`command` points to the **executable**, not its folder or downloaded archive. `POSTGRES_TARGETS_FILE` points to your **credentials file**, not the client's `mcp.json`. These examples omit access mode, so it is **unrestricted**; SQL calls remain read-only unless they explicitly set `read_only=false`. To refuse writes at the server, add `"POSTGRES_ACCESS_MODE": "restricted"` to `env`. OMP's `timeout` is in milliseconds (`30000` = 30 seconds); `POSTGRES_QUERY_TIMEOUT` is in seconds (`10` = 10 seconds). Other clients may use different timeout settings.

**Checkpoint:** save the client configuration. Neither path contains `YOUR_USER`, and both files exist at those exact paths. Credentials belong only in `targets.json`.

### 4. Validate and connect

First check database connectivity, before troubleshooting the MCP client.

**Linux / macOS:**

```bash
"$HOME/postgresql-mcp/app/PostgreSqlMcp" --targets-file "$HOME/postgresql-mcp/targets.json" --validate
```

**Windows PowerShell:**

```powershell
& "$HOME\postgresql-mcp\app\PostgreSqlMcp.exe" --targets-file "$HOME\postgresql-mcp\targets.json" --validate
```

**Checkpoint:** the command should exit successfully and print a result with your actual database name. That output is on **stderr** and can contain private database identity; keep it private. `--validate` performs a read-only check and exits. It does **not** register the server or start an ongoing MCP session.

Now reload the client's MCP configuration: in **OMP, run `/mcp reload`**. In other clients, use their server reload control or fully quit and reopen the client.

Call `list_databases` with no arguments. It connects to `primary`'s bootstrap database and returns real accessible database names in `databases.rows`, not the alias `primary`. Then call:

```json
{"name":"execute_sql","arguments":{"database":"primary","sql":"SELECT 1 AS connection_ok","limit":1}}
```

**You are ready when the SQL call succeeds with one row containing `1`.** In OMP you can ask: “Use the PostgreSQL MCP server to run `SELECT 1 AS connection_ok` against the bootstrap database using alias `primary`.” No extensions or application tables are needed for this check.

To query another discovered database, replace `"database":"primary"` with its real name, for example `"database":"tenant_b"`. Discovery checks CONNECT permission, but PostgreSQL still enforces connection rules, object privileges and RLS on the actual call. After changing profile configuration or credentials, reload/restart the server; database creation and grant changes need no configuration edits.

### If setup does not work

| What you see | What to check |
|---|---|
| Executable not found / spawn error | `command` must be the full executable path, including `.exe` on Windows. Extract the archive first and keep its files together. |
| Wrong architecture / cannot execute binary | Download the archive matching your OS and CPU from step 1. |
| Invalid targets configuration / file not found | Check the exact `POSTGRES_TARGETS_FILE` path, JSON quotes/commas, and that the file is not `targets.json.txt`. Do not paste a PostgreSQL URI as the connection string. |
| Connection refused, timeout or authentication error | Run step 4's `--validate` command. Check host, port, database, login/password, network/VPN and the database's access rules. Installation does not grant database access. |
| Certificate validation error | Use the provider's correct hostname and CA certificate. Do not disable TLS for a remote database. |
| No PostgreSQL tools in the client | Check the client's configuration location/schema, save it, then reload MCP or fully restart the client. |
| Invalid target / database missing or denied | `target` must match a configured profile such as `primary`. `database` can be a real name; check spelling, CONNECT and object permissions. There is no fallback to the bootstrap database. |
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

Except `list_databases`, every tool **requires `database`**. Every tool accepts optional `target` to select a configured connection profile. Query and metadata page defaults are `min(100, POSTGRES_MAX_ROWS)`; top-query pages default to `min(10, POSTGRES_MAX_ROWS)`.

| Tool | Capability | Options |
|---|---|---|
| `list_databases` | Live accessible physical databases, selected profile, access mode and limits | `target`, `limit`, `offset` |
| `list_schemas` | Schemas with USAGE privilege | literal `prefix`, `include_system`, page |
| `list_objects` | Tables, views, materialized views, sequences, functions, procedures and extensions | `schema`, `type`, literal `search`, `include_system`, page |
| `get_object_details` | One object's metadata section | `schema`, `name`, `section`: columns/constraints/indexes/triggers/definition/parameters; `type`, `identity_arguments`, page |
| `execute_sql` | One SQL statement; bounded results or explicitly enabled writes | `sql`, `read_only`, page |
| `explain_query` | Estimated/actual JSON plan and compact major-node summary | `sql`, `format`: summary/json, `analyze`, optional HypoPG `indexes` |
| `analyze_indexes` | Index size, usage, validity, constraints and structural duplicate evidence | `schema`, `table`, page |
| `get_top_queries` | Current-database `pg_stat_statements` workload | `order_by`: total_time/mean_time/calls/rows/reads, page |
| `analyze_db_health` | Summary or focused PostgreSQL health evidence | `section`: summary/vacuum/index/constraints/sequences/replication/blocking; `schema` where applicable, page |

Routine overloads require the exact `identity_arguments` from `list_objects`, including parameter names; an empty string selects zero arguments. Use `type` to disambiguate relation/routine name collisions. Discovery filters by role privileges; missing or hidden objects return an error. Table definitions are structural fragments, not a round-trip DDL export.

Each call independently resolves its physical database and leases a connection with that database in its connection string. Concurrent calls do not share a current database or issue `USE`; a failed selection never falls back to another database.

Focused inspection:

```json
{"name":"list_objects","arguments":{"database":"primary","schema":"public","type":"table","search":"order","limit":20}}
```

```json
{"name":"get_object_details","arguments":{"database":"primary","schema":"public","name":"orders","section":"indexes","limit":10}}
```

### Database selection and live discovery

A profile is a case-sensitive targets-file key mapped to a bootstrap connection string. The default profile is `primary` when configured, otherwise the ordinal-first profile name.

- **Without `target`:** an exact configured alias in `database` selects that alias's bootstrap database for compatibility. Any other value is a physical database name on the default profile.
- **With `target`:** that profile supplies the host, authentication and TLS settings; `database` is always a physical database name. Use this form to select a database whose name happens to match an alias.
- Only the connection string's `Database` changes. Tool calls cannot supply credentials, change the host, or rewrite the protected file. An unknown profile returns `invalid_target`; a nonexistent physical database returns PostgreSQL SQLSTATE `3D000`, and insufficient privilege returns `42501`. Connectivity failures remain errors, not fallback requests.

For example, discover and select databases on the `primary` profile:

```json
{"name":"list_databases","arguments":{"target":"primary","limit":20,"offset":0}}
```

```json
{"name":"execute_sql","arguments":{"target":"primary","database":"tenant_b","sql":"SELECT current_database() AS selected_database","limit":1}}
```

`list_databases` queries live `pg_catalog.pg_database` on the selected profile's bootstrap database. It excludes templates, databases with connections disabled and databases for which the current role lacks CONNECT; an optional explicit allowlist further filters the page. Names are ordered with PostgreSQL `COLLATE "C"`, and `is_current` marks the bootstrap database used for that listing. New databases and grant/revoke changes appear on subsequent calls without restarting; listing does not guarantee network/authentication or object access for a later connection.

The response is `{ "target": "...", "databases": { ... }, "access_mode": "...", "limits": { ... } }`. `databases` is the same bounded query-page shape described below, with `columns` named `name` and `is_current`, positional `rows` such as `[["postgres",true],["tenant_b",false]]`, and `offset`, `next_offset`, `truncated`, `truncation_reason` and `clipped_cells`. Follow `databases.next_offset` using the same `target` and `limit`; pages are live queries, not a shared snapshot. The obsolete `targets` alias-list payload is no longer returned. Catalog failures are reported rather than silently replaced with configured aliases.

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

Metadata sections wrap the page in `page`; health/index/workload tools use `result`; `list_databases` uses `databases`. Optional null envelope fields are omitted; SQL NULL remains null. DML without `RETURNING` includes `rows_affected` when known; DDL returns an empty rowset. `bytea` values are base64 strings with their PostgreSQL type retained.

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

**Unrestricted mode is the default when access mode is omitted.** Each read-only operation owns a transaction with `SET TRANSACTION READ ONLY`. Client SQL is lexically limited to one statement and cannot issue transaction/session control. The statement-boundary lexer handles comments and PostgreSQL quoting; it is **not a SQL authorization AST**.

For authorized writes, explicitly pass `read_only=false` to `execute_sql`; the PostgreSQL role must also have the necessary privileges. The server commits once on success and rolls back on failure. Default calls remain read-only in unrestricted mode. To refuse writes, opt into `POSTGRES_ACCESS_MODE=restricted` or `--access-mode restricted`; existing explicit restricted settings remain effective. `explain_query` and metadata/operations tools remain read-only; tool annotations advertise potentially destructive SQL execution in unrestricted mode.

Transaction/session controls, COPY, DO, CALL, PREPARE and VACUUM are unsupported in SQL tools; use an administrative client. Routine metadata inspection is supported and functions can be queried with SELECT.

**Never use a superuser role.** Read-only transactions do not sandbox PostgreSQL functions, SECURITY DEFINER routines, foreign servers/dblink, external side effects, session settings or privileged monitoring. Profiles constrain host/authentication/TLS, not which physical databases those credentials can access. PostgreSQL must enforce CONNECT, schema/table/column privileges and RLS; review `PUBLIC` CONNECT grants. Use separate credentials for tenants requiring separate authorization. An optional explicit database allowlist narrows selection but is not database authorization. Treat SQL results as untrusted data, not agent instructions. The server exposes stdio, not an authenticated remote transport.

Resource management:

- Lazy, thread-safe `NpgsqlDataSource` per effective normalized connection configuration, including physical database, credentials and TLS; equivalent profiles share a pool. Live listing opens the bootstrap connection.
- 1–32 configured profiles, with profiles × configured pool size at most 256. The runtime cache is separately bounded to `floor(256 / POSTGRES_POOL_SIZE)` database pools, so selecting new databases does not accumulate unbounded pools. Default pool maximum is 8 physical connections, minimum 0.
- Idle least-recently-used pool entries are evicted and disposed when capacity is needed; in-flight entries are never evicted. If every entry is active, new selections wait within the original operation deadline. Failed database selections do not accumulate pool entries.
- Idle connections above the minimum are pruned after 60 seconds with a 10-second interval; physical lifetime is 1,800 seconds. Data sources are disposed on shutdown; operations dispose connections, readers and transactions.
- Default concurrency is 16 database operations per process. Excess calls queue with cancellation; the whole-operation deadline includes queue/pool waits. Statement, lock and command timeouts are also applied.
- Pool limits, reset-on-close, enlistment, multiplexing, application name and logging-safety settings are server-owned. Input enabling `No Reset On Close` or multiplexing is rejected. Credentials and TLS remain operator-controlled.
- Metadata and database catalogs are queried on demand, without a result cache or per-operation preflight. This avoids stale privilege/schema results and cross-database cache leakage.

A pre-discovery Linux/.NET 10.0.12 stress review exercised 650 successful text/binary queries, 30 PostgreSQL errors, 30 JSON plans, three deadlines and post-timeout recovery. Post-full-GC managed heap was 5.41–5.44 MB across the last three snapshots; file descriptors stayed at 159, and shutdown left no MCP database sessions. The unchanged baseline also stabilized. These historical finite measurements do not verify the new dynamic pool cache or prove that every workload is leak-free: GC/array-pool retention and working set are different measurements, and result limits are not a process-memory ceiling.

An extended run added 3,200 successful queries, 50 errors, 50 JSON plans and one deadline. Its last three post-full-GC snapshots were 5.49, 5.50 and 5.50 MB, with 160 file descriptors throughout those snapshots. This longer sample supports stabilization after warm-up, not an absolute no-leak guarantee.

For one synthetic 32-row response over 2,000 warmed serialization calls, allocated bytes per response fell from 36,304 to 27,736 (23.6%) after replacing the temporary JSON document/clone round trip with `SerializeToElement`. This is an allocation measurement, not a throughput or model-token claim.

SDK/provider payload logging is disabled even at debug/trace levels; host diagnostics stay on stderr. Query results, metadata and workload text may still contain sensitive data readable by the role. This is not general-purpose data redaction. See [SECURITY.md](SECURITY.md).

## Configuration reference

Choose targets JSON/file **or** a base connection string with an optional database allowlist; do not combine them. `POSTGRES_TARGETS` takes precedence over the file. CLI flags override corresponding environment variables, except that `POSTGRES_CONNECTION_STRING` overrides `--connection-string`. Profile/credential configuration changes require a restart; live database/grant changes do not.

| Environment | Default / bounds |
|---|---|
| `POSTGRES_TARGETS` | JSON profile-name-to-bootstrap-connection-string object |
| `POSTGRES_TARGETS_FILE` | Protected JSON profile file; `--targets-file` supported |
| `POSTGRES_CONNECTION_STRING` | Base Npgsql string; bootstrap `Database` defaults to `postgres` if omitted; `--connection-string` supported |
| `POSTGRES_DATABASES` | Optional explicit JSON database-name array with the base string; `--databases` supported |
| `POSTGRES_ACCESS_MODE` | unrestricted when omitted; opt into restricted to refuse writes; `--access-mode` supported |
| `POSTGRES_QUERY_TIMEOUT` | 30 seconds; 1–600; `--query-timeout` supported |
| `POSTGRES_MAX_ROWS` | 1000; 1–5000 |
| `POSTGRES_MAX_RESULT_BYTES` | 65536; 4096–1048576 |
| `POSTGRES_MAX_CELL_CHARS` | 4096; 1–16384 |
| `POSTGRES_POOL_SIZE` | 8; 1–32; profiles × size ≤256; runtime database-pool cache ≤`floor(256 / size)` |
| `POSTGRES_MAX_CONCURRENT_CALLS` | 16; 1–64 |
| `POSTGRES_LOG_LEVEL` | warning; trace/debug/information/warning/error/critical/none; `--log-level` supported |

A base connection string alone creates the `primary` profile and supports live discovery. With an explicit `POSTGRES_DATABASES` JSON array, each allowlisted name becomes a seed alias and replaces any `Database` in the base string; catalog results are filtered to these names and selection of a nonallowlisted physical database is rejected. This allowlist is optional and must be maintained if used. Targets-file aliases are **not** an implicit allowlist. Prefer a protected targets file for independent credentials and to avoid exposing secrets in process arguments.

Framework-dependent packages require the .NET 10 runtime. Self-contained executables still require native OS libraries. For example, Debian/Ubuntu GSS/Kerberos support uses `libgssapi-krb5-2`; install the platform's appropriate library if that authentication is needed. Password fallback does not verify Kerberos support. The container includes this dependency. See [Npgsql security and encryption](https://www.npgsql.org/doc/security.html).

## Development and licensing

See [CONTRIBUTING.md](CONTRIBUTING.md) for source builds, container usage, artifact installation, verification and contribution guidelines. The integration fixture uses PostgreSQL 17; this is a verification target, not a guarantee for every PostgreSQL version, platform or MCP client.

Technical references: [Npgsql data sources](https://www.npgsql.org/doc/basic-usage.html), [pool parameters](https://www.npgsql.org/doc/connection-string-parameters.html), [sequential access](https://www.npgsql.org/doc/performance.html), [C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk), [PostgreSQL EXPLAIN](https://www.postgresql.org/docs/current/sql-explain.html), [read-only transactions](https://www.postgresql.org/docs/current/sql-set-transaction.html), [pg_stat_statements](https://www.postgresql.org/docs/current/pgstatstatements.html) and [HypoPG](https://hypopg.readthedocs.io/en/latest/usage.html).

Project source is MIT, copyright codegiveness. Dependency licenses remain separate: Npgsql uses the PostgreSQL license; the MCP SDK uses Apache-2.0. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and bundled `LICENSES/` for versions, attribution and exact dependency terms.
