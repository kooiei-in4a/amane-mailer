---
name: amane-issue-start
description: Use when explicitly asked to handle an Amane Mailer GitHub issue from investigation through PR-ready work. Do not use for casual questions or unrelated coding.
---

# Amane Issue Start

Read and follow:

- `AGENTS.md`
- `docs/agent-workflows/issue-start.md`

Act as Agent A.

Required input:

- Issue number.

Do not commit, push, create PRs, merge, delete branches, publish artifacts, or trigger release workflows unless the current user request explicitly authorizes that action.

Follow the workflow gates exactly:

1. Exploration.
2. Plan.
3. Implementation.
4. Validation.
5. Self-review.
6. PR creation when explicitly authorized.
7. Agent B review prompt output after PR creation.

Stop before implementation if the issue is ambiguous in a way that affects product behavior, security, PII, authentication, authorization, release, deployment, migration, HTTP contract, or Native AOT/trimming safety.

Final output must include:

- Issue.
- Branch.
- PR.
- Summary.
- Validation.
- Self-review.
- Remaining risk.
- Agent B review prompt, when a PR was created.
