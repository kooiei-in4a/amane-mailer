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
        [string]$ShaTagState = '',
        [string]$ShaTagDigest = '',
        [string]$Reason = ''
    )
    return [pscustomobject]@{
        State        = $State
        TargetSha    = $TargetSha
        Digest       = $Digest
        Revision     = $Revision
        ShaTagState  = $ShaTagState
        ShaTagDigest = $ShaTagDigest
        Reason       = $Reason
    }
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
    param([string]$Version)
    $uri = 'https://api.nuget.org/v3-flatcontainer/' + $script:NugetPackageId + '/index.json'
    $resp = Invoke-ReleaseReadOnlyRequest -Uri $uri
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
        return New-ArtifactFact -State 'PRESENT'
    }
    return New-ArtifactFact -State 'ABSENT'
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
        if (-not (Test-ReleaseSha $parsedRevision)) {
            return New-ArtifactFact -State 'INCOMPLETE' -Digest $digest -Reason 'REVISION_PARSE'
        }
        return New-ArtifactFact -State 'PRESENT' -Digest $digest -Revision $parsedRevision
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
        return New-ArtifactFact -State 'PRESENT' -Digest $digest -Revision $child.Revision
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
    $pattern = '^' + $assign + $invocation + '(?=\s|\\|$)'
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

        for ($j = $i + 1; $j -lt $Lines.Count; $j++) {
            $next = $Lines[$j]
            if ($next.Job -ne $Job) { break }
            if (-not $next.Executable) { continue }
            $nextText = Get-WorkflowExecutableText -Line $next
            if ([regex]::IsMatch($nextText, $inlineFailClosePattern, [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)) { return $true }
            break
        }
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
        if ($run.PSObject.Properties['id']) { $id = [string]$run.id }
        if ($run.PSObject.Properties['path']) { $path = [string]$run.path }
        if ($run.PSObject.Properties['event']) { $event = [string]$run.event }
        if ($run.PSObject.Properties['head_sha']) { $head = [string]$run.head_sha }
        if ($run.PSObject.Properties['status']) { $status = [string]$run.status }
        if ($run.PSObject.Properties['conclusion']) { $conclusion = [string]$run.conclusion }
        [void]$list.Add([pscustomobject]@{
            Id         = $id
            Path       = $path
            Event      = $event
            HeadSha    = $head
            Status     = $status
            Conclusion = $conclusion
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
    'Get-ActiveWorkflowLineText',
    'ConvertTo-WorkflowActiveLines',
    'ConvertTo-WorkflowDispatchRunObservation',
    'Convert-GitHubWorkflowRunsJson',
    'Get-ReleasePreflightDerivedStatus',
    'Format-ReleasePreflightLines',
    'Invoke-ReleasePreflight'
)
