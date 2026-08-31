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

function Get-PostSyncJaPatternTokens {
    $no = [char]0x306E
    $ha = [char]0x306F
    $kitei = ([char]0x65E2).ToString() + [char]0x5B9A
    $kekkka = ([char]0x7D50).ToString() + [char]0x679C
    return [pscustomobject]@{
        No     = $no
        Ha     = $ha
        Kitei  = $kitei
        Kekkka = $kekkka
    }
}

function Get-PostSyncFollowerReplacementRules {
    param(
        [string]$PrevVersion,
        [string]$TargetVersion
    )
    $rules = New-Object System.Collections.Generic.List[hashtable]
    $ja = Get-PostSyncJaPatternTokens

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
        ('v{prevVersion} release ' + $ja.No + ' GHCR runtime image')
        ($ja.Kitei + ' smoke tag ' + $ja.Ha + ' `{prevTag}`')
    ) @(
        'ghcr.io/kooiei-in4a/amane-mailer:{targetTag}'
        'v{targetVersion} publish'
        'v{targetVersion} release record'
        'docs/releases/v{targetVersion}.md'
        'releases/tag/v{targetVersion}'
        ('v{targetVersion} release ' + $ja.No + ' GHCR runtime image')
        ($ja.Kitei + ' smoke tag ' + $ja.Ha + ' `{targetTag}`')
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
        ('- v{prevVersion} release ' + $ja.No + $ja.Kitei + ' smoke tag ' + $ja.Ha + ' `{prevTag}`')
        '| `MAILER_IMAGE_TAG` | `{prevTag}` |'
        ('`{prevTag}` ' + $ja.No + ' value-free smoke ' + $ja.Kekkka)
        # Label and href are separate deterministic rules so href-only rewrite cannot leave a stale label.
        '[docs/releases/v{prevVersion}.md]'
        '../releases/v{prevVersion}.md'
    ) @(
        'v{targetVersion} publish'
        'ghcr.io/kooiei-in4a/amane-mailer:{targetTag}'
        ('- v{targetVersion} release ' + $ja.No + $ja.Kitei + ' smoke tag ' + $ja.Ha + ' `{targetTag}`')
        '| `MAILER_IMAGE_TAG` | `{targetTag}` |'
        ('`{targetTag}` ' + $ja.No + ' value-free smoke ' + $ja.Kekkka)
        '[docs/releases/v{targetVersion}.md]'
        '../releases/v{targetVersion}.md'
    ) @(1, 1, 1, 1, 1, 1, 1)

    Add-Rules 'docs/ops/release-image-smoke.en.md' @(
        'After v{prevVersion} is published'
        'ghcr.io/kooiei-in4a/amane-mailer:{prevTag}'
        'For v{prevVersion}, the default smoke tag is `{prevTag}`'
        '| `MAILER_IMAGE_TAG` | `{prevTag}` |'
        'Value-free smoke results for `v{prevVersion}`'
        # Label and href are separate deterministic rules so href-only rewrite cannot leave a stale label.
        '[docs/releases/v{prevVersion}.md]'
        '../releases/v{prevVersion}.md'
    ) @(
        'After v{targetVersion} is published'
        'ghcr.io/kooiei-in4a/amane-mailer:{targetTag}'
        'For v{targetVersion}, the default smoke tag is `{targetTag}`'
        '| `MAILER_IMAGE_TAG` | `{targetTag}` |'
        'Value-free smoke results for `v{targetVersion}`'
        '[docs/releases/v{targetVersion}.md]'
        '../releases/v{targetVersion}.md'
    ) @(1, 1, 1, 1, 1, 1, 1)

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
        if ($rule.From -eq $rule.To) {
            if ($counts.FromCount -ne $rule.Expected) { $conflict = $true }
            continue
        }
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

function Get-ReleaseRecordPlatformsFromText {
    param([string]$Text)
    $platforms = New-Object System.Collections.Generic.List[string]
    if ([string]::IsNullOrWhiteSpace($Text)) {
        return @()
    }
    foreach ($line in ($Text -split '\r?\n')) {
        if ($line -notmatch '(?i)support(ed)? platform|required platforms') {
            continue
        }
        if ($line -match 'PENDING|NOT YET PUBLISHED') {
            continue
        }
        $matches = [regex]::Matches($line, '`{1,2}(linux/[a-z0-9_-]+)`{1,2}')
        foreach ($match in $matches) {
            $value = $match.Groups[1].Value
            if ($platforms -notcontains $value) {
                [void]$platforms.Add($value)
            }
        }
    }
    return @($platforms)
}

function Resolve-PostSyncPlatforms {
    param(
        [string]$RecordText,
        [string[]]$AuthorityPlatforms
    )

    $recordPlatforms = @(Get-ReleaseRecordPlatformsFromText -Text $RecordText)
    if ($recordPlatforms.Count -gt 0) {
        return [pscustomobject]@{ State = 'RESOLVED'; Platforms = $recordPlatforms; Reason = '' }
    }

    $authorityPlatforms = @($AuthorityPlatforms)
    if ($authorityPlatforms.Count -eq 0) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Platforms = @(); Reason = 'EMPTY_AUTHORITY_PLATFORMS' }
    }

    foreach ($platform in $authorityPlatforms) {
        if ($RecordText -notmatch [regex]::Escape($platform)) {
            return [pscustomobject]@{ State = 'INCOMPLETE'; Platforms = @(); Reason = 'PLATFORM_NOT_CONFIRMED' }
        }
    }

    return [pscustomobject]@{ State = 'RESOLVED'; Platforms = $authorityPlatforms; Reason = '' }
}

function Test-ReleaseRecordLineHasPendingValue {
    param([string]$Line)
    return ($Line -match '\*\*PENDING(?:[^*]*)\*\*|`PENDING`|PENDING|NOT YET PUBLISHED|TO BE RECORDED AFTER PROMOTION')
}

function Set-ReleaseRecordStatusPublished {
    param([string]$Text)

    $updatedLines = New-Object System.Collections.Generic.List[string]
    foreach ($line in ($Text -split '\r?\n', 0)) {
        if ($line -match '(?m)^>\s*Status:\s*\*\*') {
            [void]$updatedLines.Add('> Status: **PUBLISHED**')
        }
        else {
            [void]$updatedLines.Add($line)
        }
    }

    return [string]::Join("`n", $updatedLines)
}

