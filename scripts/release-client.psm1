# Canonical read-only release observation / derivation helpers for Issue #664 RO-1.
# This module inspects local git identity and public HTTP APIs only.
# It must not update refs, dispatch workflows, or publish artifacts.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    [Net.ServicePointManager]::SecurityProtocol = `
        [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
}
catch {
    # Some hosts already enable TLS 1.2; keep going.
}

$script:CanonicalOwnerRepo = 'kooiei-in4a/amane-mailer'
$script:GitHubApiRoot = 'https://api.github.com/repos/kooiei-in4a/amane-mailer'
$script:GhcrRepository = 'kooiei-in4a/amane-mailer'
$script:NugetPackageId = 'amane.mailer.contracts'
$script:UserAgent = 'amane-mailer-release-client-ro1'
$script:Sha40 = '^[0-9a-f]{40}$'
$script:Digest64 = '^sha256:[0-9a-f]{64}$'
$script:VersionXyz = '^[0-9]+\.[0-9]+\.[0-9]+$'
$script:ReleaseMutationCommandRunnerOverride = $null
$script:ReleaseUtcClockOverride = $null

function Get-ReleaseUtcNow {
    if ($null -ne $script:ReleaseUtcClockOverride) {
        return & $script:ReleaseUtcClockOverride
    }
    return [datetime]::UtcNow
}

function Format-ReleaseUtcTimestamp {
    param([datetime]$Utc)
    $value = $Utc.ToUniversalTime()
    return $value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
}

function Test-ReleaseUtcTimestamp {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }
    return [bool]($Value -match '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$')
}

function Set-ReleaseUtcClockOverride {
    param($Clock)
    $script:ReleaseUtcClockOverride = $Clock
}

$script:StatusKeys = @(
    'VERSION',
    'SOURCE_SHA',
    'SOURCE_BASIS',
    'STATE',
    'LOCAL_REPO',
    'VERSION_ALIGNMENT',
    'GHCR',
    'GIT_TAG',
    'NUGET',
    'GITHUB_RELEASE',
    'RELEASE_RECORD',
    'NEXT_ACTION',
    'MUTATION_PERFORMED'
)

function Write-ReleaseStderr {
    param([string]$Message)
    [Console]::Error.WriteLine($Message)
}

function ConvertTo-ReleaseText {
    param($Content)
    if ($null -eq $Content) { return '' }
    if ($Content -is [byte[]]) {
        return [System.Text.Encoding]::UTF8.GetString($Content)
    }
    return [string]$Content
}

function Get-ReleaseHeaderValue {
    param(
        $Headers,
        [string]$Name
    )
    if ($null -eq $Headers) { return '' }
    $value = $Headers[$Name]
    if ($null -eq $value) { return '' }
    if ($value -is [System.Array]) {
        if ($value.Length -eq 0) { return '' }
        return [string]$value[0]
    }
    return [string]$value
}

function Test-ReleaseVersion {
    param([string]$Version)
    if ([string]::IsNullOrWhiteSpace($Version)) { return $false }
    return [bool]($Version -match $script:VersionXyz)
}

function Test-ReleaseSha {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }
    return [bool]($Value -match $script:Sha40)
}

function Test-ReleaseDigest {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }
    return [bool]($Value -match $script:Digest64)
}

function Get-OriginRepositoryIdentity {
    param([string]$Url)
    if ([string]::IsNullOrWhiteSpace($Url)) { return $null }
    if ($Url -match 'github\.com[:/]+([^/]+)/([^/]+?)(?:\.git)?/?$') {
        $owner = $Matches[1]
        $repo = $Matches[2].TrimEnd('/')
        return ('{0}/{1}' -f $owner, $repo)
    }
    return $null
}

function ConvertTo-RemotePresence {
    param(
        [int]$StatusCode,
        [bool]$TransportFailure = $false
    )
    if ($TransportFailure -or $StatusCode -le 0) { return 'INCOMPLETE' }
    if ($StatusCode -eq 404) { return 'ABSENT' }
    if ($StatusCode -ge 200 -and $StatusCode -lt 300) { return 'HTTP_OK' }
    return 'INCOMPLETE'
}

function ConvertTo-RemoteFailureClass {
    param(
        [int]$StatusCode,
        [bool]$TransportFailure,
        [string]$FailureClass
    )
    if ($TransportFailure) {
        if ([string]::IsNullOrWhiteSpace($FailureClass)) { return 'NETWORK' }
        return $FailureClass
    }
    if ($StatusCode -eq 401 -or $StatusCode -eq 403) { return 'AUTH' }
    if ($StatusCode -eq 429) { return 'RATE_LIMIT' }
    if ($StatusCode -ge 500) { return 'HTTP_5XX' }
    if ($StatusCode -eq 404) { return 'HTTP_404' }
    if ($StatusCode -gt 0) { return ('HTTP_{0}' -f $StatusCode) }
    return 'TOOL'
}

function New-ArtifactFact {
    param(
        [Parameter(Mandatory = $true)]
        [string]$State,
        [string]$TargetSha = '',
        [string]$Digest = '',
        [string]$Revision = '',
        [string]$OciVersion = '',
        [string]$ShaTagState = '',
        [string]$ShaTagDigest = '',
        [string]$Reason = '',
        [string]$ObservedAtUtc = '',
        [string]$SymbolsState = ''
    )
    return [pscustomobject]@{
        State        = $State
        TargetSha    = $TargetSha
        Digest       = $Digest
        Revision     = $Revision
        OciVersion   = $OciVersion
        ShaTagState  = $ShaTagState
        ShaTagDigest = $ShaTagDigest
        Reason       = $Reason
        ObservedAtUtc = $ObservedAtUtc
        SymbolsState = $SymbolsState
    }
}

# GetNewClosure() creates a dynamic module that cannot resolve this module's
# non-exported functions. Bind the body back into the release-client module so
# production post-readback can call private observers without Export-ModuleMember.
function New-ReleaseModuleBoundScriptBlock {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$ScriptBlock,
        [hashtable]$Capture = @{}
    )
    $module = $MyInvocation.MyCommand.Module
    $bag = @{}
    foreach ($key in @($Capture.Keys)) {
        $bag[$key] = $Capture[$key]
    }
    return {
        & $module $ScriptBlock $bag
    }.GetNewClosure()
}

function New-ReleaseObservations {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version,
        [string]$LocalRepo = 'PASS',
        [string]$LocalBranch = '',
        [string]$LocalHead = '',
        [string]$Worktree = 'CLEAN',
        [string]$OriginIdentity = 'kooiei-in4a/amane-mailer',
        [string]$GitHubMainState = 'PRESENT',
        [string]$GitHubMainSha = '',
        [string]$VersionAlignment = 'PASS',
        [string]$ContractsVersion = '',
        [string]$OpenApiVersion = '',
        [string]$ReleaseRecord = 'ABSENT',
        $GitTag,
        $GitHubRelease,
        $Ghcr,
        $Nuget
    )
    if ($null -eq $GitTag) { $GitTag = New-ArtifactFact -State 'ABSENT' }
    if ($null -eq $GitHubRelease) { $GitHubRelease = New-ArtifactFact -State 'ABSENT' }
    if ($null -eq $Ghcr) { $Ghcr = New-ArtifactFact -State 'ABSENT' }
    if ($null -eq $Nuget) { $Nuget = New-ArtifactFact -State 'ABSENT' }
    return [pscustomobject]@{
        Version           = $Version
        LocalRepo         = $LocalRepo
        LocalBranch       = $LocalBranch
        LocalHead         = $LocalHead
        Worktree          = $Worktree
        OriginIdentity    = $OriginIdentity
        GitHubMainState   = $GitHubMainState
        GitHubMainSha     = $GitHubMainSha
        VersionAlignment  = $VersionAlignment
        ContractsVersion  = $ContractsVersion
        OpenApiVersion    = $OpenApiVersion
        ReleaseRecord     = $ReleaseRecord
        GitTag            = $GitTag
        GitHubRelease     = $GitHubRelease
        Ghcr              = $Ghcr
        Nuget             = $Nuget
    }
}

function Get-ContractsVersionFromText {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    if ($Text -match '<Version>([^<]+)</Version>') {
        return $Matches[1].Trim()
    }
    return $null
}

function Get-OpenApiVersionFromText {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    if ($Text -match '(?m)^info:\r?\n(?:[ \t].*\r?\n)*?[ \t]+version:\s*"([^"]+)"') {
        return $Matches[1].Trim()
    }
    $headLines = $Text -split '\r?\n' | Select-Object -First 20
    $head = [string]::Join("`n", $headLines)
    if ($head -match 'version:\s*"(\d+\.\d+\.\d+)"') {
        return $Matches[1]
    }
    return $null
}

function Get-ReleaseRecordStateFromText {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return 'INCOMPLETE' }
    if ($Text -match '(?m)^>\s*Status:\s*\*\*(.+?)\*\*') {
        $status = $Matches[1]
        if ($status -match 'NOT YET PUBLISHED|PENDING|RELEASE PREPARATION') {
            return 'PENDING'
        }
        if ($status -match 'PUBLISHED') {
            return 'PUBLISHED'
        }
        return 'INCOMPLETE'
    }
    return 'INCOMPLETE'
}

function Get-VersionAlignment {
    param(
        [string]$RequestedVersion,
        [string]$ContractsVersion,
        [string]$OpenApiVersion
    )
    if ([string]::IsNullOrWhiteSpace($ContractsVersion) -or [string]::IsNullOrWhiteSpace($OpenApiVersion)) {
        return 'INCOMPLETE'
    }
    if ($ContractsVersion -eq $RequestedVersion -and $OpenApiVersion -eq $RequestedVersion) {
        return 'PASS'
    }
    return 'FAIL'
}

function Resolve-GitTagTargetFromGitHubJson {
    param(
        [string]$RefJson,
        [string]$TagObjectJson = ''
    )
    if ([string]::IsNullOrWhiteSpace($RefJson)) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; TargetSha = ''; Reason = 'EMPTY_REF' }
    }
    $objectType = ''
    $objectSha = ''
    if ($RefJson -match '"object"\s*:\s*\{[^}]*"type"\s*:\s*"([^"]+)"') {
        $objectType = $Matches[1]
    }
    if ($RefJson -match '"object"\s*:\s*\{[^}]*"sha"\s*:\s*"([0-9a-f]{40})"') {
        $objectSha = $Matches[1]
    }
    if ([string]::IsNullOrWhiteSpace($objectType) -or -not (Test-ReleaseSha $objectSha)) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; TargetSha = ''; Reason = 'REF_PARSE' }
    }
    if ($objectType -eq 'commit') {
        return [pscustomobject]@{ State = 'PRESENT'; TargetSha = $objectSha; Reason = '' }
    }
    if ($objectType -ne 'tag') {
        return [pscustomobject]@{ State = 'CONFLICT'; TargetSha = ''; Reason = 'UNEXPECTED_REF_TYPE' }
    }
    if ([string]::IsNullOrWhiteSpace($TagObjectJson)) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; TargetSha = ''; Reason = 'TAG_OBJECT_MISSING' }
    }
    $peeledType = ''
    $peeledSha = ''
    if ($TagObjectJson -match '"object"\s*:\s*\{[^}]*"type"\s*:\s*"([^"]+)"') {
        $peeledType = $Matches[1]
    }
    if ($TagObjectJson -match '"object"\s*:\s*\{[^}]*"sha"\s*:\s*"([0-9a-f]{40})"') {
        $peeledSha = $Matches[1]
    }
    if ($peeledType -eq 'commit' -and (Test-ReleaseSha $peeledSha)) {
        return [pscustomobject]@{ State = 'PRESENT'; TargetSha = $peeledSha; Reason = '' }
    }
    if (-not [string]::IsNullOrWhiteSpace($peeledType) -and $peeledType -ne 'commit') {
        return [pscustomobject]@{ State = 'CONFLICT'; TargetSha = ''; Reason = 'TAG_OBJECT_NOT_COMMIT' }
    }
    return [pscustomobject]@{ State = 'INCOMPLETE'; TargetSha = ''; Reason = 'TAG_OBJECT_PARSE' }
}

function Test-NugetIndexContainsVersion {
    param(
        [string]$IndexJson,
        [string]$Version
    )
    if ([string]::IsNullOrWhiteSpace($IndexJson)) { return $null }
    try {
        $parsed = $IndexJson | ConvertFrom-Json
    }
    catch {
        return $null
    }
    if ($null -eq $parsed) { return $null }
    $versions = $parsed.versions
    if ($null -eq $versions) { return $null }
    foreach ($entry in @($versions)) {
        if ([string]$entry -eq $Version) { return $true }
    }
    return $false
}

function Get-GhcrConfigDigestFromManifest {
    param([string]$ManifestJson)
    if ([string]::IsNullOrWhiteSpace($ManifestJson)) { return $null }
    if ($ManifestJson -match '"config"\s*:\s*\{[^{}]*"digest"\s*:\s*"(sha256:[0-9a-f]{64})"') {
        return $Matches[1]
    }
    return $null
}

function Get-OciRevisionFromConfigText {
    param([string]$ConfigText)
    if ([string]::IsNullOrWhiteSpace($ConfigText)) { return $null }
    $found = [regex]::Matches($ConfigText, '"org\.opencontainers\.image\.revision"\s*:\s*"([0-9a-f]{40})"')
    if ($found.Count -eq 1) { return $found[0].Groups[1].Value }
    if ($found.Count -gt 1) {
        $unique = @($found | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
        if ($unique.Count -eq 1) { return $unique[0] }
        return $null
    }
    return $null
}

function Get-OciVersionFromConfigText {
    param([string]$ConfigText)
    if ([string]::IsNullOrWhiteSpace($ConfigText)) { return $null }
    $found = [regex]::Matches($ConfigText, '"org\.opencontainers\.image\.version"\s*:\s*"([^"]+)"')
    if ($found.Count -eq 1) { return $found[0].Groups[1].Value.Trim() }
    if ($found.Count -gt 1) {
        $unique = @($found | ForEach-Object { $_.Groups[1].Value.Trim() } | Select-Object -Unique)
        if ($unique.Count -eq 1) { return $unique[0] }
        return $null
    }
    return $null
}

function Get-NugetRepositoryCommitFromNuspecText {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    if ($Text -match '<repository[^>]*\scommit="([0-9a-f]{40})"') {
        return $Matches[1]
    }
    return $null
}

function Get-ReleaseRecordCommitShaFromText {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }

    $values = New-Object System.Collections.Generic.List[string]
    # Production shape: - `releaseCommitSha`: `<40hex>`
    foreach ($match in [regex]::Matches($Text, '(?m)^-\s+`releaseCommitSha`:\s+`([0-9a-f]{40})`\s*$')) {
        [void]$values.Add($match.Groups[1].Value)
    }
    # Legacy: - releaseCommitSha: `<40hex>` (single or double backticks around the value)
    foreach ($match in [regex]::Matches($Text, '(?m)^-\s+releaseCommitSha:\s+`{1,2}([0-9a-f]{40})`{1,2}\s*$')) {
        [void]$values.Add($match.Groups[1].Value)
    }

    $unique = @($values | Select-Object -Unique)
    if ($unique.Count -eq 1) { return $unique[0] }
    # Zero matches or contradictory values: fail closed.
    return $null
}

function Get-ReleaseRecordDigestFromText {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }

    $values = New-Object System.Collections.Generic.List[string]
    # Production-shape one-line: - Public/public OCI digest: `sha256:<64hex>`
    foreach ($match in [regex]::Matches($Text, '(?im)^-\s+public OCI digest:\s+`{1,2}(sha256:[0-9a-f]{64})`{1,2}\s*$')) {
        [void]$values.Add($match.Groups[1].Value)
    }
    # Legacy multiline: - public OCI digest:\n  `sha256:<64hex>`
    foreach ($match in [regex]::Matches($Text, '(?im)^-\s+public OCI digest:\s*\r?\n\s+`{1,2}(sha256:[0-9a-f]{64})`{1,2}')) {
        [void]$values.Add($match.Groups[1].Value)
    }

    $unique = @($values | Select-Object -Unique)
    if ($unique.Count -eq 1) { return $unique[0] }
    # Zero matches or contradictory values: fail closed.
    return $null
}

function Get-ReleaseSourceAuthority {
    param($Observations)

    $tag = $Observations.GitTag
    $ghcr = $Observations.Ghcr
    $nuget = $Observations.Nuget
    $release = $Observations.GitHubRelease

    $tagKnown = ($tag.State -eq 'PRESENT' -and (Test-ReleaseSha $tag.TargetSha))
    $ghcrRevKnown = ($ghcr.State -eq 'PRESENT' -and (Test-ReleaseSha $ghcr.Revision))

    if ($tag.State -eq 'CONFLICT' -or $ghcr.State -eq 'CONFLICT') {
        return [pscustomobject]@{
            Sha        = 'UNKNOWN'
            Basis      = 'UNKNOWN'
            Conflict   = $true
            Incomplete = $false
            Reason     = 'ARTIFACT_CONFLICT'
        }
    }

    if ($tagKnown) {
        if ($ghcrRevKnown -and $ghcr.Revision -ne $tag.TargetSha) {
            return [pscustomobject]@{
                Sha        = 'UNKNOWN'
                Basis      = 'UNKNOWN'
                Conflict   = $true
                Incomplete = $false
                Reason     = 'TAG_GHCR_REVISION_MISMATCH'
            }
        }
        return [pscustomobject]@{
            Sha        = $tag.TargetSha
            Basis      = 'GIT_TAG'
            Conflict   = $false
            Incomplete = $false
            Reason     = ''
        }
    }

    if ($tag.State -eq 'INCOMPLETE') {
        return [pscustomobject]@{
            Sha        = 'UNKNOWN'
            Basis      = 'UNKNOWN'
            Conflict   = $false
            Incomplete = $true
            Reason     = 'GIT_TAG_INCOMPLETE'
        }
    }

    if ($ghcrRevKnown) {
        return [pscustomobject]@{
            Sha        = $ghcr.Revision
            Basis      = 'GHCR'
            Conflict   = $false
            Incomplete = $false
            Reason     = ''
        }
    }

    if ($ghcr.State -eq 'INCOMPLETE') {
        return [pscustomobject]@{
            Sha        = 'UNKNOWN'
            Basis      = 'UNKNOWN'
            Conflict   = $false
            Incomplete = $true
            Reason     = 'GHCR_INCOMPLETE'
        }
    }

    if ($ghcr.State -eq 'PRESENT') {
        return [pscustomobject]@{
            Sha        = 'UNKNOWN'
            Basis      = 'UNKNOWN'
            Conflict   = $false
            Incomplete = $true
            Reason     = 'GHCR_REVISION_NOT_UNIQUE'
        }
    }

    if ($nuget.State -eq 'INCOMPLETE' -or $release.State -eq 'INCOMPLETE') {
        return [pscustomobject]@{
            Sha        = 'UNKNOWN'
            Basis      = 'UNKNOWN'
            Conflict   = $false
            Incomplete = $true
            Reason     = 'PUBLIC_ARTIFACT_INCOMPLETE'
        }
    }

    if ($nuget.State -eq 'PRESENT' -or $release.State -eq 'PRESENT') {
        return [pscustomobject]@{
            Sha        = 'UNKNOWN'
            Basis      = 'UNKNOWN'
            Conflict   = $true
            Incomplete = $false
            Reason     = 'PUBLIC_ARTIFACT_WITHOUT_SOURCE'
        }
    }

    if ($Observations.GitHubMainState -ne 'PRESENT' -or -not (Test-ReleaseSha $Observations.GitHubMainSha)) {
        return [pscustomobject]@{
            Sha        = 'UNKNOWN'
            Basis      = 'UNKNOWN'
            Conflict   = $false
            Incomplete = $true
            Reason     = 'GITHUB_MAIN_INCOMPLETE'
        }
    }

    return [pscustomobject]@{
        Sha        = $Observations.GitHubMainSha
        Basis      = 'GITHUB_MAIN'
        Conflict   = $false
        Incomplete = $false
        Reason     = ''
    }
}

function Get-ReleaseDerivedStatus {
    param($Observations)

    $source = Get-ReleaseSourceAuthority -Observations $Observations

    $ghcrOut = $Observations.Ghcr.State
    $tagOut = $Observations.GitTag.State
    $nugetOut = $Observations.Nuget.State
    $releaseOut = $Observations.GitHubRelease.State
    $recordOut = $Observations.ReleaseRecord
    $localOut = $Observations.LocalRepo
    $alignOut = $Observations.VersionAlignment

    if ($Observations.Ghcr.ShaTagState -eq 'INCOMPLETE' -and $ghcrOut -eq 'PRESENT') {
        $ghcrOut = 'INCOMPLETE'
    }

    if ($Observations.Ghcr.State -eq 'PRESENT' -and $Observations.Ghcr.ShaTagState -eq 'PRESENT') {
        if ((Test-ReleaseDigest $Observations.Ghcr.Digest) -and (Test-ReleaseDigest $Observations.Ghcr.ShaTagDigest)) {
            if ($Observations.Ghcr.Digest -ne $Observations.Ghcr.ShaTagDigest) {
                $ghcrOut = 'CONFLICT'
            }
        }
        elseif ($ghcrOut -eq 'PRESENT') {
            $ghcrOut = 'INCOMPLETE'
        }
    }

    if ($source.Conflict -and $source.Reason -eq 'TAG_GHCR_REVISION_MISMATCH') {
        if ($tagOut -eq 'PRESENT') { $tagOut = 'CONFLICT' }
        if ($ghcrOut -eq 'PRESENT') { $ghcrOut = 'CONFLICT' }
    }

    $sequenceConflict = $false
    if ($tagOut -eq 'PRESENT' -and $ghcrOut -eq 'ABSENT') { $sequenceConflict = $true }
    if ($nugetOut -eq 'PRESENT' -and $tagOut -eq 'ABSENT') { $sequenceConflict = $true }
    if ($releaseOut -eq 'PRESENT' -and $tagOut -eq 'ABSENT') { $sequenceConflict = $true }
    if ($nugetOut -eq 'PRESENT' -and $ghcrOut -eq 'ABSENT') { $sequenceConflict = $true }
    if ($sequenceConflict) {
        if ($tagOut -eq 'PRESENT' -and $ghcrOut -eq 'ABSENT') { $tagOut = 'CONFLICT' }
        if ($nugetOut -eq 'PRESENT' -and ($tagOut -eq 'ABSENT' -or $tagOut -eq 'CONFLICT') -and $Observations.GitTag.State -eq 'ABSENT') {
            $nugetOut = 'CONFLICT'
        }
        if ($releaseOut -eq 'PRESENT' -and $Observations.GitTag.State -eq 'ABSENT') {
            $releaseOut = 'CONFLICT'
        }
        if ($nugetOut -eq 'PRESENT' -and $Observations.Ghcr.State -eq 'ABSENT') {
            $nugetOut = 'CONFLICT'
        }
    }

    $conflict = $false
    $incomplete = $false
    foreach ($state in @($ghcrOut, $tagOut, $nugetOut, $releaseOut, $recordOut, $localOut, $alignOut)) {
        if ($state -eq 'CONFLICT') { $conflict = $true }
        if ($state -eq 'INCOMPLETE') { $incomplete = $true }
    }
    if ($source.Conflict) { $conflict = $true }
    if ($source.Incomplete) { $incomplete = $true }
    if ($sequenceConflict) { $conflict = $true }
    if ($Observations.GitHubMainState -eq 'INCOMPLETE' -and $source.Basis -eq 'GITHUB_MAIN') {
        $incomplete = $true
    }

    $artifactState = 'UNPUBLISHED'
    $next = 'PUBLISH_IMAGE'
    if ($ghcrOut -eq 'ABSENT' -and $tagOut -eq 'ABSENT' -and $nugetOut -eq 'ABSENT' -and $releaseOut -eq 'ABSENT') {
        $artifactState = 'UNPUBLISHED'
        $next = 'PUBLISH_IMAGE'
    }
    elseif ($ghcrOut -eq 'PRESENT' -and $tagOut -eq 'ABSENT') {
        $artifactState = 'IMAGE_PUBLISHED'
        $next = 'CREATE_TAG'
    }
    elseif ($ghcrOut -eq 'PRESENT' -and $tagOut -eq 'PRESENT' -and $nugetOut -eq 'ABSENT') {
        $artifactState = 'TAGGED'
        $next = 'PUBLISH_NUGET'
    }
    elseif ($nugetOut -eq 'PRESENT' -and $releaseOut -eq 'ABSENT') {
        $artifactState = 'NUGET_PUBLISHED'
        $next = 'CREATE_GITHUB_RELEASE'
    }
    elseif ($releaseOut -eq 'PRESENT' -and $recordOut -ne 'PUBLISHED') {
        $artifactState = 'GITHUB_RELEASE_CREATED'
        $next = 'PREPARE_POST_SYNC'
    }
    elseif ($releaseOut -eq 'PRESENT' -and $recordOut -eq 'PUBLISHED') {
        $artifactState = 'PUBLISHED'
        $next = 'NONE'
    }
    else {
        $artifactState = 'UNPUBLISHED'
        $next = 'STOP'
    }

    $state = $artifactState
    if ($alignOut -eq 'FAIL' -and -not $conflict -and -not $incomplete) {
        $next = 'STOP'
        if ($ghcrOut -eq 'PRESENT' -or $tagOut -eq 'PRESENT' -or $nugetOut -eq 'PRESENT' -or $releaseOut -eq 'PRESENT') {
            $state = 'CONFLICT'
            $conflict = $true
        }
        else {
            $state = 'UNPUBLISHED'
        }
    }

    if ($incomplete -and -not $conflict) {
        $state = 'INCOMPLETE'
        $next = 'STOP'
    }
    if ($conflict) {
        $state = 'CONFLICT'
        $next = 'STOP'
    }

    $mutating = @('PUBLISH_IMAGE', 'CREATE_TAG', 'PUBLISH_NUGET', 'CREATE_GITHUB_RELEASE', 'PREPARE_POST_SYNC')
    if ($mutating -contains $next) {
        $onCanonicalMain = (
            ($localOut -eq 'PASS') -and
            ($Observations.LocalBranch -eq 'main') -and
            (Test-ReleaseSha $Observations.LocalHead) -and
            ($Observations.GitHubMainState -eq 'PRESENT') -and
            ($Observations.LocalHead -eq $Observations.GitHubMainSha)
        )
        if (-not $onCanonicalMain) {
            $next = 'STOP'
        }
    }

    $map = [ordered]@{}
    $map['VERSION'] = $Observations.Version
    $map['SOURCE_SHA'] = $source.Sha
    $map['SOURCE_BASIS'] = $source.Basis
    $map['STATE'] = $state
    $map['LOCAL_REPO'] = $localOut
    $map['VERSION_ALIGNMENT'] = $alignOut
    $map['GHCR'] = $ghcrOut
    $map['GIT_TAG'] = $tagOut
    $map['NUGET'] = $nugetOut
    $map['GITHUB_RELEASE'] = $releaseOut
    $map['RELEASE_RECORD'] = $recordOut
    $map['NEXT_ACTION'] = $next
    $map['MUTATION_PERFORMED'] = 'FALSE'
    return $map
}

function Format-ReleaseStatusLines {
    param($Map)
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($key in $script:StatusKeys) {
        $value = [string]$Map[$key]
        $value = $value -replace '[\r\n]+', ' '
        [void]$lines.Add(('{0}={1}' -f $key, $value))
    }
    return $lines
}

function Invoke-ReleaseReadOnlyRequest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Uri,
        [hashtable]$Headers,
        [int]$TimeoutSec = 30
    )

    $result = [pscustomobject]@{
        StatusCode        = 0
        BodyText          = ''
        Digest            = ''
        TransportFailure  = $false
        FailureClass      = ''
    }

    $requestHeaders = @{
        'User-Agent' = $script:UserAgent
    }
    if ($null -ne $Headers) {
        foreach ($key in $Headers.Keys) {
            $requestHeaders[$key] = $Headers[$key]
        }
    }

    try {
        if ($PSVersionTable.PSVersion.Major -ge 7) {
            $response = Invoke-WebRequest -UseBasicParsing -Method Get `
                -Uri $Uri -Headers $requestHeaders -TimeoutSec $TimeoutSec -SkipHttpErrorCheck
            $result.StatusCode = [int]$response.StatusCode
            $result.BodyText = ConvertTo-ReleaseText $response.Content
            $result.Digest = Get-ReleaseHeaderValue $response.Headers 'Docker-Content-Digest'
            return $result
        }

        $response = Invoke-WebRequest -UseBasicParsing -Method Get `
            -Uri $Uri -Headers $requestHeaders -TimeoutSec $TimeoutSec
        $result.StatusCode = [int]$response.StatusCode
        $result.BodyText = ConvertTo-ReleaseText $response.Content
        $result.Digest = Get-ReleaseHeaderValue $response.Headers 'Docker-Content-Digest'
        return $result
    }
    catch {
        $webResponse = $null
        if ($_.Exception.PSObject.Properties['Response']) {
            $webResponse = $_.Exception.Response
        }
        if ($null -ne $webResponse) {
            try {
                $result.StatusCode = [int]$webResponse.StatusCode
            }
            catch {
                $result.TransportFailure = $true
                $result.FailureClass = 'NETWORK'
                return $result
            }
            return $result
        }
        $result.TransportFailure = $true
        $message = [string]$_.Exception.Message
        if ($message -match 'timeout|timed out|timed-out') {
            $result.FailureClass = 'TIMEOUT'
        }
        else {
            $result.FailureClass = 'NETWORK'
        }
        return $result
    }
}

