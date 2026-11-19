# 0001: Releases are manual-dispatch only; npm is staged and approved

## Status

Accepted

## Context

Publishing packages is irreversible: a published npm or NuGet version is immutable. A tag push or a merge must therefore never publish by itself, and a compromised workflow token must not be able to make a package installable without a human decision. The first all-target run for 0.3.2 failed at Actions npm publishing (HTTP 404) and at NuGet OIDC (HTTP 401, no matching policy), and registry publication had to be recovered manually under owner authorization ([docs/security-posture.md](../security-posture.md), [CHANGELOG 0.3.2](../../CHANGELOG.md)).

## Decision

- [`release.yml`](../../.github/workflows/release.yml) triggers only on `workflow_dispatch` with an existing `v<version>` tag and a target (`all`, `github`, `npm`, `nuget`). The preflight job runs only for `refs/heads/main` and rejects tags not reachable from `origin/main`. Pushing a tag publishes nothing.
- Publishing jobs run in the `release` environment, consume immutable artifact IDs and never compile or run tag-source release tooling.
- npm is staged, not published: the exact verified tarball is staged through package-specific GitHub OIDC (no `NPM_TOKEN`, no token fallback) and a maintainer approves each publication on Windows with 2FA. NuGet uses its own OIDC policy with `NUGET_USERNAME`.
- Re-runs never replace published bytes: GitHub assets upload without `--clobber`, and an existing asset or NuGet version must match the built file's SHA-256 (commit `a3f8b71`; [CHANGELOG 0.5.0](../../CHANGELOG.md), "Release re-runs no longer replace published bytes").

## Alternatives considered

- **Release on tag push**: a stolen token or a mistaken tag would publish immediately.
- **Direct npm publish from Actions**: leaves no human check between a workflow run and an installable version.
- **Long-lived registry tokens**: replaced by short-lived OIDC exchange; the [CHANGELOG 0.3.3](../../CHANGELOG.md) entry replaces bypass-2FA token recommendations with package-specific npm trust.

## Consequences

- Every release needs a maintainer to dispatch and, for npm, approve on Windows; a release cannot complete unattended.
- The staged-npm and independent-publisher behavior is documented under CONTRIBUTING's "Dispatch the exact existing release" and "Approve each npm publication on Windows".
- Hosted staging behavior is only as verified as the recorded runs; see [docs/security-posture.md](../security-posture.md).

## Evidence

- Commit `45392ca` (2026-10-10): "fix: stage npm releases for Windows approval and separate registry targets (#26)".
- Commit `8cd3166` (2026-10-10): publish verified SLSA provenance bundle with GitHub Releases (#55); commit `a3f8b71` (2026-10-11): stop release re-runs from replacing published assets and NuGet versions (#56).
- [CHANGELOG 0.3.3](../../CHANGELOG.md): "Prepare locally verified independent publisher changes ... exact-tarball npm staging through stage-only GitHub OIDC (no `NPM_TOKEN`/token fallback or direct publication) ... a required Windows human 2FA approval for every public npm publish."
- `release.yml` header: `on: workflow_dispatch`, `permissions: {}`, concurrency group per tag.