function Test-PublishedReleaseRecordCoreConsistency {
    param(
        [string]$Text,
        [string]$Version,
        [string]$ReleaseCommitSha,
        [string]$PublicDigest
    )

    $tag = 'v' + $Version
    $shaTag = 'sha-' + $ReleaseCommitSha
    $ghcrVersionTag = 'ghcr.io/kooiei-in4a/amane-mailer:' + $tag
    $ghcrShaTag = 'ghcr.io/kooiei-in4a/amane-mailer:' + $shaTag
    $nugetPackage = 'Amane.Mailer.Contracts ' + $Version
    $releaseUrl = 'https://github.com/kooiei-in4a/amane-mailer/releases/tag/' + $tag
    $failures = New-Object System.Collections.Generic.List[string]
    $escSha = [regex]::Escape($ReleaseCommitSha)
    $escDigest = [regex]::Escape($PublicDigest)
    $escTag = [regex]::Escape($tag)
    $escShaTag = [regex]::Escape($shaTag)
    $escGhcrVersion = [regex]::Escape($ghcrVersionTag)
    $escGhcrSha = [regex]::Escape($ghcrShaTag)
    $escNuget = [regex]::Escape($nugetPackage)
    $escUrl = [regex]::Escape($releaseUrl)

    foreach ($line in ($Text -split '\r?\n')) {
        if ($line -match '(?m)^>\s*Status:') { continue }
        if ($line -match '^\s*-\s+' -and $line -match 'NOT YET PUBLISHED') {
            [void]$failures.Add('CORE_NOT_YET_PUBLISHED')
            break
        }
    }

    $shaFieldOk = ($Text -match ('(?m)^-\s+`?releaseCommitSha`?:\s+`{1,2}' + $escSha + '`{1,2}\s*$')) -or
        ($Text -match ('Exact release source commit \(``?releaseCommitSha``?\):\s+``' + $escSha + '``'))
    if (-not $shaFieldOk) {
        [void]$failures.Add('RELEASE_COMMIT_SHA')
    }

    $gitTagOk = ($Text -match ('(?m)^-\s+Git tag `' + $escTag + '`:\s+\*\*PUBLISHED\*\*')) -or
        ($Text -match ('(?m)^-\s+Git tag:\s+``' + $escTag + '``\s*$'))
    if (-not $gitTagOk) {
        [void]$failures.Add('GIT_TAG')
    }

    $gitTagTargetOk = ($Text -match ('(?m)^-\s+Git tag target:\s+`{1,2}' + $escSha + '`{1,2}\s*$'))
    if (-not $gitTagTargetOk) {
        [void]$failures.Add('GIT_TAG_TARGET')
    }

    $ghcrVersionOk = ($Text -match ('(?m)^-\s+GHCR `' + $escGhcrVersion + '`:\s+\*\*PUBLISHED\*\*')) -or
        ($Text -match ('(?m)^-\s+version tag:\s+``' + $escGhcrVersion + '``\s*$'))
    if (-not $ghcrVersionOk) {
        [void]$failures.Add('GHCR_VERSION')
    }

    $ghcrImmutableOk = ($Text -match ('(?m)^-\s+GHCR immutable `' + $escShaTag + '` tag:\s+\*\*PUBLISHED\*\*')) -or
        ($Text -match ('immutable tag:[\s`]*' + $escGhcrSha))
    if (-not $ghcrImmutableOk) {
        [void]$failures.Add('GHCR_IMMUTABLE')
    }

    $digestOk = ($Text -match ('(?im)^-\s+public OCI digest:\s+`{1,2}' + $escDigest + '`{1,2}\s*$')) -or
        ($Text -match ('(?is)public OCI digest:\s*(?:\r?\n\s*)?`{1,2}' + $escDigest + '`{1,2}'))
    if (-not $digestOk) {
        [void]$failures.Add('PUBLIC_DIGEST')
    }

    $nugetOk = ($Text -match ('(?m)^-\s+NuGet `' + $escNuget + '`:\s+\*\*PUBLISHED\*\*')) -or
        ($Text -match ('(?m)^-\s+package:\s+``' + $escNuget + '``\s*$'))
    if (-not $nugetOk) {
        [void]$failures.Add('NUGET')
    }

    $nugetRevOk = ($Text -match ('(?m)^-\s+NuGet SourceLink revision:\s+`{1,2}' + $escSha + '`{1,2}\s*$')) -or
        ($Text -match ('(?is)revision / nuspec repository commit:\s*(?:\r?\n\s*)?`{1,2}' + $escSha + '`{1,2}'))
    if (-not $nugetRevOk) {
        [void]$failures.Add('NUGET_REVISION')
    }

    $githubReleaseOk = ($Text -match ('(?m)^-\s+GitHub Release `' + $escTag + '`:\s+\*\*PUBLISHED\*\*')) -or
        ($Text -match ('(?m)^-\s+release:\s+``' + $escTag + '``\s*$'))
    if (-not $githubReleaseOk) {
        [void]$failures.Add('GITHUB_RELEASE')
    }

    $githubUrlOk = ($Text -match ('(?m)^-\s+GitHub Release URL:\s+`{1,2}' + $escUrl + '`{1,2}\s*$')) -or
        ($Text -match ('(?m)^-\s+URL:\s+``' + $escUrl + '``\s*$'))
    if (-not $githubUrlOk) {
        [void]$failures.Add('GITHUB_RELEASE_URL')
    }

    if ($failures.Count -eq 0) {
        return [pscustomobject]@{ State = 'PASS'; Reason = '' }
    }

    $unique = @($failures | Select-Object -Unique)
    return [pscustomobject]@{ State = 'CONFLICT'; Reason = ($unique -join ',') }
}

# --- Issue #691: observed post-sync evidence contract (schemaVersion=1) ---

function Test-PostSyncObservationState {
    param([string]$State)
    return ($State -eq 'OBSERVED' -or $State -eq 'PENDING')
}

function Test-PostSyncConsumerResult {
    param([string]$Result)
    return ($Result -eq 'PASS' -or $Result -eq 'FAIL')
}

function Test-PostSyncWorkflowResult {
    param([string]$Result)
    return ($Result -eq 'SUCCESS' -or $Result -eq 'FAILURE' -or $Result -eq 'CANCELLED' -or $Result -eq 'SKIPPED')
}

function Test-PostSyncPositiveInteger {
    param($Value)
    if ($null -eq $Value) { return $false }
    try {
        $n = [long]$Value
        return ($n -gt 0)
    }
    catch {
        return $false
    }
}

function Get-PostSyncObservedEvidenceGroupState {
    param($Group)
    if ($null -eq $Group) { return 'MISSING' }
    if ($null -eq $Group.PSObject.Properties['state']) { return 'MISSING' }
    $state = [string]$Group.state
    if (-not (Test-PostSyncObservationState -State $state)) { return 'INVALID' }
    return $state
}

function ConvertTo-PostSyncEvidenceUtcString {
    param($Value)
    if ($null -eq $Value) { return '' }
    if ($Value -is [datetime]) {
        return $Value.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    }
    return [string]$Value
}

function Get-PostSyncEvidenceNestedGroup {
    param(
        $Group,
        [string]$Name
    )
    if ($null -eq $Group) { return $null }
    if ($null -eq $Group.PSObject.Properties[$Name]) { return $null }
    return $Group.$Name
}

function Get-PostSyncEvidencePropertyValue {
    param(
        $Group,
        [string]$Name
    )
    if ($null -eq $Group) { return $null }
    if ($null -eq $Group.PSObject.Properties[$Name]) { return $null }
    return $Group.$Name
}

function Test-PostSyncObservedEvidenceGroupHasObservedValues {
    param(
        $Group,
        [string[]]$PropertyNames
    )
    foreach ($name in $PropertyNames) {
        $value = Get-PostSyncEvidencePropertyValue -Group $Group -Name $name
        if ($null -ne $value -and -not [string]::IsNullOrWhiteSpace([string]$value)) {
            return $true
        }
    }
    return $false
}

function Read-PostSyncObservedEvidenceFile {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Evidence = $null; Reason = 'MISSING_PATH' }
    }
    if (-not (Test-Path -LiteralPath $Path)) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Evidence = $null; Reason = 'MISSING_FILE' }
    }
    try {
        $text = Read-PostSyncTextFile -Path $Path
        $parsed = $text | ConvertFrom-Json
    }
    catch {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Evidence = $null; Reason = 'MALFORMED_JSON' }
    }
    if ($null -eq $parsed) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Evidence = $null; Reason = 'MALFORMED_JSON' }
    }
    return [pscustomobject]@{ State = 'PARSED'; Evidence = $parsed; Reason = '' }
}

