# Contributing

Useful contributions fix reproducible problems, improve a real workflow, clarify a contract or help another contributor make a sound decision. There is no activity quota, badge requirement or mandatory issue for a small correction.

## Build from source

Prerequisites:

- .NET 10 SDK, Git, Bash and Python 3.10 or newer.
- Docker with access to a running daemon for the disposable PostgreSQL integration fixture and container checks.
- Node.js 22 or newer and npm for package generation and installation checks.
- Network access for SDK/package restore, container images and PostgreSQL extension packages.

```bash
git clone https://github.com/codegiveness/postgresql-sharp-mcp.git
cd postgresql-sharp-mcp
dotnet build postgresql-sharp-mcp.slnx -c Release
dotnet src/PostgreSqlMcp/bin/Release/net10.0/PostgreSqlMcp.dll --help
```

Run the source entrypoint with a protected targets file configured as described in [README.md](README.md):

```bash
dotnet src/PostgreSqlMcp/bin/Release/net10.0/PostgreSqlMcp.dll \
  --targets-file "<absolute-path-to-protected-targets-file>" --validate
```

Omit `--validate` to start the stdio server. Never commit populated targets files, credentials, customer SQL or database results.

The source layout is `src/PostgreSqlMcp.Core` (configuration, SQL and database behavior), `src/PostgreSqlMcp.Tools` (MCP tools), and `src/PostgreSqlMcp` (stdio host and CLI). Preserve dependency direction `Core <- Tools <- App`. MCP stdout is reserved for JSON-RPC; diagnostics belong on stderr.

## Verify behavior

```bash
bash scripts/verify.sh
```

The verification script builds the application and exercises real MCP calls against a disposable PostgreSQL 17 Docker fixture with `pg_stat_statements` and HypoPG. It creates and removes its own container; do not substitute a production or unrelated database.

Coverage includes explicit multi-target isolation, permissions/RLS, bounded results and pagination, metadata, health/workload tools, plans, hypothetical-index cleanup, explicit writes and rollback, sanitized errors, configuration failures and log confidentiality. Report only the commands and scenarios actually exercised; passing one fixture does not prove every platform, PostgreSQL version or MCP client works.

For a bug, reproduce the failure before changing code and exercise the same path after the fix. Add regression coverage for plausible consumer-visible failures or uncertain boundaries; avoid source-text, wiring-only and mock-echo assertions. Documentation-only changes need link and command review rather than unrelated database runs.

## Build and install package artifacts

These commands build npm and NuGet artifacts without publishing:

```bash
python3 scripts/package.py --output artifacts/packages
python3 scripts/package-smoke.py --artifacts artifacts/packages
```

The package smoke installs into isolated locations with fresh caches and exercises the actual CLI and MCP entrypoints. An optional `--targets-file PATH` enables database calls against separately authorized disposable targets. Package generation verifies that npm and application versions match and includes dependency licenses and notices.

To inspect the npm package manually, replace `<version>` with the version in `npm/package.json`:

```bash
npm install --prefix ./artifacts/npm-client --ignore-scripts --no-audit --no-fund \
  "./artifacts/packages/codegiveness-postgresql-sharp-mcp-<version>.tgz"
node ./artifacts/npm-client/node_modules/@codegiveness/postgresql-sharp-mcp/bin/postgresql-sharp-mcp.js --version
```

Do not install the unbuilt `npm/` source directory; its bundled payload is created by `package.py`. The direct Node entrypoint avoids shell/PATH ambiguity. The installed npm bin is also available in `node_modules/.bin` (`.cmd` on Windows).

To inspect the .NET tool in a dedicated directory, replace `<version>` with the matching project version:

```bash
dotnet tool install --tool-path ./artifacts/tools --configfile ./artifacts/packages/NuGet.Config \
  --version "<version>" codegiveness.postgresql-sharp-mcp
./artifacts/tools/postgresql-sharp-mcp --version
```

On Windows, the tool executable ends in `.exe`. The generated `NuGet.Config` contains only the artifact directory and does not change user-level feeds. Regenerate it after moving the artifacts directory. Use a fresh tool directory or `dotnet tool update` when reinstalling. Do not point an MCP client at a `.nupkg` file.

Both installed entrypoints accept the same `--targets-file` and `--validate` flags as the source entrypoint. Runtime requirements remain those in [README.md](README.md).

### Self-contained executable and container

Example self-contained build:

```bash
dotnet publish src/PostgreSqlMcp/PostgreSqlMcp.csproj -c Release -r linux-x64 --self-contained -o artifacts/linux-x64
./artifacts/linux-x64/PostgreSqlMcp --version
```

Self-contained builds still require platform-native libraries. Cross-compiling does not establish runtime compatibility; exercise each platform you claim to support.

Build and run the container with a read-only configuration mount:

```bash
docker build -t postgresql-sharp-mcp .
docker run --rm -i \
  --mount "type=bind,src=<absolute-path-to-protected-targets-file>,dst=/run/postgres.targets.json,readonly" \
  -e POSTGRES_TARGETS_FILE=/run/postgres.targets.json postgresql-sharp-mcp
```

