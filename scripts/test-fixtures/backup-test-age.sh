#!/usr/bin/env bash
# Test double only. It copies the archive instead of encrypting/decrypting it so the
# backup/restore fixture can run on CI hosts without an age installation or a real key.
set -Eeuo pipefail

mode="${1:-}"
shift || true
output=""
input=""

while [ "$#" -gt 0 ]; do
  case "$1" in
    --output|-o|--identity|-i|--recipient|-r)
      [ "$#" -ge 2 ] || exit 2
      if [ "$1" = "--output" ] || [ "$1" = "-o" ]; then
        output="$2"
      fi
      shift 2
      ;;
    --decrypt)
      shift
      ;;
    --encrypt)
      shift
      ;;
    --*)
      shift
      ;;
    *)
      input="$1"
      shift
      ;;
  esac
done

[ "$mode" = "--encrypt" ] || [ "$mode" = "--decrypt" ] || exit 2
[ -n "$output" ] && [ -n "$input" ] || exit 2
cp -- "$input" "$output"
