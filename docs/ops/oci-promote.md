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
- Candidate handoff: single #455 workflow run ID + artifact name/ID (`setup-release-candidate-oci`)
- Candidate run validator: `scripts/validate-candidate-oci-run.sh`

## Local readiness proof

```bash
bash scripts/promote-candidate-oci-self-test.sh
```

Uses a disposable localhost `registry:2` container. Does **not** push to GHCR.
Also executed in public CI as job `OCI promote digest-preservation self-test`.

## Legacy rebuild path

`.github/workflows/publish-image.yml` rejects `v1.2.0` / package version `1.2.0` so the rebuild-and-push path cannot publish that release.