function Test-PostSyncObservedEvidenceSchema {
    param($Evidence)

    if ($null -eq $Evidence) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Reason = 'NULL_EVIDENCE' }
    }

    $schemaVersion = 0
    if ($null -ne $Evidence.PSObject.Properties['schemaVersion']) {
        $schemaVersion = [int]$Evidence.schemaVersion
    }
    if ($schemaVersion -ne 1) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Reason = 'UNSUPPORTED_SCHEMA' }
    }

    $requiredTop = @('version', 'releaseCommitSha', 'publicOciDigest', 'supportedPlatforms')
    foreach ($name in $requiredTop) {
        if ($null -eq $Evidence.PSObject.Properties[$name]) {
            return [pscustomobject]@{ State = 'INCOMPLETE'; Reason = ('MISSING_' + $name.ToUpperInvariant()) }
        }
    }

    if (-not (Test-ReleaseVersion ([string]$Evidence.version))) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Reason = 'INVALID_VERSION' }
    }
    if (-not (Test-ReleaseSha ([string]$Evidence.releaseCommitSha))) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Reason = 'INVALID_RELEASE_COMMIT_SHA' }
    }
    if (-not (Test-ReleaseDigest ([string]$Evidence.publicOciDigest))) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Reason = 'INVALID_PUBLIC_OCI_DIGEST' }
    }

    $platforms = @()
    foreach ($item in @($Evidence.supportedPlatforms)) {
        if (-not [string]::IsNullOrWhiteSpace([string]$item)) {
            $platforms += [string]$item
        }
    }
    if ($platforms.Count -eq 0) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Reason = 'EMPTY_SUPPORTED_PLATFORMS' }
    }

    $groupNames = @(
        'annotatedTagObject'
        'releaseImageWorkflow'
        'publicationArtifact'
        'publicationEvidenceArtifact'
        'versionedGhcrConsumerVerification'
        'githubRelease'
        'latestPromotion'
        'overallConsumerVerification'
    )
    foreach ($groupName in $groupNames) {
        if ($null -eq $Evidence.PSObject.Properties[$groupName]) {
            return [pscustomobject]@{ State = 'INCOMPLETE'; Reason = ('MISSING_' + $groupName.ToUpperInvariant()) }
        }
        $groupState = Get-PostSyncObservedEvidenceGroupState -Group $Evidence.$groupName
        if ($groupState -eq 'MISSING' -or $groupState -eq 'INVALID') {
            return [pscustomobject]@{ State = 'INCOMPLETE'; Reason = ('INVALID_' + $groupName.ToUpperInvariant() + '_STATE') }
        }
    }

    if ($null -eq $Evidence.PSObject.Properties['nuget']) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Reason = 'MISSING_NUGET' }
    }
    $nuget = $Evidence.nuget
    foreach ($name in @('version', 'repositoryRevision', 'symbolObservation', 'cleanConsumerVerification')) {
        if ($null -eq $nuget.PSObject.Properties[$name]) {
            return [pscustomobject]@{ State = 'INCOMPLETE'; Reason = ('MISSING_NUGET_' + $name.ToUpperInvariant()) }
        }
    }
    if ($null -eq $nuget.PSObject.Properties['publicObservedAtUtc']) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Reason = 'MISSING_NUGET_PUBLIC_OBSERVED_AT_UTC' }
    }

    $failReasons = New-Object System.Collections.Generic.List[string]
    $tagObj = $Evidence.annotatedTagObject
    $tagState = Get-PostSyncObservedEvidenceGroupState -Group $tagObj
    if ($tagState -eq 'OBSERVED') {
        if (-not (Test-ReleaseSha ([string](Get-PostSyncEvidencePropertyValue -Group $tagObj -Name 'sha')))) { [void]$failReasons.Add('ANNOTATED_TAG_OBJECT_SHA') }
    }
    elseif (Test-PostSyncObservedEvidenceGroupHasObservedValues -Group $tagObj -PropertyNames @('sha')) {
        [void]$failReasons.Add('ANNOTATED_TAG_OBJECT_AMBIGUOUS')
    }

    $imageWorkflow = $Evidence.releaseImageWorkflow
    $imageState = Get-PostSyncObservedEvidenceGroupState -Group $imageWorkflow
    if ($imageState -eq 'OBSERVED') {
        if (-not (Test-PostSyncPositiveInteger (Get-PostSyncEvidencePropertyValue -Group $imageWorkflow -Name 'runId'))) { [void]$failReasons.Add('RELEASE_IMAGE_WORKFLOW_RUN_ID') }
        elseif (-not (Test-PostSyncPositiveInteger (Get-PostSyncEvidencePropertyValue -Group $imageWorkflow -Name 'attempt'))) { [void]$failReasons.Add('RELEASE_IMAGE_WORKFLOW_ATTEMPT') }
        elseif (-not (Test-PostSyncWorkflowResult ([string](Get-PostSyncEvidencePropertyValue -Group $imageWorkflow -Name 'result')))) { [void]$failReasons.Add('RELEASE_IMAGE_WORKFLOW_RESULT') }
    }
    elseif (Test-PostSyncObservedEvidenceGroupHasObservedValues -Group $imageWorkflow -PropertyNames @('runId', 'attempt', 'result')) {
        [void]$failReasons.Add('RELEASE_IMAGE_WORKFLOW_AMBIGUOUS')
    }

    foreach ($artifactName in @('publicationArtifact', 'publicationEvidenceArtifact')) {
        $artifact = $Evidence.$artifactName
        $artifactState = Get-PostSyncObservedEvidenceGroupState -Group $artifact
        if ($artifactState -eq 'OBSERVED') {
            if ([string]::IsNullOrWhiteSpace([string](Get-PostSyncEvidencePropertyValue -Group $artifact -Name 'name'))) { [void]$failReasons.Add(($artifactName.ToUpperInvariant() + '_NAME')) }
            elseif (-not (Test-PostSyncPositiveInteger (Get-PostSyncEvidencePropertyValue -Group $artifact -Name 'id'))) { [void]$failReasons.Add(($artifactName.ToUpperInvariant() + '_ID')) }
        }
        elseif (Test-PostSyncObservedEvidenceGroupHasObservedValues -Group $artifact -PropertyNames @('name', 'id')) {
            [void]$failReasons.Add(($artifactName.ToUpperInvariant() + '_AMBIGUOUS'))
        }
    }

    $versionedConsumer = $Evidence.versionedGhcrConsumerVerification
    $versionedConsumerState = Get-PostSyncObservedEvidenceGroupState -Group $versionedConsumer
    if ($versionedConsumerState -eq 'OBSERVED') {
        if (-not (Test-PostSyncConsumerResult ([string](Get-PostSyncEvidencePropertyValue -Group $versionedConsumer -Name 'result')))) { [void]$failReasons.Add('VERSIONED_GHCR_CONSUMER_RESULT') }
    }
    elseif (Test-PostSyncObservedEvidenceGroupHasObservedValues -Group $versionedConsumer -PropertyNames @('result')) {
        [void]$failReasons.Add('VERSIONED_GHCR_CONSUMER_AMBIGUOUS')
    }

    $symbol = $nuget.symbolObservation
    $symbolState = Get-PostSyncObservedEvidenceGroupState -Group $symbol
    if ($symbolState -eq 'OBSERVED') {
        $symbolStatus = [string](Get-PostSyncEvidencePropertyValue -Group $symbol -Name 'status')
        if ($symbolStatus -ne 'OBSERVED' -and $symbolStatus -ne 'PASS') { [void]$failReasons.Add('NUGET_SYMBOL_STATUS') }
    }
    elseif (Test-PostSyncObservedEvidenceGroupHasObservedValues -Group $symbol -PropertyNames @('status')) {
        [void]$failReasons.Add('NUGET_SYMBOL_AMBIGUOUS')
    }

    $cleanConsumer = $nuget.cleanConsumerVerification
    $cleanConsumerState = Get-PostSyncObservedEvidenceGroupState -Group $cleanConsumer
    if ($cleanConsumerState -eq 'OBSERVED') {
        if (-not (Test-PostSyncConsumerResult ([string](Get-PostSyncEvidencePropertyValue -Group $cleanConsumer -Name 'result')))) { [void]$failReasons.Add('NUGET_CLEAN_CONSUMER_RESULT') }
    }
    elseif (Test-PostSyncObservedEvidenceGroupHasObservedValues -Group $cleanConsumer -PropertyNames @('result')) {
        [void]$failReasons.Add('NUGET_CLEAN_CONSUMER_AMBIGUOUS')
    }

    $observedAt = ConvertTo-PostSyncEvidenceUtcString (Get-PostSyncEvidencePropertyValue -Group $nuget -Name 'publicObservedAtUtc')
    if (-not [string]::IsNullOrWhiteSpace($observedAt) -and -not (Test-ReleaseUtcTimestamp -Value $observedAt)) {
        [void]$failReasons.Add('NUGET_PUBLIC_OBSERVED_AT_UTC')
    }

    $github = $Evidence.githubRelease
    $githubState = Get-PostSyncObservedEvidenceGroupState -Group $github
    if ($githubState -eq 'OBSERVED') {
        if (-not (Test-PostSyncPositiveInteger (Get-PostSyncEvidencePropertyValue -Group $github -Name 'id'))) { [void]$failReasons.Add('GITHUB_RELEASE_ID') }
        elseif (-not (Test-ReleaseUtcTimestamp (ConvertTo-PostSyncEvidenceUtcString (Get-PostSyncEvidencePropertyValue -Group $github -Name 'publishedAtUtc')))) { [void]$failReasons.Add('GITHUB_RELEASE_PUBLISHED_AT') }
        elseif ([string]::IsNullOrWhiteSpace([string](Get-PostSyncEvidencePropertyValue -Group $github -Name 'url'))) { [void]$failReasons.Add('GITHUB_RELEASE_URL') }
    }
    elseif (Test-PostSyncObservedEvidenceGroupHasObservedValues -Group $github -PropertyNames @('id', 'publishedAtUtc', 'url')) {
        [void]$failReasons.Add('GITHUB_RELEASE_AMBIGUOUS')
    }

    $latest = $Evidence.latestPromotion
    $latestState = Get-PostSyncObservedEvidenceGroupState -Group $latest
    if ($latestState -eq 'OBSERVED') {
        if (-not (Test-PostSyncPositiveInteger (Get-PostSyncEvidencePropertyValue -Group $latest -Name 'workflowRunId'))) { [void]$failReasons.Add('LATEST_WORKFLOW_RUN_ID') }
        elseif (-not (Test-PostSyncPositiveInteger (Get-PostSyncEvidencePropertyValue -Group $latest -Name 'workflowAttempt'))) { [void]$failReasons.Add('LATEST_WORKFLOW_ATTEMPT') }
        elseif (-not (Test-PostSyncWorkflowResult ([string](Get-PostSyncEvidencePropertyValue -Group $latest -Name 'workflowResult')))) { [void]$failReasons.Add('LATEST_WORKFLOW_RESULT') }
        elseif (-not (Test-ReleaseDigest ([string](Get-PostSyncEvidencePropertyValue -Group $latest -Name 'digest')))) { [void]$failReasons.Add('LATEST_DIGEST') }
        elseif (-not (Test-PostSyncConsumerResult ([string](Get-PostSyncEvidencePropertyValue -Group $latest -Name 'digestEquality')))) { [void]$failReasons.Add('LATEST_DIGEST_EQUALITY') }
        else {
            $latestConsumer = Get-PostSyncEvidenceNestedGroup -Group $latest -Name 'consumerVerification'
            $latestConsumerState = Get-PostSyncObservedEvidenceGroupState -Group $latestConsumer
            if ($latestConsumerState -eq 'OBSERVED') {
                if (-not (Test-PostSyncConsumerResult ([string](Get-PostSyncEvidencePropertyValue -Group $latestConsumer -Name 'result')))) { [void]$failReasons.Add('LATEST_CONSUMER_RESULT') }
            }
            elseif (Test-PostSyncObservedEvidenceGroupHasObservedValues -Group $latestConsumer -PropertyNames @('result')) {
                [void]$failReasons.Add('LATEST_CONSUMER_AMBIGUOUS')
            }
        }
    }
    elseif (Test-PostSyncObservedEvidenceGroupHasObservedValues -Group $latest -PropertyNames @('workflowRunId', 'workflowAttempt', 'workflowResult', 'digest', 'digestEquality')) {
        [void]$failReasons.Add('LATEST_PROMOTION_AMBIGUOUS')
    }

    $overall = $Evidence.overallConsumerVerification
    $overallState = Get-PostSyncObservedEvidenceGroupState -Group $overall
    if ($overallState -eq 'OBSERVED') {
        if (-not (Test-PostSyncConsumerResult ([string](Get-PostSyncEvidencePropertyValue -Group $overall -Name 'result')))) { [void]$failReasons.Add('OVERALL_CONSUMER_RESULT') }
    }
    elseif (Test-PostSyncObservedEvidenceGroupHasObservedValues -Group $overall -PropertyNames @('result')) {
        [void]$failReasons.Add('OVERALL_CONSUMER_AMBIGUOUS')
    }

    if ($failReasons.Count -gt 0) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Reason = (($failReasons | Select-Object -Unique) -join ',') }
    }

    return [pscustomobject]@{ State = 'PASS'; Reason = '' }
}

