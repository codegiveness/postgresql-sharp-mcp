# Governance

This document says how decisions are made in postgresql-sharp-mcp, who holds which role, and what happens if the maintainer is unavailable. It describes the project as it is on 2026-10-11, not as it might become. The project has one maintainer; where that limits what it can promise, the text says so.

## Governance model

postgresql-sharp-mcp follows a **single-maintainer (benevolent dictator) model**. The maintainer, the GitHub user [`codegiveness`](https://github.com/codegiveness), owns the repository, makes the final decision on every change and releases the software. Anyone may propose a change, report a problem or review someone else's work; none of that creates authority over the project. The software is MIT-licensed ([LICENSE](LICENSE)), so anyone who disagrees with a decision can fork it.

The repository's settings enforce the maintainer's own process: `main` accepts changes only through pull requests with passing required checks, force pushes and deletion are blocked, and release tags are immutable ([git workflow](docs/git-workflow.md#repository-settings)). Those rules apply to the maintainer as well. They are not independent review: GitHub does not let an author approve their own pull request, so `main` requires zero approvals.

## How decisions are made

1. **Proposal.** Open an issue or a discussion, or send a pull request directly for a small correction. [CONTRIBUTING.md](CONTRIBUTING.md#issues-pull-requests-and-reviews) lists what a useful proposal contains. Hard-to-reverse or large changes need the user need and the trade-offs written down first.
2. **Discussion.** Comments, reviews and evidence are welcome from everyone. The maintainer weighs them on correctness, security, compatibility with the documented contracts (including the [trust boundary](SECURITY.md#trust-boundary)), maintenance cost and scope.
3. **Decision.** The maintainer accepts, requests changes to, or declines the proposal and records the reason on the issue or pull request. Merging is a maintainer decision ([git workflow](docs/git-workflow.md#7-merge)).
4. **Record.** User-visible behavior goes into [CHANGELOG.md](CHANGELOG.md); security-relevant decisions go into [SECURITY.md](SECURITY.md) or [docs/security-posture.md](docs/security-posture.md).

Disagreements are settled in the open, on the issue or pull request where they arose. If a discussion stalls, the maintainer decides and says why. A contributor who is still unhappy can keep a fork. Conduct complaints follow the [code of conduct](CODE_OF_CONDUCT.md#enforcement). That process currently ends with the maintainer, so a complaint about the maintainer's own conduct has no independent route inside the project; use [GitHub's abuse reporting](https://docs.github.com/en/communities/maintaining-your-safety-on-github/reporting-abuse-or-spam) for it.

Changes to this document are made like any other change: by pull request, decided by the maintainer.

## Roles and responsibilities

| Role | Who holds it today | Responsibilities and authority |
|---|---|---|
| **Maintainer** | `codegiveness` (the only holder) | Final say on scope, design and merges. Triages issues and discussions, reviews pull requests, enforces the code of conduct, handles private vulnerability reports ([SECURITY.md](SECURITY.md#reporting-a-vulnerability)), keeps dependencies and the build current, dispatches and verifies releases, approves each npm publication with 2FA, and owns the GitHub repository, the `@codegiveness` npm packages and the NuGet package. Administers repository settings and decides who else receives any of these permissions. |
| **Contributor** | Anyone who opens an issue, discussion or pull request | Follows [CONTRIBUTING.md](CONTRIBUTING.md) and the [code of conduct](CODE_OF_CONDUCT.md); states the problem, the behavior change, the evidence and the security impact; never includes credentials, real connection strings, customer SQL or database results. Contributions are under the [MIT license](LICENSE). No authority to merge or release. |
| **Reviewer** | Any contributor who comments on or reviews a pull request | Reports actionable correctness, security or maintainability findings with reasons and, where possible, a reproduction, following the [code review standards](CONTRIBUTING.md#code-review-standards). Reviews inform the maintainer's decision; they do not replace it. |
| **Security reporter** | Anyone using private vulnerability reporting | Reports privately with a sanitized reproduction against disposable data. Is credited when the report is resolved unless they ask not to be ([SECURITY.md](SECURITY.md#how-reports-are-handled)). |
| **Automation** | Dependabot, GitHub Actions workflows | Proposes dependency updates and runs checks. A bot has no authority to approve, merge, release or change settings; Dependabot pull requests are reviewed like any other ([CONTRIBUTING.md](CONTRIBUTING.md#automation-and-releases)). |
| **AI coding agent** | Used by the maintainer, as a tool | Works only inside the authority granted for the task, in its own worktree and branch, per [AGENTS.md](AGENTS.md) and the [What agents may do](docs/git-workflow.md#what-agents-may-do) table. Agent-written pull requests carry the `agent-authored` label and say so. The maintainer remains accountable for everything merged. Agents never push, merge, tag, release, publish or change settings without explicit authorization. |

`.github/CODEOWNERS` lists `@codegiveness` for every path. Code-owner review is not required, because that would make a single maintainer unable to merge ([repository settings](docs/git-workflow.md#not-recommended-now)).

### Becoming a maintainer

There is no quota and no automatic promotion. The maintainer may invite a contributor who has made sustained, substantive contributions and reviews, shown sound judgment on security-sensitive changes and followed the code of conduct. Before any escalated permission is granted (repository write or admin access, npm package maintainer, NuGet package owner, access to the `release` environment secrets), the invitee must have two-factor authentication on the relevant accounts, and the maintainer records the grant on the pull request or issue that proposes it, with the permission and scope named. The maintainer may remove a permission at any time. Adding a second maintainer is the single change that would improve every continuity limit below.

## Continuity if the maintainer is unavailable

**Status on 2026-10-11: the access-continuity requirement is not met, and the bus factor is 1.** Nobody else can currently use the access needed to continue the project. This section records what that means so that users can judge the risk, and what exactly would change it.

### What depends on the maintainer alone

| Needed within a week of confirmed loss of the maintainer | Who can do it today |
|---|---|
| Create and close issues, label and triage | Any GitHub user can open issues and comment; only the maintainer (admin) can close others' issues, label or moderate |
| Accept proposed changes (merge to `main`) | Only the maintainer. The `main` ruleset requires a pull request, up-to-date required checks and resolved conversations, and has no bypass actors; changing it needs repository admin |
| Release a version | Only the maintainer. The [release workflow](.github/workflows/release.yml) is dispatched from `main` by someone with write access and uses the repository's `release` environment |
| Publish to npm | Only the maintainer. Every public npm version needs a human 2FA approval on the maintainer's npm account ([release procedure](CONTRIBUTING.md#approve-each-npm-publication-on-windows)); the packages' trusted-publisher settings and 2FA-only publishing access are also owner-controlled |
| Publish to NuGet | Only the maintainer. The trusted-publishing policy for `codegiveness.postgresql-sharp-mcp` is tied to the policy creator's NuGet profile (`NUGET_USERNAME` in the `release` environment) |
| Handle private vulnerability reports and code-of-conduct reports | Only the maintainer; both routes end with the maintainer ([SECURITY.md](SECURITY.md#reporting-a-vulnerability), [code of conduct](CODE_OF_CONDUCT.md#enforcement)) |

Recovery material for the maintainer's own accounts (2FA recovery codes, hardware keys, password manager) is the maintainer's personal responsibility. This repository does not hold it and cannot show whether a trusted person could use it.

### What reduces the damage

- **The project can be forked and continued.** The source, history, CI definitions and release procedure are public and MIT-licensed. Nothing needed to build or verify the software is private.
- **No credentials live in the repository.** GitHub OIDC replaces long-lived npm and NuGet tokens; the remaining secret is `NUGET_USERNAME`, a username rather than a key ([CONTRIBUTING.md](CONTRIBUTING.md#independent-publisher-setup)). The secret scanner and Gitleaks check history.
- **Releases are verifiable without the maintainer.** Existing artifacts remain verifiable through their SHA-256 manifest and GitHub build attestations, and the release tool is built from the repository ([supply-chain evidence](docs/security-posture.md#supply-chain-evidence)). Already-published versions stay installable from GitHub, npm and NuGet.
- **The release procedure is written down**, so a successor with the right permissions can follow it ([git workflow](docs/git-workflow.md#tags-and-releases), [CONTRIBUTING.md](CONTRIBUTING.md#independent-publisher-setup)).

These measures preserve users' ability to verify and fork. They do not let anyone merge, release or publish on the maintainer's behalf, so they do not satisfy the one-week continuity requirement.

### What would change the status

The OpenSSF criterion requires that issues can be created and closed, changes accepted and versions released within a week of confirmed loss of any one person. It names two ways to do that: someone else holds the necessary keys, passwords and legal rights, or, for a project run by one individual, the keys sit in a lockbox and a will grants the needed legal rights. Documentation alone meets neither. Either route below would change `access_continuity` to Met.

**Route 1: a second maintainer.** This also changes the bus factor.

1. A second person has **admin** on `codegiveness/postgresql-sharp-mcp` and 2FA on their GitHub account. They can merge, edit the ruleset, dispatch `release.yml` and manage the `release` environment.
2. The same person is a **maintainer of the six `@codegiveness/...` npm packages** (2FA required). They can approve staged versions and edit the trusted-publisher configuration.
3. The same person is a **co-owner of the NuGet package**, with their own trusted-publishing policy for `release.yml` and the `release` environment, and their own `NUGET_USERNAME`.
4. The handover is tested. The second person completes one real issue-to-release cycle, or a dry run of every step the maintainer can verify. The maintainer records the date and result in this document.

**Route 2: lockbox and written authority, for a single maintainer.** This does not change the bus factor.

1. A named trusted person has sealed access to the recovery material for the maintainer's GitHub, npm and NuGet accounts. That material includes the passwords and the 2FA recovery codes or a backup security key, and it must be enough to complete the [release procedure](CONTRIBUTING.md#independent-publisher-setup), including npm's 2FA approval. A password manager's emergency-access feature or a sealed physical copy both work. Credentials never go into this repository, an issue or a pull request.
2. A will or signed letter gives that person the right to use the material to continue the project or hand it to a new maintainer, and says when they may do so.
3. Optionally, GitHub's [account successor](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/repository-access-and-collaboration/maintaining-ownership-continuity-of-your-personal-accounts-repositories) is set as a backstop. It only lets the successor archive or transfer public repositories, after GitHub's waiting period, so it covers neither npm, NuGet, nor incapacity short of death. It does not replace step 1.
4. The maintainer records in this document that the arrangement exists and when the material was last checked to be current. The trusted person's private details appear only if they agree. The check is repeated at least yearly and whenever a password, recovery code or 2FA device changes.

When either route is complete, update this section and the OpenSSF Best Practices answer for `access_continuity`; route 1 also updates `bus_factor` ([security posture](docs/security-posture.md#openssf-best-practices-enrollment)). Until then both stay Unmet.

## Related documents

- [CONTRIBUTING.md](CONTRIBUTING.md): contribution process, coding standards, code review standards, build and verification.
- [SECURITY.md](SECURITY.md): trust boundary, reporting and handling vulnerabilities.
- [docs/roadmap.md](docs/roadmap.md): direction for the next twelve months.
- [docs/security-posture.md](docs/security-posture.md): controls, Scorecard and OpenSSF Best Practices status.
- [docs/git-workflow.md](docs/git-workflow.md): branches, pull requests, merging and releases.
