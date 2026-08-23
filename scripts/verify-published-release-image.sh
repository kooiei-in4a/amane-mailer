#!/usr/bin/env bash
# Read-only wrapper for verify-published-release-image.py.
set -Eeuo pipefail
set +x

SCRIPT_DIR="$(cd "$(dirname "$BASH_SOURCE")" >/dev/null 2>&1 && pwd)"
exec python3 "$SCRIPT_DIR/verify-published-release-image.py" "$@"
