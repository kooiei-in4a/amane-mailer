param(
    # Real Docker/cgroup total-memory limit under qualification (#532). Both
    # 256 and 512 MiB are required by the Issue; run this script once per value.
    [Parameter(Mandatory)]
    [ValidateSet(256, 512)]
    [int]$MemoryMiB,

    [string]$Image = 'mcr.microsoft.com/dotnet/runtime:10.0',
    [string]$Configuration = 'Release',

    # #532 fixtures expected to be accepted (decode + ACS envelope within the
    # #523 2 MiB/file, 5 MiB total, 8 MiB provider-envelope policy).
    [string[]]$AcceptFixtures = @('Q00', 'Q01', 'Q02', 'Q03'),

    # #532 fixtures deliberately sized just past a policy boundary; every run
    # here is expected to be rejected before ACS provider invocation.
    [string[]]$RejectFixtures = @('Q01X', 'Q02X', 'Q03X'),

    [int[]]$Concurrencies = @(1, 2),

    # Independent container repeats for the max condition (Q03, concurrency 2)
    # so a single lucky pass is not mistaken for stability (#532 section 13).
    [int]$MaxConditionRepeat = 3
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repoRoot 'tests/Amane.Mailer.Spike526.Probe/Amane.Mailer.Spike526.Probe.csproj'
$publishDir = Join-Path $repoRoot 'artifacts/publish/spike532-linux-x64'
$entrypointHostPath = (Resolve-Path (Join-Path $PSScriptRoot 'spike-532-container-entrypoint.sh')).Path
$outDir = Join-Path $repoRoot 'docs/cd/reports'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$resultsPath = Join-Path $outDir "issue-532-docker-$($MemoryMiB)mib-results.jsonl"
Remove-Item -ErrorAction SilentlyContinue -Path $resultsPath

Write-Host "--- restoring (locked-mode; preserves the committed packages.lock.json) ---"
dotnet restore $project --locked-mode
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE"
}

