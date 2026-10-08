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
- **Release workflow** is manually dispatched from `main` for an existing `v<version>` tag reachable from `origin/main`; pushing a tag does not start publication. Read-only preflight compiles trusted release tooling from the immutable main workflow revision and validates the tag's commit. Read-only packaging checks out that commit and verifies version consistency and behavior. Checkout-free attestation and publishing jobs consume immutable artifact IDs with digest mismatches rejected; they do not compile or execute tag-source release tooling. The locally verified independent publisher changes require review and merge before hosted use: npm stages through package-specific GitHub OIDC, without `NPM_TOKEN` or token fallback, and waits for Windows 2FA approval; NuGet uses its own matching OIDC policy with `NUGET_USERNAME`. Existing public registry versions are skipped. Artifact creation, staging or a successful GitHub release alone does not prove registry publication.
- **Dependency audit** checks direct and transitive NuGet packages against known advisories. This is not proof that all vulnerabilities are absent.
- **CodeQL** scans C# and GitHub Actions with `security-extended`. C# uses real builds, including generated sources. Trusted main/scheduled runs publish code-scanning results; PRs scan both the base and candidate with read-only tokens, retain raw SARIF and run the trusted-base severity-delta gate. Extraction errors and missing reports fail closed. Review workflow changes as well as findings.
- **Scorecard** publishes repository-practice findings on main and weekly. Its score is not a vulnerability-free claim or profile achievement.
- **Supply-chain evidence** includes an application CycloneDX SBOM, SHA-256 checksums, requested GitHub build attestations and requested npm provenance. Verify the actual release evidence; NuGet OIDC is authentication, not NuGet package provenance. See [Security posture](docs/security-posture.md).
- **Dependabot** proposes dependency and action updates. Review advisory impact, release notes and relevant runtime behavior before merging; it does not authorize automatic publication.
- **Secret scanning** combines GitHub provider scanning/push protection with pinned, checksum-verified Gitleaks CLI over all reachable history. PostgreSQL URI/Npgsql and SQL Server credential rules use redacted logging, no uploaded secret reports, and exact fixture exceptions. Do not baseline leaks or widen exclusions to entire files/directories.
- **Container security** builds the actual image and runs pinned, checksum-verified Trivy against OS and application packages. HIGH/CRITICAL findings include unfixed vulnerabilities and fail the gate; do not suppress failures or add broad exemptions.

