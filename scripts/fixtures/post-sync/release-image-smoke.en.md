This runbook pulls the published GHCR runtime image (current public example: `ghcr.io/kooiei-in4a/amane-mailer:v1.3.4`) from a clean state.
MAILER_IMAGE_TAG=v1.3.4 bash scripts/release-smoke.sh
| `MAILER_IMAGE_TAG` | (required; mutually exclusive with `MAILER_IMAGE_DIGEST`) |
Value-free smoke results for `v1.3.4`
[docs/releases/v1.3.4.md](../releases/v1.3.4.md)
