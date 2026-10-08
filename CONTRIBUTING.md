# Contributing

Useful contributions fix reproducible problems, improve a real workflow, clarify a contract or help another contributor make a sound decision. There is no activity quota, badge requirement or mandatory issue for a small correction.

## Build from source

Prerequisites:

- .NET 10 SDK **10.0.401 or newer stable 10.0 feature band** and Git, matching `global.json`. SDK-default C# 14 is used; no preview or floating `LangVersion=latest` override. All maintained build, installation, test and release tooling is C#/.NET; Python and Bash are not development prerequisites.
- Docker with access to a running daemon for the disposable PostgreSQL integration fixture and container checks.
- Node.js 22 or newer and npm only for npm installation/publication checks. They are not server runtime dependencies.
- Network access for SDK/package restore, container images and PostgreSQL extension packages.

```bash
git clone https://github.com/codegiveness/postgresql-sharp-mcp.git
cd postgresql-sharp-mcp
dotnet build postgresql-sharp-mcp.slnx -c Release
dotnet src/PostgreSqlMcp/bin/Release/net10.0/PostgreSqlMcp.dll --help
```

Prepare `POSTGRES_CONNECTION_STRING` using the README's [hidden Bash/Zsh or Windows PowerShell prompt](README.md#2-prepare-the-connection-environment), then run the source entrypoint from that same shell; no targets file is required for one server:

```bash
dotnet src/PostgreSqlMcp/bin/Release/net10.0/PostgreSqlMcp.dll --validate
```

Omit `--validate` to start the stdio server. Access mode defaults to unrestricted, but SQL calls remain read-only unless `execute_sql` explicitly sets `read_only=false`. Add `--access-mode restricted` or `POSTGRES_ACCESS_MODE=restricted` to refuse writes. Never commit populated targets files, credentials, customer SQL or database results.

The source layout is `src/PostgreSqlMcp.Core` (configuration, SQL and database behavior), `src/PostgreSqlMcp.Tools` (MCP tools), and `src/PostgreSqlMcp` (stdio host and CLI). Preserve dependency direction `Core <- Tools <- App`. MCP stdout is reserved for JSON-RPC; diagnostics belong on stderr.

An inherited base connection string alone creates the `primary` bootstrap profile and supports live discovery. Optional targets-file entries remain connection profiles for independent credentials, not per-database registration or implicit allowlists. Preserve exact alias selection for bootstrap compatibility, optional `target` for explicit profile selection, and physical database selection without rewriting configuration. Keep combining a base connection string with targets JSON/file fail-closed; do not silently prefer one credential source. Catalog discovery must be live and CONNECT-filtered; PostgreSQL enforces object privileges and RLS. Calls must not share a current database, fall back after selection errors, or grow unbounded pools. A base connection string's optional explicit database allowlist must constrain both listing and selection. See [selection and discovery](README.md#database-selection-and-live-discovery) and [resource boundaries](README.md#access-and-resource-boundaries).

Dependencies use current compatible stable releases verified against [official .NET release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json) and NuGet package indexes, not preview feeds. Central transitive pins keep the portable/five-RID graphs aligned; restore each RID with explicit `-p:RuntimeIdentifier=<rid> -p:RuntimeIdentifiers=<rid>` so its lockfile stays separate. Recheck compatibility and regenerate every affected lock on upgrades; do not infer that today's pins remain the latest indefinitely.

## Verify behavior

```bash
dotnet run --project tools/PostgreSqlMcp.Verify -c Release -- integration
```

The .NET verifier builds the application and exercises real MCP calls against a disposable PostgreSQL 17 Docker fixture with `pg_stat_statements` and HypoPG. It creates and removes its own container; do not substitute a production or unrelated database.

For database-discovery/setup changes, exercise one inherited connection string selecting an unregistered physical database with no targets file, live creation/grant/revoke changes, optional protected multi-profile files, optional allowlists, missing/denied selections without fallback, conflicting credential sources failing closed, and concurrent per-call isolation. Verify omitted-mode write commit/rollback with `read_only=false` separately from explicit restricted-mode refusal. Use disposable data; do not treat existing historical verification results as evidence that new behavior passed.

