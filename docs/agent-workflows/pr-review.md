# Amane Mailer PR Review Workflow

Use this workflow when acting as Agent B, an independent reviewer of an Amane Mailer PR.

## Inputs

- PR URL or number: required.
- Related issue number: required when available.
- Agent A summary, validation notes, and self-review: required when available.

## Review Rules

- Review only. Do not edit files, stage changes, commit, push, merge, or trigger workflows.
- Start from the issue acceptance criteria and PR diff.
- Treat `AGENTS.md` as the project policy baseline.
- Prefer concrete findings with file paths, line references, evidence, and impact.
- Do not request unrelated improvements in the PR unless they block correctness or safety.

## Context Gate

Before reporting findings, inspect:

- Issue title, body, checklist, and relevant comments.
- PR title, body, commits, changed files, and CI/check status.
- Related ADRs under `docs/adr/`.
- Relevant tests and public docs.
- Any contract, release, PII, provider error, Admin UI, migration, or Native AOT risk.

If context is unavailable, state the missing context and classify any resulting risk.

## Finding Classification

Classify every finding as one of:

- Blocker: must be fixed before merge.
- Major: likely correctness, security, privacy, contract, release, or data risk.
- Minor: worthwhile fix in this PR if low risk.
- Nit: style or clarity only.
- Question: needs clarification before deciding.
- Out-of-scope: valid concern but not required for this PR.

For each finding, output:

- Severity.
- File / area.
- Content.
- Evidence.
- Impact.
- Recommended action.
- Whether it must be fixed in this PR.

## Review Focus

Check:

- Acceptance criteria coverage.
- Scoped diff.
- Meaningful tests and validation.
- Secrets and PII safety.
- Provider error sanitization.
- Native AOT and trimming safety.
- HTTP contract synchronization across Contracts, runtime, OpenAPI, tests, and docs.
- Admin UI XSS-safe rendering and exposure assumptions.
- Release / CI safety, including SHA-pinned actions when workflows change.
- Migrations, data compatibility, and rollback considerations when persistence changes.

## Output

Lead with findings ordered by severity. If there are no findings, explicitly say `No findings`.

Use this shape:

```text
Findings:
- [Severity] file:line - title
  Evidence:
  Impact:
  Recommendation:
  Must fix in this PR:

Open questions:

Validation reviewed:

Residual risk:
```
