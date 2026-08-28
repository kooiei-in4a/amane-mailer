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
    [System.IO.File]::WriteAllText((Join-Path $Root 'release/current-public.json'), $authorityJson)

    Copy-Item -LiteralPath (Join-Path $fixturesRoot 'README.md') -Destination (Join-Path $Root 'README.md') -Force
    Copy-Item -LiteralPath (Join-Path $fixturesRoot 'README.en.md') -Destination (Join-Path $Root 'README.en.md') -Force
    Copy-Item -LiteralPath (Join-Path $fixturesRoot 'SECURITY.md') -Destination (Join-Path $Root 'SECURITY.md') -Force
    Copy-Item -LiteralPath (Join-Path $fixturesRoot 'release-image-smoke.md') -Destination (Join-Path $Root 'docs/ops/release-image-smoke.md') -Force
    Copy-Item -LiteralPath (Join-Path $fixturesRoot 'release-image-smoke.en.md') -Destination (Join-Path $Root 'docs/ops/release-image-smoke.en.md') -Force
    Copy-Item -LiteralPath (Join-Path $fixturesRoot 'release-smoke.sh') -Destination (Join-Path $Root 'scripts/release-smoke.sh') -Force
    Copy-Item -LiteralPath (Join-Path $fixturesRoot 'release-smoke.ps1') -Destination (Join-Path $Root 'scripts/release-smoke.ps1') -Force
    Copy-Item -LiteralPath (Join-Path $fixturesRoot 'docker-compose.release-smoke.yml') -Destination (Join-Path $Root 'infra/docker/docker-compose.release-smoke.yml') -Force
    Copy-Item -LiteralPath (Join-Path $RepoRoot 'docs/releases/v1.3.4.md') -Destination (Join-Path $Root 'docs/releases/v1.3.4.md') -Force

    $pending135 = @"
# Release evidence - v1.3.5

> Status: **RELEASE PREPARATION - NOT YET PUBLISHED**
>
> Version: ``1.3.5``

## Release identity

- release version: ``1.3.5``
- releaseCommitSha: **PENDING**
"@
    [System.IO.File]::WriteAllText((Join-Path $Root 'docs/releases/v1.3.5.md'), $pending135)

    if ($SynchronizedTo135) {
        $applyRules = Get-PostSyncFollowerReplacementRules -PrevVersion '1.3.4' -TargetVersion '1.3.5'
        foreach ($path in @('README.md', 'README.en.md', 'SECURITY.md', 'docs/ops/release-image-smoke.md', 'docs/ops/release-image-smoke.en.md', 'scripts/release-smoke.sh', 'scripts/release-smoke.ps1', 'infra/docker/docker-compose.release-smoke.yml')) {
            $full = Join-Path $Root $path
            $content = [System.IO.File]::ReadAllText($full)
            $pathRules = Get-PostSyncRulesForPath -RelativePath $path -AllRules $applyRules
            $updated = Apply-PostSyncReplacementRules -Content $content -Rules $pathRules
            [System.IO.File]::WriteAllText($full, $updated)
        }
        $published = Build-PublishedReleaseRecordForPostSync -Text $pending135 -Version '1.3.5' -ReleaseCommitSha $PostSyncSha135 -PublicDigest $PostSyncDigest135 -Platforms @('linux/amd64')
        [System.IO.File]::WriteAllText((Join-Path $Root 'docs/releases/v1.3.5.md'), $published.Text)
    }
}

$authorityGood = ConvertFrom-CurrentPublicAuthorityText -Text (Get-Content -LiteralPath (Join-Path $RepoRoot 'release/current-public.json') -Raw) -RepoRoot $RepoRoot
Assert-Equal 'authority v1.3.4 parse state' $authorityGood.State 'VALID'
Assert-Equal 'authority v1.3.4 version' $authorityGood.Version '1.3.4'
Assert-Equal 'authority v1.3.4 tag' $authorityGood.Tag 'v1.3.4'

$authorityMalformed = ConvertFrom-CurrentPublicAuthorityText -Text '{not-json' -RepoRoot $RepoRoot
Assert-Equal 'malformed authority fail closed' $authorityMalformed.State 'INCOMPLETE'
Assert-Equal 'malformed authority reason' $authorityMalformed.Reason 'MALFORMED_JSON'

$authorityBadSchema = ConvertFrom-CurrentPublicAuthorityText -Text '{"schemaVersion":2,"version":"1.3.4","tag":"v1.3.4","platforms":["linux/amd64"],"releaseRecord":"docs/releases/v1.3.4.md"}' -RepoRoot $RepoRoot
Assert-Equal 'unsupported schema fail closed' $authorityBadSchema.Reason 'UNSUPPORTED_SCHEMA'

$authorityTagMismatch = ConvertFrom-CurrentPublicAuthorityText -Text '{"schemaVersion":1,"version":"1.3.4","tag":"v1.3.5","platforms":["linux/amd64"],"releaseRecord":"docs/releases/v1.3.4.md"}' -RepoRoot $RepoRoot
Assert-Equal 'version tag mismatch fail closed' $authorityTagMismatch.Reason 'VERSION_TAG_MISMATCH'

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
}
finally {
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$authorityObsLive = Get-CurrentPublicAuthorityObservation -RepoRoot $RepoRoot
Assert-Equal 'live authority observation present' $authorityObsLive.State 'PRESENT'
Assert-Equal 'live authority version' $authorityObsLive.Authority.Version '1.3.4'

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
