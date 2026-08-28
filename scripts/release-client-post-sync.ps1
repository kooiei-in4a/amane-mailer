$script:CurrentPublicAuthorityRelativePath = 'release/current-public.json'
$script:PostSyncUtf8 = New-Object System.Text.UTF8Encoding $false

function Read-PostSyncTextFile {
    param([string]$Path)
    return [System.IO.File]::ReadAllText($Path, $script:PostSyncUtf8)
}

function Write-PostSyncTextFile {
    param(
        [string]$Path,
        [string]$Content
    )
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [System.IO.File]::WriteAllText($Path, $Content, $script:PostSyncUtf8)
}

$script:PreparePostSyncKeys = @(
    'COMMAND',
    'VERSION',
    'RELEASE_COMMIT_SHA',
    'AUTHORITY_STATE',
    'FOLLOWER_STATE',
    'FILES_PLANNED',
    'FILES_CHANGED',
    'MUTATION_RESULT',
    'MUTATION_ATTEMPTED',
    'MUTATION_PERFORMED',
    'HUMAN_AUTHORIZATION_REQUIRED'
)

$script:PostSyncPublicVerifyKeys = @(
    'GIT_TAG',
    'CONTRACTS_SOURCE',
    'OPENAPI',
    'NUGET_PACKAGE',
    'NUGET_SOURCE_REVISION',
    'GHCR_VERSION_TAG',
    'GHCR_SHA_TAG',
    'GHCR_DIGEST_BINDING',
    'OCI_REVISION',
    'OCI_VERSION',
    'GITHUB_RELEASE'
)

function Expand-PostSyncTokens {
    param(
        [string]$Template,
        [string]$PrevVersion,
        [string]$TargetVersion
    )
    $prevTag = 'v' + $PrevVersion
    $targetTag = 'v' + $TargetVersion
    return $Template.Replace('{prevVersion}', $PrevVersion).Replace('{prevTag}', $prevTag).Replace('{targetVersion}', $TargetVersion).Replace('{targetTag}', $targetTag)
}

