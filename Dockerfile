FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props postgresql-sharp-mcp.slnx ./
COPY src/ ./src/
RUN dotnet publish src/PostgreSqlMcp/PostgreSqlMcp.csproj -c Release --no-self-contained -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "PostgreSqlMcp.dll"]