Coverage includes explicit multi-target isolation, permissions/RLS, bounded results and pagination, metadata, health/workload tools, plans, hypothetical-index cleanup, explicit writes and rollback, sanitized errors, configuration failures and log confidentiality. Linux subprocess and MCP-client regressions exercise blocked stdin, inherited output pipes, failed-writer disposal and owned-child cleanup. Verification command deadlines cover stdin, exit and output drains; release capture deadlines cover exit and drains. MCP verification requests also bound semaphore wait, stdin writing and response wait with one cancellation-aware deadline. Asynchronous stdin close is bounded, and cleanup observes readers before disposal. An already-orphaned descendant is outside the exited parent's `Process.Kill` tree, so this is not an OS process-group/job-object containment guarantee. Report only the commands and scenarios actually exercised; passing one fixture does not prove every platform, PostgreSQL version or MCP client works.

The integration command also runs deterministic FsCheck SQL properties against PostgreSQL: quoted literal round trips and second-statement rejection, with shrinking and replay seeds, plus nested comments, mixed-case control statements and malformed boundaries. Failures report only synthetic generated inputs and replay information. These finite scenarios complement permission tests; they are not proof of exhaustive fuzzing or OSS-Fuzz enrollment.

On Linux x64, run the coverage-guided lexer campaign separately:

```bash
dotnet run --project tools/PostgreSqlMcp.Fuzz -c Release -p:RestoreLockedMode=true -- campaign 60
```

The C# orchestrator accepts 1–600 seconds (default 60), downloads and verifies the pinned native bridge, restores the pinned SharpFuzz tool, publishes with locked dependencies, instruments `Core.SqlGuard` in isolated output and replays every checked-in corpus seed before running libFuzzer. Downloads, instrumentation, seed replay and campaign failures fail closed. The CI workflow runs 60 seconds for pushes/PRs and 300 seconds on its daily schedule. Generated corpus/crashes stay under ignored `artifacts/fuzz`; use synthetic SQL only, never operator inputs or database results. The callback probes UTF-8 and UTF-16, normalization, input bounds and second-statement rejection; it does not connect to PostgreSQL or prove authorization. Keep seed filenames portable, avoiding Windows reserved basenames such as `NUL`.

The maintained SARIF comparator can be exercised without a database:

```bash
dotnet run --project tools/PostgreSqlMcp.Verify -c Release -- sarif-regressions
dotnet run --project tools/PostgreSqlMcp.Verify -c Release -- sarif --baseline "<baseline-report-directory>" --candidate "<candidate-report-directory>"
```

The comparator rejects new high/critical security findings (CVSS >= 7) or error-level findings, missing/malformed reports and unsuccessful scans. Matching uses tool/rule identity, stable fingerprints and multiplicity, with precise location fallback. Diagnostics omit finding messages and source snippets. Its regression scenarios cover severity escalation, shifted fingerprints/locations, duplicate findings, incomplete reports and unsafe diagnostic text.

Use raw scanner artifacts containing successful invocation evidence. GitHub-normalized API exports can omit that evidence and are intentionally rejected. An `executionSuccessful` flag does not override compiler/extraction error notifications.

For a bug, reproduce the failure before changing code and exercise the same path after the fix. Major new functionality must have automated consumer-visible behavior coverage; extend the maintained verifier with relevant boundaries, transitions and errors rather than duplicating wiring assertions. Add regression coverage for plausible consumer-visible failures or uncertain boundaries; avoid source-text, wiring-only and mock-echo assertions. Documentation-only changes need link and command review rather than unrelated database runs.

## Build and install package artifacts

These commands build npm and NuGet artifacts without publishing:

```bash
dotnet run --project tools/PostgreSqlMcp.Build -c Release -- package --output artifacts/packages
dotnet run --project tools/PostgreSqlMcp.Verify -c Release -- packages --artifacts artifacts/packages
```