function Test-PostSyncObservedEvidenceBinding {
    param(
        $Evidence,
        [string]$Version,
        [string]$ReleaseCommitSha,
        $VerifyMap,
        [string[]]$ResolvedPlatforms
    )

    $schema = Test-PostSyncObservedEvidenceSchema -Evidence $Evidence
    if ($schema.State -ne 'PASS') {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Reason = $schema.Reason }
    }

    if ([string]$Evidence.version -ne $Version) {
        return [pscustomobject]@{ State = 'CONFLICT'; Reason = 'VERSION_MISMATCH' }
    }
    if ([string]$Evidence.releaseCommitSha -ne $ReleaseCommitSha) {
        return [pscustomobject]@{ State = 'CONFLICT'; Reason = 'RELEASE_COMMIT_SHA_MISMATCH' }
    }
    if ([string]$Evidence.publicOciDigest -ne [string]$VerifyMap['PUBLIC_DIGEST']) {
        return [pscustomobject]@{ State = 'CONFLICT'; Reason = 'PUBLIC_DIGEST_MISMATCH' }
    }

    $manifestPlatforms = @($Evidence.supportedPlatforms | ForEach-Object { [string]$_ } | Sort-Object)
    $resolvedSorted = @($ResolvedPlatforms | Sort-Object)
    if (($manifestPlatforms -join ',') -ne ($resolvedSorted -join ',')) {
        return [pscustomobject]@{ State = 'CONFLICT'; Reason = 'PLATFORM_MISMATCH' }
    }

    $nuget = $Evidence.nuget
    if ([string]$nuget.version -ne $Version) {
        return [pscustomobject]@{ State = 'CONFLICT'; Reason = 'NUGET_VERSION_MISMATCH' }
    }
    if ([string]$nuget.repositoryRevision -ne $ReleaseCommitSha) {
        return [pscustomobject]@{ State = 'CONFLICT'; Reason = 'NUGET_REVISION_MISMATCH' }
    }

    $tag = 'v' + $Version
    $expectedUrl = 'https://github.com/kooiei-in4a/amane-mailer/releases/tag/' + $tag
    $githubState = Get-PostSyncObservedEvidenceGroupState -Group $Evidence.githubRelease
    if ($githubState -eq 'OBSERVED' -and [string]$Evidence.githubRelease.url -ne $expectedUrl) {
        return [pscustomobject]@{ State = 'CONFLICT'; Reason = 'GITHUB_RELEASE_URL_MISMATCH' }
    }

    $latestState = Get-PostSyncObservedEvidenceGroupState -Group $Evidence.latestPromotion
    if ($latestState -eq 'OBSERVED') {
        if ([string]$Evidence.latestPromotion.digest -ne [string]$Evidence.publicOciDigest) {
            return [pscustomobject]@{ State = 'CONFLICT'; Reason = 'LATEST_DIGEST_MISMATCH' }
        }
        if ([string]$Evidence.latestPromotion.digestEquality -ne 'PASS') {
            return [pscustomobject]@{ State = 'CONFLICT'; Reason = 'LATEST_DIGEST_EQUALITY_NOT_PASS' }
        }
    }

    if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.versionedGhcrConsumerVerification) -eq 'OBSERVED' -and
        [string]$Evidence.versionedGhcrConsumerVerification.result -eq 'FAIL') {
        return [pscustomobject]@{ State = 'CONFLICT'; Reason = 'VERSIONED_CONSUMER_FAIL' }
    }
    if ((Get-PostSyncObservedEvidenceGroupState -Group $nuget.cleanConsumerVerification) -eq 'OBSERVED' -and
        [string]$nuget.cleanConsumerVerification.result -eq 'FAIL') {
        return [pscustomobject]@{ State = 'CONFLICT'; Reason = 'NUGET_CLEAN_CONSUMER_FAIL' }
    }
    if ($latestState -eq 'OBSERVED' -and
        (Get-PostSyncObservedEvidenceGroupState -Group (Get-PostSyncEvidenceNestedGroup -Group $Evidence.latestPromotion -Name 'consumerVerification')) -eq 'OBSERVED' -and
        [string](Get-PostSyncEvidencePropertyValue -Group (Get-PostSyncEvidenceNestedGroup -Group $Evidence.latestPromotion -Name 'consumerVerification') -Name 'result') -eq 'FAIL') {
        return [pscustomobject]@{ State = 'CONFLICT'; Reason = 'LATEST_CONSUMER_FAIL' }
    }
    if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.overallConsumerVerification) -eq 'OBSERVED' -and
        [string]$Evidence.overallConsumerVerification.result -eq 'FAIL') {
        return [pscustomobject]@{ State = 'CONFLICT'; Reason = 'OVERALL_CONSUMER_FAIL' }
    }

    $imageState = Get-PostSyncObservedEvidenceGroupState -Group $Evidence.releaseImageWorkflow
    if ($imageState -eq 'OBSERVED' -and [string]$Evidence.releaseImageWorkflow.result -ne 'SUCCESS') {
        return [pscustomobject]@{ State = 'CONFLICT'; Reason = 'RELEASE_IMAGE_WORKFLOW_NOT_SUCCESS' }
    }
    if ($latestState -eq 'OBSERVED' -and [string]$Evidence.latestPromotion.workflowResult -ne 'SUCCESS') {
        return [pscustomobject]@{ State = 'CONFLICT'; Reason = 'LATEST_WORKFLOW_NOT_SUCCESS' }
    }

    return [pscustomobject]@{ State = 'PASS'; Reason = '' }
}

