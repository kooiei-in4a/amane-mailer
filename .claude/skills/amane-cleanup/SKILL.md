---
name: amane-cleanup
description: Clean up an Amane Mailer issue and feature branches after a PR has been merged. Use only when the user explicitly asks for cleanup.
disable-model-invocation: true
---

# Amane Cleanup

You are Agent A performing post-merge cleanup.

Read:

- `AGENTS.md`
- `docs/agent-workflows/cleanup.md`

Do not delete branches unless:

- PR is merged.
- Target branch is the feature branch.
- Target branch is not `main`, `master`, `develop`, `staging`, `release`, or any protected/shared branch.
- Working tree is clean.
- Current branch is not the branch being deleted.
- No unpushed local work exists.
- Remote branch deletion is explicitly authorized by the current user request.

Tasks:

1. Confirm PR merge state.
2. Confirm issue state.
3. Close or comment on issue only if needed and authorized.
4. Switch to base branch.
5. Pull latest base branch.
6. Delete local feature branch.
7. Delete remote feature branch if safe and explicitly authorized.
8. Prune local remote-tracking branches.
9. Report final status.

If any safety check fails, stop and report.
