FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props postgresql-sharp-mcp.slnx ./
COPY src/ ./src/
COPY README.md SECURITY.md CONTRIBUTING.md LICENSE THIRD-PARTY-NOTICES.md ./
COPY LICENSES/ ./LICENSES/
RUN dotnet publish src/PostgreSqlMcp/PostgreSqlMcp.csproj -c Release --no-self-contained -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
# Provide native GSS support for Npgsql's default encryption negotiation.
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "PostgreSqlMcp.dll"]
