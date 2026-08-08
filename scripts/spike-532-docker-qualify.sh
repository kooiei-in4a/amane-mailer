#!/usr/bin/env bash
# Issue #532 real Docker/cgroup total-memory qualification (Linux/macOS host
# counterpart of spike-532-docker-qualify.ps1; see that script's comments for
# rationale). Usage: MEMORY_MIB=256 scripts/spike-532-docker-qualify.sh
set -uo pipefail

memory_mib="${MEMORY_MIB:?Set MEMORY_MIB to 256 or 512}"
if [[ "$memory_mib" != "256" && "$memory_mib" != "512" ]]; then
  echo "MEMORY_MIB must be 256 or 512" >&2
  exit 2
fi

image="${IMAGE:-mcr.microsoft.com/dotnet/runtime:10.0}"
configuration="${CONFIGURATION:-Release}"
accept_fixtures=(Q00 Q01 Q02 Q03)
reject_fixtures=(Q01X Q02X Q03X)
concurrencies=(1 2)
max_condition_repeat="${MAX_CONDITION_REPEAT:-3}"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/tests/Amane.Mailer.Spike526.Probe/Amane.Mailer.Spike526.Probe.csproj"
publish_dir="$repo_root/artifacts/publish/spike532-linux-x64"
entrypoint_host_path="$repo_root/scripts/spike-532-container-entrypoint.sh"
out_dir="$repo_root/docs/cd/reports"
mkdir -p "$out_dir"
results_path="$out_dir/issue-532-docker-${memory_mib}mib-results.jsonl"
: >"$results_path"

echo "--- restoring (locked-mode; preserves the committed packages.lock.json) ---"
dotnet restore "$project" --locked-mode

# --no-restore below is required: a `dotnet publish -p:PublishAot=false` that is
# allowed to run its own implicit restore re-evaluates the dependency graph with
# AOT disabled and rewrites/truncates packages.lock.json (observed locally --
# ~54 lines of RID-specific ILCompiler entries dropped). Restoring separately
# above (honoring the project's real PublishAot=true declaration and the
# existing lock file) and then publishing with --no-restore keeps this
# test-only, AOT-skipped publish from ever touching the committed lock file.
echo "--- publishing probe for linux-x64 (framework-dependent; PublishAot disabled -- #532 does not qualify AOT) ---"
dotnet publish "$project" -c "$configuration" -r linux-x64 --self-contained false --no-restore \
  -p:PublishAot=false -p:UseAppHost=false -o "$publish_dir"

is_reject_fixture() {
  local id="$1"
  for candidate in "${reject_fixtures[@]}"; do
    [[ "$candidate" == "$id" ]] && return 0
  done
  return 1
}