function Get-GitHubAuthHeaders {
    $headers = @{
        Accept                     = 'application/vnd.github+json'
        'X-GitHub-Api-Version'     = '2022-11-28'
    }
    $token = [Environment]::GetEnvironmentVariable('GITHUB_TOKEN')
    if ([string]::IsNullOrWhiteSpace($token)) {
        $token = [Environment]::GetEnvironmentVariable('GH_TOKEN')
    }
    if (-not [string]::IsNullOrWhiteSpace($token)) {
        $headers['Authorization'] = ('Bearer {0}' -f $token)
    }
    return $headers
}

function Invoke-GitReadOnly {
    param([string[]]$GitArgs)

    $joined = [string]::Join(' ', $GitArgs)
    if ($joined -match '(^|\s)(fetch|pull|push|checkout|reset|clone|commit|tag|merge|rebase|clean)(\s|$)') {
        throw 'release-client blocked a non-read-only git invocation'
    }

    $prevPager = $env:GIT_PAGER
    $env:GIT_PAGER = 'cat'
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & git -c core.pager=cat @GitArgs 2>&1 | ForEach-Object { [string]$_ }
        return [pscustomobject]@{
            ExitCode = [int]$LASTEXITCODE
            Output   = [string]::Join("`n", @($output)).Trim()
        }
    }
    finally {
        $ErrorActionPreference = $prevEap
        if ($null -eq $prevPager) {
            Remove-Item Env:GIT_PAGER -ErrorAction SilentlyContinue
        }
        else {
            $env:GIT_PAGER = $prevPager
        }
    }
}

function Get-LocalRepoObservation {
    param([string]$RepoRoot)

    $diag = [pscustomobject]@{
        State          = 'INCOMPLETE'
        Branch         = ''
        Head           = ''
        Worktree       = 'UNKNOWN'
        OriginIdentity = ''
        LocalMain      = ''
        OriginMain     = ''
        Reason         = ''
    }

    if ([string]::IsNullOrWhiteSpace($RepoRoot) -or -not (Test-Path -LiteralPath $RepoRoot)) {
        $diag.Reason = 'REPO_ROOT'
        return $diag
    }

    Push-Location $RepoRoot
    try {
        $branch = Invoke-GitReadOnly -GitArgs @('rev-parse', '--abbrev-ref', 'HEAD')
        $head = Invoke-GitReadOnly -GitArgs @('rev-parse', 'HEAD')
        $porcelain = Invoke-GitReadOnly -GitArgs @('status', '--porcelain=v1')
        $origin = Invoke-GitReadOnly -GitArgs @('remote', 'get-url', 'origin')
        if ($branch.ExitCode -ne 0 -or $head.ExitCode -ne 0 -or $porcelain.ExitCode -ne 0) {
            $diag.Reason = 'GIT_IDENTITY'
            return $diag
        }
        if (-not (Test-ReleaseSha $head.Output)) {
            $diag.Reason = 'HEAD_PARSE'
            return $diag
        }

        $diag.Branch = $branch.Output
        $diag.Head = $head.Output
        if ([string]::IsNullOrWhiteSpace($porcelain.Output)) {
            $diag.Worktree = 'CLEAN'
        }
        else {
            $diag.Worktree = 'DIRTY'
        }

        if ($origin.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($origin.Output)) {
            $diag.OriginIdentity = Get-OriginRepositoryIdentity -Url $origin.Output
        }

        $localMain = Invoke-GitReadOnly -GitArgs @('rev-parse', 'main')
        if ($localMain.ExitCode -eq 0 -and (Test-ReleaseSha $localMain.Output)) {
            $diag.LocalMain = $localMain.Output
        }
        $originMain = Invoke-GitReadOnly -GitArgs @('rev-parse', 'origin/main')
        if ($originMain.ExitCode -eq 0 -and (Test-ReleaseSha $originMain.Output)) {
            $diag.OriginMain = $originMain.Output
        }

        $canonical = ($diag.OriginIdentity -eq $script:CanonicalOwnerRepo)
        $clean = ($diag.Worktree -eq 'CLEAN')
        $mainsKnown = ((Test-ReleaseSha $diag.LocalMain) -and (Test-ReleaseSha $diag.OriginMain))
        $mainsEqual = ($mainsKnown -and $diag.LocalMain -eq $diag.OriginMain)

        if ($canonical -and $clean -and $mainsKnown -and $mainsEqual) {
            $diag.State = 'PASS'
        }
        elseif ($branch.ExitCode -eq 0) {
            $diag.State = 'DRIFT'
            if (-not $canonical) { $diag.Reason = 'ORIGIN' }
            elseif (-not $clean) { $diag.Reason = 'DIRTY' }
            elseif (-not $mainsKnown) { $diag.Reason = 'MAIN_REF' }
            else { $diag.Reason = 'MAIN_DIVERGED' }
        }
        return $diag
    }
    catch {
        $diag.Reason = 'TOOL'
        return $diag
    }
    finally {
        Pop-Location
    }
}

function Get-GitHubMainObservation {
    $headers = Get-GitHubAuthHeaders
    $resp = Invoke-ReleaseReadOnlyRequest -Uri ($script:GitHubApiRoot + '/commits/main') -Headers $headers
    $presence = ConvertTo-RemotePresence -StatusCode $resp.StatusCode -TransportFailure $resp.TransportFailure
    if ($presence -ne 'HTTP_OK') {
        return [pscustomobject]@{
            State  = 'INCOMPLETE'
            Sha    = ''
            Reason = ConvertTo-RemoteFailureClass -StatusCode $resp.StatusCode -TransportFailure $resp.TransportFailure -FailureClass $resp.FailureClass
        }
    }
    $sha = ''
    if ($resp.BodyText -match '"sha"\s*:\s*"([0-9a-f]{40})"') {
        $sha = $Matches[1]
    }
    if (-not (Test-ReleaseSha $sha)) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Sha = ''; Reason = 'PARSE' }
    }
    return [pscustomobject]@{ State = 'PRESENT'; Sha = $sha; Reason = '' }
}

function Get-GitTagObservation {
    param([string]$Version)
    $headers = Get-GitHubAuthHeaders
    $tagName = 'v' + $Version
    $resp = Invoke-ReleaseReadOnlyRequest -Uri ($script:GitHubApiRoot + '/git/ref/tags/' + $tagName) -Headers $headers
    $presence = ConvertTo-RemotePresence -StatusCode $resp.StatusCode -TransportFailure $resp.TransportFailure
    if ($presence -eq 'ABSENT') {
        return New-ArtifactFact -State 'ABSENT'
    }
    if ($presence -ne 'HTTP_OK') {
        return New-ArtifactFact -State 'INCOMPLETE' -Reason (ConvertTo-RemoteFailureClass -StatusCode $resp.StatusCode -TransportFailure $resp.TransportFailure -FailureClass $resp.FailureClass)
    }

    $objectType = ''
    $objectSha = ''
    if ($resp.BodyText -match '"object"\s*:\s*\{[^}]*"type"\s*:\s*"([^"]+)"') {
        $objectType = $Matches[1]
    }
    if ($resp.BodyText -match '"object"\s*:\s*\{[^}]*"sha"\s*:\s*"([0-9a-f]{40})"') {
        $objectSha = $Matches[1]
    }

    $tagObjectJson = ''
    if ($objectType -eq 'tag' -and (Test-ReleaseSha $objectSha)) {
        $peeled = Invoke-ReleaseReadOnlyRequest -Uri ($script:GitHubApiRoot + '/git/tags/' + $objectSha) -Headers $headers
        $peeledPresence = ConvertTo-RemotePresence -StatusCode $peeled.StatusCode -TransportFailure $peeled.TransportFailure
        if ($peeledPresence -ne 'HTTP_OK') {
            $reason = ConvertTo-RemoteFailureClass -StatusCode $peeled.StatusCode -TransportFailure $peeled.TransportFailure -FailureClass $peeled.FailureClass
            if ($peeledPresence -eq 'ABSENT') {
                return New-ArtifactFact -State 'CONFLICT' -Reason 'TAG_OBJECT_ABSENT'
            }
            return New-ArtifactFact -State 'INCOMPLETE' -Reason $reason
        }
        $tagObjectJson = $peeled.BodyText
    }

    $resolved = Resolve-GitTagTargetFromGitHubJson -RefJson $resp.BodyText -TagObjectJson $tagObjectJson
    if ($resolved.State -eq 'PRESENT') {
        return New-ArtifactFact -State 'PRESENT' -TargetSha $resolved.TargetSha
    }
    return New-ArtifactFact -State $resolved.State -Reason $resolved.Reason
}

function Resolve-GitHubReleaseStateFromJson {
    param(
        [string]$Json,
        [string]$ExpectedTag
    )
    if ([string]::IsNullOrWhiteSpace($Json) -or [string]::IsNullOrWhiteSpace($ExpectedTag)) {
        return New-ArtifactFact -State 'INCOMPLETE' -Reason 'EMPTY_RELEASE'
    }
    $tagName = $null
    $draftRaw = $null
    $prereleaseRaw = $null
    if ($Json -match '"tag_name"\s*:\s*"([^"]+)"') { $tagName = $Matches[1] }
    if ($Json -match '"draft"\s*:\s*(true|false)') { $draftRaw = $Matches[1] }
    if ($Json -match '"prerelease"\s*:\s*(true|false)') { $prereleaseRaw = $Matches[1] }
    if ($null -eq $tagName -or $null -eq $draftRaw -or $null -eq $prereleaseRaw) {
        return New-ArtifactFact -State 'INCOMPLETE' -Reason 'RELEASE_PARSE'
    }
    if ($tagName -ne $ExpectedTag) {
        return New-ArtifactFact -State 'CONFLICT' -Reason 'TAG_NAME'
    }
    if ($prereleaseRaw -eq 'true') {
        return New-ArtifactFact -State 'CONFLICT' -Reason 'PRERELEASE'
    }
    if ($draftRaw -eq 'true') {
        return New-ArtifactFact -State 'CONFLICT' -Reason 'DRAFT'
    }
    return New-ArtifactFact -State 'PRESENT'
}

function Get-GitHubReleaseObservation {
    param([string]$Version)
    $headers = Get-GitHubAuthHeaders
    $tagName = 'v' + $Version
    $resp = Invoke-ReleaseReadOnlyRequest -Uri ($script:GitHubApiRoot + '/releases/tags/' + $tagName) -Headers $headers
    $presence = ConvertTo-RemotePresence -StatusCode $resp.StatusCode -TransportFailure $resp.TransportFailure
    if ($presence -eq 'ABSENT') {
        return New-ArtifactFact -State 'ABSENT'
    }
    if ($presence -ne 'HTTP_OK') {
        return New-ArtifactFact -State 'INCOMPLETE' -Reason (ConvertTo-RemoteFailureClass -StatusCode $resp.StatusCode -TransportFailure $resp.TransportFailure -FailureClass $resp.FailureClass)
    }
    return Resolve-GitHubReleaseStateFromJson -Json $resp.BodyText -ExpectedTag $tagName
}

function Get-NugetObservation {
    param(
        [string]$Version,
        $Request,
        $UtcClock
    )
    $uri = 'https://api.nuget.org/v3-flatcontainer/' + $script:NugetPackageId + '/index.json'
    $resp = if ($null -ne $Request) {
        & $Request $uri $null
    }
    else {
        Invoke-ReleaseReadOnlyRequest -Uri $uri
    }
    $presence = ConvertTo-RemotePresence -StatusCode $resp.StatusCode -TransportFailure $resp.TransportFailure
    if ($presence -eq 'ABSENT') {
        return New-ArtifactFact -State 'ABSENT'
    }
    if ($presence -ne 'HTTP_OK') {
        return New-ArtifactFact -State 'INCOMPLETE' -Reason (ConvertTo-RemoteFailureClass -StatusCode $resp.StatusCode -TransportFailure $resp.TransportFailure -FailureClass $resp.FailureClass)
    }
    $contains = Test-NugetIndexContainsVersion -IndexJson $resp.BodyText -Version $Version
    if ($null -eq $contains) {
        return New-ArtifactFact -State 'INCOMPLETE' -Reason 'PARSE'
    }
    if ($contains) {
        $observedAt = ''
        if ($null -ne $UtcClock) {
            $observedAt = Format-ReleaseUtcTimestamp -Utc (& $UtcClock)
        }
        else {
            $observedAt = Format-ReleaseUtcTimestamp -Utc (Get-ReleaseUtcNow)
        }
        return New-ArtifactFact -State 'PRESENT' -ObservedAtUtc $observedAt
    }
    return New-ArtifactFact -State 'ABSENT'
}

function Get-NugetSymbolsObservation {
    param(
        [string]$Version,
        $Request
    )
    if (-not (Test-ReleaseVersion $Version)) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Reason = 'INVALID_VERSION' }
    }
    $fileName = $script:NugetPackageId + '.' + $Version + '.snupkg'
    $uri = 'https://api.nuget.org/v3-flatcontainer/' + $script:NugetPackageId + '/' + $Version + '/' + $fileName
    $resp = if ($null -ne $Request) {
        & $Request $uri $null
    }
    else {
        Invoke-ReleaseReadOnlyRequest -Uri $uri
    }
    $presence = ConvertTo-RemotePresence -StatusCode $resp.StatusCode -TransportFailure $resp.TransportFailure
    if ($presence -eq 'ABSENT') {
        return [pscustomobject]@{ State = 'ABSENT'; Reason = '' }
    }
    if ($presence -ne 'HTTP_OK') {
        return [pscustomobject]@{
            State  = 'INCOMPLETE'
            Reason = (ConvertTo-RemoteFailureClass -StatusCode $resp.StatusCode -TransportFailure $resp.TransportFailure -FailureClass $resp.FailureClass)
        }
    }
    return [pscustomobject]@{ State = 'OBSERVED'; Reason = '' }
}

function Get-GhcrPullToken {
    $resp = Invoke-ReleaseReadOnlyRequest -Uri ('https://ghcr.io/token?service=ghcr.io&scope=repository:' + $script:GhcrRepository + ':pull')
    $presence = ConvertTo-RemotePresence -StatusCode $resp.StatusCode -TransportFailure $resp.TransportFailure
    if ($presence -ne 'HTTP_OK') { return $null }
    if ($resp.BodyText -match '"token"\s*:\s*"([^"]+)"') {
        return $Matches[1]
    }
    return $null
}

function Invoke-GhcrReadOnlyRequest {
    param(
        [string]$Uri,
        [hashtable]$Headers,
        $Request
    )
    if ($null -ne $Request) {
        return & $Request $Uri $Headers
    }
    return Invoke-ReleaseReadOnlyRequest -Uri $Uri -Headers $Headers
}

function Get-GhcrManifestFact {
    param(
        [string]$Reference,
        [string]$Token,
        [switch]$ReadRevision,
        $Request
    )

    $headers = @{
        Authorization = ('Bearer {0}' -f $Token)
        Accept        = 'application/vnd.oci.image.index.v1+json, application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.list.v2+json, application/vnd.docker.distribution.manifest.v2+json'
    }
    $uri = 'https://ghcr.io/v2/' + $script:GhcrRepository + '/manifests/' + $Reference
    $resp = Invoke-GhcrReadOnlyRequest -Uri $uri -Headers $headers -Request $Request
    $presence = ConvertTo-RemotePresence -StatusCode $resp.StatusCode -TransportFailure $resp.TransportFailure
    if ($presence -eq 'ABSENT') {
        return New-ArtifactFact -State 'ABSENT'
    }
    if ($presence -ne 'HTTP_OK') {
        return New-ArtifactFact -State 'INCOMPLETE' -Reason (ConvertTo-RemoteFailureClass -StatusCode $resp.StatusCode -TransportFailure $resp.TransportFailure -FailureClass $resp.FailureClass)
    }
    $digest = $resp.Digest
    if (-not (Test-ReleaseDigest $digest)) {
        return New-ArtifactFact -State 'INCOMPLETE' -Reason 'DIGEST_HEADER'
    }

    if (-not $ReadRevision) {
        return New-ArtifactFact -State 'PRESENT' -Digest $digest
    }

    $mediaType = ''
    if ($resp.BodyText -match '"mediaType"\s*:\s*"([^"]+)"') {
        $mediaType = $Matches[1]
    }
    if ($mediaType -match 'image\.manifest') {
        $configDigest = Get-GhcrConfigDigestFromManifest -ManifestJson $resp.BodyText
        if (-not (Test-ReleaseDigest $configDigest)) {
            return New-ArtifactFact -State 'INCOMPLETE' -Digest $digest -Reason 'CONFIG_DIGEST'
        }
        $blobHeaders = @{
            Authorization = ('Bearer {0}' -f $Token)
            Accept        = 'application/vnd.oci.image.config.v1+json'
        }
        $blobUri = 'https://ghcr.io/v2/' + $script:GhcrRepository + '/blobs/' + $configDigest
        $blob = Invoke-GhcrReadOnlyRequest -Uri $blobUri -Headers $blobHeaders -Request $Request
        $blobPresence = ConvertTo-RemotePresence -StatusCode $blob.StatusCode -TransportFailure $blob.TransportFailure
        if ($blobPresence -ne 'HTTP_OK') {
            $reason = ConvertTo-RemoteFailureClass -StatusCode $blob.StatusCode -TransportFailure $blob.TransportFailure -FailureClass $blob.FailureClass
            if ($blobPresence -eq 'ABSENT') { $reason = 'CONFIG_BLOB_ABSENT' }
            return New-ArtifactFact -State 'INCOMPLETE' -Digest $digest -Reason $reason
        }
        $parsedRevision = Get-OciRevisionFromConfigText -ConfigText $blob.BodyText
        $parsedVersion = Get-OciVersionFromConfigText -ConfigText $blob.BodyText
        if (-not (Test-ReleaseSha $parsedRevision)) {
            return New-ArtifactFact -State 'INCOMPLETE' -Digest $digest -Reason 'REVISION_PARSE'
        }
        return New-ArtifactFact -State 'PRESENT' -Digest $digest -Revision $parsedRevision -OciVersion $parsedVersion
    }
    if ($mediaType -match 'image\.index|manifest\.list') {
        $childDigests = @([regex]::Matches($resp.BodyText, '"digest"\s*:\s*"(sha256:[0-9a-f]{64})"') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
        if ($childDigests.Count -ne 1) {
            return New-ArtifactFact -State 'INCOMPLETE' -Digest $digest -Reason 'INDEX_NOT_UNIQUE'
        }
        $child = Get-GhcrManifestFact -Reference $childDigests[0] -Token $Token -ReadRevision -Request $Request
        if ($child.State -ne 'PRESENT' -or -not (Test-ReleaseSha $child.Revision)) {
            $reason = $child.Reason
            if ($child.State -eq 'ABSENT') { $reason = 'CHILD_MANIFEST_ABSENT' }
            if ([string]::IsNullOrWhiteSpace($reason)) { $reason = 'CHILD_MANIFEST' }
            return New-ArtifactFact -State 'INCOMPLETE' -Digest $digest -Reason $reason
        }
        return New-ArtifactFact -State 'PRESENT' -Digest $digest -Revision $child.Revision -OciVersion $child.OciVersion
    }
    return New-ArtifactFact -State 'INCOMPLETE' -Digest $digest -Reason 'MEDIA_TYPE'
}

function Get-GhcrObservation {
    param(
        [string]$Version,
        [string]$SourceSha
    )

    $token = Get-GhcrPullToken
    if ([string]::IsNullOrWhiteSpace($token)) {
        return New-ArtifactFact -State 'INCOMPLETE' -Reason 'GHCR_TOKEN'
    }

    $versionTag = 'v' + $Version
    $versionFact = Get-GhcrManifestFact -Reference $versionTag -Token $token -ReadRevision
    if ($versionFact.State -ne 'PRESENT') {
        return $versionFact
    }

    $shaLookup = $SourceSha
    if (-not (Test-ReleaseSha $shaLookup) -and (Test-ReleaseSha $versionFact.Revision)) {
        $shaLookup = $versionFact.Revision
    }

    $shaTagState = ''
    $shaTagDigest = ''
    if (Test-ReleaseSha $shaLookup) {
        $shaRef = 'sha-' + $shaLookup
        $shaFact = Get-GhcrManifestFact -Reference $shaRef -Token $token
        $shaTagState = $shaFact.State
        $shaTagDigest = $shaFact.Digest
        if ($shaFact.State -eq 'INCOMPLETE') {
            $versionFact.ShaTagState = 'INCOMPLETE'
            $versionFact.Reason = $shaFact.Reason
            return $versionFact
        }
    }

    $versionFact.ShaTagState = $shaTagState
    $versionFact.ShaTagDigest = $shaTagDigest
    return $versionFact
}

function Get-RepoVersionObservation {
    param(
        [string]$RepoRoot,
        [string]$Version
    )
    $contractsPath = Join-Path $RepoRoot 'src\Amane.Mailer.Contracts\Amane.Mailer.Contracts.csproj'
    $openapiPath = Join-Path $RepoRoot 'docs\api\openapi.yaml'
    $contractsText = $null
    $openapiText = $null
    try {
        if (Test-Path -LiteralPath $contractsPath) {
            $contractsText = [System.IO.File]::ReadAllText($contractsPath)
        }
        if (Test-Path -LiteralPath $openapiPath) {
            $openapiText = [System.IO.File]::ReadAllText($openapiPath)
        }
    }
    catch {
        return [pscustomobject]@{
            Alignment        = 'INCOMPLETE'
            ContractsVersion = ''
            OpenApiVersion   = ''
        }
    }
    $contractsVersion = Get-ContractsVersionFromText -Text $contractsText
    $openApiVersion = Get-OpenApiVersionFromText -Text $openapiText
    return [pscustomobject]@{
        Alignment        = (Get-VersionAlignment -RequestedVersion $Version -ContractsVersion $contractsVersion -OpenApiVersion $openApiVersion)
        ContractsVersion = $contractsVersion
        OpenApiVersion   = $openApiVersion
    }
}

function Get-ReleaseRecordObservation {
    param(
        [string]$RepoRoot,
        [string]$Version
    )
    $path = Join-Path $RepoRoot ('docs\releases\v{0}.md' -f $Version)
    if (-not (Test-Path -LiteralPath $path)) {
        return 'ABSENT'
    }
    try {
        $text = [System.IO.File]::ReadAllText($path)
    }
    catch {
        return 'INCOMPLETE'
    }
    return Get-ReleaseRecordStateFromText -Text $text
}

function Write-ReleaseStatusDiagnostics {
    param($Observations, $Map)
    Write-ReleaseStderr ('release-client: observing VERSION={0} (read-only)' -f $Observations.Version)
    if (Test-ReleaseSha $Observations.GitHubMainSha) {
        Write-ReleaseStderr ('release-client: github_main={0}' -f $Observations.GitHubMainSha)
    }
    elseif ($Observations.GitHubMainState -eq 'INCOMPLETE') {
        Write-ReleaseStderr 'release-client: github_main=INCOMPLETE'
    }
    if (Test-ReleaseSha $Observations.GitTag.TargetSha) {
        Write-ReleaseStderr ('release-client: git_tag_target={0}' -f $Observations.GitTag.TargetSha)
    }
    if (Test-ReleaseDigest $Observations.Ghcr.Digest) {
        Write-ReleaseStderr ('release-client: ghcr_digest={0}' -f $Observations.Ghcr.Digest)
    }
    if (Test-ReleaseSha $Observations.Ghcr.Revision) {
        Write-ReleaseStderr ('release-client: ghcr_revision={0}' -f $Observations.Ghcr.Revision)
    }
    $branch = $Observations.LocalBranch
    if ([string]::IsNullOrWhiteSpace($branch)) { $branch = 'UNKNOWN' }
    Write-ReleaseStderr ('release-client: local_branch={0} worktree={1} origin={2}' -f $branch, $Observations.Worktree, $Observations.OriginIdentity)
    foreach ($name in @('GitTag', 'Ghcr', 'Nuget', 'GitHubRelease')) {
        $fact = $Observations.$name
        if ($fact.State -eq 'INCOMPLETE' -and -not [string]::IsNullOrWhiteSpace($fact.Reason)) {
            Write-ReleaseStderr ('release-client: {0} incomplete ({1})' -f $name, $fact.Reason)
        }
    }
    Write-ReleaseStderr ('release-client: STATE={0} NEXT_ACTION={1}' -f $Map['STATE'], $Map['NEXT_ACTION'])
}

function Invoke-ReleaseStatus {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version,
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        $Observers,
        [switch]$Quiet
    )

    if (-not (Test-ReleaseVersion $Version)) {
        throw 'Version must be X.Y.Z; the client does not infer or accept a v-prefix.'
    }

    $local = $null
    $githubMain = $null
    $gitTag = $null
    $githubRelease = $null
    $nuget = $null
    $versions = $null
    $record = $null
    $ghcr = $null

    if ($null -ne $Observers) {
        $local = & $Observers['LocalRepo'] $RepoRoot
        $githubMain = & $Observers['GitHubMain']
        $gitTag = & $Observers['GitTag'] $Version
        $githubRelease = & $Observers['GitHubRelease'] $Version
        $nuget = & $Observers['Nuget'] $Version
        $versions = & $Observers['Versions'] $RepoRoot $Version
        $record = & $Observers['ReleaseRecord'] $RepoRoot $Version
    }
    else {
        $local = Get-LocalRepoObservation -RepoRoot $RepoRoot
        $githubMain = Get-GitHubMainObservation
        $gitTag = Get-GitTagObservation -Version $Version
        $githubRelease = Get-GitHubReleaseObservation -Version $Version
        $nuget = Get-NugetObservation -Version $Version
        $versions = Get-RepoVersionObservation -RepoRoot $RepoRoot -Version $Version
        $record = Get-ReleaseRecordObservation -RepoRoot $RepoRoot -Version $Version
    }

    $sourceShaGuess = ''
    if ($gitTag.State -eq 'PRESENT' -and (Test-ReleaseSha $gitTag.TargetSha)) {
        $sourceShaGuess = $gitTag.TargetSha
    }

    if ($null -ne $Observers -and $Observers.Contains('Ghcr')) {
        $ghcr = & $Observers['Ghcr'] $Version $sourceShaGuess
    }
    elseif ($null -ne $Observers) {
        $ghcr = New-ArtifactFact -State 'ABSENT'
    }
    else {
        $ghcr = Get-GhcrObservation -Version $Version -SourceSha $sourceShaGuess
    }

    if ($githubMain.State -ne 'PRESENT' -or -not (Test-ReleaseSha $githubMain.Sha)) {
        if ($local.State -eq 'PASS') {
            $local.State = 'INCOMPLETE'
            $local.Reason = 'GITHUB_MAIN'
        }
    }
    elseif ($local.State -eq 'PASS') {
        if ($local.LocalMain -ne $githubMain.Sha -or $local.OriginMain -ne $githubMain.Sha) {
            $local.State = 'DRIFT'
            $local.Reason = 'GITHUB_MAIN'
        }
        elseif ($local.Head -ne $githubMain.Sha -or $local.Branch -ne 'main') {
            $local.State = 'DRIFT'
            $local.Reason = 'NOT_CANONICAL_HEAD'
        }
    }

    $obs = New-ReleaseObservations `
        -Version $Version `
        -LocalRepo $local.State `
        -LocalBranch $local.Branch `
        -LocalHead $local.Head `
        -Worktree $local.Worktree `
        -OriginIdentity $local.OriginIdentity `
        -GitHubMainState $githubMain.State `
        -GitHubMainSha $githubMain.Sha `
        -VersionAlignment $versions.Alignment `
        -ContractsVersion $versions.ContractsVersion `
        -OpenApiVersion $versions.OpenApiVersion `
        -ReleaseRecord $record `
        -GitTag $gitTag `
        -GitHubRelease $githubRelease `
        -Ghcr $ghcr `
        -Nuget $nuget

    $map = Get-ReleaseDerivedStatus -Observations $obs
    if (-not $Quiet) {
        Write-ReleaseStatusDiagnostics -Observations $obs -Map $map
        foreach ($line in (Format-ReleaseStatusLines -Map $map)) {
            [Console]::Out.WriteLine($line)
        }
    }
    return $map
}

