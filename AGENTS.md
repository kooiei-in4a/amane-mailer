# Amane Mailer Agent Guide

## Project

- Amane Mailer is an ASP.NET Core / Native AOT mail delivery microservice.
- Runtime: `src/Amane.Mailer` — SQLite persistence, background worker, ACS/Mailpit delivery.
- Public contracts package: `src/Amane.Mailer.Contracts` — DTOs, error codes, payload hash helpers.
- Tests: `tests/Amane.Mailer.Tests`, `tests/Amane.Mailer.Contracts.Tests`
- Docs: `docs/` — service spec, ADRs, OpenAPI, runbooks.
- .NET SDK: see `global.json` (currently 10.0.301).

For human-oriented setup and runbooks, start with [README.md](README.md) and [CONTRIBUTING.md](CONTRIBUTING.md).

## Work rules

- Start from the GitHub issue and its acceptance criteria. The checklist is the definition of done.
- Keep the diff scoped to one issue or one behavior. No unrelated refactors, formatting churn, or dependencies.
- Read nearby tests and relevant ADRs under `docs/adr/` before editing.
- Add or update focused tests for behavior changes.
- Do not commit unless the human explicitly asks.
- Never push, force-push, publish artifacts, or trigger release workflows without explicit human instruction.
- Do not create extra memory, progress, or agent-notes files unless the human explicitly asks.

P1 issues use `[P1]` in the issue title, not a GitHub label. Search by title or issue number.

## Validation

Run the smallest useful check first, then broaden before finishing.

```powershell
# Default loop (matches README / CI)
dotnet restore Amane.Mailer.slnx --locked-mode
dotnet build Amane.Mailer.slnx -c Release --no-restore
dotnet test Amane.Mailer.slnx -c Release --no-build --verbosity minimal

# Focused test
dotnet test Amane.Mailer.slnx -c Release --no-build --filter "FullyQualifiedName~MailRequestApiTests"

# OpenAPI structural validation
node scripts/validate-openapi.mjs docs/api/openapi.yaml

# Native AOT publish smoke — required when runtime, dependencies, or serialization changes
dotnet publish src/Amane.Mailer/Amane.Mailer.csproj `
  -c Release -r linux-x64 --self-contained --no-restore `
  -o artifacts/publish/aot-linux-x64 `
  /p:PublishAot=true /p:IlcTreatWarningsAsErrors=true
```

On Linux/macOS, replace backticks with line continuations as needed. CI also runs `artifacts/publish/aot-linux-x64/Amane.Mailer --help` after publish.

## Hard constraints

### Secrets and PII

- Do not commit real tenant JSON, ACS connection strings, bearer tokens, private `.env` files, SQLite databases, backups, or deploy secrets.
- Use placeholders (`replace-with-*`, `local-mail-service-token`) in examples only.
- Do not log or expose recipient email, subject, body, reply-to, metadata values, provider raw errors, tokens, URLs with query secrets, or connection strings.

### Native AOT and trimming

Native AOT and full trimming are mandatory (`PublishAot`, `IsTrimmable`, `TrimMode=full` in `Amane.Mailer.csproj`).

- Avoid reflection-based JSON serialization, `dynamic`, reflection-based DI, and AOT-hostile dependencies.
- Use source-generated `System.Text.Json` contexts (`JsonSerializerContext` / `[JsonSerializable]`).
- `JsonSerializerIsReflectionEnabledByDefault` is `false`; new serializers must stay source-generated.
- Treat IL trim/AOT analyzer warnings (`IL2026`, `IL3050`, `IL2104`) as errors.

### HTTP contract (issue #21)

- HTTP contract source of truth is **unresolved** until issue #21 is closed.
- Until #21 is resolved, do not update only one of OpenAPI (`docs/api/openapi.yaml`), Contracts, or runtime DTOs for contract changes.
- After #21 is resolved, follow the resulting ADR and any CI drift checks added there.
- Mail request JSON behavior must stay consistent across OpenAPI, runtime, and tests — including unknown and duplicate property handling (#22).

### Provider errors (#26)

- Classify and sanitize provider exceptions before persisting to DB, writing logs, or showing in Admin UI.
- Never store or display raw ACS/Mailpit exception messages that may contain secrets or PII.

### Admin UI

- Admin is experimental, off by default, and not intended for direct internet exposure. See ADR 0013 (`docs/adr/0013-admin-threat-model-and-pii-policy.md`).
- HTML body preview must follow existing XSS-safe rendering patterns in tests under `tests/Amane.Mailer.Tests/Admin/`.

### Release and CI

- Changes to `.github/workflows/` must preserve tag/version validation, safe workflow inputs (no unvalidated shell interpolation), SHA-pinned actions, and release approval boundaries (`release` environment for publish workflows).
- Do not weaken SBOM/provenance/digest tracking added for release hardening (#23).

## Documentation map

Read in this order when context is unclear:

1. Target GitHub issue
2. Related ADR in `docs/adr/`
3. [docs/service-spec.md](docs/service-spec.md) and [docs/api/openapi.yaml](docs/api/openapi.yaml)
4. Ops runbooks in `docs/ops/` when changing deployment or operations

Do not cite private issue numbers or paths that no longer exist in public docs (#28).

## Modes

State the mode at the start of a session, or infer from the human's request.

| Mode | Do | Do not |
|------|----|--------|
| **Design** | Compare options, risks, ADR impact, acceptance criteria | Implement or refactor unrelated code |
| **Implement** | Smallest correct diff; update tests/docs only where needed | Change contract artifacts alone; expand scope |
| **Review** | Inspect diff for security, contract drift, AOT, PII, release, migration risks | Propose unrelated improvements |
| **Test** | Reproduce failure, add regression coverage, run focused checks, then broaden validation | Change product behavior without agreement |

## Suggested P1 order

1. Agent files (this guide) — done when `AGENTS.md` and `CLAUDE.md` exist
2. #28 — public docs cleanup (stale private refs, ROADMAP)
3. #21 — contract source of truth (blocks #22, #5, drift CI)
4. #5 — package/API/versioning policy
5. #22 — JSON strictness (unknown/duplicate properties)
6. #26 — provider error sanitize
7. #23 — release workflow hardening
8. #27, #11 — release notes and clean-state smoke

Scoped Cursor rules (`contracts-api.mdc`, `admin-security.mdc`) may be added under `.cursor/rules/` only after #21 or before Admin-heavy work — not in the initial commit.
