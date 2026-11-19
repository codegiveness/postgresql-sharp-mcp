# 0003: Beginner-friendly defaults: `SSL Mode=Prefer` and an advisory database lock

## Status

Accepted

## Context

0.6.0 made TLS verified by default (`VerifyFull` for non-loopback hosts, `Disable` for loopback) and 0.4.0 made the database lock refuse startup unless PostgreSQL grants matched ([CHANGELOG 0.6.0 and 0.4.0](../../CHANGELOG.md)). A plain connection string to a typical PostgreSQL server therefore failed with `tls_verification_failed`, `tls_unavailable` or a lock refusal before the user saw a working server. See [CONTEXT.md](../../CONTEXT.md) for **TLS default**, **Lock mode** and **Database lock**.

## Decision

Release 0.7.0 loosens both defaults, as a deliberate breaking, security-relevant change:

- A connection string without `SSL Mode` gets `SSL Mode=Prefer` for every host, loopback included: TLS when offered, plaintext otherwise, certificate never verified. A string with `Root Certificate` and no `SSL Mode` still gets `VerifyFull`; an explicit `SSL Mode` is used unchanged. There is deliberately no `POSTGRES_TLS_MODE` setting. A non-loopback host without `VerifyCA`/`VerifyFull` gets a non-blocking `[warning]`.
- The database lock's PostgreSQL grant check is `advisory` by default (new `POSTGRES_DATABASE_LOCK_MODE` / `--database-lock-mode`): findings are `[warning] database lock (advisory)` lines and startup continues. `strict` restores the 0.6.0 refusal. The lock itself is still enforced by the server's tools on every path in both modes.

## Alternatives considered

- **Keep verified-by-default**: rejected by the 0.7.0 change because a plain connection string did not work.
- **A `POSTGRES_TLS_MODE` setting**: rejected; the connection string's `SSL Mode` is the single place TLS is configured and profiles may differ (CHANGELOG 0.7.0).
- **Remove the lock check**: rejected; `strict` keeps it as an opt-in and advisory mode still reports findings.

## Consequences

- On an untrusted network an on-path attacker can read or impersonate an unverified `Prefer` connection; in advisory mode PostgreSQL does not confine the login to `POSTGRES_DATABASES`. The README "Hardening for advanced users" and SECURITY.md describe how to restore both.
- The OpenSSF Best Practices answers `crypto_certificate_verification`, `crypto_verification_private` and `crypto_used_network` changed from Met to Unmet ([CHANGELOG 0.7.1](../../CHANGELOG.md), commit `b5aa810`).
- 0.7.2 adds no database-access behavior change.

## Evidence

- Commit `e2561b2` (2026-10-11): "feat!: beginner-friendly defaults: SSL Mode=Prefer and advisory database lock (0.7.0) (#70)".
- [`TlsPolicy.cs`](../../src/PostgreSqlMcp.Core/TlsPolicy.cs) (omitted `SSL Mode` becomes `Prefer`, or `VerifyFull` with `Root Certificate`); `StrictDatabaseLock` in [`ServerOptions.cs`](../../src/PostgreSqlMcp.Core/ServerOptions.cs).
- [CHANGELOG 0.7.0](../../CHANGELOG.md) upgrade note and [docs/architecture.md](../architecture.md).