$script:PreflightKeys = @(
    'COMMAND',
    'VERSION',
    'RELEASE_COMMIT_SHA',
    'PREFLIGHT_RESULT',
    'SOURCE_BINDING',
    'VERSION_PREP',
    'COLLISION_GIT_TAG',
    'COLLISION_GITHUB_RELEASE',
    'COLLISION_GHCR_VERSION',
    'COLLISION_GHCR_SHA',
    'COLLISION_NUGET',
    'WORKFLOW_PUBLISH_IMAGE',
    'WORKFLOW_PUBLISH_CONTRACTS',
    'WORKFLOW_VERIFY_PUBLIC_IMAGE',
    'IMAGE_PUBLISH_RUN',
    'IMAGE_PUBLISH_RUN_ID',
    'NUGET_PUBLISH_RUN',
    'NUGET_PUBLISH_RUN_ID',
    'TECHNICAL_READINESS',
    'HUMAN_AUTHORIZATION_REQUIRED',
    'MUTATION_PERFORMED'
)

# Constructed so client sources never contain mutating command tokens.
$script:EventWorkflowDispatch = 'workflow' + '_dispatch'
$script:YamlWorkflowDispatch = $script:EventWorkflowDispatch + ':'
$script:NeedleDotnetNugetPush = 'dotnet ' + ('nu' + 'get') + ' push'
$script:NeedleDockerLogin = 'docker' + ' log' + 'in'
$script:NeedleDockerPush = 'docker' + ' pu' + 'sh'

function Get-LeadingSpaceCount {
    param([string]$Line)
    if ([string]::IsNullOrEmpty($Line)) { return 0 }
    $n = 0
    $chars = $Line.ToCharArray()
    $i = 0
    while ($i -lt $chars.Length) {
        if ($chars[$i] -eq ' ') { $n++; $i++; continue }
        if ($chars[$i] -eq "`t") { $n += 2; $i++; continue }
        break
    }
    return $n
}

function Get-ActiveWorkflowLineText {
    param(
        [string]$Line,
        [string]$Mode
    )
    if ([string]::IsNullOrEmpty($Line)) { return '' }
    $sb = New-Object System.Text.StringBuilder
    $inSingle = $false
    $inDouble = $false
    $escape = $false
    $chars = $Line.ToCharArray()
    $i = 0
    while ($i -lt $chars.Length) {
        $c = $chars[$i]
        if ($inSingle) {
            [void]$sb.Append($c)
            if ($c -eq [char]39) {
                if ((($i + 1) -lt $chars.Length) -and ($chars[$i + 1] -eq [char]39)) {
                    [void]$sb.Append($chars[$i + 1])
                    $i += 2
                    continue
                }
                $inSingle = $false
            }
            $i++
            continue
        }
        if ($inDouble) {
            [void]$sb.Append($c)
            if ($escape) {
                $escape = $false
                $i++
                continue
            }
            if ($c -eq [char]92) {
                $escape = $true
                $i++
                continue
            }
            if ($c -eq [char]34) { $inDouble = $false }
            $i++
            continue
        }
        if ($c -eq [char]39) {
            $inSingle = $true
            [void]$sb.Append($c)
            $i++
            continue
        }
        if ($c -eq [char]34) {
            $inDouble = $true
            [void]$sb.Append($c)
            $i++
            continue
        }
        if ($c -eq [char]35) { break }
        [void]$sb.Append($c)
        $i++
    }
    return $sb.ToString().TrimEnd()
}

function Get-WorkflowYamlKeyName {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return '' }
    $t = $Text.Trim()
    if ($t -match '^([A-Za-z0-9_.-]+)\s*:(.*)$') {
        return [string]$Matches[1]
    }
    return ''
}

function Get-WorkflowYamlKeyValue {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return '' }
    $t = $Text.Trim()
    if ($t -match '^([A-Za-z0-9_.-]+)\s*:(.*)$') {
        return ([string]$Matches[2]).Trim()
    }
    return ''
}

function ConvertTo-WorkflowActiveLines {
    param([string]$Text)
    $result = New-Object System.Collections.Generic.List[object]
    if ([string]::IsNullOrEmpty($Text)) { return ,$result }

    $rawLines = $Text -split '\r\n|\n|\r'
    $inShell = $false
    $contentIndent = -1
    $jobsIndent = -1
    $jobKeyIndent = -1
    $currentJob = ''

    foreach ($raw in @($rawLines)) {
        $rawIndent = Get-LeadingSpaceCount -Line $raw

        if ($inShell) {
            $isBlank = [string]::IsNullOrWhiteSpace($raw)
            if ($isBlank) { continue }
            if ($contentIndent -lt 0) { $contentIndent = $rawIndent }
            if ($rawIndent -lt $contentIndent) {
                $inShell = $false
                $contentIndent = -1
            }
            else {
                $shellActive = Get-ActiveWorkflowLineText -Line $raw -Mode 'Shell'
                if (-not [string]::IsNullOrWhiteSpace($shellActive.Trim())) {
                    [void]$result.Add([pscustomobject]@{
                        Kind       = 'Shell'
                        Indent     = $rawIndent
                        Text       = $shellActive.Trim()
                        Job        = $currentJob
                        Executable = $true
                    })
                }
                continue
            }
        }

        $yamlActive = Get-ActiveWorkflowLineText -Line $raw -Mode 'Yaml'
        if ([string]::IsNullOrWhiteSpace($yamlActive)) { continue }
        $trim = $yamlActive.Trim()
        $key = Get-WorkflowYamlKeyName -Text $trim
        $val = Get-WorkflowYamlKeyValue -Text $trim
        $executable = $false
        $startShell = $false
        if ($key -eq 'run') {
            if ($val -match '^[|>][-+]?\s*$') {
                $startShell = $true
            }
            elseif (-not [string]::IsNullOrWhiteSpace($val)) {
                $executable = $true
            }
        }

        if ($key -eq 'jobs') {
            $jobsIndent = $rawIndent
            $jobKeyIndent = -1
            $currentJob = ''
        }
        elseif ($jobsIndent -ge 0) {
            if ($rawIndent -le $jobsIndent) {
                $jobsIndent = -1
                $jobKeyIndent = -1
                $currentJob = ''
            }
            elseif ($jobKeyIndent -lt 0 -or $rawIndent -eq $jobKeyIndent) {
                if (-not [string]::IsNullOrWhiteSpace($key)) {
                    $currentJob = $key
                    $jobKeyIndent = $rawIndent
                }
            }
            elseif ($rawIndent -lt $jobKeyIndent) {
                $currentJob = ''
            }
        }

        [void]$result.Add([pscustomobject]@{
            Kind       = 'Yaml'
            Indent     = $rawIndent
            Text       = $trim
            Job        = $currentJob
            Executable = $executable
        })

        if ($startShell) {
            $inShell = $true
            $contentIndent = -1
        }
    }

    return ,$result
}

function Test-WorkflowYamlPath {
    param(
        $Lines,
        [string[]]$Keys
    )
    if ($null -eq $Lines -or $null -eq $Keys -or $Keys.Count -eq 0) { return $false }
    $from = 0
    $parentIndent = -1
    foreach ($want in @($Keys)) {
        $found = $false
        $i = $from
        while ($i -lt $Lines.Count) {
            $line = $Lines[$i]
            $i++
            if ($line.Kind -ne 'Yaml') { continue }
            if ($parentIndent -ge 0 -and $line.Indent -le $parentIndent) { break }
            $name = Get-WorkflowYamlKeyName -Text $line.Text
            if ($name -eq $want) {
                $found = $true
                $from = $i
                $parentIndent = $line.Indent
                break
            }
        }
        if (-not $found) { return $false }
    }
    return $true
}

function Test-WorkflowYamlPathValue {
    param(
        $Lines,
        [string[]]$Keys,
        [string]$Value
    )
    if (-not (Test-WorkflowYamlPath -Lines $Lines -Keys $Keys)) { return $false }
    $from = 0
    $parentIndent = -1
    $node = $null
    foreach ($want in @($Keys)) {
        $i = $from
        while ($i -lt $Lines.Count) {
            $line = $Lines[$i]
            $i++
            if ($line.Kind -ne 'Yaml') { continue }
            if ($parentIndent -ge 0 -and $line.Indent -le $parentIndent) { break }
            $name = Get-WorkflowYamlKeyName -Text $line.Text
            if ($name -eq $want) {
                $node = $line
                $from = $i
                $parentIndent = $line.Indent
                break
            }
        }
    }
    if ($null -eq $node) { return $false }
    $got = Get-WorkflowYamlKeyValue -Text $node.Text
    return ($got -eq $Value)
}

function Test-WorkflowJobYamlKeyValue {
    param(
        $Lines,
        [string]$Job,
        [string]$Key,
        [string]$Value
    )
    foreach ($line in $Lines) {
        if ($line.Kind -ne 'Yaml') { continue }
        if ($line.Job -ne $Job) { continue }
        $name = Get-WorkflowYamlKeyName -Text $line.Text
        if ($name -ne $Key) { continue }
        $got = Get-WorkflowYamlKeyValue -Text $line.Text
        if ($got -eq $Value) { return $true }
    }
    return $false
}

function Test-WorkflowYamlKeyValueExists {
    param(
        $Lines,
        [string]$Key,
        [string]$Value
    )
    foreach ($line in $Lines) {
        if ($line.Kind -ne 'Yaml') { continue }
        $name = Get-WorkflowYamlKeyName -Text $line.Text
        if ($name -ne $Key) { continue }
        $got = Get-WorkflowYamlKeyValue -Text $line.Text
        if ($got -eq $Value) { return $true }
    }
    return $false
}

function Test-WorkflowJobKeyExists {
    param(
        $Lines,
        [string]$Job
    )
    foreach ($line in $Lines) {
        if ($line.Kind -ne 'Yaml') { continue }
        if ($line.Job -ne $Job) { continue }
        $name = Get-WorkflowYamlKeyName -Text $line.Text
        if ($name -eq $Job) { return $true }
    }
    return $false
}

function Get-WorkflowYamlKeyValueCount {
    param(
        $Lines,
        [string]$Key,
        [string]$Value,
        [string]$Job = ''
    )
    $count = 0
    foreach ($line in $Lines) {
        if ($line.Kind -ne 'Yaml') { continue }
        if (-not [string]::IsNullOrWhiteSpace($Job) -and $line.Job -ne $Job) { continue }
        $name = Get-WorkflowYamlKeyName -Text $line.Text
        if ($name -ne $Key) { continue }
        $got = Get-WorkflowYamlKeyValue -Text $line.Text
        if ($got -eq $Value) { $count++ }
    }
    return $count
}

function Test-WorkflowActiveContains {
    param(
        $Lines,
        [string]$Needle
    )
    if ([string]::IsNullOrWhiteSpace($Needle)) { return $false }
    foreach ($line in $Lines) {
        if (([string]$line.Text).IndexOf($Needle) -ge 0) { return $true }
    }
    return $false
}

function Test-WorkflowExecutableContains {
    param(
        $Lines,
        [string]$Needle,
        [string]$Job = ''
    )
    if ([string]::IsNullOrWhiteSpace($Needle)) { return $false }
    foreach ($line in $Lines) {
        if (-not $line.Executable) { continue }
        if (-not [string]::IsNullOrWhiteSpace($Job) -and $line.Job -ne $Job) { continue }
        if (([string]$line.Text).IndexOf($Needle) -ge 0) { return $true }
    }
    return $false
}

function Get-WorkflowExecutableText {
    param($Line)
    if ($null -eq $Line -or -not $Line.Executable) { return '' }
    $text = [string]$Line.Text
    if ($Line.Kind -eq 'Yaml' -and (Get-WorkflowYamlKeyName -Text $text) -eq 'run') {
        return (Get-WorkflowYamlKeyValue -Text $text).Trim()
    }
    return $text.Trim()
}

function Test-WorkflowScriptInvocation {
    param(
        $Lines,
        [string]$ScriptPath,
        [string]$Job = ''
    )
    if ([string]::IsNullOrWhiteSpace($ScriptPath)) { return $false }
    $script = [regex]::Escape($ScriptPath.TrimStart('.', '/'))
    $assign = '(?:[A-Za-z_][A-Za-z0-9_]*=(?:"[^"]*"|''[^'']*''|[^\s]+)\s+)*'
    $invocation = '(?:(?:bash|sh)\s+(?:--\s+)?(?:\./)?' + $script + '|\./' + $script + ')'
    $pattern = '^' + $assign + $invocation + '(?=\s|$)'
    foreach ($line in $Lines) {
        if (-not $line.Executable) { continue }
        if (-not [string]::IsNullOrWhiteSpace($Job) -and $line.Job -ne $Job) { continue }
        $text = Get-WorkflowExecutableText -Line $line
        if ([regex]::IsMatch($text, $pattern, [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)) { return $true }
    }
    return $false
}

function Test-WorkflowFailClosedEqualityBinding {
    param(
        $Lines,
        [string]$Job,
        [string]$LeftVariable,
        [string]$RightVariable
    )
    if ([string]::IsNullOrWhiteSpace($LeftVariable) -or [string]::IsNullOrWhiteSpace($RightVariable)) { return $false }
    $left = [regex]::Escape(('${' + $LeftVariable + '}'))
    $right = [regex]::Escape(('${' + $RightVariable + '}'))
    $comparisonPattern = '^\[\[\s*"' + $left + '"\s*==\s*"' + $right + '"\s*\]\]\s*(?<tail>.*)$'
    $inlineFailClosePattern = '^\|\|\s*(?:exit\s+[1-9][0-9]*|\{.*;\s*exit\s+[1-9][0-9]*\s*;\s*\})\s*$'

    for ($i = 0; $i -lt $Lines.Count; $i++) {
        $line = $Lines[$i]
        if (-not $line.Executable -or $line.Job -ne $Job) { continue }
        $text = Get-WorkflowExecutableText -Line $line
        $match = [regex]::Match($text, $comparisonPattern, [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
        if (-not $match.Success) { continue }
        $tail = $match.Groups['tail'].Value.Trim()
        if ([regex]::IsMatch($tail, $inlineFailClosePattern, [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)) { return $true }
        if ($tail -ne '\') { continue }

        if (($i + 1) -ge $Lines.Count) { continue }
        $next = $Lines[$i + 1]
        if (-not $next.Executable -or $next.Kind -ne 'Shell' -or $next.Job -ne $Job) { continue }
        $nextText = Get-WorkflowExecutableText -Line $next
        if ([regex]::IsMatch($nextText, $inlineFailClosePattern, [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)) { return $true }
    }
    return $false
}

function Test-WorkflowExecutableLineHasAll {
    param(
        $Lines,
        [string[]]$Needles,
        [string]$Job = ''
    )
    foreach ($line in $Lines) {
        if (-not $line.Executable) { continue }
        if (-not [string]::IsNullOrWhiteSpace($Job) -and $line.Job -ne $Job) { continue }
        $text = [string]$line.Text
        $ok = $true
        foreach ($needle in @($Needles)) {
            if ($text.IndexOf($needle) -lt 0) { $ok = $false; break }
        }
        if ($ok) { return $true }
    }
    return $false
}

function Test-WorkflowActiveHasAnyNeedle {
    param(
        $Lines,
        [string[]]$Needles
    )
    foreach ($needle in @($Needles)) {
        if ([string]::IsNullOrWhiteSpace($needle)) { continue }
        if (Test-WorkflowActiveContains -Lines $Lines -Needle $needle) { return $true }
    }
    return $false
}

function Get-ReleaseSourceBinding {
    param(
        $Local,
        [string]$GitHubMainState,
        [string]$GitHubMainSha,
        [string]$ReleaseCommitSha
    )
    if (-not (Test-ReleaseSha $ReleaseCommitSha)) { return 'FAIL' }
    if ($null -eq $Local) { return 'INCOMPLETE' }
    if ($Local.State -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if ($GitHubMainState -ne 'PRESENT' -or -not (Test-ReleaseSha $GitHubMainSha)) { return 'INCOMPLETE' }
    if ($Local.OriginIdentity -ne $script:CanonicalOwnerRepo) { return 'FAIL' }
    if ($Local.Worktree -eq 'UNKNOWN') { return 'INCOMPLETE' }
    if ($Local.Worktree -ne 'CLEAN') { return 'FAIL' }
    if ($Local.Branch -ne 'main') { return 'FAIL' }
    if (-not (Test-ReleaseSha $Local.Head)) { return 'INCOMPLETE' }
    if (-not (Test-ReleaseSha $Local.LocalMain)) { return 'INCOMPLETE' }
    if (-not (Test-ReleaseSha $Local.OriginMain)) { return 'INCOMPLETE' }
    if ($Local.Head -ne $Local.LocalMain) { return 'FAIL' }
    if ($Local.Head -ne $Local.OriginMain) { return 'FAIL' }
    if ($Local.Head -ne $GitHubMainSha) { return 'FAIL' }
    if ($Local.Head -ne $ReleaseCommitSha) { return 'FAIL' }
    return 'PASS'
}

function Test-ChangelogHasReleaseEntry {
    param(
        [string]$Text,
        [string]$Version
    )
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    if (-not (Test-ReleaseVersion $Version)) { return $null }
    $escaped = [regex]::Escape($Version)
    if ($Text -match ('(?m)^## \[' + $escaped + '\]')) { return $true }
    return $false
}

function Get-VersionPrepState {
    param(
        [string]$Alignment,
        [string]$ChangelogHasEntry,
        [string]$ReleaseRecord
    )
    $rank = 0
    if ($Alignment -eq 'INCOMPLETE' -or $ChangelogHasEntry -eq 'INCOMPLETE' -or $ReleaseRecord -eq 'INCOMPLETE') {
        $rank = 1
    }
    if ($Alignment -eq 'FAIL' -or $ChangelogHasEntry -eq 'FAIL') { $rank = 2 }
    if ($ReleaseRecord -eq 'ABSENT' -or $ReleaseRecord -eq 'PUBLISHED') { $rank = 2 }
    if ($rank -eq 2) { return 'FAIL' }
    if ($rank -eq 1) { return 'INCOMPLETE' }
    if ($Alignment -eq 'PASS' -and $ChangelogHasEntry -eq 'PASS' -and $ReleaseRecord -eq 'PENDING') {
        return 'PASS'
    }
    return 'FAIL'
}

function Test-PublishImageWorkflowContract {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return 'INCOMPLETE' }
    $lines = ConvertTo-WorkflowActiveLines -Text $Text
    $dispatch = $script:EventWorkflowDispatch
    if (-not (Test-WorkflowYamlPath -Lines $lines -Keys @('on', $dispatch, 'inputs', 'source_sha'))) { return 'FAIL' }
    if (-not (Test-WorkflowYamlPathValue -Lines $lines -Keys @('on', $dispatch, 'inputs', 'source_sha', 'required') -Value 'true')) { return 'FAIL' }
    if (-not (Test-WorkflowYamlPath -Lines $lines -Keys @('on', $dispatch, 'inputs', 'release_version'))) { return 'FAIL' }
    if (-not (Test-WorkflowYamlPathValue -Lines $lines -Keys @('on', $dispatch, 'inputs', 'release_version', 'required') -Value 'true')) { return 'FAIL' }
    if (-not (Test-WorkflowJobYamlKeyValue -Lines $lines -Job 'publish' -Key 'environment' -Value 'release')) { return 'FAIL' }
    if (-not (Test-WorkflowJobYamlKeyValue -Lines $lines -Job 'publish' -Key 'packages' -Value 'write')) { return 'FAIL' }
    $writeCount = Get-WorkflowYamlKeyValueCount -Lines $lines -Key 'packages' -Value 'write'
    if ($writeCount -ne 1) { return 'FAIL' }
    if (-not (Test-WorkflowExecutableLineHasAll -Lines $lines -Job 'publish' -Needles @('GITHUB_REF', '==', 'refs/heads/main'))) { return 'FAIL' }
    if (-not (Test-WorkflowExecutableContains -Lines $lines -Job 'publish' -Needle 'GITHUB_WORKFLOW_REF')) { return 'FAIL' }
    if (-not (Test-WorkflowExecutableContains -Lines $lines -Job 'publish' -Needle 'publish-release-image.yml@refs/heads/main')) { return 'FAIL' }
    if (-not (Test-WorkflowExecutableLineHasAll -Lines $lines -Job 'publish' -Needles @('REQUESTED_SOURCE_SHA', '==', 'GITHUB_SHA'))) { return 'FAIL' }
    if (-not (Test-WorkflowScriptInvocation -Lines $lines -Job 'publish' -ScriptPath 'scripts/release-image-build-smoke.sh')) { return 'FAIL' }
    if (-not (Test-WorkflowScriptInvocation -Lines $lines -Job 'publish' -ScriptPath 'scripts/check-release-image-reproducibility.sh')) { return 'FAIL' }
    if (-not (Test-WorkflowScriptInvocation -Lines $lines -Job 'publish' -ScriptPath 'scripts/publish-release-image.sh')) { return 'FAIL' }
    if (-not (Test-WorkflowJobKeyExists -Lines $lines -Job 'verify-public-image')) { return 'FAIL' }
    if (-not (Test-WorkflowScriptInvocation -Lines $lines -Job 'verify-public-image' -ScriptPath 'scripts/verify-published-release-image.sh')) { return 'FAIL' }
    return 'PASS'
}

function Test-PublishContractsWorkflowContract {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return 'INCOMPLETE' }
    $lines = ConvertTo-WorkflowActiveLines -Text $Text
    $dispatch = $script:EventWorkflowDispatch
    if (-not (Test-WorkflowYamlPath -Lines $lines -Keys @('on', $dispatch))) { return 'FAIL' }
    if (-not (Test-WorkflowJobYamlKeyValue -Lines $lines -Job 'publish' -Key 'environment' -Value 'release')) { return 'FAIL' }
    if (-not (Test-WorkflowExecutableLineHasAll -Lines $lines -Job 'publish' -Needles @('GITHUB_REF_TYPE', '!= "tag"'))) { return 'FAIL' }
    if (-not (Test-WorkflowExecutableContains -Lines $lines -Job 'publish' -Needle 'v(0|[1-9][0-9]*)')) { return 'FAIL' }
    if (-not (Test-WorkflowExecutableContains -Lines $lines -Job 'publish' -Needle '^{commit}')) { return 'FAIL' }
    if (-not (Test-WorkflowExecutableLineHasAll -Lines $lines -Job 'publish' -Needles @('revision', '!=', 'tag_commit'))) { return 'FAIL' }
    if (-not (Test-WorkflowExecutableLineHasAll -Lines $lines -Job 'publish' -Needles @('revision', '!=', 'event_commit'))) { return 'FAIL' }
    if (-not (Test-WorkflowExecutableContains -Lines $lines -Job 'publish' -Needle 'getProperty:Version')) { return 'FAIL' }
    if (-not (Test-WorkflowExecutableLineHasAll -Lines $lines -Job 'publish' -Needles @('project_version', '!=', 'package_version'))) { return 'FAIL' }
    if (-not (Test-WorkflowExecutableLineHasAll -Lines $lines -Job 'publish' -Needles @($script:NeedleDotnetNugetPush, '.nupkg'))) { return 'FAIL' }
    if (-not (Test-WorkflowExecutableLineHasAll -Lines $lines -Job 'publish' -Needles @($script:NeedleDotnetNugetPush, '.snupkg'))) { return 'FAIL' }
    return 'PASS'
}

function Test-VerifyPublicImageWorkflowContract {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return 'INCOMPLETE' }
    $lines = ConvertTo-WorkflowActiveLines -Text $Text
    $dispatch = $script:EventWorkflowDispatch
    if (-not (Test-WorkflowYamlPathValue -Lines $lines -Keys @('on', $dispatch, 'inputs', 'publication_run_id', 'required') -Value 'true')) { return 'FAIL' }
    if (-not (Test-WorkflowYamlPathValue -Lines $lines -Keys @('on', $dispatch, 'inputs', 'publication_source_sha', 'required') -Value 'true')) { return 'FAIL' }
    if (-not (Test-WorkflowYamlPathValue -Lines $lines -Keys @('on', $dispatch, 'inputs', 'release_version', 'required') -Value 'true')) { return 'FAIL' }
    if (-not (Test-WorkflowYamlPathValue -Lines $lines -Keys @('on', $dispatch, 'inputs', 'expected_digest', 'required') -Value 'true')) { return 'FAIL' }
    if (-not (Test-WorkflowYamlKeyValueExists -Lines $lines -Key 'contents' -Value 'read')) { return 'FAIL' }
    if (-not (Test-WorkflowYamlKeyValueExists -Lines $lines -Key 'actions' -Value 'read')) { return 'FAIL' }
    if ((Get-WorkflowYamlKeyValueCount -Lines $lines -Key 'packages' -Value 'write') -gt 0) { return 'FAIL' }
    $forbidden = @(
        'docker/login-action',
        $script:NeedleDockerLogin,
        $script:NeedleDockerPush,
        'crane push',
        'crane copy'
    )
    if (Test-WorkflowActiveHasAnyNeedle -Lines $lines -Needles $forbidden) { return 'FAIL' }
    if (-not (Test-WorkflowExecutableLineHasAll -Lines $lines -Needles @('GITHUB_REF', '==', 'refs/heads/main'))) { return 'FAIL' }
    if (-not (Test-WorkflowExecutableContains -Lines $lines -Needle 'verify-public-release-image.yml@refs/heads/main')) { return 'FAIL' }
    if (-not (Test-WorkflowScriptInvocation -Lines $lines -Job 'verify-public-image' -ScriptPath 'scripts/verify-published-release-image.sh')) { return 'FAIL' }
    if (-not (Test-WorkflowFailClosedEqualityBinding -Lines $lines -Job 'verify-public-image' -LeftVariable 'identity_source_sha' -RightVariable 'SOURCE_SHA')) { return 'FAIL' }
    if (-not (Test-WorkflowFailClosedEqualityBinding -Lines $lines -Job 'verify-public-image' -LeftVariable 'identity_version' -RightVariable 'MAILER_VERSION')) { return 'FAIL' }
    if (-not (Test-WorkflowFailClosedEqualityBinding -Lines $lines -Job 'verify-public-image' -LeftVariable 'identity_digest' -RightVariable 'EXPECTED_DIGEST')) { return 'FAIL' }
    return 'PASS'
}

function ConvertTo-WorkflowDispatchRunObservation {
    param(
        $Runs,
        [string]$WorkflowPath,
        [string]$ReleaseCommitSha
    )
    if ($null -eq $Runs) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Id = 'NONE'; Status = 'NONE'; Conclusion = 'NONE'; Reason = 'RUNS_NULL' }
    }
    $matched = New-Object System.Collections.Generic.List[object]
    foreach ($run in @($Runs)) {
        $event = [string]$run.Event
        $head = [string]$run.HeadSha
        $path = [string]$run.Path
        if ($event -ne $script:EventWorkflowDispatch) { continue }
        if ($head -ne $ReleaseCommitSha) { continue }
        if ($path -ne $WorkflowPath) { continue }
        [void]$matched.Add($run)
    }
    if ($matched.Count -eq 0) {
        return [pscustomobject]@{ State = 'ABSENT'; Id = 'NONE'; Status = 'NONE'; Conclusion = 'NONE'; Reason = '' }
    }
    if ($matched.Count -eq 1) {
        $one = $matched[0]
        $id = [string]$one.Id
        if ([string]::IsNullOrWhiteSpace($id)) { $id = 'NONE' }
        $status = [string]$one.Status
        if ([string]::IsNullOrWhiteSpace($status)) { $status = 'NONE' }
        $conclusion = [string]$one.Conclusion
        if ([string]::IsNullOrWhiteSpace($conclusion)) { $conclusion = 'NONE' }
        return [pscustomobject]@{ State = 'CANDIDATE_PRESENT'; Id = $id; Status = $status; Conclusion = $conclusion; Reason = '' }
    }
    return [pscustomobject]@{ State = 'AMBIGUOUS'; Id = 'NONE'; Status = 'NONE'; Conclusion = 'NONE'; Reason = 'MULTIPLE' }
}

function Convert-GitHubWorkflowRunsJson {
    param([string]$Json)
    if ([string]::IsNullOrWhiteSpace($Json)) { return $null }
    try {
        $parsed = $Json | ConvertFrom-Json
    }
    catch {
        return $null
    }
    if ($null -eq $parsed) { return $null }
    $runsProp = $parsed.PSObject.Properties['workflow_runs']
    if ($null -eq $runsProp) { return ,([object[]]@()) }
    $list = New-Object System.Collections.Generic.List[object]
    foreach ($run in @($runsProp.Value)) {
        if ($null -eq $run) { continue }
        $id = ''
        $path = ''
        $event = ''
        $head = ''
        $status = ''
        $conclusion = ''
        $name = ''
        $displayTitle = ''
        if ($run.PSObject.Properties['id']) { $id = [string]$run.id }
        if ($run.PSObject.Properties['path']) { $path = [string]$run.path }
        if ($run.PSObject.Properties['event']) { $event = [string]$run.event }
        if ($run.PSObject.Properties['head_sha']) { $head = [string]$run.head_sha }
        if ($run.PSObject.Properties['status']) { $status = [string]$run.status }
        if ($run.PSObject.Properties['conclusion']) { $conclusion = [string]$run.conclusion }
        if ($run.PSObject.Properties['name']) { $name = [string]$run.name }
        if ($run.PSObject.Properties['display_title']) { $displayTitle = [string]$run.display_title }
        [void]$list.Add([pscustomobject]@{
            Id           = $id
            Path         = $path
            Event        = $event
            HeadSha      = $head
            Status       = $status
            Conclusion   = $conclusion
            Name         = $name
            DisplayTitle = $displayTitle
        })
    }
    # PS 5.1 can throw "Argument types do not match" on @($List[object]).
    return ,($list.ToArray())
}

function Get-CollisionRank {
    param([string]$State)
    if ($State -eq 'PRESENT' -or $State -eq 'CONFLICT') { return 2 }
    if ($State -eq 'INCOMPLETE') { return 1 }
    if ($State -eq 'ABSENT') { return 0 }
    return 2
}

function Get-GateRank {
    param([string]$State)
    if ($State -eq 'FAIL') { return 2 }
    if ($State -eq 'INCOMPLETE') { return 1 }
    if ($State -eq 'PASS') { return 0 }
    return 2
}

function Get-RunRank {
    param([string]$State)
    if ($State -eq 'CANDIDATE_PRESENT' -or $State -eq 'AMBIGUOUS') { return 2 }
    if ($State -eq 'INCOMPLETE') { return 1 }
    if ($State -eq 'ABSENT') { return 0 }
    return 2
}

function Get-ReleasePreflightDerivedStatus {
    param($Facts)

    $rank = 0
    $rank = [Math]::Max($rank, (Get-GateRank -State $Facts.SourceBinding))
    $rank = [Math]::Max($rank, (Get-GateRank -State $Facts.VersionPrep))
    $rank = [Math]::Max($rank, (Get-CollisionRank -State $Facts.CollisionGitTag))
    $rank = [Math]::Max($rank, (Get-CollisionRank -State $Facts.CollisionGitHubRelease))
    $rank = [Math]::Max($rank, (Get-CollisionRank -State $Facts.CollisionGhcrVersion))
    $rank = [Math]::Max($rank, (Get-CollisionRank -State $Facts.CollisionGhcrSha))
    $rank = [Math]::Max($rank, (Get-CollisionRank -State $Facts.CollisionNuget))
    $rank = [Math]::Max($rank, (Get-GateRank -State $Facts.WorkflowPublishImage))
    $rank = [Math]::Max($rank, (Get-GateRank -State $Facts.WorkflowPublishContracts))
    $rank = [Math]::Max($rank, (Get-GateRank -State $Facts.WorkflowVerifyPublicImage))
    $rank = [Math]::Max($rank, (Get-RunRank -State $Facts.ImagePublishRun))
    $rank = [Math]::Max($rank, (Get-RunRank -State $Facts.NugetPublishRun))

    $result = 'PASS'
    $ready = 'READY'
    if ($rank -eq 2) {
        $result = 'FAIL'
        $ready = 'STOP'
    }
    elseif ($rank -eq 1) {
        $result = 'INCOMPLETE'
        $ready = 'STOP'
    }

    $imageRunId = $Facts.ImagePublishRunId
    if ([string]::IsNullOrWhiteSpace($imageRunId)) { $imageRunId = 'NONE' }
    $nugetRunId = $Facts.NugetPublishRunId
    if ([string]::IsNullOrWhiteSpace($nugetRunId)) { $nugetRunId = 'NONE' }

    $map = [ordered]@{}
    $map['COMMAND'] = 'PREFLIGHT'
    $map['VERSION'] = $Facts.Version
    $map['RELEASE_COMMIT_SHA'] = $Facts.ReleaseCommitSha
    $map['PREFLIGHT_RESULT'] = $result
    $map['SOURCE_BINDING'] = $Facts.SourceBinding
    $map['VERSION_PREP'] = $Facts.VersionPrep
    $map['COLLISION_GIT_TAG'] = $Facts.CollisionGitTag
    $map['COLLISION_GITHUB_RELEASE'] = $Facts.CollisionGitHubRelease
    $map['COLLISION_GHCR_VERSION'] = $Facts.CollisionGhcrVersion
    $map['COLLISION_GHCR_SHA'] = $Facts.CollisionGhcrSha
    $map['COLLISION_NUGET'] = $Facts.CollisionNuget
    $map['WORKFLOW_PUBLISH_IMAGE'] = $Facts.WorkflowPublishImage
    $map['WORKFLOW_PUBLISH_CONTRACTS'] = $Facts.WorkflowPublishContracts
    $map['WORKFLOW_VERIFY_PUBLIC_IMAGE'] = $Facts.WorkflowVerifyPublicImage
    $map['IMAGE_PUBLISH_RUN'] = $Facts.ImagePublishRun
    $map['IMAGE_PUBLISH_RUN_ID'] = $imageRunId
    $map['NUGET_PUBLISH_RUN'] = $Facts.NugetPublishRun
    $map['NUGET_PUBLISH_RUN_ID'] = $nugetRunId
    $map['TECHNICAL_READINESS'] = $ready
    $map['HUMAN_AUTHORIZATION_REQUIRED'] = 'TRUE'
    $map['MUTATION_PERFORMED'] = 'FALSE'
    return $map
}

function Format-ReleasePreflightLines {
    param($Map)
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($key in $script:PreflightKeys) {
        $value = [string]$Map[$key]
        $value = $value -replace '[\r\n]+', ' '
        [void]$lines.Add(('{0}={1}' -f $key, $value))
    }
    return $lines
}

function Read-RepoTextFile {
    param(
        [string]$RepoRoot,
        [string]$RelativePath
    )
    $path = Join-Path $RepoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try {
        return [System.IO.File]::ReadAllText($path)
    }
    catch {
        return $null
    }
}

function Get-GitHubWorkflowDispatchRuns {
    param([string]$ReleaseCommitSha)
    $headers = Get-GitHubAuthHeaders
    $uri = $script:GitHubApiRoot + '/actions/runs?event=' + $script:EventWorkflowDispatch + '&head_sha=' + $ReleaseCommitSha + '&per_page=100'
    $resp = Invoke-ReleaseReadOnlyRequest -Uri $uri -Headers $headers
    $presence = ConvertTo-RemotePresence -StatusCode $resp.StatusCode -TransportFailure $resp.TransportFailure
    if ($presence -ne 'HTTP_OK') {
        return [pscustomobject]@{
            State  = 'INCOMPLETE'
            Runs   = $null
            Reason = ConvertTo-RemoteFailureClass -StatusCode $resp.StatusCode -TransportFailure $resp.TransportFailure -FailureClass $resp.FailureClass
        }
    }
    $runs = Convert-GitHubWorkflowRunsJson -Json $resp.BodyText
    if ($null -eq $runs) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Runs = $null; Reason = 'PARSE' }
    }
    $runCount = 0
    if ($null -ne $runs) {
        $runCount = $runs.Count
    }
    if ($runCount -ge 100) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Runs = $null; Reason = 'PAGE_CAP' }
    }
    return [pscustomobject]@{ State = 'PRESENT'; Runs = $runs; Reason = '' }
}

