# Fixture-backed self-test for the RO-1/RO-2 release client.
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
$pendingRecord = '> Status: **RELEASE PREPARATION ' + [char]0x2014 + ' NOT YET PUBLISHED**' + "`n"
Assert-Equal 'record pending' (Get-ReleaseRecordStateFromText -Text $pendingRecord) 'PENDING'
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
Assert-Equal 'dirty local NEXT STOP' $dirty['NEXT_ACTION'] 'STOP'
Assert-Equal 'dirty local MUTATION' $dirty['MUTATION_PERFORMED'] 'FALSE'

$caseA = New-AbsentObs -Version '1.3.5' -Alignment 'PASS' -LocalRepo 'DRIFT'
$caseA.LocalBranch = 'feature/version-prep'
$caseA.LocalHead = $RelSha
$caseA.GitHubMainSha = $MainSha
$caseA.ContractsVersion = '1.3.5'
$caseA.OpenApiVersion = '1.3.5'
$caseAMap = Get-Map $caseA
Assert-Equal 'case A feature version-prep NEXT' $caseAMap['NEXT_ACTION'] 'STOP'
Assert-Equal 'case A does not bind feature files to github main' $caseAMap['SOURCE_BASIS'] 'GITHUB_MAIN'
Assert-Equal 'case A SOURCE_SHA stays github main' $caseAMap['SOURCE_SHA'] $MainSha
Assert-Equal 'case A MUTATION' $caseAMap['MUTATION_PERFORMED'] 'FALSE'

$caseB = New-AbsentObs -Version '1.3.5' -Alignment 'PASS' -LocalRepo 'DRIFT'
$caseB.Worktree = 'DIRTY'
$caseBMap = Get-Map $caseB
Assert-Equal 'case B dirty LOCAL_REPO' $caseBMap['LOCAL_REPO'] 'DRIFT'
Assert-Equal 'case B dirty NEXT' $caseBMap['NEXT_ACTION'] 'STOP'
Assert-Equal 'case B alignment fact remains PASS' $caseBMap['VERSION_ALIGNMENT'] 'PASS'

$caseC = New-AbsentObs -Version '1.3.5' -Alignment 'PASS' -LocalRepo 'DRIFT'
$caseC.LocalHead = $MainSha
$caseC.GitHubMainSha = $WrongSha
$caseCMap = Get-Map $caseC
Assert-Equal 'case C main mismatch LOCAL_REPO' $caseCMap['LOCAL_REPO'] 'DRIFT'
Assert-Equal 'case C main mismatch NEXT' $caseCMap['NEXT_ACTION'] 'STOP'

$publishedDrift = New-AbsentObs -Version '1.3.4' -Record 'PUBLISHED' -LocalRepo 'DRIFT'
$publishedDrift.LocalBranch = 'feature/docs'
$publishedDrift.GitTag = New-ArtifactFact -State 'PRESENT' -TargetSha $RelSha
$publishedDrift.Ghcr = New-ArtifactFact -State 'PRESENT' -Digest $DigestA -Revision $RelSha -ShaTagState 'PRESENT' -ShaTagDigest $DigestA
$publishedDrift.Nuget = New-ArtifactFact -State 'PRESENT'
$publishedDrift.GitHubRelease = New-ArtifactFact -State 'PRESENT'
$publishedDriftMap = Get-Map $publishedDrift
Assert-Equal 'published drift STATE' $publishedDriftMap['STATE'] 'PUBLISHED'
Assert-Equal 'published drift NEXT' $publishedDriftMap['NEXT_ACTION'] 'NONE'

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
$allMaps = @($pre, $image, $tagged, $full, $wrongTag, $digestMismatch, $align, $alignPublished, $httpIncomplete, $dirty, $caseAMap, $caseBMap, $caseCMap, $publishedDriftMap, $localIncomplete, $inverted, $ghcrShaIncomplete)
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

$unknown = Invoke-Cli -CliArgs @('verify', '-Version', '1.3.4')
Assert-Equal 'CLI verify without SHA exit 2' $unknown.ExitCode 2
Assert-True 'CLI verify without SHA mentions ReleaseCommitSha' ($unknown.Output -match 'ReleaseCommitSha') 'missing -ReleaseCommitSha usage text'

$verifyBadSha = Invoke-Cli -CliArgs @('verify', '-Version', '1.3.4', '-ReleaseCommitSha', 'not-a-sha')
Assert-Equal 'CLI verify bad SHA exit 1' $verifyBadSha.ExitCode 1
Assert-True 'CLI verify bad SHA mentions hex' ($verifyBadSha.Output -match '40') 'bad SHA should be rejected'

$notImplemented = Invoke-Cli -CliArgs @('freeze', '-Version', '1.3.4')
Assert-Equal 'CLI not implemented command exit 2' $notImplemented.ExitCode 2
Assert-True 'CLI not implemented names RO-3' ($notImplemented.Output -match 'RO-3') 'unknown command should name RO-3'

$preflightNoVersion = Invoke-Cli -CliArgs @('preflight', '-ReleaseCommitSha', $MainSha)
Assert-Equal 'CLI preflight without Version exit 2' $preflightNoVersion.ExitCode 2
Assert-True 'CLI preflight without Version mentions Version' ($preflightNoVersion.Output -match 'Version') 'missing -Version usage text'

$preflightNoSha = Invoke-Cli -CliArgs @('preflight', '-Version', '1.3.5')
Assert-Equal 'CLI preflight without SHA exit 2' $preflightNoSha.ExitCode 2
Assert-True 'CLI preflight without SHA mentions ReleaseCommitSha' ($preflightNoSha.Output -match 'ReleaseCommitSha') 'missing -ReleaseCommitSha usage text'

$preflightBadSha = Invoke-Cli -CliArgs @('preflight', '-Version', '1.3.5', '-ReleaseCommitSha', 'not-a-sha')
Assert-Equal 'CLI preflight bad SHA exit 1' $preflightBadSha.ExitCode 1
Assert-True 'CLI preflight bad SHA mentions hex' ($preflightBadSha.Output -match '40-hex') 'bad SHA should be rejected'

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
    'git switch',
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

# --- GitHub Release state ---
$relPresent = Resolve-GitHubReleaseStateFromJson -Json '{"tag_name":"v1.3.4","draft":false,"prerelease":false}' -ExpectedTag 'v1.3.4'
Assert-Equal 'github release published PRESENT' $relPresent.State 'PRESENT'
$relWrongTag = Resolve-GitHubReleaseStateFromJson -Json '{"tag_name":"v1.3.3","draft":false,"prerelease":false}' -ExpectedTag 'v1.3.4'
Assert-Equal 'github release wrong tag CONFLICT' $relWrongTag.State 'CONFLICT'
$relDraft = Resolve-GitHubReleaseStateFromJson -Json '{"tag_name":"v1.3.4","draft":true,"prerelease":false}' -ExpectedTag 'v1.3.4'
Assert-Equal 'github release draft CONFLICT' $relDraft.State 'CONFLICT'
$relPre = Resolve-GitHubReleaseStateFromJson -Json '{"tag_name":"v1.3.4","draft":false,"prerelease":true}' -ExpectedTag 'v1.3.4'
Assert-Equal 'github release prerelease CONFLICT' $relPre.State 'CONFLICT'
$relBad = Resolve-GitHubReleaseStateFromJson -Json '{"html_url":"https://example.invalid"}' -ExpectedTag 'v1.3.4'
Assert-Equal 'github release malformed INCOMPLETE' $relBad.State 'INCOMPLETE'

# --- GHCR nested failure propagation ---
function New-HttpResult {
    param(
        [int]$StatusCode,
        [string]$BodyText = '',
        [string]$Digest = '',
        [bool]$TransportFailure = $false,
        [string]$FailureClass = ''
    )
    return [pscustomobject]@{
        StatusCode       = $StatusCode
        BodyText         = $BodyText
        Digest           = $Digest
        TransportFailure = $TransportFailure
        FailureClass     = $FailureClass
    }
}

$imageManifest = '{"mediaType":"application/vnd.oci.image.manifest.v1+json","config":{"mediaType":"application/vnd.oci.image.config.v1+json","digest":"' + $DigestA + '","size":1}}'
$configOk = '{"config":{"Labels":{"org.opencontainers.image.revision":"' + $RelSha + '"}}}'
$childDigest = Get-FixtureDigest 'c'
$indexManifest = '{"mediaType":"application/vnd.oci.image.index.v1+json","manifests":[{"digest":"' + $childDigest + '","platform":{"os":"linux","architecture":"amd64"}}]}'

function Get-GhcrWithBlob {
    param($BlobResult)
    $request = {
        param($Uri, $Headers)
        if ($Uri -like '*blobs*') { return $BlobResult }
        return New-HttpResult -StatusCode 200 -BodyText $imageManifest -Digest $DigestA
    }.GetNewClosure()
    return Get-GhcrManifestFact -Reference 'v1.3.4' -Token 't' -ReadRevision -Request $request
}