function Apply-ObservedEvidenceToReleaseRecord {
    param(
        [string]$Text,
        [string]$Version,
        $Evidence
    )

    if ($null -eq $Evidence) {
        return $Text
    }

    $tag = 'v' + $Version
    $escTag = [regex]::Escape($tag)
    $updatedLines = New-Object System.Collections.Generic.List[string]
    $skipNext = $false
    $lines = $Text -split '\r?\n', 0

    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($skipNext) {
            $skipNext = $false
            continue
        }

        $line = $lines[$i]
        $handled = $false

        if (-not $handled -and $line -match '^-\s+annotated tag object:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.annotatedTagObject) -eq 'OBSERVED') {
                [void]$updatedLines.Add('- annotated tag object: `' + [string]$Evidence.annotatedTagObject.sha + '`')
                $handled = $true
            }
        }

        if (-not $handled -and $line -match '^-\s+Release image workflow run / attempt:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.releaseImageWorkflow) -eq 'OBSERVED') {
                $wf = $Evidence.releaseImageWorkflow
                [void]$updatedLines.Add(('- Release image workflow run / attempt: `{0}` / `{1}` - **{2}**' -f $wf.runId, $wf.attempt, $wf.result))
                $handled = $true
            }
        }

        if (-not $handled -and $line -match '^-\s+Publication artifact:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.publicationArtifact) -eq 'OBSERVED') {
                $artifact = $Evidence.publicationArtifact
                [void]$updatedLines.Add(('- Publication artifact: `{0}` / ID `{1}`' -f $artifact.name, $artifact.id))
                $handled = $true
            }
        }

        if (-not $handled -and $line -match '^-\s+Publication evidence artifact:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.publicationEvidenceArtifact) -eq 'OBSERVED') {
                $artifact = $Evidence.publicationEvidenceArtifact
                [void]$updatedLines.Add(('- Publication evidence artifact: `{0}` / ID `{1}`' -f $artifact.name, $artifact.id))
                $handled = $true
            }
        }

        if (-not $handled -and $line -match '^-\s+Publication evidence artifact name / ID:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.publicationEvidenceArtifact) -eq 'OBSERVED') {
                $artifact = $Evidence.publicationEvidenceArtifact
                [void]$updatedLines.Add(('- Publication evidence artifact name / ID: `{0}` / `{1}`' -f $artifact.name, $artifact.id))
                $handled = $true
            }
        }

        if (-not $handled -and $line -match '^-\s+Public-consumer versioned-image verification:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.versionedGhcrConsumerVerification) -eq 'OBSERVED') {
                [void]$updatedLines.Add('- Public-consumer versioned-image verification: **' + [string]$Evidence.versionedGhcrConsumerVerification.result + '**')
                $handled = $true
            }
        }

        if (-not $handled -and $line -match '^-\s+Public-consumer verification evidence:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.versionedGhcrConsumerVerification) -eq 'OBSERVED') {
                [void]$updatedLines.Add('- Public-consumer verification evidence: **' + [string]$Evidence.versionedGhcrConsumerVerification.result + '**')
                $handled = $true
            }
        }

        if (-not $handled -and $line -match '^-\s+NuGet clean-consumer restore / build / run:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.nuget.cleanConsumerVerification) -eq 'OBSERVED') {
                [void]$updatedLines.Add('- NuGet clean-consumer restore / build / run: **' + [string]$Evidence.nuget.cleanConsumerVerification.result + '**')
                $handled = $true
            }
        }

        if (-not $handled -and $line -match '^-\s+GitHub Release ID:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.githubRelease) -eq 'OBSERVED') {
                [void]$updatedLines.Add('- GitHub Release ID: `' + [string]$Evidence.githubRelease.id + '`')
                $handled = $true
            }
        }

        if (-not $handled -and $line -match '^-\s+GitHub Release published at:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.githubRelease) -eq 'OBSERVED') {
                $publishedAt = ConvertTo-PostSyncEvidenceUtcString (Get-PostSyncEvidencePropertyValue -Group $Evidence.githubRelease -Name 'publishedAtUtc')
                [void]$updatedLines.Add('- GitHub Release published at: `' + $publishedAt + '`')
                $handled = $true
            }
        }

        # Canonical future shape: GHCR `latest` promotion:
        # Backward-compatible historical shape: GHCR `latest` digest promotion:
        if (-not $handled -and (
                ($line -match '^-\s+GHCR `latest` promotion:') -or
                ($line -match '^-\s+GHCR `latest` digest promotion:')
            ) -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.latestPromotion) -eq 'OBSERVED') {
                [void]$updatedLines.Add('- GHCR `latest` promotion: **PUBLISHED** by digest-preserving copy, no rebuild')
                $handled = $true
            }
        }

        if (-not $handled -and $line -match '^-\s+`latest` promotion workflow run / attempt:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.latestPromotion) -eq 'OBSERVED') {
                $latest = $Evidence.latestPromotion
                [void]$updatedLines.Add(('- `latest` promotion workflow run / attempt: `{0}` / `{1}` - **{2}**' -f $latest.workflowRunId, $latest.workflowAttempt, $latest.workflowResult))
                $handled = $true
            }
        }

        if (-not $handled -and $line -match '^-\s+GHCR `latest` digest:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.latestPromotion) -eq 'OBSERVED') {
                [void]$updatedLines.Add('- GHCR `latest` digest: `' + [string]$Evidence.latestPromotion.digest + '`')
                $handled = $true
            }
        }

        if (-not $handled -and $line -match ('^-\s+`latest == ' + $escTag + '` by OCI digest:') -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.latestPromotion) -eq 'OBSERVED') {
                [void]$updatedLines.Add(('- `latest == {0}` by OCI digest: **{1}**' -f $tag, $Evidence.latestPromotion.digestEquality))
                $handled = $true
            }
        }

        if (-not $handled -and $line -match '^-\s+anonymous `latest` pull and OCI version/revision read-back:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            $latestConsumerGroup = Get-PostSyncEvidenceNestedGroup -Group $Evidence.latestPromotion -Name 'consumerVerification'
            if ((Get-PostSyncObservedEvidenceGroupState -Group $latestConsumerGroup) -eq 'OBSERVED') {
                [void]$updatedLines.Add('- anonymous `latest` pull and OCI version/revision read-back: **' + [string](Get-PostSyncEvidencePropertyValue -Group $latestConsumerGroup -Name 'result') + '**')
                $handled = $true
            }
        }

        if (-not $handled -and $line -match '^-\s+Consumer verification results:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.overallConsumerVerification) -eq 'OBSERVED') {
                [void]$updatedLines.Add('- Consumer verification results: **' + [string]$Evidence.overallConsumerVerification.result + '** for versioned GHCR, NuGet, GitHub Release/tag, and promoted `latest`')
                $handled = $true
            }
        }

        if (-not $handled -and $line -match '^-\s+publish workflow run:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.releaseImageWorkflow) -eq 'OBSERVED') {
                [void]$updatedLines.Add('- publish workflow run: ``' + [string]$Evidence.releaseImageWorkflow.runId + '``')
                $handled = $true
            }
        }

        if (-not $handled -and $line -match '^-\s+run attempt:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.releaseImageWorkflow) -eq 'OBSERVED') {
                [void]$updatedLines.Add('- run attempt: ``' + [string]$Evidence.releaseImageWorkflow.attempt + '``')
                $handled = $true
            }
        }

        if (-not $handled -and $line -match '^-\s+publication artifact ID:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.publicationArtifact) -eq 'OBSERVED') {
                [void]$updatedLines.Add('- publication artifact ID: ``' + [string]$Evidence.publicationArtifact.id + '``')
                $handled = $true
            }
        }

        if (-not $handled -and $line -match '^-\s+published at:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.githubRelease) -eq 'OBSERVED') {
                $publishedAt = ConvertTo-PostSyncEvidenceUtcString (Get-PostSyncEvidencePropertyValue -Group $Evidence.githubRelease -Name 'publishedAtUtc')
                [void]$updatedLines.Add('- published at: ``' + $publishedAt + '``')
                $handled = $true
            }
        }

        if (-not $handled -and $line -match '^-\s+release ID:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.githubRelease) -eq 'OBSERVED') {
                [void]$updatedLines.Add('- release ID: ``' + [string]$Evidence.githubRelease.id + '``')
                $handled = $true
            }
        }

        if ($handled) {
            continue
        }

        [void]$updatedLines.Add($line)
    }

    return [string]::Join("`n", $updatedLines)
}

