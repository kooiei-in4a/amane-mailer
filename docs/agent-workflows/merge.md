# Amane Mailer Merge Workflow

Use this workflow only when the human explicitly asks to merge an Amane Mailer PR in the current session.

## Inputs

- PR URL or number: required.
- Expected base branch: required when not obvious.
- Expected merge method: required when repository practice is unclear.

## Safety Gate

Do not merge unless all checks pass:

- The target PR is the intended PR.
- The PR targets the intended base branch.
- CI and required checks are green.
- Required review is satisfied.
- There are no unresolved review comments that block merge.
- The branch has no merge conflicts with the base branch.
- The PR diff is scoped to the issue or approved work.
- The PR body includes an issue close reference when applicable.
- Validation exceptions, if any, are documented in the PR.
- No release workflow, artifact publication, deployment, or workflow dispatch will be triggered unexpectedly.
- The merge method follows repository practice or explicit human instruction.

If any check fails, do not merge. Report the blocker and next action.

### Draft PR Handling

Draft status is a merge blocker unless the human explicitly asked to merge in the current session.

When the human explicitly asked to merge and every other safety gate passes:

1. Mark the PR ready for review.
2. Re-check mergeability, CI/checks, reviews, comments, diff scope, and side-effect risk.
3. Merge only if the re-check passes.

If the PR remains draft, cannot be marked ready, or any re-check fails, do not merge. Report the blocker and next action.

## Execution Gate

Before merging, report:

- PR.
- Base branch.
- Head branch.
- Merge method.
- Checks reviewed.
- Review state.
- Draft handling, when the PR was draft.
- Known residual risk.

Merge only after the safety gate is satisfied.

## Output

```text
PR:
Merge method:
Merge commit:
CI/checks:
Issue state:
Next cleanup action:
```
