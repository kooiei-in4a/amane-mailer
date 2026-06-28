# Amane Mailer Cleanup Workflow

Use this workflow only after an Amane Mailer PR has been merged and the human explicitly asks for cleanup in the current session.

## Inputs

- PR URL or number: required.
- Issue number: required when available.
- Feature branch: required.
- Base branch: default `main` unless the PR used another base.

## Safety Gate

Do not delete branches or change issue state unless:

- The PR is confirmed merged.
- The target branch is the feature branch for that PR.
- The target branch is not `main`, `master`, `develop`, `staging`, `release`, or any protected/shared branch.
- The working tree is clean.
- The current local branch is not the branch being deleted.
- No unpushed local work exists on the target branch.
- Remote branch deletion is explicitly authorized by the current human request.

If any safety check fails, stop and report.

## Cleanup Steps

1. Confirm PR merge state.
2. Confirm issue state.
3. Close or comment on the issue only if needed and authorized.
4. Switch to the base branch.
5. Pull the latest base branch.
6. Delete the local feature branch when safe.
7. Delete the remote feature branch only when safe and explicitly authorized.
8. Prune local remote-tracking branches when safe.
9. Report final status.

## Output

```text
PR:
Issue:
Base branch:
Feature branch:
Local branch cleanup:
Remote branch cleanup:
Issue cleanup:
Remaining risk:
```
