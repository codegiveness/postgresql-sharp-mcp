FROM mcr.microsoft.com/dotnet/sdk:11.0@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props postgresql-sharp-mcp.slnx ./
COPY src/ ./src/
COPY README.md SECURITY.md CONTRIBUTING.md LICENSE THIRD-PARTY-NOTICES.md ./
COPY LICENSES/ ./LICENSES/
RUN dotnet restore src/PostgreSqlMcp/PostgreSqlMcp.csproj --locked-mode \
    && dotnet publish src/PostgreSqlMcp/PostgreSqlMcp.csproj -c Release --no-restore --no-self-contained -o /app

FROM mcr.microsoft.com/dotnet/runtime:11.0@sha256:3fe5f5495141c97986a49d3a4e76061f6225c3f93f94e8a7e522781376193863
LABEL org.opencontainers.image.title="postgresql-sharp-mcp" \
      org.opencontainers.image.description="Database-agnostic PostgreSQL MCP server with live discovery and bounded per-call database selection using Npgsql." \
      org.opencontainers.image.source="https://github.com/codegiveness/postgresql-sharp-mcp" \
      org.opencontainers.image.licenses="MIT"
# Native GSS for Npgsql, plus OpenSSL security updates absent from the pinned base. The base image also ships Canonical's Go
# service manager /usr/bin/pebble, which is not a dpkg package, is never started by this entrypoint and (Go stdlib 1.27.3,
# CVE-2026-97054, fixed in 1.27.4) carries a HIGH finding that cannot be fixed from inside this image, so it is removed.
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 libssl3t64 openssl \
    && rm -rf /var/lib/apt/lists/* /usr/bin/pebble
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "PostgreSqlMcp.dll"]
