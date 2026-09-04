# Roadmap

This roadmap is directional. It describes a possible order of work, not a
promise of exact scope, release contents, dates, capacity, or support level.

## Current stable line

The current public stable line is **v1.3.8**. The authoritative current
version, tag, supported image platform, and release record are maintained in
[`release/current-public.json`](release/current-public.json). This roadmap is
not a release authority; update the authority first when the public release
changes.

The current implementation and operating boundary remain the SQLite,
single-process / single-replica design described by
[`docs/adr/0019-sqlite-single-process-boundaries.md`](docs/adr/0019-sqlite-single-process-boundaries.md)
and [`docs/service-spec.md`](docs/service-spec.md).

## Historical context

The following items describe earlier 0.x planning context. They are retained
for history and are not current commitments:

- The v0.1.x line prioritized a small, auditable repository, local operation,
  the mail request API and payload hash contract, and initial Docker, Mailpit,
  ACS, backup, restore, and deploy-rehearsal documentation.
- The v0.2.x line focused on compatibility notes for the published Contracts
  package, CI coverage for publishing and migrations, and pinned release
  automation.

## Future direction (no release promise)

These are topics that may be considered if real usage or measured triggers
justify them:

- Document production deployment patterns across more providers.
- Consider broader database/provider support if real deployments need it.
- Add stronger operational examples for multi-tenant environments.
- Revisit API versioning once downstream integrations exist.
- SDK follow-ups such as status polling helpers and webhook signature verification.

## Non-goals for now

- This repository does not ship production tenant configuration.
- This repository does not include real delivery credentials, live tokens, or
  private infrastructure names.
- The historical 0.x line is not the current stable line.
