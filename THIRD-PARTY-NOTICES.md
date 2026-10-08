# Third-party notices

Project source: MIT, Copyright (c) 2026 codegiveness; see LICENSE.

## Source attribution

- Portions adapted from **codegiveness/mssql-mcp**, MIT, Copyright (c) 2026 codegiveness. https://github.com/codegiveness/mssql-mcp

## Distributed dependencies

- **Npgsql 10.0.3**: PostgreSQL license; Copyright 2025 The Npgsql Development Team. Package repository commit `d3768398c17877b3a916c3c4d87e8e11698991fc`. Exact source license preserved in `LICENSES/Npgsql.txt`. https://github.com/npgsql/npgsql
- **ModelContextProtocol / ModelContextProtocol.Core 2.2.0**: NuGet metadata declares Apache-2.0; copyright Model Context Protocol a Series of LF Projects, LLC. Package repository commit `6fa3825973949a9c4f0cd8af344e15a8db09dc35`. Upstream licensing-transition notice, Apache-2.0 terms, retained MIT terms and documentation attribution notice are preserved in `LICENSES/ModelContextProtocol.txt`. SDK code is linked, not modified. No SDK documentation prose is copied into the project README. https://github.com/modelcontextprotocol/csharp-sdk
- **Microsoft.Extensions.* 10.0.12 and Microsoft.Extensions.AI.Abstractions 10.10.1 dependencies**: MIT, Microsoft Corporation / .NET Foundation and contributors. The AI abstractions package repository commit is `59e1741f6fb45cf7ae08e2df088aef71537a7bc4`. Standard .NET MIT terms are preserved in `LICENSES/DotNet.txt`; the upgraded packages retain their existing MIT licenses. Exact resolved dependency versions are visible in the lockfiles, SDK-generated `.deps.json` and `dotnet list package --include-transitive`. https://github.com/dotnet/runtime and https://github.com/dotnet/extensions
- **.NET 10 runtime**, npm packages and self-contained builds: MIT, .NET Foundation and contributors. License and runtime third-party notices from `dotnet/runtime` tag `v10.0.12` are preserved in `LICENSES/DotNet.txt` and `LICENSES/DotNet-ThirdParty.txt`. https://github.com/dotnet/runtime
- **run-script-os 1.1.6**, npm installation only: MIT, Copyright (c) 2017 Charlie Guse. The unmodified upstream runner and its license are bundled under `installer/run-script-os/` in the npm artifact; it is not part of the server or NuGet tool. The C# builder verifies the archive's pinned SHA-512 before staging it. https://github.com/charlesguse/run-script-os/tree/9d20566670c4df85b6a139ac8dc7fb244c3721c3

NuGet/package/runtime licenses govern their respective components; the project's MIT license does not relicense dependencies. License files are copied to published output and included in the .NET tool package. PostgreSQL, pg_stat_statements and HypoPG run externally and are not bundled into the server application; the integration fixture obtains them in a disposable PostgreSQL Docker container.
