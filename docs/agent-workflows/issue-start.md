# Amane Mailer Issue Start Workflow

Use this workflow when handling a GitHub issue from investigation through PR-ready work.

## Inputs

- Issue number: required.
- Base branch: default `main` unless the issue or human specifies otherwise.
- Feature branch: derive from the issue number and a short slug, using the active tool's branch convention when applicable.
- Agent role: implementation owner.

## Hard Gates

Do not proceed to the next phase until the current phase gate is satisfied.

### 1. Exploration Gate

Confirm and report:

- Issue title, body, comments, and checklist.
- Acceptance criteria.
- Non-goals and out-of-scope items.
- Related ADRs under `docs/adr/`.
- Relevant runtime, Contracts, tests, OpenAPI, docs, scripts, or infra files.
- Risk class:
  - Normal.
  - HTTP contract.
  - Native AOT / trimming.
  - Security / PII.
  - Admin UI.
  - Provider error handling.
  - Release / CI / deployment.
  - Data migration.

Stop and ask before implementation if:

- Acceptance criteria are missing or contradictory.
- The fix requires product or specification judgment.
- The fix touches security, authentication, authorization, secrets, PII, release workflows, deployment, or data migration and the issue does not clearly authorize it.
- The issue scope would require broad refactoring.

If ambiguity is non-blocking, document the assumption and continue.

### 2. Plan Gate

Before editing, output:

- Implementation plan.
- Target files.
- Tests to add or update.
- Validation commands.
- Explicit out-of-scope items.
- Expected PR summary.

For broad, risky, security-sensitive, release, migration, or HTTP contract changes, stop after the plan unless the human explicitly asked for autonomous execution.

### 3. Implementation Gate

Implement the smallest correct diff.

Rules:

- Preserve existing architecture, naming, test style, and docs structure.
- Do not introduce unrelated refactors, formatting churn, or dependency updates.
- Do not commit real tenant files, tokens, ACS connection strings, private `.env`, SQLite DBs, backups, or infrastructure secrets.
- Do not expose recipient email, subject, body, reply-to, metadata values, provider raw errors, tokens, secret-bearing URLs, or connection strings.
- Keep Native AOT and trimming compatibility.
- Keep HTTP Contracts, runtime behavior, OpenAPI, and tests synchronized when changing HTTP contracts.

### 4. Validation Gate

Run the smallest relevant check first, then broaden before PR.

Default validation:

```powershell
dotnet restore Amane.Mailer.slnx --locked-mode
dotnet build Amane.Mailer.slnx -c Release --no-restore
dotnet test Amane.Mailer.slnx -c Release --no-build --verbosity minimal
```

Run OpenAPI validation when OpenAPI changes:

```powershell
node scripts/validate-openapi.mjs docs/api/openapi.yaml
```

Run Native AOT publish smoke when runtime, serialization, dependencies, or trimming-sensitive code changes:

```powershell
dotnet publish src/Amane.Mailer/Amane.Mailer.csproj `
  -c Release -r linux-x64 --self-contained --no-restore `
  -o artifacts/publish/aot-linux-x64 `
  /p:PublishAot=true /p:IlcTreatWarningsAsErrors=true
```

If a validation command cannot be run, record:

- Command.
- Reason it was not run.
- Replacement check, if any.
- Residual risk.

### 5. Self-Review Gate

Review the diff as a reviewer.

Check:

- Acceptance criteria satisfied.
- Scoped diff.
- Meaningful tests.
- No unrelated refactor.
- No debug artifacts.
- No secrets or PII leakage.
- No HTTP contract drift.
- No Native AOT or trimming risk.
- No Admin UI XSS or exposure regression.
- No provider raw error persistence or display.
- No release or CI weakening.

Output:

```text
Self-review:
- Pass:
- Issues found:
- Fixes applied:
- Remaining risk:
```

### 6. PR Gate

Only create a PR if the current human request explicitly authorizes PR creation and:

- Acceptance criteria are mapped to implementation.
- Self-review issues are resolved or explicitly documented.
- Validation has passed or exceptions are documented.
- Diff is scoped.
- No prohibited files or secrets are included.

PR body must include:

- `Closes #<issue>`.
- Summary.
- Acceptance criteria mapping.
- Validation.
- Security and privacy.
- Notes / known limitations.
- Review focus.

After PR creation, output the Agent B review prompt.
