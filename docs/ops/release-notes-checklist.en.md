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
- Platform list using Docker manifest notation, for example `linux/amd64` and
  `linux/arm64`
- Runtime manifest digest for each platform
- Attestation manifest digest for each platform
- Release image smoke result (`docs/releases/vX.Y.Z.md`: digest, date, environment, pass/fail summary)
- OCI source label and revision label
- NuGet package name / version / package URL
- NuGet symbol package:
  - Generation: confirm the `.snupkg` file name and successful `Verify symbol package was produced` step in the publish workflow summary.
  - Push: confirm `Push symbols to nuget.org`, or the `.nupkg` push creating the symbol package followed by an explicit symbol push completing with `--skip-duplicate`.
  - Availability: confirm `https://www.nuget.org/api/v2/symbolpackage/Amane.Mailer.Contracts/X.Y.Z` returns a `.snupkg` containing `lib/net8.0/Amane.Mailer.Contracts.pdb`.
  - Indexing / debugging: manually verify SourceLink / symbol resolution with NuGet Package Explorer, Visual Studio, or Rider. If not verified, record `not verified` in the release record.
- SourceLink commit matches the release tag commit
- .NET SDK version from `global.json` and roll-forward policy

## Operational Notes

- `202 Accepted` means the Mailer persisted the request; it does not mean
  provider delivery has completed.
- Explain `202 Accepted`, Mailer provider invocation, and the provider's actual
  delivery result as separate concepts. Do not imply an `exactly-once delivery`
  guarantee.
- When the release uses submission evidence, state that lease expiry or a
  process restart after provider invocation does not re-invoke the provider for
  the same request. Document the public state for ambiguous acceptance, the
  required exhaustive-enum handling, the condition under which the same
  `mail_request_id` must not be resent, and the new-request procedure for a
  deliberate resend (v1.3.0 uses terminal `delivery_unknown`).
- Confirm that SDK / HTTP-client 503 retries are distinct from Mailer's internal
  provider re-invocation policy. HTTP acceptance idempotency is not provider
  delivery safety.
- SQLite deployment assumes single-node / single-replica operation. Horizontally
  scaling multiple Workers over one shared SQLite file is currently out of
  operational scope (start gates and non-goals:
  [ADR 0019](../adr/0019-sqlite-single-process-boundaries.md)).
- State the Docker image platforms using the same notation as the Docker
  manifest. For a single-platform release, state a constraint such as
  `linux/amd64 only`; for a multi-arch release, record per-platform digests and
  smoke results.
- Admin UI is disabled by default, internal-network-only, and experimental.
  State current limitations such as durable session, durable throttle, durable
  audit, tenant scope operational boundaries, and retention sweep.
- Take a backup of the SQLite DB and tenant config before upgrade / migration,
  and verify the restore procedure for production.
- For GHCR image publish, confirm `promote-qualified-oci.yml` pre-login identity
  validation (candidate run/attempt, artifact IDs, candidateId, sealed
  qualification, releaseCommitSha, and OCI index digest) passed. The legacy
  `publish-image.yml` is fail-closed and performs no rebuild or image push.
- ACS live sending requires explicit configuration. Send live mail only when
  `MAILER_PROVIDER=acs`, a Staging/Production file secret registered via
  `admin provider register-acs` (`ACS_CONNECTION_STRING_FILE`; bare
  `ACS_CONNECTION_STRING` only for local/drill), a `live_sending=true` tenant,
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
- `docs/ops/release-artifact-verification.md` / `.en.md`
- `docs/ops/backup-operations.md` / `.en.md`
- `docs/ops/restore-procedure.md` / `.en.md`
- GitHub Release body (`gh release view vX.Y.Z --repo kooiei-in4a/amane-mailer`)
