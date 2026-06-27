# Release Notes Checklist

[日本語](release-notes-checklist.md)

GitHub Release notes must let an OSS consumer identify the release artifacts and
major operational constraints from the release page alone. Before or immediately
after publishing a release, verify and record the following items.

## Artifacts

- Release tag, for example `v0.1.0`
- Annotated tag object when the release uses an annotated tag
- Tag target commit SHA
- Docker image, for example `ghcr.io/kooiei-in4a/amane-mailer:vX.Y.Z`
- Image digest / index digest
- Immutable Docker tag (`sha-<git-sha>`) and tag digest
- Runtime manifest digest
- Attestation manifest digest
- Platform (currently `linux/amd64`)
- Release image smoke result (`docs/releases/vX.Y.Z.md`: digest, date, environment, pass/fail summary)
- OCI source label and revision label
- NuGet package name / version / package URL
- NuGet symbol package (.snupkg) availability (verify the push result in the publish workflow summary; after indexing, manually confirm availability via NuGet Package Explorer or VS debugger)
- SourceLink commit matches the release tag commit
- .NET SDK version from `global.json` and roll-forward policy

## Operational Notes

- `202 Accepted` means the Mailer persisted the request; it does not mean
  provider delivery has completed.
- Delivery is at-least-once. If the process stops after a provider send succeeds
  but before the Mailer DB is updated to `delivered`, the same mail may be sent
  again.
- SQLite deployment assumes single-node / single-replica operation. Horizontally
  scaling multiple Workers over one shared SQLite file is currently out of
  operational scope.
- State the Docker image platform. The current image is `linux/amd64` only.
- Admin UI is disabled by default, internal-network-only, and experimental.
  State current limitations such as durable session, durable throttle, durable
  audit, and per-admin tenant scope gaps.
- Take a backup of the SQLite DB and tenant config before upgrade / migration,
  and verify the restore procedure for production.
- ACS live sending requires explicit configuration. Send live mail only when
  `MAILER_PROVIDER=acs`, `ACS_CONNECTION_STRING`, a `live_sending=true` tenant,
  and ACS-approved sender/domain configuration are all in place.

## References To Verify

- `docs/releases/vX.Y.Z.md`
- `docs/ops/public-repository-p0-evidence.md`
- `CHANGELOG.md`
- `README.md` / `README.en.md`
- `docs/service-spec.md` / `docs/service-spec.en.md`
- `docs/adr/0012-mail-via-mailer-microservice.md`
- `docs/adr/0013-admin-threat-model-and-pii-policy.md`
- `docs/ops/ghcr-image-publish.md` / `.en.md`
- `docs/ops/backup-operations.md` / `.en.md`
- `docs/ops/restore-procedure.md` / `.en.md`
- GitHub Release body (`gh release view vX.Y.Z --repo kooiei-in4a/amane-mailer`)