$ghcrOk = Get-GhcrWithBlob -BlobResult (New-HttpResult -StatusCode 200 -BodyText $configOk)
Assert-Equal 'ghcr happy path PRESENT' $ghcrOk.State 'PRESENT'
Assert-Equal 'ghcr happy path revision' $ghcrOk.Revision $RelSha

$ghcr401 = Get-GhcrWithBlob -BlobResult (New-HttpResult -StatusCode 401)
Assert-Equal 'ghcr config 401 INCOMPLETE' $ghcr401.State 'INCOMPLETE'
$ghcr429 = Get-GhcrWithBlob -BlobResult (New-HttpResult -StatusCode 429)
Assert-Equal 'ghcr config 429 INCOMPLETE' $ghcr429.State 'INCOMPLETE'
$ghcr500 = Get-GhcrWithBlob -BlobResult (New-HttpResult -StatusCode 500)
Assert-Equal 'ghcr config 5xx INCOMPLETE' $ghcr500.State 'INCOMPLETE'
$ghcrNet = Get-GhcrWithBlob -BlobResult (New-HttpResult -StatusCode 0 -TransportFailure $true -FailureClass 'NETWORK')
Assert-Equal 'ghcr config network INCOMPLETE' $ghcrNet.State 'INCOMPLETE'
$ghcr404 = Get-GhcrWithBlob -BlobResult (New-HttpResult -StatusCode 404)
Assert-Equal 'ghcr required config 404 INCOMPLETE' $ghcr404.State 'INCOMPLETE'
$ghcrBad = Get-GhcrWithBlob -BlobResult (New-HttpResult -StatusCode 200 -BodyText '{"config":{}}')
Assert-Equal 'ghcr missing revision INCOMPLETE' $ghcrBad.State 'INCOMPLETE'

$childFail = {
    param($Uri, $Headers)
    if ($Uri -like ('*' + $childDigest + '*')) {
        return New-HttpResult -StatusCode 500
    }
    return New-HttpResult -StatusCode 200 -BodyText $indexManifest -Digest $DigestB
}.GetNewClosure()
$ghcrChild = Get-GhcrManifestFact -Reference 'v1.3.4' -Token 't' -ReadRevision -Request $childFail
Assert-Equal 'ghcr child manifest failure INCOMPLETE' $ghcrChild.State 'INCOMPLETE'

$versionAbsent = {
    param($Uri, $Headers)
    return New-HttpResult -StatusCode 404
}.GetNewClosure()
$ghcrAbsent = Get-GhcrManifestFact -Reference 'v9.9.9' -Token 't' -ReadRevision -Request $versionAbsent
Assert-Equal 'ghcr version tag 404 ABSENT' $ghcrAbsent.State 'ABSENT'

# --- RO-2 preflight ---
function New-BoundLocal {
    param(
        [string]$Sha,
        [string]$Branch = 'main',
        [string]$Worktree = 'CLEAN',
        [string]$Origin = 'kooiei-in4a/amane-mailer',
        [string]$State = 'PASS',
        [string]$Head = '',
        [string]$LocalMain = '',
        [string]$OriginMain = ''
    )
    if ([string]::IsNullOrWhiteSpace($Head)) { $Head = $Sha }
    if ([string]::IsNullOrWhiteSpace($LocalMain)) { $LocalMain = $Sha }
    if ([string]::IsNullOrWhiteSpace($OriginMain)) { $OriginMain = $Sha }
    return [pscustomobject]@{
        State          = $State
        Branch         = $Branch
        Head           = $Head
        Worktree       = $Worktree
        OriginIdentity = $Origin
        LocalMain      = $LocalMain
        OriginMain     = $OriginMain
        Reason         = ''
    }
}

function New-DispatchRun {
    param(
        [string]$Id,
        [string]$Path,
        [string]$HeadSha,
        [string]$EventName = '',
        [string]$Status = 'completed',
        [string]$Conclusion = 'success'
    )
    if ([string]::IsNullOrWhiteSpace($EventName)) { $EventName = ('workflow' + '_dispatch') }
    return [pscustomobject]@{
        Id         = $Id
        Path       = $Path
        Event      = $EventName
        HeadSha    = $HeadSha
        Status     = $Status
        Conclusion = $Conclusion
    }
}

function New-ReadyPreflightObservers {
    param(
        [string]$Sha,
        [string]$Alignment = 'PASS',
        [string]$ContractsVersion = '1.3.5',
        [string]$OpenApiVersion = '1.3.5',
        [string]$Record = 'PENDING',
        [string]$Changelog = 'PASS'
    )
    $local = New-BoundLocal -Sha $Sha
    $githubSha = $Sha
    return @{
        LocalRepo          = { param($root) $local }.GetNewClosure()
        GitHubMain         = { [pscustomobject]@{ State = 'PRESENT'; Sha = $githubSha; Reason = '' } }.GetNewClosure()
        GitTag             = { param($ver) New-ArtifactFact -State 'ABSENT' }
        GitHubRelease      = { param($ver) New-ArtifactFact -State 'ABSENT' }
        Nuget              = { param($ver) New-ArtifactFact -State 'ABSENT' }
        Versions           = { param($root, $ver) [pscustomobject]@{ Alignment = $Alignment; ContractsVersion = $ContractsVersion; OpenApiVersion = $OpenApiVersion } }.GetNewClosure()
        ReleaseRecord      = { param($root, $ver) $Record }.GetNewClosure()
        Changelog          = { param($root, $ver) $Changelog }.GetNewClosure()
        GhcrVersion        = { param($ver) New-ArtifactFact -State 'ABSENT' }
        GhcrSha            = { param($shaArg) New-ArtifactFact -State 'ABSENT' }
        WorkflowImage      = { 'PASS' }
        WorkflowContracts  = { 'PASS' }
        WorkflowVerify     = { 'PASS' }
        WorkflowRuns       = { param($shaArg) @() }
    }
}

function Invoke-PreflightFixture {
    param($Observers, [string]$Version = '1.3.5', [string]$Sha = '')
    if ([string]::IsNullOrWhiteSpace($Sha)) { $Sha = $MainSha }
    return Invoke-ReleasePreflight -Version $Version -ReleaseCommitSha $Sha -RepoRoot $RepoRoot -Observers $Observers -Quiet
}

$script:PreflightMaps = New-Object System.Collections.Generic.List[object]

$readyObs = New-ReadyPreflightObservers -Sha $MainSha
$readyMap = Invoke-PreflightFixture -Observers $readyObs
[void]$script:PreflightMaps.Add($readyMap)
Assert-Equal 'preflight READY result' $readyMap['PREFLIGHT_RESULT'] 'PASS'
Assert-Equal 'preflight READY source' $readyMap['SOURCE_BINDING'] 'PASS'
Assert-Equal 'preflight READY version prep' $readyMap['VERSION_PREP'] 'PASS'
Assert-Equal 'preflight READY git tag' $readyMap['COLLISION_GIT_TAG'] 'ABSENT'
Assert-Equal 'preflight READY github release' $readyMap['COLLISION_GITHUB_RELEASE'] 'ABSENT'
Assert-Equal 'preflight READY ghcr version' $readyMap['COLLISION_GHCR_VERSION'] 'ABSENT'
Assert-Equal 'preflight READY ghcr sha' $readyMap['COLLISION_GHCR_SHA'] 'ABSENT'
Assert-Equal 'preflight READY nuget' $readyMap['COLLISION_NUGET'] 'ABSENT'
Assert-Equal 'preflight READY image wf' $readyMap['WORKFLOW_PUBLISH_IMAGE'] 'PASS'
Assert-Equal 'preflight READY contracts wf' $readyMap['WORKFLOW_PUBLISH_CONTRACTS'] 'PASS'
Assert-Equal 'preflight READY verify wf' $readyMap['WORKFLOW_VERIFY_PUBLIC_IMAGE'] 'PASS'
Assert-Equal 'preflight READY image run' $readyMap['IMAGE_PUBLISH_RUN'] 'ABSENT'
Assert-Equal 'preflight READY nuget run' $readyMap['NUGET_PUBLISH_RUN'] 'ABSENT'
Assert-Equal 'preflight READY image run id' $readyMap['IMAGE_PUBLISH_RUN_ID'] 'NONE'
Assert-Equal 'preflight READY nuget run id' $readyMap['NUGET_PUBLISH_RUN_ID'] 'NONE'
Assert-Equal 'preflight READY technical' $readyMap['TECHNICAL_READINESS'] 'READY'
Assert-Equal 'preflight READY human auth' $readyMap['HUMAN_AUTHORIZATION_REQUIRED'] 'TRUE'
Assert-Equal 'preflight READY mutation' $readyMap['MUTATION_PERFORMED'] 'FALSE'
Assert-Equal 'preflight READY command' $readyMap['COMMAND'] 'PREFLIGHT'
Assert-Equal 'preflight READY version field' $readyMap['VERSION'] '1.3.5'
Assert-Equal 'preflight READY sha field' $readyMap['RELEASE_COMMIT_SHA'] $MainSha

