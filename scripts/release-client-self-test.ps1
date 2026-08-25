# Fixture-backed self-test for the RO-1 release client.
# Live GitHub / GHCR / NuGet responses must not decide pass/fail.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ModulePath = Join-Path $PSScriptRoot 'release-client.psm1'
$CliPath = Join-Path $PSScriptRoot 'release.ps1'

Import-Module -Force -DisableNameChecking $ModulePath

$script:PassCount = 0
$script:FailCount = 0

function Write-TestPass {
    param([string]$Name)
    $script:PassCount++
    Write-Host "[PASS] $Name"
}

function Write-TestFail {
    param(
        [string]$Name,
        [string]$Detail
    )
    $script:FailCount++
    Write-Host "[FAIL] $Name -- $Detail"
}

function Assert-Equal {
    param(
        [string]$Name,
        $Actual,
        $Expected
    )
    if ([string]$Actual -ne [string]$Expected) {
        Write-TestFail -Name $Name -Detail ("expected='$Expected' actual='$Actual'")
        return
    }
    Write-TestPass -Name $Name
}

function Assert-True {
    param(
        [string]$Name,
        [bool]$Condition,
        [string]$Detail
    )
    if (-not $Condition) {
        Write-TestFail -Name $Name -Detail $Detail
        return
    }
    Write-TestPass -Name $Name
}

function Get-FixtureSha {
    param([string]$Fill)
    return ($Fill * 40).Substring(0, 40)
}

function Get-FixtureDigest {
    param([string]$Fill)
    return ('sha256:{0}' -f (($Fill * 64).Substring(0, 64)))
}

$MainSha = Get-FixtureSha 'a'
$RelSha = Get-FixtureSha 'b'
$WrongSha = Get-FixtureSha 'c'
$DigestA = Get-FixtureDigest 'a'
$DigestB = Get-FixtureDigest 'b'

function Get-Map {
    param($Observations)
    return Get-ReleaseDerivedStatus -Observations $Observations
}

# --- HTTP presence mapping ---
Assert-Equal '404 is ABSENT' (ConvertTo-RemotePresence -StatusCode 404 -TransportFailure $false) 'ABSENT'
Assert-Equal '200 is HTTP_OK' (ConvertTo-RemotePresence -StatusCode 200 -TransportFailure $false) 'HTTP_OK'
Assert-Equal '401 is INCOMPLETE' (ConvertTo-RemotePresence -StatusCode 401 -TransportFailure $false) 'INCOMPLETE'
Assert-Equal '403 is INCOMPLETE' (ConvertTo-RemotePresence -StatusCode 403 -TransportFailure $false) 'INCOMPLETE'
Assert-Equal '429 is INCOMPLETE' (ConvertTo-RemotePresence -StatusCode 429 -TransportFailure $false) 'INCOMPLETE'
Assert-Equal '500 is INCOMPLETE' (ConvertTo-RemotePresence -StatusCode 500 -TransportFailure $false) 'INCOMPLETE'
Assert-Equal '502 is INCOMPLETE' (ConvertTo-RemotePresence -StatusCode 502 -TransportFailure $false) 'INCOMPLETE'
Assert-Equal 'network failure is INCOMPLETE' (ConvertTo-RemotePresence -StatusCode 0 -TransportFailure $true) 'INCOMPLETE'
Assert-Equal 'status 0 without transport is INCOMPLETE' (ConvertTo-RemotePresence -StatusCode 0 -TransportFailure $false) 'INCOMPLETE'
Assert-Equal '401 is never ABSENT' (ConvertTo-RemotePresence -StatusCode 401 -TransportFailure $false) 'INCOMPLETE'

