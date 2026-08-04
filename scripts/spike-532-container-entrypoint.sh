#!/usr/bin/env bash
# Issue #532 real Docker/cgroup total-memory qualification container entrypoint.
# Runs inside the qualification container (mcr.microsoft.com/dotnet/runtime:10.0
# or equivalent) with the published Amane.Mailer.Spike526.Probe bind-mounted
# read-only at /probe. Emits only fixed-name, value-free metric lines plus the
# probe's own value-free JSON result line (or its fixed failure classification
# on stderr) -- never raw request/attachment/exception content.
set -uo pipefail

fixture="$1"
concurrency="$2"

cgroup_read() {
  if [ -f "$1" ]; then
    tr '\n' ';' <"$1"
  else
    echo -n "NA"
  fi
}

echo "CGROUP_MAX=$(cgroup_read /sys/fs/cgroup/memory.max)"
echo "CGROUP_CURRENT_BEFORE=$(cgroup_read /sys/fs/cgroup/memory.current)"

start_ns=$(date +%s%N)
dotnet /probe/Amane.Mailer.Spike526.Probe.dll measure "$fixture" token "$concurrency"
probe_exit=$?
end_ns=$(date +%s%N)

echo "PROBE_EXIT=$probe_exit"
echo "WALL_NS=$((end_ns - start_ns))"
echo "CGROUP_PEAK=$(cgroup_read /sys/fs/cgroup/memory.peak)"
echo "CGROUP_CURRENT_AFTER=$(cgroup_read /sys/fs/cgroup/memory.current)"
echo "CGROUP_EVENTS=$(cgroup_read /sys/fs/cgroup/memory.events)"
# The probe writes its scoped temp root under amane-mailer-spike526-probe/ inside
# the OS temp dir; counting leftover files here is the container-end residue
# check required by #532 (a cgroup OOM kill delivers SIGKILL, so a .NET `finally`
# block is not guaranteed to run -- this check evaluates the real outcome
# instead of assuming cleanup happened).
echo "TEMP_RESIDUE_COUNT=$(find /tmp/amane-mailer-spike526-probe -type f 2>/dev/null | wc -l | tr -d ' ')"

exit "$probe_exit"
