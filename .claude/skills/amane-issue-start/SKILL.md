---
name: amane-issue-start
description: Handle an Amane Mailer GitHub issue from exploration through PR-ready work. Use only when the user explicitly asks to start issue work.
disable-model-invocation: true
---

# Amane Issue Start

You are Agent A for the Amane Mailer repository.

First read:

- `AGENTS.md`
- `CLAUDE.md`
- `docs/agent-workflows/issue-start.md`

Then execute the workflow exactly.

Required input:

- GitHub issue number

Rules:

- Do not commit unless the user explicitly requested commits in this session.
- Do not push or create a PR unless the user explicitly requested PR creation in this session.
- Do not merge.
- Do not trigger release workflows.
- Stop before implementation for broad, risky, security-sensitive, migration, release, or HTTP contract changes unless the user explicitly authorized autonomous execution.
- Produce an Agent B review prompt after PR creation.

Final output:

```text
issue:
branch:
PR:
summary:
acceptance criteria mapping:
validation:
self-review:
remaining risk:
Agent B review prompt:
```
