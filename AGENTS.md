# Development guidance for coding agents

## Scope and authority

- Work only on the requested change. Preserve unrelated work and inspect existing patterns before editing.
- For completed, substantive repository changes, automatically commit only task-owned files, push the work branch and open or update an unmerged pull request before the final response. This is standing authorization for change publication; development builds, fixtures and workflow files do not authorize merging, pushing release tags, creating releases, publishing packages or changing repository settings. Those operations still require explicit user authorization.
- Never include credentials, real connection strings, private application identifiers, customer SQL or database results in code, logs, documentation, issue drafts or evidence. Use placeholder configuration and disposable synthetic data.

## Meaningful and traceable changes

- Establish the user-visible problem and acceptance criteria before implementation. Connect changes to those criteria, not activity counts.
- For a bug, capture a reproducible failure and demonstrate the same scenario after the fix. Prefer regression coverage for plausible consumer-visible errors rather than source-text, wiring or mock-echo assertions.
- Keep changes focused and reviewable. Useful issues and PRs record the problem, relevant alternatives, behavior, compatibility/security impact and observed evidence. Existing issue links are helpful, not mandatory for small corrections.
- Do not manufacture commits, split trivial changes into extra PRs, invent reviews, approve your own PR, trade stars/answers or create empty discussions for profile recognition. Attribute work truthfully.
- Run applicable local verification before committing, then inspect actual hosted PR checks and report their observed status. Include the problem, supported changes, verification and remaining blockers in the PR. Do not treat an open PR as deployed code or merge it automatically.
- Chat-only replies and investigations with no repository changes do not warrant commits or PRs. Update an existing task PR rather than create duplicate PRs, never make empty commits, and never include unrelated user changes.

## Implementation and evidence

- Preserve the `Core <- Tools <- App` dependency direction. Read affected callers and update the full contract when behavior changes.
- Keep all maintained implementation and automation in C#/.NET. npm JSON and workflow/container configuration may invoke external package/runner tools; do not reintroduce JavaScript/Python/Bash launchers or scripts. The npm CLI's Node prerequisite is installation-only, not the server runtime.
- Restore committed portable/per-RID NuGet locks in locked mode. Regenerate changed graphs deliberately and review actual versions/hashes; never disable locking to get a release green.
- Keep MCP stdout reserved for JSON-RPC and diagnostics on stderr. Respect PostgreSQL permissions and read-only transaction boundaries; a SQL lexer or static scan does not prove isolation.
- Use the verification entrypoints in [CONTRIBUTING.md](CONTRIBUTING.md). Exercise changed runtime behavior, not just compilation. For packaging changes, install built artifacts and run their actual entrypoints. Documentation-only changes require link and command review.
- Test only databases you own or are explicitly authorized to use. The .NET verifier's `integration` command creates a disposable Docker fixture; do not substitute an operator's database.
- State which commands/scenarios ran, observed results and verification gaps. Never infer hosted workflow success, registry publication, profile awards or repository protection from configuration alone.
- Update affected help, docs, release notes, dependency notices and callers. Remove throwaway scaffolding; do not commit generated artifacts or secrets.

## Automation and review

- Pin Actions to verified full commit SHAs with version comments. Use least-privilege job permissions and disable persisted checkout credentials. Publishing jobs may need narrowly scoped write permissions; validation jobs should remain read-only.
- Never execute untrusted pull-request code through privileged `pull_request_target` or `workflow_run` paths.
- Review dependency updates and verify relevant behavior. Dependabot does not authorize automatic merging or publication.
- Preserve the raw-SARIF fail-closed gate: baseline/candidate revisions must use the same scanner/query setup, and scan or extraction errors are not a clean result. Execute the comparator from trusted base source; review workflow changes separately.
- Secret-scanner exceptions must combine exact synthetic values and paths; historical exceptions also name exact commits. Never blanket-ignore test directories, baseline real leaks, publish secret-bearing reports, or use inline suppression to hide a finding.
- Container scans must examine the built image's OS and application packages. Do not replace them with Dockerfile-only scans, skip unfixed HIGH/CRITICAL findings, or silently disable a failed scanner.
- Release artifacts are reviewable outputs, not authority to publish. Confirm each authorized publication job and registry endpoint before claiming availability.
