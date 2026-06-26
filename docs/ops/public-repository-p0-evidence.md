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
| NuGet publishing for `Amane.Mailer.Contracts` | Migrate to nuget.org Trusted Publishing for `kooiei-in4a/amane-mailer`, workflow `publish-contracts.yml`, environment `release`. Remove GitHub secret `NUGET_API_KEY`. Revoke/delete the older broader NuGet API key. | IN PROGRESS | Trusted Publishing policy `Amane.Mailer.Contracts GitHub Actions` is active on nuget.org. Workflow run `28214950412` successfully exchanged the GitHub OIDC token for a temporary NuGet API key. GitHub repository secret `NUGET_API_KEY` has been deleted. Older broader NuGet API key revoke remains TODO. |
| GitHub tokens / PATs used before publication | Confirm no broad PAT remains in active use. Rotate or delete any token that was used during publication prep. | TODO | Record only token name, provider UI confirmation, and completion date. |
| ACS connection string | Rotate the ACS access key or connection string used for live sending. Update deploy host secret storage. | TODO | Record only Azure resource name, key slot rotated, and completion date. |
| Tenant bearer token(s) | Rotate production/shared tenant token values. Update deploy host tenant secret storage and consumer app config. | TODO | Record only tenant id/name and completion date. |
| Backup / rclone credentials | Rotate rclone remote credentials and any backup encryption/deploy credentials used before publication. | TODO | Record only remote name, credential class, and completion date. |

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
- TODO: Revoke/delete the older broader NuGet API key after the
  Trusted Publishing workflow succeeds.

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

Status: TODO

The GitHub API request for code scanning alerts returned `404 Not Found` from
this local `gh` session on 2026-06-26 JST. Do not dismiss
`cs/user-controlled-bypass` without opening the alert location and confirming
the dataflow.

Suggested commands after refreshing GitHub CLI scopes or checking the GitHub UI:

```powershell
gh auth refresh -h github.com -s security_events
gh api repos/kooiei-in4a/amane-mailer/code-scanning/alerts -f state=open -f per_page=100
```

For the alert record:

- Alert URL:
- Rule id: `cs/user-controlled-bypass`
- Location:
- Source:
- Sink / bypass condition:
- Disposition: fix required / false positive
- Reason:
- Fix or dismissal date:

## GHCR image provenance

Status: verified on 2026-06-26 JST.

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
