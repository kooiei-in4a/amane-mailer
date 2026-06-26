# Public repository P0 evidence

This file records security follow-up work for the public `amane-mailer`
repository. Do not record credential values here. Store live secrets only in
1Password, under the owner-controlled Amane.Mailer credential item.

## Credential inventory and rotation

Use this as a value-free completion checklist. Record only status, date, owner,
and where the credential is stored. Never paste token, key, connection string,
or secret values.

| Credential | Required action | Status | Evidence without values |
| --- | --- | --- | --- |
| NuGet publishing for `Amane.Mailer.Contracts` | Migrate to nuget.org Trusted Publishing for `kooiei-in4a/amane-mailer`, workflow `publish-contracts.yml`, environment `release`. Remove GitHub secret `NUGET_API_KEY`. Revoke/delete the older broader NuGet API key. | DONE | Trusted Publishing policy `Amane.Mailer.Contracts GitHub Actions` is active on nuget.org. Workflow run `28214950412` successfully exchanged the GitHub OIDC token for a temporary NuGet API key. GitHub repository secret `NUGET_API_KEY` has been deleted. Older broader NuGet API key `amane-mailer-contracts-publish-2026` has been revoked. |
| GitHub tokens / PATs used before publication | Confirm no broad PAT remains in active use. Rotate or delete any token that was used during publication prep. | DONE | Classic PATs, fine-grained PATs, owned OAuth Apps, and Authorized OAuth Apps reviewed on 2026-06-26 JST. Obsolete classic `read:packages` tokens were deleted. Active OAuth grants are retained because they are still in use. |
| ACS connection string | Review whether ACS access key or connection string rotation is required for live sending. If rotation is required, update deploy host secret storage. | REVIEWED | Owner reviewed on 2026-06-26 JST and determined rotation is not required. No ACS connection string value was recorded. |
| Tenant bearer token(s) | Rotate production/shared tenant token values. Update deploy host tenant secret storage and consumer app config. | NOT APPLICABLE | Owner reviewed on 2026-06-26 JST and confirmed there are no production/shared tenant bearer tokens for this repository to rotate. |
| Backup / rclone credentials | Rotate rclone remote credentials and any backup encryption/deploy credentials used before publication. | NOT APPLICABLE | Owner reviewed on 2026-06-26 JST and confirmed there are no backup/rclone credentials for this repository to rotate. |

Credential inventory snapshot on 2026-06-26 JST:

- GitHub repository secrets: none.
- GitHub `release` environment secrets: none.
- Dependabot secrets: none.
- GitHub repository variables: none.
- GitHub `release` environment variables: none.
- GitHub CLI auth: active OAuth token for account `kooiei-in4a`; token value
  was not displayed or recorded. Observed scopes were `gist`, `read:org`,
  `repo`, and `workflow`.
- Local ignored files in this checkout: Visual Studio and build artifacts only.
  No ignored deploy `.env`, private tenant JSON, rclone config, backup key
  directory, restore directory, or local database file was present in this
  checkout.
- Repository content scan: keyword review found only documented placeholders,
  example local tokens, test tokens, and operational warnings. No real
  credential value was recorded.

Credential inventory review notes:

- DONE: GitHub account Developer settings were reviewed for personal access
  tokens, fine-grained tokens, owned OAuth Apps, and Authorized OAuth Apps.
  GitHub CLI authorization is actively used and is retained without rotation.
- DONE: GitHub personal access tokens (classic) reviewed on 2026-06-26 JST.
  The classic PAT list is empty after deleting obsolete `read:packages` tokens.
- DONE: GitHub fine-grained personal access tokens reviewed on 2026-06-26 JST.
  No fine-grained tokens are present.
- DONE: GitHub owned OAuth Apps reviewed on 2026-06-26 JST. No owned OAuth
  Apps are present.
- DONE: GitHub Authorized OAuth Apps reviewed on 2026-06-26 JST. The listed
  grants are all still in use and were retained: Azure App Service
  Authentication, Azure App Service Creates, Cursor, Git Credential Manager,
  GitHub CLI, GitHub Desktop, Visual Studio, and Visual Studio Code.
- DONE: ACS connection string reviewed on 2026-06-26 JST. Owner determined
  rotation is not required. No ACS connection string value was recorded.
- DONE: Owner confirmed there are no production/shared tenant bearer tokens to
  rotate for this repository.
- DONE: Owner confirmed there are no backup/rclone credentials to rotate for
  this repository.
- DONE: Owner confirmed there is no GHCR deploy-host pull token to rotate for
  this repository.

Trusted Publishing follow-up:

- DONE: GitHub Actions environment `release` is configured in
  `kooiei-in4a/amane-mailer` with required reviewer `kooiei-in4a`,
  `prevent_self_review=false`, admin bypass enabled, and deployment branch/tag
  policies for `main` and `v*`.
- DONE: Trusted Publishing verification run `28214950412` completed
  successfully on 2026-06-26 JST. The `NuGet login` step exchanged the GitHub
  OIDC token for a temporary NuGet API key, and the package push steps completed
  with existing `0.1.0` artifacts skipped as duplicates.
