# Third-party notices

Project source: MIT, Copyright (c) 2026 codegiveness; see LICENSE.

## Source attribution

- Portions adapted from **codegiveness/mssql-mcp**, MIT, Copyright (c) 2026 codegiveness. https://github.com/codegiveness/mssql-mcp

## Distributed dependencies

- **Npgsql 10.0.3**: PostgreSQL license; Copyright 2025 The Npgsql Development Team. Package repository commit `d3768398c17877b3a916c3c4d87e8e11698991fc`. Exact source license preserved in `LICENSES/Npgsql.txt`. https://github.com/npgsql/npgsql
- **ModelContextProtocol / ModelContextProtocol.Core 2.2.0**: NuGet metadata declares Apache-2.0; copyright Model Context Protocol a Series of LF Projects, LLC. Package repository commit `6fa3825973949a9c4f0cd8af344e15a8db09dc35`. Upstream licensing-transition notice, Apache-2.0 terms, retained MIT terms and documentation attribution notice are preserved in `LICENSES/ModelContextProtocol.txt`. SDK code is linked, not modified. No SDK documentation prose is copied into the project README. https://github.com/modelcontextprotocol/csharp-sdk
- **Microsoft.Extensions.* and Microsoft.Extensions.AI.* dependencies**: MIT, Microsoft Corporation / .NET Foundation and contributors. Standard .NET MIT terms in `LICENSES/DotNet.txt`. Exact resolved dependency versions are visible in the SDK-generated `.deps.json` and `dotnet list package --include-transitive`. https://github.com/dotnet/runtime and https://github.com/dotnet/extensions
- **.NET 10 runtime**, self-contained builds only: MIT, .NET Foundation and contributors. License and runtime third-party notices from `dotnet/runtime` tag `v10.0.12` are preserved in `LICENSES/DotNet.txt` and `LICENSES/DotNet-ThirdParty.txt`. https://github.com/dotnet/runtime

NuGet/package/runtime licenses govern their respective components; the project's MIT license does not relicense dependencies. License files are copied to published output and included in the .NET tool package. PostgreSQL, pg_stat_statements and HypoPG run externally and are not bundled into the server application; the integration fixture obtains them in a disposable PostgreSQL Docker container.
