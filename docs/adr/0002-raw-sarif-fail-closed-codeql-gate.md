# 0002: CodeQL pull-request gate compares raw SARIF and fails closed

## Status

Accepted

## Context

OpenSSF Scorecard's SAST check and reviewers need a code-scanning result on pull-request heads, but PR code must never run with a write token or privileged `pull_request_target`/`workflow_run` triggers. A gate that trusts the scanner's status alone would pass when extraction silently failed.

## Decision

- On pull requests, [`codeql.yml`](../../.github/workflows/codeql.yml) scans the base and the candidate in the read-only `pull-request-scans` job with the same scanner and query setup (`security-extended`), and retains the raw SARIF.
- The `pull-request-analysis` job checks out the **trusted base** and runs the SARIF comparator (`tools/PostgreSqlMcp.Verify`, [`SarifGate.cs`](../../tools/PostgreSqlMcp.Verify/SarifGate.cs)). It rejects new high/critical findings (CVSS >= 7) or error-level findings, differing tool inventories, and missing, malformed or unsuccessful reports. Scan or extraction errors are never a clean result.
- The `pull-request-upload` job may hold `security-events: write` only for same-repository PRs and only to upload the SARIF already produced; it builds and executes nothing from the PR.

## Alternatives considered

- **Rely on CodeQL's own check status**: does not show missing or empty extraction as a failure.
- **`pull_request_target` or `workflow_run` scans**: would execute untrusted PR code in a privileged context, which [CLAUDE.md](../../CLAUDE.md) forbids.
- **No PR scan, main/scheduled only**: PR heads would get no code-scanning check run.

## Consequences

- Workflow changes remain a review item: the gate runs trusted base code but a PR can still change the workflow files; review them separately.
- The comparator has an offline regression command (`sarif-regressions`) covering severity escalation, shifted fingerprints/locations, duplicates, incomplete reports and unsafe diagnostic text.

## Evidence

- Commit `dc297e9` (2026-10-10): "ci: upload candidate CodeQL SARIF for same-repository PRs (#29)"; commit `34cf9da`: SARIF rejection reasons asserted by exact exception type and message (#57).
- CONTRIBUTING "Automation and releases" (CodeQL bullet) and the "Verify behavior" SARIF section; [CLAUDE.md](../../CLAUDE.md) "Automation and review".
- Not verified here: hosted behavior of the gate beyond what the recorded runs show.
