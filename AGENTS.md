# Development guidance for coding agents

## Scope and authority

- Work only on the requested change. Preserve unrelated work and inspect existing patterns before editing.
- Development builds, disposable fixtures and artifact installation do not authorize remote activity. Pushing commits/tags, opening issues/PRs, creating releases, publishing packages or changing repository settings require explicit user authorization. Workflow files do not grant that authority.
- Never include credentials, real connection strings, private application identifiers, customer SQL or database results in code, logs, documentation, issue drafts or evidence. Use placeholder configuration and disposable synthetic data.

## Meaningful and traceable changes

- Establish the user-visible problem and acceptance criteria before implementation. Connect changes to those criteria, not activity counts.
- For a bug, capture a reproducible failure and demonstrate the same scenario after the fix. Prefer regression coverage for plausible consumer-visible errors rather than source-text, wiring or mock-echo assertions.
- Keep changes focused and reviewable. Useful issues and PRs record the problem, relevant alternatives, behavior, compatibility/security impact and observed evidence. Existing issue links are helpful, not mandatory for small corrections.
- Do not manufacture commits, split trivial changes into extra PRs, invent reviews, approve your own PR, trade stars/answers or create empty discussions for profile recognition. Attribute work truthfully.

## Implementation and evidence

- Preserve the `Core <- Tools <- App` dependency direction. Read affected callers and update the full contract when behavior changes.
- Keep MCP stdout reserved for JSON-RPC and diagnostics on stderr. Respect PostgreSQL permissions and read-only transaction boundaries; a SQL lexer or static scan does not prove isolation.
- Use the verification entrypoints in [CONTRIBUTING.md](CONTRIBUTING.md). Exercise changed runtime behavior, not just compilation. For packaging changes, install built artifacts and run their actual entrypoints. Documentation-only changes require link and command review.
- Test only databases you own or are explicitly authorized to use. `scripts/verify.sh` creates a disposable Docker fixture; do not substitute an operator's database.
- State which commands/scenarios ran, observed results and verification gaps. Never infer hosted workflow success, registry publication, profile awards or repository protection from configuration alone.
- Update affected help, docs, release notes, dependency notices and callers. Remove throwaway scaffolding; do not commit generated artifacts or secrets.

## Automation and review

- Pin Actions to verified full commit SHAs with version comments. Use least-privilege job permissions and disable persisted checkout credentials. Publishing jobs may need narrowly scoped write permissions; validation jobs should remain read-only.
- Never execute untrusted pull-request code through privileged `pull_request_target` or `workflow_run` paths.
- Review dependency updates and verify relevant behavior. Dependabot does not authorize automatic merging or publication.
- Release artifacts are reviewable outputs, not authority to publish. Confirm each authorized publication job and registry endpoint before claiming availability.
