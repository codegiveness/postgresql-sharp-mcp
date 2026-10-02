# Security

## Trust boundary

This server exposes PostgreSQL operations to an MCP client over stdio. Anyone who can use that client can request queries using the configured database credentials. There is no separate MCP user authentication, per-tool authorization, or tenant identity mechanism. Protect the host process and the client that launches it; do not expose stdio through an unauthenticated network bridge.

Targets are an explicit, case-sensitive allowlist of aliases mapped to connection strings. Tool requests cannot supply a new connection string or change a target's host, database, or login. Unknown targets fail without falling back to another database. Aliases that share the same effective connection configuration may share a connection pool. Separate tenant credentials and PostgreSQL permissions are necessary when tenant isolation matters; the alias allowlist is not a replacement for database authorization.

## PostgreSQL permissions are the authority

Use dedicated, least-privileged PostgreSQL roles, not superusers or database owners. Grant only the required database connection, schema usage, table access, and routine execution privileges. Configure row-level security and separate roles where required by the application's data model. Review role memberships, default privileges, `PUBLIC` grants, `SECURITY DEFINER` routines, extensions, foreign servers, and monitoring privileges.

Restricted access is the default. Each database operation starts a server-owned read-only transaction and rolls it back after reading its result. Writes require both server access mode `unrestricted` and `read_only=false` on `execute_sql`. These writes use a transaction that commits after successful execution and result reading; failures dispose the transaction without committing. Other tools remain read-only. Connection loss or cancellation during commit can leave the caller uncertain whether a write committed; inspect database state before retrying. The server does not automatically replay writes.

The SQL guard is a statement-boundary lexer, **not an authorization parser or SQL sandbox**. It accepts one statement, handles quoted SQL and comments, and rejects direct transaction/session control plus unsupported operations such as `COPY`, `DO`, `CALL`, and `VACUUM`. PostgreSQL transactions and role permissions enforce the actual access restrictions.

Read-only transactions do not make arbitrary SQL harmless. Queries can execute functions, change session settings through functions, use advisory locks, access temporary objects, consume database resources, or cause external effects through installed extensions and privileged routines. `EXPLAIN ANALYZE` executes the query. Neither transaction rollback nor connection reset can undo external effects. Restrict these capabilities in PostgreSQL. HypoPG evaluation uses installed extension functions and connection-local hypothetical indexes; it does not grant permission to create permanent indexes or install extensions.

## Credentials and diagnostics

Keep credentials in protected local configuration files or the process environment, never in committed files, SQL, tool arguments, or target aliases. Restrict configuration file access to the server's operating-system user. Environment variables and command-line arguments can be visible to sufficiently privileged local processes; CLI connection strings can also enter shell history. Restart the server after changing its target configuration or credentials.

The server does not return configured connection strings through target discovery. Configuration-parser failures use fixed diagnostics rather than forwarding parser or file-access exception messages. Unrecognized CLI arguments are not echoed. Missing or duplicate recognized options can identify the supported option name.

Npgsql error details and parameter logging are disabled by server-owned connection settings. PostgreSQL `MessageText` and `Hint` can contain query literals, data values, or arbitrary routine-generated text even when error details are disabled. Tool errors therefore retain SQLSTATE and a server-defined summary but do not forward these PostgreSQL fields. Connection and unexpected failures also use fixed summaries. Investigate detailed database errors using an appropriately protected PostgreSQL diagnostic channel.

MCP SDK and Npgsql logger categories are disabled, including when the configured log level is `debug` or `trace`, because SDK transport logging can include complete requests and responses. Remaining host diagnostics go to stderr; stdout is reserved for MCP JSON-RPC. The log-level setting does not enable SDK wire tracing.

These protections are not content redaction for successful results. Queries, plans, object definitions, statistics, workload text, and metadata can contain sensitive information that the configured role is allowed to read. `--validate` writes the actual database name and a bounded result or safe error to stderr. An MCP client, process supervisor, PostgreSQL server, proxy, or external diagnostic tool can independently record sensitive inputs and outputs. Protect those channels as well.

## Connection and resource settings

TLS and certificate trust are operator-controlled Npgsql connection-string settings. The server does not force TLS or verify certificates on the operator's behalf. For remote production connections, configure certificate validation appropriate to the deployment, such as `SSL Mode=VerifyFull`, and provision the necessary trust material. Use network controls to restrict database reachability.

The server owns pool bounds, reset-on-close, transaction enlistment, multiplexing, application name, command timeouts, and logging safety settings. Configuration with `No Reset On Close=true` or multiplexing is rejected. Operation deadlines include queue and connection-pool wait; transactions also set statement and lock timeouts. Result row, byte, cell, and column limits bound responses. These are resource controls, not a guarantee against expensive queries, external side effects, or denial of service. Apply PostgreSQL and operating-system resource controls where needed.

## Reporting a vulnerability

Do not post credentials, private data, or an exploit against an operational database in a public issue. Private vulnerability reporting is enabled for this repository: open [Security and quality](https://github.com/codegiveness/postgresql-sharp-mcp/security), then **Report a vulnerability**, following [GitHub's private-report instructions](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/report-privately). No particular response time is guaranteed. If the private route becomes unavailable, open a minimal public issue asking the maintainer for a confidential contact route, without sensitive details.

A useful report includes the affected version or commit, operating system and PostgreSQL version, configuration with secrets removed, the security boundary crossed, and a minimal reproduction using disposable data. Do not test systems without authorization. This document describes implementation protections and limitations; it is not an independent security audit or certification.
