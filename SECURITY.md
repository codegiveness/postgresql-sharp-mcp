# Security

## Trust boundary

This server exposes PostgreSQL operations to an MCP client over stdio. Anyone who can use that client can request queries using the configured database credentials. There is no separate MCP user authentication, per-tool authorization, or tenant identity mechanism. Protect the host process and the client that launches it; do not expose stdio through an unauthenticated network bridge.

Connection profiles are case-sensitive names mapped to bootstrap connection strings; a profile alone is **not a database allowlist** (use the [database lock](#database-lock-hardening) for that). An inherited `POSTGRES_CONNECTION_STRING` alone creates the `primary` profile; protected targets JSON/file remains optional for multiple independent profiles. Tool requests can select a physical PostgreSQL database on a profile's server, but cannot supply a new connection string or change its host, login or TLS settings. Optional `target` selects the profile; without it, exact aliases select their configured bootstrap database and other database names use the default profile (`primary`, otherwise ordinal-first). With `target`, `database` is always a physical name. Unknown profiles, missing databases and denied connections fail without falling back. Each call owns its connection selection; there is no shared current database. Equivalent configurations for the same physical database may share a bounded connection pool.

Live `list_databases` uses the catalog of the profile's bootstrap database (or, under a database lock, the first locked database when the bootstrap database is outside the lock), excluding templates, disabled connections and databases lacking the current role's CONNECT privilege. It exposes accessible database names, not just configured aliases. CONNECT discovery does not prove actual connectivity or schema/table access. Newly created or granted databases are selectable without configuration edits or restarting, whether credentials came from the session environment or a protected file. Review grants, including `PUBLIC` CONNECT, before upgrading from alias-only selection. A `POSTGRES_DATABASES`/`--databases` database lock filters listing and rejects every other physical name in both connection modes.

## PostgreSQL permissions are the authority

Use dedicated, least-privileged PostgreSQL roles, not superusers or database owners. Grant only the required database connection, schema usage, table access, and routine execution privileges. Configure row-level security and separate roles where required by the application's data model. Review role memberships, default privileges, `PUBLIC` grants, `SECURITY DEFINER` routines, extensions, foreign servers, and monitoring privileges.

**Unrestricted access is the default when access mode is omitted.** SQL requests still default to `read_only=true`; read-only operations use a server-owned read-only transaction and roll it back after reading the result. Writes require `read_only=false` on `execute_sql`, unrestricted mode and sufficient PostgreSQL privileges. Opt into `POSTGRES_ACCESS_MODE=restricted` or `--access-mode restricted` to refuse write requests; existing explicit restricted settings remain effective. Enabled writes use a transaction that commits after successful execution and result reading; failures dispose the transaction without committing. Other tools remain read-only. Connection loss or cancellation during commit can leave the caller uncertain whether a write committed; inspect database state before retrying. The server does not automatically replay writes.

The SQL guard is a statement-boundary lexer, **not an authorization parser or SQL sandbox**. It accepts one statement, handles quoted SQL and comments, and rejects direct transaction/session control plus unsupported operations such as `COPY`, `DO`, `CALL`, and `VACUUM`. PostgreSQL transactions and role permissions enforce the actual access restrictions.

Read-only transactions do not make arbitrary SQL harmless. Queries can execute functions, change session settings through functions, use advisory locks, access temporary objects, consume database resources, or cause external effects through installed extensions and privileged routines. `EXPLAIN ANALYZE` executes the query. Neither transaction rollback nor connection reset can undo external effects. Restrict these capabilities in PostgreSQL. HypoPG evaluation uses installed extension functions and connection-local hypothetical indexes; it does not grant permission to create permanent indexes or install extensions.

## Database lock hardening

Run one server instance per PostgreSQL server and give each instance a **database lock**: `POSTGRES_DATABASES` (or `--databases`) as a JSON array of exact, case-sensitive physical database names. The lock is a separate setting on top of either connection mode; connection-string and targets JSON/file formats are unchanged.

### What the server enforces

- Every path that opens a connection resolves to a physical database first and must name a locked database: every tool's `database`, alias resolution, explicit `target`, `list_databases`, `--validate` and the startup check. The data-source cache re-checks the lock before creating any pool, so no pool for another database can exist.
- **Connection-string mode:** each locked database becomes a profile whose alias is its name; the connection string's own `Database` is not used.
- **Targets mode:** the lock applies to every profile. A profile still supplies host, login and TLS settings, but its bootstrap database is used only when it is locked. Passing the alias of a profile whose bootstrap database is outside the lock as `database` is rejected, while `target` with that profile and a locked `database` works. `list_databases`, `--validate` and the startup check connect through the profile's bootstrap database when locked, otherwise through the first database in `POSTGRES_DATABASES` order; they never open an out-of-lock bootstrap database.
- Rejections return `invalid_target` with a message naming the database lock, before any connection is attempted.
- `POSTGRES_REQUIRE_DATABASE_LOCK=true` (or `--require-database-lock`) makes a missing lock a startup error. It is off by default; without a lock, behavior is unchanged.

### What startup verifies in PostgreSQL

When a lock is configured, normal startup and `--validate` connect only through locked databases, once per distinct login/server profile, and refuse to start (exit code 1, findings on stderr) if any of these hold. Messages name profiles, roles and databases, never connection strings or credentials.

| Finding | Why it fails |
|---|---|
| The login can connect (`has_database_privilege(..., 'CONNECT')`, including `PUBLIC` and inherited grants, on any database with `datallowconn`) to a database outside the lock | PostgreSQL would accept that connection from any other client using the same credentials; the lock would be the only barrier. |
| The login is a superuser or a member of a superuser role | Superusers bypass every privilege check, including CONNECT grants, and can reach other databases through server-side features; membership allows `SET ROLE` to one. |
| Membership in `pg_read_server_files`, `pg_write_server_files` or `pg_execute_server_program` | Server file access and `COPY ... PROGRAM` reach other databases' data files or open new local connections outside PostgreSQL's database grants. |
| `CREATEROLE` on the login or a role it is a member of | It can create roles and grant memberships, so the verified grants need not hold one statement later. |
| `CREATEDB` on the login or a role it is a member of | It can create databases outside the lock (and drop ones it then owns), changing the set of databases the check verified. |
| The login (or a role it is a member of) owns a database outside the lock | Owners hold implicit CONNECT and can `ALTER`/`DROP DATABASE` from a connection to any other database, without connecting to it. |
| Explicit `EXECUTE` on a server-file function (`pg_read_file`, `pg_read_binary_file`, `pg_ls_dir`, server-side `lo_import`/`lo_export`, adminpack `pg_file_write`) in a locked database the login can connect to | PostgreSQL revokes these from `PUBLIC`, but a direct grant needs no predefined-role membership. `pg_read_binary_file` can then read any file in the data directory, including other databases' relation files, so the role reads protected data without connecting to it. Function grants are per database, so every reachable locked database is checked. |
| `dblink` or `postgres_fdw` installed, or any foreign server defined, in a locked database the login can connect to | They open new connections from inside PostgreSQL, bypassing the server's connection-level lock. Installation is not checked as availability: both extensions are untrusted, so only a superuser can install them, which is already refused. |

Deliberately not refused: `REPLICATION` (the server never opens replication-protocol connections and logical decoding SQL functions decode only the current database), `BYPASSRLS` (affects rows only inside locked databases), and monitoring roles such as `pg_read_all_stats` (they expose names and activity, see below, not data). Review them anyway.

The check is a startup snapshot. Grants, role attributes, extensions or ownership changed after startup are not re-verified until the next start or `--validate`; the in-process lock still rejects other databases. Because CONNECT is evaluated from grants, a role that is confined only by `pg_hba.conf` rules is refused: revoke the grants too. Unreachable or unauthenticated profiles also refuse startup, because the lock cannot be verified.

### Hardening the role

Run as a superuser or the database owners, once per protected and locked database (replace placeholders):

```sql
-- Remove the default PUBLIC CONNECT from every database the MCP role must not reach,
-- including postgres and template1; grant it back explicitly to roles that need it.
REVOKE CONNECT ON DATABASE postgres FROM PUBLIC;
REVOKE CONNECT ON DATABASE template1 FROM PUBLIC;
REVOKE CONNECT ON DATABASE <protected_db> FROM PUBLIC;
GRANT CONNECT ON DATABASE postgres, <protected_db> TO <application_role>;

-- Locked databases: CONNECT for the MCP role only.
REVOKE CONNECT ON DATABASE <locked_db> FROM PUBLIC;
GRANT CONNECT ON DATABASE <locked_db> TO <mcp_role>;

ALTER ROLE <mcp_role> NOSUPERUSER NOCREATEDB NOCREATEROLE;
-- Remove memberships in superuser roles and pg_*_server_files/pg_execute_server_program;
-- In each locked database, revoke direct grants such as:
-- REVOKE EXECUTE ON FUNCTION pg_catalog.pg_read_binary_file(text) FROM <mcp_role>;
-- transfer ownership of out-of-lock databases away from the MCP role.
```

Add `pg_hba.conf` rules so the server also rejects the role at authentication time. Rules match top to bottom, so place them before broader entries:

```text
# TYPE   DATABASE           USER         ADDRESS          METHOD
hostssl  <locked_db>        <mcp_role>   <client_cidr>    scram-sha-256
host     all                <mcp_role>   all              reject
local    all                <mcp_role>                    reject
```

Do not install `dblink` or `postgres_fdw` and do not define foreign servers or user mappings in locked databases. If another application needs them, give it a separate database outside this role's lock. The server does not add SQL-guard rules for these features; PostgreSQL permissions remain the authority.

Database **names** remain visible: shared catalogs such as `pg_database`, `pg_shdescription`, `pg_stat_database` and `pg_stat_activity` (database and, with monitoring roles, other sessions' details) are readable from any locked database. `list_databases` filters by the lock, but `execute_sql` can query these catalogs directly. Treat database names as disclosed; the lock protects contents and connections, not names.

### One instance per PostgreSQL server

Each instance gets its own credentials source and lock. Keep credentials in each instance's owner-protected targets file or inherited environment, never in MCP JSON:

```json
{
  "mcpServers": {
    "postgresql-server-a": {
      "type": "stdio",
      "command": "postgresql-sharp-mcp",
      "env": {
        "POSTGRES_TARGETS_FILE": "/home/<user>/postgresql-mcp/server-a-targets.json",
        "POSTGRES_DATABASES": "[\"<server_a_db_1>\",\"<server_a_db_2>\"]",
        "POSTGRES_REQUIRE_DATABASE_LOCK": "true"
      }
    },
    "postgresql-server-b": {
      "type": "stdio",
      "command": "postgresql-sharp-mcp",
      "env": {
        "POSTGRES_TARGETS_FILE": "/home/<user>/postgresql-mcp/server-b-targets.json",
        "POSTGRES_DATABASES": "[\"<server_b_db>\"]",
        "POSTGRES_REQUIRE_DATABASE_LOCK": "true"
      }
    }
  }
}
```

Each targets file stays in its existing format, for example `{"primary":"Host=<server_a_host>;Port=5432;Username=<mcp_role>;Password=<password>;Database=<server_a_db_1>;SSL Mode=VerifyFull"}`. Run the same command with `--validate` and the same environment before starting the client; it exits non-zero with the findings above if PostgreSQL does not enforce the lock.

## Credentials and diagnostics

The single-server quickstart uses masked Bash/Zsh/PowerShell entry into the process environment, not a credentials-bearing MCP JSON entry. Masking prevents terminal echo; the resulting value is still **plaintext process state**, inherited by child processes and inspectable by sufficiently privileged local processes. Process-level environment assignment does not automatically persist across sessions, but launchers, supervisors, containers or explicit persistence can retain it. Fully quit the client/server after changing a session credential, then launch the client from the same newly prepared shell. An already running client, a desktop launch or `/mcp reload` alone cannot acquire a changed parent-shell environment. Unsetting a parent variable does not erase copies already inherited by running children or deliberately persisted elsewhere.

An optional owner-protected targets file remains useful for multiple profiles, automation and clients where session propagation is impractical. It is persistent plaintext, protected by OS permissions rather than encryption; restrict file/folder access to the server's operating-system user and consider administrator, backup and sync exposure. A session environment is not inherently safer than an owner-only file: choose based on exposure, inheritance and lifecycle requirements. Keep existing protected file names if desired; no automatic local credential migration is performed.

Never put real credentials in committed files, SQL, tool arguments, profile aliases, literal CLI commands, shell history, shell startup profiles/rc files, `setx` or MCP JSON. A reusable rc function may contain only the masked-prompt code, invoked explicitly in the launching shell—not a literal secret or automatic plaintext export. CLI connection strings can be exposed in process arguments/history. Combining a connection string with targets JSON/file is rejected; when switching workflows, clear stale variables and remove conflicting client `env`/launch arguments rather than expecting secret precedence. Restart the server after changing file-based profiles/credentials; live database creation and grant/revoke changes need no recurring JSON edits.

The server does not return configured connection strings through database discovery. Live discovery can reveal sensitive database names; protect its results like other metadata. Configuration-parser failures use fixed diagnostics rather than forwarding parser or file-access exception messages. Unrecognized CLI arguments are not echoed. Missing or duplicate recognized options can identify the supported option name.

Npgsql error details and parameter logging are disabled by server-owned connection settings. PostgreSQL `MessageText` and `Hint` can contain query literals, data values, or arbitrary routine-generated text even when error details are disabled. Tool errors therefore retain SQLSTATE and a server-defined summary but do not forward these PostgreSQL fields. Connection and unexpected failures also use fixed summaries. Investigate detailed database errors using an appropriately protected PostgreSQL diagnostic channel.

MCP SDK and Npgsql logger categories are disabled, including when the configured log level is `debug` or `trace`, because SDK transport logging can include complete requests and responses. Remaining host diagnostics go to stderr; stdout is reserved for MCP JSON-RPC. The log-level setting does not enable SDK wire tracing.

These protections are not content redaction for successful results. Queries, plans, object definitions, statistics, workload text, and metadata can contain sensitive information that the configured role is allowed to read. `--validate` writes the actual database name and a bounded result or safe error to stderr. An MCP client, process supervisor, PostgreSQL server, proxy, or external diagnostic tool can independently record sensitive inputs and outputs. Protect those channels as well.

## Connection and resource settings

TLS and certificate trust are operator-controlled Npgsql connection-string settings. The server does not force TLS or verify certificates on the operator's behalf. For remote production connections, configure certificate validation appropriate to the deployment, such as `SSL Mode=VerifyFull`, and provision the necessary trust material. Use network controls to restrict database reachability.

The server owns pool bounds, reset-on-close, transaction enlistment, multiplexing, application name, command timeouts, and logging safety settings. Configuration with `No Reset On Close=true` or multiplexing is rejected. Operation deadlines include queue and connection-pool wait; transactions also set statement and lock timeouts. Result row, byte, cell, and column limits bound responses. These are resource controls, not a guarantee against expensive queries, external side effects, or denial of service. Apply PostgreSQL and operating-system resource controls where needed.

Pools are keyed by normalized connection settings, including credentials/TLS and physical database. The runtime cache holds at most `floor(256 / POSTGRES_POOL_SIZE)` pool entries, each with that configured connection maximum. Idle least-recently-used entries are disposed when capacity is needed; active entries are not evicted. At full active capacity, calls wait within their existing operation deadline. Failed selections do not accumulate pool entries. These bounds prevent unbounded per-database pool growth, not privileged cross-database access.

## Reporting a vulnerability

Do not post credentials, private data, or an exploit against an operational database in a public issue. Private vulnerability reporting is enabled for this repository: open [Security and quality](https://github.com/codegiveness/postgresql-sharp-mcp/security), then **Report a vulnerability**, following [GitHub's private-report instructions](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/report-privately). No particular response time is guaranteed. If the private route becomes unavailable, open a minimal public issue asking the maintainer for a confidential contact route, without sensitive details.

A useful report includes the affected version or commit, operating system and PostgreSQL version, configuration with secrets removed, the security boundary crossed, and a minimal reproduction using disposable data. Do not test systems without authorization. This document describes implementation protections and limitations; it is not an independent security audit or certification.