# --no-restore below is required: a `dotnet publish -p:PublishAot=false` that is
# allowed to run its own implicit restore re-evaluates the dependency graph with
# AOT disabled and rewrites/truncates packages.lock.json (observed locally --
# ~54 lines of RID-specific ILCompiler entries dropped). Restoring separately
# above (honoring the project's real PublishAot=true declaration and the
# existing lock file) and then publishing with --no-restore keeps this
# test-only, AOT-skipped publish from ever touching the committed lock file.
Write-Host "--- publishing probe for linux-x64 (framework-dependent; PublishAot disabled -- #532 does not qualify AOT) ---"
dotnet publish $project -c $Configuration -r linux-x64 --self-contained false --no-restore `
    -p:PublishAot=false -p:UseAppHost=false -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

function ConvertTo-NullableLong([string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text) -or $Text -eq 'NA') {
        return $null
    }

    $trimmed = $Text.TrimEnd(';')
    $value = 0L
    if ([long]::TryParse($trimmed, [ref]$value)) {
        return $value
    }

    return $null
}

function Get-CgroupEventCount([string]$EventsLine, [string]$EventName) {
    if ([string]::IsNullOrWhiteSpace($EventsLine) -or $EventsLine -eq 'NA') {
        return $null
    }

    foreach ($entry in ($EventsLine -split ';')) {
        if ($entry -match "^$EventName\s+(\d+)$") {
            return [long]$Matches[1]
        }
    }

    return $null
}

function Invoke-Spike532Container {
    param(
        [string]$FixtureId,
        [int]$Concurrency,
        [int]$RepeatIndex,
        [int]$RepeatCount
    )

    $suffix = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $containerName = "spike532-$MemoryMiB-$FixtureId-c$Concurrency-r$RepeatIndex-$suffix"

    docker create --name $containerName --memory="$($MemoryMiB)m" `
        -v "${publishDir}:/probe:ro" `
        -v "${entrypointHostPath}:/entrypoint.sh:ro" `
        $Image bash /entrypoint.sh $FixtureId $Concurrency | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "docker create failed for $containerName"
    }

    $hostConfigMemory = (docker inspect $containerName --format '{{.HostConfig.Memory}}').Trim()

    $stdoutLines = @(docker start -a $containerName 2>&1)
    $dockerExitCode = [int](docker inspect $containerName --format '{{.State.ExitCode}}').Trim()
    $oomKilled = (docker inspect $containerName --format '{{.State.OOMKilled}}').Trim() -eq 'true'
    docker rm $containerName | Out-Null

    $probeResult = $null
    $cgroupMax = $null
    $cgroupCurrentBefore = $null
    $cgroupPeak = $null
    $cgroupCurrentAfter = $null
    $cgroupEvents = $null
    $probeExit = $null
    $wallNs = $null
    $tempResidue = $null
    $stderrClassification = $null

    foreach ($line in $stdoutLines) {
        $text = "$line"
        if ($text.StartsWith('{')) {
            try {
                $probeResult = $text | ConvertFrom-Json
            } catch {
                # Not the probe's JSON result line; ignore.
            }
        } elseif ($text -like 'CGROUP_MAX=*') {
            $cgroupMax = ConvertTo-NullableLong ($text.Substring(11))
        } elseif ($text -like 'CGROUP_CURRENT_BEFORE=*') {
            $cgroupCurrentBefore = ConvertTo-NullableLong ($text.Substring(22))
        } elseif ($text -like 'CGROUP_PEAK=*') {
            $cgroupPeak = ConvertTo-NullableLong ($text.Substring(12))
        } elseif ($text -like 'CGROUP_CURRENT_AFTER=*') {
            $cgroupCurrentAfter = ConvertTo-NullableLong ($text.Substring(21))
        } elseif ($text -like 'CGROUP_EVENTS=*') {
            $cgroupEvents = $text.Substring(14)
        } elseif ($text -like 'PROBE_EXIT=*') {
            $probeExit = [int]($text.Substring(11))
        } elseif ($text -like 'WALL_NS=*') {
            $wallNs = [long]($text.Substring(8))
        } elseif ($text -like 'TEMP_RESIDUE_COUNT=*') {
            $tempResidue = [int]($text.Substring(20))
        } elseif ($text -like 'Spike526 probe failed:*') {
            $stderrClassification = $text.Substring('Spike526 probe failed: '.Length).Trim()
        }
    }

    $record = [ordered]@{
        memory_limit_mib          = $MemoryMiB
        requested_memory_bytes    = $MemoryMiB * 1024 * 1024
        host_config_memory_bytes  = [long]$hostConfigMemory
        cgroup_memory_max_bytes   = $cgroupMax
        cgroup_memory_current_before_bytes = $cgroupCurrentBefore
        cgroup_memory_current_after_bytes  = $cgroupCurrentAfter
        cgroup_memory_peak_bytes  = $cgroupPeak
        cgroup_oom_events         = Get-CgroupEventCount $cgroupEvents 'oom'
        cgroup_oom_kill_events    = Get-CgroupEventCount $cgroupEvents 'oom_kill'
        fixture                  = $FixtureId
        expected                 = if ($RejectFixtures -contains $FixtureId) { 'REJECT' } else { 'ACCEPT' }
        concurrency               = $Concurrency
        repeat_index               = $RepeatIndex
        repeat_count               = $RepeatCount
        docker_exit_code           = $dockerExitCode
        probe_exit_code             = $probeExit
        oom_killed                = $oomKilled
        wall_time_ms               = if ($null -ne $wallNs) { [math]::Round($wallNs / 1000000.0, 1) } else { $null }
        temp_residue_count         = $tempResidue
        stderr_classification      = $stderrClassification
        result                    = if ($probeResult) { $probeResult.Result } elseif ($oomKilled) { 'OOM' } elseif ($dockerExitCode -eq 0) { 'PASS' } else { 'REJECTED' }
        provider_invoked           = if ($probeResult) { $probeResult.ProviderInvoked } else { $false }
        consumer_envelope_bytes    = if ($probeResult) { $probeResult.ConsumerEnvelopeBytes } else { $null }
        acs_envelope_bytes         = if ($probeResult) { $probeResult.AcsEnvelopeBytes } else { $null }
        decoded_binary_bytes       = if ($probeResult) { $probeResult.DecodedBinaryBytes } else { $null }
        gc_heap_before_bytes       = if ($probeResult) { $probeResult.GcHeapBeforeBytes } else { $null }
        gc_heap_peak_bytes         = if ($probeResult) { $probeResult.GcHeapPeakBytes } else { $null }
        gc_heap_after_bytes        = if ($probeResult) { $probeResult.GcHeapAfterBytes } else { $null }
        peak_working_set_bytes     = if ($probeResult) { $probeResult.PeakWorkingSetBytes } else { $null }
        cleanup_complete           = if ($probeResult) { $probeResult.CleanupComplete } else { $tempResidue -eq 0 }
    }

    ($record | ConvertTo-Json -Compress) | Add-Content -Path $resultsPath -Encoding utf8
    $statusLabel = $record.result
    Write-Host "  [$FixtureId c=$Concurrency r=$RepeatIndex/$RepeatCount] docker_exit=$dockerExitCode probe_exit=$probeExit oom_killed=$oomKilled result=$statusLabel"
}

Write-Host "--- $($MemoryMiB) MiB qualification: accept-path fixtures ---"
foreach ($fixtureId in $AcceptFixtures) {
    foreach ($concurrency in $Concurrencies) {
        $repeatCount = 1
        if ($fixtureId -eq 'Q03' -and $concurrency -eq 2) {
            $repeatCount = $MaxConditionRepeat
        }

        for ($repeat = 1; $repeat -le $repeatCount; $repeat++) {
            Invoke-Spike532Container -FixtureId $fixtureId -Concurrency $concurrency -RepeatIndex $repeat -RepeatCount $repeatCount
        }
    }
}

Write-Host "--- $($MemoryMiB) MiB qualification: boundary-reject fixtures (provider must not be invoked) ---"
foreach ($fixtureId in $RejectFixtures) {
    foreach ($concurrency in $Concurrencies) {
        Invoke-Spike532Container -FixtureId $fixtureId -Concurrency $concurrency -RepeatIndex 1 -RepeatCount 1
    }
}

Write-Host "--- results written to $resultsPath ---"