function Write-ReleasePreflightDiagnostics {
    param($Facts, $Map)
    Write-ReleaseStderr ('release-client: preflight VERSION={0} SHA={1} (read-only)' -f $Facts.Version, $Facts.ReleaseCommitSha)
    Write-ReleaseStderr ('release-client: SOURCE_BINDING={0} VERSION_PREP={1}' -f $Facts.SourceBinding, $Facts.VersionPrep)
    Write-ReleaseStderr ('release-client: PREFLIGHT_RESULT={0} TECHNICAL_READINESS={1}' -f $Map['PREFLIGHT_RESULT'], $Map['TECHNICAL_READINESS'])
    if ($Facts.ImagePublishRun -eq 'CANDIDATE_PRESENT') {
        Write-ReleaseStderr ('release-client: image publish run candidate id={0}' -f $Map['IMAGE_PUBLISH_RUN_ID'])
    }
    if ($Facts.NugetPublishRun -eq 'CANDIDATE_PRESENT') {
        Write-ReleaseStderr ('release-client: nuget publish run candidate id={0}' -f $Map['NUGET_PUBLISH_RUN_ID'])
    }
}

function Invoke-ReleasePreflight {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version,
        [Parameter(Mandatory = $true)]
        [string]$ReleaseCommitSha,
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        $Observers,
        [switch]$Quiet
    )

    if (-not (Test-ReleaseVersion $Version)) {
        throw 'Version must be X.Y.Z; the client does not infer or accept a v-prefix.'
    }
    if (-not (Test-ReleaseSha $ReleaseCommitSha)) {
        throw 'ReleaseCommitSha must be a lowercase 40-hex SHA; the client does not infer source.'
    }

    $local = $null
    $githubMain = $null
    $gitTag = $null
    $githubRelease = $null
    $nuget = $null
    $versions = $null
    $record = $null
    $changelogHas = 'INCOMPLETE'
    $ghcrVersion = $null
    $ghcrSha = $null
    $wfImage = 'INCOMPLETE'
    $wfContracts = 'INCOMPLETE'
    $wfVerify = 'INCOMPLETE'
    $imageRun = $null
    $nugetRun = $null

    if ($null -ne $Observers) {
        $local = & $Observers['LocalRepo'] $RepoRoot
        $githubMain = & $Observers['GitHubMain']
        $gitTag = & $Observers['GitTag'] $Version
        $githubRelease = & $Observers['GitHubRelease'] $Version
        $nuget = & $Observers['Nuget'] $Version
        $versions = & $Observers['Versions'] $RepoRoot $Version
        $record = & $Observers['ReleaseRecord'] $RepoRoot $Version
        if ($Observers.Contains('Changelog')) {
            $changelogHas = [string](& $Observers['Changelog'] $RepoRoot $Version)
        }
        if ($Observers.Contains('GhcrVersion')) {
            $ghcrVersion = & $Observers['GhcrVersion'] $Version
        }
        else {
            $ghcrVersion = New-ArtifactFact -State 'INCOMPLETE' -Reason 'OBSERVER'
        }
        if ($Observers.Contains('GhcrSha')) {
            $ghcrSha = & $Observers['GhcrSha'] $ReleaseCommitSha
        }
        else {
            $ghcrSha = New-ArtifactFact -State 'INCOMPLETE' -Reason 'OBSERVER'
        }
        if ($Observers.Contains('WorkflowImage')) {
            $wfImage = [string](& $Observers['WorkflowImage'])
        }
        if ($Observers.Contains('WorkflowContracts')) {
            $wfContracts = [string](& $Observers['WorkflowContracts'])
        }
        if ($Observers.Contains('WorkflowVerify')) {
            $wfVerify = [string](& $Observers['WorkflowVerify'])
        }
        $runList = @()
        $runFetchState = 'INCOMPLETE'
        if ($Observers.Contains('WorkflowRuns')) {
            $runFetch = & $Observers['WorkflowRuns'] $ReleaseCommitSha
            if ($runFetch -is [string]) {
                $runFetchState = [string]$runFetch
                $runList = @()
            }
            elseif ($null -eq $runFetch) {
                # PowerShell unwraps an empty array return from a scriptblock to $null.
                $runFetchState = 'PRESENT'
                $runList = @()
            }
            elseif ($null -ne $runFetch.PSObject.Properties['Runs']) {
                $runFetchState = [string]$runFetch.State
                if ([string]::IsNullOrWhiteSpace($runFetchState)) { $runFetchState = 'PRESENT' }
                if ($null -ne $runFetch.Runs) {
                    $runList = @($runFetch.Runs)
                }
            }
            else {
                $runFetchState = 'PRESENT'
                $runList = @($runFetch)
            }
        }
        if ($runFetchState -eq 'INCOMPLETE') {
            $imageRun = [pscustomobject]@{ State = 'INCOMPLETE'; Id = 'NONE' }
            $nugetRun = [pscustomobject]@{ State = 'INCOMPLETE'; Id = 'NONE' }
        }
        else {
            $imageRun = ConvertTo-WorkflowDispatchRunObservation -Runs $runList -WorkflowPath '.github/workflows/publish-release-image.yml' -ReleaseCommitSha $ReleaseCommitSha
            $nugetRun = ConvertTo-WorkflowDispatchRunObservation -Runs $runList -WorkflowPath '.github/workflows/publish-contracts.yml' -ReleaseCommitSha $ReleaseCommitSha
        }
    }
    else {
        $local = Get-LocalRepoObservation -RepoRoot $RepoRoot
        $githubMain = Get-GitHubMainObservation
        $gitTag = Get-GitTagObservation -Version $Version
        $githubRelease = Get-GitHubReleaseObservation -Version $Version
        $nuget = Get-NugetObservation -Version $Version
        $versions = Get-RepoVersionObservation -RepoRoot $RepoRoot -Version $Version
        $record = Get-ReleaseRecordObservation -RepoRoot $RepoRoot -Version $Version
        $changelogText = Read-RepoTextFile -RepoRoot $RepoRoot -RelativePath 'CHANGELOG.md'
        if ($null -eq $changelogText) {
            $changelogHas = 'INCOMPLETE'
        }
        else {
            $hasEntry = Test-ChangelogHasReleaseEntry -Text $changelogText -Version $Version
            if ($null -eq $hasEntry) { $changelogHas = 'INCOMPLETE' }
            elseif ($hasEntry) { $changelogHas = 'PASS' }
            else { $changelogHas = 'FAIL' }
        }
        $token = Get-GhcrPullToken
        if ([string]::IsNullOrWhiteSpace($token)) {
            $ghcrVersion = New-ArtifactFact -State 'INCOMPLETE' -Reason 'GHCR_TOKEN'
            $ghcrSha = New-ArtifactFact -State 'INCOMPLETE' -Reason 'GHCR_TOKEN'
        }
        else {
            $ghcrVersion = Get-GhcrManifestFact -Reference ('v' + $Version) -Token $token
            $ghcrSha = Get-GhcrManifestFact -Reference ('sha-' + $ReleaseCommitSha) -Token $token
        }
        $imageText = Read-RepoTextFile -RepoRoot $RepoRoot -RelativePath '.github/workflows/publish-release-image.yml'
        $contractsText = Read-RepoTextFile -RepoRoot $RepoRoot -RelativePath '.github/workflows/publish-contracts.yml'
        $verifyText = Read-RepoTextFile -RepoRoot $RepoRoot -RelativePath '.github/workflows/verify-public-release-image.yml'
        $wfImage = Test-PublishImageWorkflowContract -Text $imageText
        $wfContracts = Test-PublishContractsWorkflowContract -Text $contractsText
        $wfVerify = Test-VerifyPublicImageWorkflowContract -Text $verifyText
        $runFetch = Get-GitHubWorkflowDispatchRuns -ReleaseCommitSha $ReleaseCommitSha
        if ($runFetch.State -eq 'INCOMPLETE') {
            $imageRun = [pscustomobject]@{ State = 'INCOMPLETE'; Id = 'NONE' }
            $nugetRun = [pscustomobject]@{ State = 'INCOMPLETE'; Id = 'NONE' }
        }
        else {
            $imageRun = ConvertTo-WorkflowDispatchRunObservation -Runs $runFetch.Runs -WorkflowPath '.github/workflows/publish-release-image.yml' -ReleaseCommitSha $ReleaseCommitSha
            $nugetRun = ConvertTo-WorkflowDispatchRunObservation -Runs $runFetch.Runs -WorkflowPath '.github/workflows/publish-contracts.yml' -ReleaseCommitSha $ReleaseCommitSha
        }
    }

    if ($null -eq $githubMain) {
        $githubMain = [pscustomobject]@{ State = 'INCOMPLETE'; Sha = '' }
    }
    if ($null -eq $gitTag) { $gitTag = New-ArtifactFact -State 'INCOMPLETE' }
    if ($null -eq $githubRelease) { $githubRelease = New-ArtifactFact -State 'INCOMPLETE' }
    if ($null -eq $nuget) { $nuget = New-ArtifactFact -State 'INCOMPLETE' }
    if ($null -eq $ghcrVersion) { $ghcrVersion = New-ArtifactFact -State 'INCOMPLETE' }
    if ($null -eq $ghcrSha) { $ghcrSha = New-ArtifactFact -State 'INCOMPLETE' }
    if ($null -eq $versions) { $versions = [pscustomobject]@{ Alignment = 'INCOMPLETE' } }
    if ($null -eq $record) { $record = 'INCOMPLETE' }
    if ($null -eq $imageRun) { $imageRun = [pscustomobject]@{ State = 'INCOMPLETE'; Id = 'NONE' } }
    if ($null -eq $nugetRun) { $nugetRun = [pscustomobject]@{ State = 'INCOMPLETE'; Id = 'NONE' } }

    $sourceBinding = Get-ReleaseSourceBinding -Local $local -GitHubMainState $githubMain.State -GitHubMainSha $githubMain.Sha -ReleaseCommitSha $ReleaseCommitSha
    $versionPrep = Get-VersionPrepState -Alignment $versions.Alignment -ChangelogHasEntry $changelogHas -ReleaseRecord $record

    $facts = [pscustomobject]@{
        Version                     = $Version
        ReleaseCommitSha            = $ReleaseCommitSha
        SourceBinding               = $sourceBinding
        VersionPrep                 = $versionPrep
        CollisionGitTag             = $gitTag.State
        CollisionGitHubRelease      = $githubRelease.State
        CollisionGhcrVersion        = $ghcrVersion.State
        CollisionGhcrSha            = $ghcrSha.State
        CollisionNuget              = $nuget.State
        WorkflowPublishImage        = $wfImage
        WorkflowPublishContracts    = $wfContracts
        WorkflowVerifyPublicImage   = $wfVerify
        ImagePublishRun             = $imageRun.State
        ImagePublishRunId           = $imageRun.Id
        NugetPublishRun             = $nugetRun.State
        NugetPublishRunId           = $nugetRun.Id
    }

    $map = Get-ReleasePreflightDerivedStatus -Facts $facts
    if (-not $Quiet) {
        Write-ReleasePreflightDiagnostics -Facts $facts -Map $map
        foreach ($line in (Format-ReleasePreflightLines -Map $map)) {
            [Console]::Out.WriteLine($line)
        }
    }
    return $map
}

$script:VerifyKeys = @(
    'COMMAND',
    'VERSION',
    'RELEASE_COMMIT_SHA',
    'VERIFY_RESULT',
    'GIT_TAG',
    'CONTRACTS_SOURCE',
    'OPENAPI',
    'NUGET_PACKAGE',
    'NUGET_SOURCE_REVISION',
    'NUGET_PUBLIC',
    'NUGET_VERSION',
    'NUGET_REPOSITORY_REVISION',
    'NUGET_SYMBOLS',
    'NUGET_PUBLIC_OBSERVED_AT_UTC',
    'GHCR_VERSION_TAG',
    'GHCR_SHA_TAG',
    'GHCR_DIGEST_BINDING',
    'OCI_REVISION',
    'OCI_VERSION',
    'GITHUB_RELEASE',
    'RELEASE_RECORD',
    'PUBLIC_DIGEST',
    'MUTATION_PERFORMED'
)

function Get-VerifyIdentityRank {
    param([string]$State)
    if ($State -eq 'EXACT_MATCH') { return 0 }
    if ($State -eq 'INCOMPLETE') { return 1 }
    return 2
}

