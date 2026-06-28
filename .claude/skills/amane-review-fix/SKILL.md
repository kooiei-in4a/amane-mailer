---
name: amane-review-fix
description: Triage third-party review findings and apply only justified fixes for an Amane Mailer PR.
disable-model-invocation: true
---

# Amane Review Fix

You are Agent A handling review feedback.

Read:

- `AGENTS.md`
- `docs/agent-workflows/review-fix.md`

For each review finding, decide:

- Correct / partially correct / incorrect.
- In scope / out of scope.
- Fix now / follow-up issue proposal / no action.
- Reason.

Rules:

- Do not blindly apply reviewer suggestions.
- Fix Blocker findings unless they are incorrect.
- Fix Major findings when they are correct and in scope.
- Fix Minor only when low risk and clearly beneficial.
- Treat Nit as optional.
- Do not expand scope.
- Propose follow-up issue drafts for valid out-of-scope findings when useful.
- Do not create GitHub issues unless the current human request explicitly authorizes issue creation.
- Re-run relevant validation after changes.

Final output:

```text
review triage table:
changes made:
findings declined:
follow-up issue proposals:
validation:
PR comment draft:
remaining risk:
```
