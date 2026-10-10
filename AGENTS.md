# Development guidance for coding agents

## Scope and authority

- Work only on the requested change. Preserve unrelated work and inspect existing patterns before editing.
- Development builds, disposable fixtures and artifact installation do not authorize remote activity. Pushing commits/tags, opening issues/PRs, creating releases, publishing packages or changing repository settings require explicit user authorization. Workflow files do not grant that authority.
- Never include credentials, real connection strings, private application identifiers, customer SQL or database results in code, logs, documentation, issue drafts or evidence. Use placeholder configuration and disposable synthetic data.

## Git workflow

Follow [docs/git-workflow.md](docs/git-workflow.md) for branches, worktrees, commits, pull requests, merging, releases and branch cleanup. Its [What agents may do](docs/git-workflow.md#what-agents-may-do) table is binding. In short:

- Do each task in its own worktree on a new `<type>/<description>` branch from `origin/main`. Never switch the branch of, or edit files in, a working tree you did not create for the task.
- On the VMware shared folder, pass `git -c safe.directory=<path>` per command; do not change global git config.
- A task's request may authorize pushing its own branch, opening a draft PR or opening an issue. Merging, deleting any branch, creating/moving/deleting tags, dispatching releases and changing settings, labels, rulesets or branch protection each need a separate explicit maintainer OK.
- Never push or force-push `main`, and never force-push a branch you do not own; use `--force-with-lease` on your own task branch only.

## Meaningful and traceable changes

- Establish the user-visible problem and acceptance criteria before implementation. Connect changes to those criteria, not activity counts.
- For a bug, capture a reproducible failure and demonstrate the same scenario after the fix. Prefer regression coverage for plausible consumer-visible errors rather than source-text, wiring or mock-echo assertions.
- Keep changes focused and reviewable. Useful issues and PRs record the problem, relevant alternatives, behavior, compatibility/security impact and observed evidence. Existing issue links are helpful, not mandatory for small corrections.
- Do not manufacture commits, split trivial changes into extra PRs, invent reviews, approve your own PR, trade stars/answers or create empty discussions for profile recognition. Attribute work truthfully.

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

- Pin Actions to verified full commit SHAs with version comments. Use least-privilege job permissions and disable persisted checkout credentials. Publishing jobs may need narrowly scoped write permissions; validation jobs should remain read-only. The one exception is CodeQL's `pull-request-upload` job: for same-repository PRs only, it may hold `security-events: write` to upload candidate SARIF that has already been produced. It must not build or execute PR code.
- Never execute untrusted pull-request code through privileged `pull_request_target` or `workflow_run` paths.
- Review dependency updates and verify relevant behavior. Dependabot does not authorize automatic merging or publication.
- Preserve the raw-SARIF fail-closed gate: baseline/candidate revisions must use the same scanner/query setup, and scan or extraction errors are not a clean result. Execute the comparator from trusted base source; review workflow changes separately.
- Secret-scanner exceptions must combine exact synthetic values and paths; historical exceptions also name exact commits. Never blanket-ignore test directories, baseline real leaks, publish secret-bearing reports, or use inline suppression to hide a finding.
- Container scans must examine the built image's OS and application packages. Do not replace them with Dockerfile-only scans, skip unfixed HIGH/CRITICAL findings, or silently disable a failed scanner.
- Release artifacts are reviewable outputs, not authority to publish. Confirm each authorized publication job and registry endpoint before claiming availability.