function ConvertTo-GitTagVerifyState {
    param(
        $TagFact,
        [string]$ReleaseCommitSha
    )
    if ($null -eq $TagFact) { return 'INCOMPLETE' }
    if ($TagFact.State -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if ($TagFact.State -eq 'ABSENT') { return 'ABSENT' }
    if ($TagFact.State -eq 'CONFLICT') { return 'CONFLICT' }
    if ($TagFact.State -eq 'PRESENT') {
        if ((Test-ReleaseSha $TagFact.TargetSha) -and $TagFact.TargetSha -eq $ReleaseCommitSha) {
            return 'EXACT_MATCH'
        }
        return 'CONFLICT'
    }
    return 'INCOMPLETE'
}

function ConvertTo-GitHubReleaseVerifyState {
    param($ReleaseFact)
    if ($null -eq $ReleaseFact) { return 'INCOMPLETE' }
    if ($ReleaseFact.State -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if ($ReleaseFact.State -eq 'ABSENT') { return 'ABSENT' }
    if ($ReleaseFact.State -eq 'CONFLICT') { return 'CONFLICT' }
    if ($ReleaseFact.State -eq 'PRESENT') { return 'EXACT_MATCH' }
    return 'INCOMPLETE'
}

function ConvertTo-NugetPackageVerifyState {
    param($NugetFact)
    if ($null -eq $NugetFact) { return 'INCOMPLETE' }
    if ($NugetFact.State -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if ($NugetFact.State -eq 'ABSENT') { return 'ABSENT' }
    if ($NugetFact.State -eq 'CONFLICT') { return 'CONFLICT' }
    if ($NugetFact.State -eq 'PRESENT') { return 'EXACT_MATCH' }
    return 'INCOMPLETE'
}

function ConvertTo-SourceVersionVerifyState {
    param(
        [string]$ObservedVersion,
        [string]$ExpectedVersion,
        [string]$FetchState
    )
    if ($FetchState -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if ($FetchState -eq 'ABSENT') { return 'ABSENT' }
    if ([string]::IsNullOrWhiteSpace($ObservedVersion)) { return 'INCOMPLETE' }
    if ($ObservedVersion -eq $ExpectedVersion) { return 'EXACT_MATCH' }
    return 'CONFLICT'
}

function ConvertTo-NugetRevisionVerifyState {
    param(
        [string]$PackageState,
        [string]$ObservedCommit,
        [string]$ExpectedCommit,
        [string]$FetchState
    )
    if ($PackageState -eq 'ABSENT') { return 'ABSENT' }
    if ($PackageState -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if ($FetchState -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if ($FetchState -eq 'ABSENT') { return 'ABSENT' }
    if (-not (Test-ReleaseSha $ObservedCommit)) { return 'INCOMPLETE' }
    if ($ObservedCommit -eq $ExpectedCommit) { return 'EXACT_MATCH' }
    return 'CONFLICT'
}

function ConvertTo-GhcrTagVerifyState {
    param($Fact)
    if ($null -eq $Fact) { return 'INCOMPLETE' }
    if ($Fact.State -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if ($Fact.State -eq 'ABSENT') { return 'ABSENT' }
    if ($Fact.State -eq 'CONFLICT') { return 'CONFLICT' }
    if ($Fact.State -eq 'PRESENT') {
        if (Test-ReleaseDigest $Fact.Digest) { return 'EXACT_MATCH' }
        return 'INCOMPLETE'
    }
    return 'INCOMPLETE'
}

function ConvertTo-GhcrShaTagVerifyState {
    param($VersionFact, $ShaTagState, [string]$ShaTagDigest)
    if ($null -eq $VersionFact) { return 'INCOMPLETE' }
    if ($VersionFact.State -eq 'ABSENT') { return 'ABSENT' }
    if ($VersionFact.State -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if ([string]::IsNullOrWhiteSpace($ShaTagState)) { return 'INCOMPLETE' }
    if ($ShaTagState -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if ($ShaTagState -eq 'ABSENT') { return 'ABSENT' }
    if ($ShaTagState -eq 'CONFLICT') { return 'CONFLICT' }
    if ($ShaTagState -eq 'PRESENT') {
        if (Test-ReleaseDigest $ShaTagDigest) { return 'EXACT_MATCH' }
        return 'INCOMPLETE'
    }
    return 'INCOMPLETE'
}

function ConvertTo-GhcrDigestBindingVerifyState {
    param(
        $VersionFact,
        [string]$ShaTagState,
        [string]$ShaTagDigest
    )
    if ($null -eq $VersionFact) { return 'INCOMPLETE' }
    if ($VersionFact.State -eq 'ABSENT' -or $ShaTagState -eq 'ABSENT') { return 'ABSENT' }
    if ($VersionFact.State -eq 'INCOMPLETE' -or $ShaTagState -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if ($VersionFact.State -eq 'CONFLICT' -or $ShaTagState -eq 'CONFLICT') { return 'CONFLICT' }
    if ($VersionFact.State -eq 'PRESENT' -and $ShaTagState -eq 'PRESENT') {
        if (-not (Test-ReleaseDigest $VersionFact.Digest) -or -not (Test-ReleaseDigest $ShaTagDigest)) {
            return 'INCOMPLETE'
        }
        if ($VersionFact.Digest -eq $ShaTagDigest) { return 'EXACT_MATCH' }
        return 'CONFLICT'
    }
    return 'INCOMPLETE'
}

function ConvertTo-OciRevisionVerifyState {
    param(
        $VersionFact,
        [string]$ReleaseCommitSha
    )
    if ($null -eq $VersionFact) { return 'INCOMPLETE' }
    if ($VersionFact.State -eq 'ABSENT') { return 'ABSENT' }
    if ($VersionFact.State -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if ($VersionFact.State -eq 'CONFLICT') { return 'CONFLICT' }
    if ($VersionFact.State -eq 'PRESENT') {
        if (-not (Test-ReleaseSha $VersionFact.Revision)) { return 'INCOMPLETE' }
        if ($VersionFact.Revision -eq $ReleaseCommitSha) { return 'EXACT_MATCH' }
        return 'CONFLICT'
    }
    return 'INCOMPLETE'
}

function ConvertTo-OciVersionVerifyState {
    param(
        $VersionFact,
        [string]$ExpectedVersion
    )
    if ($null -eq $VersionFact) { return 'INCOMPLETE' }
    if ($VersionFact.State -eq 'ABSENT') { return 'ABSENT' }
    if ($VersionFact.State -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if ($VersionFact.State -eq 'CONFLICT') { return 'CONFLICT' }
    if ($VersionFact.State -eq 'PRESENT') {
        if ([string]::IsNullOrWhiteSpace($VersionFact.OciVersion)) { return 'INCOMPLETE' }
        if ($VersionFact.OciVersion -eq $ExpectedVersion) { return 'EXACT_MATCH' }
        return 'CONFLICT'
    }
    return 'INCOMPLETE'
}

function ConvertTo-ReleaseRecordVerifyState {
    param(
        [string]$FetchState,
        [string]$Text,
        [string]$Version,
        [string]$ReleaseCommitSha,
        [string]$ObservedDigest
    )
    if ($FetchState -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if ($FetchState -eq 'ABSENT') { return 'ABSENT' }
    $recordState = Get-ReleaseRecordStateFromText -Text $Text
    if ($recordState -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if ($recordState -ne 'PUBLISHED') { return 'CONFLICT' }
    $recordSha = Get-ReleaseRecordCommitShaFromText -Text $Text
    if (-not (Test-ReleaseSha $recordSha)) { return 'INCOMPLETE' }
    if ($recordSha -ne $ReleaseCommitSha) { return 'CONFLICT' }
    if (Test-ReleaseDigest $ObservedDigest) {
        $recordDigest = Get-ReleaseRecordDigestFromText -Text $Text
        if (-not (Test-ReleaseDigest $recordDigest)) { return 'INCOMPLETE' }
        if ($recordDigest -ne $ObservedDigest) { return 'CONFLICT' }
    }
    return 'EXACT_MATCH'
}

function Get-GitHubFileContentAtRef {
    param(
        [string]$RelativePath,
        [string]$Ref,
        $Request
    )
    $path = $RelativePath.Replace('\', '/').TrimStart('/')
    $uri = $script:GitHubApiRoot + '/contents/' + $path + '?ref=' + $Ref
    $headers = Get-GitHubAuthHeaders
    $resp = if ($null -ne $Request) {
        & $Request $uri $headers
    }
    else {
        Invoke-ReleaseReadOnlyRequest -Uri $uri -Headers $headers
    }
    $presence = ConvertTo-RemotePresence -StatusCode $resp.StatusCode -TransportFailure $resp.TransportFailure
    if ($presence -eq 'ABSENT') {
        return [pscustomobject]@{ State = 'ABSENT'; Text = ''; Reason = '' }
    }
    if ($presence -ne 'HTTP_OK') {
        return [pscustomobject]@{
            State  = 'INCOMPLETE'
            Text   = ''
            Reason = (ConvertTo-RemoteFailureClass -StatusCode $resp.StatusCode -TransportFailure $resp.TransportFailure -FailureClass $resp.FailureClass)
        }
    }
    try {
        $parsed = $resp.BodyText | ConvertFrom-Json
    }
    catch {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Text = ''; Reason = 'JSON' }
    }
    if ($null -eq $parsed -or [string]::IsNullOrWhiteSpace([string]$parsed.content)) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Text = ''; Reason = 'CONTENT_PARSE' }
    }
    $encoded = ([string]$parsed.content) -replace '\s', ''
    try {
        $bytes = [Convert]::FromBase64String($encoded)
        $text = [System.Text.Encoding]::UTF8.GetString($bytes)
        return [pscustomobject]@{ State = 'PRESENT'; Text = $text; Reason = '' }
    }
    catch {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Text = ''; Reason = 'BASE64' }
    }
}

function Get-SourceVersionAtCommitObservation {
    param(
        [string]$ReleaseCommitSha,
        [string]$Version,
        $Request
    )
    $contractsFetch = Get-GitHubFileContentAtRef -RelativePath 'src/Amane.Mailer.Contracts/Amane.Mailer.Contracts.csproj' -Ref $ReleaseCommitSha -Request $Request
    $openapiFetch = Get-GitHubFileContentAtRef -RelativePath 'docs/api/openapi.yaml' -Ref $ReleaseCommitSha -Request $Request
    return [pscustomobject]@{
        ContractsState   = $contractsFetch.State
        ContractsVersion = (Get-ContractsVersionFromText -Text $contractsFetch.Text)
        OpenApiState     = $openapiFetch.State
        OpenApiVersion   = (Get-OpenApiVersionFromText -Text $openapiFetch.Text)
    }
}

function Get-NugetSourceRevisionObservation {
    param(
        [string]$Version,
        $Request
    )
    $nuspecName = $script:NugetPackageId + '.nuspec'
    $uri = 'https://api.nuget.org/v3-flatcontainer/' + $script:NugetPackageId + '/' + $Version + '/' + $nuspecName
    $resp = if ($null -ne $Request) {
        & $Request $uri $null
    }
    else {
        Invoke-ReleaseReadOnlyRequest -Uri $uri
    }
    $presence = ConvertTo-RemotePresence -StatusCode $resp.StatusCode -TransportFailure $resp.TransportFailure
    if ($presence -eq 'ABSENT') {
        return [pscustomobject]@{ State = 'ABSENT'; Commit = ''; Reason = '' }
    }
    if ($presence -ne 'HTTP_OK') {
        return [pscustomobject]@{
            State  = 'INCOMPLETE'
            Commit = ''
            Reason = (ConvertTo-RemoteFailureClass -StatusCode $resp.StatusCode -TransportFailure $resp.TransportFailure -FailureClass $resp.FailureClass)
        }
    }
    $commit = Get-NugetRepositoryCommitFromNuspecText -Text $resp.BodyText
    if (-not (Test-ReleaseSha $commit)) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Commit = ''; Reason = 'NUSPEC_PARSE' }
    }
    return [pscustomobject]@{ State = 'PRESENT'; Commit = $commit; Reason = '' }
}

function Get-ReleaseRecordContentForVerify {
    param(
        [string]$Version,
        [string]$ReleaseCommitSha,
        $Request,
        [string]$MainRef = ''
    )
    $relativePath = 'docs/releases/v' + $Version + '.md'
    $refs = @()
    if (-not [string]::IsNullOrWhiteSpace($MainRef)) {
        $refs += $MainRef
    }
    else {
        $main = Get-GitHubMainObservation
        if ($main.State -eq 'PRESENT' -and (Test-ReleaseSha $main.Sha)) {
            $refs += $main.Sha
        }
    }
    $refs += $ReleaseCommitSha
    $seen = @{}
    foreach ($ref in $refs) {
        if ($seen.ContainsKey($ref)) { continue }
        $seen[$ref] = $true
        $fetch = Get-GitHubFileContentAtRef -RelativePath $relativePath -Ref $ref -Request $Request
        if ($fetch.State -eq 'PRESENT') {
            $recordState = Get-ReleaseRecordStateFromText -Text $fetch.Text
            if ($recordState -eq 'PUBLISHED') {
                return $fetch
            }
            if ($ref -eq $ReleaseCommitSha) {
                return $fetch
            }
        }
        if ($fetch.State -eq 'INCOMPLETE') {
            return $fetch
        }
    }
    return [pscustomobject]@{ State = 'ABSENT'; Text = ''; Reason = '' }
}

function Get-GhcrVerifyObservation {
    param(
        [string]$Version,
        [string]$ReleaseCommitSha,
        $GhcrObserver
    )
    if ($null -ne $GhcrObserver) {
        return & $GhcrObserver $Version $ReleaseCommitSha
    }
    return Get-GhcrObservation -Version $Version -SourceSha $ReleaseCommitSha
}

function Get-ReleaseVerifyDerivedStatus {
    param($Facts)

    $rank = 0
    foreach ($key in @(
            'GitTag',
            'ContractsSource',
            'OpenApi',
            'NugetPackage',
            'NugetSourceRevision',
            'GhcrVersionTag',
            'GhcrShaTag',
            'GhcrDigestBinding',
            'OciRevision',
            'OciVersion',
            'GitHubRelease',
            'ReleaseRecord'
        )) {
        $rank = [Math]::Max($rank, (Get-VerifyIdentityRank -State $Facts.$key))
    }

    $result = 'PASS'
    if ($rank -eq 2) { $result = 'FAIL' }
    elseif ($rank -eq 1) { $result = 'INCOMPLETE' }

    $digest = 'NONE'
    if ($null -ne $Facts.PublicDigest -and (Test-ReleaseDigest $Facts.PublicDigest)) {
        $digest = $Facts.PublicDigest
    }

    $nugetPublic = 'FALSE'
    $nugetVersion = 'NONE'
    $nugetRepoRevision = 'NONE'
    $nugetSymbols = 'NONE'
    $nugetObservedAt = 'NONE'
    if ($Facts.NugetPackage -eq 'EXACT_MATCH') {
        $nugetPublic = 'TRUE'
        $nugetVersion = [string]$Facts.Version
        if ($Facts.NugetSourceRevision -eq 'EXACT_MATCH' -and (Test-ReleaseSha $Facts.NugetRepositoryRevision)) {
            $nugetRepoRevision = [string]$Facts.NugetRepositoryRevision
        }
        elseif ($Facts.NugetSourceRevision -eq 'ABSENT') {
            $nugetRepoRevision = 'NONE'
        }
        elseif ($Facts.NugetSourceRevision -eq 'INCOMPLETE' -or $Facts.NugetSourceRevision -eq 'CONFLICT') {
            $nugetRepoRevision = [string]$Facts.NugetSourceRevision
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$Facts.NugetSymbols) -and [string]$Facts.NugetSymbols -ne 'NONE') {
            $nugetSymbols = [string]$Facts.NugetSymbols
        }
        if (Test-ReleaseUtcTimestamp -Value ([string]$Facts.NugetPublicObservedAtUtc)) {
            $nugetObservedAt = [string]$Facts.NugetPublicObservedAtUtc
        }
    }
    elseif ($Facts.NugetPackage -eq 'ABSENT') {
        $nugetPublic = 'FALSE'
        $nugetVersion = 'NONE'
        $nugetRepoRevision = 'NONE'
        $nugetSymbols = 'NONE'
        $nugetObservedAt = 'NONE'
    }
    elseif ($Facts.NugetPackage -eq 'INCOMPLETE' -or $Facts.NugetPackage -eq 'CONFLICT') {
        $nugetPublic = 'INCOMPLETE'
        $nugetVersion = 'NONE'
        $nugetRepoRevision = 'NONE'
        $nugetSymbols = 'NONE'
        $nugetObservedAt = 'NONE'
    }

    $map = [ordered]@{}
    $map['COMMAND'] = 'VERIFY'
    $map['VERSION'] = $Facts.Version
    $map['RELEASE_COMMIT_SHA'] = $Facts.ReleaseCommitSha
    $map['VERIFY_RESULT'] = $result
    $map['GIT_TAG'] = $Facts.GitTag
    $map['CONTRACTS_SOURCE'] = $Facts.ContractsSource
    $map['OPENAPI'] = $Facts.OpenApi
    $map['NUGET_PACKAGE'] = $Facts.NugetPackage
    $map['NUGET_SOURCE_REVISION'] = $Facts.NugetSourceRevision
    $map['NUGET_PUBLIC'] = $nugetPublic
    $map['NUGET_VERSION'] = $nugetVersion
    $map['NUGET_REPOSITORY_REVISION'] = $nugetRepoRevision
    $map['NUGET_SYMBOLS'] = $nugetSymbols
    $map['NUGET_PUBLIC_OBSERVED_AT_UTC'] = $nugetObservedAt
    $map['GHCR_VERSION_TAG'] = $Facts.GhcrVersionTag
    $map['GHCR_SHA_TAG'] = $Facts.GhcrShaTag
    $map['GHCR_DIGEST_BINDING'] = $Facts.GhcrDigestBinding
    $map['OCI_REVISION'] = $Facts.OciRevision
    $map['OCI_VERSION'] = $Facts.OciVersion
    $map['GITHUB_RELEASE'] = $Facts.GitHubRelease
    $map['RELEASE_RECORD'] = $Facts.ReleaseRecord
    $map['PUBLIC_DIGEST'] = $digest
    $map['MUTATION_PERFORMED'] = 'FALSE'
    return $map
}

function Format-ReleaseVerifyLines {
    param($Map)
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($key in $script:VerifyKeys) {
        $value = [string]$Map[$key]
        $value = $value -replace '[\r\n]+', ' '
        [void]$lines.Add(('{0}={1}' -f $key, $value))
    }
    return $lines
}

function Write-ReleaseVerifyDiagnostics {
    param($Facts, $Map)
    Write-ReleaseStderr ('release-client: verify VERSION={0} RELEASE_COMMIT_SHA={1} (read-only)' -f $Facts.Version, $Facts.ReleaseCommitSha)
    Write-ReleaseStderr ('release-client: VERIFY_RESULT={0} PUBLIC_DIGEST={1}' -f $Map['VERIFY_RESULT'], $Map['PUBLIC_DIGEST'])
}

function Invoke-ReleaseVerify {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version,
        [Parameter(Mandatory = $true)]
        [string]$ReleaseCommitSha,
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        $Observers,
        [switch]$Quiet
    )

    if (-not (Test-ReleaseVersion $Version)) {
        throw 'Version must be X.Y.Z; the client does not infer or accept a v-prefix.'
    }
    if (-not (Test-ReleaseSha $ReleaseCommitSha)) {
        throw 'ReleaseCommitSha must be a 40-character lowercase hex git commit.'
    }

    $gitTag = $null
    $githubRelease = $null
    $nuget = $null
    $sourceVersions = $null
    $nugetRevision = $null
    $nugetSymbols = $null
    $recordFetch = $null
    $ghcr = $null

    if ($null -ne $Observers) {
        if ($Observers.Contains('GitTag')) { $gitTag = & $Observers['GitTag'] $Version }
        if ($Observers.Contains('GitHubRelease')) { $githubRelease = & $Observers['GitHubRelease'] $Version }
        if ($Observers.Contains('Nuget')) { $nuget = & $Observers['Nuget'] $Version }
        if ($Observers.Contains('SourceVersions')) { $sourceVersions = & $Observers['SourceVersions'] $ReleaseCommitSha $Version }
        if ($Observers.Contains('NugetRevision')) { $nugetRevision = & $Observers['NugetRevision'] $Version }
        if ($Observers.Contains('NugetSymbols')) { $nugetSymbols = & $Observers['NugetSymbols'] $Version }
        if ($Observers.Contains('ReleaseRecord')) { $recordFetch = & $Observers['ReleaseRecord'] $Version $ReleaseCommitSha }
        if ($Observers.Contains('Ghcr')) { $ghcr = & $Observers['Ghcr'] $Version $ReleaseCommitSha }
    }
    else {
        $gitTag = Get-GitTagObservation -Version $Version
        $githubRelease = Get-GitHubReleaseObservation -Version $Version
        $nuget = Get-NugetObservation -Version $Version
        $sourceVersions = Get-SourceVersionAtCommitObservation -ReleaseCommitSha $ReleaseCommitSha -Version $Version
        $nugetRevision = Get-NugetSourceRevisionObservation -Version $Version
        $nugetSymbols = Get-NugetSymbolsObservation -Version $Version
        $recordFetch = Get-ReleaseRecordContentForVerify -Version $Version -ReleaseCommitSha $ReleaseCommitSha
        $ghcr = Get-GhcrVerifyObservation -Version $Version -ReleaseCommitSha $ReleaseCommitSha
    }

    if ($null -eq $gitTag) { $gitTag = New-ArtifactFact -State 'INCOMPLETE' }
    if ($null -eq $githubRelease) { $githubRelease = New-ArtifactFact -State 'INCOMPLETE' }
    if ($null -eq $nuget) { $nuget = New-ArtifactFact -State 'INCOMPLETE' }
    if ($null -eq $sourceVersions) {
        $sourceVersions = [pscustomobject]@{
            ContractsState   = 'INCOMPLETE'
            ContractsVersion = ''
            OpenApiState     = 'INCOMPLETE'
            OpenApiVersion   = ''
        }
    }
    if ($null -eq $nugetRevision) {
        $nugetRevision = [pscustomobject]@{ State = 'INCOMPLETE'; Commit = ''; Reason = 'OBSERVER' }
    }
    if ($null -eq $nugetSymbols) {
        $nugetSymbols = [pscustomobject]@{ State = 'NONE'; Reason = 'OBSERVER' }
    }
    if ($null -eq $recordFetch) {
        $recordFetch = [pscustomobject]@{ State = 'INCOMPLETE'; Text = ''; Reason = 'OBSERVER' }
    }
    if ($null -eq $ghcr) { $ghcr = New-ArtifactFact -State 'INCOMPLETE' }

    $publicDigest = ''
    if ($ghcr.State -eq 'PRESENT' -and (Test-ReleaseDigest $ghcr.Digest)) {
        $publicDigest = $ghcr.Digest
    }

    $nugetSymbolsState = 'NONE'
    if ($null -ne $nugetSymbols -and -not [string]::IsNullOrWhiteSpace([string]$nugetSymbols.State)) {
        $nugetSymbolsState = [string]$nugetSymbols.State
    }
    elseif ($null -ne $nuget.SymbolsState -and -not [string]::IsNullOrWhiteSpace([string]$nuget.SymbolsState)) {
        $nugetSymbolsState = [string]$nuget.SymbolsState
    }

    $nugetObservedAt = ''
    if ($nuget.State -eq 'PRESENT' -and (Test-ReleaseUtcTimestamp -Value ([string]$nuget.ObservedAtUtc))) {
        $nugetObservedAt = [string]$nuget.ObservedAtUtc
    }

    $nugetRepoRevision = ''
    if ($nugetRevision.State -eq 'PRESENT' -and (Test-ReleaseSha $nugetRevision.Commit)) {
        $nugetRepoRevision = [string]$nugetRevision.Commit
    }

    $facts = [pscustomobject]@{
        Version                   = $Version
        ReleaseCommitSha          = $ReleaseCommitSha
        GitTag                    = (ConvertTo-GitTagVerifyState -TagFact $gitTag -ReleaseCommitSha $ReleaseCommitSha)
        ContractsSource           = (ConvertTo-SourceVersionVerifyState -ObservedVersion $sourceVersions.ContractsVersion -ExpectedVersion $Version -FetchState $sourceVersions.ContractsState)
        OpenApi                   = (ConvertTo-SourceVersionVerifyState -ObservedVersion $sourceVersions.OpenApiVersion -ExpectedVersion $Version -FetchState $sourceVersions.OpenApiState)
        NugetPackage              = (ConvertTo-NugetPackageVerifyState -NugetFact $nuget)
        NugetSourceRevision       = (ConvertTo-NugetRevisionVerifyState -PackageState $nuget.State -ObservedCommit $nugetRevision.Commit -ExpectedCommit $ReleaseCommitSha -FetchState $nugetRevision.State)
        NugetRepositoryRevision   = $nugetRepoRevision
        NugetSymbols              = $nugetSymbolsState
        NugetPublicObservedAtUtc  = $nugetObservedAt
        GhcrVersionTag            = (ConvertTo-GhcrTagVerifyState -Fact $ghcr)
        GhcrShaTag                = (ConvertTo-GhcrShaTagVerifyState -VersionFact $ghcr -ShaTagState $ghcr.ShaTagState -ShaTagDigest $ghcr.ShaTagDigest)
        GhcrDigestBinding         = (ConvertTo-GhcrDigestBindingVerifyState -VersionFact $ghcr -ShaTagState $ghcr.ShaTagState -ShaTagDigest $ghcr.ShaTagDigest)
        OciRevision               = (ConvertTo-OciRevisionVerifyState -VersionFact $ghcr -ReleaseCommitSha $ReleaseCommitSha)
        OciVersion                = (ConvertTo-OciVersionVerifyState -VersionFact $ghcr -ExpectedVersion $Version)
        GitHubRelease             = (ConvertTo-GitHubReleaseVerifyState -ReleaseFact $githubRelease)
        ReleaseRecord             = (ConvertTo-ReleaseRecordVerifyState -FetchState $recordFetch.State -Text $recordFetch.Text -Version $Version -ReleaseCommitSha $ReleaseCommitSha -ObservedDigest $publicDigest)
        PublicDigest              = $publicDigest
    }

    $map = Get-ReleaseVerifyDerivedStatus -Facts $facts
    if (-not $Quiet) {
        Write-ReleaseVerifyDiagnostics -Facts $facts -Map $map
        foreach ($line in (Format-ReleaseVerifyLines -Map $map)) {
            [Console]::Out.WriteLine($line)
        }
    }
    return $map
}

$script:PublishImageMutationKeys = @(
    'COMMAND',
    'VERSION',
    'RELEASE_COMMIT_SHA',
    'MUTATION_RESULT',
    'MUTATION_ATTEMPTED',
    'HUMAN_AUTHORIZATION_REQUIRED',
    'SOURCE_BINDING',
    'VERSION_PREP',
    'GUARD_GHCR',
    'GUARD_IMAGE_PUBLISH_RUN',
    'IMAGE_PUBLISH_RUN_ID',
    'MUTATION_PERFORMED'
)

$script:CreateTagMutationKeys = @(
    'COMMAND',
    'VERSION',
    'RELEASE_COMMIT_SHA',
    'MUTATION_RESULT',
    'MUTATION_ATTEMPTED',
    'HUMAN_AUTHORIZATION_REQUIRED',
    'GUARD_GHCR',
    'GUARD_GIT_TAG',
    'MUTATION_PERFORMED'
)

$script:PublishNugetMutationKeys = @(
    'COMMAND',
    'VERSION',
    'RELEASE_COMMIT_SHA',
    'MUTATION_RESULT',
    'MUTATION_ATTEMPTED',
    'HUMAN_AUTHORIZATION_REQUIRED',
    'GUARD_GHCR',
    'GUARD_GIT_TAG',
    'GUARD_NUGET',
    'GUARD_NUGET_PUBLISH_RUN',
    'NUGET_PUBLISH_RUN_ID',
    'MUTATION_PERFORMED'
)

$script:CreateGitHubReleaseMutationKeys = @(
    'COMMAND',
    'VERSION',
    'RELEASE_COMMIT_SHA',
    'MUTATION_RESULT',
    'MUTATION_ATTEMPTED',
    'HUMAN_AUTHORIZATION_REQUIRED',
    'GUARD_GHCR',
    'GUARD_GIT_TAG',
    'GUARD_NUGET',
    'GUARD_GITHUB_RELEASE',
    'RELEASE_NOTES_PATH',
    'MUTATION_PERFORMED'
)

function Get-MutationGuardRank {
    param([string]$State)
    if ($State -eq 'INCOMPLETE') { return 1 }
    if ($State -eq 'CONFLICT' -or $State -eq 'EXACT_MATCH') { return 2 }
    if ($State -eq 'ABSENT') { return 0 }
    return 2
}

function ConvertTo-IdentityGuardStates {
    param([string[]]$States)
    $rank = 0
    foreach ($state in $States) {
        $rank = [Math]::Max($rank, (Get-MutationGuardRank -State $state))
    }
    if ($rank -eq 2) {
        foreach ($state in $States) {
            if ($state -eq 'CONFLICT') { return 'CONFLICT' }
        }
        return 'EXACT_MATCH'
    }
    if ($rank -eq 1) { return 'INCOMPLETE' }
    foreach ($state in $States) {
        if ($state -eq 'ABSENT') { return 'ABSENT' }
    }
    return 'INCOMPLETE'
}

function ConvertTo-GhcrPrerequisiteGuardState {
    param(
        $GhcrFact,
        [string]$ReleaseCommitSha,
        [string]$Version
    )
    if ($null -eq $GhcrFact) { return 'INCOMPLETE' }
    if ($GhcrFact.State -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if ($GhcrFact.State -eq 'CONFLICT') { return 'CONFLICT' }
    if ($GhcrFact.State -eq 'ABSENT') { return 'ABSENT' }
    if ($GhcrFact.State -eq 'PRESENT') {
        $states = @(
            (ConvertTo-GhcrTagVerifyState -Fact $GhcrFact),
            (ConvertTo-GhcrShaTagVerifyState -VersionFact $GhcrFact -ShaTagState $GhcrFact.ShaTagState -ShaTagDigest $GhcrFact.ShaTagDigest),
            (ConvertTo-GhcrDigestBindingVerifyState -VersionFact $GhcrFact -ShaTagState $GhcrFact.ShaTagState -ShaTagDigest $GhcrFact.ShaTagDigest),
            (ConvertTo-OciRevisionVerifyState -VersionFact $GhcrFact -ReleaseCommitSha $ReleaseCommitSha),
            (ConvertTo-OciVersionVerifyState -VersionFact $GhcrFact -ExpectedVersion $Version)
        )
        return ConvertTo-IdentityGuardStates -States $states
    }
    return 'INCOMPLETE'
}

function ConvertTo-GhcrPublishTargetGuardState {
    param(
        $GhcrFact,
        [string]$ReleaseCommitSha,
        [string]$Version
    )
    return ConvertTo-GhcrPrerequisiteGuardState -GhcrFact $GhcrFact -ReleaseCommitSha $ReleaseCommitSha -Version $Version
}

function ConvertTo-GitTagMutationGuardState {
    param(
        $TagFact,
        [string]$ReleaseCommitSha
    )
    if ($null -eq $TagFact) { return 'INCOMPLETE' }
    if ($TagFact.State -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if ($TagFact.State -eq 'CONFLICT') { return 'CONFLICT' }
    if ($TagFact.State -eq 'ABSENT') { return 'ABSENT' }
    if ($TagFact.State -eq 'PRESENT') {
        if ((Test-ReleaseSha $TagFact.TargetSha) -and $TagFact.TargetSha -eq $ReleaseCommitSha) {
            return 'EXACT_MATCH'
        }
        return 'CONFLICT'
    }
    return 'INCOMPLETE'
}

function ConvertTo-NugetMutationGuardState {
    param($NugetFact)
    if ($null -eq $NugetFact) { return 'INCOMPLETE' }
    if ($NugetFact.State -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if ($NugetFact.State -eq 'CONFLICT') { return 'CONFLICT' }
    if ($NugetFact.State -eq 'ABSENT') { return 'ABSENT' }
    if ($NugetFact.State -eq 'PRESENT') { return 'EXACT_MATCH' }
    return 'INCOMPLETE'
}

function ConvertTo-GitHubReleaseMutationGuardState {
    param(
        $ReleaseFact,
        [string]$Version
    )
    if ($null -eq $ReleaseFact) { return 'INCOMPLETE' }
    if ($ReleaseFact.State -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if ($ReleaseFact.State -eq 'CONFLICT') { return 'CONFLICT' }
    if ($ReleaseFact.State -eq 'ABSENT') { return 'ABSENT' }
    if ($ReleaseFact.State -eq 'PRESENT') {
        $tagName = 'v' + $Version
        if ($ReleaseFact.Reason -eq 'TAG_NAME') { return 'CONFLICT' }
        if ($ReleaseFact.Reason -eq 'DRAFT' -or $ReleaseFact.Reason -eq 'PRERELEASE') { return 'CONFLICT' }
        return 'EXACT_MATCH'
    }
    return 'INCOMPLETE'
}

function ConvertTo-WorkflowRunMutationGuardState {
    param([string]$RunState)
    if ($RunState -eq 'ABSENT') { return 'ABSENT' }
    if ($RunState -eq 'CANDIDATE_PRESENT') { return 'EXACT_MATCH' }
    if ($RunState -eq 'AMBIGUOUS') { return 'CONFLICT' }
    if ($RunState -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    return 'CONFLICT'
}

function Test-ReleaseMutationPreflightEligible {
    param($PreflightMap)
    if ($null -eq $PreflightMap) { return $false }
    if ($PreflightMap['PREFLIGHT_RESULT'] -ne 'PASS') { return $false }
    if ($PreflightMap['TECHNICAL_READINESS'] -ne 'READY') { return $false }
    return $true
}

function ConvertTo-PreflightMutationGuardState {
    param($PreflightMap)
    if ($null -eq $PreflightMap) { return 'INCOMPLETE' }
    if ($PreflightMap['PREFLIGHT_RESULT'] -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if (-not (Test-ReleaseMutationPreflightEligible -PreflightMap $PreflightMap)) { return 'CONFLICT' }
    return 'ABSENT'
}

function Resolve-ReleaseMutationPrecheck {
    param(
        [string[]]$GuardStates,
        [string]$TargetGuardState,
        [string[]]$PrerequisiteGuardStates = @(),
        [bool]$Execute,
        [string]$ReleaseNotesGuard = ''
    )

    foreach ($pre in @($PrerequisiteGuardStates)) {
        if ($pre -eq 'EXACT_MATCH') { continue }
        if ($pre -eq 'INCOMPLETE') {
            return [pscustomobject]@{
                Result    = 'INCOMPLETE'
                Attempted = 'FALSE'
                Performed = 'FALSE'
            }
        }
        return [pscustomobject]@{
            Result    = 'CONFLICT'
            Attempted = 'FALSE'
            Performed = 'FALSE'
        }
    }

    $rank = 0
    foreach ($state in @($GuardStates)) {
        $rank = [Math]::Max($rank, (Get-MutationGuardRank -State $state))
    }
    $targetRank = Get-MutationGuardRank -State $TargetGuardState

    if (-not [string]::IsNullOrWhiteSpace($ReleaseNotesGuard)) {
        if ($ReleaseNotesGuard -eq 'ABSENT') {
            return [pscustomobject]@{
                Result    = 'NOT_ATTEMPTED'
                Attempted = 'FALSE'
                Performed = 'FALSE'
            }
        }
        if ($ReleaseNotesGuard -eq 'INCOMPLETE') {
            return [pscustomobject]@{
                Result    = 'INCOMPLETE'
                Attempted = 'FALSE'
                Performed = 'FALSE'
            }
        }
    }

    if ($TargetGuardState -eq 'EXACT_MATCH') {
        return [pscustomobject]@{
            Result    = 'ALREADY_APPLIED'
            Attempted = 'FALSE'
            Performed = 'FALSE'
        }
    }

    foreach ($state in @($GuardStates)) {
        if ($state -eq 'EXACT_MATCH') {
            return [pscustomobject]@{
                Result    = 'ALREADY_APPLIED'
                Attempted = 'FALSE'
                Performed = 'FALSE'
            }
        }
    }

    if ($rank -eq 1 -or $TargetGuardState -eq 'INCOMPLETE') {
        return [pscustomobject]@{
            Result    = 'INCOMPLETE'
            Attempted = 'FALSE'
            Performed = 'FALSE'
        }
    }

    if ($rank -eq 2 -or $TargetGuardState -eq 'CONFLICT') {
        return [pscustomobject]@{
            Result    = 'CONFLICT'
            Attempted = 'FALSE'
            Performed = 'FALSE'
        }
    }

    if (-not $Execute) {
        return [pscustomobject]@{
            Result    = 'NOT_ATTEMPTED'
            Attempted = 'FALSE'
            Performed = 'FALSE'
        }
    }

    if ($TargetGuardState -eq 'ABSENT') {
        return $null
    }

    return [pscustomobject]@{
        Result    = 'CONFLICT'
        Attempted = 'FALSE'
        Performed = 'FALSE'
    }
}

function Resolve-ReleaseCreateGitHubReleasePostReadBackGuard {
    param(
        [string]$ReleaseGuard,
        [string]$TagGuard
    )
    if ($ReleaseGuard -eq 'INCOMPLETE' -or $TagGuard -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if ($ReleaseGuard -eq 'ABSENT' -or $TagGuard -eq 'ABSENT') { return 'INCOMPLETE' }
    if ($ReleaseGuard -eq 'CONFLICT' -or $TagGuard -eq 'CONFLICT') { return 'CONFLICT' }
    if ($ReleaseGuard -eq 'EXACT_MATCH' -and $TagGuard -eq 'EXACT_MATCH') { return 'EXACT_MATCH' }
    return 'INCOMPLETE'
}

function Resolve-ReleaseMutationPostAttempt {
    param(
        [string]$ExecutorState,
        [string]$ReadBackGuardState,
        [string]$TargetGuardState
    )
    if ($ExecutorState -eq 'AMBIGUOUS') {
        return [pscustomobject]@{
            Result    = 'AMBIGUOUS_AFTER_ATTEMPT'
            Attempted = 'TRUE'
            Performed = 'UNKNOWN'
        }
    }
    if ($ExecutorState -ne 'SUCCESS') {
        return [pscustomobject]@{
            Result    = 'INCOMPLETE'
            Attempted = 'TRUE'
            Performed = 'UNKNOWN'
        }
    }
    if ($ReadBackGuardState -eq 'EXACT_MATCH') {
        return [pscustomobject]@{
            Result    = 'APPLIED'
            Attempted = 'TRUE'
            Performed = 'TRUE'
        }
    }
    if ($ReadBackGuardState -eq 'INCOMPLETE' -or $ReadBackGuardState -eq 'ABSENT') {
        return [pscustomobject]@{
            Result    = 'INCOMPLETE'
            Attempted = 'TRUE'
            Performed = 'UNKNOWN'
        }
    }
    return [pscustomobject]@{
        Result    = 'CONFLICT'
        Attempted = 'TRUE'
        Performed = 'UNKNOWN'
    }
}

function Invoke-ReleaseCommandRunner {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Program,
        [Parameter(Mandatory = $true)]
        [string[]]$ArgumentList,
        [string]$WorkingDirectory = ''
    )

    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
            Push-Location -LiteralPath $WorkingDirectory
        }
        $output = & $Program @ArgumentList 2>&1 | ForEach-Object { [string]$_ }
        return [pscustomobject]@{
            ExitCode = [int]$LASTEXITCODE
            Stdout   = [string]::Join("`n", @($output)).Trim()
            Stderr   = ''
        }
    }
    finally {
        if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
            Pop-Location
        }
        $ErrorActionPreference = $prevEap
    }
}

function New-ReleaseRealCommandRunner {
    return {
        param(
            [string]$Program,
            [string[]]$ArgumentList,
            [string]$WorkingDirectory = ''
        )
        return Invoke-ReleaseCommandRunner -Program $Program -ArgumentList $ArgumentList -WorkingDirectory $WorkingDirectory
    }.GetNewClosure()
}

function Resolve-ReleaseCommandRunner {
    param($CommandRunner)
    if ($null -ne $CommandRunner) { return $CommandRunner }
    if ($null -ne $script:ReleaseMutationCommandRunnerOverride) {
        return $script:ReleaseMutationCommandRunnerOverride
    }
    return New-ReleaseRealCommandRunner
}

function New-ReleaseProductionPublishImageExecutor {
    param(
        $CommandRunner,
        [string]$RepoRoot = ''
    )

    $runner = Resolve-ReleaseCommandRunner -CommandRunner $CommandRunner
    $ownerRepo = $script:CanonicalOwnerRepo
    return {
        param($ArgumentTable)
        $version = [string]$ArgumentTable.Version
        $sha = [string]$ArgumentTable.ReleaseCommitSha
        if ($version -notmatch $script:VersionXyz -or $sha -notmatch $script:Sha40) {
            return [pscustomobject]@{ State = 'FAILED_BEFORE_MUTATION' }
        }
        $result = & $runner 'gh' @(
            'workflow', 'run', 'publish-release-image.yml',
            '--repo', $ownerRepo,
            '--ref', 'main',
            '-f', ('source_sha=' + $sha),
            '-f', ('release_version=' + $version)
        ) $RepoRoot
        if ($result.ExitCode -ne 0) {
            return [pscustomobject]@{ State = 'FAILED_BEFORE_MUTATION' }
        }
        return [pscustomobject]@{ State = 'SUCCESS' }
    }.GetNewClosure()
}

function New-ReleaseProductionCreateTagExecutor {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        $CommandRunner
    )

    $runner = Resolve-ReleaseCommandRunner -CommandRunner $CommandRunner
    return {
        param($ArgumentTable)
        $version = [string]$ArgumentTable.Version
        $sha = [string]$ArgumentTable.ReleaseCommitSha
        $tagName = [string]$ArgumentTable.TagName
        if ([string]::IsNullOrWhiteSpace($tagName)) {
            $tagName = 'v' + $version
        }
        if ($version -notmatch $script:VersionXyz -or $sha -notmatch $script:Sha40) {
            return [pscustomobject]@{ State = 'FAILED_BEFORE_MUTATION' }
        }
        if ($tagName -ne ('v' + $version)) {
            return [pscustomobject]@{ State = 'FAILED_BEFORE_MUTATION' }
        }

        $remoteCheck = & $runner 'git' @('ls-remote', '--exit-code', 'origin', ('refs/tags/' + $tagName)) $RepoRoot
        if ($remoteCheck.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($remoteCheck.Stdout)) {
            return [pscustomobject]@{ State = 'FAILED_BEFORE_MUTATION' }
        }
        if ($remoteCheck.ExitCode -ne 0 -and $remoteCheck.ExitCode -ne 2) {
            return [pscustomobject]@{ State = 'AMBIGUOUS' }
        }

        $localCheck = & $runner 'git' @('tag', '-l', $tagName) $RepoRoot
        if ($localCheck.ExitCode -ne 0) {
            return [pscustomobject]@{ State = 'AMBIGUOUS' }
        }
        if (-not [string]::IsNullOrWhiteSpace($localCheck.Stdout)) {
            return [pscustomobject]@{ State = 'FAILED_BEFORE_MUTATION' }
        }

        $tagMessage = 'Amane Mailer v' + $version
        $createResult = & $runner 'git' @('tag', '-a', $tagName, $sha, '-m', $tagMessage) $RepoRoot
        if ($createResult.ExitCode -ne 0) {
            return [pscustomobject]@{ State = 'FAILED_BEFORE_MUTATION' }
        }

        $pushResult = & $runner 'git' @('push', 'origin', ('refs/tags/' + $tagName)) $RepoRoot
        if ($pushResult.ExitCode -ne 0) {
            return [pscustomobject]@{ State = 'AMBIGUOUS' }
        }
        return [pscustomobject]@{ State = 'SUCCESS' }
    }.GetNewClosure()
}

function New-ReleaseProductionPublishNugetExecutor {
    param(
        $CommandRunner,
        [string]$RepoRoot = ''
    )

    $runner = Resolve-ReleaseCommandRunner -CommandRunner $CommandRunner
    $ownerRepo = $script:CanonicalOwnerRepo
    return {
        param($ArgumentTable)
        $version = [string]$ArgumentTable.Version
        $ref = [string]$ArgumentTable.Ref
        if ([string]::IsNullOrWhiteSpace($ref)) {
            $ref = 'v' + $version
        }
        if ($version -notmatch $script:VersionXyz -or $ref -ne ('v' + $version)) {
            return [pscustomobject]@{ State = 'FAILED_BEFORE_MUTATION' }
        }
        $result = & $runner 'gh' @(
            'workflow', 'run', 'publish-contracts.yml',
            '--repo', $ownerRepo,
            '--ref', $ref
        ) $RepoRoot
        if ($result.ExitCode -ne 0) {
            return [pscustomobject]@{ State = 'FAILED_BEFORE_MUTATION' }
        }
        return [pscustomobject]@{ State = 'SUCCESS' }
    }.GetNewClosure()
}

function New-ReleaseProductionCreateGitHubReleaseExecutor {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        $CommandRunner
    )

    $runner = Resolve-ReleaseCommandRunner -CommandRunner $CommandRunner
    $ownerRepo = $script:CanonicalOwnerRepo
    return {
        param($ArgumentTable)
        $version = [string]$ArgumentTable.Version
        $tagName = [string]$ArgumentTable.TagName
        $notesPathInput = [string]$ArgumentTable.ReleaseNotesPath
        if ([string]::IsNullOrWhiteSpace($tagName)) {
            $tagName = 'v' + $version
        }
        if ($version -notmatch $script:VersionXyz -or $tagName -ne ('v' + $version)) {
            return [pscustomobject]@{ State = 'FAILED_BEFORE_MUTATION' }
        }
        if ([string]::IsNullOrWhiteSpace($notesPathInput)) {
            return [pscustomobject]@{ State = 'FAILED_BEFORE_MUTATION' }
        }

        $notesPath = $notesPathInput
        if (-not [System.IO.Path]::IsPathRooted($notesPathInput)) {
            $notesPath = Join-Path $RepoRoot $notesPathInput
        }
        try {
            $notesPath = (Resolve-Path -LiteralPath $notesPath -ErrorAction Stop).Path
        }
        catch {
            return [pscustomobject]@{ State = 'FAILED_BEFORE_MUTATION' }
        }
        if (-not (Test-Path -LiteralPath $notesPath -PathType Leaf)) {
            return [pscustomobject]@{ State = 'FAILED_BEFORE_MUTATION' }
        }

        $title = 'Amane Mailer v' + $version
        $result = & $runner 'gh' @(
            'release', 'create', $tagName,
            '--repo', $ownerRepo,
            '--title', $title,
            '--notes-file', $notesPath,
            '--verify-tag'
        ) ''
        if ($result.ExitCode -ne 0) {
            return [pscustomobject]@{ State = 'FAILED_BEFORE_MUTATION' }
        }
        return [pscustomobject]@{ State = 'SUCCESS' }
    }.GetNewClosure()
}

function New-ReleaseProductionPromoteLatestExecutor {
    param(
        $CommandRunner,
        [string]$RepoRoot = ''
    )

    $runner = Resolve-ReleaseCommandRunner -CommandRunner $CommandRunner
    $ownerRepo = $script:CanonicalOwnerRepo
    return {
        param($ArgumentTable)
        $version = [string]$ArgumentTable.Version
        $sha = [string]$ArgumentTable.ReleaseCommitSha
        $digest = [string]$ArgumentTable.ExpectedDigest
        if ($version -notmatch $script:VersionXyz -or $sha -notmatch $script:Sha40 -or $digest -notmatch $script:Digest64) {
            return [pscustomobject]@{ State = 'FAILED_BEFORE_MUTATION' }
        }
        $result = & $runner 'gh' @(
            'workflow', 'run', 'promote-release-latest.yml',
            '--repo', $ownerRepo,
            '--ref', 'main',
            '-f', ('release_version=' + $version),
            '-f', ('release_commit_sha=' + $sha),
            '-f', ('expected_digest=' + $digest)
        ) $RepoRoot
        if ($result.ExitCode -ne 0) {
            return [pscustomobject]@{ State = 'FAILED_BEFORE_MUTATION' }
        }
        return [pscustomobject]@{ State = 'SUCCESS' }
    }.GetNewClosure()
}

function New-ReleaseProductionMutationExecutor {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CommandName,
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        $CommandRunner
    )

    switch ($CommandName) {
        'publish-image' {
            return New-ReleaseProductionPublishImageExecutor -CommandRunner $CommandRunner -RepoRoot $RepoRoot
        }
        'create-tag' {
            return New-ReleaseProductionCreateTagExecutor -RepoRoot $RepoRoot -CommandRunner $CommandRunner
        }
        'publish-nuget' {
            return New-ReleaseProductionPublishNugetExecutor -CommandRunner $CommandRunner -RepoRoot $RepoRoot
        }
        'create-github-release' {
            return New-ReleaseProductionCreateGitHubReleaseExecutor -RepoRoot $RepoRoot -CommandRunner $CommandRunner
        }
        'promote-latest' {
            return New-ReleaseProductionPromoteLatestExecutor -CommandRunner $CommandRunner -RepoRoot $RepoRoot
        }
        default {
            throw ("release-client: unsupported production mutation command '{0}'." -f $CommandName)
        }
    }
}

function Resolve-ReleaseMutationExecutor {
    param(
        $Executor,
        [switch]$Execute,
        [Parameter(Mandatory = $true)]
        [string]$CommandName,
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        $CommandRunner
    )
    if ($null -ne $Executor) { return $Executor }
    if (-not $Execute) { return $null }
    return New-ReleaseProductionMutationExecutor -CommandName $CommandName -RepoRoot $RepoRoot -CommandRunner $CommandRunner
}

function Invoke-ReleaseMutationExecutor {
    param(
        $Executor,
        $ArgumentTable
    )
    if ($null -eq $Executor) {
        return [pscustomobject]@{ State = 'NOT_CONFIGURED' }
    }
    return & $Executor $ArgumentTable
}

function Format-ReleaseMutationLines {
    param(
        $Map,
        [string[]]$Keys
    )
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($key in $Keys) {
        $value = [string]$Map[$key]
        $value = $value -replace '[\r\n]+', ' '
        [void]$lines.Add(('{0}={1}' -f $key, $value))
    }
    return $lines
}

function Get-ReleasePublishImageMutationStatus {
    param($Facts)

    $guardGhcr = ConvertTo-GhcrPublishTargetGuardState -GhcrFact $Facts.Ghcr -ReleaseCommitSha $Facts.ReleaseCommitSha -Version $Facts.Version
    $guardRun = ConvertTo-WorkflowRunMutationGuardState -RunState $Facts.ImagePublishRun

    $precheck = Resolve-ReleaseMutationPrecheck -GuardStates @($guardRun, (ConvertTo-PreflightMutationGuardState -PreflightMap $Facts.PreflightMap)) -TargetGuardState $guardGhcr -Execute $Facts.Execute

    $result = 'NOT_ATTEMPTED'
    $attempted = 'FALSE'
    $performed = 'FALSE'
    if ($null -ne $precheck) {
        $result = $precheck.Result
        $attempted = $precheck.Attempted
        $performed = $precheck.Performed
    }
    elseif ($Facts.Execute) {
        $execResult = Invoke-ReleaseMutationExecutor -Executor $Facts.Executor -ArgumentTable @{
            Version          = $Facts.Version
            ReleaseCommitSha = $Facts.ReleaseCommitSha
        }
        if ($execResult.State -eq 'NOT_CONFIGURED') {
            $result = 'INCOMPLETE'
            $attempted = 'FALSE'
            $performed = 'FALSE'
        }
        else {
            $readBackGuard = 'INCOMPLETE'
            if ($execResult.State -eq 'SUCCESS' -and $null -ne $Facts.ReadBackFetcher) {
                $readBackRunState = [string](& $Facts.ReadBackFetcher)
                if (-not [string]::IsNullOrWhiteSpace($readBackRunState)) {
                    $readBackGuard = ConvertTo-WorkflowRunMutationGuardState -RunState $readBackRunState
                }
            }
            elseif ($execResult.State -eq 'SUCCESS') {
                $readBackGuard = ConvertTo-WorkflowRunMutationGuardState -RunState $Facts.ReadBackImagePublishRun
            }
            $post = Resolve-ReleaseMutationPostAttempt -ExecutorState $execResult.State -ReadBackGuardState $readBackGuard -TargetGuardState 'EXACT_MATCH'
            $result = $post.Result
            $attempted = $post.Attempted
            $performed = $post.Performed
        }
    }

    $runId = $Facts.ImagePublishRunId
    if ([string]::IsNullOrWhiteSpace($runId)) { $runId = 'NONE' }

    $map = [ordered]@{}
    $map['COMMAND'] = 'PUBLISH_IMAGE'
    $map['VERSION'] = $Facts.Version
    $map['RELEASE_COMMIT_SHA'] = $Facts.ReleaseCommitSha
    $map['MUTATION_RESULT'] = $result
    $map['MUTATION_ATTEMPTED'] = $attempted
    $map['MUTATION_PERFORMED'] = $performed
    $map['HUMAN_AUTHORIZATION_REQUIRED'] = 'TRUE'
    $map['SOURCE_BINDING'] = $Facts.SourceBinding
    $map['VERSION_PREP'] = $Facts.VersionPrep
    $map['GUARD_GHCR'] = $guardGhcr
    $map['GUARD_IMAGE_PUBLISH_RUN'] = $guardRun
    $map['IMAGE_PUBLISH_RUN_ID'] = $runId
    return $map
}

function Invoke-ReleasePublishImage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version,
        [Parameter(Mandatory = $true)]
        [string]$ReleaseCommitSha,
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        $Observers,
        $Executor,
        $CommandRunner,
        [switch]$Execute,
        [switch]$Quiet
    )

    if (-not (Test-ReleaseVersion $Version)) {
        throw 'Version must be X.Y.Z; the client does not infer or accept a v-prefix.'
    }
    if (-not (Test-ReleaseSha $ReleaseCommitSha)) {
        throw 'ReleaseCommitSha must be a lowercase 40-hex SHA; the client does not infer source.'
    }

    $preflightMap = Invoke-ReleasePreflight -Version $Version -ReleaseCommitSha $ReleaseCommitSha -RepoRoot $RepoRoot -Observers $Observers -Quiet

    $ghcr = $null
    if ($null -ne $Observers -and $Observers.Contains('Ghcr')) {
        $ghcr = & $Observers['Ghcr'] $Version $ReleaseCommitSha
    }
    elseif ($null -ne $Observers -and $Observers.Contains('GhcrVersion') -and $Observers.Contains('GhcrSha')) {
        $ghcrVersion = & $Observers['GhcrVersion'] $Version
        $ghcrSha = & $Observers['GhcrSha'] $ReleaseCommitSha
        if ($null -ne $ghcrVersion -and $ghcrVersion.State -eq 'PRESENT') {
            $ghcr = $ghcrVersion
            if ($null -ne $ghcrSha) {
                $ghcr.ShaTagState = $ghcrSha.State
                $ghcr.ShaTagDigest = $ghcrSha.Digest
            }
        }
        elseif ($null -ne $ghcrVersion) {
            $ghcr = $ghcrVersion
        }
    }
    else {
        $ghcr = Get-GhcrVerifyObservation -Version $Version -ReleaseCommitSha $ReleaseCommitSha
    }
    if ($null -eq $ghcr) { $ghcr = New-ArtifactFact -State 'INCOMPLETE' }

    $readBackFetcher = $null
    if ($Execute) {
        if ($null -ne $Observers -and $Observers.Contains('ReadBackImagePublishRun')) {
            $readBackFetcher = New-ReleaseModuleBoundScriptBlock -Capture @{
                Observers        = $Observers
                ReleaseCommitSha = $ReleaseCommitSha
            } -ScriptBlock {
                param($c)
                $obs = & $c.Observers['ReadBackImagePublishRun'] $c.ReleaseCommitSha
                if ($null -eq $obs) { return 'INCOMPLETE' }
                return [string]$obs.State
            }
        }
        else {
            $readBackFetcher = New-ReleaseModuleBoundScriptBlock -Capture @{
                ReleaseCommitSha = $ReleaseCommitSha
            } -ScriptBlock {
                param($c)
                $runFetch = Get-GitHubWorkflowDispatchRuns -ReleaseCommitSha $c.ReleaseCommitSha
                if ($runFetch.State -eq 'INCOMPLETE') { return 'INCOMPLETE' }
                $runObs = ConvertTo-WorkflowDispatchRunObservation -Runs $runFetch.Runs -WorkflowPath '.github/workflows/publish-release-image.yml' -ReleaseCommitSha $c.ReleaseCommitSha
                return [string]$runObs.State
            }
        }
    }

    $resolvedExecutor = Resolve-ReleaseMutationExecutor -Executor $Executor -Execute:$Execute -CommandName 'publish-image' -RepoRoot $RepoRoot -CommandRunner $CommandRunner

    $facts = [pscustomobject]@{
        Version           = $Version
        ReleaseCommitSha  = $ReleaseCommitSha
        PreflightMap      = $preflightMap
        SourceBinding     = $preflightMap['SOURCE_BINDING']
        VersionPrep       = $preflightMap['VERSION_PREP']
        Ghcr              = $ghcr
        ReadBackGhcr      = $ghcr
        ReadBackFetcher   = $readBackFetcher
        ImagePublishRun   = $preflightMap['IMAGE_PUBLISH_RUN']
        ImagePublishRunId = $preflightMap['IMAGE_PUBLISH_RUN_ID']
        Execute           = [bool]$Execute
        Executor          = $resolvedExecutor
    }

    $map = Get-ReleasePublishImageMutationStatus -Facts $facts
    if (-not $Quiet) {
        Write-ReleaseStderr ('release-client: publish-image VERSION={0} SHA={1}' -f $Version, $ReleaseCommitSha)
        Write-ReleaseStderr ('release-client: MUTATION_RESULT={0} MUTATION_ATTEMPTED={1} MUTATION_PERFORMED={2}' -f $map['MUTATION_RESULT'], $map['MUTATION_ATTEMPTED'], $map['MUTATION_PERFORMED'])
        foreach ($line in (Format-ReleaseMutationLines -Map $map -Keys $script:PublishImageMutationKeys)) {
            [Console]::Out.WriteLine($line)
        }
    }
    return $map
}

function Get-ReleaseCreateTagMutationStatus {
    param($Facts)

    $guardGhcr = ConvertTo-GhcrPrerequisiteGuardState -GhcrFact $Facts.Ghcr -ReleaseCommitSha $Facts.ReleaseCommitSha -Version $Facts.Version
    $guardTag = ConvertTo-GitTagMutationGuardState -TagFact $Facts.GitTag -ReleaseCommitSha $Facts.ReleaseCommitSha

    $precheck = Resolve-ReleaseMutationPrecheck -PrerequisiteGuardStates @($guardGhcr) -GuardStates @() -TargetGuardState $guardTag -Execute $Facts.Execute

    $result = 'NOT_ATTEMPTED'
    $attempted = 'FALSE'
    $performed = 'FALSE'
    if ($null -ne $precheck) {
        $result = $precheck.Result
        $attempted = $precheck.Attempted
        $performed = $precheck.Performed
    }
    elseif ($Facts.Execute) {
        $execResult = Invoke-ReleaseMutationExecutor -Executor $Facts.Executor -ArgumentTable @{
            Version          = $Facts.Version
            ReleaseCommitSha = $Facts.ReleaseCommitSha
            TagName          = ('v' + $Facts.Version)
        }
        if ($execResult.State -eq 'NOT_CONFIGURED') {
            $result = 'INCOMPLETE'
            $attempted = 'FALSE'
            $performed = 'FALSE'
        }
        else {
            $readBackFact = $Facts.ReadBackGitTag
            if ($execResult.State -eq 'SUCCESS' -and $null -ne $Facts.ReadBackFetcher) {
                $readBackFact = & $Facts.ReadBackFetcher
                if ($null -eq $readBackFact) { $readBackFact = New-ArtifactFact -State 'INCOMPLETE' }
            }
            $readTag = ConvertTo-GitTagMutationGuardState -TagFact $readBackFact -ReleaseCommitSha $Facts.ReleaseCommitSha
            $post = Resolve-ReleaseMutationPostAttempt -ExecutorState $execResult.State -ReadBackGuardState $readTag -TargetGuardState 'EXACT_MATCH'
            $result = $post.Result
            $attempted = $post.Attempted
            $performed = $post.Performed
        }
    }

    $map = [ordered]@{}
    $map['COMMAND'] = 'CREATE_TAG'
    $map['VERSION'] = $Facts.Version
    $map['RELEASE_COMMIT_SHA'] = $Facts.ReleaseCommitSha
    $map['MUTATION_RESULT'] = $result
    $map['MUTATION_ATTEMPTED'] = $attempted
    $map['MUTATION_PERFORMED'] = $performed
    $map['HUMAN_AUTHORIZATION_REQUIRED'] = 'TRUE'
    $map['GUARD_GHCR'] = $guardGhcr
    $map['GUARD_GIT_TAG'] = $guardTag
    return $map
}

function Invoke-ReleaseCreateTag {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version,
        [Parameter(Mandatory = $true)]
        [string]$ReleaseCommitSha,
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        $Observers,
        $Executor,
        $CommandRunner,
        [switch]$Execute,
        [switch]$Quiet
    )

    if (-not (Test-ReleaseVersion $Version)) {
        throw 'Version must be X.Y.Z; the client does not infer or accept a v-prefix.'
    }
    if (-not (Test-ReleaseSha $ReleaseCommitSha)) {
        throw 'ReleaseCommitSha must be a lowercase 40-hex SHA; the client does not infer source.'
    }

    $gitTag = $null
    $ghcr = $null
    if ($null -ne $Observers) {
        if ($Observers.Contains('GitTag')) { $gitTag = & $Observers['GitTag'] $Version }
        if ($Observers.Contains('Ghcr')) { $ghcr = & $Observers['Ghcr'] $Version $ReleaseCommitSha }
    }
    else {
        $gitTag = Get-GitTagObservation -Version $Version
        $ghcr = Get-GhcrVerifyObservation -Version $Version -ReleaseCommitSha $ReleaseCommitSha
    }
    if ($null -eq $gitTag) { $gitTag = New-ArtifactFact -State 'INCOMPLETE' }
    if ($null -eq $ghcr) { $ghcr = New-ArtifactFact -State 'INCOMPLETE' }

    $readBackFetcher = $null
    if ($Execute) {
        if ($null -ne $Observers -and $Observers.Contains('ReadBackGitTag')) {
            $readBackFetcher = New-ReleaseModuleBoundScriptBlock -Capture @{
                Observers = $Observers
                Version   = $Version
            } -ScriptBlock {
                param($c)
                return & $c.Observers['ReadBackGitTag'] $c.Version
            }
        }
        else {
            $readBackFetcher = New-ReleaseModuleBoundScriptBlock -Capture @{
                Version = $Version
            } -ScriptBlock {
                param($c)
                return Get-GitTagObservation -Version $c.Version
            }
        }
    }

    $resolvedExecutor = Resolve-ReleaseMutationExecutor -Executor $Executor -Execute:$Execute -CommandName 'create-tag' -RepoRoot $RepoRoot -CommandRunner $CommandRunner

    $facts = [pscustomobject]@{
        Version          = $Version
        ReleaseCommitSha = $ReleaseCommitSha
        GitTag           = $gitTag
        ReadBackGitTag   = $gitTag
        ReadBackFetcher  = $readBackFetcher
        Ghcr             = $ghcr
        Execute          = [bool]$Execute
        Executor         = $resolvedExecutor
    }

    $map = Get-ReleaseCreateTagMutationStatus -Facts $facts
    if (-not $Quiet) {
        Write-ReleaseStderr ('release-client: create-tag VERSION={0} SHA={1}' -f $Version, $ReleaseCommitSha)
        Write-ReleaseStderr ('release-client: MUTATION_RESULT={0} MUTATION_ATTEMPTED={1} MUTATION_PERFORMED={2}' -f $map['MUTATION_RESULT'], $map['MUTATION_ATTEMPTED'], $map['MUTATION_PERFORMED'])
        foreach ($line in (Format-ReleaseMutationLines -Map $map -Keys $script:CreateTagMutationKeys)) {
            [Console]::Out.WriteLine($line)
        }
    }
    return $map
}

function Get-ReleasePublishNugetMutationStatus {
    param($Facts)

    $guardGhcr = ConvertTo-GhcrPrerequisiteGuardState -GhcrFact $Facts.Ghcr -ReleaseCommitSha $Facts.ReleaseCommitSha -Version $Facts.Version
    $guardTag = ConvertTo-GitTagMutationGuardState -TagFact $Facts.GitTag -ReleaseCommitSha $Facts.ReleaseCommitSha
    $guardNuget = ConvertTo-NugetMutationGuardState -NugetFact $Facts.Nuget
    $guardRun = ConvertTo-WorkflowRunMutationGuardState -RunState $Facts.NugetPublishRun

    $precheck = Resolve-ReleaseMutationPrecheck -PrerequisiteGuardStates @($guardGhcr, $guardTag) -GuardStates @($guardRun) -TargetGuardState $guardNuget -Execute $Facts.Execute

    $result = 'NOT_ATTEMPTED'
    $attempted = 'FALSE'
    $performed = 'FALSE'
    if ($null -ne $precheck) {
        $result = $precheck.Result
        $attempted = $precheck.Attempted
        $performed = $precheck.Performed
    }
    elseif ($Facts.Execute) {
        $execResult = Invoke-ReleaseMutationExecutor -Executor $Facts.Executor -ArgumentTable @{
            Version          = $Facts.Version
            ReleaseCommitSha = $Facts.ReleaseCommitSha
            Ref              = ('v' + $Facts.Version)
        }
        if ($execResult.State -eq 'NOT_CONFIGURED') {
            $result = 'INCOMPLETE'
            $attempted = 'FALSE'
            $performed = 'FALSE'
        }
        else {
            $readBackGuard = 'INCOMPLETE'
            if ($execResult.State -eq 'SUCCESS' -and $null -ne $Facts.ReadBackFetcher) {
                $readBackRunState = [string](& $Facts.ReadBackFetcher)
                if (-not [string]::IsNullOrWhiteSpace($readBackRunState)) {
                    $readBackGuard = ConvertTo-WorkflowRunMutationGuardState -RunState $readBackRunState
                }
            }
            elseif ($execResult.State -eq 'SUCCESS') {
                $readBackGuard = ConvertTo-WorkflowRunMutationGuardState -RunState $Facts.ReadBackNugetPublishRun
            }
            $post = Resolve-ReleaseMutationPostAttempt -ExecutorState $execResult.State -ReadBackGuardState $readBackGuard -TargetGuardState 'EXACT_MATCH'
            $result = $post.Result
            $attempted = $post.Attempted
            $performed = $post.Performed
        }
    }

    $runId = $Facts.NugetPublishRunId
    if ([string]::IsNullOrWhiteSpace($runId)) { $runId = 'NONE' }

    $map = [ordered]@{}
    $map['COMMAND'] = 'PUBLISH_NUGET'
    $map['VERSION'] = $Facts.Version
    $map['RELEASE_COMMIT_SHA'] = $Facts.ReleaseCommitSha
    $map['MUTATION_RESULT'] = $result
    $map['MUTATION_ATTEMPTED'] = $attempted
    $map['MUTATION_PERFORMED'] = $performed
    $map['HUMAN_AUTHORIZATION_REQUIRED'] = 'TRUE'
    $map['GUARD_GHCR'] = $guardGhcr
    $map['GUARD_GIT_TAG'] = $guardTag
    $map['GUARD_NUGET'] = $guardNuget
    $map['GUARD_NUGET_PUBLISH_RUN'] = $guardRun
    $map['NUGET_PUBLISH_RUN_ID'] = $runId
    return $map
}

function Invoke-ReleasePublishNuget {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version,
        [Parameter(Mandatory = $true)]
        [string]$ReleaseCommitSha,
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        $Observers,
        $Executor,
        $CommandRunner,
        [switch]$Execute,
        [switch]$Quiet
    )

    if (-not (Test-ReleaseVersion $Version)) {
        throw 'Version must be X.Y.Z; the client does not infer or accept a v-prefix.'
    }
    if (-not (Test-ReleaseSha $ReleaseCommitSha)) {
        throw 'ReleaseCommitSha must be a lowercase 40-hex SHA; the client does not infer source.'
    }

    $gitTag = $null
    $ghcr = $null
    $nuget = $null
    $nugetRun = 'INCOMPLETE'
    $nugetRunId = 'NONE'

    if ($null -ne $Observers) {
        if ($Observers.Contains('GitTag')) { $gitTag = & $Observers['GitTag'] $Version }
        if ($Observers.Contains('Ghcr')) { $ghcr = & $Observers['Ghcr'] $Version $ReleaseCommitSha }
        if ($Observers.Contains('Nuget')) { $nuget = & $Observers['Nuget'] $Version }
        if ($Observers.Contains('NugetPublishRun')) {
            $runObs = & $Observers['NugetPublishRun'] $ReleaseCommitSha
            if ($null -ne $runObs) {
                $nugetRun = [string]$runObs.State
                $nugetRunId = [string]$runObs.Id
            }
        }
    }
    else {
        $gitTag = Get-GitTagObservation -Version $Version
        $ghcr = Get-GhcrVerifyObservation -Version $Version -ReleaseCommitSha $ReleaseCommitSha
        $nuget = Get-NugetObservation -Version $Version
        $runFetch = Get-GitHubWorkflowDispatchRuns -ReleaseCommitSha $ReleaseCommitSha
        if ($runFetch.State -eq 'INCOMPLETE') {
            $nugetRun = 'INCOMPLETE'
        }
        else {
            $runObs = ConvertTo-WorkflowDispatchRunObservation -Runs $runFetch.Runs -WorkflowPath '.github/workflows/publish-contracts.yml' -ReleaseCommitSha $ReleaseCommitSha
            $nugetRun = $runObs.State
            $nugetRunId = $runObs.Id
        }
    }

    if ($null -eq $gitTag) { $gitTag = New-ArtifactFact -State 'INCOMPLETE' }
    if ($null -eq $ghcr) { $ghcr = New-ArtifactFact -State 'INCOMPLETE' }
    if ($null -eq $nuget) { $nuget = New-ArtifactFact -State 'INCOMPLETE' }

    $readBackFetcher = $null
    if ($Execute) {
        if ($null -ne $Observers -and $Observers.Contains('ReadBackNugetPublishRun')) {
            $readBackFetcher = New-ReleaseModuleBoundScriptBlock -Capture @{
                Observers        = $Observers
                ReleaseCommitSha = $ReleaseCommitSha
            } -ScriptBlock {
                param($c)
                $obs = & $c.Observers['ReadBackNugetPublishRun'] $c.ReleaseCommitSha
                if ($null -eq $obs) { return 'INCOMPLETE' }
                return [string]$obs.State
            }
        }
        else {
            $readBackFetcher = New-ReleaseModuleBoundScriptBlock -Capture @{
                ReleaseCommitSha = $ReleaseCommitSha
            } -ScriptBlock {
                param($c)
                $runFetch = Get-GitHubWorkflowDispatchRuns -ReleaseCommitSha $c.ReleaseCommitSha
                if ($runFetch.State -eq 'INCOMPLETE') { return 'INCOMPLETE' }
                $runObs = ConvertTo-WorkflowDispatchRunObservation -Runs $runFetch.Runs -WorkflowPath '.github/workflows/publish-contracts.yml' -ReleaseCommitSha $c.ReleaseCommitSha
                return [string]$runObs.State
            }
        }
    }

    $resolvedExecutor = Resolve-ReleaseMutationExecutor -Executor $Executor -Execute:$Execute -CommandName 'publish-nuget' -RepoRoot $RepoRoot -CommandRunner $CommandRunner

    $facts = [pscustomobject]@{
        Version          = $Version
        ReleaseCommitSha = $ReleaseCommitSha
        GitTag           = $gitTag
        Ghcr             = $ghcr
        Nuget            = $nuget
        ReadBackNuget    = $nuget
        ReadBackFetcher  = $readBackFetcher
        NugetPublishRun  = $nugetRun
        NugetPublishRunId = $nugetRunId
        Execute          = [bool]$Execute
        Executor         = $resolvedExecutor
    }

    $map = Get-ReleasePublishNugetMutationStatus -Facts $facts
    if (-not $Quiet) {
        Write-ReleaseStderr ('release-client: publish-nuget VERSION={0} SHA={1}' -f $Version, $ReleaseCommitSha)
        Write-ReleaseStderr ('release-client: MUTATION_RESULT={0} MUTATION_ATTEMPTED={1} MUTATION_PERFORMED={2}' -f $map['MUTATION_RESULT'], $map['MUTATION_ATTEMPTED'], $map['MUTATION_PERFORMED'])
        foreach ($line in (Format-ReleaseMutationLines -Map $map -Keys $script:PublishNugetMutationKeys)) {
            [Console]::Out.WriteLine($line)
        }
    }
    return $map
}

function Test-ReleaseNotesPathGuard {
    param(
        [string]$RepoRoot,
        [string]$ReleaseNotesPath
    )
    if ([string]::IsNullOrWhiteSpace($ReleaseNotesPath)) { return 'ABSENT' }
    $fullPath = $ReleaseNotesPath
    if (-not [System.IO.Path]::IsPathRooted($ReleaseNotesPath)) {
        $fullPath = Join-Path $RepoRoot $ReleaseNotesPath
    }
    try {
        $resolved = (Resolve-Path -LiteralPath $fullPath -ErrorAction Stop).Path
    }
    catch {
        return 'INCOMPLETE'
    }
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        return 'INCOMPLETE'
    }
    return 'PRESENT'
}

function Get-ReleaseCreateGitHubReleaseMutationStatus {
    param($Facts)

    $guardGhcr = ConvertTo-GhcrPrerequisiteGuardState -GhcrFact $Facts.Ghcr -ReleaseCommitSha $Facts.ReleaseCommitSha -Version $Facts.Version
    $guardTag = ConvertTo-GitTagMutationGuardState -TagFact $Facts.GitTag -ReleaseCommitSha $Facts.ReleaseCommitSha
    $guardNuget = ConvertTo-NugetMutationGuardState -NugetFact $Facts.Nuget
    $guardRelease = ConvertTo-GitHubReleaseMutationGuardState -ReleaseFact $Facts.GitHubRelease -Version $Facts.Version
    $notesGuard = $Facts.ReleaseNotesGuard

    $precheck = Resolve-ReleaseMutationPrecheck -PrerequisiteGuardStates @($guardGhcr, $guardTag, $guardNuget) -GuardStates @() -TargetGuardState $guardRelease -Execute $Facts.Execute -ReleaseNotesGuard $(if ($notesGuard -ne 'PRESENT') { $notesGuard } else { '' })

    $result = 'NOT_ATTEMPTED'
    $attempted = 'FALSE'
    $performed = 'FALSE'
    if ($null -ne $precheck) {
        $result = $precheck.Result
        $attempted = $precheck.Attempted
        $performed = $precheck.Performed
    }
    elseif ($Facts.Execute) {
        $execResult = Invoke-ReleaseMutationExecutor -Executor $Facts.Executor -ArgumentTable @{
            Version          = $Facts.Version
            ReleaseCommitSha = $Facts.ReleaseCommitSha
            TagName          = ('v' + $Facts.Version)
            ReleaseNotesPath = $Facts.ReleaseNotesPath
        }
        if ($execResult.State -eq 'NOT_CONFIGURED') {
            $result = 'INCOMPLETE'
            $attempted = 'FALSE'
            $performed = 'FALSE'
        }
        else {
            $readBackReleaseFact = $Facts.ReadBackGitHubRelease
            if ($execResult.State -eq 'SUCCESS' -and $null -ne $Facts.ReadBackFetcher) {
                $readBackReleaseFact = & $Facts.ReadBackFetcher
                if ($null -eq $readBackReleaseFact) { $readBackReleaseFact = New-ArtifactFact -State 'INCOMPLETE' }
            }
            $readBackTagFact = $Facts.ReadBackGitTag
            if ($execResult.State -eq 'SUCCESS' -and $null -ne $Facts.ReadBackTagFetcher) {
                $readBackTagFact = & $Facts.ReadBackTagFetcher
                if ($null -eq $readBackTagFact) { $readBackTagFact = New-ArtifactFact -State 'INCOMPLETE' }
            }
            $readRelease = ConvertTo-GitHubReleaseMutationGuardState -ReleaseFact $readBackReleaseFact -Version $Facts.Version
            $readTag = ConvertTo-GitTagMutationGuardState -TagFact $readBackTagFact -ReleaseCommitSha $Facts.ReleaseCommitSha
            $combinedReadBack = Resolve-ReleaseCreateGitHubReleasePostReadBackGuard -ReleaseGuard $readRelease -TagGuard $readTag
            $post = Resolve-ReleaseMutationPostAttempt -ExecutorState $execResult.State -ReadBackGuardState $combinedReadBack -TargetGuardState 'EXACT_MATCH'
            $result = $post.Result
            $attempted = $post.Attempted
            $performed = $post.Performed
        }
    }

    $notesPathOut = $Facts.ReleaseNotesPath
    if ([string]::IsNullOrWhiteSpace($notesPathOut)) { $notesPathOut = 'NONE' }

    $map = [ordered]@{}
    $map['COMMAND'] = 'CREATE_GITHUB_RELEASE'
    $map['VERSION'] = $Facts.Version
    $map['RELEASE_COMMIT_SHA'] = $Facts.ReleaseCommitSha
    $map['MUTATION_RESULT'] = $result
    $map['MUTATION_ATTEMPTED'] = $attempted
    $map['MUTATION_PERFORMED'] = $performed
    $map['HUMAN_AUTHORIZATION_REQUIRED'] = 'TRUE'
    $map['GUARD_GHCR'] = $guardGhcr
    $map['GUARD_GIT_TAG'] = $guardTag
    $map['GUARD_NUGET'] = $guardNuget
    $map['GUARD_GITHUB_RELEASE'] = $guardRelease
    $map['RELEASE_NOTES_PATH'] = $notesPathOut
    return $map
}

function Invoke-ReleaseCreateGitHubRelease {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version,
        [Parameter(Mandatory = $true)]
        [string]$ReleaseCommitSha,
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        [string]$ReleaseNotesPath = '',
        $Observers,
        $Executor,
        $CommandRunner,
        [switch]$Execute,
        [switch]$Quiet
    )

    if (-not (Test-ReleaseVersion $Version)) {
        throw 'Version must be X.Y.Z; the client does not infer or accept a v-prefix.'
    }
    if (-not (Test-ReleaseSha $ReleaseCommitSha)) {
        throw 'ReleaseCommitSha must be a lowercase 40-hex SHA; the client does not infer source.'
    }

    $gitTag = $null
    $ghcr = $null
    $nuget = $null
    $githubRelease = $null
    if ($null -ne $Observers) {
        if ($Observers.Contains('GitTag')) { $gitTag = & $Observers['GitTag'] $Version }
        if ($Observers.Contains('Ghcr')) { $ghcr = & $Observers['Ghcr'] $Version $ReleaseCommitSha }
        if ($Observers.Contains('Nuget')) { $nuget = & $Observers['Nuget'] $Version }
        if ($Observers.Contains('GitHubRelease')) { $githubRelease = & $Observers['GitHubRelease'] $Version }
    }
    else {
        $gitTag = Get-GitTagObservation -Version $Version
        $ghcr = Get-GhcrVerifyObservation -Version $Version -ReleaseCommitSha $ReleaseCommitSha
        $nuget = Get-NugetObservation -Version $Version
        $githubRelease = Get-GitHubReleaseObservation -Version $Version
    }
    if ($null -eq $gitTag) { $gitTag = New-ArtifactFact -State 'INCOMPLETE' }
    if ($null -eq $ghcr) { $ghcr = New-ArtifactFact -State 'INCOMPLETE' }
    if ($null -eq $nuget) { $nuget = New-ArtifactFact -State 'INCOMPLETE' }
    if ($null -eq $githubRelease) { $githubRelease = New-ArtifactFact -State 'INCOMPLETE' }

    $notesGuard = Test-ReleaseNotesPathGuard -RepoRoot $RepoRoot -ReleaseNotesPath $ReleaseNotesPath

    $readBackFetcher = $null
    $readBackTagFetcher = $null
    if ($Execute) {
        if ($null -ne $Observers -and $Observers.Contains('ReadBackGitHubRelease')) {
            $readBackFetcher = New-ReleaseModuleBoundScriptBlock -Capture @{
                Observers = $Observers
                Version   = $Version
            } -ScriptBlock {
                param($c)
                return & $c.Observers['ReadBackGitHubRelease'] $c.Version
            }
        }
        else {
            $readBackFetcher = New-ReleaseModuleBoundScriptBlock -Capture @{
                Version = $Version
            } -ScriptBlock {
                param($c)
                return Get-GitHubReleaseObservation -Version $c.Version
            }
        }
        if ($null -ne $Observers -and $Observers.Contains('ReadBackGitTag')) {
            $readBackTagFetcher = New-ReleaseModuleBoundScriptBlock -Capture @{
                Observers = $Observers
                Version   = $Version
            } -ScriptBlock {
                param($c)
                return & $c.Observers['ReadBackGitTag'] $c.Version
            }
        }
        else {
            $readBackTagFetcher = New-ReleaseModuleBoundScriptBlock -Capture @{
                Version = $Version
            } -ScriptBlock {
                param($c)
                return Get-GitTagObservation -Version $c.Version
            }
        }
    }

    $resolvedExecutor = Resolve-ReleaseMutationExecutor -Executor $Executor -Execute:$Execute -CommandName 'create-github-release' -RepoRoot $RepoRoot -CommandRunner $CommandRunner

    $facts = [pscustomobject]@{
        Version               = $Version
        ReleaseCommitSha      = $ReleaseCommitSha
        GitTag                = $gitTag
        Ghcr                  = $ghcr
        Nuget                 = $nuget
        GitHubRelease         = $githubRelease
        ReadBackGitHubRelease = $githubRelease
        ReadBackGitTag        = $gitTag
        ReadBackFetcher       = $readBackFetcher
        ReadBackTagFetcher    = $readBackTagFetcher
        ReleaseNotesPath      = $ReleaseNotesPath
        ReleaseNotesGuard     = $notesGuard
        Execute               = [bool]$Execute
        Executor              = $resolvedExecutor
    }

    $map = Get-ReleaseCreateGitHubReleaseMutationStatus -Facts $facts
    if (-not $Quiet) {
        Write-ReleaseStderr ('release-client: create-github-release VERSION={0} SHA={1}' -f $Version, $ReleaseCommitSha)
        Write-ReleaseStderr ('release-client: MUTATION_RESULT={0} MUTATION_ATTEMPTED={1} MUTATION_PERFORMED={2}' -f $map['MUTATION_RESULT'], $map['MUTATION_ATTEMPTED'], $map['MUTATION_PERFORMED'])
        foreach ($line in (Format-ReleaseMutationLines -Map $map -Keys $script:CreateGitHubReleaseMutationKeys)) {
            [Console]::Out.WriteLine($line)
        }
    }
    return $map
}

$script:PromoteLatestWorkflowPath = '.github/workflows/promote-release-latest.yml'
$script:PromoteLatestMutationKeys = @(
    'COMMAND',
    'VERSION',
    'RELEASE_COMMIT_SHA',
    'EXPECTED_DIGEST',
    'MUTATION_RESULT',
    'MUTATION_ATTEMPTED',
    'HUMAN_AUTHORIZATION_REQUIRED',
    'LATEST_STATE',
    'GUARD_EXPECTED_DIGEST',
    'GUARD_GHCR',
    'GUARD_GIT_TAG',
    'GUARD_CONTRACTS_SOURCE',
    'GUARD_OPENAPI',
    'GUARD_NUGET',
    'GUARD_NUGET_REVISION',
    'GUARD_GITHUB_RELEASE',
    'GUARD_PROMOTE_LATEST_RUN',
    'PROMOTE_LATEST_RUN_ID',
    'PROMOTE_LATEST_RUN_IDENTITY',
    'MUTATION_PERFORMED'
)

function Get-PromoteLatestRunIdentity {
    param(
        [string]$Version,
        [string]$ReleaseCommitSha,
        [string]$ExpectedDigest
    )
    return ('promote-latest {0} {1} {2}' -f $Version, $ReleaseCommitSha, $ExpectedDigest)
}

function ConvertTo-ExpectedDigestMatchGuardState {
    param(
        $GhcrFact,
        [string]$ExpectedDigest
    )
    if (-not (Test-ReleaseDigest $ExpectedDigest)) { return 'INCOMPLETE' }
    if ($null -eq $GhcrFact) { return 'INCOMPLETE' }
    if ($GhcrFact.State -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if ($GhcrFact.State -eq 'ABSENT') { return 'ABSENT' }
    if ($GhcrFact.State -eq 'CONFLICT') { return 'CONFLICT' }
    if ($GhcrFact.State -ne 'PRESENT') { return 'INCOMPLETE' }
    if (-not (Test-ReleaseDigest $GhcrFact.Digest)) { return 'INCOMPLETE' }
    if ($GhcrFact.Digest -ne $ExpectedDigest) { return 'CONFLICT' }
    if ([string]::IsNullOrWhiteSpace($GhcrFact.ShaTagState)) { return 'INCOMPLETE' }
    if ($GhcrFact.ShaTagState -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if ($GhcrFact.ShaTagState -eq 'ABSENT') { return 'ABSENT' }
    if ($GhcrFact.ShaTagState -eq 'CONFLICT') { return 'CONFLICT' }
    if ($GhcrFact.ShaTagState -ne 'PRESENT') { return 'INCOMPLETE' }
    if (-not (Test-ReleaseDigest $GhcrFact.ShaTagDigest)) { return 'INCOMPLETE' }
    if ($GhcrFact.ShaTagDigest -ne $ExpectedDigest) { return 'CONFLICT' }
    return 'EXACT_MATCH'
}

function ConvertTo-LatestAliasState {
    param(
        $LatestFact,
        [string]$ExpectedDigest
    )
    if ($null -eq $LatestFact) { return 'INCOMPLETE' }
    if ($LatestFact.State -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if ($LatestFact.State -eq 'ABSENT') { return 'ABSENT' }
    if ($LatestFact.State -eq 'CONFLICT') { return 'INCOMPLETE' }
    if ($LatestFact.State -ne 'PRESENT') { return 'INCOMPLETE' }
    if (-not (Test-ReleaseDigest $ExpectedDigest)) { return 'INCOMPLETE' }
    if (-not (Test-ReleaseDigest $LatestFact.Digest)) { return 'INCOMPLETE' }
    if ($LatestFact.Digest -eq $ExpectedDigest) { return 'EXACT_MATCH' }
    return 'STALE'
}

function ConvertTo-LatestAliasMutationGuardState {
    param([string]$LatestState)
    if ($LatestState -eq 'ABSENT') { return 'ABSENT' }
    if ($LatestState -eq 'EXACT_MATCH') { return 'EXACT_MATCH' }
    if ($LatestState -eq 'STALE') { return 'STALE' }
    if ($LatestState -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    return 'INCOMPLETE'
}

function ConvertTo-PromoteLatestRunObservation {
    param(
        $Runs,
        [string]$WorkflowPath,
        [string]$RunIdentity
    )
    if ($null -eq $Runs) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Id = 'NONE'; Status = 'NONE'; Conclusion = 'NONE'; Reason = 'RUNS_NULL' }
    }
    if ([string]::IsNullOrWhiteSpace($RunIdentity)) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Id = 'NONE'; Status = 'NONE'; Conclusion = 'NONE'; Reason = 'IDENTITY' }
    }
    $matched = New-Object System.Collections.Generic.List[object]
    foreach ($run in @($Runs)) {
        $event = [string]$run.Event
        $path = [string]$run.Path
        $name = [string]$run.Name
        $display = [string]$run.DisplayTitle
        if ($event -ne $script:EventWorkflowDispatch) { continue }
        if ($path -ne $WorkflowPath) { continue }
        $identityHit = ($name -eq $RunIdentity) -or ($display -eq $RunIdentity)
        if (-not $identityHit) { continue }
        [void]$matched.Add($run)
    }
    if ($matched.Count -eq 0) {
        return [pscustomobject]@{ State = 'ABSENT'; Id = 'NONE'; Status = 'NONE'; Conclusion = 'NONE'; Reason = '' }
    }
    if ($matched.Count -gt 1) {
        return [pscustomobject]@{ State = 'AMBIGUOUS'; Id = 'NONE'; Status = 'NONE'; Conclusion = 'NONE'; Reason = 'MULTIPLE' }
    }
    $one = $matched[0]
    $id = [string]$one.Id
    if ([string]::IsNullOrWhiteSpace($id)) { $id = 'NONE' }
    $status = [string]$one.Status
    if ([string]::IsNullOrWhiteSpace($status)) { $status = 'NONE' }
    $conclusion = [string]$one.Conclusion
    if ([string]::IsNullOrWhiteSpace($conclusion)) { $conclusion = 'NONE' }
    $statusLower = $status.ToLowerInvariant()
    $conclusionLower = $conclusion.ToLowerInvariant()
    # Active / approval-waiting runs: no redispatch; latest digest need not have changed yet.
    if ($statusLower -in @('queued', 'in_progress', 'waiting', 'requested', 'pending')) {
        return [pscustomobject]@{ State = 'CANDIDATE_PRESENT'; Id = $id; Status = $status; Conclusion = $conclusion; Reason = '' }
    }
    # Historical success must be reconciled with current latest (Finding 3).
    if ($statusLower -eq 'completed' -and $conclusionLower -eq 'success') {
        return [pscustomobject]@{ State = 'SUCCESS_MATCH'; Id = $id; Status = $status; Conclusion = $conclusion; Reason = '' }
    }
    if ($statusLower -eq 'completed') {
        return [pscustomobject]@{ State = 'FAILED_MATCH'; Id = $id; Status = $status; Conclusion = $conclusion; Reason = 'FAILED_RUN' }
    }
    return [pscustomobject]@{ State = 'INCOMPLETE'; Id = $id; Status = $status; Conclusion = $conclusion; Reason = 'STATUS' }
}

function ConvertTo-PromoteLatestRunMutationGuardState {
    param([string]$RunState)
    if ($RunState -eq 'ABSENT') { return 'ABSENT' }
    if ($RunState -eq 'CANDIDATE_PRESENT') { return 'ACTIVE' }
    if ($RunState -eq 'SUCCESS_MATCH') { return 'SUCCESS_MATCH' }
    if ($RunState -eq 'AMBIGUOUS') { return 'CONFLICT' }
    if ($RunState -eq 'FAILED_MATCH') { return 'CONFLICT' }
    if ($RunState -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    return 'INCOMPLETE'
}

function ConvertTo-PromoteLatestPostDispatchRunGuardState {
    param([string]$RunState)
    # Dispatch applied when a deterministic matching run is active or already successful.
    # Latest digest equality is enforced by the workflow itself after environment approval.
    if ($RunState -eq 'CANDIDATE_PRESENT') { return 'EXACT_MATCH' }
    if ($RunState -eq 'SUCCESS_MATCH') { return 'EXACT_MATCH' }
    if ($RunState -eq 'AMBIGUOUS') { return 'CONFLICT' }
    if ($RunState -eq 'FAILED_MATCH') { return 'CONFLICT' }
    if ($RunState -eq 'ABSENT') { return 'INCOMPLETE' }
    if ($RunState -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    return 'INCOMPLETE'
}

function Get-GitHubPromoteLatestWorkflowRuns {
    $headers = Get-GitHubAuthHeaders
    $uri = $script:GitHubApiRoot + '/actions/workflows/promote-release-latest.yml/runs?event=' + $script:EventWorkflowDispatch + '&per_page=100'
    $resp = Invoke-ReleaseReadOnlyRequest -Uri $uri -Headers $headers
    $presence = ConvertTo-RemotePresence -StatusCode $resp.StatusCode -TransportFailure $resp.TransportFailure
    if ($presence -ne 'HTTP_OK') {
        return [pscustomobject]@{
            State  = 'INCOMPLETE'
            Runs   = $null
            Reason = ConvertTo-RemoteFailureClass -StatusCode $resp.StatusCode -TransportFailure $resp.TransportFailure -FailureClass $resp.FailureClass
        }
    }
    $runs = Convert-GitHubWorkflowRunsJson -Json $resp.BodyText
    if ($null -eq $runs) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Runs = $null; Reason = 'PARSE' }
    }
    return [pscustomobject]@{ State = 'PRESENT'; Runs = $runs; Reason = '' }
}

function Get-GhcrLatestObservation {
    param($Request)
    $token = Get-GhcrPullToken
    if ([string]::IsNullOrWhiteSpace($token)) {
        return New-ArtifactFact -State 'INCOMPLETE' -Reason 'GHCR_TOKEN'
    }
    return Get-GhcrManifestFact -Reference 'latest' -Token $token -ReadRevision -Request $Request
}

function Resolve-ReleasePromoteLatestPrecheck {
    param(
        [string[]]$PrerequisiteGuardStates,
        [string]$LatestGuardState,
        [string]$RunGuardState,
        [bool]$Execute
    )

    foreach ($pre in @($PrerequisiteGuardStates)) {
        if ($pre -eq 'EXACT_MATCH') { continue }
        if ($pre -eq 'INCOMPLETE') {
            return [pscustomobject]@{
                Result    = 'INCOMPLETE'
                Attempted = 'FALSE'
                Performed = 'FALSE'
            }
        }
        return [pscustomobject]@{
            Result    = 'CONFLICT'
            Attempted = 'FALSE'
            Performed = 'FALSE'
        }
    }

    # Multiple matching / failed matching runs: never redispatch.
    if ($RunGuardState -eq 'CONFLICT') {
        return [pscustomobject]@{
            Result    = 'CONFLICT'
            Attempted = 'FALSE'
            Performed = 'FALSE'
        }
    }
    if ($RunGuardState -eq 'INCOMPLETE') {
        return [pscustomobject]@{
            Result    = 'INCOMPLETE'
            Attempted = 'FALSE'
            Performed = 'FALSE'
        }
    }

    # Active matching run (queued/waiting/in_progress/...): no redispatch.
    if ($RunGuardState -eq 'ACTIVE') {
        return [pscustomobject]@{
            Result    = 'ALREADY_APPLIED'
            Attempted = 'FALSE'
            Performed = 'FALSE'
        }
    }

    # Historical successful matching run must agree with current latest alias.
    if ($RunGuardState -eq 'SUCCESS_MATCH') {
        if ($LatestGuardState -eq 'EXACT_MATCH') {
            return [pscustomobject]@{
                Result    = 'ALREADY_APPLIED'
                Attempted = 'FALSE'
                Performed = 'FALSE'
            }
        }
        if ($LatestGuardState -eq 'INCOMPLETE') {
            return [pscustomobject]@{
                Result    = 'INCOMPLETE'
                Attempted = 'FALSE'
                Performed = 'FALSE'
            }
        }
        # STALE / ABSENT contradict historical success -> STOP (not ALREADY_APPLIED).
        return [pscustomobject]@{
            Result    = 'CONFLICT'
            Attempted = 'FALSE'
            Performed = 'FALSE'
        }
    }

    # No matching run: current latest exact is already applied.
    if ($LatestGuardState -eq 'EXACT_MATCH') {
        return [pscustomobject]@{
            Result    = 'ALREADY_APPLIED'
            Attempted = 'FALSE'
            Performed = 'FALSE'
        }
    }
    if ($LatestGuardState -eq 'INCOMPLETE') {
        return [pscustomobject]@{
            Result    = 'INCOMPLETE'
            Attempted = 'FALSE'
            Performed = 'FALSE'
        }
    }
    if ($LatestGuardState -ne 'ABSENT' -and $LatestGuardState -ne 'STALE') {
        return [pscustomobject]@{
            Result    = 'CONFLICT'
            Attempted = 'FALSE'
            Performed = 'FALSE'
        }
    }

    if (-not $Execute) {
        return [pscustomobject]@{
            Result    = 'NOT_ATTEMPTED'
            Attempted = 'FALSE'
            Performed = 'FALSE'
        }
    }

    return $null
}

function ConvertTo-LatestPostReadBackGuardState {
    param(
        $LatestFact,
        [string]$ExpectedDigest
    )
    $state = ConvertTo-LatestAliasState -LatestFact $LatestFact -ExpectedDigest $ExpectedDigest
    if ($state -eq 'EXACT_MATCH') { return 'EXACT_MATCH' }
    if ($state -eq 'INCOMPLETE') { return 'INCOMPLETE' }
    if ($state -eq 'ABSENT') { return 'INCOMPLETE' }
    if ($state -eq 'STALE') { return 'CONFLICT' }
    return 'INCOMPLETE'
}

function Get-ReleasePromoteLatestMutationStatus {
    param($Facts)

    $guardExpected = ConvertTo-ExpectedDigestMatchGuardState -GhcrFact $Facts.Ghcr -ExpectedDigest $Facts.ExpectedDigest
    $guardGhcr = ConvertTo-GhcrPrerequisiteGuardState -GhcrFact $Facts.Ghcr -ReleaseCommitSha $Facts.ReleaseCommitSha -Version $Facts.Version
    $guardTag = ConvertTo-GitTagMutationGuardState -TagFact $Facts.GitTag -ReleaseCommitSha $Facts.ReleaseCommitSha
    $guardContracts = ConvertTo-SourceVersionVerifyState -ObservedVersion $Facts.ContractsVersion -ExpectedVersion $Facts.Version -FetchState $Facts.ContractsFetchState
    $guardOpenApi = ConvertTo-SourceVersionVerifyState -ObservedVersion $Facts.OpenApiVersion -ExpectedVersion $Facts.Version -FetchState $Facts.OpenApiFetchState
    $guardNuget = ConvertTo-NugetMutationGuardState -NugetFact $Facts.Nuget
    $guardNugetRevision = ConvertTo-NugetRevisionVerifyState -PackageState $Facts.Nuget.State -ObservedCommit $Facts.NugetRevisionCommit -ExpectedCommit $Facts.ReleaseCommitSha -FetchState $Facts.NugetRevisionFetchState
    $guardRelease = ConvertTo-GitHubReleaseMutationGuardState -ReleaseFact $Facts.GitHubRelease -Version $Facts.Version
    $latestState = ConvertTo-LatestAliasState -LatestFact $Facts.Latest -ExpectedDigest $Facts.ExpectedDigest
    $guardLatest = ConvertTo-LatestAliasMutationGuardState -LatestState $latestState
    $guardRun = ConvertTo-PromoteLatestRunMutationGuardState -RunState $Facts.PromoteLatestRun

    $precheck = Resolve-ReleasePromoteLatestPrecheck `
        -PrerequisiteGuardStates @(
            $guardExpected,
            $guardGhcr,
            $guardTag,
            $guardContracts,
            $guardOpenApi,
            $guardNuget,
            $guardNugetRevision,
            $guardRelease
        ) `
        -LatestGuardState $guardLatest `
        -RunGuardState $guardRun `
        -Execute $Facts.Execute

    $result = 'NOT_ATTEMPTED'
    $attempted = 'FALSE'
    $performed = 'FALSE'
    if ($null -ne $precheck) {
        $result = $precheck.Result
        $attempted = $precheck.Attempted
        $performed = $precheck.Performed
    }
    elseif ($Facts.Execute) {
        $execResult = Invoke-ReleaseMutationExecutor -Executor $Facts.Executor -ArgumentTable @{
            Version          = $Facts.Version
            ReleaseCommitSha = $Facts.ReleaseCommitSha
            ExpectedDigest   = $Facts.ExpectedDigest
        }
        if ($execResult.State -eq 'NOT_CONFIGURED') {
            $result = 'INCOMPLETE'
            $attempted = 'FALSE'
            $performed = 'FALSE'
        }
        else {
            $readBackGuard = 'INCOMPLETE'
            if ($execResult.State -eq 'SUCCESS' -and $null -ne $Facts.ReadBackFetcher) {
                $readBackRunState = [string](& $Facts.ReadBackFetcher)
                if (-not [string]::IsNullOrWhiteSpace($readBackRunState)) {
                    $readBackGuard = ConvertTo-PromoteLatestPostDispatchRunGuardState -RunState $readBackRunState
                }
            }
            $post = Resolve-ReleaseMutationPostAttempt -ExecutorState $execResult.State -ReadBackGuardState $readBackGuard -TargetGuardState 'EXACT_MATCH'
            $result = $post.Result
            $attempted = $post.Attempted
            $performed = $post.Performed
        }
    }

    $runId = $Facts.PromoteLatestRunId
    if ([string]::IsNullOrWhiteSpace($runId)) { $runId = 'NONE' }
    $runIdentity = $Facts.PromoteLatestRunIdentity
    if ([string]::IsNullOrWhiteSpace($runIdentity)) { $runIdentity = 'NONE' }

    $map = [ordered]@{}
    $map['COMMAND'] = 'PROMOTE_LATEST'
    $map['VERSION'] = $Facts.Version
    $map['RELEASE_COMMIT_SHA'] = $Facts.ReleaseCommitSha
    $map['EXPECTED_DIGEST'] = $Facts.ExpectedDigest
    $map['MUTATION_RESULT'] = $result
    $map['MUTATION_ATTEMPTED'] = $attempted
    $map['MUTATION_PERFORMED'] = $performed
    $map['HUMAN_AUTHORIZATION_REQUIRED'] = 'TRUE'
    $map['LATEST_STATE'] = $latestState
    $map['GUARD_EXPECTED_DIGEST'] = $guardExpected
    $map['GUARD_GHCR'] = $guardGhcr
    $map['GUARD_GIT_TAG'] = $guardTag
    $map['GUARD_CONTRACTS_SOURCE'] = $guardContracts
    $map['GUARD_OPENAPI'] = $guardOpenApi
    $map['GUARD_NUGET'] = $guardNuget
    $map['GUARD_NUGET_REVISION'] = $guardNugetRevision
    $map['GUARD_GITHUB_RELEASE'] = $guardRelease
    $map['GUARD_PROMOTE_LATEST_RUN'] = $guardRun
    $map['PROMOTE_LATEST_RUN_ID'] = $runId
    $map['PROMOTE_LATEST_RUN_IDENTITY'] = $runIdentity
    return $map
}

function Invoke-ReleasePromoteLatest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version,
        [Parameter(Mandatory = $true)]
        [string]$ReleaseCommitSha,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedDigest,
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        $Observers,
        $Executor,
        $CommandRunner,
        [switch]$Execute,
        [switch]$Quiet
    )

    if (-not (Test-ReleaseVersion $Version)) {
        throw 'Version must be X.Y.Z; the client does not infer or accept a v-prefix.'
    }
    if (-not (Test-ReleaseSha $ReleaseCommitSha)) {
        throw 'ReleaseCommitSha must be a lowercase 40-hex SHA; the client does not infer source.'
    }
    if (-not (Test-ReleaseDigest $ExpectedDigest)) {
        throw 'ExpectedDigest must be sha256:<64-lowercase-hex>; the client does not infer digest from a version tag.'
    }

    $gitTag = $null
    $ghcr = $null
    $nuget = $null
    $githubRelease = $null
    $latest = $null
    $contractsFetchState = 'INCOMPLETE'
    $contractsVersion = ''
    $openApiFetchState = 'INCOMPLETE'
    $openApiVersion = ''
    $nugetRevisionFetchState = 'INCOMPLETE'
    $nugetRevisionCommit = ''
    $promoteRun = 'INCOMPLETE'
    $promoteRunId = 'NONE'
    $runIdentity = Get-PromoteLatestRunIdentity -Version $Version -ReleaseCommitSha $ReleaseCommitSha -ExpectedDigest $ExpectedDigest

    if ($null -ne $Observers) {
        if ($Observers.Contains('GitTag')) { $gitTag = & $Observers['GitTag'] $Version }
        if ($Observers.Contains('Ghcr')) { $ghcr = & $Observers['Ghcr'] $Version $ReleaseCommitSha }
        if ($Observers.Contains('Nuget')) { $nuget = & $Observers['Nuget'] $Version }
        if ($Observers.Contains('GitHubRelease')) { $githubRelease = & $Observers['GitHubRelease'] $Version }
        if ($Observers.Contains('Latest')) { $latest = & $Observers['Latest'] }
        if ($Observers.Contains('SourceVersions')) {
            $sv = & $Observers['SourceVersions'] $ReleaseCommitSha $Version
            if ($null -ne $sv) {
                $contractsFetchState = [string]$sv.ContractsState
                $contractsVersion = [string]$sv.ContractsVersion
                $openApiFetchState = [string]$sv.OpenApiState
                $openApiVersion = [string]$sv.OpenApiVersion
            }
        }
        if ($Observers.Contains('NugetRevision')) {
            $nr = & $Observers['NugetRevision'] $Version
            if ($null -ne $nr) {
                $nugetRevisionFetchState = [string]$nr.State
                $nugetRevisionCommit = [string]$nr.Commit
            }
        }
        if ($Observers.Contains('PromoteLatestRun')) {
            $runObs = & $Observers['PromoteLatestRun'] $runIdentity
            if ($null -ne $runObs) {
                $promoteRun = [string]$runObs.State
                $promoteRunId = [string]$runObs.Id
            }
        }
    }
    else {
        $gitTag = Get-GitTagObservation -Version $Version
        $ghcr = Get-GhcrVerifyObservation -Version $Version -ReleaseCommitSha $ReleaseCommitSha
        $nuget = Get-NugetObservation -Version $Version
        $githubRelease = Get-GitHubReleaseObservation -Version $Version
        $latest = Get-GhcrLatestObservation
        $sourceVersions = Get-SourceVersionAtCommitObservation -ReleaseCommitSha $ReleaseCommitSha -Version $Version
        $contractsFetchState = [string]$sourceVersions.ContractsState
        $contractsVersion = [string]$sourceVersions.ContractsVersion
        $openApiFetchState = [string]$sourceVersions.OpenApiState
        $openApiVersion = [string]$sourceVersions.OpenApiVersion
        $nugetRevision = Get-NugetSourceRevisionObservation -Version $Version
        $nugetRevisionFetchState = [string]$nugetRevision.State
        $nugetRevisionCommit = [string]$nugetRevision.Commit
        $runFetch = Get-GitHubPromoteLatestWorkflowRuns
        if ($runFetch.State -eq 'INCOMPLETE') {
            $promoteRun = 'INCOMPLETE'
        }
        else {
            $runObs = ConvertTo-PromoteLatestRunObservation -Runs $runFetch.Runs -WorkflowPath $script:PromoteLatestWorkflowPath -RunIdentity $runIdentity
            $promoteRun = $runObs.State
            $promoteRunId = $runObs.Id
        }
    }

    if ($null -eq $gitTag) { $gitTag = New-ArtifactFact -State 'INCOMPLETE' }
    if ($null -eq $ghcr) { $ghcr = New-ArtifactFact -State 'INCOMPLETE' }
    if ($null -eq $nuget) { $nuget = New-ArtifactFact -State 'INCOMPLETE' }
    if ($null -eq $githubRelease) { $githubRelease = New-ArtifactFact -State 'INCOMPLETE' }
    if ($null -eq $latest) { $latest = New-ArtifactFact -State 'INCOMPLETE' }

    $readBackFetcher = $null
    if ($Execute) {
        # Post-dispatch read-back is the matching promote-release-latest workflow run
        # (environment: release may leave latest unchanged until human approval).
        if ($null -ne $Observers -and $Observers.Contains('ReadBackPromoteLatestRun')) {
            $readBackFetcher = New-ReleaseModuleBoundScriptBlock -Capture @{
                Observers   = $Observers
                RunIdentity = $runIdentity
            } -ScriptBlock {
                param($c)
                $obs = & $c.Observers['ReadBackPromoteLatestRun'] $c.RunIdentity
                if ($null -eq $obs) { return 'INCOMPLETE' }
                return [string]$obs.State
            }
        }
        else {
            $readBackFetcher = New-ReleaseModuleBoundScriptBlock -Capture @{
                RunIdentity = $runIdentity
            } -ScriptBlock {
                param($c)
                $null = Get-Command -Name Get-GitHubPromoteLatestWorkflowRuns -CommandType Function -ErrorAction Stop
                $null = Get-Command -Name ConvertTo-PromoteLatestRunObservation -CommandType Function -ErrorAction Stop
                $runFetch = Get-GitHubPromoteLatestWorkflowRuns
                if ($runFetch.State -eq 'INCOMPLETE') { return 'INCOMPLETE' }
                $runObs = ConvertTo-PromoteLatestRunObservation -Runs $runFetch.Runs -WorkflowPath $script:PromoteLatestWorkflowPath -RunIdentity $c.RunIdentity
                return [string]$runObs.State
            }
        }
    }

    $resolvedExecutor = Resolve-ReleaseMutationExecutor -Executor $Executor -Execute:$Execute -CommandName 'promote-latest' -RepoRoot $RepoRoot -CommandRunner $CommandRunner

    $facts = [pscustomobject]@{
        Version                 = $Version
        ReleaseCommitSha        = $ReleaseCommitSha
        ExpectedDigest          = $ExpectedDigest
        GitTag                  = $gitTag
        Ghcr                    = $ghcr
        Nuget                   = $nuget
        GitHubRelease           = $githubRelease
        Latest                  = $latest
        ContractsFetchState     = $contractsFetchState
        ContractsVersion        = $contractsVersion
        OpenApiFetchState       = $openApiFetchState
        OpenApiVersion          = $openApiVersion
        NugetRevisionFetchState = $nugetRevisionFetchState
        NugetRevisionCommit     = $nugetRevisionCommit
        PromoteLatestRun        = $promoteRun
        PromoteLatestRunId      = $promoteRunId
        PromoteLatestRunIdentity = $runIdentity
        ReadBackFetcher         = $readBackFetcher
        Execute                 = [bool]$Execute
        Executor                = $resolvedExecutor
    }

    $map = Get-ReleasePromoteLatestMutationStatus -Facts $facts
    if (-not $Quiet) {
        Write-ReleaseStderr ('release-client: promote-latest VERSION={0} SHA={1} DIGEST={2}' -f $Version, $ReleaseCommitSha, $ExpectedDigest)
        Write-ReleaseStderr ('release-client: MUTATION_RESULT={0} MUTATION_ATTEMPTED={1} MUTATION_PERFORMED={2} LATEST_STATE={3}' -f $map['MUTATION_RESULT'], $map['MUTATION_ATTEMPTED'], $map['MUTATION_PERFORMED'], $map['LATEST_STATE'])
        foreach ($line in (Format-ReleaseMutationLines -Map $map -Keys $script:PromoteLatestMutationKeys)) {
            [Console]::Out.WriteLine($line)
        }
    }
    return $map
}