$featureObs = New-ReadyPreflightObservers -Sha $MainSha
$featureLocal = New-BoundLocal -Sha $MainSha -Branch 'feature/664-release-client-preflight' -State 'DRIFT'
$featureObs['LocalRepo'] = { param($root) $featureLocal }.GetNewClosure()
$featureMap = Invoke-PreflightFixture -Observers $featureObs
[void]$script:PreflightMaps.Add($featureMap)
Assert-Equal 'preflight feature branch source' $featureMap['SOURCE_BINDING'] 'FAIL'
Assert-Equal 'preflight feature branch result' $featureMap['PREFLIGHT_RESULT'] 'FAIL'
Assert-Equal 'preflight feature branch ready' $featureMap['TECHNICAL_READINESS'] 'STOP'

$dirtyObs = New-ReadyPreflightObservers -Sha $MainSha
$dirtyLocal = New-BoundLocal -Sha $MainSha -Worktree 'DIRTY' -State 'DRIFT'
$dirtyObs['LocalRepo'] = { param($root) $dirtyLocal }.GetNewClosure()
$dirtyMap = Invoke-PreflightFixture -Observers $dirtyObs
[void]$script:PreflightMaps.Add($dirtyMap)
Assert-Equal 'preflight dirty source' $dirtyMap['SOURCE_BINDING'] 'FAIL'
Assert-Equal 'preflight dirty ready' $dirtyMap['TECHNICAL_READINESS'] 'STOP'

$mismatchObs = New-ReadyPreflightObservers -Sha $MainSha
$mismatchLocal = New-BoundLocal -Sha $MainSha -OriginMain $WrongSha -State 'DRIFT'
$mismatchObs['LocalRepo'] = { param($root) $mismatchLocal }.GetNewClosure()
$mismatchMap = Invoke-PreflightFixture -Observers $mismatchObs
[void]$script:PreflightMaps.Add($mismatchMap)
Assert-Equal 'preflight origin/main mismatch source' $mismatchMap['SOURCE_BINDING'] 'FAIL'
Assert-Equal 'preflight origin/main mismatch ready' $mismatchMap['TECHNICAL_READINESS'] 'STOP'

$githubMismatchObs = New-ReadyPreflightObservers -Sha $MainSha
$githubMismatchObs['GitHubMain'] = { [pscustomobject]@{ State = 'PRESENT'; Sha = $WrongSha; Reason = '' } }.GetNewClosure()
$githubMismatchMap = Invoke-PreflightFixture -Observers $githubMismatchObs
[void]$script:PreflightMaps.Add($githubMismatchMap)
Assert-Equal 'preflight github main mismatch source' $githubMismatchMap['SOURCE_BINDING'] 'FAIL'

$shaMismatchObs = New-ReadyPreflightObservers -Sha $MainSha
$shaMismatchMap = Invoke-PreflightFixture -Observers $shaMismatchObs -Sha $WrongSha
[void]$script:PreflightMaps.Add($shaMismatchMap)
Assert-Equal 'preflight requested SHA mismatch source' $shaMismatchMap['SOURCE_BINDING'] 'FAIL'
Assert-Equal 'preflight requested SHA mismatch ready' $shaMismatchMap['TECHNICAL_READINESS'] 'STOP'

$contractsObs = New-ReadyPreflightObservers -Sha $MainSha -Alignment 'FAIL' -ContractsVersion '1.3.4' -OpenApiVersion '1.3.5'
$contractsMap = Invoke-PreflightFixture -Observers $contractsObs
[void]$script:PreflightMaps.Add($contractsMap)
Assert-Equal 'preflight contracts mismatch prep' $contractsMap['VERSION_PREP'] 'FAIL'
Assert-Equal 'preflight contracts mismatch result' $contractsMap['PREFLIGHT_RESULT'] 'FAIL'

$openapiObs = New-ReadyPreflightObservers -Sha $MainSha -Alignment 'FAIL' -ContractsVersion '1.3.5' -OpenApiVersion '1.3.4'
$openapiMap = Invoke-PreflightFixture -Observers $openapiObs
[void]$script:PreflightMaps.Add($openapiMap)
Assert-Equal 'preflight openapi mismatch prep' $openapiMap['VERSION_PREP'] 'FAIL'

$changelogObs = New-ReadyPreflightObservers -Sha $MainSha -Changelog 'FAIL'
$changelogMap = Invoke-PreflightFixture -Observers $changelogObs
[void]$script:PreflightMaps.Add($changelogMap)
Assert-Equal 'preflight changelog missing prep' $changelogMap['VERSION_PREP'] 'FAIL'

$recordAbsentObs = New-ReadyPreflightObservers -Sha $MainSha -Record 'ABSENT'
$recordAbsentMap = Invoke-PreflightFixture -Observers $recordAbsentObs
[void]$script:PreflightMaps.Add($recordAbsentMap)
Assert-Equal 'preflight record ABSENT prep' $recordAbsentMap['VERSION_PREP'] 'FAIL'

$recordPublishedObs = New-ReadyPreflightObservers -Sha $MainSha -Record 'PUBLISHED'
$recordPublishedMap = Invoke-PreflightFixture -Observers $recordPublishedObs
[void]$script:PreflightMaps.Add($recordPublishedMap)
Assert-Equal 'preflight record PUBLISHED prep' $recordPublishedMap['VERSION_PREP'] 'FAIL'

$recordBadObs = New-ReadyPreflightObservers -Sha $MainSha -Record 'INCOMPLETE'
$recordBadMap = Invoke-PreflightFixture -Observers $recordBadObs
[void]$script:PreflightMaps.Add($recordBadMap)
Assert-Equal 'preflight record malformed prep' $recordBadMap['VERSION_PREP'] 'INCOMPLETE'
Assert-Equal 'preflight record malformed result' $recordBadMap['PREFLIGHT_RESULT'] 'INCOMPLETE'
Assert-Equal 'preflight record malformed ready' $recordBadMap['TECHNICAL_READINESS'] 'STOP'

$collisionNames = @(
    @{ Key = 'GitTag'; Field = 'COLLISION_GIT_TAG' },
    @{ Key = 'GitHubRelease'; Field = 'COLLISION_GITHUB_RELEASE' },
    @{ Key = 'GhcrVersion'; Field = 'COLLISION_GHCR_VERSION' },
    @{ Key = 'GhcrSha'; Field = 'COLLISION_GHCR_SHA' },
    @{ Key = 'Nuget'; Field = 'COLLISION_NUGET' }
)
foreach ($collision in $collisionNames) {
    $obs = New-ReadyPreflightObservers -Sha $MainSha
    $obs[$collision.Key] = { param($arg) New-ArtifactFact -State 'PRESENT' }
    $map = Invoke-PreflightFixture -Observers $obs
    [void]$script:PreflightMaps.Add($map)
    Assert-Equal ("preflight collision PRESENT " + $collision.Field) $map[$collision.Field] 'PRESENT'
    Assert-Equal ("preflight collision PRESENT result " + $collision.Field) $map['PREFLIGHT_RESULT'] 'FAIL'
    Assert-Equal ("preflight collision PRESENT ready " + $collision.Field) $map['TECHNICAL_READINESS'] 'STOP'
}

$incompleteReasons = @('401', '403', '429', '5xx', 'network')
foreach ($reason in $incompleteReasons) {
    $obs = New-ReadyPreflightObservers -Sha $MainSha
    $captured = $reason
    $obs['GitHubRelease'] = { param($ver) New-ArtifactFact -State 'INCOMPLETE' -Reason $captured }.GetNewClosure()
    $map = Invoke-PreflightFixture -Observers $obs
    [void]$script:PreflightMaps.Add($map)
    Assert-Equal ("preflight remote INCOMPLETE never ABSENT " + $reason) $map['COLLISION_GITHUB_RELEASE'] 'INCOMPLETE'
    Assert-Equal ("preflight remote INCOMPLETE result " + $reason) $map['PREFLIGHT_RESULT'] 'INCOMPLETE'
    Assert-Equal ("preflight remote INCOMPLETE ready " + $reason) $map['TECHNICAL_READINESS'] 'STOP'
}

foreach ($collision in $collisionNames) {
    $obs = New-ReadyPreflightObservers -Sha $MainSha
    $obs[$collision.Key] = { param($arg) New-ArtifactFact -State 'INCOMPLETE' -Reason 'AUTH' }
    $map = Invoke-PreflightFixture -Observers $obs
    [void]$script:PreflightMaps.Add($map)
    Assert-Equal ("preflight surface INCOMPLETE " + $collision.Field) $map[$collision.Field] 'INCOMPLETE'
    Assert-True ("preflight surface INCOMPLETE not ABSENT " + $collision.Field) ($map[$collision.Field] -ne 'ABSENT') 'INCOMPLETE must not collapse to ABSENT'
}

