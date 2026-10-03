# postgresql-sharp-mcp — PostgreSQL tools for your AI assistant

[![CI](https://github.com/codegiveness/postgresql-sharp-mcp/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/codegiveness/postgresql-sharp-mcp/actions/workflows/ci.yml)
[![GitHub release](https://img.shields.io/github/v/release/codegiveness/postgresql-sharp-mcp)](https://github.com/codegiveness/postgresql-sharp-mcp/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

This app lets an MCP-compatible AI assistant connect to PostgreSQL, list the databases you can access, inspect tables, and run SQL. It also offers query plans, index analysis and database health checks. Your MCP client starts the app when needed; it does not create a PostgreSQL database for you.

**For one PostgreSQL server, you need one connection string—not a targets file or an entry for every database.** You also need access to an existing database and an installed MCP client. The walkthrough below uses **OMP**. It stores only the executable path and nonsensitive options in OMP's configuration; you enter credentials privately each session.

SQL calls are read-only by default, but the server's default access mode permits explicitly requested writes. Use a least-privileged database login, never a superuser. Keep an existing explicit `restricted` setting; [access boundaries](#access-and-resource-boundaries) explain how to refuse writes entirely.

## Quick start

**Already installed and registered?** Skip to [step 2](#2-prepare-the-connection-environment). Switching from a targets file? Open **Advanced setup, troubleshooting and legacy profiles** below first. Keep your existing file.

### 1. Install or run

Download **one archive** and `SHA256SUMS` from [release 0.3.1](https://github.com/codegiveness/postgresql-sharp-mcp/releases/tag/v0.3.1). These archives include the .NET runtime: you do not need the .NET SDK, npm, Node.js or Docker for this installation.

| Your computer | Archive |
|---|---|
| Linux, Intel/AMD 64-bit | `postgresql-sharp-mcp-linux-x64.tar.gz` |
| Linux, ARM64 | `postgresql-sharp-mcp-linux-arm64.tar.gz` |
| Mac, Apple Silicon (M-series) | `postgresql-sharp-mcp-osx-arm64.tar.gz` |
| Mac, Intel | `postgresql-sharp-mcp-osx-x64.tar.gz` |
| Windows, Intel/AMD 64-bit | `postgresql-sharp-mcp-win-x64.zip` |

Extract into **`postgresql-mcp/app` inside your home folder**, keeping all the extracted files together. Open the instructions for your OS below to check the download, extract it and check the version. Commands assume you saved the archive in `Downloads`.

<details>
<summary>Linux: verify and extract</summary>

Compare the hash printed by the first command with your archive's entry in the downloaded `SHA256SUMS`. Stop if they differ; extract only after they match. For ARM64, replace both archive filenames with `postgresql-sharp-mcp-linux-arm64.tar.gz`.

```sh
sha256sum "$HOME/Downloads/postgresql-sharp-mcp-linux-x64.tar.gz"
```

```sh
mkdir -p "$HOME/postgresql-mcp/app"
tar -xzf "$HOME/Downloads/postgresql-sharp-mcp-linux-x64.tar.gz" -C "$HOME/postgresql-mcp/app"
"$HOME/postgresql-mcp/app/PostgreSqlMcp" --version
```

</details>

<details>
<summary>macOS: verify and extract</summary>

Compare the hash printed by the first command with your archive's entry in the downloaded `SHA256SUMS`. Stop if they differ; extract only after they match. For an Intel Mac, replace both archive filenames with `postgresql-sharp-mcp-osx-x64.tar.gz`.

```sh
shasum -a 256 "$HOME/Downloads/postgresql-sharp-mcp-osx-arm64.tar.gz"
```

```sh
mkdir -p "$HOME/postgresql-mcp/app"
tar -xzf "$HOME/Downloads/postgresql-sharp-mcp-osx-arm64.tar.gz" -C "$HOME/postgresql-mcp/app"
"$HOME/postgresql-mcp/app/PostgreSqlMcp" --version
```

</details>

<details>
<summary>Windows: verify and extract in PowerShell</summary>

Compare the hash printed by the first command with your archive's entry in the downloaded `SHA256SUMS` (case does not matter). Stop if they differ; extract only after they match.

```powershell
Get-FileHash "$HOME\Downloads\postgresql-sharp-mcp-win-x64.zip" -Algorithm SHA256
```

```powershell
New-Item -ItemType Directory -Force "$HOME\postgresql-mcp\app" | Out-Null
Expand-Archive -Path "$HOME\Downloads\postgresql-sharp-mcp-win-x64.zip" -DestinationPath "$HOME\postgresql-mcp\app"
& "$HOME\postgresql-mcp\app\PostgreSqlMcp.exe" --version
```

</details>

The executable should report **0.3.1**. If you already have an older installation, read [upgrade notes](#upgrading-an-existing-installation) before reusing its configuration.

**Register with OMP once.** Edit or create `~/.omp/agent/mcp.json` on Linux/macOS, or `%USERPROFILE%\.omp\agent\mcp.json` on Windows (for a named OMP profile, use that profile's MCP configuration). Installing this app does not register it automatically.

For a new file, use this JSON. If the file already has other servers, add only the `"postgresql"` entry inside its existing `mcpServers` object; preserve every other entry and separate adjacent entries with a comma.

```json
{
  "mcpServers": {
    "postgresql": {
      "type": "stdio",
      "command": "/home/YOUR_USER/postgresql-mcp/app/PostgreSqlMcp"
    }
  }
}
```

Replace `command` with the **absolute executable path**, not the folder or archive:

- Linux: `/home/YOUR_USER/postgresql-mcp/app/PostgreSqlMcp`
- macOS: `/Users/YOUR_USER/postgresql-mcp/app/PostgreSqlMcp`
- Windows JSON: `"C:\\Users\\YOUR_USER\\postgresql-mcp\\app\\PostgreSqlMcp.exe"`

Replace `YOUR_USER` with your home-folder name, or adjust the whole home prefix if yours differs. `echo "$HOME"` on Linux/macOS or `$HOME` in PowerShell shows your home path. JSON does not expand `~`, `$HOME` or `%USERPROFILE%`. **Do not add credentials to this file.** Preserve any intentional `"POSTGRES_ACCESS_MODE": "restricted"` setting when updating an existing entry.

### 2. Prepare the connection environment

Enter one complete **.NET/Npgsql `key=value` connection string** at the hidden prompt below. Your provider's .NET/Npgsql string is accepted; a raw `postgres://` or `postgresql://` URL is not. Replace all `<...>` placeholders, including angle brackets, with your database details. This example is a format guide, **not a command**:

```text
Host=<YOUR_HOST>;Port=5432;Database=<YOUR_DATABASE>;Username=<YOUR_DATABASE_USER>;Password=<YOUR_PASSWORD>;SSL Mode=VerifyFull
```

`Database` is an accessible starting database, such as your existing database or `postgres`; the same login can discover other accessible databases on that server. Use `VerifyFull` for remote TLS. For special characters, provider CA certificates or a local server without TLS, open [connection-string guidance](#connection-string-quoting-and-tls).

**Linux/macOS — paste into your current Bash or Zsh terminal.** No shell change or startup-file edit is needed. Then type or paste the connection string at the hidden prompt and press Enter:

```sh
unset POSTGRES_TARGETS POSTGRES_TARGETS_FILE POSTGRES_CONNECTION_STRING
printf 'PostgreSQL connection string (hidden): ' >&2
IFS= read -r -s POSTGRES_CONNECTION_STRING && export POSTGRES_CONNECTION_STRING
printf '\n' >&2
```

**Windows — use the built-in Windows PowerShell (5.1 or newer) in an interactive terminal:**

```powershell
Remove-Item Env:POSTGRES_TARGETS, Env:POSTGRES_TARGETS_FILE, Env:POSTGRES_CONNECTION_STRING -ErrorAction SilentlyContinue
$connection = Read-Host 'PostgreSQL connection string (hidden)' -AsSecureString
try {
    $env:POSTGRES_CONNECTION_STRING = [System.Net.NetworkCredential]::new('', $connection).Password
} finally {
    $connection.Dispose()
    Remove-Variable connection
}
```

Keep this terminal open. Enter the secret **at the prompt**, never in a command, MCP JSON, chat or shell startup file. Do not print the variable to check it. Hidden entry prevents display/history exposure; the exported environment is still plaintext process memory, inherited by child processes—not encrypted storage.

### 3. Validate, start OMP and ask for databases

**Fully quit any existing OMP process first.** In the same terminal from step 2, run the command for your OS. It checks connectivity and starts a **new** OMP process only if validation succeeds:

**Linux/macOS:**

```sh
"$HOME/postgresql-mcp/app/PostgreSqlMcp" --validate && omp
```

**Windows PowerShell:**

```powershell
& "$HOME\postgresql-mcp\app\PostgreSqlMcp.exe" --validate
if ($LASTEXITCODE -eq 0) {
    omp
} else {
    throw 'Connection validation failed; repeat step 2 or check your database details.'
}
```

Validation is read-only and exits; its successful stderr output includes your database name, so keep it private. Empty input is rejected before OMP starts. If it fails, fix your connection details and repeat step 2; [troubleshooting](#if-setup-does-not-work) covers common failures.

In OMP, ask: **“Use the PostgreSQL MCP server to list the databases I can access, then run `SELECT 1 AS connection_ok` in the starting database using alias `primary`.”** You are ready when it lists real database names and the query returns `1`.

The client must inherit this terminal's environment. A desktop/Start-menu launch, an already-running client, or `/mcp reload` cannot pick up a newly entered secret. For a later session or changed password, fully quit OMP, repeat step 2, and launch it again from that prepared terminal. You do **not** repeat registration or edit a file for new databases. Other MCP clients need their own configuration schema and a launcher that starts a new process with the inherited environment; GUI clients that cannot do this need the [advanced file alternative](#optional-protected-targets-file).

When finished, fully quit OMP/server and clear the terminal's secret with `unset POSTGRES_CONNECTION_STRING` (Bash/Zsh) or `Remove-Item Env:POSTGRES_CONNECTION_STRING -ErrorAction SilentlyContinue` (PowerShell). This does not erase copies in children that are still running.

**Optional help:** Click a section title below to open it.

<details>
<summary>Advanced setup, troubleshooting and legacy profiles</summary>

### Connection-string quoting and TLS

Use the database host and port supplied by your provider, an accessible bootstrap database, and a PostgreSQL login role with CONNECT to the intended databases and only the object privileges it needs. The database username is not necessarily your computer's username.

At the hidden prompt, quoting has **one layer: Npgsql**, not JSON or shell syntax. Quote a value containing a semicolon, for example the synthetic fragment `Password="sample;value"`. Inside a double-quoted value, double a literal double quote (`Password="sample""value"`). Backslashes are entered literally; do not add JSON's `\"` or double backslashes for JSON. Prefer a provider-generated .NET/Npgsql string over constructing one by hand.

If your provider supplies only a PostgreSQL URL, map its host, port, database, username and password to the example's fields, decode URL-escaped values, and preserve required TLS/authentication options. The URL itself cannot be entered unchanged.

For a remote server needing a provider CA certificate, retain `SSL Mode=VerifyFull` and add `Root Certificate=<absolute-path-to-provider-CA-file>`. Follow your provider's [TLS requirements](https://www.npgsql.org/doc/security.html); do not disable TLS to work around a remote certificate error. For PostgreSQL on **this same computer** without TLS, use `Host=127.0.0.1` and replace `SSL Mode=VerifyFull` with `SSL Mode=Disable`.

### Upgrading an existing installation

Automatic discovery and the unrestricted default require **0.3.0 or newer**. This guide targets **0.3.1**, the environment-first patch follow-on. Older 0.2.0 executables use configured database aliases and a restricted default. Changing configuration does not upgrade an old executable; replace the binary with the compatible archive or use a [source build](CONTRIBUTING.md#build-from-source).

**Breaking behavior:** a targets-file entry is now a host/login connection profile, not a database allowlist. The same credentials can select other physical databases on that PostgreSQL server. Omitted access mode means `unrestricted`; `list_databases` returns live databases rather than configured aliases. Preserve an existing explicit `POSTGRES_ACCESS_MODE=restricted` and review PostgreSQL grants before upgrading.

To migrate an existing MCP entry to the environment workflow, remove `POSTGRES_TARGETS`, `POSTGRES_TARGETS_FILE` and literal `POSTGRES_CONNECTION_STRING` from its PostgreSQL `env`, plus `--targets-file`/`--connection-string` arguments. Preserve other nonsensitive settings, especially restricted access mode. Step 2 clears conflicting variables only in the current shell. Combining a connection string with targets JSON/file fails closed. Protected files can remain on disk; do not delete or rename them merely to switch workflows.

### Optional reusable Bash/Zsh prompt in your rc file

This is optional; the quick start needs no rc changes. The **same function works in Bash and Zsh** and validates before a chained launch. Save only the function definition, never a credential or an automatic invocation. Preserve your existing startup configuration.

```sh
postgres_mcp_env() {
  unset POSTGRES_TARGETS POSTGRES_TARGETS_FILE POSTGRES_CONNECTION_STRING
  printf 'PostgreSQL connection string (hidden): ' >&2
  IFS= read -r -s POSTGRES_CONNECTION_STRING && export POSTGRES_CONNECTION_STRING
  printf '\n' >&2
  "$HOME/postgresql-mcp/app/PostgreSqlMcp" --validate
}
```

Adjust the executable path if yours differs. EOF does not export partially entered input; validation rejects missing/empty credentials. Call `postgres_mcp_env && omp` only after fully quitting the existing client and completing registration. Do not call the function in a subprocess and expect it to change the parent environment. It does not modify credential files.

- **Bash:** an interactive non-login shell reads `~/.bashrc`. Login Bash reads the first existing readable file among `~/.bash_profile`, `~/.bash_login`, `~/.profile`, not all three; it does not automatically read `.bashrc`. If needed, let your existing login file conditionally source `.bashrc` for interactive Bash. Do not replace your files or put shell-specific commands into a shared `.profile`. See [Bash startup files](https://www.gnu.org/software/bash/manual/html_node/Bash-Startup-Files.html).
- **Zsh:** use `~/.zshrc`, or `$ZDOTDIR/.zshrc` if customized, for interactive shells. `.zprofile` is login-only; `.zshenv` also runs in noninteractive shells and is not a place for secret prompts. See [Zsh startup files](https://zsh.sourceforge.io/Doc/Release/Files.html).

After reviewing and saving the chosen file, source **only your current shell's rc file**: `source "$HOME/.bashrc"` in Bash or `source "${ZDOTDIR:-$HOME}/.zshrc"` in Zsh. Sourcing executes all commands in the file. No shell-specific prompt option is used.

### Optional protected targets file

Choose this **instead of the connection-string environment** for multiple hosts/logins or GUI/automation launches where session inheritance is impractical. It is not required for one-server discovery. Existing `targets.json`, `targets-0.2.0.json` or other protected filenames remain supported; use the actual path without a rename or per-database entries.

Before switching, fully quit the client/server. Clear `POSTGRES_CONNECTION_STRING` from the launching environment (`unset POSTGRES_CONNECTION_STRING` in Bash/Zsh; `Remove-Item Env:POSTGRES_CONNECTION_STRING -ErrorAction SilentlyContinue` in PowerShell), and remove its client `env` entry and `--connection-string` argument. Clear stale `POSTGRES_TARGETS` too: that JSON source takes precedence over a file. Combining a base string with targets JSON/file is rejected.

For a **new** Linux/macOS file, create private permissions before adding credentials:

```sh
mkdir -p "$HOME/postgresql-mcp"
chmod 700 "$HOME/postgresql-mcp"
touch "$HOME/postgresql-mcp/targets.json"
chmod 600 "$HOME/postgresql-mcp/targets.json"
```

On Windows, restrict the file/folder to your account and administrators with Windows permissions. In Notepad's Save As dialog select **All files** to avoid `targets.json.txt`. Avoid shared/publicly synced folders. OS permissions do not encrypt contents; consider backup/sync exposure and privileged access.

Populate it with a plain-text editor, using the connection-string/TLS guidance above:

```json
{
  "primary": "Host=<YOUR_HOST>;Port=5432;Database=<YOUR_DATABASE>;Username=<YOUR_DATABASE_USER>;Password=<YOUR_PASSWORD>;SSL Mode=VerifyFull"
}
```

A file has **two escaping layers**: Npgsql, then JSON. The synthetic Npgsql fragment `Password="sample;value"` becomes `Password=\"sample;value\"` in a JSON string. Double a literal quote inside an Npgsql quoted value, then escape each quote for JSON; double JSON backslashes too. A JSON-aware editor/serializer helps. No trailing commas are allowed. Never commit the file or paste it into chat/public reports.

For separate hosts/logins, add case-sensitive profile keys such as `reporting`, each with its own bootstrap string—not an entry per database. A profile is **not an allowlist**. Optional `target` selects a profile explicitly; see [selection rules](#database-selection-and-live-discovery).

In the MCP entry, add only `"POSTGRES_TARGETS_FILE"` in `env` with the actual absolute file path, alongside nonsensitive options. Linux: `/home/YOUR_USER/postgresql-mcp/targets.json`; macOS: `/Users/YOUR_USER/postgresql-mcp/targets.json`; Windows JSON: `"C:\\Users\\YOUR_USER\\postgresql-mcp\\targets.json"`. This path points to the credentials file, not MCP JSON. Do not also add a connection string.

Validate from the shell where conflicting variables were cleared:

```sh
"$HOME/postgresql-mcp/app/PostgreSqlMcp" --targets-file "$HOME/postgresql-mcp/targets.json" --validate
```

```powershell
& "$HOME\postgresql-mcp\app\PostgreSqlMcp.exe" --targets-file "$HOME\postgresql-mcp\targets.json" --validate
```

Use your actual paths and keep validation stderr private. Reload the saved MCP configuration (OMP: `/mcp reload`) or fully restart the client. After changing credentials/profiles, restart the server; new databases/grant changes require no edits or restart. The file route avoids relying on a session secret, but conflicting inherited connection strings must still be removed before launching.

### If setup does not work

| What you see | What to check |
|---|---|
| Executable not found / spawn error | Use an absolute executable path including `.exe` on Windows, not an archive/folder; keep extracted files together. |
| Wrong architecture / cannot execute binary | Match the archive to your OS and CPU. |
| Invalid configuration / missing credentials | Repeat the hidden prompt, clear stale credential sources from the shell/client entry, and start a new client from that shell. Raw PostgreSQL URLs are not accepted. For files, check the path, JSON escaping and accidental `.txt` extension. |
| Connection refused, timeout or authentication error | Run `--validate`; check host, port, database, login/password, network/VPN and PostgreSQL access rules. Installing this app does not grant access. |
| Certificate validation error | Use the provider's correct hostname and CA certificate. Do not disable remote TLS. |
| No PostgreSQL tools in OMP | Check MCP configuration location/schema and executable path, then fully quit and launch OMP from the prepared shell. Reload alone cannot supply a missing inherited secret. |
| Invalid target / database missing or denied | `target` must match a configured profile; `database` may be a real name. Check spelling, CONNECT and object privileges. Selection never falls back. |
| Server waits silently without `--validate` | Normal: stdio mode waits for MCP messages. Let the client launch it; use `--validate` for a terminal check. |

Normal stdout is reserved for MCP JSON-RPC; diagnostics use stderr. Keep credentials, SQL and results out of public reports. OMP `timeout` is in milliseconds (`30000` = 30 seconds); `POSTGRES_QUERY_TIMEOUT` is in seconds (`10` = 10 seconds).

### Other installation methods

Choose one installation method and follow the same environment/client setup. **0.3.1 registry publication is blocked:** npm returned HTTP 404 and NuGet rejected the OIDC trust-policy match with HTTP 401. Downloadable npm/NuGet release package files are not registry publication. Use the self-contained archive, a [verified local NuGet package](CONTRIBUTING.md#install-a-verified-release-artifact), or a [source build](CONTRIBUTING.md#build-from-source); do not install an older registry executable for this guide.

For a local .NET tool, installation requires the .NET 10 SDK and running requires the .NET 10 runtime. Put its absolute installed executable path in MCP JSON, not a `.nupkg`; custom runtime locations may need `DOTNET_ROOT`. See CONTRIBUTING for artifact verification and installation commands.

The npm installer requires Node.js 22 or newer and .NET 10 on PATH during installation. It is C# and uses bundled native apphosts, not downloaded runtime/binaries; the installed server runs directly as .NET without a JavaScript launcher or Node child process. Registry commands are not the default setup while publication is unavailable.

For a source build, use `dotnet` as MCP `command` and the absolute path to `PostgreSqlMcp.dll` as the first `args` item. Keep nonsensitive settings and launch the client from the prepared shell. Self-contained builds still need native OS libraries; Debian/Ubuntu GSS/Kerberos may require `libgssapi-krb5-2`.

</details>

<details>
<summary>Tool and database-selection reference</summary>

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

A profile is a case-sensitive name mapped to a bootstrap connection string: `POSTGRES_CONNECTION_STRING` alone creates `primary`, while optional targets JSON/file defines named profiles. The default profile is `primary` when configured, otherwise the ordinal-first profile name.

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

</details>

<details>
<summary>Result format, bounded results and pagination</summary>

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

</details>

<details>
<summary>Security, access and resource boundaries</summary>

## Access and resource boundaries

**Unrestricted mode is the default when access mode is omitted.** Each read-only operation owns a transaction with `SET TRANSACTION READ ONLY`. Client SQL is lexically limited to one statement and cannot issue transaction/session control. The statement-boundary lexer handles comments and PostgreSQL quoting; it is **not a SQL authorization AST**.

For authorized writes, explicitly pass `read_only=false` to `execute_sql`; the PostgreSQL role must also have the necessary privileges. The server commits once on success and rolls back on failure. Default calls remain read-only in unrestricted mode. To refuse writes, opt into `POSTGRES_ACCESS_MODE=restricted` or `--access-mode restricted`; existing explicit restricted settings remain effective. `explain_query` and metadata/operations tools remain read-only; tool annotations advertise potentially destructive SQL execution in unrestricted mode.

Transaction/session controls, COPY, DO, CALL, PREPARE and VACUUM are unsupported in SQL tools; use an administrative client. Routine metadata inspection is supported and functions can be queried with SELECT.

**Never use a superuser role.** Read-only transactions do not sandbox PostgreSQL functions, SECURITY DEFINER routines, foreign servers/dblink, external side effects, session settings or privileged monitoring. Profiles constrain host/authentication/TLS, not which physical databases those credentials can access. PostgreSQL must enforce CONNECT, schema/table/column privileges and RLS; review `PUBLIC` CONNECT grants. Use separate credentials for tenants requiring separate authorization. An optional explicit database allowlist narrows selection but is not database authorization. Treat SQL results as untrusted data, not agent instructions. The server exposes stdio, not an authenticated remote transport.

Session credentials are plaintext environment state readable by sufficiently privileged local processes and inherited by children. They are not automatically persisted, but supervisors, launchers, containers or deliberate persistence can store them. Protected files are persistent plaintext with OS permissions, not encryption. Neither route is inherently safer in every deployment; see [credential storage tradeoffs](SECURITY.md#credentials-and-diagnostics). Hidden PowerShell input uses a SecureString only while reading; conversion to the environment is plaintext.

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

</details>

<details>
<summary>Configuration reference</summary>

## Configuration reference

For one server, use inherited `POSTGRES_CONNECTION_STRING`; it creates the `primary` seed and enables live discovery without targets JSON. Choose a base connection string (optionally with a database allowlist) **or** targets JSON/file. Combining a connection string with either targets source is rejected, not silently prioritized. Within the targets-only route, `POSTGRES_TARGETS` takes precedence over the file. CLI flags override corresponding environment variables, except that `POSTGRES_CONNECTION_STRING` overrides `--connection-string`; avoid CLI secrets because process arguments/history can expose them. Profile/credential changes require a server restart; environment secret changes also require fully restarting the client from the newly prepared shell. Live database/grant changes do not.

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

A base connection string alone creates the `primary` profile and supports live discovery. With an explicit `POSTGRES_DATABASES` JSON array, each allowlisted name becomes a seed alias and replaces any `Database` in the base string; catalog results are filtered to these names and selection of a nonallowlisted physical database is rejected. This allowlist is optional and must be maintained if used. Targets-file aliases are **not** an implicit allowlist. The [optional protected targets file](#optional-protected-targets-file) supports independent credentials/multiple profiles and launch environments where session propagation is impractical; neither method requires putting secrets in process arguments. Environment variables are plaintext inherited state, while files are persistent plaintext protected by OS permissions—choose according to the deployment's exposure and lifecycle, not a blanket safety claim.

Framework-dependent packages require the .NET 10 runtime. Self-contained executables still require native OS libraries. For example, Debian/Ubuntu GSS/Kerberos support uses `libgssapi-krb5-2`; install the platform's appropriate library if that authentication is needed. Password fallback does not verify Kerberos support. The container includes this dependency. See [Npgsql security and encryption](https://www.npgsql.org/doc/security.html).

</details>

## Development and licensing

See [CONTRIBUTING.md](CONTRIBUTING.md) for source builds, container usage, artifact installation, verification and contribution guidelines. The integration fixture uses PostgreSQL 17; this is a verification target, not a guarantee for every PostgreSQL version, platform or MCP client.

The server, npm installer, packaging, verification and release automation are C#/.NET. Workflow badges and [Scorecard](https://scorecard.dev/viewer/?uri=github.com/codegiveness/postgresql-sharp-mcp) report checks and practices, not certifications. [Best Practices](https://www.bestpractices.dev/en/projects/15155) is the saved owner self-assessment and may be in progress. See [Security posture](docs/security-posture.md) for [supply-chain evidence](docs/security-posture.md#supply-chain-evidence), SBOMs and limits, and [SECURITY.md](SECURITY.md) for reporting.

Technical references: [Npgsql data sources](https://www.npgsql.org/doc/basic-usage.html), [pool parameters](https://www.npgsql.org/doc/connection-string-parameters.html), [sequential access](https://www.npgsql.org/doc/performance.html), [C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk), [PostgreSQL EXPLAIN](https://www.postgresql.org/docs/current/sql-explain.html), [read-only transactions](https://www.postgresql.org/docs/current/sql-set-transaction.html), [pg_stat_statements](https://www.postgresql.org/docs/current/pgstatstatements.html) and [HypoPG](https://hypopg.readthedocs.io/en/latest/usage.html).

Project source is MIT, copyright codegiveness. Dependency licenses remain separate: Npgsql uses the PostgreSQL license; the MCP SDK uses Apache-2.0. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and bundled `LICENSES/` for versions, attribution and exact dependency terms.