function Get-PostSyncFollowerReplacementRules {
    param(
        [string]$PrevVersion,
        [string]$TargetVersion
    )
    $rules = New-Object System.Collections.Generic.List[hashtable]

    function Add-Rules {
        param([string]$Path, [string[]]$FromTemplates, [string[]]$ToTemplates, [int[]]$ExpectedCounts)
        for ($i = 0; $i -lt $FromTemplates.Count; $i++) {
            [void]$rules.Add(@{
                    Path     = $Path
                    From     = (Expand-PostSyncTokens -Template $FromTemplates[$i] -PrevVersion $PrevVersion -TargetVersion $TargetVersion)
                    To       = (Expand-PostSyncTokens -Template $ToTemplates[$i] -PrevVersion $PrevVersion -TargetVersion $TargetVersion)
                    Expected = $ExpectedCounts[$i]
                })
        }
    }

    Add-Rules 'README.md' @(
        'ghcr.io/kooiei-in4a/amane-mailer:{prevTag}'
        'v{prevVersion} publish'
        'v{prevVersion} release record'
        'docs/releases/v{prevVersion}.md'
        'releases/tag/v{prevVersion}'
        'v{prevVersion} release の GHCR runtime image'
        '既定 smoke tag は `{prevTag}`'
    ) @(
        'ghcr.io/kooiei-in4a/amane-mailer:{targetTag}'
        'v{targetVersion} publish'
        'v{targetVersion} release record'
        'docs/releases/v{targetVersion}.md'
        'releases/tag/v{targetVersion}'
        'v{targetVersion} release の GHCR runtime image'
        '既定 smoke tag は `{targetTag}`'
    ) @(1, 1, 1, 1, 1, 1, 1)

    Add-Rules 'README.en.md' @(
        'ghcr.io/kooiei-in4a/amane-mailer:{prevTag}'
        'After v{prevVersion} is published'
        'v{prevVersion} release record'
        'docs/releases/v{prevVersion}.md'
        'releases/tag/v{prevVersion}'
        'The v{prevVersion} GHCR'
        'default smoke tag is `{prevTag}`'
    ) @(
        'ghcr.io/kooiei-in4a/amane-mailer:{targetTag}'
        'After v{targetVersion} is published'
        'v{targetVersion} release record'
        'docs/releases/v{targetVersion}.md'
        'releases/tag/v{targetVersion}'
        'The v{targetVersion} GHCR'
        'default smoke tag is `{targetTag}`'
    ) @(1, 1, 1, 1, 1, 1, 1)

    Add-Rules 'SECURITY.md' @(
        '| {prevVersion}   | Yes (latest release) |'
    ) @(
        '| {targetVersion}   | Yes (latest release) |'
    ) @(1)

    Add-Rules 'docs/ops/release-image-smoke.md' @(
        'v{prevVersion} publish'
        'ghcr.io/kooiei-in4a/amane-mailer:{prevTag}'
        '- v{prevVersion} release の既定 smoke tag は `{prevTag}`'
        '| `MAILER_IMAGE_TAG` | `{prevTag}` |'
        '`{prevTag}` の value-free smoke 結果'
        '../releases/v{prevVersion}.md'
    ) @(
        'v{targetVersion} publish'
        'ghcr.io/kooiei-in4a/amane-mailer:{targetTag}'
        '- v{targetVersion} release の既定 smoke tag は `{targetTag}`'
        '| `MAILER_IMAGE_TAG` | `{targetTag}` |'
        '`{targetTag}` の value-free smoke 結果'
        '../releases/v{targetVersion}.md'
    ) @(1, 1, 1, 1, 1, 1)

    Add-Rules 'docs/ops/release-image-smoke.en.md' @(
        'After v{prevVersion} is published'
        'ghcr.io/kooiei-in4a/amane-mailer:{prevTag}'
        'For v{prevVersion}, the default smoke tag is `{prevTag}`'
        '| `MAILER_IMAGE_TAG` | `{prevTag}` |'
        'Value-free smoke results for `v{prevVersion}`'
        '../releases/v{prevVersion}.md'
    ) @(
        'After v{targetVersion} is published'
        'ghcr.io/kooiei-in4a/amane-mailer:{targetTag}'
        'For v{targetVersion}, the default smoke tag is `{targetTag}`'
        '| `MAILER_IMAGE_TAG` | `{targetTag}` |'
        'Value-free smoke results for `v{targetVersion}`'
        '../releases/v{targetVersion}.md'
    ) @(1, 1, 1, 1, 1, 1)

    Add-Rules 'scripts/release-smoke.sh' @(
        'ghcr.io/kooiei-in4a/amane-mailer:{prevTag}'
        'MAILER_IMAGE_TAG         default {prevTag}'
        'MAILER_IMAGE_TAG:-{prevTag}'
    ) @(
        'ghcr.io/kooiei-in4a/amane-mailer:{targetTag}'
        'MAILER_IMAGE_TAG         default {targetTag}'
        'MAILER_IMAGE_TAG:-{targetTag}'
    ) @(1, 1, 1)

    Add-Rules 'scripts/release-smoke.ps1' @(
        'ghcr.io/kooiei-in4a/amane-mailer:{prevTag}'
        'MAILER_IMAGE_TAG         default {prevTag}'
        "Get-EnvOrDefault 'MAILER_IMAGE_TAG' '{prevTag}'"
    ) @(
        'ghcr.io/kooiei-in4a/amane-mailer:{targetTag}'
        'MAILER_IMAGE_TAG         default {targetTag}'
        "Get-EnvOrDefault 'MAILER_IMAGE_TAG' '{targetTag}'"
    ) @(1, 1, 1)

    Add-Rules 'infra/docker/docker-compose.release-smoke.yml' @(
        'MAILER_IMAGE_TAG:-{prevTag}'
    ) @(
        'MAILER_IMAGE_TAG:-{targetTag}'
    ) @(2)

    return @($rules)
}

function Get-CurrentPublicAuthorityPath {
    param([string]$RepoRoot)
    return (Join-Path $RepoRoot $script:CurrentPublicAuthorityRelativePath)
}

