param(
    [ValidateSet('F00','F01','F02','F03','F04','F05','F06')]
    [string[]]$Fixture = @('F00','F01','F02','F03','F04','F05','F06'),
    [ValidateSet('buffered','token')]
    [string[]]$Mode = @('buffered','token'),
    [ValidateSet(1,2)]
    [int[]]$Concurrency = @(1,2),
    # Additional managed GC heap hard-limit profiles (MiB) to run at
    # concurrency 2, on top of the unconstrained passes above. This constrains
    # only the .NET managed GC heap (DOTNET_GCHeapHardLimit), not the
    # process's total working set or the OS/container's total memory -- it is
    # a managed-heap-pressure approximation, not a substitute for a real
    # Docker/cgroup total-memory qualification (which this script does not
    # perform; see the probe's Program.cs remarks). Off by default because
    # some fixture/mode cells are expected to fail closed (OUT_OF_MEMORY) at
    # the smaller profiles by design -- that is Evidence, not a script bug.
    [int[]]$ContainerProfileMiB = @(),
    # Independent cold-process repeats per fixture/mode/concurrency cell. Each
    # repeat is a fresh `dotnet run` process (its own warm-up + measured pass),
    # so the resulting JSON lines let a reader take the max or distribution
    # across independent runs rather than trusting a single sample.
    [int]$Repeat = 1,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\tests\Amane.Mailer.Spike526.Probe\Amane.Mailer.Spike526.Probe.csproj'

# `measure` now runs an in-process warm-up pass before the measured pass, so a
# separate `warmup` invocation in a different process is no longer needed --
# it could not warm the JIT/assembly state of the process that actually
# measures.
for ($run = 1; $run -le $Repeat; $run++) {
    foreach ($fixtureId in $Fixture) {
        foreach ($modeName in $Mode) {
            foreach ($c in $Concurrency) {
                dotnet run --project $project -c $Configuration --no-launch-profile -- measure $fixtureId $modeName $c
            }
        }
    }
}

foreach ($miB in $ContainerProfileMiB) {
    $hex = '{0:X}' -f ([long]$miB * 1MB)
    Write-Host "--- managed GC heap hard-limit profile: ${miB} MiB (concurrency 2; not a container/cgroup total-memory qualification) ---"
    foreach ($fixtureId in $Fixture) {
        foreach ($modeName in $Mode) {
            $env:DOTNET_GCHeapHardLimit = $hex
            try {
                dotnet run --project $project -c $Configuration --no-launch-profile -- measure $fixtureId $modeName 2
            }
            finally {
                Remove-Item Env:\DOTNET_GCHeapHardLimit -ErrorAction SilentlyContinue
            }
        }
    }
}