- DONE: GitHub repository secret `NUGET_API_KEY` has been deleted.
- DONE: Older broader NuGet API key `amane-mailer-contracts-publish-2026`
  has been revoked on nuget.org.

## Git history secret scan

Initial scan date: 2026-06-26 JST

Repository:

- Remote: `https://github.com/kooiei-in4a/amane-mailer`
- Local branch: `main`
- Local HEAD: `a9d0752e903cfec9629acf1a6b7788159bba478e`
- Local tag checked: `v0.1.0`
- Commits scanned: 4
- Tool: `gitleaks 8.30.1`
- Config: default config plus repository `.gitleaks.toml`

Raw default-config result:

- `gitleaks git . --report-format json --redact=100 --verbose`
- Findings: 2
- Disposition: false positive. Both findings are the documented local-only
  sample token `local-mail-service-token` in README curl examples.
- Files:
  - `README.md`, rule `curl-auth-header`
  - `README.en.md`, rule `curl-auth-header`

Configured result:

- `gitleaks git . --config .gitleaks.toml --report-format json --redact=100 --verbose`
- Result: no leaks found

Working tree result:

- `gitleaks dir . --config .gitleaks.toml --report-format json --redact=100 --verbose`
- Result: no leaks found

Recommended additional scope:

- Run the same configured scan against the internal repository full history, if
  accessible.
- Run the same configured scan against any separate publication-prep working
  tree, if it is outside this checkout.

## CodeQL alert review

Status: reviewed on 2026-06-26 JST; no code fix required.

Alert record:

- Alert URL:
  `https://github.com/kooiei-in4a/amane-mailer/security/code-scanning/1`
- Rule id: `cs/user-controlled-bypass`
- Location: `src/Amane.Mailer/Api/MailRequestEndpoints.cs:35`
- Alert commit SHA: `dd9c29c9947f262da941d793b2c8d6fe1c534879`
- CodeQL category: `/language:csharp`
- Source: `HttpRequest.ContentLength`, which is controlled by the HTTP client.
- Guard condition: the early reject check returns `413 Payload Too Large` when
  the declared `Content-Length` is over `MaxRequestBodyBytes`.
- Sensitive action under review: request body processing before creating the
  mail request.
- Actual enforcement: `ReadRequestBodyAsync` reads the body in chunks, counts
  the actual bytes read, and throws `RequestBodyTooLargeException` when
  `totalBytes > MaxRequestBodyBytes`. That exception is handled as
  `413 Payload Too Large`.
- Disposition: false positive. A missing or understated `Content-Length` can
  bypass only the optimization at line 35, not the actual body-size limit.
- GitHub state: dismissed as false positive on 2026-06-25 16:56 UTC.
- Regression coverage: `Oversized_request_body_without_content_length_returns_413`
  verifies the body-size limit still returns 413 when the request has no
  computable content length.

## GHCR image provenance

Status: verified on 2026-06-26 JST.

Future image publishes are gated by `.github/workflows/publish-image.yml` on a
release tag ref and the GitHub Environment `release`. The workflow records the
published digest, platform, OCI labels, and SBOM/provenance attestation manifest
status in the workflow summary before the release record is updated manually.
It refuses to overwrite an existing GHCR `sha-<git-sha>` tag or release tag.

Release record: [docs/releases/v0.1.0.md](../releases/v0.1.0.md)

Release:

- Release tag: `v0.1.0`
- Local tag object: `52490a7c7256c9a3f6dd92d62c77d8b4bfa61cde`
- Tag resolves to commit: `a9d0752e903cfec9629acf1a6b7788159bba478e`
- Release URL: `https://github.com/kooiei-in4a/amane-mailer/releases/tag/v0.1.0`

Image:

- Image: `ghcr.io/kooiei-in4a/amane-mailer:v0.1.0`
- Index digest: `sha256:b0e513663df2be1df6045b8bff39d1fcd93536cae98287b91f07cfc7b8700677`
- Release body digest: `sha256:b0e513663df2be1df6045b8bff39d1fcd93536cae98287b91f07cfc7b8700677`
- `sha-a9d0752e903cfec9629acf1a6b7788159bba478e` tag digest:
  `sha256:b0e513663df2be1df6045b8bff39d1fcd93536cae98287b91f07cfc7b8700677`
- Runtime manifest digest: `sha256:0505b7e37f62c8a2ce47ba46c0d38235d1791d5859c01367ba25fb01e984c48a`
- Attestation manifest digest: `sha256:256fea711b275db4830ebb77598ed2767edd6a7c274ec00d29c18b42bae5c8f1`
- Platform: `linux/amd64`
- OCI source label: `https://github.com/kooiei-in4a/amane-mailer`
- OCI revision label: `a9d0752e903cfec9629acf1a6b7788159bba478e`

Conclusion: the release digest, `v0.1.0` tag digest, immutable `sha-...` tag
digest, source label, and revision label are consistent.