function Test-PromoteReleaseLatestWorkflowContract {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return 'INCOMPLETE' }
    $lines = ConvertTo-WorkflowActiveLines -Text $Text
    $dispatch = $script:EventWorkflowDispatch
    if (-not (Test-WorkflowYamlPath -Lines $lines -Keys @('on', $dispatch, 'inputs', 'release_version'))) { return 'FAIL' }
    if (-not (Test-WorkflowYamlPathValue -Lines $lines -Keys @('on', $dispatch, 'inputs', 'release_version', 'required') -Value 'true')) { return 'FAIL' }
    if (-not (Test-WorkflowYamlPath -Lines $lines -Keys @('on', $dispatch, 'inputs', 'release_commit_sha'))) { return 'FAIL' }
    if (-not (Test-WorkflowYamlPathValue -Lines $lines -Keys @('on', $dispatch, 'inputs', 'release_commit_sha', 'required') -Value 'true')) { return 'FAIL' }
    if (-not (Test-WorkflowYamlPath -Lines $lines -Keys @('on', $dispatch, 'inputs', 'expected_digest'))) { return 'FAIL' }
    if (-not (Test-WorkflowYamlPathValue -Lines $lines -Keys @('on', $dispatch, 'inputs', 'expected_digest', 'required') -Value 'true')) { return 'FAIL' }
    if (-not (Test-WorkflowJobYamlKeyValue -Lines $lines -Job 'promote-latest' -Key 'environment' -Value 'release')) { return 'FAIL' }
    if (-not (Test-WorkflowJobYamlKeyValue -Lines $lines -Job 'promote-latest' -Key 'packages' -Value 'write')) { return 'FAIL' }
    $writeCount = Get-WorkflowYamlKeyValueCount -Lines $lines -Key 'packages' -Value 'write'
    if ($writeCount -ne 1) { return 'FAIL' }
    if (-not (Test-WorkflowExecutableLineHasAll -Lines $lines -Job 'promote-latest' -Needles @('GITHUB_REF', '==', 'refs/heads/main'))) { return 'FAIL' }
    if (-not (Test-WorkflowExecutableContains -Lines $lines -Job 'promote-latest' -Needle 'promote-release-latest.yml@refs/heads/main')) { return 'FAIL' }
    if (-not (Test-WorkflowExecutableContains -Lines $lines -Job 'promote-latest' -Needle 'install-pinned-crane.sh')) { return 'FAIL' }
    if (-not (Test-WorkflowExecutableContains -Lines $lines -Job 'promote-latest' -Needle 'classify-crane-digest-lookup.sh')) { return 'FAIL' }
    if (-not (Test-WorkflowExecutableContains -Lines $lines -Job 'promote-latest' -Needle 'classify_crane_digest_lookup')) { return 'FAIL' }
    if (-not (Test-WorkflowExecutableContains -Lines $lines -Job 'promote-latest' -Needle 'copy "${IMAGE_REPOSITORY}@${EXPECTED_DIGEST}"')) { return 'FAIL' }
    if (-not (Test-WorkflowExecutableLineHasAll -Lines $lines -Job 'promote-latest' -Needles @('@${EXPECTED_DIGEST}', ':latest'))) { return 'FAIL' }
    if (-not (Test-WorkflowExecutableLineHasAll -Lines $lines -Job 'promote-latest' -Needles @('latest_digest', '==', 'EXPECTED_DIGEST'))) { return 'FAIL' }
    # Fail-close: never swallow latest digest errors as ABSENT via 2>/dev/null.
    if (Test-WorkflowExecutableContains -Lines $lines -Job 'promote-latest' -Needle 'digest "${IMAGE_REPOSITORY}:latest" 2>/dev/null') { return 'FAIL' }
    if (-not (Test-WorkflowExecutableContains -Lines $lines -Job 'promote-latest' -Needle 'latest tag lookup state unknown')) { return 'FAIL' }
    if (-not (Test-WorkflowExecutableContains -Lines $lines -Job 'promote-latest' -Needle 'pre-copy latest lookup state unknown')) { return 'FAIL' }
    $forbidden = @(
        'docker build',
        'docker buildx',
        'buildx build',
        'buildx build --push',
        'docker/setup-buildx-action',
        'docker/build-push-action',
        'scripts/publish-release-image.sh',
        'scripts/build-candidate-oci-image.sh',
        'scripts/assemble-candidate-oci-image.sh'
    )
    if (Test-WorkflowActiveHasAnyNeedle -Lines $lines -Needles $forbidden) { return 'FAIL' }
    if (-not (Test-WorkflowActiveHasAnyNeedle -Lines $lines -Needles @('promote-latest'))) { return 'FAIL' }
    if (-not (Test-WorkflowActiveHasAnyNeedle -Lines $lines -Needles @('inputs.release_version'))) { return 'FAIL' }
    if (-not (Test-WorkflowActiveHasAnyNeedle -Lines $lines -Needles @('inputs.release_commit_sha'))) { return 'FAIL' }
    if (-not (Test-WorkflowActiveHasAnyNeedle -Lines $lines -Needles @('inputs.expected_digest'))) { return 'FAIL' }
    return 'PASS'
}