function Test-ObservedEvidenceRenderingConsistency {
    param(
        [string]$Text,
        [string]$Version,
        $Evidence
    )

    if ($null -eq $Evidence) {
        return [pscustomobject]@{ State = 'PASS'; Reason = '' }
    }

    $failures = New-Object System.Collections.Generic.List[string]
    $tag = 'v' + $Version

    if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.annotatedTagObject) -eq 'OBSERVED') {
        if ($Text -notmatch ('annotated tag object: `' + [regex]::Escape([string]$Evidence.annotatedTagObject.sha) + '`')) {
            [void]$failures.Add('ANNOTATED_TAG_OBJECT')
        }
        if ($Text -match '(?m)^-\s+annotated tag object:.*\*\*PENDING') {
            [void]$failures.Add('ANNOTATED_TAG_OBJECT_STALE_PENDING')
        }
    }

    if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.releaseImageWorkflow) -eq 'OBSERVED') {
        $wf = $Evidence.releaseImageWorkflow
        if (($Text -notmatch ([string]$wf.runId)) -or ($Text -match 'Release image workflow run / attempt: \*\*PENDING\*\*') -or ($Text -match 'publish workflow run: \*\*PENDING\*\*')) {
            [void]$failures.Add('RELEASE_IMAGE_WORKFLOW')
        }
        if ($Text -match '(?m)^-\s+Release image workflow run / attempt:.*\*\*PENDING') {
            [void]$failures.Add('RELEASE_IMAGE_WORKFLOW_STALE_PENDING')
        }
    }

    if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.publicationArtifact) -eq 'OBSERVED') {
        $artifact = $Evidence.publicationArtifact
        if (($Text -notmatch [regex]::Escape([string]$artifact.name)) -or ($Text -notmatch ([string]$artifact.id))) {
            [void]$failures.Add('PUBLICATION_ARTIFACT')
        }
        if ($Text -match '(?m)^-\s+Publication artifact:.*\*\*PENDING') {
            [void]$failures.Add('PUBLICATION_ARTIFACT_STALE_PENDING')
        }
    }

    if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.publicationEvidenceArtifact) -eq 'OBSERVED') {
        $artifact = $Evidence.publicationEvidenceArtifact
        if (($Text -notmatch [regex]::Escape([string]$artifact.name)) -or ($Text -notmatch ([string]$artifact.id))) {
            [void]$failures.Add('PUBLICATION_EVIDENCE_ARTIFACT')
        }
        if ($Text -match '(?m)^-\s+Publication evidence artifact(?: name / ID)?:.*\*\*PENDING') {
            [void]$failures.Add('PUBLICATION_EVIDENCE_ARTIFACT_STALE_PENDING')
        }
    }

    if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.versionedGhcrConsumerVerification) -eq 'OBSERVED') {
        $versionedRendered = ($Text -match ('Public-consumer versioned-image verification: \*\*' + [regex]::Escape([string]$Evidence.versionedGhcrConsumerVerification.result) + '\*\*')) -or
            ($Text -match ('Public-consumer verification evidence: \*\*' + [regex]::Escape([string]$Evidence.versionedGhcrConsumerVerification.result) + '\*\*'))
        if (-not $versionedRendered) {
            [void]$failures.Add('VERSIONED_GHCR_CONSUMER')
        }
        if ($Text -match '(?m)^-\s+Public-consumer versioned-image verification:.*\*\*PENDING' -or
            $Text -match '(?m)^-\s+Public-consumer verification evidence:.*\*\*PENDING') {
            [void]$failures.Add('VERSIONED_GHCR_CONSUMER_STALE_PENDING')
        }
    }

    $nugetObservedAt = ConvertTo-PostSyncEvidenceUtcString (Get-PostSyncEvidencePropertyValue -Group $Evidence.nuget -Name 'publicObservedAtUtc')
    if (-not [string]::IsNullOrWhiteSpace($nugetObservedAt)) {
        if ($Text -notmatch [regex]::Escape($nugetObservedAt)) {
            [void]$failures.Add('NUGET_OBSERVED_AT')
        }
        if ($Text -match '(?m)^-\s+NuGet public observed-at \(UTC\):.*\*\*PENDING') {
            [void]$failures.Add('NUGET_OBSERVED_AT_STALE_PENDING')
        }
    }

    if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.nuget.symbolObservation) -eq 'OBSERVED') {
        if ($Text -notmatch 'NuGet symbol package status: \*\*OBSERVED\*\*') {
            [void]$failures.Add('NUGET_SYMBOL')
        }
        if ($Text -match '(?m)^-\s+NuGet symbol package status:.*\*\*PENDING') {
            [void]$failures.Add('NUGET_SYMBOL_STALE_PENDING')
        }
    }

    if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.nuget.cleanConsumerVerification) -eq 'OBSERVED') {
        if ($Text -notmatch ('NuGet clean-consumer restore / build / run: \*\*' + [regex]::Escape([string]$Evidence.nuget.cleanConsumerVerification.result) + '\*\*')) {
            [void]$failures.Add('NUGET_CLEAN_CONSUMER')
        }
        if ($Text -match '(?m)^-\s+NuGet clean-consumer restore / build / run:.*\*\*PENDING') {
            [void]$failures.Add('NUGET_CLEAN_CONSUMER_STALE_PENDING')
        }
    }

    if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.githubRelease) -eq 'OBSERVED') {
        $gh = $Evidence.githubRelease
        $ghPublishedAt = ConvertTo-PostSyncEvidenceUtcString (Get-PostSyncEvidencePropertyValue -Group $gh -Name 'publishedAtUtc')
        if (($Text -notmatch ([string](Get-PostSyncEvidencePropertyValue -Group $gh -Name 'id'))) -or ($Text -notmatch [regex]::Escape($ghPublishedAt)) -or ($Text -notmatch [regex]::Escape([string](Get-PostSyncEvidencePropertyValue -Group $gh -Name 'url')))) {
            [void]$failures.Add('GITHUB_RELEASE')
        }
        if ($Text -match '(?m)^-\s+GitHub Release ID:.*\*\*PENDING' -or
            $Text -match '(?m)^-\s+GitHub Release published at:.*\*\*PENDING' -or
            $Text -match '(?m)^-\s+GitHub Release URL:.*\*\*PENDING') {
            [void]$failures.Add('GITHUB_RELEASE_STALE_PENDING')
        }
    }

    if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.latestPromotion) -eq 'OBSERVED') {
        $latest = $Evidence.latestPromotion
        if (($Text -notmatch ([string]$latest.workflowRunId)) -or ($Text -notmatch [regex]::Escape([string]$latest.digest)) -or ($Text -notmatch ('`latest == ' + [regex]::Escape($tag)))) {
            [void]$failures.Add('LATEST_PROMOTION')
        }
        if ($Text -notmatch 'GHCR `latest` promotion: \*\*PUBLISHED\*\*') {
            [void]$failures.Add('LATEST_PROMOTION_STATUS')
        }
        if ($Text -match '(?m)^-\s+GHCR `latest`(?: digest)? promotion:.*\*\*PENDING' -or
            $Text -match '(?m)^-\s+`latest` promotion workflow run / attempt:.*\*\*PENDING' -or
            $Text -match '(?m)^-\s+GHCR `latest` digest:.*\*\*PENDING' -or
            $Text -match ('(?m)^-\s+`latest == ' + [regex]::Escape($tag) + '` by OCI digest:.*\*\*PENDING')) {
            [void]$failures.Add('LATEST_PROMOTION_STALE_PENDING')
        }
        if ($Text -notmatch ('`latest == ' + [regex]::Escape($tag) + '` by OCI digest: \*\*PASS\*\*')) {
            [void]$failures.Add('LATEST_DIGEST_EQUALITY')
        }
    }

    if ((Get-PostSyncObservedEvidenceGroupState -Group (Get-PostSyncEvidenceNestedGroup -Group $Evidence.latestPromotion -Name 'consumerVerification')) -eq 'OBSERVED') {
        $latestConsumerGroup = Get-PostSyncEvidenceNestedGroup -Group $Evidence.latestPromotion -Name 'consumerVerification'
        if ($Text -notmatch ('anonymous `latest` pull and OCI version/revision read-back: \*\*' + [regex]::Escape([string](Get-PostSyncEvidencePropertyValue -Group $latestConsumerGroup -Name 'result')) + '\*\*')) {
            [void]$failures.Add('LATEST_CONSUMER')
        }
        if ($Text -match '(?m)^-\s+anonymous `latest` pull and OCI version/revision read-back:.*\*\*PENDING') {
            [void]$failures.Add('LATEST_CONSUMER_STALE_PENDING')
        }
    }

    if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.overallConsumerVerification) -eq 'OBSERVED') {
        if ($Text -notmatch ('Consumer verification results: \*\*' + [regex]::Escape([string]$Evidence.overallConsumerVerification.result) + '\*\*')) {
            [void]$failures.Add('OVERALL_CONSUMER')
        }
        if ($Text -match '(?m)^-\s+Consumer verification results:.*\*\*PENDING') {
            [void]$failures.Add('OVERALL_CONSUMER_STALE_PENDING')
        }
    }

    if ($failures.Count -gt 0) {
        return [pscustomobject]@{ State = 'CONFLICT'; Reason = (($failures | Select-Object -Unique) -join ',') }
    }

    return [pscustomobject]@{ State = 'PASS'; Reason = '' }
}

function Test-ObservedEvidenceRecordConflict {
    param(
        [string]$Text,
        $Evidence
    )

    if ($null -eq $Evidence) {
        return [pscustomobject]@{ State = 'PASS'; Reason = '' }
    }

    if ((Get-PostSyncObservedEvidenceGroupState -Group $Evidence.annotatedTagObject) -eq 'OBSERVED') {
        $expected = [string]$Evidence.annotatedTagObject.sha
        if ($Text -match 'annotated tag object: `' + '([0-9a-f]{40})' + '`') {
            if ($Matches[1] -ne $expected) {
                return [pscustomobject]@{ State = 'CONFLICT'; Reason = 'ANNOTATED_TAG_OBJECT' }
            }
        }
    }

    return [pscustomobject]@{ State = 'PASS'; Reason = '' }
}