Assert-Equal '401 class AUTH' (ConvertTo-RemoteFailureClass -StatusCode 401 -TransportFailure $false -FailureClass '') 'AUTH'
Assert-Equal '429 class RATE_LIMIT' (ConvertTo-RemoteFailureClass -StatusCode 429 -TransportFailure $false -FailureClass '') 'RATE_LIMIT'
Assert-Equal '500 class HTTP_5XX' (ConvertTo-RemoteFailureClass -StatusCode 500 -TransportFailure $false -FailureClass '') 'HTTP_5XX'
Assert-Equal 'timeout class TIMEOUT' (ConvertTo-RemoteFailureClass -StatusCode 0 -TransportFailure $true -FailureClass 'TIMEOUT') 'TIMEOUT'

# --- parsers ---
Assert-Equal 'contracts version parse' (Get-ContractsVersionFromText -Text "<Version>1.3.4</Version>") '1.3.4'
$openapi = @"
openapi: 3.0.3
info:
  title: Amane Mailer API
  version: "1.3.4"
paths: {}
"@
Assert-Equal 'openapi version parse' (Get-OpenApiVersionFromText -Text $openapi) '1.3.4'
Assert-Equal 'version alignment pass' (Get-VersionAlignment -RequestedVersion '1.3.4' -ContractsVersion '1.3.4' -OpenApiVersion '1.3.4') 'PASS'
Assert-Equal 'version alignment fail' (Get-VersionAlignment -RequestedVersion '1.3.5' -ContractsVersion '1.3.4' -OpenApiVersion '1.3.4') 'FAIL'
Assert-Equal 'version alignment incomplete' (Get-VersionAlignment -RequestedVersion '1.3.5' -ContractsVersion '' -OpenApiVersion '1.3.4') 'INCOMPLETE'

Assert-Equal 'record published' (Get-ReleaseRecordStateFromText -Text "> Status: **PUBLISHED**`n") 'PUBLISHED'
Assert-Equal 'record pending' (Get-ReleaseRecordStateFromText -Text "> Status: **RELEASE PREPARATION — NOT YET PUBLISHED**`n") 'PENDING'
Assert-Equal 'record incomplete header' (Get-ReleaseRecordStateFromText -Text "# no status header`n") 'INCOMPLETE'

$commitRef = '{"ref":"refs/tags/v1.3.4","object":{"sha":"' + $RelSha + '","type":"commit"}}'
$resolvedCommit = Resolve-GitTagTargetFromGitHubJson -RefJson $commitRef
Assert-Equal 'lightweight tag target' $resolvedCommit.TargetSha $RelSha

$annotatedRef = '{"ref":"refs/tags/v1.3.4","object":{"sha":"' + (Get-FixtureSha 'd') + '","type":"tag"}}'
$annotatedObj = '{"object":{"sha":"' + $RelSha + '","type":"commit"},"tag":"v1.3.4"}'
$resolvedAnnotated = Resolve-GitTagTargetFromGitHubJson -RefJson $annotatedRef -TagObjectJson $annotatedObj
Assert-Equal 'annotated tag peel' $resolvedAnnotated.TargetSha $RelSha

$badType = '{"object":{"sha":"' + $RelSha + '","type":"tree"}}'
$resolvedBad = Resolve-GitTagTargetFromGitHubJson -RefJson $badType
Assert-Equal 'unexpected tag object type is CONFLICT' $resolvedBad.State 'CONFLICT'

$nugetPresent = Test-NugetIndexContainsVersion -IndexJson '{"versions":["1.3.0","1.3.4"]}' -Version '1.3.4'
$nugetAbsent = Test-NugetIndexContainsVersion -IndexJson '{"versions":["1.3.0","1.3.4"]}' -Version '1.3.5'
$nugetNoPrefix = Test-NugetIndexContainsVersion -IndexJson '{"versions":["1.3.4"]}' -Version '1.3.40'
Assert-Equal 'nuget version present' $nugetPresent $true
Assert-Equal 'nuget version absent in index' $nugetAbsent $false
Assert-Equal 'nuget version is exact' $nugetNoPrefix $false
Assert-Equal 'nuget parse failure' (Test-NugetIndexContainsVersion -IndexJson '{not-json' -Version '1.3.4') $null