. (Join-Path $PSScriptRoot 'release-client-post-sync.ps1')
. (Join-Path $PSScriptRoot 'release-client-prepare-version.ps1')

Export-ModuleMember -Function @(
    'ConvertTo-RemotePresence',
    'ConvertTo-RemoteFailureClass',
    'Get-ContractsVersionFromText',
    'Get-OpenApiVersionFromText',
    'Get-ReleaseRecordStateFromText',
    'Get-VersionAlignment',
    'Get-OriginRepositoryIdentity',
    'Resolve-GitTagTargetFromGitHubJson',
    'Test-NugetIndexContainsVersion',
    'Get-OciRevisionFromConfigText',
    'Get-GhcrConfigDigestFromManifest',
    'New-ArtifactFact',
    'New-ReleaseObservations',
    'New-ReleaseModuleBoundScriptBlock',
    'Get-ReleaseSourceAuthority',
    'Get-ReleaseDerivedStatus',
    'Format-ReleaseStatusLines',
    'Test-ReleaseVersion',
    'Invoke-ReleaseStatus',
    'Get-GhcrManifestFact',
    'Resolve-GitHubReleaseStateFromJson',
    'Get-ReleaseSourceBinding',
    'Get-VersionPrepState',
    'Test-ChangelogHasReleaseEntry',
    'Test-PublishImageWorkflowContract',
    'Test-PublishContractsWorkflowContract',
    'Test-VerifyPublicImageWorkflowContract',
    'Test-PromoteReleaseLatestWorkflowContract',
    'Get-ActiveWorkflowLineText',
    'ConvertTo-WorkflowActiveLines',
    'ConvertTo-WorkflowDispatchRunObservation',
    'ConvertTo-PromoteLatestRunObservation',
    'Convert-GitHubWorkflowRunsJson',
    'Get-ReleasePreflightDerivedStatus',
    'Format-ReleasePreflightLines',
    'Invoke-ReleasePreflight',
    'Get-OciVersionFromConfigText',
    'Get-NugetRepositoryCommitFromNuspecText',
    'Get-ReleaseRecordCommitShaFromText',
    'Get-ReleaseRecordDigestFromText',
    'Get-VerifyIdentityRank',
    'ConvertTo-GitTagVerifyState',
    'ConvertTo-GitHubReleaseVerifyState',
    'ConvertTo-NugetPackageVerifyState',
    'ConvertTo-SourceVersionVerifyState',
    'ConvertTo-NugetRevisionVerifyState',
    'ConvertTo-GhcrTagVerifyState',
    'ConvertTo-GhcrShaTagVerifyState',
    'ConvertTo-GhcrDigestBindingVerifyState',
    'ConvertTo-OciRevisionVerifyState',
    'ConvertTo-OciVersionVerifyState',
    'ConvertTo-ReleaseRecordVerifyState',
    'ConvertTo-ExpectedDigestMatchGuardState',
    'ConvertTo-LatestAliasState',
    'ConvertTo-LatestAliasMutationGuardState',
    'ConvertTo-PromoteLatestRunMutationGuardState',
    'ConvertTo-PromoteLatestPostDispatchRunGuardState',
    'ConvertTo-LatestPostReadBackGuardState',
    'Get-PromoteLatestRunIdentity',
    'Get-GitHubFileContentAtRef',
    'Get-SourceVersionAtCommitObservation',
    'Get-NugetSourceRevisionObservation',
    'Get-NugetObservation',
    'Get-NugetSymbolsObservation',
    'Get-ReleaseUtcNow',
    'Format-ReleaseUtcTimestamp',
    'Test-ReleaseUtcTimestamp',
    'Set-ReleaseUtcClockOverride',
    'Get-ReleaseRecordContentForVerify',
    'Get-GhcrVerifyObservation',
    'Get-ReleaseVerifyDerivedStatus',
    'Format-ReleaseVerifyLines',
    'Invoke-ReleaseVerify',
    'Get-MutationGuardRank',
    'ConvertTo-IdentityGuardStates',
    'ConvertTo-GhcrPrerequisiteGuardState',
    'ConvertTo-GhcrPublishTargetGuardState',
    'ConvertTo-GitTagMutationGuardState',
    'ConvertTo-NugetMutationGuardState',
    'ConvertTo-GitHubReleaseMutationGuardState',
    'ConvertTo-WorkflowRunMutationGuardState',
    'ConvertTo-PreflightMutationGuardState',
    'Resolve-ReleaseMutationPrecheck',
    'Resolve-ReleasePromoteLatestPrecheck',
    'Resolve-ReleaseCreateGitHubReleasePostReadBackGuard',
    'Resolve-ReleaseMutationPostAttempt',
    'Format-ReleaseMutationLines',
    'Invoke-ReleaseCommandRunner',
    'New-ReleaseRealCommandRunner',
    'New-ReleaseProductionMutationExecutor',
    'New-ReleaseProductionPublishImageExecutor',
    'New-ReleaseProductionCreateTagExecutor',
    'New-ReleaseProductionPublishNugetExecutor',
    'New-ReleaseProductionCreateGitHubReleaseExecutor',
    'New-ReleaseProductionPromoteLatestExecutor',
    'Resolve-ReleaseMutationExecutor',
    'Get-ReleasePublishImageMutationStatus',
    'Invoke-ReleasePublishImage',
    'Get-ReleaseCreateTagMutationStatus',
    'Invoke-ReleaseCreateTag',
    'Get-ReleasePublishNugetMutationStatus',
    'Invoke-ReleasePublishNuget',
    'Test-ReleaseNotesPathGuard',
    'Get-ReleaseCreateGitHubReleaseMutationStatus',
    'Invoke-ReleaseCreateGitHubRelease',
    'Get-ReleasePromoteLatestMutationStatus',
    'Invoke-ReleasePromoteLatest',
    'ConvertFrom-CurrentPublicAuthorityText',
    'Get-CurrentPublicAuthorityObservation',
    'New-CurrentPublicAuthorityJson',
    'Test-ReleasePostSyncPublicVerify',
    'Get-PostSyncFollowerReplacementRules',
    'Get-PostSyncRulesForPath',
    'Apply-PostSyncReplacementRules',
    'Get-PostSyncFollowerFileState',
    'Get-ReleaseRecordPlatformsFromText',
    'Resolve-PostSyncPlatforms',
    'Update-ReleaseRecordObservableFields',
    'Build-PublishedReleaseRecordForPostSync',
    'Test-PublishedReleaseRecordCoreConsistency',
    'Get-ReleasePreparePostSyncPlan',
    'Format-ReleasePreparePostSyncLines',
    'Invoke-ReleasePreparePostSync',
    'New-PrepareVersionPendingReleaseRecordText',
    'Set-ContractsVersionInText',
    'Set-OpenApiVersionInText',
    'Get-ReleasePrepareVersionPlan',
    'Format-ReleasePrepareVersionLines',
    'Invoke-ReleasePrepareVersion'
)
