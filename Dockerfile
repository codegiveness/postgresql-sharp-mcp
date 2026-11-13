FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props postgresql-sharp-mcp.slnx ./
COPY src/ ./src/
COPY README.md SECURITY.md CONTRIBUTING.md LICENSE THIRD-PARTY-NOTICES.md ./
COPY LICENSES/ ./LICENSES/
RUN dotnet restore src/PostgreSqlMcp/PostgreSqlMcp.csproj --locked-mode \
    && dotnet publish src/PostgreSqlMcp/PostgreSqlMcp.csproj -c Release --no-restore --no-self-contained -o /app

FROM mcr.microsoft.com/dotnet/runtime:11.0@sha256:3fe5f5495141c97986a49d3a4e76061f6225c3f93f94e8a7e522781376193863
# Native GSS for Npgsql, plus OpenSSL security updates absent from the pinned base.
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 libssl3t64 openssl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "PostgreSqlMcp.dll"]