$config = '{"config":{"Labels":{"org.opencontainers.image.revision":"' + $RelSha + '"}}}'
Assert-Equal 'oci revision parse' (Get-OciRevisionFromConfigText -ConfigText $config) $RelSha
$manifest = '{"config":{"mediaType":"application/vnd.oci.image.config.v1+json","digest":"' + $DigestA + '","size":1}}'
Assert-Equal 'ghcr config digest parse' (Get-GhcrConfigDigestFromManifest -ManifestJson $manifest) $DigestA

Assert-Equal 'canonical https origin' (Get-OriginRepositoryIdentity -Url 'https://github.com/kooiei-in4a/amane-mailer.git') 'kooiei-in4a/amane-mailer'
Assert-Equal 'canonical ssh origin' (Get-OriginRepositoryIdentity -Url 'git@github.com:kooiei-in4a/amane-mailer.git') 'kooiei-in4a/amane-mailer'
Assert-Equal 'foreign origin' (Get-OriginRepositoryIdentity -Url 'https://github.com/example/other.git') 'example/other'
Assert-Equal 'version X.Y.Z' (Test-ReleaseVersion -Version '1.3.4') $true
Assert-Equal 'version rejects v-prefix' (Test-ReleaseVersion -Version 'v1.3.4') $false

# --- derivation fixtures ---
function New-AbsentObs {
    param(
        [string]$Version = '1.3.5',
        [string]$Alignment = 'PASS',
        [string]$LocalRepo = 'PASS',
        [string]$MainShaValue = $MainSha,
        [string]$MainState = 'PRESENT',
        [string]$Record = 'ABSENT'
    )
    return New-ReleaseObservations `
        -Version $Version `
        -LocalRepo $LocalRepo `
        -LocalBranch 'main' `
        -LocalHead $MainShaValue `
        -Worktree 'CLEAN' `
        -GitHubMainState $MainState `
        -GitHubMainSha $MainShaValue `
        -VersionAlignment $Alignment `
        -ContractsVersion $Version `
        -OpenApiVersion $Version `
        -ReleaseRecord $Record `
        -GitTag (New-ArtifactFact -State 'ABSENT') `
        -GitHubRelease (New-ArtifactFact -State 'ABSENT') `
        -Ghcr (New-ArtifactFact -State 'ABSENT') `
        -Nuget (New-ArtifactFact -State 'ABSENT')
}

$pre = Get-Map (New-AbsentObs)
Assert-Equal 'pre-publication STATE' $pre['STATE'] 'UNPUBLISHED'
Assert-Equal 'pre-publication NEXT' $pre['NEXT_ACTION'] 'PUBLISH_IMAGE'
Assert-Equal 'pre-publication SOURCE_BASIS' $pre['SOURCE_BASIS'] 'GITHUB_MAIN'
Assert-Equal 'pre-publication SOURCE_SHA' $pre['SOURCE_SHA'] $MainSha
Assert-Equal 'pre-publication GHCR' $pre['GHCR'] 'ABSENT'
Assert-Equal 'pre-publication GIT_TAG' $pre['GIT_TAG'] 'ABSENT'
Assert-Equal 'pre-publication NUGET' $pre['NUGET'] 'ABSENT'
Assert-Equal 'pre-publication GITHUB_RELEASE' $pre['GITHUB_RELEASE'] 'ABSENT'
Assert-Equal 'pre-publication MUTATION' $pre['MUTATION_PERFORMED'] 'FALSE'

$imageObs = New-AbsentObs -Version '1.3.5'
$imageObs.Ghcr = New-ArtifactFact -State 'PRESENT' -Digest $DigestA -Revision $RelSha -ShaTagState 'PRESENT' -ShaTagDigest $DigestA
$image = Get-Map $imageObs
Assert-Equal 'image-published STATE' $image['STATE'] 'IMAGE_PUBLISHED'
Assert-Equal 'image-published NEXT' $image['NEXT_ACTION'] 'CREATE_TAG'
Assert-Equal 'image-published SOURCE_BASIS' $image['SOURCE_BASIS'] 'GHCR'
Assert-Equal 'image-published SOURCE_SHA' $image['SOURCE_SHA'] $RelSha
Assert-Equal 'image-published GHCR' $image['GHCR'] 'PRESENT'
Assert-Equal 'image-published GIT_TAG' $image['GIT_TAG'] 'ABSENT'
Assert-Equal 'image-published MUTATION' $image['MUTATION_PERFORMED'] 'FALSE'

$taggedObs = New-AbsentObs -Version '1.3.4'
$taggedObs.GitTag = New-ArtifactFact -State 'PRESENT' -TargetSha $RelSha
$taggedObs.Ghcr = New-ArtifactFact -State 'PRESENT' -Digest $DigestA -Revision $RelSha -ShaTagState 'PRESENT' -ShaTagDigest $DigestA
$tagged = Get-Map $taggedObs
Assert-Equal 'tag+ghcr STATE' $tagged['STATE'] 'TAGGED'
Assert-Equal 'tag+ghcr NEXT' $tagged['NEXT_ACTION'] 'PUBLISH_NUGET'
Assert-Equal 'tag+ghcr SOURCE_BASIS' $tagged['SOURCE_BASIS'] 'GIT_TAG'
Assert-Equal 'tag+ghcr SOURCE_SHA' $tagged['SOURCE_SHA'] $RelSha
Assert-Equal 'tag+ghcr MUTATION' $tagged['MUTATION_PERFORMED'] 'FALSE'

$fullObs = New-AbsentObs -Version '1.3.4' -Record 'PUBLISHED'
$fullObs.GitTag = New-ArtifactFact -State 'PRESENT' -TargetSha $RelSha
$fullObs.Ghcr = New-ArtifactFact -State 'PRESENT' -Digest $DigestA -Revision $RelSha -ShaTagState 'PRESENT' -ShaTagDigest $DigestA
$fullObs.Nuget = New-ArtifactFact -State 'PRESENT'
$fullObs.GitHubRelease = New-ArtifactFact -State 'PRESENT'
$full = Get-Map $fullObs
Assert-Equal 'full consistent STATE' $full['STATE'] 'PUBLISHED'
Assert-Equal 'full consistent NEXT' $full['NEXT_ACTION'] 'NONE'
Assert-Equal 'full consistent SOURCE_BASIS' $full['SOURCE_BASIS'] 'GIT_TAG'
Assert-Equal 'full consistent GHCR' $full['GHCR'] 'PRESENT'
Assert-Equal 'full consistent GIT_TAG' $full['GIT_TAG'] 'PRESENT'
Assert-Equal 'full consistent NUGET' $full['NUGET'] 'PRESENT'
Assert-Equal 'full consistent RELEASE' $full['GITHUB_RELEASE'] 'PRESENT'
Assert-Equal 'full consistent RECORD' $full['RELEASE_RECORD'] 'PUBLISHED'
Assert-Equal 'full consistent MUTATION' $full['MUTATION_PERFORMED'] 'FALSE'

$wrongTagObs = New-AbsentObs -Version '1.3.4'
$wrongTagObs.GitTag = New-ArtifactFact -State 'PRESENT' -TargetSha $WrongSha
$wrongTagObs.Ghcr = New-ArtifactFact -State 'PRESENT' -Digest $DigestA -Revision $RelSha -ShaTagState 'PRESENT' -ShaTagDigest $DigestA
$wrongTag = Get-Map $wrongTagObs
Assert-Equal 'wrong tag target STATE' $wrongTag['STATE'] 'CONFLICT'
Assert-Equal 'wrong tag target NEXT' $wrongTag['NEXT_ACTION'] 'STOP'
Assert-Equal 'wrong tag target GIT_TAG' $wrongTag['GIT_TAG'] 'CONFLICT'
Assert-Equal 'wrong tag target GHCR' $wrongTag['GHCR'] 'CONFLICT'
Assert-Equal 'wrong tag target MUTATION' $wrongTag['MUTATION_PERFORMED'] 'FALSE'

$digestMismatchObs = New-AbsentObs -Version '1.3.4'
$digestMismatchObs.GitTag = New-ArtifactFact -State 'PRESENT' -TargetSha $RelSha
$digestMismatchObs.Ghcr = New-ArtifactFact -State 'PRESENT' -Digest $DigestA -Revision $RelSha -ShaTagState 'PRESENT' -ShaTagDigest $DigestB
$digestMismatch = Get-Map $digestMismatchObs
Assert-Equal 'ghcr digest mismatch STATE' $digestMismatch['STATE'] 'CONFLICT'
Assert-Equal 'ghcr digest mismatch NEXT' $digestMismatch['NEXT_ACTION'] 'STOP'
Assert-Equal 'ghcr digest mismatch GHCR' $digestMismatch['GHCR'] 'CONFLICT'
Assert-Equal 'ghcr digest mismatch MUTATION' $digestMismatch['MUTATION_PERFORMED'] 'FALSE'

$alignObs = New-AbsentObs -Version '1.3.5' -Alignment 'FAIL'
$align = Get-Map $alignObs
Assert-Equal 'version mismatch unpublished STATE' $align['STATE'] 'UNPUBLISHED'
Assert-Equal 'version mismatch unpublished NEXT' $align['NEXT_ACTION'] 'STOP'
Assert-Equal 'version mismatch ALIGNMENT' $align['VERSION_ALIGNMENT'] 'FAIL'
Assert-Equal 'version mismatch MUTATION' $align['MUTATION_PERFORMED'] 'FALSE'

$alignPublishedObs = New-AbsentObs -Version '1.3.5' -Alignment 'FAIL' -Record 'PUBLISHED'
$alignPublishedObs.GitTag = New-ArtifactFact -State 'PRESENT' -TargetSha $RelSha
$alignPublishedObs.Ghcr = New-ArtifactFact -State 'PRESENT' -Digest $DigestA -Revision $RelSha -ShaTagState 'PRESENT' -ShaTagDigest $DigestA
$alignPublishedObs.Nuget = New-ArtifactFact -State 'PRESENT'
$alignPublishedObs.GitHubRelease = New-ArtifactFact -State 'PRESENT'
$alignPublished = Get-Map $alignPublishedObs
Assert-Equal 'version mismatch with artifacts STATE' $alignPublished['STATE'] 'CONFLICT'
Assert-Equal 'version mismatch with artifacts NEXT' $alignPublished['NEXT_ACTION'] 'STOP'

$httpIncompleteObs = New-AbsentObs
$httpIncompleteObs.Ghcr = New-ArtifactFact -State 'INCOMPLETE' -Reason 'AUTH'
$httpIncomplete = Get-Map $httpIncompleteObs
Assert-Equal 'ghcr 401 STATE' $httpIncomplete['STATE'] 'INCOMPLETE'
Assert-Equal 'ghcr 401 NEXT' $httpIncomplete['NEXT_ACTION'] 'STOP'
Assert-Equal 'ghcr 401 GHCR' $httpIncomplete['GHCR'] 'INCOMPLETE'
Assert-Equal 'ghcr 401 is not ABSENT' $httpIncomplete['GHCR'] 'INCOMPLETE'
Assert-Equal 'ghcr 401 MUTATION' $httpIncomplete['MUTATION_PERFORMED'] 'FALSE'

$dirtyObs = New-AbsentObs -LocalRepo 'DRIFT'
$dirtyObs.Worktree = 'DIRTY'
$dirty = Get-Map $dirtyObs
Assert-Equal 'dirty local is DRIFT' $dirty['LOCAL_REPO'] 'DRIFT'
Assert-Equal 'dirty local still UNPUBLISHED' $dirty['STATE'] 'UNPUBLISHED'
Assert-Equal 'dirty local MUTATION' $dirty['MUTATION_PERFORMED'] 'FALSE'

$localIncompleteObs = New-AbsentObs -LocalRepo 'INCOMPLETE'
$localIncomplete = Get-Map $localIncompleteObs
Assert-Equal 'local incomplete STATE' $localIncomplete['STATE'] 'INCOMPLETE'
Assert-Equal 'local incomplete NEXT' $localIncomplete['NEXT_ACTION'] 'STOP'

$invertedObs = New-AbsentObs -Version '1.3.5'
$invertedObs.GitTag = New-ArtifactFact -State 'PRESENT' -TargetSha $RelSha
$inverted = Get-Map $invertedObs
Assert-Equal 'tag without ghcr STATE' $inverted['STATE'] 'CONFLICT'
Assert-Equal 'tag without ghcr NEXT' $inverted['NEXT_ACTION'] 'STOP'

$ghcrShaIncompleteObs = New-AbsentObs -Version '1.3.5'
$ghcrShaIncompleteObs.Ghcr = New-ArtifactFact -State 'PRESENT' -Digest $DigestA -Revision $RelSha -ShaTagState 'INCOMPLETE'
$ghcrShaIncomplete = Get-Map $ghcrShaIncompleteObs
Assert-Equal 'sha-tag incomplete GHCR' $ghcrShaIncomplete['GHCR'] 'INCOMPLETE'
Assert-Equal 'sha-tag incomplete STATE' $ghcrShaIncomplete['STATE'] 'INCOMPLETE'
Assert-Equal 'sha-tag incomplete NEXT' $ghcrShaIncomplete['NEXT_ACTION'] 'STOP'

# Every derived map keeps MUTATION_PERFORMED=FALSE
$allMaps = @($pre, $image, $tagged, $full, $wrongTag, $digestMismatch, $align, $alignPublished, $httpIncomplete, $dirty, $localIncomplete, $inverted, $ghcrShaIncomplete)
$mutationOk = $true
foreach ($item in $allMaps) {
    if ($item['MUTATION_PERFORMED'] -ne 'FALSE') { $mutationOk = $false }
}
Assert-True 'all derivation fixtures keep MUTATION_PERFORMED=FALSE' $mutationOk 'a fixture set MUTATION_PERFORMED to a non-FALSE value'

$formatted = Format-ReleaseStatusLines -Map $full
Assert-Equal 'format starts with VERSION' $formatted[0] 'VERSION=1.3.4'
Assert-Equal 'format ends with MUTATION' $formatted[$formatted.Count - 1] 'MUTATION_PERFORMED=FALSE'
Assert-Equal 'format key count' $formatted.Count 13

# --- Invoke-ReleaseStatus with injected observers (no live network) ---
$observerLocal = [pscustomobject]@{
    State          = 'DRIFT'
    Branch         = 'feature/test'
    Head           = $MainSha
    Worktree       = 'DIRTY'
    OriginIdentity = 'kooiei-in4a/amane-mailer'
    LocalMain      = $MainSha
    OriginMain     = $MainSha
    Reason         = 'DIRTY'
}
$observers = @{
    LocalRepo     = { param($root) $observerLocal }
    GitHubMain    = { [pscustomobject]@{ State = 'PRESENT'; Sha = $MainSha; Reason = '' } }
    GitTag        = { param($ver) New-ArtifactFact -State 'ABSENT' }
    GitHubRelease = { param($ver) New-ArtifactFact -State 'ABSENT' }
    Nuget         = { param($ver) New-ArtifactFact -State 'ABSENT' }
    Versions      = { param($root, $ver) [pscustomobject]@{ Alignment = 'FAIL'; ContractsVersion = '1.3.4'; OpenApiVersion = '1.3.4' } }
    ReleaseRecord = { param($root, $ver) 'ABSENT' }
    Ghcr          = { param($ver, $sha) New-ArtifactFact -State 'ABSENT' }
}
$injected = Invoke-ReleaseStatus -Version '1.3.5' -RepoRoot $RepoRoot -Observers $observers -Quiet
Assert-Equal 'injected dirty LOCAL_REPO' $injected['LOCAL_REPO'] 'DRIFT'
Assert-Equal 'injected VERSION_ALIGNMENT' $injected['VERSION_ALIGNMENT'] 'FAIL'
Assert-Equal 'injected NEXT STOP' $injected['NEXT_ACTION'] 'STOP'
Assert-Equal 'injected MUTATION' $injected['MUTATION_PERFORMED'] 'FALSE'

# --- CLI usage / unknown command (no network) ---
$cliHost = 'powershell'
if ($PSVersionTable.PSVersion.Major -ge 7) { $cliHost = 'pwsh' }

function Invoke-Cli {
    param([string[]]$CliArgs)
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $cliHost -NoProfile -ExecutionPolicy Bypass -File $CliPath @CliArgs 2>&1 | ForEach-Object { [string]$_ }
        return [pscustomobject]@{
            ExitCode = [int]$LASTEXITCODE
            Output   = [string]::Join("`n", @($output))
        }
    }
    finally {
        $ErrorActionPreference = $prevEap
    }
}

