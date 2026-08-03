#!/usr/bin/env bash
set -euo pipefail

configuration="${CONFIGURATION:-Release}"
project="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/tests/Amane.Mailer.Spike526.Probe/Amane.Mailer.Spike526.Probe.csproj"
fixtures=("${@:-F00 F01 F03 F04}")
modes=(buffered token)

for fixture in ${fixtures[*]}; do
  for mode in "${modes[@]}"; do
    dotnet run --project "$project" -c "$configuration" --no-launch-profile -- warmup "$fixture" "$mode" >/dev/null
    dotnet run --project "$project" -c "$configuration" --no-launch-profile -- measure "$fixture" "$mode"
  done
done