run_container() {
  local fixture="$1" concurrency="$2" repeat_index="$3" repeat_count="$4"
  local suffix container_name
  suffix=$(od -An -N4 -tx1 /dev/urandom | tr -d ' \n')
  container_name="spike532-${memory_mib}-${fixture}-c${concurrency}-r${repeat_index}-${suffix}"

  docker create --name "$container_name" --memory="${memory_mib}m" \
    -v "${publish_dir}:/probe:ro" \
    -v "${entrypoint_host_path}:/entrypoint.sh:ro" \
    "$image" bash /entrypoint.sh "$fixture" "$concurrency" >/dev/null

  local host_config_memory
  host_config_memory=$(docker inspect "$container_name" --format '{{.HostConfig.Memory}}')

  local stdout
  stdout=$(docker start -a "$container_name" 2>&1)
  local docker_exit oom_killed
  docker_exit=$(docker inspect "$container_name" --format '{{.State.ExitCode}}')
  oom_killed=$(docker inspect "$container_name" --format '{{.State.OOMKilled}}')
  docker rm "$container_name" >/dev/null

  local probe_json="" cgroup_max="null" cgroup_current_before="null" cgroup_peak="null"
  local cgroup_current_after="null" cgroup_events="" probe_exit="null" wall_ns="null"
  local temp_residue="null" stderr_class="null"

  while IFS= read -r line; do
    case "$line" in
      \{*) probe_json="$line" ;;
      CGROUP_MAX=*) cgroup_max="${line#CGROUP_MAX=}"; [[ "$cgroup_max" == "NA" ]] && cgroup_max="null" ;;
      CGROUP_CURRENT_BEFORE=*) cgroup_current_before="${line#CGROUP_CURRENT_BEFORE=}"; [[ "$cgroup_current_before" == "NA" ]] && cgroup_current_before="null" ;;
      CGROUP_PEAK=*) cgroup_peak="${line#CGROUP_PEAK=}"; [[ "$cgroup_peak" == "NA" ]] && cgroup_peak="null" ;;
      CGROUP_CURRENT_AFTER=*) cgroup_current_after="${line#CGROUP_CURRENT_AFTER=}"; [[ "$cgroup_current_after" == "NA" ]] && cgroup_current_after="null" ;;
      CGROUP_EVENTS=*) cgroup_events="${line#CGROUP_EVENTS=}" ;;
      PROBE_EXIT=*) probe_exit="${line#PROBE_EXIT=}" ;;
      WALL_NS=*) wall_ns="${line#WALL_NS=}" ;;
      TEMP_RESIDUE_COUNT=*) temp_residue="${line#TEMP_RESIDUE_COUNT=}" ;;
      "Spike526 probe failed:"*) stderr_class="\"${line#Spike526 probe failed: }\"" ;;
    esac
  done <<<"$stdout"

  local oom_events="null" oom_kill_events="null"
  if [[ -n "$cgroup_events" ]]; then
    oom_events=$(grep -o 'oom [0-9]*' <<<"${cgroup_events//;/$'\n'}" | awk '{print $2}' | head -1)
    oom_kill_events=$(grep -o 'oom_kill [0-9]*' <<<"${cgroup_events//;/$'\n'}" | awk '{print $2}' | head -1)
    [[ -z "$oom_events" ]] && oom_events="null"
    [[ -z "$oom_kill_events" ]] && oom_kill_events="null"
  fi

  local expected="ACCEPT"
  is_reject_fixture "$fixture" && expected="REJECT"

  local result="REJECTED"
  if [[ -n "$probe_json" ]]; then
    result=$(sed -n 's/.*"Result":"\([^"]*\)".*/\1/p' <<<"$probe_json")
  elif [[ "$oom_killed" == "true" ]]; then
    result="OOM"
  elif [[ "$docker_exit" == "0" ]]; then
    result="PASS"
  fi

  printf '{"memory_limit_mib":%s,"fixture":"%s","expected":"%s","concurrency":%s,"repeat_index":%s,"repeat_count":%s,"docker_exit_code":%s,"probe_exit_code":%s,"oom_killed":%s,"host_config_memory_bytes":%s,"cgroup_memory_max_bytes":%s,"cgroup_memory_current_before_bytes":%s,"cgroup_memory_current_after_bytes":%s,"cgroup_memory_peak_bytes":%s,"cgroup_oom_events":%s,"cgroup_oom_kill_events":%s,"wall_time_ns":%s,"temp_residue_count":%s,"stderr_classification":%s,"result":"%s","probe_result_json":%s}\n' \
    "$memory_mib" "$fixture" "$expected" "$concurrency" "$repeat_index" "$repeat_count" \
    "$docker_exit" "$probe_exit" "$oom_killed" "$host_config_memory" "$cgroup_max" \
    "$cgroup_current_before" "$cgroup_current_after" "$cgroup_peak" "$oom_events" "$oom_kill_events" \
    "$wall_ns" "$temp_residue" "$stderr_class" "$result" "${probe_json:-null}" \
    >>"$results_path"

  echo "  [$fixture c=$concurrency r=$repeat_index/$repeat_count] docker_exit=$docker_exit probe_exit=$probe_exit oom_killed=$oom_killed result=$result"
}

echo "--- ${memory_mib} MiB qualification: accept-path fixtures ---"
for fixture in "${accept_fixtures[@]}"; do
  for concurrency in "${concurrencies[@]}"; do
    repeat_count=1
    if [[ "$fixture" == "Q03" && "$concurrency" == "2" ]]; then
      repeat_count="$max_condition_repeat"
    fi
    for ((repeat = 1; repeat <= repeat_count; repeat++)); do
      run_container "$fixture" "$concurrency" "$repeat" "$repeat_count"
    done
  done
done

echo "--- ${memory_mib} MiB qualification: boundary-reject fixtures (provider must not be invoked) ---"
for fixture in "${reject_fixtures[@]}"; do
  for concurrency in "${concurrencies[@]}"; do
    run_container "$fixture" "$concurrency" 1 1
  done
done

echo "--- results written to $results_path ---"