function ConvertFrom-CurrentPublicAuthorityText {
    param(
        [string]$Text,
        [string]$RepoRoot
    )

    $result = [pscustomobject]@{
        State         = 'INCOMPLETE'
        SchemaVersion = 0
        Version       = ''
        Tag           = ''
        Platforms     = @()
        ReleaseRecord = ''
        Reason        = ''
    }

    if ([string]::IsNullOrWhiteSpace($Text)) {
        $result.Reason = 'EMPTY'
        return $result
    }

    try {
        $parsed = $Text | ConvertFrom-Json
    }
    catch {
        $result.Reason = 'MALFORMED_JSON'
        return $result
    }

    if ($null -eq $parsed) {
        $result.Reason = 'MALFORMED_JSON'
        return $result
    }

    $schemaVersion = 0
    if ($null -ne $parsed.PSObject.Properties['schemaVersion']) {
        $schemaVersion = [int]$parsed.schemaVersion
    }
    if ($schemaVersion -ne 1) {
        $result.Reason = 'UNSUPPORTED_SCHEMA'
        return $result
    }

    $version = [string]$parsed.version
    if (-not (Test-ReleaseVersion $version)) {
        $result.Reason = 'INVALID_VERSION'
        return $result
    }

    $tag = [string]$parsed.tag
    $expectedTag = 'v' + $version
    if ($tag -ne $expectedTag) {
        $result.Reason = 'VERSION_TAG_MISMATCH'
        return $result
    }

    $platforms = @()
    if ($null -ne $parsed.platforms) {
        foreach ($item in @($parsed.platforms)) {
            if (-not [string]::IsNullOrWhiteSpace([string]$item)) {
                $platforms += [string]$item
            }
        }
    }
    if ($platforms.Count -eq 0) {
        $result.Reason = 'EMPTY_PLATFORMS'
        return $result
    }

    $releaseRecord = [string]$parsed.releaseRecord
    $expectedRecord = 'docs/releases/' + $expectedTag + '.md'
    if ($releaseRecord -ne $expectedRecord) {
        $result.Reason = 'INVALID_RELEASE_RECORD'
        return $result
    }

    $recordPath = Join-Path $RepoRoot $releaseRecord
    if (-not (Test-Path -LiteralPath $recordPath)) {
        $result.Reason = 'MISSING_RELEASE_RECORD'
        return $result
    }

    $result.State = 'VALID'
    $result.SchemaVersion = 1
    $result.Version = $version
    $result.Tag = $tag
    $result.Platforms = $platforms
    $result.ReleaseRecord = $releaseRecord
    $result.Reason = ''
    return $result
}

function Get-CurrentPublicAuthorityObservation {
    param([string]$RepoRoot)

    $path = Get-CurrentPublicAuthorityPath -RepoRoot $RepoRoot
    if (-not (Test-Path -LiteralPath $path)) {
        return [pscustomobject]@{ State = 'ABSENT'; Authority = $null; Reason = 'MISSING' }
    }

    try {
        $text = Read-PostSyncTextFile -Path $path
    }
    catch {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Authority = $null; Reason = 'READ' }
    }

    $authority = ConvertFrom-CurrentPublicAuthorityText -Text $text -RepoRoot $RepoRoot
    if ($authority.State -ne 'VALID') {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Authority = $authority; Reason = $authority.Reason }
    }

    return [pscustomobject]@{ State = 'PRESENT'; Authority = $authority; Reason = '' }
}

