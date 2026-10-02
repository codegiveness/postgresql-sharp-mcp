FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props postgresql-sharp-mcp.slnx ./
COPY src/ ./src/
COPY README.md SECURITY.md CONTRIBUTING.md LICENSE THIRD-PARTY-NOTICES.md ./
COPY LICENSES/ ./LICENSES/
RUN dotnet restore src/PostgreSqlMcp/PostgreSqlMcp.csproj --locked-mode \
    && dotnet publish src/PostgreSqlMcp/PostgreSqlMcp.csproj -c Release --no-restore --no-self-contained -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:ff17a18b639a0327e52c7c296fa2e1abe6e03eb61d8121a8ef67cc6aa430a27e
# Native GSS for Npgsql, plus OpenSSL security updates absent from the pinned base.
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 libssl3t64 openssl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "PostgreSqlMcp.dll"]
