@AGENTS.md

## Claude Code

This file is a Claude Code entry point for the project-wide guidance in
`AGENTS.md`.

- Use plan mode for broad, risky, security-sensitive, release, migration, or
  HTTP contract changes.
- Treat the repository docs as guidance and local Claude Code settings as the
  source for tool permissions.

## Project skills

Use these explicit project skills for repeatable AI-assisted workflows:

- `/amane-issue-start`
- `/amane-pr-review`
- `/amane-review-fix`
- `/amane-merge`
- `/amane-cleanup`

For broad, risky, security-sensitive, release, migration, or HTTP contract
changes, use plan mode and stop before implementation unless the maintainer has
explicitly authorized the work in the current session.