function New-CurrentPublicAuthorityJson {
    param(
        [string]$Version,
        [string[]]$Platforms = @('linux/amd64')
    )
    $tag = 'v' + $Version
    $record = 'docs/releases/' + $tag + '.md'
    $platformJson = ($Platforms | ForEach-Object { '"{0}"' -f $_ }) -join ', '
    return (@"
{
  "schemaVersion": 1,
  "version": "$Version",
  "tag": "$tag",
  "platforms": [$platformJson],
  "releaseRecord": "$record"
}
"@).TrimEnd()
}

function Test-ReleasePostSyncPublicVerify {
    param($VerifyMap)
    if ($null -eq $VerifyMap) {
        return 'INCOMPLETE'
    }
    foreach ($key in $script:PostSyncPublicVerifyKeys) {
        $value = [string]$VerifyMap[$key]
        if ($value -eq 'INCOMPLETE') { return 'INCOMPLETE' }
        if ($value -ne 'EXACT_MATCH') { return 'FAIL' }
    }
    if (-not (Test-ReleaseDigest ([string]$VerifyMap['PUBLIC_DIGEST']))) {
        return 'INCOMPLETE'
    }
    return 'PASS'
}

function Get-PostSyncReplacementMatchCounts {
    param(
        [string]$Content,
        [hashtable]$Rule
    )
    $fromPattern = [regex]::Escape($Rule.From)
    $toPattern = [regex]::Escape($Rule.To)
    return [pscustomobject]@{
        FromCount = ([regex]::Matches($Content, $fromPattern)).Count
        ToCount   = ([regex]::Matches($Content, $toPattern)).Count
    }
}

function Get-PostSyncFollowerFileState {
    param(
        [string]$Content,
        [hashtable[]]$Rules,
        [ValidateSet('PREDECESSOR', 'TARGET')]
        [string]$Mode = 'PREDECESSOR'
    )

    $conflict = $false

    foreach ($rule in $Rules) {
        $counts = Get-PostSyncReplacementMatchCounts -Content $Content -Rule $rule
        if ($Mode -eq 'PREDECESSOR') {
            if ($counts.ToCount -gt 0) { $conflict = $true }
            if ($counts.FromCount -ne $rule.Expected) { $conflict = $true }
        }
        else {
            if ($counts.FromCount -gt 0) { $conflict = $true }
            if ($counts.ToCount -ne $rule.Expected) { $conflict = $true }
        }
    }

    if ($conflict) { return 'CONFLICT' }
    if ($Mode -eq 'PREDECESSOR') { return 'PREDECESSOR' }
    return 'TARGET'
}

function Apply-PostSyncReplacementRules {
    param(
        [string]$Content,
        [hashtable[]]$Rules
    )
    $updated = $Content
    foreach ($rule in $Rules) {
        $updated = $updated.Replace($rule.From, $rule.To)
    }
    return $updated
}

function Build-PublishedReleaseRecordForPostSync {
    param(
        [string]$Text,
        [string]$Version,
        [string]$ReleaseCommitSha,
        [string]$PublicDigest,
        [string[]]$Platforms
    )

    $recordState = Get-ReleaseRecordStateFromText -Text $Text
    if ($recordState -eq 'PUBLISHED') {
        return [pscustomobject]@{ State = 'ALREADY'; Text = $Text }
    }
    if ($recordState -ne 'PENDING') {
        return [pscustomobject]@{ State = 'CONFLICT'; Text = '' }
    }
    if (-not (Test-ReleaseSha $ReleaseCommitSha)) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Text = '' }
    }
    if (-not (Test-ReleaseDigest $PublicDigest)) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Text = '' }
    }

    $tag = 'v' + $Version
    $shaTag = 'sha-' + $ReleaseCommitSha
    $platformText = ($Platforms -join ', ')
    if ([string]::IsNullOrWhiteSpace($platformText)) {
        $platformText = 'linux/amd64'
    }

    $published = @"
# Release evidence — $tag

> Status: **PUBLISHED**
>
> Version: ``$Version``

## Release identity

- release version: ``$Version``
- releaseCommitSha: ``$ReleaseCommitSha``
- Git tag: ``$tag``
- Git tag target: ``$ReleaseCommitSha``
- Contracts version: ``$Version``
- OpenAPI version: ``$Version``

The post-release documentation commit is not release source. The immutable
``$tag`` tag remains bound to the releaseCommitSha above.

## GHCR

- version tag: ``ghcr.io/kooiei-in4a/amane-mailer:$tag``
- immutable tag:
  ``ghcr.io/kooiei-in4a/amane-mailer:$shaTag``
- public OCI digest:
  ``$PublicDigest``
- supported platform: ``$platformText``

## NuGet

- package: ``Amane.Mailer.Contracts $Version``
- revision / nuspec repository commit:
  ``$ReleaseCommitSha``

## GitHub Release

- release: ``$tag``
- URL: ``https://github.com/kooiei-in4a/amane-mailer/releases/tag/$tag``

## Publication invariants

- same-version GHCR republish: none
- same-version NuGet workflow redispatch: none
- Git tag overwrite / move: none
- ``latest`` tag publication: none
- source rebind after first public artifact: none

$tag remains permanently bound to
``$ReleaseCommitSha``.
"@

    return [pscustomobject]@{ State = 'APPLIED'; Text = $published.TrimEnd() + "`n" }
}

function Get-PostSyncRulesForPath {
    param(
        [string]$RelativePath,
        [hashtable[]]$AllRules
    )
    return @($AllRules | Where-Object { $_.Path -eq $RelativePath })
}

