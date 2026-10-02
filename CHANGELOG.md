# Changelog

## 0.1.0

- PostgreSQL-specific C#/.NET 10 MCP server using Npgsql 10.0.3 and official MCP SDK 2.2.0.
- One nine-tool stdio surface with mandatory per-operation target aliases; allowlisted connection credentials, no mutable current database or fallback.
- Lazy bounded Npgsql pools, duplicate-alias reuse, operation concurrency/timeouts, idle pruning and shutdown disposal.
- Compact columns/row arrays, server-side read-only pagination, byte/cell bounds with explicit continuation/clipping, focused schema/object sections.
- Actionable scalar-conversion errors for calendar-month intervals and oversized numeric values, with SQL projection guidance.
- Restricted transactions by default; explicit unrestricted writes with committed DML/DDL and no replay pagination.
- Native plans/ANALYZE, optional same-session HypoPG what-if comparison/cleanup, database-filtered pg_stat_statements, index evidence and focused health checks.
- .NET tool/container/self-contained packaging, real two-database MCP integration verification, GitHub CI/release workflows and configuration documentation.
