# Security posture

Configured controls are not proof that a scan passed, an artifact was attested, or a registry accepted a package. Check the corresponding run and release version.

## Runtime boundaries

- PostgreSQL roles and permissions are the authorization boundary. Omitted server access mode defaults to **unrestricted**, while SQL requests default to `read_only=true` and run in server-owned read-only transactions. Explicit writes require `read_only=false` and sufficient PostgreSQL privileges; successful writes commit, failures roll back, and writes are not automatically replayed. Explicit `restricted` mode refuses writes and remains an operator opt-in.
- The SQL lexer enforces a single statement and excludes transaction/session control. It is not an AST allowlist, SQL authorization parser, or sandbox. Read-only transactions cannot undo privileged routine or extension external effects.
- Configured aliases are bootstrap connection profiles, not database allowlists. Optional `target` selects a profile; each call independently selects a physical database using its host/authentication/TLS settings, with no shared current database, fallback or connection strings accepted from tool arguments. Live discovery checks CONNECT and excludes templates/disabled databases, but actual connectivity and object access remain PostgreSQL-enforced. Newly accessible databases need no configuration edits. Tenant separation requires appropriate PostgreSQL credentials and permissions, including review of `PUBLIC` CONNECT. A base connection string can optionally use an explicit database allowlist to narrow listing and selection.
- Environment-first setup uses a masked prompt for one inherited `POSTGRES_CONNECTION_STRING`, creating the `primary` seed without mandatory targets JSON. Masking is not encryption: session values are plaintext, copied to children and inspectable by privileged local processes; they are not automatically persisted, but launchers/container tools or explicit persistence can retain them. The client must start in the prepared shell and fully restart for changed environment secrets; a reload or existing GUI process cannot acquire the new parent environment. Optional rc functions persist prompt code only, never credentials or automatic secret exports. Optional owner-protected files remain for multiple profiles/automation and impractical environment propagation; they are persistent plaintext with OS-permission/backup/sync risks. Neither storage choice is universally safer. Combining a base string with targets JSON/file fails closed; switching is manual and does not require deleting protected files.
- SDK/provider payload logging is disabled. Tool errors preserve SQLSTATE with fixed summaries, not PostgreSQL messages, hints or details. Successful results can contain sensitive authorized data.
- Runtime database pools are bounded to `floor(256 / POSTGRES_POOL_SIZE)` entries; idle least-recently-used entries are disposed, active entries are never evicted, and full active capacity waits within the operation deadline. These bounds, server-side read-only pagination and response limits constrain resource use, but do not prevent expensive SQL or guarantee a total process memory/CPU ceiling.
- Connections, transactions, commands/readers, cancellation sources and query/plan scratch buffers have scoped ownership. HypoPG failure cleanup resets session-local state or clears the pool. A pre-discovery finite repeated-call heap/handle review and in-flight shutdown smoke found no progressive retention on the exercised paths; see [measured resource limits](../README.md#access-and-resource-boundaries). Those historical measurements do not verify the new dynamic pool cache or establish a blanket leak-free guarantee.

See [SECURITY.md](../SECURITY.md) for credentials, TLS, extension privileges, reporting and operational limits.

**Upgrade boundary:** the **0.3.0** contract makes targets-file aliases connection profiles rather than confinement to bootstrap databases, changes omitted access mode from restricted, and returns a live `databases` query page from `list_databases` rather than a `targets` alias list. The **0.3.1 patch follow-on** documents environment-first setup; it is not another discovery boundary or proof of package publication. Preserve explicit restricted settings where intended and review database-role grants before deploying. Neither default unrestricted mode nor discovery grants privileges beyond those of the configured PostgreSQL role. Remote TLS/certificate validation remains operator-configured; masked credential entry does not replace `SSL Mode=VerifyFull` and appropriate CA trust.

## Development controls

- CodeQL uses `security-extended` for C# and GitHub Actions. C# scans build the real solution to capture compiler-generated code. Trusted main/scheduled runs publish results. PR base/candidate scans and the aggregate gate use read-only tokens and preserve raw SARIF artifacts, without privileged uploads or release secrets.
- The PR gate executes the comparator from the trusted base revision and rejects new CVSS >= 7/error-level findings, incomplete reports and extraction errors. Stable fingerprints and multiplicity prevent broad rule-only suppression. Reviewers must still inspect workflow changes; this is not protection against a maintainer deliberately weakening the workflow.
- Dependency auditing checks direct and transitive NuGet packages against known advisories. Portable and per-platform lockfiles make dependency resolution explicit; locked restore rejects drift. GitHub Dependabot alerts and automatic security-update PRs are enabled; proposed fixes still require review and passing checks.
- OpenSSF Scorecard reports repository practices. Findings and the public score can lag configuration changes. The score is neither an independent vulnerability audit nor a certification.
- Actions use verified full commit pins, checkout credentials are not persisted, and publishing secrets are scoped to the `release` environment. Repository policy now enforces full-SHA Action pins and permits GitHub-owned Actions plus `ossf/scorecard-action` and `NuGet/login`; Marketplace-wide approval is disabled. The allowlist permits future SHA-pinned updates of those two Actions, rather than freezing Dependabot to one commit. No privileged `pull_request_target` or `workflow_run` execution of untrusted code is configured.
- Main requires PRs, up-to-date required checks from GitHub Actions and resolved review conversations, and blocks force pushes/deletion, including administrators. The explicitly chosen solo-maintainer policy requires zero independent approvals, no code-owner approval and no last-pusher approval. The maintainer can merge after required checks pass and resolve conversations themselves; this does not claim independent review. Default workflow tokens remain read-only, bot review approval remains disabled, and external fork workflows require owner approval before running.
- Installation checks execute native npm and NuGet entrypoints on Linux, Windows and macOS in CI. Database integration runs against disposable PostgreSQL, not an operator's configured database. Cross-built ARM64/Intel archives are not proof of execution on all architectures.
- Deterministic/shrinking FsCheck properties exercise real PostgreSQL literal round trips and second-statement rejection, alongside control/malformed-input regressions. The separate SharpFuzz/libFuzzer campaign instruments the actual SQL lexer, replays checked-in regressions and mutates synthetic UTF-8/UTF-16 inputs. Its C# orchestration verifies the native bridge checksum and fails on replay/instrumentation/engine errors. These layers supplement permissions, rollback and cancellation coverage; neither proves exhaustive fuzzing, SQL authorization or OSS-Fuzz enrollment.
- GitHub provider secret scanning and push protection are enabled. Checksum-verified Gitleaks CLI scans all reachable history, including PR commits, with default rules plus PostgreSQL URI/Npgsql/SQL Server password rules. Logs are redacted and no secret reports are published. Exceptions require exact synthetic values and paths; removed legacy fixtures also require exact historical commits. No findings baseline or inline suppression is accepted.
- Checksum-verified Trivy scans the actual built container's OS and application packages. HIGH/CRITICAL findings, including unfixed findings, fail; scan/network failures are not clean results. Runtime OpenSSL updates addressed the observed CVE-2026-84782. Advisory databases and package availability change, so a passing run is a point-in-time result, not a vulnerability-free guarantee.

Scorecard's published results can have measurement blind spots: its default Actions token cannot read classic branch-protection rules, and packaging heuristics may not recognize compiled release orchestration. Check protection through the repository API and publication/attestations through the actual release evidence instead of adding a broad token or changing code merely to satisfy a heuristic. A young repository and solo-maintainer reviews can also lower the score; no artificial activity or unsupported achievement badge is used to inflate it.

The 2026-10-02 authorized settings update requested generic detection and validity checks using explicit JSON and the current REST API version. GitHub accepted the requests but returned both `secret_scanning_non_provider_patterns` and `secret_scanning_validity_checks` still disabled; provider scanning and push protection remain enabled. Disabled status does not establish ineligibility. Authenticated settings were unavailable and the owner-browser relay was not connected, so their enablement is not claimed. An owner must inspect **Settings → Advanced Security → Secret Protection → Generic patterns / Validity checks**. Gitleaks supplies repository-specific database-credential detection independently. Validity checks cover supported provider credentials, not generic database-password patterns, and send detected credentials to their issuer for validation.

### Published Scorecard findings

The [published report](https://api.securityscorecards.dev/projects/github.com/codegiveness/postgresql-sharp-mcp) dated **2026-10-02 06:54:41 UTC** scored **6.3**, on revision `bde167e95e0fb3ff74b895db55c87f4b04d69b0c` with Scorecard v5.5.0. The five open GitHub code-scanning alerts at assessment time were Scorecard practice findings, not CodeQL source vulnerabilities:

| Finding | Published evidence | Assessment and supported action |
|---|---|---|
| SAST: 7 | CodeQL configuration detected; 0/6 sampled commits recognized as checked | The matching [push CodeQL run](https://github.com/codegiveness/postgresql-sharp-mcp/actions/runs/36975816274) completed C# and Actions analysis after this snapshot. The [PR run](https://github.com/codegiveness/postgresql-sharp-mcp/actions/runs/36975555833) passed all four raw scans and the trusted-base comparator. Keep read-only PR scanning; do not grant upload privileges solely to satisfy the detector. |
| Fuzzing: 0 | No recognized fuzzer integration in the published revision | Local FsCheck PostgreSQL properties and SharpFuzz/libFuzzer lexer campaigns provide distinct testing layers. The new workflow was not yet published; local execution does not prove hosted success or a future score. Scorecard's version-matched documentation recognizes FsCheck; it does not promise direct detection of this SharpFuzz setup. |
| Code-Review: 0 | 0/9 sampled changesets approved | Main requires PRs/checks but zero independent approvals. The latest merged PR had no review entries. Obtain genuine independent human review; configuration and self-review cannot repair the historical sample. |
| Maintained: 0 | Repository created within the preceding 90 days | The repository was created on 2026-10-02. Age and meaningful ongoing maintenance cannot be manufactured by extra commits or empty issues. |
| Best Practices | Historical report lacked enrollment evidence | Live entry 15155 is now enrolled at 19%, not passing; see [enrollment evidence](#openssf-best-practices-enrollment). |

Dangerous-Workflow, Token-Permissions and Pinned-Dependencies each scored 10 in that snapshot. These are detector results, not a comprehensive workflow audit. The local review retained matching scanner/query/build settings across revisions, trusted-base comparison, failed-scan rejection, credential-free checkout and separation of PR validation from privileged publication.

The sibling [mssql-mcp report](https://api.securityscorecards.dev/projects/github.com/codegiveness/mssql-mcp), dated 2026-10-02 05:37:07 UTC, scored 6.4 and also reported Code-Review 0, Maintained 0, Fuzzing 0 and SAST 7. Its live branch policy requires zero approvals, and PR 149 had no review entries. Dependabot and Release Please create useful update/release PRs; they do not supply independent human approval. This repository retains the useful PR/check, dependency-update and pinned-Action patterns without copying group-review requirements, Bash launchers or unrelated registry credentials. A solo workflow can be well-tested and secure without receiving every Scorecard practice score.

Local verification for this assessment used SDK 10.0.401 with locked restores. The final Release solution build passed with zero warnings/errors. Disposable PostgreSQL integration passed all runtime scenarios, including 129 FsCheck literal properties, permissions/RLS, target isolation, rollback, result limits, confidentiality, HypoPG cleanup and the SARIF comparator regressions. A nominal 10-second native campaign replayed 13 seeds and completed 18,975 engine runs in 12 seconds with an empty crash directory; `campaign 0` rejected the invalid duration with exit 1. The first campaign exposed a nonportable `nul.sql` seed name on the shared filesystem; renaming it to `null-byte.sql` preserved its bytes and allowed replay. The verifier also initially failed compilation due to a missing `async` modifier; the same integration entrypoint passed after correction. These finite local results do not establish hosted workflow success, exhaustive coverage or a vulnerability-free runtime.

## Supply-chain evidence

From release 0.2.0, the release workflow generates:

1. Five self-contained platform archives, an npm tarball and a NuGet tool package.
2. An application CycloneDX JSON SBOM generated by the pinned .NET CycloneDX tool from the resolved NuGet graph, excluding dependencies classified as development-only. Some build-time transitive packages may remain; this is not a complete operating-system/container/native-library inventory.
3. A `SHA256SUMS` manifest covering the five archives, npm/NuGet packages and SBOM. Checksums detect mismatched bytes; an unauthenticated checksum download is not proof of publisher identity.
4. Requested GitHub build attestations for the archives, npm/NuGet package files, SBOM and checksum manifest. Verify the subject digest and repository/workflow identity; do not infer attestation success from workflow configuration. The attested NuGet subject is the GitHub asset, not a claim that registry signing preserves those bytes.
5. Requested npm provenance for a successful registry publication. NuGet trusted publishing exchanges GitHub OIDC for a short-lived API key; this authenticates publication and is not equivalent to npm provenance.

### Release execution boundary

The hardened workflow is manually dispatched from `main`, validates an existing version tag's ancestry against `origin/main`, and compiles release orchestration from the immutable main workflow revision. Tag application/build code runs only in read-only packaging. Attestation and publishing jobs have no source checkout; they consume explicit publication metadata and same-run immutable artifact IDs, rejecting digest mismatches. Publisher commands require the metadata context and do not discover a source tree or compile tag-source tooling.

The `target` release input defaults to `all`. Selecting `nuget` skips GitHub Release creation and npm publication, without removing common build, installation checks or attestation. NuGet explicitly requires successful preflight, packaging and attestation, and a non-canceled run; it tolerates the intentionally skipped GitHub Release job only in NuGet-only mode. In all-target mode it still requires successful GitHub Release publication. NuGet username reuse does not broaden a repository-specific trusted-publishing policy.

The live `release` environment now permits only the `main` branch; its former `v*` tag policy was removed. No independent-review barrier was added. The execution-boundary protections require this workflow revision on `main`; environment settings alone do not deploy workflow changes. An administrator can still deliberately change the reviewed workflow or environment restrictions.

Local release smoke used disposable Git repositories: lightweight and annotated main tags passed; off-main, missing, malformed, newline-suffixed and empty tags failed without producing a commit output. Checkout-free publication context loaded actual repository metadata, while missing context/credentials and path-like package metadata failed before network/publication. The locked Release solution build had zero warnings/errors; actionlint 1.7.12 passed all workflow files. A final nominal 10-second native campaign replayed 13 checked-in seeds and completed 8,943 runs in 11 seconds with no crash artifacts. No release workflow, attestation or registry publication was invoked by these checks.

Download and verify actual evidence for the version being installed:

```bash
gh release download v0.2.0 --repo codegiveness/postgresql-sharp-mcp --dir release-verification
```

On Linux, run `sha256sum --check SHA256SUMS` in that directory. On macOS, use `shasum -a 256 --check SHA256SUMS`; on Windows use `Get-FileHash -Algorithm SHA256` and compare each filename/digest. For an attested asset, use a current [GitHub CLI](https://cli.github.com/) with `gh attestation` support; older CLI versions do not provide this command:

```bash
gh attestation verify ./release-verification/postgresql-sharp-mcp-linux-x64.tar.gz --repo codegiveness/postgresql-sharp-mcp
```

A successful local package installation does not establish registry publication. Inspect all publication jobs and install the exact version from each registry before claiming availability. GitHub releases can succeed independently of npm token permissions or the target NuGet account's trusted-publishing policy.

## Badges and recognition

README workflow badges link to their actual workflows; Scorecard links to published findings; Best Practices displays the project's saved owner self-assessment, including its in-progress state; .NET names the implementation target; the security-policy badge links to the reporting policy. Local answer proposals do not update the hosted badge until an authorized owner reviews and saves them. None guarantees a vulnerability-free server. Registry-version badges are appropriate only after the corresponding registry package exists.

GitHub profile Achievements are separate from repository status badges. Useful issues, coherent PRs, substantive reviews and helpful answers may be relevant under GitHub's current rules, but configuration or activity counts do not guarantee an award. See [contribution guidance](../CONTRIBUTING.md#github-profile-recognition); do not manufacture activity.

### OpenSSF Best Practices enrollment

The public [project entry 15155](https://www.bestpractices.dev/en/projects/15155) was verified on 2026-10-02: enrolled, **19% in progress**, with no passing achievement. This corrects the earlier lack-of-enrollment assessment. The form was read-only in the available unauthenticated browser session; no answers were saved.

[`.bestpractices.json`](../.bestpractices.json) supplies 34 evidence-backed proposed answers, not certification or a claim that the hosted entry has changed. Public evidence is distinguished from the unmerged major-feature test-policy candidate; do not attest the latter as published before merge. Once the file is available on the public default branch, the entry owner can review automation proposals using **Save (and continue) 🤖**, following the badge application's [repository proposal instructions](https://github.com/ossf/best-practices-badge/blob/main/docs/bestpractices-json.md). Review every proposed answer before saving. Neither repository publication nor CI saves answers on the badge website.

| Evidence available | Criteria supported |
|---|---|
| [README](../README.md), [contribution guidance](../CONTRIBUTING.md), MIT license and public GitHub issues | Description, interaction, contribution requirements, external input/output documentation, English documentation and report archive/tracker |
| [Release tags](https://github.com/codegiveness/postgresql-sharp-mcp/releases), [changelog](../CHANGELOG.md) | Unique versions, semantic version format, release tags and human-readable release notes |
| [Security reporting policy](../SECURITY.md#reporting-a-vulnerability), enabled private-reporting API response | Public reporting process and confidential HTTPS reporting route |
| [Build settings](../Directory.Build.props), [verification instructions](../CONTRIBUTING.md#verify-behavior), [successful CI run](https://github.com/codegiveness/postgresql-sharp-mcp/actions/runs/36975816424) | Common build tools, automated suite, .NET invocation, CI, nullable checking and warnings-as-errors |
| [Successful CodeQL run](https://github.com/codegiveness/postgresql-sharp-mcp/actions/runs/36975816274), [scanner workflow](../.github/workflows/codeql.yml) | Common-vulnerability static-analysis rules and push/PR scan frequency |

Leave developer-knowledge attestations, historical response-time claims, branch-coverage percentages, cryptographic policy guarantees, release-specific pre-release analysis and absence-of-vulnerabilities/credentials claims unanswered until their evidence is established. Configuration or a successful current scan does not prove those criteria. The repository was created on the assessment date; activity does not supply months of maintenance or response history. Independent review also remains distinct from the enforced PR/check policy.

## Sources

- [GitHub secure use of Actions](https://docs.github.com/en/actions/reference/security/secure-use)
- [GitHub artifact attestation verification](https://docs.github.com/en/enterprise-cloud%40latest/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations#verifying-artifact-attestations-with-the-github-cli)
- [CodeQL supported languages](https://docs.github.com/en/code-security/concepts/code-scanning/codeql/codeql-code-scanning)
- [CodeQL build accuracy and generated sources](https://docs.github.com/en/code-security/reference/code-scanning/codeql/build-options-for-compiled-languages)
- [GitHub secret patterns and limits](https://docs.github.com/en/code-security/reference/secret-security/supported-secret-scanning-patterns)
- [GitHub generic-pattern enablement](https://docs.github.com/en/code-security/how-tos/secure-your-secrets/detect-secret-leaks/enabling-secret-scanning-for-generic-patterns)
- [GitHub validity checks and issuer requests](https://docs.github.com/en/code-security/concepts/secret-security/validity-checks)
- [OpenSSF Best Practices passing criteria](https://www.bestpractices.dev/en/criteria/0)
- [Gitleaks CLI and configuration](https://github.com/gitleaks/gitleaks)
- [Trivy image vulnerability scanning](https://github.com/aquasecurity/trivy)
- [OpenSSF Scorecard](https://github.com/ossf/scorecard)
- [CycloneDX .NET generator](https://github.com/CycloneDX/cyclonedx-dotnet)
- [npm provenance](https://docs.npmjs.com/generating-provenance-statements)
- [NuGet trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)
- [PostgreSQL read-only transactions](https://www.postgresql.org/docs/current/sql-set-transaction.html)
- [GitHub profile reference](https://docs.github.com/en/account-and-profile/reference/profile-reference#earning-achievements)