function Get-ReleasePreparePostSyncPlan {
    param(
        [string]$RepoRoot,
        [string]$TargetVersion,
        [string]$ReleaseCommitSha,
        $VerifyMap,
        [bool]$Execute,
        $LocalRepoOverride
    )

    $plan = [pscustomobject]@{
        MutationResult    = 'NOT_ATTEMPTED'
        MutationAttempted = 'FALSE'
        MutationPerformed = 'FALSE'
        AuthorityState    = 'INCOMPLETE'
        FollowerState     = 'INCOMPLETE'
        FilesPlanned      = @()
        FilesChanged      = @()
        FileWrites        = @()
        Reason            = ''
    }

    if (-not (Test-ReleaseVersion $TargetVersion)) {
        $plan.Reason = 'INVALID_VERSION'
        $plan.MutationResult = 'INCOMPLETE'
        return $plan
    }
    if (-not (Test-ReleaseSha $ReleaseCommitSha)) {
        $plan.Reason = 'INVALID_SHA'
        $plan.MutationResult = 'INCOMPLETE'
        return $plan
    }

    $local = if ($null -ne $LocalRepoOverride) { $LocalRepoOverride } else { Get-LocalRepoObservation -RepoRoot $RepoRoot }
    if ($local.State -ne 'PASS') {
        $plan.Reason = 'LOCAL_REPO'
        $plan.MutationResult = 'CONFLICT'
        return $plan
    }
    if ($local.OriginIdentity -ne $script:CanonicalOwnerRepo) {
        $plan.Reason = 'ORIGIN'
        $plan.MutationResult = 'CONFLICT'
        return $plan
    }

    $authorityObs = Get-CurrentPublicAuthorityObservation -RepoRoot $RepoRoot
    if ($authorityObs.State -ne 'PRESENT') {
        $plan.Reason = 'AUTHORITY_' + $authorityObs.Reason
        $plan.MutationResult = 'INCOMPLETE'
        $plan.AuthorityState = 'INCOMPLETE'
        return $plan
    }

    $authority = $authorityObs.Authority
    $publicVerify = Test-ReleasePostSyncPublicVerify -VerifyMap $VerifyMap
    if ($publicVerify -ne 'PASS') {
        $plan.Reason = 'PUBLIC_VERIFY_' + $publicVerify
        $plan.MutationResult = 'INCOMPLETE'
        return $plan
    }

    $publicDigest = [string]$VerifyMap['PUBLIC_DIGEST']
    $targetTag = 'v' + $TargetVersion
    $targetRecord = 'docs/releases/' + $targetTag + '.md'
    $targetRecordPath = Join-Path $RepoRoot $targetRecord
    if (-not (Test-Path -LiteralPath $targetRecordPath)) {
        $plan.Reason = 'MISSING_TARGET_RECORD'
        $plan.MutationResult = 'INCOMPLETE'
        return $plan
    }

    if ($authority.Version -eq $TargetVersion) {
        $plan.AuthorityState = 'EXACT_MATCH'
    }
    else {
        $plan.AuthorityState = 'PREDECESSOR'
        $prevVersion = $authority.Version
    }

    if ($plan.AuthorityState -eq 'EXACT_MATCH') {
        $rules = Get-PostSyncFollowerReplacementRules -PrevVersion '0.0.0' -TargetVersion $TargetVersion
        $followerMode = 'TARGET'
    }
    else {
        $rules = Get-PostSyncFollowerReplacementRules -PrevVersion $prevVersion -TargetVersion $TargetVersion
        $followerMode = 'PREDECESSOR'
    }
    $followerPaths = @(
        'release/current-public.json'
        'README.md'
        'README.en.md'
        'SECURITY.md'
        'docs/ops/release-image-smoke.md'
        'docs/ops/release-image-smoke.en.md'
        'scripts/release-smoke.sh'
        'scripts/release-smoke.ps1'
        'infra/docker/docker-compose.release-smoke.yml'
        $targetRecord
    )

    $allTarget = $true
    $allPredecessor = $true
    $anyConflict = $false

    foreach ($relativePath in $followerPaths) {
        if ($relativePath -eq 'release/current-public.json') {
            if ($authority.Version -eq $TargetVersion) {
                $state = 'TARGET'
            }
            else {
                $state = 'PREDECESSOR'
            }
        }
        elseif ($relativePath -eq $targetRecord) {
            $recordText = Read-PostSyncTextFile -Path $targetRecordPath
            $recordState = Get-ReleaseRecordStateFromText -Text $recordText
            if ($recordState -eq 'PUBLISHED') {
                $state = 'TARGET'
            }
            elseif ($recordState -eq 'PENDING') {
                $state = 'PREDECESSOR'
            }
            else {
                $state = 'CONFLICT'
            }
        }
        else {
            $fullPath = Join-Path $RepoRoot $relativePath
            if (-not (Test-Path -LiteralPath $fullPath)) {
                $state = 'CONFLICT'
            }
            else {
                $content = Read-PostSyncTextFile -Path $fullPath
                $pathRules = Get-PostSyncRulesForPath -RelativePath $relativePath -AllRules $rules
                if ($pathRules.Count -eq 0) {
                    $state = 'CONFLICT'
                }
                else {
                    $state = Get-PostSyncFollowerFileState -Content $content -Rules $pathRules -Mode $followerMode
                }
            }
        }

        if ($state -eq 'CONFLICT') { $anyConflict = $true }
        if ($state -ne 'TARGET') { $allTarget = $false }
        if ($state -ne 'PREDECESSOR') { $allPredecessor = $false }
    }

    if ($anyConflict) {
        $plan.FollowerState = 'CONFLICT'
        $plan.MutationResult = 'CONFLICT'
        $plan.Reason = 'MIXED_FOLLOWERS'
        $plan.FilesPlanned = @($followerPaths | Sort-Object)
        return $plan
    }

    if ($plan.AuthorityState -eq 'EXACT_MATCH' -and $allTarget) {
        $plan.FollowerState = 'TARGET'
        $plan.MutationResult = 'ALREADY_APPLIED'
        $plan.FilesPlanned = @($followerPaths | Sort-Object)
        return $plan
    }

    if ($plan.AuthorityState -eq 'PREDECESSOR' -and $allPredecessor) {
        $plan.FollowerState = 'PREDECESSOR'
    }
    elseif ($plan.AuthorityState -eq 'EXACT_MATCH' -and -not $allTarget) {
        $plan.FollowerState = 'CONFLICT'
        $plan.MutationResult = 'CONFLICT'
        $plan.Reason = 'AUTHORITY_TARGET_FOLLOWER_DRIFT'
        $plan.FilesPlanned = @($followerPaths | Sort-Object)
        return $plan
    }
    else {
        $plan.FollowerState = 'CONFLICT'
        $plan.MutationResult = 'CONFLICT'
        $plan.Reason = 'AUTHORITY_FOLLOWER_MISMATCH'
        $plan.FilesPlanned = @($followerPaths | Sort-Object)
        return $plan
    }

    $applyRules = Get-PostSyncFollowerReplacementRules -PrevVersion $authority.Version -TargetVersion $TargetVersion

    $planned = New-Object System.Collections.Generic.List[string]
    $writes = New-Object System.Collections.Generic.List[hashtable]

    [void]$planned.Add('release/current-public.json')
    $newAuthority = New-CurrentPublicAuthorityJson -Version $TargetVersion -Platforms $authority.Platforms
    [void]$writes.Add(@{ Path = 'release/current-public.json'; Content = $newAuthority })

    foreach ($relativePath in $followerPaths) {
        if ($relativePath -eq 'release/current-public.json') { continue }
        if ($relativePath -eq $targetRecord) {
            [void]$planned.Add($targetRecord)
            $recordText = Read-PostSyncTextFile -Path $targetRecordPath
            $recordBuild = Build-PublishedReleaseRecordForPostSync -Text $recordText -Version $TargetVersion -ReleaseCommitSha $ReleaseCommitSha -PublicDigest $publicDigest -Platforms $authority.Platforms
            if ($recordBuild.State -eq 'ALREADY') { continue }
            if ($recordBuild.State -ne 'APPLIED') {
                $plan.FollowerState = 'CONFLICT'
                $plan.MutationResult = 'INCOMPLETE'
                $plan.Reason = 'RELEASE_RECORD_' + $recordBuild.State
                return $plan
            }
            [void]$writes.Add(@{ Path = $targetRecord; Content = $recordBuild.Text })
            continue
        }

        [void]$planned.Add($relativePath)
        $fullPath = Join-Path $RepoRoot $relativePath
        $content = Read-PostSyncTextFile -Path $fullPath
        $pathRules = Get-PostSyncRulesForPath -RelativePath $relativePath -AllRules $applyRules
        $updated = Apply-PostSyncReplacementRules -Content $content -Rules $pathRules
        if ($updated -ne $content) {
            [void]$writes.Add(@{ Path = $relativePath; Content = $updated })
        }
    }

    $plan.FilesPlanned = @($planned | Sort-Object)
    $plan.FileWrites = @($writes)

    if (-not $Execute) {
        $plan.MutationResult = 'NOT_ATTEMPTED'
        return $plan
    }

    $changed = New-Object System.Collections.Generic.List[string]
    foreach ($write in $writes) {
        $fullPath = Join-Path $RepoRoot $write.Path
        Write-PostSyncTextFile -Path $fullPath -Content $write.Content
        [void]$changed.Add($write.Path)
    }

    $plan.MutationAttempted = 'TRUE'
    $plan.MutationPerformed = 'TRUE'
    $plan.MutationResult = 'APPLIED'
    $plan.FilesChanged = @($changed | Sort-Object)
    return $plan
}