function Update-ReleaseRecordObservableFields {
    param(
        [string]$Text,
        [string]$Version,
        [string]$ReleaseCommitSha,
        [string]$PublicDigest,
        [string[]]$Platforms,
        [string]$NugetPublicObservedAtUtc = '',
        [string]$NugetSymbolsStatus = ''
    )

    $tag = 'v' + $Version
    $shaTag = 'sha-' + $ReleaseCommitSha
    $ghcrVersionTag = 'ghcr.io/kooiei-in4a/amane-mailer:' + $tag
    $ghcrShaTag = 'ghcr.io/kooiei-in4a/amane-mailer:' + $shaTag
    $nugetPackage = 'Amane.Mailer.Contracts ' + $Version
    $releaseUrl = 'https://github.com/kooiei-in4a/amane-mailer/releases/tag/' + $tag
    $escTag = [regex]::Escape($tag)
    $escGhcrVersion = [regex]::Escape($ghcrVersionTag)
    $escNuget = [regex]::Escape($nugetPackage)

    $updatedLines = New-Object System.Collections.Generic.List[string]
    $skipNext = $false
    $lines = $Text -split '\r?\n', 0

    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($skipNext) {
            $skipNext = $false
            continue
        }

        $line = $lines[$i]

        # Do not promote Status here. Callers must validate core fields first,
        # then apply Set-ReleaseRecordStatusPublished.

        # Production shape: - `releaseCommitSha`: **PENDING...
        if ($line -match '^-\s+`releaseCommitSha`:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            [void]$updatedLines.Add('- `releaseCommitSha`: `' + $ReleaseCommitSha + '`')
            continue
        }
        if ($line -match '^-\s+releaseCommitSha:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            [void]$updatedLines.Add('- releaseCommitSha: ``' + $ReleaseCommitSha + '``')
            continue
        }
        if ($line -match 'Exact release source commit \(`releaseCommitSha`\):' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            [void]$updatedLines.Add('- Exact release source commit (``releaseCommitSha``): ``' + $ReleaseCommitSha + '``')
            continue
        }

        # Production shape: - Git tag `vX.Y.Z`: **NOT YET PUBLISHED**
        if ($line -match ('^-\s+Git tag `' + $escTag + '`:') -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            [void]$updatedLines.Add('- Git tag `' + $tag + '`: **PUBLISHED**')
            continue
        }
        if ($line -match '^-\s+Git tag:' -and $line -notmatch 'target|overwrite|move|publication' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            [void]$updatedLines.Add('- Git tag: ``' + $tag + '``')
            continue
        }
        if ($line -match '^-\s+Git tag target:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            # Generic fixture historically uses double backticks; production uses single.
            if ($line -match '``' -or $line -match '\*\*PENDING\*\*\s*$') {
                # Ambiguous: both shapes use bold PENDING. Prefer double when the
                # surrounding record already uses double-backtick identities.
                $preferDouble = ($Text -match '(?m)^-\s+release version:\s+``') -or ($line -match '``')
                if ($preferDouble) {
                    [void]$updatedLines.Add('- Git tag target: ``' + $ReleaseCommitSha + '``')
                }
                else {
                    [void]$updatedLines.Add('- Git tag target: `' + $ReleaseCommitSha + '`')
                }
            }
            else {
                [void]$updatedLines.Add('- Git tag target: `' + $ReleaseCommitSha + '`')
            }
            continue
        }
        if ($line -match '^-\s+Contracts version:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            [void]$updatedLines.Add('- Contracts version: ``' + $Version + '``')
            continue
        }
        if ($line -match '^-\s+OpenAPI version:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            [void]$updatedLines.Add('- OpenAPI version: ``' + $Version + '``')
            continue
        }

        # Production shape: - GHCR `ghcr.io/...:vX.Y.Z`: **NOT YET PUBLISHED**
        if ($line -match ('^-\s+GHCR `' + $escGhcrVersion + '`:') -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            [void]$updatedLines.Add('- GHCR `' + $ghcrVersionTag + '`: **PUBLISHED**')
            continue
        }
        if ($line -match '^-\s+version tag:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            [void]$updatedLines.Add('- version tag: ``' + $ghcrVersionTag + '``')
            continue
        }

        # Production shape: - GHCR immutable `sha-<releaseCommitSha>` tag: **PENDING**
        if ($line -match '^-\s+GHCR immutable `sha-(?:<releaseCommitSha>|[0-9a-f]{40})` tag:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            [void]$updatedLines.Add('- GHCR immutable `' + $shaTag + '` tag: **PUBLISHED**')
            continue
        }
        if ($line -match '^-\s+immutable tag:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            if ($i + 1 -lt $lines.Count -and $lines[$i + 1] -match '^\s+``') {
                [void]$updatedLines.Add('- immutable tag:')
                [void]$updatedLines.Add('  ``' + $ghcrShaTag + '``')
                $skipNext = $true
            }
            else {
                [void]$updatedLines.Add('- immutable tag: ``' + $ghcrShaTag + '``')
            }
            continue
        }

        # Production / generic OCI digest (preserve Public vs public label casing)
        if ($line -match '^(-\s+)([Pp]ublic OCI digest:)(\s*)' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            $digestLabel = $Matches[1] + $Matches[2]
            # Generic fixture uses lowercase + double backticks; production uses Public + single.
            $useDouble = $Matches[2].StartsWith('p') -or ($line -match '``')
            if ($i + 1 -lt $lines.Count -and $lines[$i + 1] -match '^\s+`') {
                [void]$updatedLines.Add($digestLabel)
                if ($useDouble -or $lines[$i + 1] -match '``') {
                    [void]$updatedLines.Add('  ``' + $PublicDigest + '``')
                }
                else {
                    [void]$updatedLines.Add('  `' + $PublicDigest + '`')
                }
                $skipNext = $true
            }
            else {
                if ($useDouble) {
                    [void]$updatedLines.Add($digestLabel + ' ``' + $PublicDigest + '``')
                }
                else {
                    [void]$updatedLines.Add($digestLabel + ' `' + $PublicDigest + '`')
                }
            }
            continue
        }

        # Production shape: - NuGet `Amane.Mailer.Contracts X.Y.Z`: **NOT YET PUBLISHED**
        if ($line -match ('^-\s+NuGet `' + $escNuget + '`:') -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            [void]$updatedLines.Add('- NuGet `' + $nugetPackage + '`: **PUBLISHED**')
            continue
        }
        if ($line -match '^-\s+package:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            [void]$updatedLines.Add('- package: ``' + $nugetPackage + '``')
            continue
        }

        # Production shape: - NuGet SourceLink revision: **PENDING**
        if ($line -match '^-\s+NuGet SourceLink revision:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            $useDouble = ($line -match '``')
            if ($useDouble) {
                [void]$updatedLines.Add('- NuGet SourceLink revision: ``' + $ReleaseCommitSha + '``')
            }
            else {
                [void]$updatedLines.Add('- NuGet SourceLink revision: `' + $ReleaseCommitSha + '`')
            }
            continue
        }
        if ($line -match '^-\s+revision / nuspec repository commit:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            if ($i + 1 -lt $lines.Count -and $lines[$i + 1] -match '^\s+``') {
                [void]$updatedLines.Add('- revision / nuspec repository commit:')
                [void]$updatedLines.Add('  ``' + $ReleaseCommitSha + '``')
                $skipNext = $true
            }
            else {
                [void]$updatedLines.Add('- revision / nuspec repository commit: ``' + $ReleaseCommitSha + '``')
            }
            continue
        }

        # Observable verifier fact only (never invent NuGet service indexing time).
        if ($line -match '^-\s+NuGet public observed-at \(UTC\):' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            if (Test-ReleaseUtcTimestamp -Value $NugetPublicObservedAtUtc) {
                $useDouble = ($line -match '``')
                if ($useDouble) {
                    [void]$updatedLines.Add('- NuGet public observed-at (UTC): ``' + $NugetPublicObservedAtUtc + '``')
                }
                else {
                    [void]$updatedLines.Add('- NuGet public observed-at (UTC): `' + $NugetPublicObservedAtUtc + '`')
                }
            }
            else {
                [void]$updatedLines.Add($line)
            }
            continue
        }

        if ($line -match '^-\s+NuGet symbol package status:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            if ($NugetSymbolsStatus -eq 'OBSERVED' -or $NugetSymbolsStatus -eq 'PASS') {
                [void]$updatedLines.Add('- NuGet symbol package status: **OBSERVED**')
            }
            else {
                [void]$updatedLines.Add($line)
            }
            continue
        }

        # Production shape: - GitHub Release `vX.Y.Z`: **NOT YET PUBLISHED**
        if ($line -match ('^-\s+GitHub Release `' + $escTag + '`:') -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            [void]$updatedLines.Add('- GitHub Release `' + $tag + '`: **PUBLISHED**')
            continue
        }
        if ($line -match '^-\s+release:\s' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            [void]$updatedLines.Add('- release: ``' + $tag + '``')
            continue
        }

        # Production shape: - GitHub Release URL: **PENDING**
        if ($line -match '^-\s+GitHub Release URL:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            $useDouble = ($line -match '``')
            if ($useDouble) {
                [void]$updatedLines.Add('- GitHub Release URL: ``' + $releaseUrl + '``')
            }
            else {
                [void]$updatedLines.Add('- GitHub Release URL: `' + $releaseUrl + '`')
            }
            continue
        }
        if ($line -match '^-\s+URL:' -and (Test-ReleaseRecordLineHasPendingValue -Line $line)) {
            [void]$updatedLines.Add('- URL: ``' + $releaseUrl + '``')
            continue
        }

        [void]$updatedLines.Add($line)
    }

    return [string]::Join("`n", $updatedLines)
}

