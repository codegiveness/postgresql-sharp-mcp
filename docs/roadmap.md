# Roadmap

This is the direction for the twelve months from 2026-10-11 to 2027-10-11, so that users and contributors can see what the project intends to do and not do. It is not a promise: the project has one maintainer ([governance](../GOVERNANCE.md)), the software is a 0.x release series, and dates depend on the maintainer's time. Items that need someone other than the maintainer are marked as such. The list is derived from gaps the project has documented itself in [docs/security-posture.md](security-posture.md), [docs/assurance-case.md](assurance-case.md) and the [OpenSSF Best Practices answers](security-posture.md#openssf-best-practices-enrollment).

## What the project intends to do

### Keep the supported surface current

- Keep working with the PostgreSQL majors the integration fixture covers (13–18, see [supported versions](../README.md#supported-postgresql-versions)) and add newly released majors to the CI matrix when the fixture can pull them by digest.
- Stay on a supported .NET 10 SDK and runtime. Dependabot proposes SDK, NuGet, Docker and Actions updates weekly; each is reviewed against release notes and the verifier before merging.
- Track the C# MCP SDK and Npgsql releases, and the MCP protocol revisions that the SDK implements, without changing existing tool names, parameters or environment variables in compatible releases.
- Release compatible fixes as patch versions and reserve minor versions for new or incompatible 0.x contracts ([versioning](../CONTRIBUTING.md#automation-and-releases)). Breaking changes are listed under upgrade notes in the [CHANGELOG](../CHANGELOG.md).

### Close the security gaps the project already knows about

- **Statement coverage above 90%.** The integration scenarios reach 89.41% of statements and 84.84% of branches (measured 2026-10-11; CI fails below 80% lines, see [Measure test coverage](../CONTRIBUTING.md#measure-test-coverage)). The least-covered file is `src/PostgreSqlMcp/Program.cs`: a scenario for `--help`, `--version` and an invalid `--log-level` would close most of the gap to 90%.
- **Independently reproduced release.** Every release asset except the per-run provenance bundle is now byte-identical across rebuilds of the same source. The next release is the first whose `.nupkg` and SBOM can be checked against an independent rebuild.
- **Release provenance visibility.** Releases from 0.5.0 carry `provenance.sigstore.json`, which is how Scorecard's Signed-Releases check sees the signatures. Each new release extends that coverage.
- **Assurance case upkeep.** Review and update the assurance case whenever the trust boundary, a default or a release path changes, and at least once a year.

### Sustain the project

- Keep issues, discussions and pull requests triaged, and security reports handled as described in [SECURITY.md](../SECURITY.md#how-reports-are-handled).
- Keep the documentation consistent with the released version; a documentation defect found in review is treated as a bug.
- Welcome a second maintainer and contributors from other organizations. This depends on others joining and cannot be scheduled; [governance](../GOVERNANCE.md#continuity-if-the-maintainer-is-unavailable) lists what would change.

## What the project does not plan to do

These follow from the documented [trust boundary](../SECURITY.md#trust-boundary) and design:

- **MCP-level authentication, per-tool authorization or tenant identity.** The client that launches the server is trusted; PostgreSQL roles are the authorization boundary.
- **Network transports.** The server speaks stdio only and opens no listener. Do not expose it through an unauthenticated bridge.
- **A SQL authorization parser or sandbox.** The SQL guard is a statement-boundary lexer. Isolation comes from read-only transactions, role grants and the optional database lock.
- **Storing or managing credentials.** The server reads them from the environment or an owner-protected file and does not persist them.
- **Other database engines and graphical interfaces.** The scope is PostgreSQL through MCP.

## How to influence the roadmap

Open an issue or a [discussion](https://github.com/codegiveness/postgresql-sharp-mcp/discussions) describing the problem and the trade-offs. A pull request with evidence is the most direct route. The maintainer decides what is accepted ([governance](../GOVERNANCE.md#how-decisions-are-made)).