function Format-ReleasePreparePostSyncLines {
    param($Plan, [string]$Version, [string]$ReleaseCommitSha)
    $map = [ordered]@{}
    $map['COMMAND'] = 'PREPARE_POST_SYNC'
    $map['VERSION'] = $Version
    $map['RELEASE_COMMIT_SHA'] = $ReleaseCommitSha
    $map['AUTHORITY_STATE'] = $Plan.AuthorityState
    $map['FOLLOWER_STATE'] = $Plan.FollowerState
    $map['FILES_PLANNED'] = ([string]::Join(',', @($Plan.FilesPlanned)))
    if ($null -eq $Plan.FilesChanged -or $Plan.FilesChanged.Count -eq 0) {
        $map['FILES_CHANGED'] = 'NONE'
    }
    else {
        $map['FILES_CHANGED'] = ([string]::Join(',', @($Plan.FilesChanged)))
    }
    $map['MUTATION_RESULT'] = $Plan.MutationResult
    $map['MUTATION_ATTEMPTED'] = $Plan.MutationAttempted
    $map['MUTATION_PERFORMED'] = $Plan.MutationPerformed
    $map['HUMAN_AUTHORIZATION_REQUIRED'] = 'TRUE'

    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($key in $script:PreparePostSyncKeys) {
        $value = [string]$map[$key]
        $value = $value -replace '[\r\n]+', ' '
        [void]$lines.Add(('{0}={1}' -f $key, $value))
    }
    return $lines
}