function Build-PublishedReleaseRecordForPostSync {
    param(
        [string]$Text,
        [string]$Version,
        [string]$ReleaseCommitSha,
        [string]$PublicDigest,
        [string[]]$Platforms,
        [string]$NugetPublicObservedAtUtc = '',
        [string]$NugetSymbolsStatus = '',
        $ObservedEvidence = $null
    )

    $recordState = Get-ReleaseRecordStateFromText -Text $Text
    if ($recordState -eq 'PUBLISHED') {
        return [pscustomobject]@{ State = 'ALREADY'; Text = $Text; Reason = '' }
    }
    if ($recordState -ne 'PENDING') {
        return [pscustomobject]@{ State = 'CONFLICT'; Text = ''; Reason = 'RECORD_STATE' }
    }
    if (-not (Test-ReleaseSha $ReleaseCommitSha)) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Text = ''; Reason = 'INVALID_SHA' }
    }
    if (-not (Test-ReleaseDigest $PublicDigest)) {
        return [pscustomobject]@{ State = 'INCOMPLETE'; Text = ''; Reason = 'INVALID_DIGEST' }
    }

    if ($null -ne $ObservedEvidence) {
        $recordConflict = Test-ObservedEvidenceRecordConflict -Text $Text -Evidence $ObservedEvidence
        if ($recordConflict.State -ne 'PASS') {
            return [pscustomobject]@{ State = 'CONFLICT'; Text = ''; Reason = $recordConflict.Reason }
        }
    }

    $effectiveNugetObservedAt = $NugetPublicObservedAtUtc
    $effectiveNugetSymbols = $NugetSymbolsStatus
    if ($null -ne $ObservedEvidence) {
        $manifestObservedAt = ConvertTo-PostSyncEvidenceUtcString (Get-PostSyncEvidencePropertyValue -Group $ObservedEvidence.nuget -Name 'publicObservedAtUtc')
        if (Test-ReleaseUtcTimestamp -Value $manifestObservedAt) {
            $effectiveNugetObservedAt = $manifestObservedAt
        }
        if ((Get-PostSyncObservedEvidenceGroupState -Group $ObservedEvidence.nuget.symbolObservation) -eq 'OBSERVED') {
            $effectiveNugetSymbols = 'OBSERVED'
        }
    }

    $transformed = Update-ReleaseRecordObservableFields -Text $Text -Version $Version -ReleaseCommitSha $ReleaseCommitSha -PublicDigest $PublicDigest -Platforms $Platforms -NugetPublicObservedAtUtc $effectiveNugetObservedAt -NugetSymbolsStatus $effectiveNugetSymbols
    if ($null -ne $ObservedEvidence) {
        $transformed = Apply-ObservedEvidenceToReleaseRecord -Text $transformed -Version $Version -Evidence $ObservedEvidence
    }

    $consistency = Test-PublishedReleaseRecordCoreConsistency -Text $transformed -Version $Version -ReleaseCommitSha $ReleaseCommitSha -PublicDigest $PublicDigest
    if ($consistency.State -ne 'PASS') {
        return [pscustomobject]@{ State = 'CONFLICT'; Text = ''; Reason = $consistency.Reason }
    }

    if ($null -ne $ObservedEvidence) {
        $evidenceRender = Test-ObservedEvidenceRenderingConsistency -Text $transformed -Version $Version -Evidence $ObservedEvidence
        if ($evidenceRender.State -ne 'PASS') {
            return [pscustomobject]@{ State = 'CONFLICT'; Text = ''; Reason = ('EVIDENCE_RENDER_' + $evidenceRender.Reason) }
        }
    }

    $published = Set-ReleaseRecordStatusPublished -Text $transformed
    if (-not $published.EndsWith("`n")) {
        $published += "`n"
    }

    return [pscustomobject]@{ State = 'APPLIED'; Text = $published; Reason = '' }
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
        $LocalRepoOverride,
        $ObservedEvidence = $null
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

    try {
        $authVersion = [version]$authority.Version
        $targetVersionObj = [version]$TargetVersion
    }
    catch {
        $plan.Reason = 'VERSION_COMPARE'
        $plan.MutationResult = 'INCOMPLETE'
        return $plan
    }

    if ($authVersion -gt $targetVersionObj) {
        $plan.AuthorityState = 'AHEAD'
        $plan.Reason = 'AUTHORITY_AHEAD'
        $plan.MutationResult = 'CONFLICT'
        return $plan
    }

    $publicVerify = Test-ReleasePostSyncPublicVerify -VerifyMap $VerifyMap
    if ($publicVerify -ne 'PASS') {
        $plan.Reason = 'PUBLIC_VERIFY_' + $publicVerify
        $plan.MutationResult = 'INCOMPLETE'
        return $plan
    }

    $publicDigest = [string]$VerifyMap['PUBLIC_DIGEST']
    $nugetObservedAt = ''
    $candidateObserved = [string]$VerifyMap['NUGET_PUBLIC_OBSERVED_AT_UTC']
    if (Test-ReleaseUtcTimestamp -Value $candidateObserved) {
        $nugetObservedAt = $candidateObserved
    }
    $nugetSymbolsStatus = [string]$VerifyMap['NUGET_SYMBOLS']
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

    $targetRecordText = Read-PostSyncTextFile -Path $targetRecordPath
    $platformResolution = Resolve-PostSyncPlatforms -RecordText $targetRecordText -AuthorityPlatforms $authority.Platforms
    if ($platformResolution.State -ne 'RESOLVED') {
        $plan.FollowerState = 'INCOMPLETE'
        $plan.MutationResult = 'INCOMPLETE'
        $plan.Reason = 'PLATFORMS_' + $platformResolution.Reason
        return $plan
    }
    $resolvedPlatforms = $platformResolution.Platforms

    if ($null -eq $ObservedEvidence) {
        $plan.FollowerState = 'INCOMPLETE'
        $plan.MutationResult = 'INCOMPLETE'
        $plan.Reason = 'MISSING_OBSERVED_EVIDENCE'
        return $plan
    }

    $evidenceBinding = Test-PostSyncObservedEvidenceBinding -Evidence $ObservedEvidence -Version $TargetVersion -ReleaseCommitSha $ReleaseCommitSha -VerifyMap $VerifyMap -ResolvedPlatforms $resolvedPlatforms
    if ($evidenceBinding.State -ne 'PASS') {
        if ($evidenceBinding.State -eq 'CONFLICT') {
            $plan.FollowerState = 'CONFLICT'
            $plan.MutationResult = 'CONFLICT'
        }
        else {
            $plan.FollowerState = 'INCOMPLETE'
            $plan.MutationResult = 'INCOMPLETE'
        }
        $plan.Reason = 'EVIDENCE_' + $evidenceBinding.Reason
        return $plan
    }

    $planned = New-Object System.Collections.Generic.List[string]
    $writes = New-Object System.Collections.Generic.List[hashtable]

    [void]$planned.Add('release/current-public.json')
    $newAuthority = New-CurrentPublicAuthorityJson -Version $TargetVersion -Platforms $resolvedPlatforms
    [void]$writes.Add(@{ Path = 'release/current-public.json'; Content = $newAuthority })

    foreach ($relativePath in $followerPaths) {
        if ($relativePath -eq 'release/current-public.json') { continue }
        if ($relativePath -eq $targetRecord) {
            [void]$planned.Add($targetRecord)
            $recordBuild = Build-PublishedReleaseRecordForPostSync -Text $targetRecordText -Version $TargetVersion -ReleaseCommitSha $ReleaseCommitSha -PublicDigest $publicDigest -Platforms $resolvedPlatforms -NugetPublicObservedAtUtc $nugetObservedAt -NugetSymbolsStatus $nugetSymbolsStatus -ObservedEvidence $ObservedEvidence
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
        [Parameter(Mandatory = $true)]
        [string]$ObservedEvidencePath,
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
    if ([string]::IsNullOrWhiteSpace($ObservedEvidencePath)) {
        throw 'ObservedEvidencePath is required for prepare-post-sync.'
    }

    $evidenceRead = Read-PostSyncObservedEvidenceFile -Path $ObservedEvidencePath
    if ($evidenceRead.State -ne 'PARSED') {
        throw ('ObservedEvidencePath is invalid: ' + $evidenceRead.Reason)
    }

    $verifyMap = Invoke-ReleaseVerify -Version $Version -ReleaseCommitSha $ReleaseCommitSha -RepoRoot $RepoRoot -Observers $Observers -Quiet

    $plan = Get-ReleasePreparePostSyncPlan -RepoRoot $RepoRoot -TargetVersion $Version -ReleaseCommitSha $ReleaseCommitSha -VerifyMap $verifyMap -Execute:$Execute -LocalRepoOverride $LocalRepoOverride -ObservedEvidence $evidenceRead.Evidence

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
