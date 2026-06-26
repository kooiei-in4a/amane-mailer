# Roadmap

This roadmap is directional. It describes the likely order of work, not a
promise of exact scope or dates.

## v0.1.x

- Keep the public repository small, auditable, and easy to run locally.
- Stabilize the mail request API and payload hash contract.
- Improve onboarding docs for Docker, Mailpit, ACS, backup, restore, and local
  deploy rehearsal.
- Add more OSS-facing examples for tenant configuration and consumer integration.
- Continue hardening admin UI masking, audit behavior, and operational defaults.

## v0.2.x

- Keep compatibility notes current for the published `Amane.Mailer.Contracts`
  package, starting from version `0.1.0`.
- Document package/API/image versioning expectations for consumers.
- Expand CI coverage around container publishing, schema validation, and
  migration compatibility.
- Improve release automation while keeping actions pinned to full-length SHAs.

## Later

- Document production deployment patterns across more providers.
- Consider broader database/provider support if real deployments need it.
- Add stronger operational examples for multi-tenant environments.
- Revisit API versioning once downstream integrations exist.

## Non-goals For Now

- This repository does not ship production tenant configuration.
- This repository does not include real delivery credentials, live tokens, or
  private infrastructure names.
- The 0.x line should be treated as early, useful, and still subject to change.