$failOverIncompleteFacts = [pscustomobject]@{
    Version                   = '1.3.5'
    ReleaseCommitSha          = $MainSha
    SourceBinding             = 'FAIL'
    VersionPrep               = 'INCOMPLETE'
    CollisionGitTag           = 'ABSENT'
    CollisionGitHubRelease    = 'INCOMPLETE'
    CollisionGhcrVersion      = 'ABSENT'
    CollisionGhcrSha          = 'ABSENT'
    CollisionNuget            = 'ABSENT'
    WorkflowPublishImage      = 'PASS'
    WorkflowPublishContracts  = 'PASS'
    WorkflowVerifyPublicImage = 'PASS'
    ImagePublishRun           = 'ABSENT'
    ImagePublishRunId         = 'NONE'
    NugetPublishRun           = 'ABSENT'
    NugetPublishRunId         = 'NONE'
}
$failOverMap = Get-ReleasePreflightDerivedStatus -Facts $failOverIncompleteFacts
[void]$script:PreflightMaps.Add($failOverMap)
Assert-Equal 'aggregation FAIL over INCOMPLETE result' $failOverMap['PREFLIGHT_RESULT'] 'FAIL'
Assert-Equal 'aggregation FAIL over INCOMPLETE ready' $failOverMap['TECHNICAL_READINESS'] 'STOP'

$imageText = [System.IO.File]::ReadAllText((Join-Path $RepoRoot '.github\workflows\publish-release-image.yml'))
$contractsText = [System.IO.File]::ReadAllText((Join-Path $RepoRoot '.github\workflows\publish-contracts.yml'))
$verifyText = [System.IO.File]::ReadAllText((Join-Path $RepoRoot '.github\workflows\verify-public-release-image.yml'))
Assert-Equal 'canonical publish-release-image contract' (Test-PublishImageWorkflowContract -Text $imageText) 'PASS'
Assert-Equal 'canonical publish-contracts contract' (Test-PublishContractsWorkflowContract -Text $contractsText) 'PASS'
Assert-Equal 'canonical verify-public-release-image contract' (Test-VerifyPublicImageWorkflowContract -Text $verifyText) 'PASS'

$quotedHash = Get-ActiveWorkflowLineText -Line "        echo '## Release image publication'" -Mode 'Shell'
Assert-True 'quoted hash is not treated as comment' ($quotedHash.IndexOf('##') -ge 0) 'quoted ## was stripped'
$yamlHash = Get-ActiveWorkflowLineText -Line '        uses: actions/checkout@deadbeef # v7.0.1' -Mode 'Yaml'
Assert-True 'yaml trailing comment stripped' ($yamlHash.IndexOf('#') -lt 0) 'yaml # comment remained active'
Assert-True 'yaml uses remains after comment strip' ($yamlHash.IndexOf('uses:') -ge 0) 'uses mapping was lost'

$commentOnly = ConvertTo-WorkflowActiveLines -Text "# packages: write`n# environment: release`n"
Assert-Equal 'comment-only yaml yields no active lines' $commentOnly.Count 0

$noMainGuard = $imageText.Replace('refs/heads/main', 'refs/heads/other')
Assert-Equal 'image workflow main guard missing FAIL' (Test-PublishImageWorkflowContract -Text $noMainGuard) 'FAIL'

$noSourceBind = $imageText.Replace('REQUESTED_SOURCE_SHA', 'REQUESTED_OTHER_SHA')
Assert-Equal 'image workflow source binding missing FAIL' (Test-PublishImageWorkflowContract -Text $noSourceBind) 'FAIL'

$noEnv = $imageText.Replace('environment: release', 'environment: other')
Assert-Equal 'image workflow environment release missing FAIL' (Test-PublishImageWorkflowContract -Text $noEnv) 'FAIL'

$noTagGuard = $contractsText.Replace('GITHUB_REF_TYPE', 'GITHUB_OTHER_TYPE')
Assert-Equal 'contracts tag guard missing FAIL' (Test-PublishContractsWorkflowContract -Text $noTagGuard) 'FAIL'

$noVersionGuard = $contractsText.Replace('getProperty:Version', 'getProperty:Other')
Assert-Equal 'contracts version guard missing FAIL' (Test-PublishContractsWorkflowContract -Text $noVersionGuard) 'FAIL'

$verifyWrite = $verifyText + "`n      packages: write`n"
Assert-Equal 'verify packages write FAIL' (Test-VerifyPublicImageWorkflowContract -Text $verifyWrite) 'FAIL'

$verifyLogin = $verifyText + "`n      " + ('docker' + ' log' + 'in') + "`n"
Assert-Equal 'verify docker login FAIL' (Test-VerifyPublicImageWorkflowContract -Text $verifyLogin) 'FAIL'

$verifyPush = $verifyText + "`n      " + ('docker' + ' pu' + 'sh') + "`n"
Assert-Equal 'verify docker push FAIL' (Test-VerifyPublicImageWorkflowContract -Text $verifyPush) 'FAIL'