The package smoke installs into isolated locations with fresh caches and exercises the actual CLI and MCP entrypoints. Local npm, global npm and fresh-cache npx run with `dotnet` absent from PATH and deliberately invalid `DOTNET_ROOT` settings; the NuGet tool retains its .NET prerequisite. By default the smoke creates and removes its own PostgreSQL Docker fixture, requiring Docker and the same network/extension prerequisites as `integration`; installed entrypoints must use an inherited connection string without a targets file, discover/select a newly created database and commit/clean up an explicitly requested write. An optional `--targets-file PATH` instead enables database calls against separately authorized disposable profiles. Package generation verifies matching versions and includes dependency licenses/notices.

Windows/macOS CI uses `packages --artifacts artifacts/packages --installation-only` because those runners do not provide the Docker fixture. This explicit mode still installs both artifacts offline, exercises npm local/global/npx without installed .NET and the NuGet native CLI/MCP entrypoints, requires structured unavailable-database errors and verifies shutdown. It does not claim live discovery/query/write coverage; the Linux package job retains the full disposable-database scenarios above. Do not combine this mode with `--targets-file`, and do not use it to bypass a failed database verification run.

Package CLI checks and local-tarball npx initialization allow up to 600 seconds: npm re-reads the bundled five-runtime archive on each invocation, including warm-cache runs, and Windows archive resolution can exceed 45 seconds. Subsequent MCP calls retain their 20-second deadline. The smoke verifies stdin-EOF shutdown through the real npx wrapper and SIGTERM against that npx installation's native server: npm does not forward a signal sent only to its root process. It runs npm at `warn` log level to separate npm's command-echo notices from server diagnostics while retaining warnings/errors; production commands and logging are unchanged.

To inspect the npm package manually, replace `<version>` with the version in `npm/package.json`:

```bash
npm install --prefix ./artifacts/npm-client --ignore-scripts --no-audit --no-fund "./artifacts/packages/codegiveness-postgresql-sharp-mcp-<version>.tgz"
npm --prefix ./artifacts/npm-client/node_modules/@codegiveness/postgresql-sharp-mcp run postinstall
./artifacts/npm-client/node_modules/.bin/postgresql-sharp-mcp --version
```

Do not install the unbuilt `npm/` source directory; the .NET builder stages five self-contained server/installer payloads and the checksum-pinned, unmodified upstream `run-script-os` 1.1.6 installation runner. The manual commands deliberately disable dependency scripts and explicitly run this package's trusted initializer; they do not need an installed .NET runtime. Automatic registry npx/global setup on npm 12 uses `--allow-scripts=@codegiveness/postgresql-sharp-mcp`. Local tarballs instead need approval for their exact `file:<absolute-tarball-path>` spec; for example, `npx -y --allow-scripts=file:<absolute-tarball-path> --package=<absolute-tarball-path> -- postgresql-sharp-mcp --version`. Project installs may use a matching consumer `allowScripts` entry. Never use broad all-script bypasses. On Windows use `node_modules/.bin/postgresql-sharp-mcp.cmd`. The internal binary is `bin/postgresql-sharp-mcp.exe` on every OS. Unix installation uses `uname`. No runtime or binary is fetched during installation; the builder fetches and verifies the pinned upstream runner before packaging.

To inspect the .NET tool in a dedicated directory, replace `<version>` with the matching project version:

```bash
dotnet tool install --tool-path ./artifacts/tools --configfile ./artifacts/packages/NuGet.Config \
  --version "<version>" codegiveness.postgresql-sharp-mcp
./artifacts/tools/postgresql-sharp-mcp --version
```

On Windows, the tool executable ends in `.exe`. The generated `NuGet.Config` contains only the artifact directory and does not change user-level feeds. Regenerate it after moving the artifacts directory. Use a fresh tool directory or `dotnet tool update` when reinstalling. Do not point an MCP client at a `.nupkg` file.

Both installed entrypoints accept the inherited `POSTGRES_CONNECTION_STRING` and `--validate` used by the source entrypoint; optional `--targets-file` remains supported as an alternative, not combined with the base string. Runtime requirements remain those in [README.md](README.md).

### Install a verified release artifact

