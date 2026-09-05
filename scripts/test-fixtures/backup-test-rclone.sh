#!/usr/bin/env bash
# Test double only. Verify that the backup script hands rclone only an encrypted
# artifact; do not contact a remote or inspect archive contents.
set -Eeuo pipefail

[ "$1" = "copy" ] || exit 2
shift
source_path=""
remote=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    --config)
      [ "$#" -ge 2 ] || exit 2
      shift 2
      ;;
    *)
      if [ -z "$source_path" ]; then
        source_path="$1"
      else
        remote="$1"
      fi
      shift
      ;;
  esac
done

[ -n "$source_path" ] && [ -n "$remote" ] || exit 2
case "$source_path" in
  *.age) ;;
  *) exit 1 ;;
esac
[ -s "$source_path" ]