$imageMainComment = $imageText.Replace(
    ('          [[ "${GITHUB_REF}" == "refs/heads/main" ]] ' + '\'),
    '          # [[ "${GITHUB_REF}" == "refs/heads/main" ]]'
)
Assert-Equal 'image main guard comment-only FAIL' (Test-PublishImageWorkflowContract -Text $imageMainComment) 'FAIL'

$imageSourceComment = $imageText.Replace(
    ('          [[ "${REQUESTED_SOURCE_SHA}" == "${GITHUB_SHA}" ]] ' + '\'),
    '          # [[ "${REQUESTED_SOURCE_SHA}" == "${GITHUB_SHA}" ]]'
)
Assert-Equal 'image source guard comment-only FAIL' (Test-PublishImageWorkflowContract -Text $imageSourceComment) 'FAIL'

$imageEnvComment = $imageText.Replace('    environment: release', '    # environment: release')
Assert-Equal 'image environment comment-only FAIL' (Test-PublishImageWorkflowContract -Text $imageEnvComment) 'FAIL'

$imageWriteComment = $imageText.Replace('      packages: write', '      # packages: write')
Assert-Equal 'image packages write comment-only FAIL' (Test-PublishImageWorkflowContract -Text $imageWriteComment) 'FAIL'

$contractsTagComment = $contractsText.Replace(
    '          if [ "${GITHUB_REF_TYPE}" != "tag" ]; then',
    '          # if [ "${GITHUB_REF_TYPE}" != "tag" ]; then'
)
Assert-Equal 'contracts tag guard comment-only FAIL' (Test-PublishContractsWorkflowContract -Text $contractsTagComment) 'FAIL'

$contractsVersionComment = $contractsText.Replace(
    '          if [ "${project_version}" != "${package_version}" ]; then',
    '          # if [ "${project_version}" != "${package_version}" ]; then'
)
Assert-Equal 'contracts version guard comment-only FAIL' (Test-PublishContractsWorkflowContract -Text $contractsVersionComment) 'FAIL'

$verifyWriteComment = $verifyText + "`n# packages: write`n"
Assert-Equal 'verify packages write comment-only PASS' (Test-VerifyPublicImageWorkflowContract -Text $verifyWriteComment) 'PASS'

Assert-Equal 'verify actual packages write FAIL' (Test-VerifyPublicImageWorkflowContract -Text $verifyWrite) 'FAIL'

$verifyMutationComment = $verifyText + "`n# " + ('docker' + ' log' + 'in') + "`n# " + ('docker' + ' pu' + 'sh') + "`n"
Assert-Equal 'verify mutation comment-only PASS' (Test-VerifyPublicImageWorkflowContract -Text $verifyMutationComment) 'PASS'

$verifyActualMutation = $verifyText + "`n      - run: " + ('docker' + ' log' + 'in') + "`n"
Assert-Equal 'verify actual mutation FAIL' (Test-VerifyPublicImageWorkflowContract -Text $verifyActualMutation) 'FAIL'

function Add-WorkflowEnvNote {
    param(
        [string]$Text,
        [string]$Note
    )
    $needle = "env:`n  SOURCE_SHA:"
    $insert = "env:`n  NOTE: $Note`n  SOURCE_SHA:"
    if ($Text.IndexOf($needle) -lt 0) {
        return ($Text + "`nenv:`n  NOTE: $Note`n")
    }
    return $Text.Replace($needle, $insert)
}

$imageSmokeMeta = Add-WorkflowEnvNote -Text ($imageText.Replace('bash scripts/release-image-build-smoke.sh', 'true')) -Note 'release-image-build-smoke.sh'
Assert-Equal 'image smoke script env NOTE spoof FAIL' (Test-PublishImageWorkflowContract -Text $imageSmokeMeta) 'FAIL'

$imageReproMeta = Add-WorkflowEnvNote -Text ($imageText.Replace('bash scripts/check-release-image-reproducibility.sh', 'true')) -Note 'check-release-image-reproducibility.sh'
Assert-Equal 'image repro script env NOTE spoof FAIL' (Test-PublishImageWorkflowContract -Text $imageReproMeta) 'FAIL'

$imagePublishMeta = Add-WorkflowEnvNote -Text ($imageText.Replace('bash scripts/publish-release-image.sh', 'true')) -Note 'publish-release-image.sh'
Assert-Equal 'image publish script env NOTE spoof FAIL' (Test-PublishImageWorkflowContract -Text $imagePublishMeta) 'FAIL'

$imageVerifyMeta = Add-WorkflowEnvNote -Text ($imageText.Replace('bash scripts/verify-published-release-image.sh', 'true')) -Note 'verify-published-release-image.sh'
Assert-Equal 'image verify script env NOTE spoof FAIL' (Test-PublishImageWorkflowContract -Text $imageVerifyMeta) 'FAIL'

$imageSmokeDirect = $imageText.Replace('bash scripts/release-image-build-smoke.sh', './scripts/release-image-build-smoke.sh --fixture')
Assert-Equal 'image smoke direct command invocation PASS' (Test-PublishImageWorkflowContract -Text $imageSmokeDirect) 'PASS'

$imageSmokeBashDot = $imageText.Replace('bash scripts/release-image-build-smoke.sh', 'bash ./scripts/release-image-build-smoke.sh --fixture')
Assert-Equal 'image smoke bash command invocation PASS' (Test-PublishImageWorkflowContract -Text $imageSmokeBashDot) 'PASS'

$imageSmokeEcho = $imageText.Replace('bash scripts/release-image-build-smoke.sh', 'echo ./scripts/release-image-build-smoke.sh')
Assert-Equal 'image smoke exact-token echo FAIL' (Test-PublishImageWorkflowContract -Text $imageSmokeEcho) 'FAIL'

$imageSmokePrintf = $imageText.Replace('bash scripts/release-image-build-smoke.sh', "printf './scripts/release-image-build-smoke.sh'")
Assert-Equal 'image smoke printf mention FAIL' (Test-PublishImageWorkflowContract -Text $imageSmokePrintf) 'FAIL'

$imageSmokeMismatchedQuote = $imageText.Replace('bash scripts/release-image-build-smoke.sh', 'bash "./scripts/release-image-build-smoke.sh''')
Assert-Equal 'image smoke mismatched quote FAIL' (Test-PublishImageWorkflowContract -Text $imageSmokeMismatchedQuote) 'FAIL'

$imageSmokeTokenSplice = $imageText.Replace(
    '        run: bash scripts/release-image-build-smoke.sh',
    ('        run: |' + "`n" + '          bash scripts/release-image-build-smoke.sh\' + "`n" + '          -spoofed-suffix')
)
Assert-Equal 'image smoke escaped-newline token splice FAIL' (Test-PublishImageWorkflowContract -Text $imageSmokeTokenSplice) 'FAIL'

$imageSmokeAssignment = $imageText.Replace('bash scripts/release-image-build-smoke.sh', 'SCRIPT=./scripts/release-image-build-smoke.sh')
Assert-Equal 'image smoke variable assignment only FAIL' (Test-PublishImageWorkflowContract -Text $imageSmokeAssignment) 'FAIL'

$imageSmokeComment = $imageText.Replace('bash scripts/release-image-build-smoke.sh', '# ./scripts/release-image-build-smoke.sh')
Assert-Equal 'image smoke comment-only occurrence FAIL' (Test-PublishImageWorkflowContract -Text $imageSmokeComment) 'FAIL'

$imageSmokeWrongJob = $imageText.Replace('bash scripts/release-image-build-smoke.sh', 'true')
$imageSmokeWrongJob = $imageSmokeWrongJob.Replace(
    "jobs:`n  publish:",
    "jobs:`n  unrelated:`n    runs-on: ubuntu-latest`n    steps:`n      - run: bash ./scripts/release-image-build-smoke.sh`n  publish:"
)
Assert-Equal 'image smoke unrelated executable job FAIL' (Test-PublishImageWorkflowContract -Text $imageSmokeWrongJob) 'FAIL'

$verifyCmpSource = ('          [[ "${identity_source_sha}" == "${SOURCE_SHA}" ]] ' + '\')
$verifyCmpVersion = ('          [[ "${identity_version}" == "${MAILER_VERSION}" ]] ' + '\')
$verifyCmpDigest = ('          [[ "${identity_digest}" == "${EXPECTED_DIGEST}" ]] ' + '\')
$verifyFailSource = "            || { echo '::error::artifact source SHA does not match publication_source_sha'; exit 1; }"
$verifyEchoSpoof = $verifyText.Replace($verifyCmpSource, '').Replace($verifyCmpVersion, '').Replace($verifyCmpDigest, '')
$verifyEchoSpoof = $verifyEchoSpoof + "`n          echo sourceCommitSha releaseVersion EXPECTED_DIGEST`n"
Assert-Equal 'verify binding echo spoof FAIL' (Test-VerifyPublicImageWorkflowContract -Text $verifyEchoSpoof) 'FAIL'

$verifyExactEcho = $verifyText.Replace($verifyCmpSource, '          echo "identity_source_sha == SOURCE_SHA" ' + '\')
Assert-Equal 'verify exact comparison text inside echo FAIL' (Test-VerifyPublicImageWorkflowContract -Text $verifyExactEcho) 'FAIL'

$verifyComparisonMissing = $verifyText.Replace($verifyCmpSource, '').Replace($verifyFailSource, '')
Assert-Equal 'verify source comparison missing FAIL' (Test-VerifyPublicImageWorkflowContract -Text $verifyComparisonMissing) 'FAIL'

$verifyWrongLeft = $verifyText.Replace($verifyCmpSource, ('          [[ "${wrong_source_sha}" == "${SOURCE_SHA}" ]] ' + '\'))
Assert-Equal 'verify wrong left-hand binding FAIL' (Test-VerifyPublicImageWorkflowContract -Text $verifyWrongLeft) 'FAIL'

$verifyWrongRight = $verifyText.Replace($verifyCmpSource, ('          [[ "${identity_source_sha}" == "${WRONG_SHA}" ]] ' + '\'))
Assert-Equal 'verify wrong right-hand binding FAIL' (Test-VerifyPublicImageWorkflowContract -Text $verifyWrongRight) 'FAIL'

$verifySwapped = $verifyText.Replace($verifyCmpSource, ('          [[ "${identity_source_sha}" == "${MAILER_VERSION}" ]] ' + '\'))
$verifySwapped = $verifySwapped.Replace($verifyCmpVersion, ('          [[ "${identity_version}" == "${EXPECTED_DIGEST}" ]] ' + '\'))
$verifySwapped = $verifySwapped.Replace($verifyCmpDigest, ('          [[ "${identity_digest}" == "${SOURCE_SHA}" ]] ' + '\'))
Assert-Equal 'verify source version digest relationships swapped FAIL' (Test-VerifyPublicImageWorkflowContract -Text $verifySwapped) 'FAIL'

$verifyComparisonAssignment = $verifyText.Replace($verifyCmpSource, '          CHECK="identity_source_sha == SOURCE_SHA" ' + '\')
Assert-Equal 'verify comparison variable assignment only FAIL' (Test-VerifyPublicImageWorkflowContract -Text $verifyComparisonAssignment) 'FAIL'

$verifyComparisonComment = $verifyText.Replace($verifyCmpSource, '          # [[ "${identity_source_sha}" == "${SOURCE_SHA}" ]] ' + '\')
Assert-Equal 'verify comparison comment-only occurrence FAIL' (Test-VerifyPublicImageWorkflowContract -Text $verifyComparisonComment) 'FAIL'

$verifyNoFailClose = $verifyText.Replace($verifyCmpSource, '          [[ "${identity_source_sha}" == "${SOURCE_SHA}" ]]').Replace($verifyFailSource, '')
Assert-Equal 'verify comparison without fail-close FAIL' (Test-VerifyPublicImageWorkflowContract -Text $verifyNoFailClose) 'FAIL'

$verifyCrossStepFailClose = $verifyText.Replace(
    ($verifyCmpSource + "`n" + $verifyFailSource),
    ($verifyCmpSource + "`n" + "      - name: Unrelated fail-close text`n        run: |`n" + $verifyFailSource)
)
Assert-Equal 'verify fail-close from unrelated step FAIL' (Test-VerifyPublicImageWorkflowContract -Text $verifyCrossStepFailClose) 'FAIL'

$imageRun = New-DispatchRun -Id '32726533661' -Path '.github/workflows/publish-release-image.yml' -HeadSha $MainSha
$imageRunObs = New-ReadyPreflightObservers -Sha $MainSha
$imageRunObs['WorkflowRuns'] = { param($shaArg) @($imageRun) }.GetNewClosure()
$imageRunMap = Invoke-PreflightFixture -Observers $imageRunObs
[void]$script:PreflightMaps.Add($imageRunMap)
Assert-Equal 'preflight image run candidate' $imageRunMap['IMAGE_PUBLISH_RUN'] 'CANDIDATE_PRESENT'
Assert-Equal 'preflight image run candidate id' $imageRunMap['IMAGE_PUBLISH_RUN_ID'] '32726533661'
Assert-Equal 'preflight image run candidate result' $imageRunMap['PREFLIGHT_RESULT'] 'FAIL'
Assert-Equal 'preflight image run candidate ready' $imageRunMap['TECHNICAL_READINESS'] 'STOP'

$imageRun2 = New-DispatchRun -Id '32726533662' -Path '.github/workflows/publish-release-image.yml' -HeadSha $MainSha
$imageAmbObs = New-ReadyPreflightObservers -Sha $MainSha
$imageAmbObs['WorkflowRuns'] = { param($shaArg) @($imageRun, $imageRun2) }.GetNewClosure()
$imageAmbMap = Invoke-PreflightFixture -Observers $imageAmbObs
[void]$script:PreflightMaps.Add($imageAmbMap)
Assert-Equal 'preflight image runs ambiguous' $imageAmbMap['IMAGE_PUBLISH_RUN'] 'AMBIGUOUS'
Assert-Equal 'preflight image runs ambiguous id' $imageAmbMap['IMAGE_PUBLISH_RUN_ID'] 'NONE'
Assert-Equal 'preflight image runs ambiguous ready' $imageAmbMap['TECHNICAL_READINESS'] 'STOP'

$nugetRun = New-DispatchRun -Id '32728423486' -Path '.github/workflows/publish-contracts.yml' -HeadSha $MainSha
$nugetRunObs = New-ReadyPreflightObservers -Sha $MainSha
$nugetRunObs['WorkflowRuns'] = { param($shaArg) @($nugetRun) }.GetNewClosure()
$nugetRunMap = Invoke-PreflightFixture -Observers $nugetRunObs
[void]$script:PreflightMaps.Add($nugetRunMap)
Assert-Equal 'preflight nuget run candidate' $nugetRunMap['NUGET_PUBLISH_RUN'] 'CANDIDATE_PRESENT'
Assert-Equal 'preflight nuget run candidate id' $nugetRunMap['NUGET_PUBLISH_RUN_ID'] '32728423486'
Assert-Equal 'preflight nuget run candidate ready' $nugetRunMap['TECHNICAL_READINESS'] 'STOP'

$actionsIncompleteObs = New-ReadyPreflightObservers -Sha $MainSha
$actionsIncompleteObs['WorkflowRuns'] = { param($shaArg) 'INCOMPLETE' }
$actionsIncompleteMap = Invoke-PreflightFixture -Observers $actionsIncompleteObs
[void]$script:PreflightMaps.Add($actionsIncompleteMap)
Assert-Equal 'preflight actions API incomplete image' $actionsIncompleteMap['IMAGE_PUBLISH_RUN'] 'INCOMPLETE'
Assert-Equal 'preflight actions API incomplete nuget' $actionsIncompleteMap['NUGET_PUBLISH_RUN'] 'INCOMPLETE'
Assert-Equal 'preflight actions API incomplete result' $actionsIncompleteMap['PREFLIGHT_RESULT'] 'INCOMPLETE'
Assert-Equal 'preflight actions API incomplete ready' $actionsIncompleteMap['TECHNICAL_READINESS'] 'STOP'

$preflightHuman = $true
$preflightMutation = $true
foreach ($item in $script:PreflightMaps) {
    if ($item['HUMAN_AUTHORIZATION_REQUIRED'] -ne 'TRUE') { $preflightHuman = $false }
    if ($item['MUTATION_PERFORMED'] -ne 'FALSE') { $preflightMutation = $false }
}
Assert-True 'every preflight path HUMAN_AUTHORIZATION_REQUIRED=TRUE' $preflightHuman 'a preflight map dropped HUMAN_AUTHORIZATION_REQUIRED'
Assert-True 'every preflight path MUTATION_PERFORMED=FALSE' $preflightMutation 'a preflight map set MUTATION_PERFORMED'

$preflightLines = Format-ReleasePreflightLines -Map $readyMap
Assert-Equal 'preflight format starts COMMAND' $preflightLines[0] 'COMMAND=PREFLIGHT'
Assert-Equal 'preflight format version key' $preflightLines[1] 'VERSION=1.3.5'
Assert-Equal 'preflight format sha key' $preflightLines[2] ('RELEASE_COMMIT_SHA=' + $MainSha)
Assert-Equal 'preflight format ends mutation' $preflightLines[$preflightLines.Count - 1] 'MUTATION_PERFORMED=FALSE'
Assert-Equal 'preflight format human key' $preflightLines[$preflightLines.Count - 2] 'HUMAN_AUTHORIZATION_REQUIRED=TRUE'
Assert-Equal 'preflight format key count' $preflightLines.Count 21

$changelogHit = Test-ChangelogHasReleaseEntry -Text "## [1.3.5] - 2026-08-26`n" -Version '1.3.5'
Assert-Equal 'changelog entry present' $changelogHit $true
$changelogMiss = Test-ChangelogHasReleaseEntry -Text "## [Unreleased]`n" -Version '1.3.5'
Assert-Equal 'changelog entry missing' $changelogMiss $false

$zeroRuns = ConvertTo-WorkflowDispatchRunObservation -Runs @() -WorkflowPath '.github/workflows/publish-release-image.yml' -ReleaseCommitSha $MainSha
Assert-Equal 'zero dispatch runs ABSENT' $zeroRuns.State 'ABSENT'
$nullRuns = ConvertTo-WorkflowDispatchRunObservation -Runs $null -WorkflowPath '.github/workflows/publish-release-image.yml' -ReleaseCommitSha $MainSha
Assert-Equal 'null dispatch runs INCOMPLETE' $nullRuns.State 'INCOMPLETE'

$twoRunJson = '{"total_count":2,"workflow_runs":[{"id":32726533661,"path":".github/workflows/publish-release-image.yml","event":"workflow' + '_dispatch","head_sha":"' + $MainSha + '","status":"completed","conclusion":"success"},{"id":32728423486,"path":".github/workflows/publish-contracts.yml","event":"workflow' + '_dispatch","head_sha":"' + $MainSha + '","status":"completed","conclusion":"success"}]}'
$parsedRuns = Convert-GitHubWorkflowRunsJson -Json $twoRunJson
Assert-Equal 'parsed workflow run count' $parsedRuns.Count 2
Assert-Equal 'parsed image run id' $parsedRuns[0].Id '32726533661'
Assert-Equal 'parsed nuget run id' $parsedRuns[1].Id '32728423486'

Assert-Equal 'version prep PASS' (Get-VersionPrepState -Alignment 'PASS' -ChangelogHasEntry 'PASS' -ReleaseRecord 'PENDING') 'PASS'
Assert-Equal 'version prep FAIL over INCOMPLETE' (Get-VersionPrepState -Alignment 'FAIL' -ChangelogHasEntry 'INCOMPLETE' -ReleaseRecord 'PENDING') 'FAIL'

# --- RO-3 verify ---
function New-PublishedGhcrFact {
    param(
        [string]$Revision = $RelSha,
        [string]$Digest = $DigestA,
        [string]$OciVersion = '1.3.4',
        [string]$ShaTagState = 'PRESENT',
        [string]$ShaTagDigest = $DigestA
    )
    return New-ArtifactFact -State 'PRESENT' -Digest $Digest -Revision $Revision -OciVersion $OciVersion -ShaTagState $ShaTagState -ShaTagDigest $ShaTagDigest
}

function New-PublishedRecordText {
    param(
        [string]$Sha = $RelSha,
        [string]$Digest = $DigestA
    )
    return @"
> Status: **PUBLISHED**

## Release identity

- releaseCommitSha: ``$Sha``

## GHCR

- public OCI digest:
  ``$Digest``
"@
}

function New-ReadyVerifyObservers {
    param(
        [string]$Sha = $RelSha,
        [string]$Version = '1.3.4',
        [string]$Digest = $DigestA
    )
    $recordText = New-PublishedRecordText -Sha $Sha -Digest $Digest
    return @{
        GitTag          = { param($ver) New-ArtifactFact -State 'PRESENT' -TargetSha $Sha }.GetNewClosure()
        GitHubRelease   = { param($ver) New-ArtifactFact -State 'PRESENT' }.GetNewClosure()
        Nuget           = { param($ver) New-ArtifactFact -State 'PRESENT' }.GetNewClosure()
        SourceVersions  = {
            param($shaArg, $verArg)
            [pscustomobject]@{
                ContractsState   = 'PRESENT'
                ContractsVersion = $verArg
                OpenApiState     = 'PRESENT'
                OpenApiVersion   = $verArg
            }
        }.GetNewClosure()
        NugetRevision   = { param($ver) [pscustomobject]@{ State = 'PRESENT'; Commit = $Sha; Reason = '' } }.GetNewClosure()
        ReleaseRecord   = { param($ver, $shaArg) [pscustomobject]@{ State = 'PRESENT'; Text = $recordText; Reason = '' } }.GetNewClosure()
        Ghcr            = { param($ver, $shaArg) New-PublishedGhcrFact -Revision $Sha -Digest $Digest -OciVersion $ver }.GetNewClosure()
    }
}

function Invoke-VerifyFixture {
    param($Observers)
    return Invoke-ReleaseVerify -Version '1.3.4' -ReleaseCommitSha $RelSha -RepoRoot $RepoRoot -Observers $Observers -Quiet
}

$script:VerifyMaps = New-Object System.Collections.Generic.List[object]
$readyVerifyObs = New-ReadyVerifyObservers
$readyVerifyMap = Invoke-VerifyFixture -Observers $readyVerifyObs
[void]$script:VerifyMaps.Add($readyVerifyMap)
Assert-Equal 'verify published PASS' $readyVerifyMap['VERIFY_RESULT'] 'PASS'
Assert-Equal 'verify published git tag' $readyVerifyMap['GIT_TAG'] 'EXACT_MATCH'
Assert-Equal 'verify published contracts' $readyVerifyMap['CONTRACTS_SOURCE'] 'EXACT_MATCH'
Assert-Equal 'verify published openapi' $readyVerifyMap['OPENAPI'] 'EXACT_MATCH'
Assert-Equal 'verify published nuget package' $readyVerifyMap['NUGET_PACKAGE'] 'EXACT_MATCH'
Assert-Equal 'verify published nuget revision' $readyVerifyMap['NUGET_SOURCE_REVISION'] 'EXACT_MATCH'
Assert-Equal 'verify published ghcr version tag' $readyVerifyMap['GHCR_VERSION_TAG'] 'EXACT_MATCH'
Assert-Equal 'verify published ghcr sha tag' $readyVerifyMap['GHCR_SHA_TAG'] 'EXACT_MATCH'
Assert-Equal 'verify published digest binding' $readyVerifyMap['GHCR_DIGEST_BINDING'] 'EXACT_MATCH'
Assert-Equal 'verify published oci revision' $readyVerifyMap['OCI_REVISION'] 'EXACT_MATCH'
Assert-Equal 'verify published oci version' $readyVerifyMap['OCI_VERSION'] 'EXACT_MATCH'
Assert-Equal 'verify published github release' $readyVerifyMap['GITHUB_RELEASE'] 'EXACT_MATCH'
Assert-Equal 'verify published release record' $readyVerifyMap['RELEASE_RECORD'] 'EXACT_MATCH'
Assert-Equal 'verify published public digest' $readyVerifyMap['PUBLIC_DIGEST'] $DigestA
Assert-Equal 'verify published mutation' $readyVerifyMap['MUTATION_PERFORMED'] 'FALSE'
Assert-Equal 'verify published command' $readyVerifyMap['COMMAND'] 'VERIFY'

$tagMismatchObs = New-ReadyVerifyObservers
$tagMismatchObs['GitTag'] = { param($ver) New-ArtifactFact -State 'PRESENT' -TargetSha $WrongSha }
$tagMismatchMap = Invoke-VerifyFixture -Observers $tagMismatchObs
[void]$script:VerifyMaps.Add($tagMismatchMap)
Assert-Equal 'verify tag target mismatch FAIL' $tagMismatchMap['VERIFY_RESULT'] 'FAIL'
Assert-Equal 'verify tag target mismatch state' $tagMismatchMap['GIT_TAG'] 'CONFLICT'

$contractsMismatchObs = New-ReadyVerifyObservers
$contractsMismatchObs['SourceVersions'] = {
    param($shaArg, $verArg)
    [pscustomobject]@{
        ContractsState   = 'PRESENT'
        ContractsVersion = '1.3.5'
        OpenApiState     = 'PRESENT'
        OpenApiVersion   = $verArg
    }
}
$contractsMismatchMap = Invoke-VerifyFixture -Observers $contractsMismatchObs
[void]$script:VerifyMaps.Add($contractsMismatchMap)
Assert-Equal 'verify contracts mismatch FAIL' $contractsMismatchMap['VERIFY_RESULT'] 'FAIL'
Assert-Equal 'verify contracts mismatch state' $contractsMismatchMap['CONTRACTS_SOURCE'] 'CONFLICT'

$openapiMismatchObs = New-ReadyVerifyObservers
$openapiMismatchObs['SourceVersions'] = {
    param($shaArg, $verArg)
    [pscustomobject]@{
        ContractsState   = 'PRESENT'
        ContractsVersion = $verArg
        OpenApiState     = 'PRESENT'
        OpenApiVersion   = '9.9.9'
    }
}
$openapiMismatchMap = Invoke-VerifyFixture -Observers $openapiMismatchObs
[void]$script:VerifyMaps.Add($openapiMismatchMap)
Assert-Equal 'verify openapi mismatch FAIL' $openapiMismatchMap['VERIFY_RESULT'] 'FAIL'
Assert-Equal 'verify openapi mismatch state' $openapiMismatchMap['OPENAPI'] 'CONFLICT'

$digestMismatchObs = New-ReadyVerifyObservers
$digestMismatchObs['Ghcr'] = { param($ver, $shaArg) New-PublishedGhcrFact -Revision $RelSha -Digest $DigestA -ShaTagDigest $DigestB }
$digestMismatchMap = Invoke-VerifyFixture -Observers $digestMismatchObs
[void]$script:VerifyMaps.Add($digestMismatchMap)
Assert-Equal 'verify digest mismatch FAIL' $digestMismatchMap['VERIFY_RESULT'] 'FAIL'
Assert-Equal 'verify digest mismatch binding' $digestMismatchMap['GHCR_DIGEST_BINDING'] 'CONFLICT'

$ociRevisionMismatchObs = New-ReadyVerifyObservers
$ociRevisionMismatchObs['Ghcr'] = { param($ver, $shaArg) New-PublishedGhcrFact -Revision $WrongSha -Digest $DigestA -OciVersion $ver }
$ociRevisionMismatchMap = Invoke-VerifyFixture -Observers $ociRevisionMismatchObs
[void]$script:VerifyMaps.Add($ociRevisionMismatchMap)
Assert-Equal 'verify oci revision mismatch FAIL' $ociRevisionMismatchMap['VERIFY_RESULT'] 'FAIL'
Assert-Equal 'verify oci revision mismatch state' $ociRevisionMismatchMap['OCI_REVISION'] 'CONFLICT'

$ociVersionMismatchObs = New-ReadyVerifyObservers
$ociVersionMismatchObs['Ghcr'] = { param($ver, $shaArg) New-PublishedGhcrFact -Revision $RelSha -Digest $DigestA -OciVersion '9.9.9' }
$ociVersionMismatchMap = Invoke-VerifyFixture -Observers $ociVersionMismatchObs
[void]$script:VerifyMaps.Add($ociVersionMismatchMap)
Assert-Equal 'verify oci version mismatch FAIL' $ociVersionMismatchMap['VERIFY_RESULT'] 'FAIL'
Assert-Equal 'verify oci version mismatch state' $ociVersionMismatchMap['OCI_VERSION'] 'CONFLICT'

$nugetRevisionMismatchObs = New-ReadyVerifyObservers
$nugetRevisionMismatchObs['NugetRevision'] = { param($ver) [pscustomobject]@{ State = 'PRESENT'; Commit = $WrongSha; Reason = '' } }
$nugetRevisionMismatchMap = Invoke-VerifyFixture -Observers $nugetRevisionMismatchObs
[void]$script:VerifyMaps.Add($nugetRevisionMismatchMap)
Assert-Equal 'verify nuget revision mismatch FAIL' $nugetRevisionMismatchMap['VERIFY_RESULT'] 'FAIL'
Assert-Equal 'verify nuget revision mismatch state' $nugetRevisionMismatchMap['NUGET_SOURCE_REVISION'] 'CONFLICT'

$githubReleaseMismatchObs = New-ReadyVerifyObservers
$githubReleaseMismatchObs['GitHubRelease'] = { param($ver) New-ArtifactFact -State 'CONFLICT' -Reason 'TAG_NAME' }
$githubReleaseMismatchMap = Invoke-VerifyFixture -Observers $githubReleaseMismatchObs
[void]$script:VerifyMaps.Add($githubReleaseMismatchMap)
Assert-Equal 'verify github release mismatch FAIL' $githubReleaseMismatchMap['VERIFY_RESULT'] 'FAIL'
Assert-Equal 'verify github release mismatch state' $githubReleaseMismatchMap['GITHUB_RELEASE'] 'CONFLICT'

$recordDigestMismatchObs = New-ReadyVerifyObservers
$recordDigestMismatchObs['ReleaseRecord'] = {
    param($ver, $shaArg)
    [pscustomobject]@{
        State  = 'PRESENT'
        Text   = (New-PublishedRecordText -Sha $RelSha -Digest $DigestB)
        Reason = ''
    }
}
$recordDigestMismatchMap = Invoke-VerifyFixture -Observers $recordDigestMismatchObs
[void]$script:VerifyMaps.Add($recordDigestMismatchMap)
Assert-Equal 'verify release record digest mismatch FAIL' $recordDigestMismatchMap['VERIFY_RESULT'] 'FAIL'
Assert-Equal 'verify release record digest mismatch state' $recordDigestMismatchMap['RELEASE_RECORD'] 'CONFLICT'

$ghcrAbsentObs = New-ReadyVerifyObservers
$ghcrAbsentObs['Ghcr'] = { param($ver, $shaArg) New-ArtifactFact -State 'ABSENT' }
$ghcrAbsentMap = Invoke-VerifyFixture -Observers $ghcrAbsentObs
[void]$script:VerifyMaps.Add($ghcrAbsentMap)
Assert-Equal 'verify ghcr absent FAIL' $ghcrAbsentMap['VERIFY_RESULT'] 'FAIL'
Assert-Equal 'verify ghcr absent version tag' $ghcrAbsentMap['GHCR_VERSION_TAG'] 'ABSENT'

$nugetAbsentObs = New-ReadyVerifyObservers
$nugetAbsentObs['Nuget'] = { param($ver) New-ArtifactFact -State 'ABSENT' }
$nugetAbsentMap = Invoke-VerifyFixture -Observers $nugetAbsentObs
[void]$script:VerifyMaps.Add($nugetAbsentMap)
Assert-Equal 'verify nuget absent FAIL' $nugetAbsentMap['VERIFY_RESULT'] 'FAIL'
Assert-Equal 'verify nuget absent package' $nugetAbsentMap['NUGET_PACKAGE'] 'ABSENT'
Assert-Equal 'verify nuget absent revision' $nugetAbsentMap['NUGET_SOURCE_REVISION'] 'ABSENT'

$gitTagAbsentObs = New-ReadyVerifyObservers
$gitTagAbsentObs['GitTag'] = { param($ver) New-ArtifactFact -State 'ABSENT' }
$gitTagAbsentMap = Invoke-VerifyFixture -Observers $gitTagAbsentObs
[void]$script:VerifyMaps.Add($gitTagAbsentMap)
Assert-Equal 'verify git tag absent FAIL' $gitTagAbsentMap['VERIFY_RESULT'] 'FAIL'
Assert-Equal 'verify git tag absent state' $gitTagAbsentMap['GIT_TAG'] 'ABSENT'

$transportObs = New-ReadyVerifyObservers
$transportObs['Nuget'] = { param($ver) New-ArtifactFact -State 'INCOMPLETE' -Reason 'NETWORK' }
$transportMap = Invoke-VerifyFixture -Observers $transportObs
[void]$script:VerifyMaps.Add($transportMap)
Assert-Equal 'verify transport INCOMPLETE' $transportMap['VERIFY_RESULT'] 'INCOMPLETE'
Assert-Equal 'verify transport nuget state' $transportMap['NUGET_PACKAGE'] 'INCOMPLETE'

$authObs = New-ReadyVerifyObservers
$authObs['GitTag'] = { param($ver) New-ArtifactFact -State 'INCOMPLETE' -Reason 'AUTH' }
$authMap = Invoke-VerifyFixture -Observers $authObs
[void]$script:VerifyMaps.Add($authMap)
Assert-Equal 'verify auth INCOMPLETE' $authMap['VERIFY_RESULT'] 'INCOMPLETE'
Assert-Equal 'verify auth git tag state' $authMap['GIT_TAG'] 'INCOMPLETE'

$rateObs = New-ReadyVerifyObservers
$rateObs['Ghcr'] = { param($ver, $shaArg) New-ArtifactFact -State 'INCOMPLETE' -Reason 'RATE_LIMIT' }
$rateMap = Invoke-VerifyFixture -Observers $rateObs
[void]$script:VerifyMaps.Add($rateMap)
Assert-Equal 'verify rate limit INCOMPLETE' $rateMap['VERIFY_RESULT'] 'INCOMPLETE'
Assert-Equal 'verify rate limit ghcr state' $rateMap['GHCR_VERSION_TAG'] 'INCOMPLETE'

$fivexxObs = New-ReadyVerifyObservers
$fivexxObs['ReleaseRecord'] = { param($ver, $shaArg) [pscustomobject]@{ State = 'INCOMPLETE'; Text = ''; Reason = 'HTTP_5XX' } }
$fivexxMap = Invoke-VerifyFixture -Observers $fivexxObs
[void]$script:VerifyMaps.Add($fivexxMap)
Assert-Equal 'verify 5xx INCOMPLETE' $fivexxMap['VERIFY_RESULT'] 'INCOMPLETE'
Assert-Equal 'verify 5xx release record state' $fivexxMap['RELEASE_RECORD'] 'INCOMPLETE'

$parseObs = New-ReadyVerifyObservers
$parseObs['NugetRevision'] = { param($ver) [pscustomobject]@{ State = 'PRESENT'; Commit = ''; Reason = 'NUSPEC_PARSE' } }
$parseMap = Invoke-VerifyFixture -Observers $parseObs
[void]$script:VerifyMaps.Add($parseMap)
Assert-Equal 'verify parse failure INCOMPLETE' $parseMap['VERIFY_RESULT'] 'INCOMPLETE'
Assert-Equal 'verify parse failure nuget revision' $parseMap['NUGET_SOURCE_REVISION'] 'INCOMPLETE'

$mixedSourceObs = New-ReadyVerifyObservers
$mixedSourceObs['GitTag'] = { param($ver) New-ArtifactFact -State 'PRESENT' -TargetSha $RelSha }
$mixedSourceObs['Ghcr'] = { param($ver, $shaArg) New-PublishedGhcrFact -Revision $WrongSha -Digest $DigestA -OciVersion $ver }
$mixedSourceMap = Invoke-VerifyFixture -Observers $mixedSourceObs
[void]$script:VerifyMaps.Add($mixedSourceMap)
Assert-Equal 'verify mixed source identities FAIL' $mixedSourceMap['VERIFY_RESULT'] 'FAIL'
Assert-Equal 'verify mixed source git tag' $mixedSourceMap['GIT_TAG'] 'EXACT_MATCH'
Assert-Equal 'verify mixed source oci revision' $mixedSourceMap['OCI_REVISION'] 'CONFLICT'

Assert-Equal 'nuspec commit parse' (Get-NugetRepositoryCommitFromNuspecText -Text '<repository type="git" commit="0123456789012345678901234567890123456789" />') '0123456789012345678901234567890123456789'
Assert-Equal 'record sha parse' (Get-ReleaseRecordCommitShaFromText -Text (New-PublishedRecordText -Sha $RelSha -Digest $DigestA)) $RelSha
Assert-Equal 'record digest parse' (Get-ReleaseRecordDigestFromText -Text (New-PublishedRecordText -Sha $RelSha -Digest $DigestA)) $DigestA
$configVersion = '{"config":{"Labels":{"org.opencontainers.image.version":"1.3.4","org.opencontainers.image.revision":"' + $RelSha + '"}}}'
Assert-Equal 'oci version parse' (Get-OciVersionFromConfigText -ConfigText $configVersion) '1.3.4'
Assert-Equal 'git tag verify EXACT_MATCH' (ConvertTo-GitTagVerifyState -TagFact (New-ArtifactFact -State 'PRESENT' -TargetSha $RelSha) -ReleaseCommitSha $RelSha) 'EXACT_MATCH'
Assert-Equal 'git tag verify CONFLICT' (ConvertTo-GitTagVerifyState -TagFact (New-ArtifactFact -State 'PRESENT' -TargetSha $WrongSha) -ReleaseCommitSha $RelSha) 'CONFLICT'
Assert-Equal 'digest binding EXACT_MATCH' (ConvertTo-GhcrDigestBindingVerifyState -VersionFact (New-PublishedGhcrFact) -ShaTagState 'PRESENT' -ShaTagDigest $DigestA) 'EXACT_MATCH'
Assert-Equal 'digest binding CONFLICT' (ConvertTo-GhcrDigestBindingVerifyState -VersionFact (New-PublishedGhcrFact -Digest $DigestA) -ShaTagState 'PRESENT' -ShaTagDigest $DigestB) 'CONFLICT'
Assert-Equal 'release record verify EXACT_MATCH' (ConvertTo-ReleaseRecordVerifyState -FetchState 'PRESENT' -Text (New-PublishedRecordText -Sha $RelSha -Digest $DigestA) -Version '1.3.4' -ReleaseCommitSha $RelSha -ObservedDigest $DigestA) 'EXACT_MATCH'
Assert-Equal 'release record pending CONFLICT' (ConvertTo-ReleaseRecordVerifyState -FetchState 'PRESENT' -Text '> Status: **PENDING**' -Version '1.3.4' -ReleaseCommitSha $RelSha -ObservedDigest $DigestA) 'CONFLICT'

$verifyMutationOk = $true
foreach ($item in $script:VerifyMaps) {
    if ($item['MUTATION_PERFORMED'] -ne 'FALSE') { $verifyMutationOk = $false }
}
Assert-True 'every verify path MUTATION_PERFORMED=FALSE' $verifyMutationOk 'a verify map set MUTATION_PERFORMED'

$verifyLines = Format-ReleaseVerifyLines -Map $readyVerifyMap
Assert-Equal 'verify format starts COMMAND' $verifyLines[0] 'COMMAND=VERIFY'
Assert-Equal 'verify format ends mutation' $verifyLines[$verifyLines.Count - 1] 'MUTATION_PERFORMED=FALSE'
Assert-Equal 'verify format key count' $verifyLines.Count 18

# --- self-test source stays ASCII ---
$sourceBytes = [System.IO.File]::ReadAllBytes($PSCommandPath)
$nonAscii = 0
foreach ($b in $sourceBytes) {
    if ($b -gt 127) { $nonAscii++ }
}
Assert-Equal 'self-test source is ASCII' $nonAscii 0

Write-Host ''
Write-Host ("Self-test result: {0} passed, {1} failed" -f $script:PassCount, $script:FailCount)
if ($script:FailCount -gt 0) {
    exit 1
}
exit 0
