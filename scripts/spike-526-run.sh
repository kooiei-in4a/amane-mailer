#!/usr/bin/env bash
set -uo pipefail

configuration="${CONFIGURATION:-Release}"
project="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/tests/Amane.Mailer.Spike526.Probe/Amane.Mailer.Spike526.Probe.csproj"
fixtures=("${@:-F00 F01 F02 F03 F04 F05 F06}")
modes=(buffered token)
concurrencies=(1 2)
# Independent cold-process repeats per fixture/mode/concurrency cell. Each
# repeat is a fresh `dotnet run` process (its own warm-up + measured pass), so
# the resulting JSON lines let a reader take the max or distribution across
# independent runs rather than trusting a single sample.
repeat="${REPEAT:-1}"

# `measure` now runs an in-process warm-up pass before the measured pass, so a
# separate `warmup` invocation in a different process is no longer needed --
# it could not warm the JIT/assembly state of the process that actually
# measures.
set -e
for ((run = 1; run <= repeat; run++)); do
  for fixture in ${fixtures[*]}; do
    for mode in "${modes[@]}"; do
      for concurrency in "${concurrencies[@]}"; do
        dotnet run --project "$project" -c "$configuration" --no-launch-profile -- measure "$fixture" "$mode" "$concurrency"
      done
    done
  done
done
set +e

# Optional GC heap hard-limit profiles (space-separated MiB values, e.g.
# CONTAINER_PROFILES_MIB="256 512"), run at concurrency 2 on top of the
# unconstrained passes above. DOTNET_GCHeapHardLimit approximates a container
# memory ceiling without requiring Docker: .NET enforces it the same way it
# enforces a real cgroup memory limit. Off by default because some
# fixture/mode cells are expected to fail closed (OUT_OF_MEMORY) at the
# smaller profiles by design -- that is Evidence, not a script bug, so
# non-zero exit codes from this section are intentionally not fatal.
if [[ -n "${CONTAINER_PROFILES_MIB:-}" ]]; then
  for mib in ${CONTAINER_PROFILES_MIB}; do
    hex=$(printf '%X' $((mib * 1024 * 1024)))
    echo "--- container profile: ${mib} MiB heap hard limit (concurrency 2) ---"
    for fixture in ${fixtures[*]}; do
      for mode in "${modes[@]}"; do
        DOTNET_GCHeapHardLimit="$hex" dotnet run --project "$project" -c "$configuration" --no-launch-profile -- measure "$fixture" "$mode" 2
      done
    done
  done
fi
