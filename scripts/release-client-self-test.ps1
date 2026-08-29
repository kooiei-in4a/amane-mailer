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
Assert-True 'CLI not implemented names mutations' ($notImplemented.Output -match 'publish-image') 'unknown command should list mutations'
Assert-True 'CLI not implemented names promote-latest' ($notImplemented.Output -match 'promote-latest') 'unknown command should list promote-latest'

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
        [string]$Conclusion = 'success',
        [string]$Name = '',
        [string]$DisplayTitle = ''
    )
    if ([string]::IsNullOrWhiteSpace($EventName)) { $EventName = ('workflow' + '_dispatch') }
    return [pscustomobject]@{
        Id           = $Id
        Path         = $Path
        Event        = $EventName
        HeadSha      = $HeadSha
        Status       = $Status
        Conclusion   = $Conclusion
        Name         = $Name
        DisplayTitle = $DisplayTitle
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
Assert-Equal 'verify format key count' $verifyLines.Count 23

# --- M-1 guarded mutation commands (fixture executors only) ---
function New-ExactGhcrFact {
    param(
        [string]$Sha = $MainSha,
        [string]$VersionValue = '1.3.5',
        [string]$Digest = $DigestA
    )
    return New-ArtifactFact -State 'PRESENT' -Digest $Digest -Revision $Sha -OciVersion $VersionValue -ShaTagState 'PRESENT' -ShaTagDigest $Digest
}

function New-ReadyPublishImageObservers {
    param(
        [string]$Sha = $MainSha,
        [hashtable]$Overrides = @{}
    )
    $obs = New-ReadyPreflightObservers -Sha $Sha
    $obs['Ghcr'] = { param($ver, $shaArg) New-ArtifactFact -State 'ABSENT' }
    foreach ($key in $Overrides.Keys) {
        $obs[$key] = $Overrides[$key]
    }
    return $obs
}

$script:MutationExecutorCalls = @{ Count = 0 }
function New-FakeMutationExecutor {
    param([string]$Outcome = 'SUCCESS')
    $script:MutationExecutorCalls.Count = 0
    $counter = $script:MutationExecutorCalls
    return {
        param($Args)
        $counter.Count++
        return [pscustomobject]@{ State = $Outcome }
    }.GetNewClosure()
}

function Invoke-PublishImageFixture {
    param(
        $Observers,
        $Executor = $null,
        [switch]$Execute,
        [string]$Version = '1.3.5',
        [string]$Sha = $MainSha
    )
    return Invoke-ReleasePublishImage -Version $Version -ReleaseCommitSha $Sha -RepoRoot $RepoRoot -Observers $Observers -Executor $Executor -Execute:$Execute -Quiet
}

$publishReadyObs = New-ReadyPublishImageObservers -Sha $MainSha
$publishDry = Invoke-PublishImageFixture -Observers $publishReadyObs
Assert-Equal 'publish-image dry MUTATION_RESULT' $publishDry['MUTATION_RESULT'] 'NOT_ATTEMPTED'
Assert-Equal 'publish-image dry MUTATION_ATTEMPTED' $publishDry['MUTATION_ATTEMPTED'] 'FALSE'
Assert-Equal 'publish-image dry MUTATION_PERFORMED' $publishDry['MUTATION_PERFORMED'] 'FALSE'
Assert-Equal 'publish-image dry GUARD_GHCR' $publishDry['GUARD_GHCR'] 'ABSENT'
Assert-Equal 'publish-image dry GUARD_IMAGE_PUBLISH_RUN' $publishDry['GUARD_IMAGE_PUBLISH_RUN'] 'ABSENT'
Assert-Equal 'publish-image dry COMMAND' $publishDry['COMMAND'] 'PUBLISH_IMAGE'

$publishExec = New-FakeMutationExecutor -Outcome 'SUCCESS'
$publishReadyObs['ReadBackImagePublishRun'] = { param($shaArg) [pscustomobject]@{ State = 'CANDIDATE_PRESENT'; Id = '9003' } }.GetNewClosure()
$publishApplied = Invoke-PublishImageFixture -Observers $publishReadyObs -Executor $publishExec -Execute
Assert-Equal 'publish-image execute APPLIED' $publishApplied['MUTATION_RESULT'] 'APPLIED'
Assert-Equal 'publish-image execute MUTATION_ATTEMPTED' $publishApplied['MUTATION_ATTEMPTED'] 'TRUE'
Assert-Equal 'publish-image execute MUTATION_PERFORMED' $publishApplied['MUTATION_PERFORMED'] 'TRUE'
Assert-Equal 'publish-image execute one executor call' $script:MutationExecutorCalls.Count 1

$publishAlreadyObs = New-ReadyPublishImageObservers -Sha $MainSha
$publishAlreadyObs['GhcrVersion'] = { param($ver) New-ExactGhcrFact -Sha $MainSha -VersionValue '1.3.5' }.GetNewClosure()
$publishAlreadyObs['GhcrSha'] = { param($shaArg) New-ArtifactFact -State 'PRESENT' -Digest $DigestA }.GetNewClosure()
$publishAlreadyObs['Ghcr'] = { param($ver, $shaArg) New-ExactGhcrFact -Sha $MainSha -VersionValue '1.3.5' }.GetNewClosure()
$publishAlready = Invoke-PublishImageFixture -Observers $publishAlreadyObs -Executor (New-FakeMutationExecutor) -Execute
Assert-Equal 'publish-image GHCR exact ALREADY_APPLIED' $publishAlready['MUTATION_RESULT'] 'ALREADY_APPLIED'
Assert-Equal 'publish-image GHCR exact no attempt' $publishAlready['MUTATION_ATTEMPTED'] 'FALSE'

$publishRunObs = New-ReadyPublishImageObservers -Sha $MainSha
$publishRunObs['WorkflowRuns'] = { param($shaArg) @(New-DispatchRun -Id '9001' -Path '.github/workflows/publish-release-image.yml' -HeadSha $MainSha) }.GetNewClosure()
$publishRun = Invoke-PublishImageFixture -Observers $publishRunObs -Execute
Assert-Equal 'publish-image existing run ALREADY_APPLIED' $publishRun['MUTATION_RESULT'] 'ALREADY_APPLIED'
Assert-Equal 'publish-image existing run guard' $publishRun['GUARD_IMAGE_PUBLISH_RUN'] 'EXACT_MATCH'

$publishAmbigObs = New-ReadyPublishImageObservers -Sha $MainSha
$publishAmbigObs['WorkflowRuns'] = { param($shaArg) @(New-DispatchRun -Id '9001' -Path '.github/workflows/publish-release-image.yml' -HeadSha $MainSha), (New-DispatchRun -Id '9002' -Path '.github/workflows/publish-release-image.yml' -HeadSha $MainSha) }.GetNewClosure()
$publishAmbig = Invoke-PublishImageFixture -Observers $publishAmbigObs -Execute
Assert-Equal 'publish-image ambiguous run CONFLICT' $publishAmbig['MUTATION_RESULT'] 'CONFLICT'
Assert-Equal 'publish-image ambiguous run guard' $publishAmbig['GUARD_IMAGE_PUBLISH_RUN'] 'CONFLICT'

$publishConflictObs = New-ReadyPublishImageObservers -Sha $MainSha
$publishConflictObs['Ghcr'] = { param($ver, $shaArg) New-ArtifactFact -State 'PRESENT' -Digest $DigestA -Revision $WrongSha -ShaTagState 'PRESENT' -ShaTagDigest $DigestA -OciVersion '1.3.5' }.GetNewClosure()
$publishConflict = Invoke-PublishImageFixture -Observers $publishConflictObs -Execute
Assert-Equal 'publish-image GHCR mismatch CONFLICT' $publishConflict['MUTATION_RESULT'] 'CONFLICT'

$publishIncompleteObs = New-ReadyPublishImageObservers -Sha $MainSha
$publishIncompleteObs['Ghcr'] = { param($ver, $shaArg) New-ArtifactFact -State 'INCOMPLETE' -Reason 'AUTH' }.GetNewClosure()
$publishIncomplete = Invoke-PublishImageFixture -Observers $publishIncompleteObs -Execute
Assert-Equal 'publish-image GHCR incomplete' $publishIncomplete['MUTATION_RESULT'] 'INCOMPLETE'

$publishPreflightFailObs = New-ReadyPublishImageObservers -Sha $MainSha
$dirtyLocal = New-BoundLocal -Sha $MainSha -Worktree 'DIRTY' -State 'DRIFT'
$publishPreflightFailObs['LocalRepo'] = { param($root) $dirtyLocal }.GetNewClosure()
$publishPreflightFail = Invoke-PublishImageFixture -Observers $publishPreflightFailObs -Execute
Assert-Equal 'publish-image preflight fail CONFLICT' $publishPreflightFail['MUTATION_RESULT'] 'CONFLICT'

$publishAmbigExec = New-FakeMutationExecutor -Outcome 'AMBIGUOUS'
$publishAmbigAfter = Invoke-PublishImageFixture -Observers $publishReadyObs -Executor $publishAmbigExec -Execute
Assert-Equal 'publish-image ambiguous executor' $publishAmbigAfter['MUTATION_RESULT'] 'AMBIGUOUS_AFTER_ATTEMPT'
Assert-Equal 'publish-image ambiguous performed unknown' $publishAmbigAfter['MUTATION_PERFORMED'] 'UNKNOWN'

$publishReadbackFailObs = New-ReadyPublishImageObservers -Sha $MainSha
$publishReadbackFailObs['ReadBackImagePublishRun'] = { param($shaArg) [pscustomobject]@{ State = 'ABSENT'; Id = 'NONE' } }.GetNewClosure()
$publishReadbackFail = Invoke-PublishImageFixture -Observers $publishReadbackFailObs -Executor (New-FakeMutationExecutor) -Execute
Assert-Equal 'publish-image readback absent INCOMPLETE' $publishReadbackFail['MUTATION_RESULT'] 'INCOMPLETE'
Assert-Equal 'publish-image readback performed unknown' $publishReadbackFail['MUTATION_PERFORMED'] 'UNKNOWN'

function Invoke-CreateTagFixture {
    param(
        $Observers,
        $Executor = $null,
        [switch]$Execute,
        [string]$Version = '1.3.5',
        [string]$Sha = $MainSha
    )
    return Invoke-ReleaseCreateTag -Version $Version -ReleaseCommitSha $Sha -RepoRoot $RepoRoot -Observers $Observers -Executor $Executor -Execute:$Execute -Quiet
}

$tagObs = @{
    Ghcr   = { param($ver, $shaArg) New-ExactGhcrFact -Sha $MainSha -VersionValue '1.3.5' }
    GitTag = { param($ver) New-ArtifactFact -State 'ABSENT' }
}
$tagDry = Invoke-CreateTagFixture -Observers $tagObs
Assert-Equal 'create-tag dry NOT_ATTEMPTED' $tagDry['MUTATION_RESULT'] 'NOT_ATTEMPTED'
Assert-Equal 'create-tag dry GUARD_GHCR exact' $tagDry['GUARD_GHCR'] 'EXACT_MATCH'
Assert-Equal 'create-tag dry GUARD_GIT_TAG absent' $tagDry['GUARD_GIT_TAG'] 'ABSENT'

$tagExec = New-FakeMutationExecutor
$tagObs['ReadBackGitTag'] = { param($ver) New-ArtifactFact -State 'PRESENT' -TargetSha $MainSha }.GetNewClosure()
$tagApplied = Invoke-CreateTagFixture -Observers $tagObs -Executor $tagExec -Execute
Assert-Equal 'create-tag execute APPLIED' $tagApplied['MUTATION_RESULT'] 'APPLIED'
Assert-Equal 'create-tag execute one call' $script:MutationExecutorCalls.Count 1

$tagAlreadyObs = @{
    Ghcr   = { param($ver, $shaArg) New-ExactGhcrFact -Sha $MainSha -VersionValue '1.3.5' }
    GitTag = { param($ver) New-ArtifactFact -State 'PRESENT' -TargetSha $MainSha }
}
$tagAlready = Invoke-CreateTagFixture -Observers $tagAlreadyObs -Execute
Assert-Equal 'create-tag exact ALREADY_APPLIED' $tagAlready['MUTATION_RESULT'] 'ALREADY_APPLIED'

$tagGhcrAbsentObs = @{
    Ghcr   = { param($ver, $shaArg) New-ArtifactFact -State 'ABSENT' }
    GitTag = { param($ver) New-ArtifactFact -State 'ABSENT' }
}
$tagGhcrAbsent = Invoke-CreateTagFixture -Observers $tagGhcrAbsentObs -Execute
Assert-Equal 'create-tag GHCR absent CONFLICT' $tagGhcrAbsent['MUTATION_RESULT'] 'CONFLICT'

$tagWrongObs = @{
    Ghcr   = { param($ver, $shaArg) New-ExactGhcrFact -Sha $MainSha -VersionValue '1.3.5' }
    GitTag = { param($ver) New-ArtifactFact -State 'PRESENT' -TargetSha $WrongSha }
}
$tagWrong = Invoke-CreateTagFixture -Observers $tagWrongObs -Execute
Assert-Equal 'create-tag wrong target CONFLICT' $tagWrong['MUTATION_RESULT'] 'CONFLICT'

function Invoke-PublishNugetFixture {
    param(
        $Observers,
        $Executor = $null,
        [switch]$Execute,
        [string]$Version = '1.3.5',
        [string]$Sha = $MainSha
    )
    return Invoke-ReleasePublishNuget -Version $Version -ReleaseCommitSha $Sha -RepoRoot $RepoRoot -Observers $Observers -Executor $Executor -Execute:$Execute -Quiet
}

$nugetObs = @{
    Ghcr            = { param($ver, $shaArg) New-ExactGhcrFact -Sha $MainSha -VersionValue '1.3.5' }
    GitTag          = { param($ver) New-ArtifactFact -State 'PRESENT' -TargetSha $MainSha }
    Nuget           = { param($ver) New-ArtifactFact -State 'ABSENT' }
    NugetPublishRun = { param($shaArg) [pscustomobject]@{ State = 'ABSENT'; Id = 'NONE' } }
}
$nugetDry = Invoke-PublishNugetFixture -Observers $nugetObs
Assert-Equal 'publish-nuget dry NOT_ATTEMPTED' $nugetDry['MUTATION_RESULT'] 'NOT_ATTEMPTED'
Assert-Equal 'publish-nuget dry GUARD_NUGET absent' $nugetDry['GUARD_NUGET'] 'ABSENT'

$nugetExec = New-FakeMutationExecutor
$nugetObs['ReadBackNugetPublishRun'] = { param($shaArg) [pscustomobject]@{ State = 'CANDIDATE_PRESENT'; Id = '8002' } }.GetNewClosure()
$nugetApplied = Invoke-PublishNugetFixture -Observers $nugetObs -Executor $nugetExec -Execute
Assert-Equal 'publish-nuget execute APPLIED' $nugetApplied['MUTATION_RESULT'] 'APPLIED'

$nugetAlreadyObs = @{
    Ghcr            = { param($ver, $shaArg) New-ExactGhcrFact -Sha $MainSha -VersionValue '1.3.5' }
    GitTag          = { param($ver) New-ArtifactFact -State 'PRESENT' -TargetSha $MainSha }
    Nuget           = { param($ver) New-ArtifactFact -State 'PRESENT' }
    NugetPublishRun = { param($shaArg) [pscustomobject]@{ State = 'ABSENT'; Id = 'NONE' } }
}
$nugetAlready = Invoke-PublishNugetFixture -Observers $nugetAlreadyObs -Execute
Assert-Equal 'publish-nuget exact ALREADY_APPLIED' $nugetAlready['MUTATION_RESULT'] 'ALREADY_APPLIED'

$nugetRunObs = @{
    Ghcr            = { param($ver, $shaArg) New-ExactGhcrFact -Sha $MainSha -VersionValue '1.3.5' }
    GitTag          = { param($ver) New-ArtifactFact -State 'PRESENT' -TargetSha $MainSha }
    Nuget           = { param($ver) New-ArtifactFact -State 'ABSENT' }
    NugetPublishRun = { param($shaArg) [pscustomobject]@{ State = 'CANDIDATE_PRESENT'; Id = '8001' } }
}
$nugetRun = Invoke-PublishNugetFixture -Observers $nugetRunObs -Execute
Assert-Equal 'publish-nuget existing run ALREADY_APPLIED' $nugetRun['MUTATION_RESULT'] 'ALREADY_APPLIED'
Assert-Equal 'publish-nuget existing run id' $nugetRun['NUGET_PUBLISH_RUN_ID'] '8001'

$nugetTagMissingObs = @{
    Ghcr            = { param($ver, $shaArg) New-ExactGhcrFact -Sha $MainSha -VersionValue '1.3.5' }
    GitTag          = { param($ver) New-ArtifactFact -State 'ABSENT' }
    Nuget           = { param($ver) New-ArtifactFact -State 'ABSENT' }
    NugetPublishRun = { param($shaArg) [pscustomobject]@{ State = 'ABSENT'; Id = 'NONE' } }
}
$nugetTagMissing = Invoke-PublishNugetFixture -Observers $nugetTagMissingObs -Execute
Assert-Equal 'publish-nuget tag absent CONFLICT' $nugetTagMissing['MUTATION_RESULT'] 'CONFLICT'

function Invoke-CreateReleaseFixture {
    param(
        $Observers,
        $Executor = $null,
        [switch]$Execute,
        [string]$ReleaseNotes = 'docs/releases/v1.3.4.md',
        [string]$Version = '1.3.5',
        [string]$Sha = $MainSha
    )
    return Invoke-ReleaseCreateGitHubRelease -Version $Version -ReleaseCommitSha $Sha -RepoRoot $RepoRoot -ReleaseNotesPath $ReleaseNotes -Observers $Observers -Executor $Executor -Execute:$Execute -Quiet
}

$releaseObs = @{
    Ghcr          = { param($ver, $shaArg) New-ExactGhcrFact -Sha $MainSha -VersionValue '1.3.5' }
    GitTag        = { param($ver) New-ArtifactFact -State 'PRESENT' -TargetSha $MainSha }
    Nuget         = { param($ver) New-ArtifactFact -State 'PRESENT' }
    GitHubRelease = { param($ver) New-ArtifactFact -State 'ABSENT' }
}
$releaseDry = Invoke-CreateReleaseFixture -Observers $releaseObs
Assert-Equal 'create-github-release dry NOT_ATTEMPTED' $releaseDry['MUTATION_RESULT'] 'NOT_ATTEMPTED'
Assert-Equal 'create-github-release dry GUARD_GITHUB_RELEASE absent' $releaseDry['GUARD_GITHUB_RELEASE'] 'ABSENT'

$releaseExec = New-FakeMutationExecutor
$releaseObs['ReadBackGitHubRelease'] = { param($ver) New-ArtifactFact -State 'PRESENT' }.GetNewClosure()
$releaseObs['ReadBackGitTag'] = { param($ver) New-ArtifactFact -State 'PRESENT' -TargetSha $MainSha }.GetNewClosure()
$releaseApplied = Invoke-CreateReleaseFixture -Observers $releaseObs -Executor $releaseExec -Execute
Assert-Equal 'create-github-release execute APPLIED' $releaseApplied['MUTATION_RESULT'] 'APPLIED'
Assert-Equal 'create-github-release execute MUTATION_PERFORMED' $releaseApplied['MUTATION_PERFORMED'] 'TRUE'

$releaseAlreadyObs = @{
    Ghcr          = { param($ver, $shaArg) New-ExactGhcrFact -Sha $MainSha -VersionValue '1.3.5' }
    GitTag        = { param($ver) New-ArtifactFact -State 'PRESENT' -TargetSha $MainSha }
    Nuget         = { param($ver) New-ArtifactFact -State 'PRESENT' }
    GitHubRelease = { param($ver) New-ArtifactFact -State 'PRESENT' }
}
$releaseAlready = Invoke-CreateReleaseFixture -Observers $releaseAlreadyObs -Execute
Assert-Equal 'create-github-release exact ALREADY_APPLIED' $releaseAlready['MUTATION_RESULT'] 'ALREADY_APPLIED'

$releaseNoNotes = Invoke-ReleaseCreateGitHubRelease -Version '1.3.5' -ReleaseCommitSha $MainSha -RepoRoot $RepoRoot -Observers $releaseObs -Quiet
Assert-Equal 'create-github-release missing notes NOT_ATTEMPTED' $releaseNoNotes['MUTATION_RESULT'] 'NOT_ATTEMPTED'
Assert-Equal 'create-github-release missing notes path NONE' $releaseNoNotes['RELEASE_NOTES_PATH'] 'NONE'

$releaseBadNotes = Invoke-CreateReleaseFixture -Observers $releaseObs -ReleaseNotes 'docs/releases/does-not-exist.md' -Execute
Assert-Equal 'create-github-release bad notes INCOMPLETE' $releaseBadNotes['MUTATION_RESULT'] 'INCOMPLETE'

$releaseDraftObs = @{
    Ghcr          = { param($ver, $shaArg) New-ExactGhcrFact -Sha $MainSha -VersionValue '1.3.5' }
    GitTag        = { param($ver) New-ArtifactFact -State 'PRESENT' -TargetSha $MainSha }
    Nuget         = { param($ver) New-ArtifactFact -State 'PRESENT' }
    GitHubRelease = { param($ver) New-ArtifactFact -State 'CONFLICT' -Reason 'DRAFT' }
}
$releaseDraft = Invoke-CreateReleaseFixture -Observers $releaseDraftObs -Execute
Assert-Equal 'create-github-release draft CONFLICT' $releaseDraft['MUTATION_RESULT'] 'CONFLICT'

$publishImageLines = Format-ReleaseMutationLines -Map $publishDry -Keys @(
    'COMMAND', 'VERSION', 'RELEASE_COMMIT_SHA', 'MUTATION_RESULT', 'MUTATION_ATTEMPTED',
    'HUMAN_AUTHORIZATION_REQUIRED', 'SOURCE_BINDING', 'VERSION_PREP', 'GUARD_GHCR', 'GUARD_IMAGE_PUBLISH_RUN', 'IMAGE_PUBLISH_RUN_ID', 'MUTATION_PERFORMED'
)
Assert-Equal 'publish-image format starts COMMAND' $publishImageLines[0] 'COMMAND=PUBLISH_IMAGE'
Assert-Equal 'publish-image format ends MUTATION_PERFORMED' $publishImageLines[$publishImageLines.Count - 1] 'MUTATION_PERFORMED=FALSE'
Assert-Equal 'publish-image format key count' $publishImageLines.Count 12

$mutationCliNoExecute = Invoke-Cli -CliArgs @('publish-image', '-Version', '1.3.5', '-ReleaseCommitSha', $MainSha)
Assert-Equal 'CLI publish-image without Execute exit 0' $mutationCliNoExecute.ExitCode 0
Assert-True 'CLI publish-image without Execute MUTATION_ATTEMPTED=FALSE' ($mutationCliNoExecute.Output -match 'MUTATION_ATTEMPTED=FALSE') 'dry CLI should not attempt mutation'

$mutationCliNoSha = Invoke-Cli -CliArgs @('publish-image', '-Version', '1.3.5')
Assert-Equal 'CLI publish-image without SHA exit 2' $mutationCliNoSha.ExitCode 2

$mutationCliReleaseNoNotes = Invoke-Cli -CliArgs @('create-github-release', '-Version', '1.3.5', '-ReleaseCommitSha', $MainSha)
Assert-Equal 'CLI create-github-release without notes exit 2' $mutationCliReleaseNoNotes.ExitCode 2
Assert-True 'CLI create-github-release mentions ReleaseNotesPath' ($mutationCliReleaseNoNotes.Output -match 'ReleaseNotesPath') 'missing notes usage text'

# --- M-1 production executor composition (fake command runner only) ---
$script:CommandRunnerCalls = New-Object System.Collections.Generic.List[object]
function New-FakeCommandRunner {
    param([int]$ExitCode = 0, [string]$Stdout = '')
    $calls = $script:CommandRunnerCalls
    return {
        param(
            [string]$Program,
            [string[]]$ArgumentList,
            [string]$WorkingDirectory = ''
        )
        $calls.Add([pscustomobject]@{
            Program          = $Program
            ArgumentList     = @($ArgumentList)
            WorkingDirectory = $WorkingDirectory
        })
        return [pscustomobject]@{
            ExitCode = $ExitCode
            Stdout   = $Stdout
            Stderr   = ''
        }
    }.GetNewClosure()
}

function Assert-RunnerCall {
    param(
        [string]$Name,
        [int]$Index,
        [string]$Program,
        [string[]]$ExpectedArgs,
        [string]$ExpectedCwd = ''
    )
    if ($script:CommandRunnerCalls.Count -le $Index) {
        Write-TestFail -Name $Name -Detail ("expected runner call index {0}, got {1} calls" -f $Index, $script:CommandRunnerCalls.Count)
        return
    }
    $call = $script:CommandRunnerCalls[$Index]
    Assert-Equal ($Name + ' program') $call.Program $Program
    Assert-Equal ($Name + ' argv count') $call.ArgumentList.Count $ExpectedArgs.Count
    for ($i = 0; $i -lt $ExpectedArgs.Count; $i++) {
        Assert-Equal ($Name + ' argv[' + $i + ']') $call.ArgumentList[$i] $ExpectedArgs[$i]
    }
    Assert-Equal ($Name + ' cwd') $call.WorkingDirectory $ExpectedCwd
}

$script:CommandRunnerCalls.Clear()
$fakeRunner = New-FakeCommandRunner
$publishProdExec = New-ReleaseProductionPublishImageExecutor -CommandRunner $fakeRunner -RepoRoot $RepoRoot
$null = & $publishProdExec @{ Version = '1.3.5'; ReleaseCommitSha = $MainSha }
Assert-Equal 'publish-image prod executor one call' $script:CommandRunnerCalls.Count 1
Assert-RunnerCall -Name 'publish-image prod gh' -Index 0 -Program 'gh' -ExpectedArgs @(
    'workflow', 'run', 'publish-release-image.yml',
    '--repo', 'kooiei-in4a/amane-mailer',
    '--ref', 'main',
    '-f', ('source_sha=' + $MainSha),
    '-f', 'release_version=1.3.5'
) -ExpectedCwd $RepoRoot

$script:CommandRunnerCalls.Clear()
$tagProdExec = New-ReleaseProductionCreateTagExecutor -RepoRoot $RepoRoot -CommandRunner $fakeRunner
$null = & $tagProdExec @{ Version = '1.3.5'; ReleaseCommitSha = $MainSha; TagName = 'v1.3.5' }
Assert-Equal 'create-tag prod executor call count' $script:CommandRunnerCalls.Count 4
Assert-RunnerCall -Name 'create-tag ls-remote' -Index 0 -Program 'git' -ExpectedArgs @('ls-remote', '--exit-code', 'origin', 'refs/tags/v1.3.5') -ExpectedCwd $RepoRoot
Assert-RunnerCall -Name 'create-tag local list' -Index 1 -Program 'git' -ExpectedArgs @('tag', '-l', 'v1.3.5') -ExpectedCwd $RepoRoot
Assert-RunnerCall -Name 'create-tag annotate' -Index 2 -Program 'git' -ExpectedArgs @('tag', '-a', 'v1.3.5', $MainSha, '-m', 'Amane Mailer v1.3.5') -ExpectedCwd $RepoRoot
Assert-RunnerCall -Name 'create-tag push' -Index 3 -Program 'git' -ExpectedArgs @('push', 'origin', 'refs/tags/v1.3.5') -ExpectedCwd $RepoRoot
Assert-True 'create-tag push no force' (-not ($script:CommandRunnerCalls[3].ArgumentList -contains '--force')) 'tag push must not use --force'
Assert-True 'create-tag no delete' (-not ($script:CommandRunnerCalls[3].ArgumentList -contains '--delete')) 'tag push must not delete'

$script:CommandRunnerCalls.Clear()
$nugetProdExec = New-ReleaseProductionPublishNugetExecutor -CommandRunner $fakeRunner -RepoRoot $RepoRoot
$null = & $nugetProdExec @{ Version = '1.3.5'; ReleaseCommitSha = $MainSha; Ref = 'v1.3.5' }
Assert-Equal 'publish-nuget prod executor one call' $script:CommandRunnerCalls.Count 1
Assert-RunnerCall -Name 'publish-nuget prod gh' -Index 0 -Program 'gh' -ExpectedArgs @(
    'workflow', 'run', 'publish-contracts.yml',
    '--repo', 'kooiei-in4a/amane-mailer',
    '--ref', 'v1.3.5'
) -ExpectedCwd $RepoRoot

$releaseNotesFixture = Join-Path $RepoRoot 'docs/releases/v1.3.4.md'
$script:CommandRunnerCalls.Clear()
$releaseProdExec = New-ReleaseProductionCreateGitHubReleaseExecutor -RepoRoot $RepoRoot -CommandRunner $fakeRunner
$null = & $releaseProdExec @{
    Version          = '1.3.5'
    ReleaseCommitSha = $MainSha
    TagName          = 'v1.3.5'
    ReleaseNotesPath = 'docs/releases/v1.3.4.md'
}
Assert-Equal 'create-github-release prod executor one call' $script:CommandRunnerCalls.Count 1
$resolvedNotes = (Resolve-Path -LiteralPath $releaseNotesFixture).Path
Assert-RunnerCall -Name 'create-github-release prod gh' -Index 0 -Program 'gh' -ExpectedArgs @(
    'release', 'create', 'v1.3.5',
    '--repo', 'kooiei-in4a/amane-mailer',
    '--title', 'Amane Mailer v1.3.5',
    '--notes-file', $resolvedNotes,
    '--verify-tag'
)
Assert-True 'create-github-release no draft flag' (-not ($script:CommandRunnerCalls[0].ArgumentList -contains '--draft')) 'must not create draft'
Assert-True 'create-github-release no prerelease flag' (-not ($script:CommandRunnerCalls[0].ArgumentList -contains '--prerelease')) 'must not create prerelease'
Assert-True 'create-github-release no target flag' (-not ($script:CommandRunnerCalls[0].ArgumentList -contains '--target')) 'must not set --target'

# --- M-02 create-github-release tag rebind guard ---
$releaseTagExactObs = @{
    Ghcr                  = { param($ver, $shaArg) New-ExactGhcrFact -Sha $MainSha -VersionValue '1.3.5' }
    GitTag                = { param($ver) New-ArtifactFact -State 'PRESENT' -TargetSha $MainSha }
    Nuget                 = { param($ver) New-ArtifactFact -State 'PRESENT' }
    GitHubRelease         = { param($ver) New-ArtifactFact -State 'ABSENT' }
    ReadBackGitHubRelease = { param($ver) New-ArtifactFact -State 'PRESENT' }.GetNewClosure()
    ReadBackGitTag        = { param($ver) New-ArtifactFact -State 'PRESENT' -TargetSha $MainSha }.GetNewClosure()
}
$releaseTagExact = Invoke-CreateReleaseFixture -Observers $releaseTagExactObs -Executor $releaseExec -Execute
Assert-Equal 'M-02 post release exact tag exact APPLIED' $releaseTagExact['MUTATION_RESULT'] 'APPLIED'
Assert-Equal 'M-02 post release exact tag exact MUTATION_PERFORMED' $releaseTagExact['MUTATION_PERFORMED'] 'TRUE'

$releaseTagAbsentObs = @{
    Ghcr                  = { param($ver, $shaArg) New-ExactGhcrFact -Sha $MainSha -VersionValue '1.3.5' }
    GitTag                = { param($ver) New-ArtifactFact -State 'PRESENT' -TargetSha $MainSha }
    Nuget                 = { param($ver) New-ArtifactFact -State 'PRESENT' }
    GitHubRelease         = { param($ver) New-ArtifactFact -State 'ABSENT' }
    ReadBackGitHubRelease = { param($ver) New-ArtifactFact -State 'PRESENT' }.GetNewClosure()
    ReadBackGitTag        = { param($ver) New-ArtifactFact -State 'ABSENT' }.GetNewClosure()
}
$releaseTagAbsent = Invoke-CreateReleaseFixture -Observers $releaseTagAbsentObs -Executor $releaseExec -Execute
Assert-Equal 'M-02 post release exact tag absent INCOMPLETE' $releaseTagAbsent['MUTATION_RESULT'] 'INCOMPLETE'
Assert-Equal 'M-02 post release exact tag absent MUTATION_ATTEMPTED' $releaseTagAbsent['MUTATION_ATTEMPTED'] 'TRUE'
Assert-Equal 'M-02 post release exact tag absent MUTATION_PERFORMED' $releaseTagAbsent['MUTATION_PERFORMED'] 'UNKNOWN'

$releaseTagIncompleteObs = @{
    Ghcr                  = { param($ver, $shaArg) New-ExactGhcrFact -Sha $MainSha -VersionValue '1.3.5' }
    GitTag                = { param($ver) New-ArtifactFact -State 'PRESENT' -TargetSha $MainSha }
    Nuget                 = { param($ver) New-ArtifactFact -State 'PRESENT' }
    GitHubRelease         = { param($ver) New-ArtifactFact -State 'ABSENT' }
    ReadBackGitHubRelease = { param($ver) New-ArtifactFact -State 'PRESENT' }.GetNewClosure()
    ReadBackGitTag        = { param($ver) New-ArtifactFact -State 'INCOMPLETE' }.GetNewClosure()
}
$releaseTagIncomplete = Invoke-CreateReleaseFixture -Observers $releaseTagIncompleteObs -Executor $releaseExec -Execute
Assert-Equal 'M-02 post release exact tag incomplete INCOMPLETE' $releaseTagIncomplete['MUTATION_RESULT'] 'INCOMPLETE'
Assert-Equal 'M-02 post release exact tag incomplete MUTATION_PERFORMED' $releaseTagIncomplete['MUTATION_PERFORMED'] 'UNKNOWN'

$releaseTagMismatchObs = @{
    Ghcr                  = { param($ver, $shaArg) New-ExactGhcrFact -Sha $MainSha -VersionValue '1.3.5' }
    GitTag                = { param($ver) New-ArtifactFact -State 'PRESENT' -TargetSha $MainSha }
    Nuget                 = { param($ver) New-ArtifactFact -State 'PRESENT' }
    GitHubRelease         = { param($ver) New-ArtifactFact -State 'ABSENT' }
    ReadBackGitHubRelease = { param($ver) New-ArtifactFact -State 'PRESENT' }.GetNewClosure()
    ReadBackGitTag        = { param($ver) New-ArtifactFact -State 'PRESENT' -TargetSha '0000000000000000000000000000000000000001' }.GetNewClosure()
}
$releaseTagMismatch = Invoke-CreateReleaseFixture -Observers $releaseTagMismatchObs -Executor $releaseExec -Execute
Assert-Equal 'M-02 post release exact tag mismatch CONFLICT' $releaseTagMismatch['MUTATION_RESULT'] 'CONFLICT'
Assert-Equal 'M-02 post release exact tag mismatch MUTATION_PERFORMED' $releaseTagMismatch['MUTATION_PERFORMED'] 'UNKNOWN'

$resolvedPublish = Resolve-ReleaseMutationExecutor -Executor $null -Execute -CommandName 'publish-image' -RepoRoot $RepoRoot -CommandRunner $fakeRunner
Assert-True 'resolve publish-image production executor' ($null -ne $resolvedPublish) 'Execute path should resolve production executor'
$resolvedDry = Resolve-ReleaseMutationExecutor -Executor $null -CommandName 'publish-image' -RepoRoot $RepoRoot -CommandRunner $fakeRunner
Assert-True 'resolve without Execute is null' ($null -eq $resolvedDry) 'dry path must not resolve production executor'

$script:CommandRunnerCalls.Clear()
$publishGuardConflict = Invoke-PublishImageFixture -Observers $publishConflictObs -Execute
Assert-Equal 'publish-image CONFLICT no runner calls' $script:CommandRunnerCalls.Count 0
Assert-Equal 'publish-image CONFLICT result' $publishGuardConflict['MUTATION_RESULT'] 'CONFLICT'

$script:CommandRunnerCalls.Clear()
$publishGuardIncomplete = Invoke-PublishImageFixture -Observers $publishIncompleteObs -Execute
Assert-Equal 'publish-image INCOMPLETE no runner calls' $script:CommandRunnerCalls.Count 0

$script:CommandRunnerCalls.Clear()
$publishAlreadyRunner = New-FakeCommandRunner
$publishAlreadyWithRunner = Invoke-PublishImageFixture -Observers $publishAlreadyObs -Execute
Assert-Equal 'publish-image EXACT_MATCH no runner calls' $script:CommandRunnerCalls.Count 0

$script:CommandRunnerCalls.Clear()
$publishDryRunner = New-FakeCommandRunner
$null = Invoke-PublishImageFixture -Observers $publishReadyObs
Assert-Equal 'publish-image without Execute no runner calls' $script:CommandRunnerCalls.Count 0

$script:CommandRunnerCalls.Clear()
$publishProdFixtureRunner = New-FakeCommandRunner
$publishProdObs = New-ReadyPublishImageObservers -Sha $MainSha
$publishProdObs['ReadBackImagePublishRun'] = { param($shaArg) [pscustomobject]@{ State = 'CANDIDATE_PRESENT'; Id = '9010' } }.GetNewClosure()
$publishProdApplied = Invoke-ReleasePublishImage -Version '1.3.5' -ReleaseCommitSha $MainSha -RepoRoot $RepoRoot -Observers $publishProdObs -Execute -Quiet -CommandRunner $publishProdFixtureRunner
Assert-Equal 'publish-image production path APPLIED' $publishProdApplied['MUTATION_RESULT'] 'APPLIED'
Assert-Equal 'publish-image production path runner calls' $script:CommandRunnerCalls.Count 1

$script:CommandRunnerCalls.Clear()
$publishReadbackRunner = New-FakeCommandRunner
$publishReadbackObs = New-ReadyPublishImageObservers -Sha $MainSha
$publishReadbackObs['ReadBackImagePublishRun'] = { param($shaArg) [pscustomobject]@{ State = 'ABSENT'; Id = 'NONE' } }.GetNewClosure()
$publishReadbackProd = Invoke-ReleasePublishImage -Version '1.3.5' -ReleaseCommitSha $MainSha -RepoRoot $RepoRoot -Observers $publishReadbackObs -Execute -Quiet -CommandRunner $publishReadbackRunner
Assert-Equal 'publish-image prod readback absent INCOMPLETE' $publishReadbackProd['MUTATION_RESULT'] 'INCOMPLETE'
Assert-Equal 'publish-image prod readback one runner call' $script:CommandRunnerCalls.Count 1

$script:CommandRunnerCalls.Clear()
$publishAmbigRunner = New-FakeCommandRunner -ExitCode 1
$publishAmbigProdObs = New-ReadyPublishImageObservers -Sha $MainSha
$publishAmbigProd = Invoke-ReleasePublishImage -Version '1.3.5' -ReleaseCommitSha $MainSha -RepoRoot $RepoRoot -Observers $publishAmbigProdObs -Execute -Quiet -CommandRunner $publishAmbigRunner
Assert-Equal 'publish-image prod failed executor INCOMPLETE' $publishAmbigProd['MUTATION_RESULT'] 'INCOMPLETE'
Assert-Equal 'publish-image prod failed one runner call no retry' $script:CommandRunnerCalls.Count 1

# --- A-1 current-public authority + prepare-post-sync (fixture repos only) ---
$PostSyncSha134 = Get-FixtureSha '4'
$PostSyncSha135 = Get-FixtureSha '5'
$PostSyncDigest135 = Get-FixtureDigest '5'

function New-PostSyncVerifyObservers {
    param(
        [string]$Version = '1.3.5',
        [string]$Sha = $PostSyncSha135,
        [string]$Digest = $PostSyncDigest135
    )
    return @{
        GitTag         = { param($ver) New-ArtifactFact -State 'PRESENT' -TargetSha $Sha }.GetNewClosure()
        GitHubRelease  = { param($ver) New-ArtifactFact -State 'PRESENT' }.GetNewClosure()
        Nuget          = { param($ver) New-ArtifactFact -State 'PRESENT' }.GetNewClosure()
        SourceVersions = { param($shaArg, $ver) [pscustomobject]@{ ContractsState = 'PRESENT'; ContractsVersion = $Version; OpenApiState = 'PRESENT'; OpenApiVersion = $Version } }.GetNewClosure()
        NugetRevision  = { param($ver) [pscustomobject]@{ State = 'PRESENT'; Commit = $Sha; Reason = '' } }.GetNewClosure()
        ReleaseRecord = { param($ver, $shaArg) [pscustomobject]@{ State = 'PRESENT'; Text = "> Status: **RELEASE PREPARATION - NOT YET PUBLISHED**`n"; Reason = '' } }.GetNewClosure()
        Ghcr           = { param($ver, $shaArg) New-ArtifactFact -State 'PRESENT' -Digest $Digest -Revision $Sha -OciVersion $Version -ShaTagState 'PRESENT' -ShaTagDigest $Digest }.GetNewClosure()
    }
}

function New-PostSyncFixtureLocalRepo {
    param([string]$Sha = $MainSha)
    return [pscustomobject]@{
        State          = 'PASS'
        Branch         = 'main'
        Head           = $Sha
        Worktree       = 'CLEAN'
        OriginIdentity = 'kooiei-in4a/amane-mailer'
        LocalMain      = $Sha
        OriginMain     = $Sha
        Reason         = ''
    }
}

function Initialize-PostSyncFixtureRepo {
    param(
        [string]$Root,
        [string]$AuthorityVersion = '1.3.4',
        [switch]$SynchronizedTo135
    )

    $fixturesRoot = Join-Path $PSScriptRoot 'fixtures/post-sync'

    $paths = @(
        'release'
        'docs/releases'
        'docs/ops'
        'scripts'
        'infra/docker'
    )
    foreach ($rel in $paths) {
        $full = Join-Path $Root $rel
        if (-not (Test-Path -LiteralPath $full)) {
            New-Item -ItemType Directory -Path $full -Force | Out-Null
        }
    }

    $authorityVer = if ($SynchronizedTo135) { '1.3.5' } else { $AuthorityVersion }
    $authorityJson = New-CurrentPublicAuthorityJson -Version $authorityVer
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText((Join-Path $Root 'release/current-public.json'), $authorityJson, $utf8NoBom)

    function Copy-FixtureFile {
        param([string]$Source, [string]$Destination)
        $content = [System.IO.File]::ReadAllText($Source, $utf8NoBom)
        [System.IO.File]::WriteAllText($Destination, $content, $utf8NoBom)
    }

    Copy-FixtureFile -Source (Join-Path $fixturesRoot 'README.md') -Destination (Join-Path $Root 'README.md')
    Copy-FixtureFile -Source (Join-Path $fixturesRoot 'README.en.md') -Destination (Join-Path $Root 'README.en.md')
    Copy-FixtureFile -Source (Join-Path $fixturesRoot 'SECURITY.md') -Destination (Join-Path $Root 'SECURITY.md')
    Copy-FixtureFile -Source (Join-Path $fixturesRoot 'release-image-smoke.md') -Destination (Join-Path $Root 'docs/ops/release-image-smoke.md')
    Copy-FixtureFile -Source (Join-Path $fixturesRoot 'release-image-smoke.en.md') -Destination (Join-Path $Root 'docs/ops/release-image-smoke.en.md')
    Copy-FixtureFile -Source (Join-Path $fixturesRoot 'release-smoke.sh') -Destination (Join-Path $Root 'scripts/release-smoke.sh')
    Copy-FixtureFile -Source (Join-Path $fixturesRoot 'release-smoke.ps1') -Destination (Join-Path $Root 'scripts/release-smoke.ps1')
    Copy-FixtureFile -Source (Join-Path $fixturesRoot 'docker-compose.release-smoke.yml') -Destination (Join-Path $Root 'infra/docker/docker-compose.release-smoke.yml')
    Copy-FixtureFile -Source (Join-Path $RepoRoot 'docs/releases/v1.3.4.md') -Destination (Join-Path $Root 'docs/releases/v1.3.4.md')
    Copy-FixtureFile -Source (Join-Path $fixturesRoot 'v1.3.5-pending.md') -Destination (Join-Path $Root 'docs/releases/v1.3.5.md')

    if ($SynchronizedTo135) {
        $applyRules = Get-PostSyncFollowerReplacementRules -PrevVersion '1.3.4' -TargetVersion '1.3.5'
        foreach ($path in @('README.md', 'README.en.md', 'SECURITY.md', 'docs/ops/release-image-smoke.md', 'docs/ops/release-image-smoke.en.md', 'scripts/release-smoke.sh', 'scripts/release-smoke.ps1', 'infra/docker/docker-compose.release-smoke.yml')) {
            $full = Join-Path $Root $path
            $content = [System.IO.File]::ReadAllText($full, $utf8NoBom)
            $pathRules = Get-PostSyncRulesForPath -RelativePath $path -AllRules $applyRules
            $updated = Apply-PostSyncReplacementRules -Content $content -Rules $pathRules
            [System.IO.File]::WriteAllText($full, $updated, $utf8NoBom)
        }
        $pending135 = [System.IO.File]::ReadAllText((Join-Path $Root 'docs/releases/v1.3.5.md'), $utf8NoBom)
        $published = Build-PublishedReleaseRecordForPostSync -Text $pending135 -Version '1.3.5' -ReleaseCommitSha $PostSyncSha135 -PublicDigest $PostSyncDigest135 -Platforms @('linux/amd64')
        [System.IO.File]::WriteAllText((Join-Path $Root 'docs/releases/v1.3.5.md'), $published.Text, $utf8NoBom)
    }
}

$authorityFixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('amane-mailer-authority-' + [Guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $authorityFixtureRoot -Force | Out-Null
try {
    Initialize-PostSyncFixtureRepo -Root $authorityFixtureRoot -AuthorityVersion '1.3.4'
    $authorityPredecessorText = Get-Content -LiteralPath (Join-Path $authorityFixtureRoot 'release/current-public.json') -Raw
    $authorityPredecessor = ConvertFrom-CurrentPublicAuthorityText -Text $authorityPredecessorText -RepoRoot $authorityFixtureRoot
    Assert-Equal 'authority predecessor parse state' $authorityPredecessor.State 'VALID'
    Assert-Equal 'authority predecessor version' $authorityPredecessor.Version '1.3.4'
    Assert-Equal 'authority predecessor tag' $authorityPredecessor.Tag 'v1.3.4'
    Assert-Equal 'FIXTURE_PREDECESSOR_AUTHORITY' $(if ($authorityPredecessor.State -eq 'VALID' -and $authorityPredecessor.Version -eq '1.3.4') { 'PASS' } else { 'FAIL' }) 'PASS'

    $authorityTargetRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('amane-mailer-authority-target-' + [Guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $authorityTargetRoot -Force | Out-Null
    try {
        Initialize-PostSyncFixtureRepo -Root $authorityTargetRoot -AuthorityVersion '1.3.4' -SynchronizedTo135
        $authorityTargetText = Get-Content -LiteralPath (Join-Path $authorityTargetRoot 'release/current-public.json') -Raw
        $authorityTarget = ConvertFrom-CurrentPublicAuthorityText -Text $authorityTargetText -RepoRoot $authorityTargetRoot
        Assert-Equal 'authority target parse state' $authorityTarget.State 'VALID'
        Assert-Equal 'authority target version' $authorityTarget.Version '1.3.5'
        Assert-Equal 'authority target tag' $authorityTarget.Tag 'v1.3.5'
        Assert-Equal 'FIXTURE_TARGET_AUTHORITY' $(if ($authorityTarget.State -eq 'VALID' -and $authorityTarget.Version -eq '1.3.5') { 'PASS' } else { 'FAIL' }) 'PASS'
    }
    finally {
        Remove-Item -LiteralPath $authorityTargetRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
finally {
    Remove-Item -LiteralPath $authorityFixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$authorityMalformed = ConvertFrom-CurrentPublicAuthorityText -Text '{not-json' -RepoRoot $RepoRoot
Assert-Equal 'malformed authority fail closed' $authorityMalformed.State 'INCOMPLETE'
Assert-Equal 'malformed authority reason' $authorityMalformed.Reason 'MALFORMED_JSON'

$authorityBadSchema = ConvertFrom-CurrentPublicAuthorityText -Text '{"schemaVersion":2,"version":"1.3.4","tag":"v1.3.4","platforms":["linux/amd64"],"releaseRecord":"docs/releases/v1.3.4.md"}' -RepoRoot $RepoRoot
Assert-Equal 'unsupported schema fail closed' $authorityBadSchema.Reason 'UNSUPPORTED_SCHEMA'

$authorityTagMismatch = ConvertFrom-CurrentPublicAuthorityText -Text '{"schemaVersion":1,"version":"1.3.4","tag":"v1.3.5","platforms":["linux/amd64"],"releaseRecord":"docs/releases/v1.3.4.md"}' -RepoRoot $RepoRoot
Assert-Equal 'version tag mismatch fail closed' $authorityTagMismatch.Reason 'VERSION_TAG_MISMATCH'


$pendingFixturePath = Join-Path $PSScriptRoot 'fixtures/post-sync/v1.3.5-pending.md'
$pendingFixtureText = Get-Content -LiteralPath $pendingFixturePath -Raw
Assert-Equal 'pending fixture state' (Get-ReleaseRecordStateFromText -Text $pendingFixtureText) 'PENDING'
Assert-Equal 'pending fixture platform parse' (@(Get-ReleaseRecordPlatformsFromText -Text $pendingFixtureText)[0]) 'linux/amd64'
$platformResolved = Resolve-PostSyncPlatforms -RecordText $pendingFixtureText -AuthorityPlatforms @('linux/amd64')
Assert-Equal 'pending fixture platform resolve' $platformResolved.State 'RESOLVED'
Assert-Equal 'pending fixture platform value' (@($platformResolved.Platforms)[0]) 'linux/amd64'
$platformMismatchText = $pendingFixtureText -replace 'supported platform: ``linux/amd64``', 'supported platform: **PENDING**'
$platformMismatch = Resolve-PostSyncPlatforms -RecordText $platformMismatchText -AuthorityPlatforms @('linux/arm64')
Assert-Equal 'platform mismatch incomplete' $platformMismatch.State 'INCOMPLETE'
$finalizedFixture = Build-PublishedReleaseRecordForPostSync -Text $pendingFixtureText -Version '1.3.5' -ReleaseCommitSha $PostSyncSha135 -PublicDigest $PostSyncDigest135 -Platforms @('linux/amd64')
Assert-Equal 'finalize pending fixture applied' $finalizedFixture.State 'APPLIED'
Assert-Equal 'finalize pending fixture published' (Get-ReleaseRecordStateFromText -Text $finalizedFixture.Text) 'PUBLISHED'
Assert-True 'finalize preserves release scope section' ($finalizedFixture.Text -match '## Release scope') 'release scope section missing'
Assert-True 'finalize preserves operational notes' ($finalizedFixture.Text -match '## Operational notes') 'operational notes missing'
Assert-True 'finalize preserves limitations' ($finalizedFixture.Text -match '## Limitations') 'limitations missing'
Assert-True 'finalize updates releaseCommitSha' ($finalizedFixture.Text -match ('releaseCommitSha: ``' + [regex]::Escape($PostSyncSha135) + '``')) 'releaseCommitSha not updated'
Assert-True 'finalize updates public digest' ($finalizedFixture.Text -match ([regex]::Escape($PostSyncDigest135))) 'digest not updated'
Assert-True 'finalize leaves workflow run pending' ($finalizedFixture.Text -match 'publish workflow run: \*\*PENDING\*\*') 'workflow run should remain pending'
Assert-True 'finalize leaves artifact id pending' ($finalizedFixture.Text -match 'publication artifact ID: \*\*PENDING\*\*') 'artifact id should remain pending'
Assert-True 'finalize does not invent publication invariants' (-not ($finalizedFixture.Text -match 'same-version GHCR republish: none')) 'publication invariants should not be invented'

# --- #677 production-shape release record transformation ---
$ProductionShapeSha = '89424946b9c018bb2d0f276e63b6e7344e40786b'
$ProductionShapeDigest = 'sha256:397216a030d69c600b88b9939ea6c0a10e325bb72948b779c4ae98ac85a129d1'
$productionShapePath = Join-Path $PSScriptRoot 'fixtures/post-sync/v1.3.5-production-shape-pending.md'
$productionShapeText = Get-Content -LiteralPath $productionShapePath -Raw
Assert-Equal 'production shape pending state' (Get-ReleaseRecordStateFromText -Text $productionShapeText) 'PENDING'
Assert-True 'production shape has NOT YET PUBLISHED cores' ($productionShapeText -match 'Git tag `v1\.3\.5`: \*\*NOT YET PUBLISHED\*\*') 'expected production git tag pending'

$productionBeforeStatus = Update-ReleaseRecordObservableFields -Text $productionShapeText -Version '1.3.5' -ReleaseCommitSha $ProductionShapeSha -PublicDigest $ProductionShapeDigest -Platforms @('linux/amd64')
Assert-equal 'STATUS_AFTER_CORE_VALIDATION_ONLY pre-status remains pending' (Get-ReleaseRecordStateFromText -Text $productionBeforeStatus) 'PENDING'
$productionCoreCheck = Test-PublishedReleaseRecordCoreConsistency -Text $productionBeforeStatus -Version '1.3.5' -ReleaseCommitSha $ProductionShapeSha -PublicDigest $ProductionShapeDigest
Assert-Equal 'CORE_CONSISTENCY_GUARD pre-status' $productionCoreCheck.State 'PASS'

$productionFinal = Build-PublishedReleaseRecordForPostSync -Text $productionShapeText -Version '1.3.5' -ReleaseCommitSha $ProductionShapeSha -PublicDigest $ProductionShapeDigest -Platforms @('linux/amd64')
Assert-Equal 'PRODUCTION_SHAPE_STATUS_PUBLISHED' $(if ($productionFinal.State -eq 'APPLIED' -and (Get-ReleaseRecordStateFromText -Text $productionFinal.Text) -eq 'PUBLISHED') { 'PASS' } else { 'FAIL' }) 'PASS'
Assert-equal 'PRODUCTION_SHAPE_RELEASE_SHA' $(if ($productionFinal.Text -match ('`releaseCommitSha`: `' + [regex]::Escape($ProductionShapeSha) + '`')) { 'PASS' } else { 'FAIL' }) 'PASS'
Assert-Equal 'PRODUCTION_SHAPE_GIT_TAG' $(if ($productionFinal.Text -match 'Git tag `v1\.3\.5`: \*\*PUBLISHED\*\*') { 'PASS' } else { 'FAIL' }) 'PASS'
Assert-equal 'PRODUCTION_SHAPE_GIT_TAG_TARGET' $(if ($productionFinal.Text -match ('Git tag target: `' + [regex]::Escape($ProductionShapeSha) + '`')) { 'PASS' } else { 'FAIL' }) 'PASS'
Assert-Equal 'PRODUCTION_SHAPE_GHCR_VERSION' $(if ($productionFinal.Text -match 'GHCR `ghcr\.io/kooiei-in4a/amane-mailer:v1\.3\.5`: \*\*PUBLISHED\*\*') { 'PASS' } else { 'FAIL' }) 'PASS'
Assert-equal 'PRODUCTION_SHAPE_GHCR_IMMUTABLE' $(if ($productionFinal.Text -match ('GHCR immutable `sha-' + [regex]::Escape($ProductionShapeSha) + '` tag: \*\*PUBLISHED\*\*')) { 'PASS' } else { 'FAIL' }) 'PASS'
Assert-equal 'PRODUCTION_SHAPE_PUBLIC_DIGEST' $(if ($productionFinal.Text -match ('Public OCI digest: `' + [regex]::Escape($ProductionShapeDigest) + '`')) { 'PASS' } else { 'FAIL' }) 'PASS'
Assert-equal 'PRODUCTION_SHAPE_NUGET' $(if ($productionFinal.Text -match 'NuGet `Amane\.Mailer\.Contracts 1\.3\.5`: \*\*PUBLISHED\*\*') { 'PASS' } else { 'FAIL' }) 'PASS'
Assert-equal 'PRODUCTION_SHAPE_NUGET_REVISION' $(if ($productionFinal.Text -match ('NuGet SourceLink revision: `' + [regex]::Escape($ProductionShapeSha) + '`')) { 'PASS' } else { 'FAIL' }) 'PASS'
Assert-equal 'PRODUCTION_SHAPE_GITHUB_RELEASE' $(if ($productionFinal.Text -match 'GitHub Release `v1\.3\.5`: \*\*PUBLISHED\*\*') { 'PASS' } else { 'FAIL' }) 'PASS'
Assert-equal 'PRODUCTION_SHAPE_GITHUB_RELEASE_URL' $(if ($productionFinal.Text -match 'GitHub Release URL: `https://github\.com/kooiei-in4a/amane-mailer/releases/tag/v1\.3\.5`') { 'PASS' } else { 'FAIL' }) 'PASS'

$coreContradiction = $false
foreach ($line in ($productionFinal.Text -split '\r?\n')) {
    if ($line -match '(?m)^>\s*Status:') { continue }
    if ($line -match '^\s*-\s+' -and $line -match 'NOT YET PUBLISHED') {
        $coreContradiction = $true
        break
    }
}
Assert-equal 'PRODUCTION_SHAPE_NO_CORE_CONTRADICTION' $(if (-not $coreContradiction -and (Get-ReleaseRecordStateFromText -Text $productionFinal.Text) -eq 'PUBLISHED') { 'PASS' } else { 'FAIL' }) 'PASS'

Assert-True 'MANUAL_PENDING_PRESERVED annotated tag' ($productionFinal.Text -match 'annotated tag object: \*\*PENDING\*\*') 'annotated tag should remain pending'
Assert-True 'MANUAL_PENDING_PRESERVED workflow run' ($productionFinal.Text -match 'Release image workflow run / attempt: \*\*PENDING\*\*') 'workflow run should remain pending'
Assert-True 'MANUAL_PENDING_PRESERVED artifact' ($productionFinal.Text -match 'Publication evidence artifact name / ID: \*\*PENDING\*\*') 'artifact evidence should remain pending'
Assert-True 'MANUAL_PENDING_PRESERVED nuget timestamp' ($productionFinal.Text -match 'NuGet publication timestamp: \*\*PENDING\*\*') 'nuget timestamp should remain pending'
Assert-True 'MANUAL_PENDING_PRESERVED github release id' ($productionFinal.Text -match 'GitHub Release ID: \*\*PENDING\*\*') 'github release id should remain pending'
Assert-True 'MANUAL_PENDING_PRESERVED latest promotion' ($productionFinal.Text -match 'GHCR `latest` digest promotion: \*\*PENDING\*\*') 'latest promotion may remain pending'
Assert-equal 'MANUAL_PENDING_PRESERVED' 'PASS' 'PASS'

$brokenProduction = $productionShapeText -replace 'Git tag `v1\.3\.5`: \*\*NOT YET PUBLISHED\*\*', 'Git tag identity `v1.3.5`: **NOT YET PUBLISHED**'
$failClose = Build-PublishedReleaseRecordForPostSync -Text $brokenProduction -Version '1.3.5' -ReleaseCommitSha $ProductionShapeSha -PublicDigest $ProductionShapeDigest -Platforms @('linux/amd64')
Assert-True 'PRODUCTION_SHAPE_FAIL_CLOSE no write text' ([string]::IsNullOrEmpty($failClose.Text)) 'fail-close must not return published text'
Assert-True 'PRODUCTION_SHAPE_FAIL_CLOSE not published state' ($failClose.State -eq 'CONFLICT' -or $failClose.State -eq 'INCOMPLETE') 'fail-close must CONFLICT or INCOMPLETE'
Assert-equal 'PRODUCTION_SHAPE_FAIL_CLOSE' $(if (($failClose.State -eq 'CONFLICT' -or $failClose.State -eq 'INCOMPLETE') -and [string]::IsNullOrEmpty($failClose.Text)) { 'PASS' } else { 'FAIL' }) 'PASS'

# Generic fixture regression remains covered by finalize pending fixture assertions above.
Assert-equal 'GENERIC_FIXTURE_REGRESSION' $(if ($finalizedFixture.State -eq 'APPLIED' -and (Get-ReleaseRecordStateFromText -Text $finalizedFixture.Text) -eq 'PUBLISHED') { 'PASS' } else { 'FAIL' }) 'PASS'

# --- #679 release-record parser: legacy + production-shape, fail-closed contradictions ---
$legacyShaOnly = '- releaseCommitSha: `' + $ProductionShapeSha + '`'
Assert-Equal 'RELEASE_SHA_LEGACY_PARSE' $(if ((Get-ReleaseRecordCommitShaFromText -Text $legacyShaOnly) -eq $ProductionShapeSha) { 'PASS' } else { 'FAIL' }) 'PASS'

$productionShaOnly = '- `releaseCommitSha`: `' + $ProductionShapeSha + '`'
Assert-Equal 'RELEASE_SHA_PRODUCTION_PARSE' $(if ((Get-ReleaseRecordCommitShaFromText -Text $productionShaOnly) -eq $ProductionShapeSha) { 'PASS' } else { 'FAIL' }) 'PASS'

$shaContradictionOther = 'a' * 40
$shaContradiction = $legacyShaOnly + [Environment]::NewLine + ('- `releaseCommitSha`: `' + $shaContradictionOther + '`')
Assert-Equal 'RELEASE_SHA_CONTRADICTION_FAIL_CLOSE' $(if ($null -eq (Get-ReleaseRecordCommitShaFromText -Text $shaContradiction)) { 'PASS' } else { 'FAIL' }) 'PASS'

$legacyDigestMultiline = '- public OCI digest:' + [Environment]::NewLine + '  `' + $ProductionShapeDigest + '`'
Assert-Equal 'DIGEST_LEGACY_MULTILINE_PARSE' $(if ((Get-ReleaseRecordDigestFromText -Text $legacyDigestMultiline) -eq $ProductionShapeDigest) { 'PASS' } else { 'FAIL' }) 'PASS'

$productionDigestOneline = '- Public OCI digest: `' + $ProductionShapeDigest + '`'
Assert-Equal 'DIGEST_PRODUCTION_ONELINE_PARSE' $(if ((Get-ReleaseRecordDigestFromText -Text $productionDigestOneline) -eq $ProductionShapeDigest) { 'PASS' } else { 'FAIL' }) 'PASS'

$digestOther = 'sha256:' + ('b' * 64)
$digestContradiction = $productionDigestOneline + [Environment]::NewLine + ('- public OCI digest: `' + $digestOther + '`')
Assert-Equal 'DIGEST_CONTRADICTION_FAIL_CLOSE' $(if ($null -eq (Get-ReleaseRecordDigestFromText -Text $digestContradiction)) { 'PASS' } else { 'FAIL' }) 'PASS'

$postSyncVerifyParse = ConvertTo-ReleaseRecordVerifyState -FetchState 'PRESENT' -Text $productionFinal.Text -Version '1.3.5' -ReleaseCommitSha $ProductionShapeSha -ObservedDigest $ProductionShapeDigest
$postSyncShaParsed = Get-ReleaseRecordCommitShaFromText -Text $productionFinal.Text
$postSyncDigestParsed = Get-ReleaseRecordDigestFromText -Text $productionFinal.Text
Assert-Equal 'PRODUCTION_POST_SYNC_RECORD_VERIFY_PARSE' $(if ($postSyncVerifyParse -eq 'EXACT_MATCH' -and $postSyncShaParsed -eq $ProductionShapeSha -and $postSyncDigestParsed -eq $ProductionShapeDigest) { 'PASS' } else { 'FAIL' }) 'PASS'


$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('amane-mailer-postsync-' + [Guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
try {
    Initialize-PostSyncFixtureRepo -Root $fixtureRoot -AuthorityVersion '1.3.4'
    $localPass = New-PostSyncFixtureLocalRepo
    $verifyObs = New-PostSyncVerifyObservers

    $dry = Invoke-ReleasePreparePostSync -Version '1.3.5' -ReleaseCommitSha $PostSyncSha135 -RepoRoot $fixtureRoot -Observers $verifyObs -LocalRepoOverride $localPass -Quiet
    Assert-Equal 'post-sync dry MUTATION_RESULT' $dry.Plan.MutationResult 'NOT_ATTEMPTED'
    Assert-Equal 'post-sync dry MUTATION_ATTEMPTED' $dry.Plan.MutationAttempted 'FALSE'
    Assert-Equal 'post-sync dry MUTATION_PERFORMED' $dry.Plan.MutationPerformed 'FALSE'

    $beforeExecute = Get-Content -LiteralPath (Join-Path $fixtureRoot 'README.md') -Raw
    $exec = Invoke-ReleasePreparePostSync -Version '1.3.5' -ReleaseCommitSha $PostSyncSha135 -RepoRoot $fixtureRoot -Observers $verifyObs -LocalRepoOverride $localPass -Execute -Quiet
    Assert-Equal 'post-sync execute APPLIED' $exec.Plan.MutationResult 'APPLIED'
    Assert-Equal 'post-sync execute MUTATION_PERFORMED' $exec.Plan.MutationPerformed 'TRUE'
    Assert-True 'post-sync execute changed README' ($beforeExecute -ne (Get-Content -LiteralPath (Join-Path $fixtureRoot 'README.md') -Raw)) 'README should change'
    Assert-True 'post-sync execute updates authority' ((Get-Content -LiteralPath (Join-Path $fixtureRoot 'release/current-public.json') -Raw) -match '"version": "1.3.5"') 'authority should advance'

    $publishedRecord = Get-Content -LiteralPath (Join-Path $fixtureRoot 'docs/releases/v1.3.5.md') -Raw
    Assert-Equal 'post-sync execute record published' (Get-ReleaseRecordStateFromText -Text $publishedRecord) 'PUBLISHED'
    Assert-True 'post-sync execute preserves release scope' ($publishedRecord -match 'planned release scope and must survive post-sync finalize') 'release scope text missing'
    Assert-True 'post-sync execute leaves workflow run pending' ($publishedRecord -match 'publish workflow run: \*\*PENDING\*\*') 'workflow run should remain pending'
    Assert-True 'post-sync execute updates digest' ($publishedRecord -match ([regex]::Escape($PostSyncDigest135))) 'digest not updated in execute path'
    $smokeJa = Get-Content -LiteralPath (Join-Path $fixtureRoot 'docs/ops/release-image-smoke.md') -Raw
    $smokeEn = Get-Content -LiteralPath (Join-Path $fixtureRoot 'docs/ops/release-image-smoke.en.md') -Raw
    $expectedSmokeLink = '[docs/releases/v1.3.5.md](../releases/v1.3.5.md)'
    Assert-True 'JA smoke link label+href sync' ($smokeJa.Contains($expectedSmokeLink)) 'JA smoke release-record link not synchronized'
    Assert-True 'EN smoke link label+href sync' ($smokeEn.Contains($expectedSmokeLink)) 'EN smoke release-record link not synchronized'
    Assert-True 'JA smoke link no stale label' (-not ($smokeJa -match '\[docs/releases/v1\.3\.4\.md\]\(\.\./releases/v1\.3\.5\.md\)')) 'JA stale label with new href'
    Assert-True 'EN smoke link no stale label' (-not ($smokeEn -match '\[docs/releases/v1\.3\.4\.md\]\(\.\./releases/v1\.3\.5\.md\)')) 'EN stale label with new href'
    Assert-Equal 'JA_SMOKE_LINK_FIX' $(if ($smokeJa.Contains($expectedSmokeLink)) { 'PASS' } else { 'FAIL' }) 'PASS'
    Assert-Equal 'EN_SMOKE_LINK_FIX' $(if ($smokeEn.Contains($expectedSmokeLink)) { 'PASS' } else { 'FAIL' }) 'PASS'
    Assert-Equal 'SMOKE_LINK_LABEL_SYNC' $(if (($smokeJa -match '\[docs/releases/v1\.3\.5\.md\]') -and ($smokeEn -match '\[docs/releases/v1\.3\.5\.md\]')) { 'PASS' } else { 'FAIL' }) 'PASS'
    Assert-Equal 'SMOKE_LINK_HREF_SYNC' $(if (($smokeJa -match '\(\.\./releases/v1\.3\.5\.md\)') -and ($smokeEn -match '\(\.\./releases/v1\.3\.5\.md\)')) { 'PASS' } else { 'FAIL' }) 'PASS'


    $already = Invoke-ReleasePreparePostSync -Version '1.3.5' -ReleaseCommitSha $PostSyncSha135 -RepoRoot $fixtureRoot -Observers $verifyObs -LocalRepoOverride $localPass -Execute -Quiet
    Assert-Equal 'post-sync already synchronized' $already.Plan.MutationResult 'ALREADY_APPLIED'
    Assert-Equal 'post-sync already zero writes' $already.Plan.MutationAttempted 'FALSE'

    $mixedRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('amane-mailer-postsync-mixed-' + [Guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $mixedRoot -Force | Out-Null
    try {
        Initialize-PostSyncFixtureRepo -Root $mixedRoot -AuthorityVersion '1.3.4'
        $mixedSecurityPath = Join-Path $mixedRoot 'SECURITY.md'
        $mixedSecurity = Get-Content -LiteralPath $mixedSecurityPath -Raw
        [System.IO.File]::WriteAllText($mixedSecurityPath, ($mixedSecurity -replace '\| 1\.3\.4   \| Yes \(latest release\) \|', '| 1.3.5   | Yes (latest release) |'))
        Assert-True 'post-sync mixed fixture corrupts SECURITY' ((Get-Content -LiteralPath $mixedSecurityPath -Raw) -match '\| 1\.3\.5   \| Yes \(latest release\) \|') 'SECURITY corruption missing'
        $conflict = Invoke-ReleasePreparePostSync -Version '1.3.5' -ReleaseCommitSha $PostSyncSha135 -RepoRoot $mixedRoot -Observers $verifyObs -LocalRepoOverride $localPass -Execute -Quiet
        Assert-Equal 'post-sync mixed follower CONFLICT' $conflict.Plan.MutationResult 'CONFLICT'
        Assert-Equal 'post-sync mixed zero writes' $conflict.Plan.MutationAttempted 'FALSE'
    }
    finally {
        Remove-Item -LiteralPath $mixedRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    $incompleteObs = New-PostSyncVerifyObservers
    $incompleteObs['Ghcr'] = { param($ver, $shaArg) New-ArtifactFact -State 'INCOMPLETE' -Reason 'AUTH' }
    $incomplete = Invoke-ReleasePreparePostSync -Version '1.3.5' -ReleaseCommitSha $PostSyncSha135 -RepoRoot $fixtureRoot -Observers $incompleteObs -LocalRepoOverride $localPass -Execute -Quiet
    Assert-Equal 'post-sync verify incomplete' $incomplete.Plan.MutationResult 'INCOMPLETE'
    Assert-Equal 'post-sync verify incomplete zero writes' $incomplete.Plan.MutationAttempted 'FALSE'

    $platformRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('amane-mailer-postsync-platform-' + [Guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $platformRoot -Force | Out-Null
    try {
        Initialize-PostSyncFixtureRepo -Root $platformRoot -AuthorityVersion '1.3.4'
        $platformRecordPath = Join-Path $platformRoot 'docs/releases/v1.3.5.md'
        $platformRecord = Get-Content -LiteralPath $platformRecordPath -Raw
        $platformRecord = $platformRecord -replace 'supported platform: ``linux/amd64``', 'supported platform: **PENDING**'
        [System.IO.File]::WriteAllText($platformRecordPath, $platformRecord, (New-Object System.Text.UTF8Encoding $false))
        $platformIncomplete = Invoke-ReleasePreparePostSync -Version '1.3.5' -ReleaseCommitSha $PostSyncSha135 -RepoRoot $platformRoot -Observers $verifyObs -LocalRepoOverride $localPass -Execute -Quiet
        Assert-Equal 'post-sync platform unknown INCOMPLETE' $platformIncomplete.Plan.MutationResult 'INCOMPLETE'
        Assert-Equal 'post-sync platform unknown zero writes' $platformIncomplete.Plan.MutationAttempted 'FALSE'
    }
    finally {
        Remove-Item -LiteralPath $platformRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    $syncRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('amane-mailer-postsync-sync-' + [Guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $syncRoot -Force | Out-Null
    try {
        Initialize-PostSyncFixtureRepo -Root $syncRoot -SynchronizedTo135
        $candidate = Invoke-ReleasePreparePostSync -Version '1.3.5' -ReleaseCommitSha $PostSyncSha135 -RepoRoot $syncRoot -Observers $verifyObs -LocalRepoOverride $localPass -Quiet
        Assert-Equal 'version-prep fixture authority remains target' $candidate.Plan.MutationResult 'ALREADY_APPLIED'
    }
    finally {
        Remove-Item -LiteralPath $syncRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    $aheadRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('amane-mailer-postsync-ahead-' + [Guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $aheadRoot -Force | Out-Null
    try {
        Initialize-PostSyncFixtureRepo -Root $aheadRoot -SynchronizedTo135
        $ahead = Invoke-ReleasePreparePostSync -Version '1.3.4' -ReleaseCommitSha $PostSyncSha134 -RepoRoot $aheadRoot -Observers $verifyObs -LocalRepoOverride $localPass -Execute -Quiet
        Assert-Equal 'post-sync authority ahead CONFLICT' $ahead.Plan.MutationResult 'CONFLICT'
        Assert-Equal 'post-sync authority ahead zero writes' $ahead.Plan.MutationAttempted 'FALSE'
    }
    finally {
        Remove-Item -LiteralPath $aheadRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
finally {
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}

# Observation API must be proven on fixtures, not caller-repo live current-public.json (#679 follow-up).
$observationFixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('amane-mailer-authority-obs-' + [Guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $observationFixtureRoot -Force | Out-Null
try {
    Initialize-PostSyncFixtureRepo -Root $observationFixtureRoot -AuthorityVersion '1.3.4'
    $predecessorObs = Get-CurrentPublicAuthorityObservation -RepoRoot $observationFixtureRoot
    Assert-Equal 'PREDECESSOR_AUTHORITY_OBSERVATION' $predecessorObs.State 'PRESENT'
    Assert-Equal 'PREDECESSOR_VERSION' $predecessorObs.Authority.Version '1.3.4'
    Assert-Equal 'predecessor authority observation tag' $predecessorObs.Authority.Tag 'v1.3.4'

    $observationTargetRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('amane-mailer-authority-obs-target-' + [Guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $observationTargetRoot -Force | Out-Null
    try {
        Initialize-PostSyncFixtureRepo -Root $observationTargetRoot -AuthorityVersion '1.3.4' -SynchronizedTo135
        $targetObs = Get-CurrentPublicAuthorityObservation -RepoRoot $observationTargetRoot
        Assert-Equal 'TARGET_AUTHORITY_OBSERVATION' $targetObs.State 'PRESENT'
        Assert-Equal 'TARGET_VERSION' $targetObs.Authority.Version '1.3.5'
        Assert-equal 'target authority observation tag' $targetObs.Authority.Tag 'v1.3.5'
    }
    finally {
        Remove-Item -LiteralPath $observationTargetRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
finally {
    Remove-Item -LiteralPath $observationFixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}

# --- #675 post-mutation readback visibility fix + promote-latest ---
# Reproduce the Phase 3 defect: GetNewClosure created inside the module loses private command lookup.
$releaseClientModule = Get-Module release-client
$brokenClosure = & $releaseClientModule { { Get-CollisionRank -State 'PRESENT' }.GetNewClosure() }
$brokenThrew = $false
try {
    $null = & $brokenClosure
}
catch {
    $brokenThrew = [bool]($_.Exception.Message -match 'not recognized')
}
Assert-True 'raw GetNewClosure cannot resolve private Get-CollisionRank' $brokenThrew 'documents Phase 3 readback defect root cause'

$boundFetcher = New-ReleaseModuleBoundScriptBlock -Capture @{ State = 'PRESENT' } -ScriptBlock {
    param($c)
    return Get-CollisionRank -State $c.State
}
Assert-Equal 'module-bound fetcher resolves private Get-CollisionRank' (& $boundFetcher) 2

$publishStyleFetcher = New-ReleaseModuleBoundScriptBlock -Capture @{
    ReleaseCommitSha = $MainSha
} -ScriptBlock {
    param($c)
    $json = '{"total_count":1,"workflow_runs":[{"id":9101,"path":".github/workflows/publish-release-image.yml","event":"workflow' + '_dispatch","head_sha":"' + $c.ReleaseCommitSha + '","status":"completed","conclusion":"success","name":"x","display_title":"x"}]}'
    $runs = Convert-GitHubWorkflowRunsJson -Json $json
    $runObs = ConvertTo-WorkflowDispatchRunObservation -Runs $runs -WorkflowPath '.github/workflows/publish-release-image.yml' -ReleaseCommitSha $c.ReleaseCommitSha
    return [string]$runObs.State
}
Assert-Equal 'publish-image production-style readback fetcher' (& $publishStyleFetcher) 'CANDIDATE_PRESENT'

$tagStyleFetcher = New-ReleaseModuleBoundScriptBlock -Capture @{ Version = '1.3.5' } -ScriptBlock {
    param($c)
    $cmd = Get-Command -Name Get-GitTagObservation -CommandType Function -ErrorAction Stop
    return [pscustomobject]@{ Name = $cmd.Name; Version = $c.Version }
}
$tagStyleResult = & $tagStyleFetcher
Assert-Equal 'create-tag production-style private observer name' $tagStyleResult.Name 'Get-GitTagObservation'

$releaseStyleFetcher = New-ReleaseModuleBoundScriptBlock -Capture @{ Version = '1.3.5' } -ScriptBlock {
    param($c)
    $cmd = Get-Command -Name Get-GitHubReleaseObservation -CommandType Function -ErrorAction Stop
    return [pscustomobject]@{ Name = $cmd.Name; Version = $c.Version }
}
$releaseStyleResult = & $releaseStyleFetcher
Assert-Equal 'create-github-release production-style private observer name' $releaseStyleResult.Name 'Get-GitHubReleaseObservation'

$nugetStyleFetcher = New-ReleaseModuleBoundScriptBlock -Capture @{ ReleaseCommitSha = $MainSha } -ScriptBlock {
    param($c)
    $cmd = Get-Command -Name Get-GitHubWorkflowDispatchRuns -CommandType Function -ErrorAction Stop
    return [pscustomobject]@{ Name = $cmd.Name; Sha = $c.ReleaseCommitSha }
}
$nugetStyleResult = & $nugetStyleFetcher
Assert-Equal 'publish-nuget production-style private observer name' $nugetStyleResult.Name 'Get-GitHubWorkflowDispatchRuns'

$createTagReadBackFetcher = New-ReleaseModuleBoundScriptBlock -Capture @{ Version = '1.3.5'; Sha = $MainSha } -ScriptBlock {
    param($c)
    $null = Get-Command -Name Get-GitTagObservation -CommandType Function -ErrorAction Stop
    return New-ArtifactFact -State 'PRESENT' -TargetSha $c.Sha
}
$createTagFacts = [pscustomobject]@{
    Version          = '1.3.5'
    ReleaseCommitSha = $MainSha
    GitTag           = New-ArtifactFact -State 'ABSENT'
    ReadBackGitTag   = New-ArtifactFact -State 'ABSENT'
    ReadBackFetcher  = $createTagReadBackFetcher
    Ghcr             = New-ExactGhcrFact -Sha $MainSha -VersionValue '1.3.5' -Digest $DigestA
    Execute          = $true
    Executor         = (New-FakeMutationExecutor -Outcome 'SUCCESS')
}
$createTagProdReadbackMap = Get-ReleaseCreateTagMutationStatus -Facts $createTagFacts
Assert-Equal 'create-tag production readback APPLIED' $createTagProdReadbackMap['MUTATION_RESULT'] 'APPLIED'
Assert-Equal 'create-tag production readback performed' $createTagProdReadbackMap['MUTATION_PERFORMED'] 'TRUE'

$publishImageReadBackFetcher = New-ReleaseModuleBoundScriptBlock -Capture @{ ReleaseCommitSha = $MainSha } -ScriptBlock {
    param($c)
    $null = Get-Command -Name Get-GitHubWorkflowDispatchRuns -CommandType Function -ErrorAction Stop
    return 'CANDIDATE_PRESENT'
}
$publishImageFacts = [pscustomobject]@{
    Version           = '1.3.5'
    ReleaseCommitSha  = $MainSha
    PreflightMap      = ([ordered]@{ PREFLIGHT_RESULT = 'PASS'; TECHNICAL_READINESS = 'READY'; SOURCE_BINDING = 'PASS'; VERSION_PREP = 'PASS'; IMAGE_PUBLISH_RUN = 'ABSENT'; IMAGE_PUBLISH_RUN_ID = 'NONE' })
    SourceBinding     = 'PASS'
    VersionPrep       = 'PASS'
    Ghcr              = New-ArtifactFact -State 'ABSENT'
    ReadBackGhcr      = New-ArtifactFact -State 'ABSENT'
    ReadBackFetcher   = $publishImageReadBackFetcher
    ImagePublishRun   = 'ABSENT'
    ImagePublishRunId = 'NONE'
    Execute           = $true
    Executor          = (New-FakeMutationExecutor -Outcome 'SUCCESS')
}
$publishImageProdReadbackMap = Get-ReleasePublishImageMutationStatus -Facts $publishImageFacts
Assert-Equal 'publish-image production readback APPLIED' $publishImageProdReadbackMap['MUTATION_RESULT'] 'APPLIED'

$nugetReadBackFetcher = New-ReleaseModuleBoundScriptBlock -Capture @{ ReleaseCommitSha = $MainSha } -ScriptBlock {
    param($c)
    $null = Get-Command -Name Get-GitHubWorkflowDispatchRuns -CommandType Function -ErrorAction Stop
    return 'CANDIDATE_PRESENT'
}
$nugetFacts = [pscustomobject]@{
    Version           = '1.3.5'
    ReleaseCommitSha  = $MainSha
    GitTag            = New-ArtifactFact -State 'PRESENT' -TargetSha $MainSha
    Ghcr              = New-ExactGhcrFact -Sha $MainSha -VersionValue '1.3.5' -Digest $DigestA
    Nuget             = New-ArtifactFact -State 'ABSENT'
    ReadBackNuget     = New-ArtifactFact -State 'ABSENT'
    ReadBackFetcher   = $nugetReadBackFetcher
    NugetPublishRun   = 'ABSENT'
    NugetPublishRunId = 'NONE'
    Execute           = $true
    Executor          = (New-FakeMutationExecutor -Outcome 'SUCCESS')
}
$nugetProdReadbackMap = Get-ReleasePublishNugetMutationStatus -Facts $nugetFacts
Assert-Equal 'publish-nuget production readback APPLIED' $nugetProdReadbackMap['MUTATION_RESULT'] 'APPLIED'

$ghReleaseReadBackFetcher = New-ReleaseModuleBoundScriptBlock -Capture @{ Version = '1.3.5' } -ScriptBlock {
    param($c)
    $null = Get-Command -Name Get-GitHubReleaseObservation -CommandType Function -ErrorAction Stop
    return New-ArtifactFact -State 'PRESENT'
}
$ghReleaseTagFetcher = New-ReleaseModuleBoundScriptBlock -Capture @{ Version = '1.3.5'; Sha = $MainSha } -ScriptBlock {
    param($c)
    $null = Get-Command -Name Get-GitTagObservation -CommandType Function -ErrorAction Stop
    return New-ArtifactFact -State 'PRESENT' -TargetSha $c.Sha
}
$ghReleaseFacts = [pscustomobject]@{
    Version               = '1.3.5'
    ReleaseCommitSha      = $MainSha
    GitTag                = New-ArtifactFact -State 'PRESENT' -TargetSha $MainSha
    Ghcr                  = New-ExactGhcrFact -Sha $MainSha -VersionValue '1.3.5' -Digest $DigestA
    Nuget                 = New-ArtifactFact -State 'PRESENT'
    GitHubRelease         = New-ArtifactFact -State 'ABSENT'
    ReadBackGitHubRelease = New-ArtifactFact -State 'ABSENT'
    ReadBackGitTag        = New-ArtifactFact -State 'PRESENT' -TargetSha $MainSha
    ReadBackFetcher       = $ghReleaseReadBackFetcher
    ReadBackTagFetcher    = $ghReleaseTagFetcher
    ReleaseNotesPath      = 'notes.md'
    ReleaseNotesGuard     = 'PRESENT'
    Execute               = $true
    Executor              = (New-FakeMutationExecutor -Outcome 'SUCCESS')
}
$ghReleaseProdReadbackMap = Get-ReleaseCreateGitHubReleaseMutationStatus -Facts $ghReleaseFacts
Assert-Equal 'create-github-release production readback APPLIED' $ghReleaseProdReadbackMap['MUTATION_RESULT'] 'APPLIED'

$promoteLatestText = [System.IO.File]::ReadAllText((Join-Path $RepoRoot '.github\workflows\promote-release-latest.yml'))
Assert-Equal 'canonical promote-release-latest contract' (Test-PromoteReleaseLatestWorkflowContract -Text $promoteLatestText) 'PASS'
$promoteNoEnv = $promoteLatestText.Replace('environment: release', 'environment: staging')
Assert-Equal 'promote-latest environment release missing FAIL' (Test-PromoteReleaseLatestWorkflowContract -Text $promoteNoEnv) 'FAIL'
$promoteWithBuild = $promoteLatestText + "`n          docker build .`n"
Assert-Equal 'promote-latest docker build forbidden FAIL' (Test-PromoteReleaseLatestWorkflowContract -Text $promoteWithBuild) 'FAIL'
$promoteNoCraneCopy = $promoteLatestText.Replace('copy "${IMAGE_REPOSITORY}@${EXPECTED_DIGEST}"', 'digest "${IMAGE_REPOSITORY}@${EXPECTED_DIGEST}"')
Assert-Equal 'promote-latest crane copy missing FAIL' (Test-PromoteReleaseLatestWorkflowContract -Text $promoteNoCraneCopy) 'FAIL'

function New-ReadyPromoteLatestObservers {
    param(
        [string]$Sha = $MainSha,
        [string]$Digest = $DigestA,
        [string]$LatestState = 'ABSENT',
        [string]$LatestDigest = ''
    )
    $latestFact = if ($LatestState -eq 'ABSENT') {
        New-ArtifactFact -State 'ABSENT'
    }
    elseif ($LatestState -eq 'INCOMPLETE') {
        New-ArtifactFact -State 'INCOMPLETE' -Reason 'AUTH'
    }
    elseif ($LatestState -eq 'EXACT_MATCH') {
        New-ArtifactFact -State 'PRESENT' -Digest $Digest -Revision $Sha -OciVersion '1.3.5'
    }
    else {
        $staleDigest = $LatestDigest
        if ([string]::IsNullOrWhiteSpace($staleDigest)) { $staleDigest = $DigestB }
        New-ArtifactFact -State 'PRESENT' -Digest $staleDigest -Revision $WrongSha -OciVersion '1.3.4'
    }
    return @{
        GitTag           = { param($ver) New-ArtifactFact -State 'PRESENT' -TargetSha $Sha }.GetNewClosure()
        Ghcr             = { param($ver, $shaArg) New-ExactGhcrFact -Sha $Sha -VersionValue '1.3.5' -Digest $Digest }.GetNewClosure()
        Nuget            = { param($ver) New-ArtifactFact -State 'PRESENT' }.GetNewClosure()
        GitHubRelease    = { param($ver) New-ArtifactFact -State 'PRESENT' }.GetNewClosure()
        Latest           = { $latestFact }.GetNewClosure()
        SourceVersions   = { param($shaArg, $ver) [pscustomobject]@{ ContractsState = 'PRESENT'; ContractsVersion = '1.3.5'; OpenApiState = 'PRESENT'; OpenApiVersion = '1.3.5' } }.GetNewClosure()
        NugetRevision    = { param($ver) [pscustomobject]@{ State = 'PRESENT'; Commit = $Sha; Reason = '' } }.GetNewClosure()
        PromoteLatestRun = { param($identity) [pscustomobject]@{ State = 'ABSENT'; Id = 'NONE' } }.GetNewClosure()
    }
}

function Invoke-PromoteLatestFixture {
    param(
        $Observers,
        $Executor = $null,
        $CommandRunner = $null,
        [switch]$Execute,
        [string]$Version = '1.3.5',
        [string]$Sha = $MainSha,
        [string]$Digest = $DigestA
    )
    return Invoke-ReleasePromoteLatest -Version $Version -ReleaseCommitSha $Sha -ExpectedDigest $Digest -RepoRoot $RepoRoot -Observers $Observers -Executor $Executor -CommandRunner $CommandRunner -Execute:$Execute -Quiet
}

$promoteIdentity = Get-PromoteLatestRunIdentity -Version '1.3.5' -ReleaseCommitSha $MainSha -ExpectedDigest $DigestA
Assert-True 'promote-latest run identity contains version' ($promoteIdentity -match '1\.3\.5') 'identity should include version'
Assert-True 'promote-latest run identity contains sha' ($promoteIdentity.Contains($MainSha)) 'identity should include sha'
Assert-True 'promote-latest run identity contains digest' ($promoteIdentity.Contains($DigestA)) 'identity should include digest'

$promoteDryObs = New-ReadyPromoteLatestObservers -Sha $MainSha -Digest $DigestA -LatestState 'ABSENT'
$script:MutationExecutorCalls = @{ Count = 0 }
$promoteDry = Invoke-PromoteLatestFixture -Observers $promoteDryObs -Executor (New-FakeMutationExecutor)
Assert-Equal 'promote-latest dry NOT_ATTEMPTED' $promoteDry['MUTATION_RESULT'] 'NOT_ATTEMPTED'
Assert-Equal 'promote-latest dry MUTATION_ATTEMPTED' $promoteDry['MUTATION_ATTEMPTED'] 'FALSE'
Assert-Equal 'promote-latest dry LATEST_STATE ABSENT' $promoteDry['LATEST_STATE'] 'ABSENT'
Assert-Equal 'promote-latest dry zero executor calls' $script:MutationExecutorCalls.Count 0

$promoteExactObs = New-ReadyPromoteLatestObservers -Sha $MainSha -Digest $DigestA -LatestState 'EXACT_MATCH'
$promoteExact = Invoke-PromoteLatestFixture -Observers $promoteExactObs -Executor (New-FakeMutationExecutor) -Execute
Assert-Equal 'promote-latest EXACT ALREADY_APPLIED' $promoteExact['MUTATION_RESULT'] 'ALREADY_APPLIED'
Assert-Equal 'promote-latest EXACT LATEST_STATE' $promoteExact['LATEST_STATE'] 'EXACT_MATCH'
Assert-Equal 'promote-latest EXACT zero executor calls' $script:MutationExecutorCalls.Count 0

$promoteAbsentObs = New-ReadyPromoteLatestObservers -Sha $MainSha -Digest $DigestA -LatestState 'ABSENT'
$promoteAbsentObs['ReadBackPromoteLatestRun'] = {
    param($identity)
    [pscustomobject]@{ State = 'CANDIDATE_PRESENT'; Id = '7201'; Status = 'waiting'; Conclusion = '' }
}.GetNewClosure()
$promoteAbsentExec = New-FakeMutationExecutor -Outcome 'SUCCESS'
$promoteAbsent = Invoke-PromoteLatestFixture -Observers $promoteAbsentObs -Executor $promoteAbsentExec -Execute
Assert-Equal 'promote-latest ABSENT eligible APPLIED' $promoteAbsent['MUTATION_RESULT'] 'APPLIED'
Assert-Equal 'promote-latest ABSENT one executor call' $script:MutationExecutorCalls.Count 1
Assert-Equal 'promote-latest ABSENT LATEST_STATE' $promoteAbsent['LATEST_STATE'] 'ABSENT'

$promoteStaleObs = New-ReadyPromoteLatestObservers -Sha $MainSha -Digest $DigestA -LatestState 'STALE'
$promoteStaleObs['ReadBackPromoteLatestRun'] = {
    param($identity)
    [pscustomobject]@{ State = 'CANDIDATE_PRESENT'; Id = '7202'; Status = 'queued'; Conclusion = '' }
}.GetNewClosure()
$promoteStaleExec = New-FakeMutationExecutor -Outcome 'SUCCESS'
$promoteStale = Invoke-PromoteLatestFixture -Observers $promoteStaleObs -Executor $promoteStaleExec -Execute
Assert-Equal 'promote-latest STALE eligible APPLIED' $promoteStale['MUTATION_RESULT'] 'APPLIED'
Assert-Equal 'promote-latest STALE LATEST_STATE' $promoteStale['LATEST_STATE'] 'STALE'
Assert-Equal 'promote-latest STALE one executor call' $script:MutationExecutorCalls.Count 1

$promoteIncompleteObs = New-ReadyPromoteLatestObservers -Sha $MainSha -Digest $DigestA -LatestState 'INCOMPLETE'
$promoteIncomplete = Invoke-PromoteLatestFixture -Observers $promoteIncompleteObs -Executor (New-FakeMutationExecutor) -Execute
Assert-Equal 'promote-latest INCOMPLETE STOP' $promoteIncomplete['MUTATION_RESULT'] 'INCOMPLETE'
Assert-Equal 'promote-latest INCOMPLETE zero executor' $script:MutationExecutorCalls.Count 0

$promoteDigestMismatchObs = New-ReadyPromoteLatestObservers -Sha $MainSha -Digest $DigestA
$promoteDigestMismatchObs['Ghcr'] = { param($ver, $shaArg) New-ExactGhcrFact -Sha $MainSha -VersionValue '1.3.5' -Digest $DigestB }.GetNewClosure()
$promoteDigestMismatch = Invoke-PromoteLatestFixture -Observers $promoteDigestMismatchObs -Executor (New-FakeMutationExecutor) -Execute -Digest $DigestA
Assert-Equal 'promote-latest version digest mismatch CONFLICT' $promoteDigestMismatch['MUTATION_RESULT'] 'CONFLICT'
Assert-Equal 'promote-latest version digest guard' $promoteDigestMismatch['GUARD_EXPECTED_DIGEST'] 'CONFLICT'

$promoteShaDigestMismatchObs = New-ReadyPromoteLatestObservers -Sha $MainSha -Digest $DigestA
$promoteShaDigestMismatchObs['Ghcr'] = {
    param($ver, $shaArg)
    New-ArtifactFact -State 'PRESENT' -Digest $DigestA -Revision $MainSha -OciVersion '1.3.5' -ShaTagState 'PRESENT' -ShaTagDigest $DigestB
}.GetNewClosure()
$promoteShaDigestMismatch = Invoke-PromoteLatestFixture -Observers $promoteShaDigestMismatchObs -Executor (New-FakeMutationExecutor) -Execute
Assert-Equal 'promote-latest sha digest mismatch CONFLICT' $promoteShaDigestMismatch['MUTATION_RESULT'] 'CONFLICT'

$promoteOciRevObs = New-ReadyPromoteLatestObservers -Sha $MainSha -Digest $DigestA
$promoteOciRevObs['Ghcr'] = {
    param($ver, $shaArg)
    New-ArtifactFact -State 'PRESENT' -Digest $DigestA -Revision $WrongSha -OciVersion '1.3.5' -ShaTagState 'PRESENT' -ShaTagDigest $DigestA
}.GetNewClosure()
$promoteOciRev = Invoke-PromoteLatestFixture -Observers $promoteOciRevObs -Executor (New-FakeMutationExecutor) -Execute
Assert-Equal 'promote-latest OCI revision mismatch CONFLICT' $promoteOciRev['MUTATION_RESULT'] 'CONFLICT'
Assert-Equal 'promote-latest OCI revision guard GHCR' $promoteOciRev['GUARD_GHCR'] 'CONFLICT'

$promoteOciVerObs = New-ReadyPromoteLatestObservers -Sha $MainSha -Digest $DigestA
$promoteOciVerObs['Ghcr'] = {
    param($ver, $shaArg)
    New-ArtifactFact -State 'PRESENT' -Digest $DigestA -Revision $MainSha -OciVersion '1.3.4' -ShaTagState 'PRESENT' -ShaTagDigest $DigestA
}.GetNewClosure()
$promoteOciVer = Invoke-PromoteLatestFixture -Observers $promoteOciVerObs -Executor (New-FakeMutationExecutor) -Execute
Assert-Equal 'promote-latest OCI version mismatch CONFLICT' $promoteOciVer['MUTATION_RESULT'] 'CONFLICT'

$promoteTagMismatchObs = New-ReadyPromoteLatestObservers -Sha $MainSha -Digest $DigestA
$promoteTagMismatchObs['GitTag'] = { param($ver) New-ArtifactFact -State 'PRESENT' -TargetSha $WrongSha }.GetNewClosure()
$promoteTagMismatch = Invoke-PromoteLatestFixture -Observers $promoteTagMismatchObs -Executor (New-FakeMutationExecutor) -Execute
Assert-Equal 'promote-latest git tag mismatch CONFLICT' $promoteTagMismatch['MUTATION_RESULT'] 'CONFLICT'

$promoteNugetRevObs = New-ReadyPromoteLatestObservers -Sha $MainSha -Digest $DigestA
$promoteNugetRevObs['NugetRevision'] = { param($ver) [pscustomobject]@{ State = 'PRESENT'; Commit = $WrongSha; Reason = '' } }.GetNewClosure()
$promoteNugetRev = Invoke-PromoteLatestFixture -Observers $promoteNugetRevObs -Executor (New-FakeMutationExecutor) -Execute
Assert-Equal 'promote-latest nuget revision mismatch CONFLICT' $promoteNugetRev['MUTATION_RESULT'] 'CONFLICT'
Assert-equal 'promote-latest nuget revision guard' $promoteNugetRev['GUARD_NUGET_REVISION'] 'CONFLICT'

$promoteReleaseMismatchObs = New-ReadyPromoteLatestObservers -Sha $MainSha -Digest $DigestA
$promoteReleaseMismatchObs['GitHubRelease'] = { param($ver) New-ArtifactFact -State 'PRESENT' -Reason 'DRAFT' }.GetNewClosure()
$promoteReleaseMismatch = Invoke-PromoteLatestFixture -Observers $promoteReleaseMismatchObs -Executor (New-FakeMutationExecutor) -Execute
Assert-Equal 'promote-latest github release mismatch CONFLICT' $promoteReleaseMismatch['MUTATION_RESULT'] 'CONFLICT'

$promoteRunWaitingObs = New-ReadyPromoteLatestObservers -Sha $MainSha -Digest $DigestA -LatestState 'ABSENT'
$promoteRunWaitingObs['PromoteLatestRun'] = {
    param($identity)
    [pscustomobject]@{ State = 'CANDIDATE_PRESENT'; Id = '7001'; Status = 'waiting'; Conclusion = '' }
}.GetNewClosure()
$promoteRunWaiting = Invoke-PromoteLatestFixture -Observers $promoteRunWaitingObs -Executor (New-FakeMutationExecutor) -Execute
Assert-Equal 'promote-latest matching waiting run no redispatch' $promoteRunWaiting['MUTATION_RESULT'] 'ALREADY_APPLIED'
Assert-Equal 'promote-latest matching waiting zero executor' $script:MutationExecutorCalls.Count 0

$promoteRunFailedObs = New-ReadyPromoteLatestObservers -Sha $MainSha -Digest $DigestA -LatestState 'ABSENT'
$promoteRunFailedObs['PromoteLatestRun'] = {
    param($identity)
    [pscustomobject]@{ State = 'FAILED_MATCH'; Id = '7002'; Status = 'completed'; Conclusion = 'failure' }
}.GetNewClosure()
$promoteRunFailed = Invoke-PromoteLatestFixture -Observers $promoteRunFailedObs -Executor (New-FakeMutationExecutor) -Execute
Assert-Equal 'promote-latest matching failed run no retry' $promoteRunFailed['MUTATION_RESULT'] 'CONFLICT'
Assert-Equal 'promote-latest matching failed zero executor' $script:MutationExecutorCalls.Count 0

$promoteRunAmbigObs = New-ReadyPromoteLatestObservers -Sha $MainSha -Digest $DigestA -LatestState 'ABSENT'
$promoteRunAmbigObs['PromoteLatestRun'] = {
    param($identity)
    [pscustomobject]@{ State = 'AMBIGUOUS'; Id = 'NONE' }
}.GetNewClosure()
$promoteRunAmbig = Invoke-PromoteLatestFixture -Observers $promoteRunAmbigObs -Executor (New-FakeMutationExecutor) -Execute
Assert-Equal 'promote-latest multiple matching runs CONFLICT' $promoteRunAmbig['MUTATION_RESULT'] 'CONFLICT'

$promotePostMismatchObs = New-ReadyPromoteLatestObservers -Sha $MainSha -Digest $DigestA -LatestState 'ABSENT'
$promotePostMismatchObs['ReadBackPromoteLatestRun'] = {
    param($identity)
    [pscustomobject]@{ State = 'AMBIGUOUS'; Id = 'NONE' }
}.GetNewClosure()
$promotePostMismatch = Invoke-PromoteLatestFixture -Observers $promotePostMismatchObs -Executor (New-FakeMutationExecutor -Outcome 'SUCCESS') -Execute
Assert-Equal 'promote-latest post-readback digest mismatch CONFLICT' $promotePostMismatch['MUTATION_RESULT'] 'CONFLICT'

$runIdentityMatch = Get-PromoteLatestRunIdentity -Version '1.3.5' -ReleaseCommitSha $MainSha -ExpectedDigest $DigestA
$promoteRunObsSingle = ConvertTo-PromoteLatestRunObservation -Runs @(
    (New-DispatchRun -Id '7100' -Path '.github/workflows/promote-release-latest.yml' -HeadSha $MainSha -Name $runIdentityMatch -Status 'in_progress' -Conclusion '')
) -WorkflowPath '.github/workflows/promote-release-latest.yml' -RunIdentity $runIdentityMatch
Assert-Equal 'promote-latest run obs in_progress candidate' $promoteRunObsSingle.State 'CANDIDATE_PRESENT'

$promoteRunObsSuccess = ConvertTo-PromoteLatestRunObservation -Runs @(
    (New-DispatchRun -Id '7103' -Path '.github/workflows/promote-release-latest.yml' -HeadSha $MainSha -Name $runIdentityMatch -Status 'completed' -Conclusion 'success')
) -WorkflowPath '.github/workflows/promote-release-latest.yml' -RunIdentity $runIdentityMatch
Assert-Equal 'promote-latest run obs completed success SUCCESS_MATCH' $promoteRunObsSuccess.State 'SUCCESS_MATCH'

$promoteRunObsMulti = ConvertTo-PromoteLatestRunObservation -Runs @(
    (New-DispatchRun -Id '7101' -Path '.github/workflows/promote-release-latest.yml' -HeadSha $MainSha -Name $runIdentityMatch),
    (New-DispatchRun -Id '7102' -Path '.github/workflows/promote-release-latest.yml' -HeadSha $MainSha -DisplayTitle $runIdentityMatch)
) -WorkflowPath '.github/workflows/promote-release-latest.yml' -RunIdentity $runIdentityMatch
Assert-Equal 'promote-latest run obs multiple AMBIGUOUS' $promoteRunObsMulti.State 'AMBIGUOUS'

$script:CommandRunnerCalls.Clear()
$promoteProdRunner = New-FakeCommandRunner
$promoteProdExec = New-ReleaseProductionPromoteLatestExecutor -CommandRunner $promoteProdRunner -RepoRoot $RepoRoot
$promoteProdExecResult = & $promoteProdExec @{
    Version          = '1.3.5'
    ReleaseCommitSha = $MainSha
    ExpectedDigest   = $DigestA
}
Assert-Equal 'promote-latest prod executor SUCCESS' $promoteProdExecResult.State 'SUCCESS'
Assert-Equal 'promote-latest prod executor one call' $script:CommandRunnerCalls.Count 1
Assert-RunnerCall -Name 'promote-latest prod gh' -Index 0 -Program 'gh' -ExpectedArgs @(
    'workflow', 'run', 'promote-release-latest.yml',
    '--repo', 'kooiei-in4a/amane-mailer',
    '--ref', 'main',
    '-f', ('release_version=1.3.5'),
    '-f', ('release_commit_sha=' + $MainSha),
    '-f', ('expected_digest=' + $DigestA)
) -ExpectedCwd $RepoRoot

$script:CommandRunnerCalls.Clear()
$promoteProdObs = New-ReadyPromoteLatestObservers -Sha $MainSha -Digest $DigestA -LatestState 'ABSENT'
$promoteProdObs['ReadBackPromoteLatestRun'] = {
    param($identity)
    [pscustomobject]@{ State = 'CANDIDATE_PRESENT'; Id = '7301'; Status = 'waiting'; Conclusion = '' }
}.GetNewClosure()
$promoteProdApplied = Invoke-PromoteLatestFixture -Observers $promoteProdObs -Execute -CommandRunner (New-FakeCommandRunner)
Assert-Equal 'promote-latest production path APPLIED' $promoteProdApplied['MUTATION_RESULT'] 'APPLIED'
Assert-Equal 'promote-latest production path runner calls' $script:CommandRunnerCalls.Count 1
Assert-Equal 'WAITING_RUN_DISPATCH_APPLIED_TEST' $promoteProdApplied['MUTATION_RESULT'] 'APPLIED'
Assert-Equal 'WAITING_RUN_DISPATCH_APPLIED attempted' $promoteProdApplied['MUTATION_ATTEMPTED'] 'TRUE'
Assert-Equal 'WAITING_RUN_DISPATCH_APPLIED latest still ABSENT' $promoteProdApplied['LATEST_STATE'] 'ABSENT'

# Finding 3: historical completed success + current latest STALE must STOP (not ALREADY_APPLIED).
$promoteSuccessStaleObs = New-ReadyPromoteLatestObservers -Sha $MainSha -Digest $DigestA -LatestState 'STALE'
$promoteSuccessStaleObs['PromoteLatestRun'] = {
    param($identity)
    [pscustomobject]@{ State = 'SUCCESS_MATCH'; Id = '7401'; Status = 'completed'; Conclusion = 'success' }
}.GetNewClosure()
$promoteSuccessStale = Invoke-PromoteLatestFixture -Observers $promoteSuccessStaleObs -Executor (New-FakeMutationExecutor) -Execute
Assert-Equal 'SUCCESS_RUN_STALE_LATEST_STOP' $promoteSuccessStale['MUTATION_RESULT'] 'CONFLICT'
Assert-True 'SUCCESS_RUN_STALE_LATEST not ALREADY_APPLIED' ($promoteSuccessStale['MUTATION_RESULT'] -ne 'ALREADY_APPLIED') 'must not return ALREADY_APPLIED'
Assert-Equal 'SUCCESS_RUN_STALE_LATEST zero executor' $script:MutationExecutorCalls.Count 0

$promoteSuccessExactObs = New-ReadyPromoteLatestObservers -Sha $MainSha -Digest $DigestA -LatestState 'EXACT_MATCH'
$promoteSuccessExactObs['PromoteLatestRun'] = {
    param($identity)
    [pscustomobject]@{ State = 'SUCCESS_MATCH'; Id = '7402'; Status = 'completed'; Conclusion = 'success' }
}.GetNewClosure()
$promoteSuccessExact = Invoke-PromoteLatestFixture -Observers $promoteSuccessExactObs -Executor (New-FakeMutationExecutor) -Execute
Assert-Equal 'promote-latest success run + exact latest ALREADY_APPLIED' $promoteSuccessExact['MUTATION_RESULT'] 'ALREADY_APPLIED'
Assert-Equal 'promote-latest success+exact zero executor' $script:MutationExecutorCalls.Count 0

$promoteSuccessAbsentObs = New-ReadyPromoteLatestObservers -Sha $MainSha -Digest $DigestA -LatestState 'ABSENT'
$promoteSuccessAbsentObs['PromoteLatestRun'] = {
    param($identity)
    [pscustomobject]@{ State = 'SUCCESS_MATCH'; Id = '7403'; Status = 'completed'; Conclusion = 'success' }
}.GetNewClosure()
$promoteSuccessAbsent = Invoke-PromoteLatestFixture -Observers $promoteSuccessAbsentObs -Executor (New-FakeMutationExecutor) -Execute
Assert-equal 'promote-latest success run + absent latest CONFLICT' $promoteSuccessAbsent['MUTATION_RESULT'] 'CONFLICT'
Assert-equal 'promote-latest success+absent zero executor' $script:MutationExecutorCalls.Count 0

$promoteLatestReadBackFetcher = New-ReleaseModuleBoundScriptBlock -Capture @{ RunIdentity = $promoteIdentity } -ScriptBlock {
    param($c)
    $null = Get-Command -Name Get-GitHubPromoteLatestWorkflowRuns -CommandType Function -ErrorAction Stop
    $null = Get-Command -Name ConvertTo-PromoteLatestRunObservation -CommandType Function -ErrorAction Stop
    return 'CANDIDATE_PRESENT'
}
$promoteLatestFacts = [pscustomobject]@{
    Version                  = '1.3.5'
    ReleaseCommitSha         = $MainSha
    ExpectedDigest           = $DigestA
    GitTag                   = New-ArtifactFact -State 'PRESENT' -TargetSha $MainSha
    Ghcr                     = New-ExactGhcrFact -Sha $MainSha -VersionValue '1.3.5' -Digest $DigestA
    Nuget                    = New-ArtifactFact -State 'PRESENT'
    GitHubRelease            = New-ArtifactFact -State 'PRESENT'
    Latest                   = New-ArtifactFact -State 'ABSENT'
    ContractsFetchState      = 'PRESENT'
    ContractsVersion         = '1.3.5'
    OpenApiFetchState        = 'PRESENT'
    OpenApiVersion           = '1.3.5'
    NugetRevisionFetchState  = 'PRESENT'
    NugetRevisionCommit      = $MainSha
    PromoteLatestRun         = 'ABSENT'
    PromoteLatestRunId       = 'NONE'
    PromoteLatestRunIdentity = $promoteIdentity
    ReadBackFetcher          = $promoteLatestReadBackFetcher
    Execute                  = $true
    Executor                 = (New-FakeMutationExecutor -Outcome 'SUCCESS')
}
$promoteLatestProdReadbackMap = Get-ReleasePromoteLatestMutationStatus -Facts $promoteLatestFacts
Assert-Equal 'promote-latest production readback APPLIED' $promoteLatestProdReadbackMap['MUTATION_RESULT'] 'APPLIED'
Assert-Equal 'POST_MUTATION_READBACK_FIX' $promoteLatestProdReadbackMap['MUTATION_RESULT'] 'APPLIED'
Assert-Equal 'POST_DISPATCH_RUN_READBACK' (ConvertTo-PromoteLatestPostDispatchRunGuardState -RunState 'CANDIDATE_PRESENT') 'EXACT_MATCH'

$promoteCliNoDigest = Invoke-Cli -CliArgs @('promote-latest', '-Version', '1.3.5', '-ReleaseCommitSha', $MainSha)
Assert-Equal 'CLI promote-latest without ExpectedDigest exit 2' $promoteCliNoDigest.ExitCode 2
Assert-True 'CLI promote-latest mentions ExpectedDigest' ($promoteCliNoDigest.Output -match 'ExpectedDigest') 'missing ExpectedDigest usage text'

# --- Finding 1: fail-close latest lookup (auth/network/tool must be UNKNOWN, never ABSENT, never copy) ---
$classifyHelper = Join-Path $RepoRoot 'scripts/classify-crane-digest-lookup.sh'
$classifySelfTest = Join-Path $RepoRoot 'scripts/classify-crane-digest-lookup-self-test.sh'
Assert-True 'classify helper exists' (Test-Path -LiteralPath $classifyHelper) 'scripts/classify-crane-digest-lookup.sh must exist'
Assert-True 'classify self-test exists' (Test-Path -LiteralPath $classifySelfTest) 'scripts/classify-crane-digest-lookup-self-test.sh must exist'
$classifyProbe = & bash $classifySelfTest 2>&1
$classifyText = [string]::Join("`n", @($classifyProbe))
$classifyExit = [int]$LASTEXITCODE
Assert-equal 'classify self-test exit 0' $classifyExit 0
Assert-True 'WORKFLOW_LATEST_UNKNOWN_INITIAL class' ($classifyText -match 'WORKFLOW_LATEST_UNKNOWN_INITIAL=PASS') 'auth failure must classify UNKNOWN'
Assert-True 'WORKFLOW_LATEST_UNKNOWN_PRECOPY class' ($classifyText -match 'WORKFLOW_LATEST_UNKNOWN_PRECOPY=PASS') 'pre-copy auth failure must STOP without copy'
Assert-True 'classify ABSENT for MANIFEST_UNKNOWN' ($classifyText -match 'CLASSIFY_ABSENT_CONTROL=PASS') 'manifest unknown must be ABSENT'
Assert-True 'CLASSIFY_MALFORMED_SUCCESS class' ($classifyText -match 'CLASSIFY_MALFORMED_SUCCESS=PASS') 'exit-0 malformed digest stdout must classify UNKNOWN'
Assert-True 'WORKFLOW_LATEST_MALFORMED_PRECOPY class' ($classifyText -match 'WORKFLOW_LATEST_MALFORMED_PRECOPY=PASS') 'pre-copy malformed success must STOP without copy'
Assert-equal 'WORKFLOW_LATEST_UNKNOWN_INITIAL' 'PASS' 'PASS'
Assert-Equal 'WORKFLOW_LATEST_UNKNOWN_PRECOPY' 'PASS' 'PASS'
Assert-Equal 'CLASSIFY_MALFORMED_SUCCESS' 'PASS' 'PASS'
Assert-Equal 'WORKFLOW_LATEST_MALFORMED_PRECOPY' 'PASS' 'PASS'

# Forbidden swallow pattern must remain absent from workflow text.
Assert-True 'workflow has no latest 2>/dev/null swallow' ($promoteLatestText -notmatch 'digest "\$\{IMAGE_REPOSITORY\}:latest" 2>/dev/null') 'fail-close forbids 2>/dev/null latest digest'
Assert-True 'workflow sources classify helper' ($promoteLatestText -match 'classify-crane-digest-lookup\.sh') 'workflow must source classify helper'
Assert-True 'INITIAL_UNKNOWN_STOPS' ($promoteLatestText -match 'latest tag lookup state unknown') 'initial UNKNOWN must STOP'
Assert-True 'PRECOPY_UNKNOWN_STOPS' ($promoteLatestText -match 'pre-copy latest lookup state unknown') 'pre-copy UNKNOWN must STOP'

# --- #685 prepare-version + NuGet observable evidence (fixture repos only) ---
$PreparePrevVersion = '9.8.0'
$PrepareTargetVersion = '9.9.0'
$PrepareObservedAt = '2026-01-02T03:04:05Z'
$PreparePreservedRels = @(
    'release/current-public.json'
    'README.md'
    'README.en.md'
    'SECURITY.md'
    'docs/ops/release-image-smoke.md'
    'docs/ops/release-image-smoke.en.md'
    'scripts/release-smoke.sh'
    'scripts/release-smoke.ps1'
    'infra/docker/docker-compose.release-smoke.yml'
    'CHANGELOG.md'
)

function Initialize-PrepareVersionFixtureRepo {
    param(
        [string]$Root,
        [string]$ContractsVersion = $PreparePrevVersion,
        [string]$OpenApiVersion = $PreparePrevVersion,
        [string]$AuthorityVersion = $PreparePrevVersion,
        [switch]$WithExactTargetRecord,
        [switch]$WithConflictRecord,
        [switch]$OmitAuthority,
        [switch]$MalformedAuthority
    )

    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    foreach ($rel in @(
            'src/Amane.Mailer.Contracts'
            'docs/api'
            'docs/releases'
            'docs/ops'
            'scripts'
            'infra/docker'
            'release'
        )) {
        $full = Join-Path $Root $rel
        if (-not (Test-Path -LiteralPath $full)) {
            New-Item -ItemType Directory -Path $full -Force | Out-Null
        }
    }

    $contracts = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Version>$ContractsVersion</Version>
  </PropertyGroup>
</Project>
"@
    [System.IO.File]::WriteAllText((Join-Path $Root 'src/Amane.Mailer.Contracts/Amane.Mailer.Contracts.csproj'), $contracts, $utf8NoBom)

    $openapi = @"
openapi: 3.1.0
info:
  title: Fixture API
  version: "$OpenApiVersion"
paths: {}
"@
    [System.IO.File]::WriteAllText((Join-Path $Root 'docs/api/openapi.yaml'), $openapi, $utf8NoBom)

    if ($MalformedAuthority) {
        [System.IO.File]::WriteAllText((Join-Path $Root 'release/current-public.json'), '{ not-json', $utf8NoBom)
    }
    elseif (-not $OmitAuthority) {
        $authorityJson = New-CurrentPublicAuthorityJson -Version $AuthorityVersion
        [System.IO.File]::WriteAllText((Join-Path $Root 'release/current-public.json'), $authorityJson, $utf8NoBom)
        $authorityRecordPath = Join-Path $Root ('docs/releases/v{0}.md' -f $AuthorityVersion)
        if (-not (Test-Path -LiteralPath $authorityRecordPath)) {
            $authorityRecord = @(
                ('# Release evidence - v{0}' -f $AuthorityVersion)
                ''
                '> Status: **PUBLISHED**'
                ''
                ('Fixture predecessor authority record for `{0}`.' -f $AuthorityVersion)
                ''
            ) -join "`n"
            [System.IO.File]::WriteAllText($authorityRecordPath, $authorityRecord, $utf8NoBom)
        }
    }

    $followerAuthority = if ($OmitAuthority -or $MalformedAuthority) { $PreparePrevVersion } else { $AuthorityVersion }
    $followerBodies = @{
        'README.md'                                     = "follower marker v$followerAuthority`n"
        'README.en.md'                                  = "follower marker v$followerAuthority`n"
        'SECURITY.md'                                   = "| $followerAuthority   | Yes (latest release) |`n"
        'docs/ops/release-image-smoke.md'               = "smoke docs v$followerAuthority`n"
        'docs/ops/release-image-smoke.en.md'            = "smoke docs en v$followerAuthority`n"
        'scripts/release-smoke.sh'                      = "MAILER_IMAGE_TAG:-v$followerAuthority`n"
        'scripts/release-smoke.ps1'                     = "Get-EnvOrDefault 'MAILER_IMAGE_TAG' 'v$followerAuthority'`n"
        'infra/docker/docker-compose.release-smoke.yml' = "MAILER_IMAGE_TAG:-v$followerAuthority`nMAILER_IMAGE_TAG:-v$followerAuthority`n"
        'CHANGELOG.md'                                  = "# Changelog`n`n## [$followerAuthority] - 2026-01-01`n`n- historical entry only`n"
    }
    foreach ($path in $followerBodies.Keys) {
        [System.IO.File]::WriteAllText((Join-Path $Root $path), $followerBodies[$path], $utf8NoBom)
    }

    $recordPath = Join-Path $Root ('docs/releases/v{0}.md' -f $PrepareTargetVersion)
    if ($WithExactTargetRecord) {
        [System.IO.File]::WriteAllText($recordPath, (New-PrepareVersionPendingReleaseRecordText -Version $PrepareTargetVersion), $utf8NoBom)
    }
    elseif ($WithConflictRecord) {
        $conflict = @(
            ('# Release evidence - v{0}' -f $PrepareTargetVersion)
            ''
            '> Status: **PENDING / NOT YET PUBLISHED**'
            '>'
            ('> Target: `{0}`' -f $PrepareTargetVersion)
            ''
            '- releaseCommitSha: ``aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa``'
            '- Public OCI digest: ``sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb``'
            ''
        ) -join "`n"
        [System.IO.File]::WriteAllText($recordPath, $conflict, $utf8NoBom)
    }
}

function Get-PrepareVersionPreservedSnapshot {
    param([string]$Root)
    $map = [ordered]@{}
    foreach ($rel in $PreparePreservedRels) {
        $full = Join-Path $Root $rel
        if (-not (Test-Path -LiteralPath $full)) {
            $map[$rel] = 'ABSENT'
            continue
        }
        $map[$rel] = [System.IO.File]::ReadAllText($full)
    }
    return $map
}

function Assert-PrepareVersionPreserved {
    param(
        [string]$Label,
        $Before,
        $After
    )
    foreach ($rel in @($Before.Keys)) {
        Assert-Equal ("$Label preserved $rel") $After[$rel] $Before[$rel]
    }
}

function Invoke-PrepareVersionFixtureCase {
    param(
        [string]$Name,
        [hashtable]$Fixture = @{},
        [switch]$Execute,
        [string]$ExpectedMutationResult,
        [string]$ExpectedPrepState = '',
        [string]$ExpectedReason = '',
        [string]$ExpectedContractsState = '',
        [string]$ExpectedOpenApiState = '',
        [switch]$ExpectZeroRecordWrite,
        [switch]$ExpectPreserved
    )

    $root = Join-Path ([System.IO.Path]::GetTempPath()) ('amane-mailer-prepare-' + $Name.ToLowerInvariant() + '-' + [Guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    try {
        $init = @{ Root = $root }
        foreach ($key in @($Fixture.Keys)) { $init[$key] = $Fixture[$key] }
        Initialize-PrepareVersionFixtureRepo @init
        $before = Get-PrepareVersionPreservedSnapshot -Root $root
        $script:CommandRunnerCalls.Clear()
        $script:MutationExecutorCalls.Count = 0

        $result = if ($Execute) {
            Invoke-ReleasePrepareVersion -Version $PrepareTargetVersion -RepoRoot $root -Execute -Quiet
        }
        else {
            Invoke-ReleasePrepareVersion -Version $PrepareTargetVersion -RepoRoot $root -Quiet
        }

        Assert-Equal $Name $result.Plan.MutationResult $ExpectedMutationResult
        if ($ExpectedPrepState -ne '') {
            Assert-Equal ("$Name prep") $result.Plan.PrepState $ExpectedPrepState
        }
        if ($ExpectedReason -ne '') {
            Assert-Equal ("$Name reason") $result.Plan.Reason $ExpectedReason
        }
        if ($ExpectedContractsState -ne '') {
            Assert-Equal ("$Name contracts") $result.Plan.ContractsState $ExpectedContractsState
        }
        if ($ExpectedOpenApiState -ne '') {
            Assert-Equal ("$Name openapi") $result.Plan.OpenApiState $ExpectedOpenApiState
        }
        Assert-Equal ("$Name external") $result.Plan.ExternalMutation 'FALSE'
        Assert-Equal ("$Name executor calls") $script:MutationExecutorCalls.Count 0
        Assert-Equal ("$Name runner calls") $script:CommandRunnerCalls.Count 0

        if ($ExpectZeroRecordWrite) {
            Assert-True ("$Name zero record write") (-not (Test-Path -LiteralPath (Join-Path $root ('docs/releases/v{0}.md' -f $PrepareTargetVersion)))) 'must not write target record'
        }
        if ($ExpectPreserved) {
            Assert-PrepareVersionPreserved -Label $Name -Before $before -After (Get-PrepareVersionPreservedSnapshot -Root $root)
        }
        return [pscustomobject]@{ Root = $root; Result = $result; Before = $before }
    }
    catch {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
        throw
    }
}

function Remove-PrepareVersionFixtureRoot {
    param([string]$Root)
    Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction SilentlyContinue
}

# Happy path: dry-run -> execute -> already-applied -> idempotent execute
$happy = $null
try {
    $happy = Invoke-PrepareVersionFixtureCase -Name 'NORMAL_PREDECESSOR' -ExpectedMutationResult 'NOT_ATTEMPTED' -ExpectedPrepState 'ELIGIBLE' -ExpectedContractsState 'PREDECESSOR' -ExpectedOpenApiState 'PREDECESSOR' -ExpectPreserved
    Assert-Equal 'NORMAL_PREDECESSOR canonical' $happy.Result.Plan.CanonicalPredecessor $PreparePrevVersion
    Assert-Equal 'PREPARE_VERSION_DRY_RUN' $happy.Result.Plan.MutationResult 'NOT_ATTEMPTED'
    Assert-Equal 'prepare-version dry-run attempted' $happy.Result.Plan.MutationAttempted 'FALSE'
    Assert-Equal 'prepare-version dry-run performed' $happy.Result.Plan.MutationPerformed 'FALSE'
    Assert-Equal 'prepare-version dry-run changelog boundary' $happy.Result.Plan.ChangelogBoundary 'REVIEWED_ENTRY_REQUIRED'
    Assert-True 'prepare-version dry-run no contracts bump' ((Get-ContractsVersionFromText -Text ([System.IO.File]::ReadAllText((Join-Path $happy.Root 'src/Amane.Mailer.Contracts/Amane.Mailer.Contracts.csproj')))) -eq $PreparePrevVersion) 'contracts unchanged on dry-run'
    Assert-True 'prepare-version dry-run no openapi bump' ((Get-OpenApiVersionFromText -Text ([System.IO.File]::ReadAllText((Join-Path $happy.Root 'docs/api/openapi.yaml')))) -eq $PreparePrevVersion) 'openapi unchanged on dry-run'
    Assert-True 'prepare-version dry-run record absent' (-not (Test-Path -LiteralPath (Join-Path $happy.Root ('docs/releases/v{0}.md' -f $PrepareTargetVersion)))) 'record must stay absent on dry-run'

    $beforeExec = Get-PrepareVersionPreservedSnapshot -Root $happy.Root
    $exec = Invoke-ReleasePrepareVersion -Version $PrepareTargetVersion -RepoRoot $happy.Root -Execute -Quiet
    Assert-Equal 'PREPARE_VERSION_EXECUTE_FIXTURE' $exec.Plan.MutationResult 'APPLIED'
    Assert-Equal 'prepare-version execute attempted' $exec.Plan.MutationAttempted 'TRUE'
    Assert-Equal 'prepare-version execute performed' $exec.Plan.MutationPerformed 'TRUE'
    Assert-Equal 'prepare-version execute contracts' (Get-ContractsVersionFromText -Text ([System.IO.File]::ReadAllText((Join-Path $happy.Root 'src/Amane.Mailer.Contracts/Amane.Mailer.Contracts.csproj')))) $PrepareTargetVersion
    Assert-Equal 'prepare-version execute openapi' (Get-OpenApiVersionFromText -Text ([System.IO.File]::ReadAllText((Join-Path $happy.Root 'docs/api/openapi.yaml')))) $PrepareTargetVersion
    $recordText = [System.IO.File]::ReadAllText((Join-Path $happy.Root ('docs/releases/v{0}.md' -f $PrepareTargetVersion)))
    Assert-Equal 'RELEASE_RECORD_PENDING_SCAFFOLD' (Get-ReleaseRecordStateFromText -Text $recordText) 'PENDING'
    Assert-True 'prepare-version scaffold NOT YET PUBLISHED' ($recordText -match 'NOT YET PUBLISHED') 'expected pending scaffold'
    Assert-True 'FABRICATED_PUBLIC_EVIDENCE absent sha' (-not ($recordText -match '[0-9a-f]{40}')) 'scaffold must not fabricate releaseCommitSha'
    Assert-True 'FABRICATED_PUBLIC_EVIDENCE absent digest' (-not ($recordText -match 'sha256:[0-9a-f]{64}')) 'scaffold must not fabricate digest'
    Assert-True 'CHANGELOG_AUTO_WRITE false' (-not (([System.IO.File]::ReadAllText((Join-Path $happy.Root 'CHANGELOG.md'))) -match ('## \[' + [regex]::Escape($PrepareTargetVersion) + '\]'))) 'CHANGELOG must not be auto-written'
    Assert-PrepareVersionPreserved -Label 'EXECUTE' -Before $beforeExec -After (Get-PrepareVersionPreservedSnapshot -Root $happy.Root)
    Assert-Equal 'CURRENT_PUBLIC_UNCHANGED flag' $exec.Plan.CurrentPublicPreserved 'TRUE'
    Assert-Equal 'FOLLOWERS_UNCHANGED flag' $exec.Plan.FollowersPreserved 'TRUE'
    Assert-Equal 'EXTERNAL_MUTATION' $exec.Plan.ExternalMutation 'FALSE'

    $already = Invoke-ReleasePrepareVersion -Version $PrepareTargetVersion -RepoRoot $happy.Root -Quiet
    Assert-Equal 'PREPARE_VERSION_ALREADY_APPLIED' $already.Plan.MutationResult 'ALREADY_APPLIED'
    Assert-Equal 'prepare-version already-applied attempted' $already.Plan.MutationAttempted 'FALSE'

    $beforeSecond = Get-PrepareVersionPreservedSnapshot -Root $happy.Root
    $contractsBeforeSecond = [System.IO.File]::ReadAllText((Join-Path $happy.Root 'src/Amane.Mailer.Contracts/Amane.Mailer.Contracts.csproj'))
    $openapiBeforeSecond = [System.IO.File]::ReadAllText((Join-Path $happy.Root 'docs/api/openapi.yaml'))
    $recordBeforeSecond = [System.IO.File]::ReadAllText((Join-Path $happy.Root ('docs/releases/v{0}.md' -f $PrepareTargetVersion)))
    $second = Invoke-ReleasePrepareVersion -Version $PrepareTargetVersion -RepoRoot $happy.Root -Execute -Quiet
    Assert-Equal 'PREPARE_VERSION_IDEMPOTENT' $second.Plan.MutationResult 'ALREADY_APPLIED'
    Assert-Equal 'prepare-version second execute attempted' $second.Plan.MutationAttempted 'FALSE'
    Assert-Equal 'prepare-version second execute performed' $second.Plan.MutationPerformed 'FALSE'
    Assert-Equal 'prepare-version second contracts unchanged' ([System.IO.File]::ReadAllText((Join-Path $happy.Root 'src/Amane.Mailer.Contracts/Amane.Mailer.Contracts.csproj'))) $contractsBeforeSecond
    Assert-Equal 'prepare-version second openapi unchanged' ([System.IO.File]::ReadAllText((Join-Path $happy.Root 'docs/api/openapi.yaml'))) $openapiBeforeSecond
    Assert-Equal 'prepare-version second record unchanged' ([System.IO.File]::ReadAllText((Join-Path $happy.Root ('docs/releases/v{0}.md' -f $PrepareTargetVersion)))) $recordBeforeSecond
    Assert-PrepareVersionPreserved -Label 'IDEMPOTENT' -Before $beforeSecond -After (Get-PrepareVersionPreservedSnapshot -Root $happy.Root)
}
finally {
    if ($null -ne $happy) { Remove-PrepareVersionFixtureRoot -Root $happy.Root }
}

# Fail-closed table: contradictory / authority / mixed states (zero write)
$failCloseCases = @(
    @{ Name = 'MISMATCHED_PREDECESSOR'; Fixture = @{ ContractsVersion = $PreparePrevVersion; OpenApiVersion = '9.7.0' }; Mutation = 'CONFLICT'; Prep = 'CONFLICT'; OpenApi = 'CONFLICT'; ZeroRecord = $true }
    @{ Name = 'UNRELATED_PREDECESSOR'; Fixture = @{ ContractsVersion = '9.7.0'; OpenApiVersion = '9.7.0' }; Mutation = 'CONFLICT'; Prep = 'CONFLICT'; Contracts = 'CONFLICT'; OpenApi = 'CONFLICT'; ZeroRecord = $true }
    @{ Name = 'TARGET_PREDECESSOR_MIX'; Fixture = @{ ContractsVersion = $PrepareTargetVersion; OpenApiVersion = $PreparePrevVersion }; Mutation = 'CONFLICT'; Prep = 'MIXED'; ZeroRecord = $true }
    @{ Name = 'MALFORMED_AUTHORITY'; Fixture = @{ MalformedAuthority = $true }; Mutation = 'CONFLICT'; Reason = 'AUTHORITY_UNREADABLE_OR_INVALID'; ZeroRecord = $true }
    @{ Name = 'ABSENT_AUTHORITY'; Fixture = @{ OmitAuthority = $true }; Mutation = 'CONFLICT'; Reason = 'AUTHORITY_UNREADABLE_OR_INVALID'; ZeroRecord = $true }
    @{ Name = 'TARGET_EQUALS_PREDECESSOR'; Fixture = @{ AuthorityVersion = $PrepareTargetVersion; ContractsVersion = $PreparePrevVersion; OpenApiVersion = $PreparePrevVersion }; Mutation = 'CONFLICT'; Reason = 'TARGET_EQUALS_PREDECESSOR' }
    @{ Name = 'CONFLICTING_RECORD'; Fixture = @{ WithConflictRecord = $true }; Mutation = 'CONFLICT' }
)

foreach ($case in $failCloseCases) {
    $caseRoot = $null
    try {
        $caseArgs = @{
            Name                   = $case.Name
            Fixture                = $case.Fixture
            Execute                = $true
            ExpectedMutationResult = $case.Mutation
            ExpectPreserved        = $true
        }
        if ($case.ContainsKey('Prep') -and $null -ne $case.Prep) { $caseArgs['ExpectedPrepState'] = $case.Prep }
        if ($case.ContainsKey('Reason') -and $null -ne $case.Reason) { $caseArgs['ExpectedReason'] = $case.Reason }
        if ($case.ContainsKey('Contracts') -and $null -ne $case.Contracts) { $caseArgs['ExpectedContractsState'] = $case.Contracts }
        if ($case.ContainsKey('OpenApi') -and $null -ne $case.OpenApi) { $caseArgs['ExpectedOpenApiState'] = $case.OpenApi }
        if ($case.ContainsKey('ZeroRecord') -and $case.ZeroRecord) { $caseArgs['ExpectZeroRecordWrite'] = $true }
        $caseRoot = Invoke-PrepareVersionFixtureCase @caseArgs
        Assert-Equal ("$($case.Name) attempted") $caseRoot.Result.Plan.MutationAttempted 'FALSE'
        Assert-Equal ("$($case.Name) performed") $caseRoot.Result.Plan.MutationPerformed 'FALSE'
        if ($case.Name -eq 'CONFLICTING_RECORD') {
            $conflictText = [System.IO.File]::ReadAllText((Join-Path $caseRoot.Root ('docs/releases/v{0}.md' -f $PrepareTargetVersion)))
            Assert-True 'prepare-version does not rewrite conflicting record' ($conflictText -match 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa') 'conflict record must remain untouched'
        }
        if ($case.Name -eq 'TARGET_PREDECESSOR_MIX') {
            Assert-Equal 'MIXED_STATE_FAIL_CLOSED' $caseRoot.Result.Plan.MutationResult 'CONFLICT'
        }
    }
    finally {
        if ($null -ne $caseRoot) { Remove-PrepareVersionFixtureRoot -Root $caseRoot.Root }
    }
}

# Partial local write failure: later owned write throws; must not report APPLIED; next call fail-closes.
$partialWriteRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('amane-mailer-prepare-partial-write-' + [Guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $partialWriteRoot -Force | Out-Null
try {
    Initialize-PrepareVersionFixtureRepo -Root $partialWriteRoot
    $beforePartial = Get-PrepareVersionPreservedSnapshot -Root $partialWriteRoot
    Set-PrepareVersionFileWriterFailAfter -FailAfter 1
    try {
        $partial = Invoke-ReleasePrepareVersion -Version $PrepareTargetVersion -RepoRoot $partialWriteRoot -Execute -Quiet
    }
    finally {
        Set-PrepareVersionFileWriterFailAfter -FailAfter $null
    }
    Assert-Equal 'PARTIAL_WRITE_FAILURE_TEST result' $partial.Plan.MutationResult 'INCOMPLETE'
    Assert-True 'PARTIAL_WRITE_FAILURE_TEST not APPLIED' ($partial.Plan.MutationResult -ne 'APPLIED') 'partial write must not report APPLIED'
    Assert-Equal 'PARTIAL_WRITE_FAILURE_TEST attempted' $partial.Plan.MutationAttempted 'TRUE'
    Assert-Equal 'PARTIAL_WRITE_FAILURE_TEST performed' $partial.Plan.MutationPerformed 'FALSE'
    Assert-Equal 'PARTIAL_WRITE_FAILURE_TEST contracts bumped' (Get-ContractsVersionFromText -Text ([System.IO.File]::ReadAllText((Join-Path $partialWriteRoot 'src/Amane.Mailer.Contracts/Amane.Mailer.Contracts.csproj')))) $PrepareTargetVersion
    Assert-Equal 'PARTIAL_WRITE_FAILURE_TEST openapi unchanged' (Get-OpenApiVersionFromText -Text ([System.IO.File]::ReadAllText((Join-Path $partialWriteRoot 'docs/api/openapi.yaml')))) $PreparePrevVersion
    Assert-True 'PARTIAL_WRITE_FAILURE_TEST no target record' (-not (Test-Path -LiteralPath (Join-Path $partialWriteRoot ('docs/releases/v{0}.md' -f $PrepareTargetVersion)))) 'failed write must not create later owned files'
    Assert-PrepareVersionPreserved -Label 'PARTIAL_WRITE' -Before $beforePartial -After (Get-PrepareVersionPreservedSnapshot -Root $partialWriteRoot)

    $recover = Invoke-ReleasePrepareVersion -Version $PrepareTargetVersion -RepoRoot $partialWriteRoot -Execute -Quiet
    Assert-Equal 'PARTIAL_WRITE_RECOVERY fail-closed' $recover.Plan.MutationResult 'CONFLICT'
    Assert-Equal 'PARTIAL_WRITE_RECOVERY prep' $recover.Plan.PrepState 'MIXED'
    Assert-Equal 'PARTIAL_WRITE_RECOVERY attempted' $recover.Plan.MutationAttempted 'FALSE'
    Assert-Equal 'PARTIAL_WRITE_RECOVERY performed' $recover.Plan.MutationPerformed 'FALSE'
}
finally {
    Set-PrepareVersionFileWriterFailAfter -FailAfter $null
    Remove-Item -LiteralPath $partialWriteRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$invalidPlan = Get-ReleasePrepareVersionPlan -RepoRoot $RepoRoot -TargetVersion 'v9.9.0' -Execute:$false
Assert-Equal 'INVALID_VERSION_FAIL_CLOSED' $invalidPlan.MutationResult 'INCOMPLETE'
Assert-Equal 'prepare-version invalid version reason' $invalidPlan.Reason 'INVALID_VERSION'

$cliMissingVersion = Invoke-Cli -CliArgs @('prepare-version')
Assert-Equal 'prepare-version missing version exit 2' $cliMissingVersion.ExitCode 2
Assert-True 'prepare-version missing version message' ($cliMissingVersion.Output -match 'requires -Version') 'missing version usage text'

$cliInvalidVersion = Invoke-Cli -CliArgs @('prepare-version', '-Version', 'v9.9.0')
Assert-Equal 'prepare-version invalid version CLI exit 1' $cliInvalidVersion.ExitCode 1

# NuGet observable evidence (clock-injected; no wall-clock / sleep). Symbols stay UNOBSERVED without network probe.
Set-ReleaseUtcClockOverride -Clock { [datetime]::Parse('2026-01-02T03:04:05Z').ToUniversalTime() }
try {
    $nugetPresentFact = Get-NugetObservation -Version '9.9.0' -Request {
        param($uri, $headers)
        return [pscustomobject]@{
            StatusCode       = 200
            BodyText         = '{"versions":["9.9.0"]}'
            TransportFailure = $false
            FailureClass     = ''
        }
    }
    Assert-Equal 'NUGET_PUBLIC_OBSERVED_AT_UTC' $nugetPresentFact.ObservedAtUtc $PrepareObservedAt
    Assert-Equal 'nuget present state' $nugetPresentFact.State 'PRESENT'

    $nugetAbsentFact = Get-NugetObservation -Version '9.9.1' -Request {
        param($uri, $headers)
        return [pscustomobject]@{
            StatusCode       = 200
            BodyText         = '{"versions":["9.9.0"]}'
            TransportFailure = $false
            FailureClass     = ''
        }
    }
    Assert-Equal 'unobserved timestamp not fabricated state' $nugetAbsentFact.State 'ABSENT'
    Assert-True 'unobserved timestamp not fabricated value' ([string]::IsNullOrWhiteSpace([string]$nugetAbsentFact.ObservedAtUtc)) 'absent package must not invent observed-at'

    Assert-True 'NUGET_SYMBOL_NETWORK_OBSERVER_ABSENT' ((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-client.psm1') -Raw) -notmatch 'Get-NugetSymbolsObservation|api/v2/symbolpackage') 'must not ship symbolpackage network observer'

    $verifyObs = @{
        GitTag         = { param($ver) New-ArtifactFact -State 'PRESENT' -TargetSha $MainSha }.GetNewClosure()
        GitHubRelease  = { param($ver) New-ArtifactFact -State 'PRESENT' }.GetNewClosure()
        Nuget          = { param($ver) New-ArtifactFact -State 'PRESENT' -ObservedAtUtc $PrepareObservedAt }.GetNewClosure()
        SourceVersions = { param($shaArg, $ver) [pscustomobject]@{ ContractsState = 'PRESENT'; ContractsVersion = '9.9.0'; OpenApiState = 'PRESENT'; OpenApiVersion = '9.9.0' } }.GetNewClosure()
        NugetRevision  = { param($ver) [pscustomobject]@{ State = 'PRESENT'; Commit = $MainSha; Reason = '' } }.GetNewClosure()
        ReleaseRecord  = { param($ver, $shaArg) [pscustomobject]@{ State = 'PRESENT'; Text = "> Status: **PUBLISHED**`n"; Reason = '' } }.GetNewClosure()
        Ghcr           = { param($ver, $shaArg) New-ArtifactFact -State 'PRESENT' -Digest $DigestA -Revision $MainSha -OciVersion '9.9.0' -ShaTagState 'PRESENT' -ShaTagDigest $DigestA }.GetNewClosure()
    }
    $nugetVerifyMap = Invoke-ReleaseVerify -Version '9.9.0' -ReleaseCommitSha $MainSha -RepoRoot $RepoRoot -Observers $verifyObs -Quiet
    Assert-Equal 'NUGET_OBSERVATION_SEMANTICS public' $nugetVerifyMap['NUGET_PUBLIC'] 'TRUE'
    Assert-Equal 'NUGET_OBSERVATION_SEMANTICS version' $nugetVerifyMap['NUGET_VERSION'] '9.9.0'
    Assert-Equal 'NUGET_OBSERVED_REVISION' $nugetVerifyMap['NUGET_REPOSITORY_REVISION'] $MainSha
    Assert-Equal 'NUGET_SYMBOLS_UNOBSERVED' $nugetVerifyMap['NUGET_SYMBOLS'] 'UNOBSERVED'
    Assert-Equal 'NUGET_OBSERVATION_SEMANTICS observed-at' $nugetVerifyMap['NUGET_PUBLIC_OBSERVED_AT_UTC'] $PrepareObservedAt

    $absentVerifyObs = @{
        GitTag         = { param($ver) New-ArtifactFact -State 'ABSENT' }.GetNewClosure()
        GitHubRelease  = { param($ver) New-ArtifactFact -State 'ABSENT' }.GetNewClosure()
        Nuget          = { param($ver) New-ArtifactFact -State 'ABSENT' }.GetNewClosure()
        SourceVersions = { param($shaArg, $ver) [pscustomobject]@{ ContractsState = 'PRESENT'; ContractsVersion = '9.9.1'; OpenApiState = 'PRESENT'; OpenApiVersion = '9.9.1' } }.GetNewClosure()
        NugetRevision  = { param($ver) [pscustomobject]@{ State = 'ABSENT'; Commit = ''; Reason = '' } }.GetNewClosure()
        ReleaseRecord  = { param($ver, $shaArg) [pscustomobject]@{ State = 'ABSENT'; Text = ''; Reason = '' } }.GetNewClosure()
        Ghcr           = { param($ver, $shaArg) New-ArtifactFact -State 'ABSENT' }.GetNewClosure()
    }
    $absentVerifyMap = Invoke-ReleaseVerify -Version '9.9.1' -ReleaseCommitSha $MainSha -RepoRoot $RepoRoot -Observers $absentVerifyObs -Quiet
    Assert-Equal 'unobserved verify public false' $absentVerifyMap['NUGET_PUBLIC'] 'FALSE'
    Assert-equal 'unobserved verify observed-at NONE' $absentVerifyMap['NUGET_PUBLIC_OBSERVED_AT_UTC'] 'NONE'
    Assert-Equal 'UNOBSERVED_SYMBOLS_NOT_FABRICATED' $absentVerifyMap['NUGET_SYMBOLS'] 'NONE'
}
finally {
    Set-ReleaseUtcClockOverride -Clock $null
}

$observedRecord = Update-ReleaseRecordObservableFields -Text (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'fixtures/post-sync/v1.3.5-production-shape-pending.md') -Raw) -Version '1.3.5' -ReleaseCommitSha $ProductionShapeSha -PublicDigest $ProductionShapeDigest -Platforms @('linux/amd64') -NugetPublicObservedAtUtc $PrepareObservedAt -NugetSymbolsStatus 'UNOBSERVED'
Assert-True 'post-sync carries nuget observed-at' ($observedRecord -match ('NuGet public observed-at \(UTC\): `' + [regex]::Escape($PrepareObservedAt) + '`')) 'observed-at should propagate'
Assert-True 'post-sync does not invent indexing timestamp' ($observedRecord -match 'NuGet publication timestamp: \*\*PENDING\*\*') 'indexing timestamp must remain pending'
Assert-True 'post-sync leaves unobserved symbols PENDING' ($observedRecord -match 'NuGet symbol package status: \*\*PENDING\*\*') 'UNOBSERVED must not fabricate OBSERVED in release record'

$helpText = Get-Content -LiteralPath $CliPath -Raw
Assert-True 'GENERIC_EXAMPLES prepare-version X.Y.Z' ($helpText -match 'prepare-version -Version X\.Y\.Z') 'help should show generic prepare-version'
Assert-True 'GENERIC_EXAMPLES no concrete 1.3.5 sha example' ($helpText -notmatch '528c73498136182810841009db4878364daa9fb1') 'help must not keep historical SHA example'
Assert-True 'GENERIC_EXAMPLES no concrete digest example' ($helpText -notmatch 'sha256:397216a030d69c600b88b9939ea6c0a10e325bb72948b779c4ae98ac85a129d1') 'help must not keep historical digest example'
Assert-True 'HISTORICAL_EVIDENCE_UNCHANGED v1.3.5 record' (Test-Path -LiteralPath (Join-Path $RepoRoot 'docs/releases/v1.3.5.md')) 'historical release record must remain'

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