Registry publication can fail after the release's package and attestation jobs succeed. A maintainer-approved `release-artifacts` download from that main-branch run can still provide a verified local NuGet installation. This is not a published registry version; confirm the intended tag/version and successful package-verification and attestation jobs before installing. Access depends on GitHub artifact permissions and retention.

Using a recent GitHub CLI with `gh attestation` support, replace `<run-id>` and `<version>`:

```bash
gh run download "<run-id>" --repo codegiveness/postgresql-sharp-mcp \
  --name release-artifacts --dir ./release-artifacts
sha256sum "./release-artifacts/packages/codegiveness.postgresql-sharp-mcp.<version>.nupkg"
gh attestation verify "./release-artifacts/packages/codegiveness.postgresql-sharp-mcp.<version>.nupkg" \
  --repo codegiveness/postgresql-sharp-mcp \
  --signer-workflow codegiveness/postgresql-sharp-mcp/.github/workflows/release.yml
```

Compare the printed checksum with the package's entry in `release-artifacts/SHA256SUMS`, and require successful attestation verification. A checksum by itself does not authenticate its source.

The downloaded artifact does not contain the build's generated local-feed configuration. Create `release-artifacts/NuGet.Config` with this content; `packages` is relative to that configuration file:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="verified-release-artifact" value="packages" />
  </packageSources>
</configuration>
```

Install into a fresh dedicated tool directory using only that local source. For the final `--validate` command, first prepare the connection string with the [hidden prompt](README.md#2-prepare-the-connection-environment) in this same shell:

```bash
dotnet tool install codegiveness.postgresql-sharp-mcp --version "<version>" \
  --tool-path ./postgresql-mcp-tool --configfile ./release-artifacts/NuGet.Config
./postgresql-mcp-tool/postgresql-sharp-mcp --version
./postgresql-mcp-tool/postgresql-sharp-mcp --validate
```

`--validate` performs a read-only connectivity query and can print the PostgreSQL database identity on stderr. Keep that output private; do not paste it into public reports or CI evidence for a real database.

Use the absolute installed executable path in your MCP client's stdio entry and keep the .NET 10 runtime available. Follow [connection environment and MCP setup](README.md#2-prepare-the-connection-environment), fully quit the client and launch it from that prepared shell, then verify live database discovery and an authorized `SELECT 1`. `/mcp reload` alone cannot give an existing client a new parent environment. To keep this installation check in restricted mode, explicitly set `POSTGRES_ACCESS_MODE=restricted` in the client environment; the README's primary example otherwise defaults to unrestricted. Do not use application data or attempt writes merely to prove installation. Protected files remain an [optional alternative](README.md#optional-protected-targets-file), including for GUI clients that cannot inherit the prepared environment.

### Self-contained executable and container

Example self-contained build:

```bash
dotnet publish src/PostgreSqlMcp/PostgreSqlMcp.csproj -c Release -r linux-x64 --self-contained -o artifacts/linux-x64
./artifacts/linux-x64/PostgreSqlMcp --version
```

Self-contained builds still require platform-native libraries. Cross-compiling does not establish runtime compatibility; exercise each platform you claim to support.

Build and run the container from a shell prepared with the README's [hidden connection-string prompt](README.md#2-prepare-the-connection-environment), with stale targets settings cleared:

```bash
docker build -t postgresql-sharp-mcp .
docker run --rm -i -e POSTGRES_CONNECTION_STRING postgresql-sharp-mcp
```

`-e POSTGRES_CONNECTION_STRING` passes the existing shell value without putting its literal contents in command history; Docker/container inspection and sufficiently privileged operators can still see the plaintext container environment. It is not a secret store. Restart the container after credential changes.

For an optional protected file instead, clear the conflicting connection-string/targets-JSON settings as described in the README, then use a read-only mount:

```bash
docker run --rm -i \
  --mount "type=bind,src=<absolute-path-to-protected-targets-file>,dst=/run/postgres.targets.json,readonly" \
  -e POSTGRES_TARGETS_FILE=/run/postgres.targets.json postgresql-sharp-mcp
