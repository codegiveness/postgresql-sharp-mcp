# postgresql-sharp-mcp

A [Model Context Protocol](https://modelcontextprotocol.io/) server for PostgreSQL, written in C#/.NET with Npgsql. This file is the shared vocabulary of the repository: use the preferred term in code, documentation, issues and pull requests. Every definition below comes from [README.md](README.md), [docs/architecture.md](docs/architecture.md), [SECURITY.md](SECURITY.md) or the source files named in it; when behavior changes, change the entry in the same pull request.

## Language

**Profile** (also called **target**):
One named bootstrap connection: a host, login and TLS configuration. A base `POSTGRES_CONNECTION_STRING` creates the profile `primary`; a targets JSON/file defines up to 32 profiles by alias. A profile is a connection profile, not a database registration and not an allowlist: the same credentials can select other physical databases on that server. The tool argument that selects one is `target`.
_Avoid_: connection (ambiguous with a pooled physical connection), server entry, per-database entry

**Alias**:
The case-sensitive name of a profile (nonblank, 128 characters or fewer, no control characters). With a database lock on a connection string, each locked database name also becomes a seed alias of the one login profile.
_Avoid_: profile id, nickname

**Physical database**:
A real PostgreSQL database chosen per call with the `database` argument. Each call resolves its database independently: there is no shared current database and no fallback after a failed selection.
_Avoid_: current database, default database (the `Database` in a connection string is only the bootstrap database)

**Discovery**:
Live, on-demand listing of the databases the login can CONNECT to (`list_databases`), without a configured per-database list and without a result cache. Discovery is filtered by role privileges and, with a database lock, by the locked names.
_Avoid_: registration, auto-registration, database list (when meaning configuration)

**Database lock**:
The optional `POSTGRES_DATABASES` list of unique physical database names. This server's tools always enforce it in C# on every path: `invalid_target` before connecting, `list_databases` listing only the locked names, no session on an out-of-lock database. It is not PostgreSQL authorization by itself.
_Avoid_: allowlist (a profile is not an allowlist), database whitelist, restriction

**Lock mode**:
`POSTGRES_DATABASE_LOCK_MODE`, which decides what the startup/`--validate` PostgreSQL grant check does. **Advisory** (default since 0.7.0): findings print as `[warning] database lock (advisory)` lines and startup continues; PostgreSQL does not confine the login. **Strict**: any finding, or a check that cannot connect, refuses startup; PostgreSQL grants were verified to confine the login. `strict` without a lock is a startup error.
_Avoid_: lock level, enforced/unenforced lock, hard lock, soft lock

**Access mode**:
`POSTGRES_ACCESS_MODE`. **Unrestricted** (default when omitted): read-only transactions by default, and `execute_sql` accepts an explicit `read_only=false` write that commits once. **Restricted**: writes are refused. Neither mode makes PostgreSQL permissions unnecessary; the role must still hold the privileges.
_Avoid_: read-only mode (the unrestricted default is also read-only per call), safe mode, permission level, admin mode

**SQL guard**:
`SqlGuard`, a statement-boundary lexer. It accepts exactly one statement, understands comments, quoted text and dollar quoting, and rejects transaction/session control and some unsupported commands. It is not an authorization parser or a sandbox; PostgreSQL read-only transactions and the configured role enforce permissions.
_Avoid_: validator, SQL firewall, authorization layer, SQL parser, AST allowlist

**Statement boundary**:
The point where the SQL guard decides a statement ends (a terminating `;` followed by anything but comments and whitespace is a second statement and is rejected). Parameters (`$1..$n`) are bound as values and never interpolated into SQL.
_Avoid_: statement splitting, query separation

**Page**:
One row- and byte-bounded result of `execute_sql`. The next page is requested with `next_offset`; pages re-execute the SQL and share no snapshot, so a stable unique `ORDER BY` or keyset SQL is needed. Writes are never paginated or replayed.
_Avoid_: batch, chunk, cursor (no server-side cursor is held)

**Truncation**:
Row-level cutoff of a page, reported as `truncation_reason` `row_limit` or `byte_limit`; the next offset advances by returned rows.
_Avoid_: clipping (that is the cell-level concept below), cap, limit hit

**Clipping**:
Cell-level shortening of a single value to `POSTGRES_MAX_CELL_CHARS` (or the equivalent base64 budget for binary), reported in `clipped_cells` as zero-based row/column coordinates. Distinct from truncation: clipping shortens a value, truncation drops rows.
_Avoid_: truncation (for cells), trimming, cutting

**TLS default**:
A connection string without `SSL Mode` gets `SSL Mode=Prefer` for every host, loopback included: TLS when the server offers it, plaintext otherwise, certificate never verified; a non-loopback host gets a `[warning]`. Exception: a string with `Root Certificate` and no `SSL Mode` gets `VerifyFull`. An explicit `SSL Mode` is used unchanged. There is deliberately no `POSTGRES_TLS_MODE` setting. Applied by `TlsPolicy`.
_Avoid_: TLS on/off setting, encrypted by default, secure by default

**Bootstrap connection**:
The connection a profile uses to discover databases and to run the startup checks; its `Database` is only a starting point (defaults to `postgres` in connection-string mode only; targets profiles must name one).
_Avoid_: main database, home database

**Validate**:
`--validate`: a read-only connectivity check plus the privilege, TLS and database-lock warnings. It proves the connection works; it is not a proof of safe configuration. Its output names the database, so keep it private.
_Avoid_: health check, smoke test, self-test

**Tool**:
One of the nine MCP tools the server registers (`list_databases`, `list_schemas`, `list_objects`, `get_object_details`, `execute_sql`, `explain_query`, `analyze_db_health`, `get_top_queries`, `analyze_indexes`). Do not confuse with the C# build/verify programs under `tools/`, which this repository calls **tool projects**.
_Avoid_: function, endpoint, command (for MCP tools)

**Tool project**:
A C# program under `tools/` that builds, verifies, releases or fuzzes the app (`PostgreSqlMcp.Build`, `.Verify`, `.Release`, `.Fuzz`, `.NpmInstall`). All maintained automation is C#/.NET.
_Avoid_: script, helper script

**Verifier**:
`tools/PostgreSqlMcp.Verify`, the command-line program that runs integration, package, SARIF, release and coverage checks (`dotnet run --project tools/PostgreSqlMcp.Verify -c Release -- <command>`).
_Avoid_: test runner, test suite (the verifier is not the unit-test project)

**Disposable fixture**:
The Docker PostgreSQL container the verifier creates and removes for integration runs. Only databases you own or are explicitly authorized to use are ever tested.
_Avoid_: test database, staging database

**Raw-SARIF gate**:
CodeQL's pull-request check: baseline and candidate are scanned with the same scanner and query setup, raw SARIF is kept, and a comparator built from trusted base source rejects new high/critical or error-level findings. Scan or extraction errors and missing reports fail closed; they are never a clean result.
_Avoid_: CodeQL status check, alert gate

**Release dispatch**:
Publication starts only when a maintainer manually dispatches `release.yml` from `main` for an existing `v<version>` tag reachable from `origin/main`. Pushing a tag publishes nothing. npm is staged and needs a separate Windows 2FA approval.
_Avoid_: release on tag, auto-release, publish

## Relationships

- A **target** (profile) is selected per call by `target`; a **physical database** is selected per call by `database`; the **database lock** narrows which physical databases may ever be opened, and its **lock mode** decides whether PostgreSQL grants must confine the login.
- The **access mode** decides whether writes are possible; the **SQL guard** decides whether one statement is well-formed; PostgreSQL decides what the login may do.
- A **page** can be **truncated** (rows) and have **clipped** cells (values); the two are reported separately.
- Dependency direction is `Core <- Tools <- App` ([docs/architecture.md](docs/architecture.md)).

## Flagged ambiguities

- "Tool" means an MCP tool in prose about the server and a **tool project** when it names something under `tools/`; say which.
- "Read-only" describes a per-call transaction, not a server mode: the default unrestricted access mode is read-only per call and still permits an explicit write.
- "Target" is the argument name and "profile" the concept; both name the same thing, and neither means a database.
- "Strict" and "advisory" apply only to the database lock mode, not to TLS or access mode.
