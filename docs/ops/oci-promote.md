# Digest-preserving OCI promotion (P-OCI-PROMOTE)

For v1.2.0, publish GHCR images are promoted from the qualified #455 OCI layout without rebuild.

## Decisions

| Field | Value |
|-------|-------|
| `publishMethod` | `P-OCI-PROMOTE` |
| `ociHandoffMode` | `SINGLE_WF_RUN_OPTION_A` |
| `attestMode` | `EXTERNAL_PROVENANCE` |

## Authority

- Script: `scripts/promote-candidate-oci.sh`
- Workflow wrapper: `.github/workflows/promote-qualified-oci.yml`
- Tool: pinned `crane` (`go-containerregistry` `v0.20.3`), checksum-verified via `scripts/install-pinned-crane.sh`
- Candidate handoff identity: `#455` workflow run ID + artifact ID (name must match `setup-release-candidate-oci`)
- Candidate run validator: `scripts/validate-candidate-oci-run.sh`

## Local readiness proof

```bash
bash scripts/promote-candidate-oci-self-test.sh
```

Uses a disposable localhost `registry:2` container. Does **not** push to GHCR.
Also executed in public CI as job `OCI promote digest-preservation self-test`.
Candidate-run validator fixtures: `bash scripts/validate-candidate-oci-run-self-test.sh`.

## Legacy rebuild path

`.github/workflows/publish-image.yml` rejects `v1.2.0` / package version `1.2.0` so the rebuild-and-push path cannot publish that release.