The database hostname must be reachable from the container; `localhost` refers to the container itself. Ensure its non-root runtime user can read the mount using a suitable group/UID or secret mount, not world-readable permissions. `-i` keeps stdin available for MCP stdio.

## Issues, pull requests and reviews

- **Bug reports:** search existing reports, then provide the package version, sanitized configuration, minimal reproduction, expected/actual behavior and redacted diagnostics. Use [SECURITY.md](SECURITY.md) for sensitive vulnerability reports instead of public issues.
- **Design work:** explain the user need and trade-offs before a large or hard-to-reverse change. A concise issue or PR description is enough.
- **Pull requests:** keep a coherent scope and link an existing issue when useful. Small corrections may go directly to a PR. Describe contract, compatibility and security impacts, commands/scenarios exercised, results and verification gaps. Do not approve your own PR or present a local run as proof of hosted automation or registry publication.
- **Reviews:** report actionable correctness, security or maintainability findings with reasons and reproductions where possible. Base approval on reviewed changes and relevant evidence, not a rubber stamp.
- **Support:** use the repository's available support channels for contextual questions and verified answers. Use an issue for actionable defects. Do not publish sensitive data in support threads.

Keep versions, affected help/docs and release notes consistent. Update [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and bundled licenses when shipped dependencies change. Contributions are under the project's [MIT license](LICENSE); dependencies retain their own terms.

## Automation and releases

- **CI** runs database integration and package installation checks for pushes and pull requests and uploads generated packages for inspection.
- **Release workflow** accepts version tags and a manual rerun for an existing tag. It verifies tag/package version consistency and behavior before producing npm/NuGet packages and self-contained platform archives. Independent jobs create the GitHub release and publish registry packages. npm requires `NPM_TOKEN`; NuGet uses GitHub OIDC with `NUGET_USERNAME` and a matching trusted publishing policy. Missing prerequisites fail only the corresponding registry job. Existing registry versions are skipped. Artifact creation or a successful GitHub release alone does not prove registry publication.
- **Dependency audit** checks direct and transitive NuGet packages against known advisories. This is not proof that all vulnerabilities are absent.
- **CodeQL** performs static analysis on trusted branch/scheduled runs. Repository eligibility and code-scanning setup must be configured by maintainers; workflow files alone do not enable remote settings.
- **Dependabot** proposes dependency and action updates. Review advisory impact, release notes and relevant runtime behavior before merging; it does not authorize automatic publication.

Use verified full action commit SHAs with version comments, least-privilege job permissions and checkout without persisted credentials. Verify action pins against the upstream action repository, following annotated tags to their commits. Never run untrusted pull-request code in a privileged `pull_request_target` or `workflow_run` context. Publishing jobs require narrowly scoped write permissions; validation jobs should remain read-only. See [GitHub's secure-use guidance](https://docs.github.com/en/actions/reference/security/secure-use).

Only authorized maintainers may push release tags, publish packages or change repository settings. Check every release/publication job and the actual registry endpoints before announcing availability. Cross-platform archives are build outputs, not proof of execution on every platform.

### Publisher setup

Use the `release` GitHub environment for narrowly scoped publishing secrets and deployment-ref restrictions. Do not put tokens in source, command history, issues or pull requests.

1. In the npm account that owns `@codegiveness`, create a short-lived granular token with **Read and write (publish and stage)** access to that scope, including new package creation. For unattended publishing with account 2FA, enable **Bypass 2FA** on this token. Store it as `NPM_TOKEN` in GitHub Actions secrets. See [npm token setup](https://docs.npmjs.com/creating-and-viewing-access-tokens).
2. In the NuGet account that will own the package, add a **Trusted Publishing** policy for owner `codegiveness`, repository `postgresql-sharp-mcp`, workflow filename `release.yml`, and environment `release`. Permit new packages and versions matching `codegiveness.postgresql-sharp-mcp`. Store the account's NuGet profile username as `NUGET_USERNAME`, not its email or password. The workflow exchanges GitHub OIDC for a short-lived key; no persistent NuGet API key is required. See [NuGet trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing).
3. Push the matching `v<version>` tag after verification, or run **Actions → Release → Run workflow** with that existing tag. Check each publication job and install the exact version from each registry before announcing availability. GitHub cannot reveal or copy another repository's encrypted secret values; create or securely re-enter credentials for this repository.

## GitHub profile recognition

GitHub's [contribution rules](https://docs.github.com/en/account-and-profile/reference/profile-contributions-reference) determine which activity appears on profiles. [Achievements](https://docs.github.com/en/account-and-profile/reference/profile-reference#earning-achievements) are a separate GitHub feature with criteria that may change; participation does not guarantee an award. Repository status badges are neither profile Achievements nor security certifications.

Useful issues, cohesive PRs, thoughtful reviews, genuinely helpful answers and truthful co-author attribution are welcome whether GitHub counts them or not. Do not manufacture activity, split trivial work, exchange stars/answers or bypass review to farm recognition. Displaying profile recognition is optional.