```

The database hostname must be reachable from the container; `localhost` refers to the container itself. Retain certificate validation for remote connections and mount any required CA material at the connection string's container path. For the optional credentials file, ensure the non-root runtime user can read it using a suitable group/UID or secret mount, not world-readable permissions. `-i` keeps stdin available for MCP stdio. Both routes use one bootstrap profile to select live physical databases without per-database JSON edits.

## Issues, pull requests and reviews

- **Bug reports:** search existing reports, then provide the package version, sanitized configuration, minimal reproduction, expected/actual behavior and redacted diagnostics. Use [SECURITY.md](SECURITY.md) for sensitive vulnerability reports instead of public issues.
- **Design work:** explain the user need and trade-offs before a large or hard-to-reverse change. A concise issue or PR description is enough.
- **Pull requests:** keep a coherent scope and link an existing issue when useful. Small corrections may go directly to a PR. Describe contract, compatibility and security impacts, commands/scenarios exercised, results and verification gaps. Do not approve your own PR or present a local run as proof of hosted automation or registry publication.
- **Reviews:** report actionable correctness, security or maintainability findings with reasons and reproductions where possible. Base approval on reviewed changes and relevant evidence, not a rubber stamp.
- **Support:** use the repository's available support channels for contextual questions and verified answers. Use an issue for actionable defects. Do not publish sensitive data in support threads.

Keep versions, affected help/docs and release notes consistent. Update [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and bundled licenses when shipped dependencies change. Contributions are under the project's [MIT license](LICENSE); dependencies retain their own terms.

## Automation and releases

Compatible fixes use a patch increment, such as **0.3.2 → 0.3.3**; reserve a minor increment for new or incompatible 0.x contracts. Discovery and the unrestricted-default contract remain the **0.3.0** capability boundary. **0.3.3** removes npm's installed-.NET prerequisite without changing database-access behavior; the .NET tool still requires .NET. A version in source is not evidence of NuGet/npm/GitHub Release publication; check the corresponding registry endpoint and release jobs.

- **CI** runs database integration and package installation checks for pushes and pull requests and uploads generated packages for inspection.
- **Release workflow** is manually dispatched from `main` for an existing `v<version>` tag reachable from `origin/main`; pushing a tag does not start publication. Read-only preflight compiles trusted release tooling from the immutable main workflow revision and validates the tag's commit. Read-only packaging checks out that commit and verifies version consistency and behavior. Checkout-free attestation and publishing jobs consume immutable artifact IDs with digest mismatches rejected; they do not compile or execute tag-source release tooling. npm requires `NPM_TOKEN`; NuGet uses GitHub OIDC with `NUGET_USERNAME` and a matching trusted publishing policy. Missing prerequisites fail only the corresponding registry job. Existing registry versions are skipped. Artifact creation or a successful GitHub release alone does not prove registry publication.
- **Dependency audit** checks direct and transitive NuGet packages against known advisories. This is not proof that all vulnerabilities are absent.
- **CodeQL** scans C# and GitHub Actions with `security-extended`. C# uses real builds, including generated sources. Trusted main/scheduled runs publish code-scanning results; PRs scan both the base and candidate with read-only tokens, retain raw SARIF and run the trusted-base severity-delta gate. Extraction errors and missing reports fail closed. Review workflow changes as well as findings.
- **Scorecard** publishes repository-practice findings on main and weekly. Its score is not a vulnerability-free claim or profile achievement.
- **Supply-chain evidence** includes an application CycloneDX SBOM, SHA-256 checksums, requested GitHub build attestations and requested npm provenance. Verify the actual release evidence; NuGet OIDC is authentication, not NuGet package provenance. See [Security posture](docs/security-posture.md).
- **Dependabot** proposes dependency and action updates. Review advisory impact, release notes and relevant runtime behavior before merging; it does not authorize automatic publication.
- **Secret scanning** combines GitHub provider scanning/push protection with pinned, checksum-verified Gitleaks CLI over all reachable history. PostgreSQL URI/Npgsql and SQL Server credential rules use redacted logging, no uploaded secret reports, and exact fixture exceptions. Do not baseline leaks or widen exclusions to entire files/directories.
- **Container security** builds the actual image and runs pinned, checksum-verified Trivy against OS and application packages. HIGH/CRITICAL findings include unfixed vulnerabilities and fail the gate; do not suppress failures or add broad exemptions.

Use verified full action commit SHAs with version comments, least-privilege job permissions and checkout without persisted credentials. Verify action pins against the upstream action repository, following annotated tags to their commits. Never run untrusted pull-request code in a privileged `pull_request_target` or `workflow_run` context. Publishing jobs require narrowly scoped write permissions; validation jobs should remain read-only. See [GitHub's secure-use guidance](https://docs.github.com/en/actions/reference/security/secure-use).

Only authorized maintainers may push release tags, publish packages or change repository settings. Check every release/publication job and the actual registry endpoints before announcing availability. Cross-platform archives are build outputs, not proof of execution on every platform.

### Publisher setup

Use the `release` GitHub environment for narrowly scoped publishing secrets. Restrict its deployment refs to the **main branch only**, not `v*` tags; the workflow validates the supplied tag separately. This keeps release invocation available to the solo maintainer without requiring another person's approval. Do not put tokens in source, command history, issues or pull requests.

1. In the npm account authorized to publish under `@codegiveness`, create a short-lived granular token with **Read and write (publish and stage)** access to that scope, including new package creation—not only the sibling's existing package or **stage only** access. For unattended publishing with account 2FA, enable **Bypass 2FA** on this token. Store it as `NPM_TOKEN` in the `release` environment. Organization-management permission alone does not grant package publication rights. Granular tokens currently must be created on the website, not with `npm token create`. See [npm token setup](https://docs.npmjs.com/creating-and-viewing-access-tokens).
2. In the authorized NuGet account, add a **Trusted Publishing** policy for owner `codegiveness`, repository `postgresql-sharp-mcp`, workflow filename `release.yml`, and environment `release`. Permit new packages and versions matching `codegiveness.postgresql-sharp-mcp`. Store the **policy creator's NuGet profile username** as `NUGET_USERNAME`, not an organization/policy owner's name, email or password. The workflow exchanges GitHub OIDC for a short-lived key; no persistent NuGet API key is required. See [NuGet trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing).
3. After verification, create the matching `v<version>` tag on a commit reachable from `main`. Run **Actions → Release → Run workflow** on **main** with that existing tag, or use the explicit `--ref main` command below. A tag push alone does not publish. Check each publication job and install the exact version from each registry before announcing availability.

GitHub's secret API exposes names and public encryption keys, not plaintext values. With explicit owner authorization, a source Actions job can encrypt selected existing credentials using the target environment's public key; an authorized operator then installs that ciphertext through the target secret API. Never print plaintext, upload it as an artifact, copy an unrelated token, or retain the temporary transfer branch/artifact. Token scope still must authorize this package, and NuGet's trusted-publishing policy must separately authorize this repository/workflow/environment.

If entering replacement credentials yourself, these commands prompt without putting their values in command history:

```bash
gh secret set NPM_TOKEN --repo codegiveness/postgresql-sharp-mcp --env release
gh secret set NUGET_USERNAME --repo codegiveness/postgresql-sharp-mcp --env release
gh workflow run release.yml --repo codegiveness/postgresql-sharp-mcp --ref main -f tag=v0.3.3
```

To publish only NuGet, select `target=nuget`. This skips npm publication and GitHub Release creation; the default `target=all` preserves publication to all three destinations:

```bash
gh workflow run release.yml --repo codegiveness/postgresql-sharp-mcp --ref main -f tag=v0.3.3 -f target=nuget
```

NuGet-only publication still requires successful preflight, package verification and attestation. A failed or canceled prerequisite cannot reach publishing. Reusing another repository's NuGet username does not reuse its trust policy: the policy must match this repository, workflow and environment, with permission to create this package.

For an owner-authorized manual registry recovery, `target=github` builds/verifies all artifacts, attests them and publishes only the GitHub release. It skips both registry jobs; publish the exact attested npm/NuGet files through separately authorized credentials instead of retrying a known publisher mismatch:

```bash
gh workflow run release.yml --repo codegiveness/postgresql-sharp-mcp --ref main -f tag=v0.3.3 -f target=github
```

Only run publication after account permissions/policy and the existing version tag are ready. For a failed run of the main-dispatched workflow, use `gh run rerun <run-id> --failed --repo codegiveness/postgresql-sharp-mcp` to reuse that run's immutable artifacts. Expired or deleted artifacts fail closed; start a new main dispatch rather than rebuilding publisher tooling from tag source.

An interactive local `npm login` does not update the workflow's `NPM_TOKEN` and does not necessarily satisfy npm's separate publish-time 2FA/browser approval. For an authorized manual recovery, publish the exact verified tarball using that login: `npm publish ./artifacts/packages/codegiveness-postgresql-sharp-mcp-<version>.tgz --ignore-scripts --access public --registry https://registry.npmjs.org/`. Open and approve the command's browser challenge promptly; an expired challenge is not publication. Do not request npm provenance from a workstation: that requires a supported CI identity. Record the provenance gap, check the registry's version/integrity, and exercise npx with a fresh cache. Local npm authentication does not grant NuGet publication access.