$missingVersion = Invoke-Cli -CliArgs @('status')
Assert-Equal 'CLI status without Version exit 2' $missingVersion.ExitCode 2
Assert-True 'CLI status without Version mentions Version' ($missingVersion.Output -match 'Version') 'missing -Version usage text'

$unknown = Invoke-Cli -CliArgs @('preflight', '-Version', '1.3.4')
Assert-Equal 'CLI unknown command exit 2' $unknown.ExitCode 2
Assert-True 'CLI unknown command is RO-1 only' ($unknown.Output -match 'RO-1') 'unknown command should name RO-1'
Assert-True 'CLI unknown command does not claim mutation' ($unknown.Output -notmatch 'MUTATION_PERFORMED=TRUE') 'unknown command must not claim a mutation'

$badVersion = Invoke-Cli -CliArgs @('status', '-Version', 'v1.3.4')
Assert-Equal 'CLI v-prefix exit 1' $badVersion.ExitCode 1
Assert-True 'CLI v-prefix rejected' ($badVersion.Output -match 'X\.Y\.Z') 'v-prefix should be rejected'

# --- static mutation-guard ---
$sourceFiles = @(
    (Join-Path $PSScriptRoot 'release.ps1'),
    (Join-Path $PSScriptRoot 'release-client.psm1')
)
$blocked = @(
    'git fetch',
    'git pull',
    'git push',
    'git checkout',
    'git reset',
    'git clone',
    'gh workflow run',
    'workflow_dispatch',
    'nuget push',
    'dotnet nuget push',
    'docker push',
    'docker login',
    '-OutFile',
    '-Method Post',
    '-Method Put',
    '-Method Patch',
    '-Method Delete'
)
$guardFailed = $false
foreach ($file in $sourceFiles) {
    $text = [System.IO.File]::ReadAllText($file)
    foreach ($token in $blocked) {
        if ($text.Contains($token)) {
            Write-TestFail -Name "mutation-guard $token" -Detail ("found in " + [System.IO.Path]::GetFileName($file))
            $guardFailed = $true
        }
    }
}
if (-not $guardFailed) {
    Write-TestPass -Name 'mutation-guard: no mutating tokens in client sources'
}

Write-Host ''
Write-Host ("Self-test result: {0} passed, {1} failed" -f $script:PassCount, $script:FailCount)
if ($script:FailCount -gt 0) {
    exit 1
}
exit 0