Use verified full action commit SHAs with version comments, least-privilege job permissions and checkout without persisted credentials. Verify action pins against the upstream action repository, following annotated tags to their commits. Never run untrusted pull-request code in a privileged `pull_request_target` or `workflow_run` context. Publishing jobs require narrowly scoped write permissions; validation jobs should remain read-only. See [GitHub's secure-use guidance](https://docs.github.com/en/actions/reference/security/secure-use).

Only authorized maintainers may push release tags, publish packages or change repository settings. Check every release/publication job and the actual registry endpoints before announcing availability. Cross-platform archives are build outputs, not proof of execution on every platform.

### Independent publisher setup

The setup below describes locally verified workflow changes, not a completed hosted rollout. Keep application/source version **0.3.3** and the existing **v0.3.3** tag unchanged; release-tool changes do not replace immutable shipped artifacts.

Use this repository's `release` GitHub environment. Its deployment policy permits the **main branch only**, not `v*` tags; the workflow validates the supplied tag separately. No environment reviewer is required for solo-maintainer dispatch. npm's human approval is a separate registry-side 2FA gate. Do not transfer credentials from another repository or depend on a sibling job. Never put credentials in source, command history, issues or pull requests.

#### npm package trust and Windows setup

1. Sign in to npm as the package maintainer with publish access to **`@codegiveness/postgresql-sharp-mcp`** and enable account 2FA. A linked GitHub account in npm account settings establishes account identity/recovery; it does **not** authorize a GitHub Actions workflow to stage this package.
2. Open this package's **Settings → Trusted publishing** on npmjs.com and add **GitHub Actions** with organization/user **`codegiveness`**, repository **`postgresql-sharp-mcp`**, workflow filename **`release.yml`** (not a path), and environment **`release`**. Configure **stage-only** allowed actions: allow **`npm stage publish`**, leave **`Allow npm publish` disabled**, and leave **`Allow npm dist-tag` disabled**. Inspect all other package trusted publishers and remove any direct-publish route that would defeat the required human approval.
3. In **Settings → Publishing access**, choose **Require two-factor authentication and disallow tokens**, then save. Do not create or use a bypass-2FA automation token. The staging workflow must use GitHub OIDC only; no `NPM_TOKEN`, `NODE_AUTH_TOKEN` or token fallback belongs in its npm job. Remove obsolete repository/environment npm publishing secrets and revoke obsolete publishing tokens through their owning account.
4. On the approving Windows workstation, install [Node.js **24.15.0**](https://nodejs.org/en/download), open a new PowerShell terminal so PATH is refreshed, and use **npm 12.2.0**, matching the release workflow's explicit versions. The general staging minimum is Node **22.14.0** and npm **11.15.0**, but npm 12.2.0 has the stricter Node engine requirement **`^22.22.2 || ^24.15.0 || >=26`**; the older staging minimum alone is not sufficient for this pinned CLI.

```powershell
node --version
npm.cmd install --global npm@12.2.0 --ignore-scripts --registry https://registry.npmjs.org/
npm.cmd --version
npm.cmd login --auth-type=web --registry https://registry.npmjs.org/
npm.cmd whoami --registry https://registry.npmjs.org/
```

Complete the login browser challenge on Windows and confirm the intended package-maintainer account (`codegiveness` for the current release). `npm.cmd` avoids PowerShell's script execution-policy issue with `npm.ps1`. Local login authenticates the human review/approval commands; it neither configures nor tests package OIDC trust, and `whoami` is not a trusted-publisher permission check.

A **newly created npm trusted publisher must complete its first successful publish within two days** or it expires. Create it when the reviewed workflow and Windows approver are ready. Staging alone is **not documented as satisfying that deadline**; complete approval and verify publication within the window, then check the configuration's status. If it expires, delete it and create a fresh matching stage-only configuration; do not enable direct publishing to avoid expiry. See [npm trust configuration and expiry](https://docs.npmjs.com/trusted-publishers) and [staged publishing](https://docs.npmjs.com/staged-publishing).

#### NuGet policy owned by this release

In the authorized NuGet account, create a **Trusted Publishing** policy matching GitHub owner **`codegiveness`**, repository **`postgresql-sharp-mcp`**, workflow filename **`release.yml`**, and environment **`release`**. Its package permission must cover exactly **`codegiveness.postgresql-sharp-mcp`**, with permission to publish its versions (and create that exact package if needed); do not use a broad sibling/package-prefix permission. The policy creator must have publishing authority over that package. Set **`NUGET_USERNAME`** to the **policy creator's NuGet profile username**, not the organization/policy-owner name, email or password:

```powershell
gh secret set NUGET_USERNAME --repo codegiveness/postgresql-sharp-mcp --env release
```

Enter the username at the prompt rather than copying a sibling secret. GitHub OIDC exchanges for a short-lived NuGet key; no persistent NuGet API key is required. Username reuse does not reuse the sibling's trust policy. See [NuGet trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing).

#### Dispatch the exact existing release

Merge the reviewed release-tool/workflow changes to `main` before using them. Only dispatch after the account permissions, matching policies and existing `v0.3.3` tag are ready. **Actions → Release → Run workflow** must select **main**. The equivalent commands below are alternatives; run only the intended target:

```powershell
# Default all: GitHub Release + npm staging + NuGet publication.
gh workflow run release.yml --repo codegiveness/postgresql-sharp-mcp --ref main -f tag=v0.3.3 -f target=all
# npm staging only; no GitHub Release or NuGet publication.
gh workflow run release.yml --repo codegiveness/postgresql-sharp-mcp --ref main -f tag=v0.3.3 -f target=npm
# NuGet publication only; no GitHub Release or npm staging.
gh workflow run release.yml --repo codegiveness/postgresql-sharp-mcp --ref main -f tag=v0.3.3 -f target=nuget
# GitHub Release only; no registry staging/publication.
gh workflow run release.yml --repo codegiveness/postgresql-sharp-mcp --ref main -f tag=v0.3.3 -f target=github
```

`target` choices are `all`, `github`, `npm` and `nuget`, defaulting to `all`. Every target retains common preflight, package/installed-distribution verification and attestation. npm-only and NuGet-only jobs tolerate an intentionally skipped GitHub Release job **only for their dedicated target**; failed/canceled prerequisites cannot reach a publisher. In `all` mode both registry jobs require successful GitHub Release publication. The npm command stages the exact attested tarball using `npm stage publish`, never approves it or calls direct `npm publish`. Its successful summary means **staged, pending Windows 2FA approval**, not published.

For a failed current main-dispatched workflow, `gh run rerun <run-id> --failed --repo codegiveness/postgresql-sharp-mcp` reuses its immutable artifacts. Expired/deleted artifacts fail closed; use a new main dispatch rather than compiling publisher tooling from tag source. Do not rerun an older token-based npm publisher as a substitute for the reviewed stage-only workflow.

#### Approve each npm publication on Windows

After the npm staging job succeeds, review its stage ID and intended `latest` tag. Every public npm publication requires the maintainer's Windows 2FA approval. A successful dispatch, login, OIDC exchange or staging response is not approval.

```powershell
npm.cmd stage list '@codegiveness/postgresql-sharp-mcp' --registry https://registry.npmjs.org/
$StageId = Read-Host 'Stage ID for @codegiveness/postgresql-sharp-mcp 0.3.3'
npm.cmd stage view $StageId --registry https://registry.npmjs.org/
npm.cmd stage download $StageId --registry https://registry.npmjs.org/
```

Confirm the stage is **`@codegiveness/postgresql-sharp-mcp@0.3.3`**, comes from the intended run and uses the intended dist-tag. Compare the downloaded staged tarball's SHA-256 against the attested release tarball before approval. For the existing 0.3.3 GitHub release:

```powershell
gh release download v0.3.3 --repo codegiveness/postgresql-sharp-mcp --pattern codegiveness-postgresql-sharp-mcp-0.3.3.tgz --dir release-verification
gh attestation verify ./release-verification/codegiveness-postgresql-sharp-mcp-0.3.3.tgz --repo codegiveness/postgresql-sharp-mcp --signer-workflow codegiveness/postgresql-sharp-mcp/.github/workflows/release.yml
Get-FileHash ./release-verification/codegiveness-postgresql-sharp-mcp-0.3.3.tgz -Algorithm SHA256
$StagedTarball = Read-Host 'Path printed by npm stage download'
Get-FileHash $StagedTarball -Algorithm SHA256
```

Both hashes must match; also enforce the intended repository, `release.yml` signer, main ref and exact source commit in the attestation review. For dedicated-target artifacts, use the same-run immutable artifact and attestation verification procedure [above](#install-a-verified-release-artifact), not an unverified rebuild.

Only after that review, approve the exact stage:

```powershell
npm.cmd stage approve $StageId --registry https://registry.npmjs.org/
npm.cmd view '@codegiveness/postgresql-sharp-mcp@0.3.3' version dist.integrity dist.tarball --registry https://registry.npmjs.org/
npm.cmd view '@codegiveness/postgresql-sharp-mcp' dist-tags --registry https://registry.npmjs.org/
npx.cmd --yes --allow-scripts=@codegiveness/postgresql-sharp-mcp --cache ./release-verification/npm-cache-0.3.3 '@codegiveness/postgresql-sharp-mcp@0.3.3' --version
```

Complete the command's **2FA/browser challenge on Windows** promptly; an expired or canceled challenge does not publish. npmjs.com's **Staged Packages → Approve** is an alternative with the same required 2FA, not an automated bypass. Approval cannot use workflow OIDC. Check public version, integrity/tarball equality and fresh-cache execution before announcing availability; check actual provenance evidence separately. A workstation-staged recovery must use the exact verified tarball with `npm.cmd stage publish ./release-verification/codegiveness-postgresql-sharp-mcp-0.3.3.tgz --ignore-scripts --access public --tag latest --registry https://registry.npmjs.org/` and the same review/2FA approval; it does not gain CI provenance. Do not restage a version already pending approval or already published.

#### Recorded publication evidence

The [v0.3.3 GitHub Release](https://github.com/codegiveness/postgresql-sharp-mcp/releases/tag/v0.3.3) was published by [run 37737794459](https://github.com/codegiveness/postgresql-sharp-mcp/actions/runs/37737794459); its npm and NuGet jobs were skipped. Both downloaded package hashes matched the release manifest, and GitHub attestations passed with the exact workflow, main ref, source commit and hosted-runner identity enforced. Local npm/npx and NuGet tool installations reported **0.3.3.0** and passed help execution. The exact npm tarball was staged through the maintainer's local session; public publication still requires final Windows 2FA approval and registry verification, and this recovery has no CI npm provenance.

The independently dispatched [NuGet-only run 37742507501](https://github.com/codegiveness/postgresql-sharp-mcp/actions/runs/37742507501) passed preflight, real PostgreSQL integration, installed-package verification, packaging and attestation. Its NuGet OIDC exchange failed with HTTP 401, reporting no matching trust policy owned by the supplied user; the push step did not run. Correct the target policy/creator username before rerunning only the failed jobs. The maintainer reports both target policies configured, but npm OIDC staging remains unexercised and the NuGet exchange has not succeeded.

Local release-tool verification passed a locked build with zero warnings/errors, actionlint 1.7.12 and the real npm tarball staging dry-run with isolated configuration. Missing GitHub OIDC was rejected even with an inherited synthetic token. The dry-run submitted no stage and did not validate a real OIDC exchange. These checks do not claim hosted execution of the new workflow or public registry availability.

The [0.3.1 release run](https://github.com/codegiveness/postgresql-sharp-mcp/actions/runs/37085312914) published the attested GitHub Release assets, but npm's publish endpoint returned HTTP 404 (“not found or no permission”), and NuGet's OIDC exchange returned HTTP 401 (“no matching trust policy”). These repeat the first 0.2.0 registry attempt's authorization failures; npm's response does not identify the exact permission or account mismatch. GitHub secrets were present, and neither failure establishes registry publication. These historical failures do not test the new stage-only setup; future releases require the package trust and target NuGet policy described above.

The [0.3.2 release run](https://github.com/codegiveness/postgresql-sharp-mcp/actions/runs/37571048770) passed packaging, installed-distribution verification, attestation and GitHub publication, but initially failed npm (HTTP 404) and NuGet OIDC (HTTP 401). npm publication was recovered with local browser approval; its public tarball matches the attested original and fresh-cache npx execution passed. The npm Actions job subsequently passed by skipping the existing immutable version, without adding CI provenance to the local publication.

NuGet publication was recovered through an explicitly owner-authorized isolated job in the sibling `mssql-mcp` repository using its existing OIDC policy: [run 37573791557](https://github.com/codegiveness/mssql-mcp/actions/runs/37573791557). It verified the original PostgreSQL attestation before requesting credentials, skipped normal MSSQL release jobs and pushed only the PostgreSQL 0.3.2 package. NuGet enforced the existing scope; no secret transfer or policy widening was needed. The registry signature and every package payload entry matched verification expectations; public-feed tool installation passed after indexing. That one-time recovery did not repair this repository's policy mismatch and is not the independent release procedure. Future NuGet dispatches need this repository's own matching policy; copying a username or depending on a sibling job is not a substitute.

## GitHub profile recognition

GitHub's [contribution rules](https://docs.github.com/en/account-and-profile/reference/profile-contributions-reference) determine which activity appears on profiles. [Achievements](https://docs.github.com/en/account-and-profile/reference/profile-reference#earning-achievements) are a separate GitHub feature with criteria that may change; participation does not guarantee an award. Repository status badges are neither profile Achievements nor security certifications.

GitHub currently documents Achievements as a **public-preview** feature and explicitly says the Mars 2020 Helicopter Contributor award is no longer available. Its profile reference does not publish a complete authoritative list of current named-award thresholds; this project does not claim verified requirements for Pull Shark, Pair Extraordinaire, Galaxy Brain or other community-described awards. Qualifying contributions and earning an Achievement are different claims.

Useful issues, cohesive PRs, thoughtful reviews, genuinely helpful answers and truthful co-author attribution are welcome whether GitHub counts them or not. Do not manufacture activity, split trivial work, exchange stars/answers or bypass review to farm recognition. Displaying profile recognition is optional.
