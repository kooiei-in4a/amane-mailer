# Amane Mailer Review Fix Workflow

Use this workflow when Agent A is responding to third-party PR review findings.

## Inputs

- PR URL or number: required.
- Review findings: required.
- Related issue number and acceptance criteria: required when available.

## Triage Gate

Before editing, triage every review finding.

For each finding, decide:

- Correct, partially correct, or incorrect.
- In scope or out of scope.
- Fix now, follow-up issue, or no action.
- Reason.
- Validation required after the decision.

Fix rules:

- Fix Blocker findings unless they are incorrect.
- Fix Major findings when they are correct and in scope.
- Fix Minor findings only when low risk and clearly beneficial.
- Treat Nit findings as optional.
- Do not blindly apply reviewer suggestions.
- Do not expand the issue scope.
- Create or propose follow-up issues for valid out-of-scope findings.

Stop and ask before implementation if a finding requires product or specification judgment, security posture changes, release process changes, data migration, or HTTP contract changes beyond the accepted issue scope.

## Plan Gate

Before editing, output:

- Triage table.
- Planned fixes.
- Findings declined and why.
- Follow-up issue candidates.
- Validation commands.

## Implementation Gate

Apply only justified fixes.

Rules:

- Keep fixes minimal, scoped, and justified by the review finding.
- Avoid unrelated cleanup.
- Preserve Native AOT, trimming, security, PII, provider error, Admin UI, and HTTP contract constraints from `AGENTS.md`.
- Update or add tests when behavior changes.
- Update docs only when the review finding requires it.

## Validation Gate

Run relevant validation after changes. Start focused, then broaden as risk requires.

Record any command that could not be run, why it could not be run, any replacement check, and residual risk.

## Output

```text
Review triage:

Changes made:

Findings declined:

Follow-up issues:

Validation:

PR comment draft:

Remaining risk:
```
