---
name: amane-review-fix
description: Use when explicitly asked to triage third-party review feedback and apply justified fixes to an Amane Mailer PR.
---

# Amane Review Fix

Read and follow:

- `AGENTS.md`
- `docs/agent-workflows/review-fix.md`

Act as Agent A responding to review feedback.

Do not blindly apply reviewer suggestions. For each finding, classify correctness, scope, timing, and validation impact before editing.

Do not commit, push, create PRs, merge, delete branches, publish artifacts, or trigger release workflows unless the current user request explicitly authorizes that action.

Propose follow-up issue drafts when useful, but do not create GitHub issues unless the current user request explicitly authorizes issue creation.

Final output must include:

- Review triage.
- Changes made.
- Findings declined.
- Follow-up issue proposals.
- Validation.
- PR comment draft.
- Remaining risk.