function Invoke-ReleasePreparePostSync {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version,
        [Parameter(Mandatory = $true)]
        [string]$ReleaseCommitSha,
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        $Observers,
        $LocalRepoOverride,
        [switch]$Execute,
        [switch]$Quiet
    )

    if (-not (Test-ReleaseVersion $Version)) {
        throw 'Version must be X.Y.Z; the client does not infer or accept a v-prefix.'
    }
    if (-not (Test-ReleaseSha $ReleaseCommitSha)) {
        throw 'ReleaseCommitSha must be a 40-character lowercase hex git commit.'
    }

    $verifyMap = Invoke-ReleaseVerify -Version $Version -ReleaseCommitSha $ReleaseCommitSha -RepoRoot $RepoRoot -Observers $Observers -Quiet

    $plan = Get-ReleasePreparePostSyncPlan -RepoRoot $RepoRoot -TargetVersion $Version -ReleaseCommitSha $ReleaseCommitSha -VerifyMap $verifyMap -Execute:$Execute -LocalRepoOverride $LocalRepoOverride

    if (-not $Quiet) {
        Write-ReleaseStderr ('release-client: prepare-post-sync VERSION={0} SHA={1}' -f $Version, $ReleaseCommitSha)
        Write-ReleaseStderr ('release-client: MUTATION_RESULT={0} MUTATION_ATTEMPTED={1} MUTATION_PERFORMED={2}' -f $plan.MutationResult, $plan.MutationAttempted, $plan.MutationPerformed)
        foreach ($line in (Format-ReleasePreparePostSyncLines -Plan $plan -Version $Version -ReleaseCommitSha $ReleaseCommitSha)) {
            [Console]::Out.WriteLine($line)
        }
    }

    return [pscustomobject]@{
        Plan      = $plan
        VerifyMap = $verifyMap
    }
}