The [0.3.1 release run](https://github.com/codegiveness/postgresql-sharp-mcp/actions/runs/37085312914) published the attested GitHub Release assets, but npm's publish endpoint returned HTTP 404 (“not found or no permission”), and NuGet's OIDC exchange returned HTTP 401 (“no matching trust policy”). These repeat the first 0.2.0 registry attempt's authorization failures; npm's response does not identify the exact permission or account mismatch. GitHub secrets were present, and neither failure establishes registry publication. Confirm npm account/scope and new-package authorization and configure the matching target NuGet policy. After repairing account configuration, rerun only the failed jobs from this main-dispatched run to reuse its immutable verified artifacts.

The [0.3.2 release run](https://github.com/codegiveness/postgresql-sharp-mcp/actions/runs/37571048770) passed packaging, installed-distribution verification, attestation and GitHub publication, but initially failed npm (HTTP 404) and NuGet OIDC (HTTP 401). npm publication was recovered with local browser approval; its public tarball matches the attested original and fresh-cache npx execution passed. The npm Actions job subsequently passed by skipping the existing immutable version, without adding CI provenance to the local publication.

NuGet publication was recovered through an explicitly owner-authorized isolated job in the sibling `mssql-mcp` repository using its existing OIDC policy: [run 37573791557](https://github.com/codegiveness/mssql-mcp/actions/runs/37573791557). It verified the original PostgreSQL attestation before requesting credentials, skipped normal MSSQL release jobs and pushed only the PostgreSQL 0.3.2 package. NuGet enforced the existing scope; no secret transfer or policy widening was needed. The registry signature and every package payload entry matched verification expectations; public-feed tool installation passed after indexing. The target repository's policy mismatch remains separate from that successful recovery. Do not rerun its NuGet job expecting a copied username to change the policy, or reuse a sibling publisher without explicit owner authorization.

## GitHub profile recognition

GitHub's [contribution rules](https://docs.github.com/en/account-and-profile/reference/profile-contributions-reference) determine which activity appears on profiles. [Achievements](https://docs.github.com/en/account-and-profile/reference/profile-reference#earning-achievements) are a separate GitHub feature with criteria that may change; participation does not guarantee an award. Repository status badges are neither profile Achievements nor security certifications.

GitHub currently documents Achievements as a **public-preview** feature and explicitly says the Mars 2020 Helicopter Contributor award is no longer available. Its profile reference does not publish a complete authoritative list of current named-award thresholds; this project does not claim verified requirements for Pull Shark, Pair Extraordinaire, Galaxy Brain or other community-described awards. Qualifying contributions and earning an Achievement are different claims.

Useful issues, cohesive PRs, thoughtful reviews, genuinely helpful answers and truthful co-author attribution are welcome whether GitHub counts them or not. Do not manufacture activity, split trivial work, exchange stars/answers or bypass review to farm recognition. Displaying profile recognition is optional.
