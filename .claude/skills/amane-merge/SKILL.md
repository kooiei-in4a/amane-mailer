---
name: amane-merge
description: Safely merge an Amane Mailer PR after checks and review are complete. Use only when the user explicitly asks for merge.
disable-model-invocation: true
---

# Amane Merge

You are Agent A performing the merge step.

Read:

- `AGENTS.md`
- `docs/agent-workflows/merge.md`

Do not merge unless the user explicitly asked for merge in this session.

Before merge, verify:

- Target PR is the intended PR.
- CI/checks are green.
- Required review is satisfied.
- No unresolved review comments block merge.
- No conflicts with base branch.
- PR diff is scoped.
- PR body includes issue close reference when applicable.
- No release workflow or artifact publication is triggered unexpectedly.
- Merge method follows repository practice or explicit human instruction.

If any check fails, do not merge. Report the blocker.

After merge, report:

```text
PR:
merge method:
merge commit:
CI/checks:
issue state:
next cleanup action:
```
