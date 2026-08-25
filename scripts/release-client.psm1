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
    return New-ArtifactFact -State 'PRESENT'
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

function Get-GhcrManifestFact {
    param(
        [string]$Reference,
        [string]$Token,
        [switch]$ReadRevision
    )

    $headers = @{
        Authorization = ('Bearer {0}' -f $Token)
        Accept        = 'application/vnd.oci.image.index.v1+json, application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.list.v2+json, application/vnd.docker.distribution.manifest.v2+json'
    }
    $uri = 'https://ghcr.io/v2/' + $script:GhcrRepository + '/manifests/' + $Reference
    $resp = Invoke-ReleaseReadOnlyRequest -Uri $uri -Headers $headers
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

    $revision = ''
    if ($ReadRevision) {
        $mediaType = ''
        if ($resp.BodyText -match '"mediaType"\s*:\s*"([^"]+)"') {
            $mediaType = $Matches[1]
        }
        if ($mediaType -match 'image\.manifest') {
            $configDigest = Get-GhcrConfigDigestFromManifest -ManifestJson $resp.BodyText
            if (Test-ReleaseDigest $configDigest) {
                $blobHeaders = @{
                    Authorization = ('Bearer {0}' -f $Token)
                    Accept        = 'application/vnd.oci.image.config.v1+json'
                }
                $blobUri = 'https://ghcr.io/v2/' + $script:GhcrRepository + '/blobs/' + $configDigest
                $blob = Invoke-ReleaseReadOnlyRequest -Uri $blobUri -Headers $blobHeaders
                $blobPresence = ConvertTo-RemotePresence -StatusCode $blob.StatusCode -TransportFailure $blob.TransportFailure
                if ($blobPresence -eq 'HTTP_OK') {
                    $parsedRevision = Get-OciRevisionFromConfigText -ConfigText $blob.BodyText
                    if (Test-ReleaseSha $parsedRevision) {
                        $revision = $parsedRevision
                    }
                }
            }
        }
        elseif ($mediaType -match 'image\.index|manifest\.list') {
            $childDigests = @([regex]::Matches($resp.BodyText, '"digest"\s*:\s*"(sha256:[0-9a-f]{64})"') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
            if ($childDigests.Count -eq 1) {
                $child = Get-GhcrManifestFact -Reference $childDigests[0] -Token $Token -ReadRevision
                if ($child.State -eq 'PRESENT' -and (Test-ReleaseSha $child.Revision)) {
                    $revision = $child.Revision
                }
            }
        }
    }

    return New-ArtifactFact -State 'PRESENT' -Digest $digest -Revision $revision
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

    if ($local.State -eq 'PASS' -and (Test-ReleaseSha $githubMain.Sha) -and (Test-ReleaseSha $local.LocalMain)) {
        if ($local.LocalMain -ne $githubMain.Sha -or $local.OriginMain -ne $githubMain.Sha) {
            $local.State = 'DRIFT'
            $local.Reason = 'GITHUB_MAIN'
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
    'Invoke-ReleaseStatus'
)
