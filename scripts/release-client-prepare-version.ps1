# A-0 / Issue #685: guarded local Version Preparation (prepare-version).
# Without -Execute: zero file writes, zero external executors, zero Git/GitHub/public mutations.
# With -Execute: only Contracts, OpenAPI, and PENDING release-record scaffold may change.
# current-public authority and governed followers are never written by this command.

$script:PrepareVersionKeys = @(
    'COMMAND',
    'VERSION',
    'PREP_STATE',
    'CONTRACTS_STATE',
    'OPENAPI_STATE',
    'RELEASE_RECORD_STATE',
    'CHANGELOG_BOUNDARY',
    'FILES_PLANNED',
    'FILES_CHANGED',
    'MUTATION_RESULT',
    'MUTATION_ATTEMPTED',
    'MUTATION_PERFORMED',
    'CURRENT_PUBLIC_PRESERVED',
    'FOLLOWERS_PRESERVED',
    'EXTERNAL_MUTATION',
    'HUMAN_AUTHORIZATION_REQUIRED'
)

$script:PrepareVersionContractsRel = 'src/Amane.Mailer.Contracts/Amane.Mailer.Contracts.csproj'
$script:PrepareVersionOpenApiRel = 'docs/api/openapi.yaml'

# Optional test seam for bounded local writes. Production path uses Write-PostSyncTextFile.
# FailAfter: after N successful writes, the next write throws (null disables injection).
$script:PrepareVersionFileWriterFailAfter = $null
$script:PrepareVersionFileWriterCallCount = 0

function Set-PrepareVersionFileWriterFailAfter {
    param(
        [AllowNull()]
        $FailAfter
    )
    $script:PrepareVersionFileWriterFailAfter = $FailAfter
    $script:PrepareVersionFileWriterCallCount = 0
}

function Invoke-PrepareVersionOwnedFileWrite {
    param(
        [string]$Path,
        [string]$Content
    )
    if ($null -ne $script:PrepareVersionFileWriterFailAfter) {
        $script:PrepareVersionFileWriterCallCount = [int]$script:PrepareVersionFileWriterCallCount + 1
        if ([int]$script:PrepareVersionFileWriterCallCount -gt [int]$script:PrepareVersionFileWriterFailAfter) {
            throw 'injected later owned-file write failure'
        }
    }
    Write-PostSyncTextFile -Path $Path -Content $Content
}

function Get-PrepareVersionReleaseRecordRelativePath {
    param([string]$Version)
    return ('docs/releases/v{0}.md' -f $Version)
}

# Resolve supported platforms from current-public authority for PENDING scaffold.
# Fail-close on empty / malformed / ambiguous values; never invent platforms.
function Resolve-PrepareVersionPlatformsFromAuthority {
    param([string[]]$AuthorityPlatforms)

    $result = [pscustomobject]@{
        State     = 'INCOMPLETE'
        Platforms = @()
        Reason    = ''
    }

    $raw = @(
        @($AuthorityPlatforms) |
            ForEach-Object { ([string]$_).Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($raw.Count -eq 0) {
        $result.Reason = 'EMPTY_AUTHORITY_PLATFORMS'
        return $result
    }

    $confirmed = New-Object System.Collections.Generic.List[string]
    foreach ($platform in $raw) {
        if ($platform -notmatch '^linux/[a-z0-9_-]+$') {
            $result.Reason = 'MALFORMED_AUTHORITY_PLATFORM'
            return $result
        }
        if ($confirmed -contains $platform) {
            continue
        }
        [void]$confirmed.Add($platform)
    }

    if ($confirmed.Count -eq 0) {
        $result.Reason = 'EMPTY_AUTHORITY_PLATFORMS'
        return $result
    }

    # Deterministic order for exact scaffold comparison / idempotent TARGET checks.
    $sorted = @($confirmed | Sort-Object)
    $result.State = 'RESOLVED'
    $result.Platforms = $sorted
    $result.Reason = ''
    return $result
}

function Format-PrepareVersionSupportedPlatformLine {
    param([string]$Platform)
    # Canonical form shared with post-sync fixtures / Get-ReleaseRecordPlatformsFromText.
    return ('- supported platform: ``{0}``' -f $Platform)
}

function New-PrepareVersionPendingReleaseRecordText {
    param(
        [string]$Version,
        [string[]]$Platforms
    )

    $platformResolution = Resolve-PrepareVersionPlatformsFromAuthority -AuthorityPlatforms $Platforms
    if ($platformResolution.State -ne 'RESOLVED') {
        throw ('prepare-version pending record requires confirmed platforms: {0}' -f $platformResolution.Reason)
    }

    $tag = 'v' + $Version
    $platformLines = @(
        $platformResolution.Platforms | ForEach-Object { Format-PrepareVersionSupportedPlatformLine -Platform $_ }
    )
    $lines = @(
        ('# Release evidence — {0}' -f $tag)
        ''
        '> Status: **PENDING / NOT YET PUBLISHED**'
        '>'
        ('> Target: `{0}`' -f $Version)
        ''
        '## Release boundary'
        ''
        ('{0} is planned as a full service release. Public artifact identities are' -f $tag)
        'not recorded until they are observed after publication.'
        ''
        ('Version preparation updates Contracts and OpenAPI to `{0}` and creates this' -f $Version)
        'PENDING release record. `release/current-public.json` and governed current-public'
        'followers remain on the predecessor public version until verified publication and'
        '`prepare-post-sync`.'
        ''
        'CHANGELOG release-scope prose is a Human/Agent reviewed input and is not'
        'fabricated by `prepare-version`.'
        ''
        '## Version preparation'
        ''
        ('- `src/Amane.Mailer.Contracts/Amane.Mailer.Contracts.csproj`: `{0}`' -f $Version)
        ('- `docs/api/openapi.yaml` `info.version`: `{0}`' -f $Version)
        ('- `CHANGELOG.md`: reviewed `## [{0}]` entry required before Version Preparation acceptance' -f $Version)
        '- `release/current-public.json` and README / SECURITY / release-smoke defaults:'
        '  intentionally unchanged during Version Preparation'
        ''
        '## PENDING public identities'
        ''
        'The following fields are deliberately not guessed during version preparation:'
        ''
        '- `releaseCommitSha`: **PENDING — freeze after version-prep merge**'
        ('- Git tag `{0}`: **NOT YET PUBLISHED**' -f $tag)
        '- Git tag target: **PENDING**'
        '- annotated tag object: **PENDING**'
        ('- GHCR `ghcr.io/kooiei-in4a/amane-mailer:{0}`: **NOT YET PUBLISHED**' -f $tag)
        '- GHCR immutable `sha-<releaseCommitSha>` tag: **PENDING**'
        '- Public OCI digest: **PENDING**'
    )
    $lines += $platformLines
    $lines += @(
        '- Release image workflow run / attempt: **PENDING**'
        '- Publication evidence artifact name / ID: **PENDING**'
        '- Public-consumer verification evidence: **PENDING**'
        ('- NuGet `Amane.Mailer.Contracts {0}`: **NOT YET PUBLISHED**' -f $Version)
        '- NuGet symbol package status: **PENDING**'
        '- NuGet SourceLink revision: **PENDING**'
        '- NuGet publication timestamp: **PENDING** (NuGet service indexing time is not claimed)'
        '- NuGet public observed-at (UTC): **PENDING** (set when the canonical verifier observes the package)'
        ('- GitHub Release `{0}`: **NOT YET PUBLISHED**' -f $tag)
        '- GitHub Release ID: **PENDING**'
        '- GitHub Release URL: **PENDING**'
        '- GHCR `latest` digest promotion: **PENDING**'
        '- Consumer verification results: **PENDING**'
        ''
        '## Publication invariants'
        ''
        ('- Do not republish an existing `{0}` artifact.' -f $Version)
        '- Do not overwrite or move an existing release tag.'
        '- Do not overwrite GHCR version or immutable SHA tags.'
        '- Do not promote `latest` until versioned publication and consumer verification PASS.'
        '- Do not advance `release/current-public.json` before verified publication and deterministic post-sync.'
        '- Do not fabricate digests, SHAs, workflow run IDs, or publication timestamps.'
        ''
        'Only after observed public facts are recorded may this record change from **PENDING** to **PUBLISHED**.'
        ''
    )
    return [string]::Join("`n", $lines)
}

function Set-ContractsVersionInText {
    param(
        [string]$Text,
        [string]$Version
    )
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    if ($Text -notmatch '<Version>[^<]+</Version>') { return $null }
    return [regex]::Replace($Text, '<Version>[^<]+</Version>', ('<Version>{0}</Version>' -f $Version), 1)
}

function Set-OpenApiVersionInText {
    param(
        [string]$Text,
        [string]$Version
    )
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    if ($Text -notmatch '(?m)^[ \t]+version:\s*"[^"]+"') { return $null }
    $replacement = '${1}' + $Version + '${2}'
    return [regex]::Replace($Text, '(?m)^([ \t]+version:\s*")[^"]+(")', $replacement, 1)
}

function Normalize-PrepareVersionText {
    param([string]$Text)
    if ($null -eq $Text) { return '' }
    return ($Text -replace "`r`n", "`n" -replace "`r", "`n")
}

function Get-PrepareVersionOwnedFileState {
    param(
        [string]$ObservedVersion,
        [string]$TargetVersion,
        [string]$CanonicalPredecessor,
        [bool]$FilePresent
    )
    if (-not $FilePresent) { return 'ABSENT' }
    if ([string]::IsNullOrWhiteSpace($ObservedVersion)) { return 'INCOMPLETE' }
    if (-not (Test-ReleaseVersion $ObservedVersion)) { return 'CONFLICT' }
    if ($ObservedVersion -eq $TargetVersion) { return 'TARGET' }
    if ((Test-ReleaseVersion $CanonicalPredecessor) -and ($ObservedVersion -eq $CanonicalPredecessor)) {
        return 'PREDECESSOR'
    }
    return 'CONFLICT'
}

function Get-PrepareVersionReleaseRecordFileState {
    param(
        [string]$RepoRoot,
        [string]$Version,
        [string[]]$Platforms
    )
    $relativePath = Get-PrepareVersionReleaseRecordRelativePath -Version $Version
    $fullPath = Join-Path $RepoRoot $relativePath
    if (-not (Test-Path -LiteralPath $fullPath)) {
        return [pscustomobject]@{
            State        = 'ABSENT'
            RelativePath = $relativePath
            Text         = ''
        }
    }
    try {
        $text = Read-PostSyncTextFile -Path $fullPath
    }
    catch {
        return [pscustomobject]@{
            State        = 'INCOMPLETE'
            RelativePath = $relativePath
            Text         = ''
        }
    }

    try {
        $expected = New-PrepareVersionPendingReleaseRecordText -Version $Version -Platforms $Platforms
    }
    catch {
        return [pscustomobject]@{
            State        = 'INCOMPLETE'
            RelativePath = $relativePath
            Text         = $text
        }
    }
    $recordState = Get-ReleaseRecordStateFromText -Text $text
    if ($recordState -ne 'PENDING') {
        return [pscustomobject]@{
            State        = 'CONFLICT'
            RelativePath = $relativePath
            Text         = $text
        }
    }
    if ((Normalize-PrepareVersionText -Text $text) -eq (Normalize-PrepareVersionText -Text $expected)) {
        return [pscustomobject]@{
            State        = 'TARGET'
            RelativePath = $relativePath
            Text         = $text
        }
    }
    return [pscustomobject]@{
        State        = 'CONFLICT'
        RelativePath = $relativePath
        Text         = $text
    }
}

function New-PrepareVersionPlanObject {
    return [pscustomobject]@{
        MutationResult         = 'NOT_ATTEMPTED'
        MutationAttempted      = 'FALSE'
        MutationPerformed      = 'FALSE'
        PrepState              = 'INCOMPLETE'
        ContractsState         = 'INCOMPLETE'
        OpenApiState           = 'INCOMPLETE'
        ReleaseRecordState     = 'INCOMPLETE'
        CanonicalPredecessor   = ''
        ChangelogBoundary      = 'REVIEWED_ENTRY_REQUIRED'
        FilesPlanned           = @()
        FilesChanged           = @()
        FileWrites             = @()
        # By construction: this command only writes the three owned paths below.
        CurrentPublicPreserved = 'TRUE'
        FollowersPreserved     = 'TRUE'
        ExternalMutation       = 'FALSE'
        Reason                 = ''
    }
}

function Set-PrepareVersionConflict {
    param(
        $Plan,
        [string]$Reason,
        [string]$PrepState = 'CONFLICT',
        [string]$MutationResult = 'CONFLICT',
        $FilesPlanned = $null
    )
    $Plan.PrepState = $PrepState
    $Plan.MutationResult = $MutationResult
    $Plan.Reason = $Reason
    if ($null -ne $FilesPlanned) {
        $Plan.FilesPlanned = @($FilesPlanned | Sort-Object)
    }
    return $Plan
}

function Get-ReleasePrepareVersionPlan {
    param(
        [string]$RepoRoot,
        [string]$TargetVersion,
        [bool]$Execute
    )

    $plan = New-PrepareVersionPlanObject

    if (-not (Test-ReleaseVersion $TargetVersion)) {
        return (Set-PrepareVersionConflict -Plan $plan -Reason 'INVALID_VERSION' -PrepState 'INCOMPLETE' -MutationResult 'INCOMPLETE')
    }

    $authorityObs = Get-CurrentPublicAuthorityObservation -RepoRoot $RepoRoot
    if ($authorityObs.State -ne 'PRESENT' -or $null -eq $authorityObs.Authority -or $authorityObs.Authority.State -ne 'VALID') {
        return (Set-PrepareVersionConflict -Plan $plan -Reason 'AUTHORITY_UNREADABLE_OR_INVALID')
    }

    $canonicalPredecessor = [string]$authorityObs.Authority.Version
    $plan.CanonicalPredecessor = $canonicalPredecessor
    if (-not (Test-ReleaseVersion $canonicalPredecessor)) {
        return (Set-PrepareVersionConflict -Plan $plan -Reason 'AUTHORITY_UNREADABLE_OR_INVALID')
    }

    $platformResolution = Resolve-PrepareVersionPlatformsFromAuthority -AuthorityPlatforms @($authorityObs.Authority.Platforms)
    if ($platformResolution.State -ne 'RESOLVED') {
        return (Set-PrepareVersionConflict -Plan $plan -Reason $platformResolution.Reason)
    }
    $confirmedPlatforms = @($platformResolution.Platforms)

    if ($TargetVersion -eq $canonicalPredecessor) {
        $plan.ContractsState = 'CONFLICT'
        $plan.OpenApiState = 'CONFLICT'
        $plan.ReleaseRecordState = 'CONFLICT'
        return (Set-PrepareVersionConflict -Plan $plan -Reason 'TARGET_EQUALS_PREDECESSOR')
    }

    $contractsRel = $script:PrepareVersionContractsRel
    $openapiRel = $script:PrepareVersionOpenApiRel
    $contractsPath = Join-Path $RepoRoot $contractsRel
    $openapiPath = Join-Path $RepoRoot $openapiRel

    $contractsPresent = Test-Path -LiteralPath $contractsPath
    $openapiPresent = Test-Path -LiteralPath $openapiPath
    $contractsText = ''
    $openapiText = ''
    if ($contractsPresent) {
        try { $contractsText = Read-PostSyncTextFile -Path $contractsPath } catch { $contractsPresent = $false }
    }
    if ($openapiPresent) {
        try { $openapiText = Read-PostSyncTextFile -Path $openapiPath } catch { $openapiPresent = $false }
    }

    $contractsVersion = Get-ContractsVersionFromText -Text $contractsText
    $openapiVersion = Get-OpenApiVersionFromText -Text $openapiText
    $plan.ContractsState = Get-PrepareVersionOwnedFileState -ObservedVersion $contractsVersion -TargetVersion $TargetVersion -CanonicalPredecessor $canonicalPredecessor -FilePresent $contractsPresent
    $plan.OpenApiState = Get-PrepareVersionOwnedFileState -ObservedVersion $openapiVersion -TargetVersion $TargetVersion -CanonicalPredecessor $canonicalPredecessor -FilePresent $openapiPresent

    $recordObs = Get-PrepareVersionReleaseRecordFileState -RepoRoot $RepoRoot -Version $TargetVersion -Platforms $confirmedPlatforms
    $plan.ReleaseRecordState = $recordObs.State
    $recordRel = $recordObs.RelativePath
    $ownedPaths = @($contractsRel, $openapiRel, $recordRel)

    if ($plan.ContractsState -in @('ABSENT', 'INCOMPLETE', 'CONFLICT') -or
        $plan.OpenApiState -in @('ABSENT', 'INCOMPLETE', 'CONFLICT') -or
        $plan.ReleaseRecordState -eq 'INCOMPLETE') {
        return (Set-PrepareVersionConflict -Plan $plan -Reason 'OWNED_FILE_UNREADABLE_OR_INVALID' -FilesPlanned $ownedPaths)
    }

    if ($plan.ReleaseRecordState -eq 'CONFLICT') {
        return (Set-PrepareVersionConflict -Plan $plan -Reason 'RELEASE_RECORD_CONFLICT' -FilesPlanned $ownedPaths)
    }

    $allTarget = ($plan.ContractsState -eq 'TARGET' -and $plan.OpenApiState -eq 'TARGET' -and $plan.ReleaseRecordState -eq 'TARGET')
    $allEligible = ($plan.ContractsState -eq 'PREDECESSOR' -and $plan.OpenApiState -eq 'PREDECESSOR' -and $plan.ReleaseRecordState -eq 'ABSENT')
    $hasTarget = (@($plan.ContractsState, $plan.OpenApiState, $plan.ReleaseRecordState) -contains 'TARGET')
    $hasPredecessorOrAbsent = (
        ($plan.ContractsState -eq 'PREDECESSOR') -or
        ($plan.OpenApiState -eq 'PREDECESSOR') -or
        ($plan.ReleaseRecordState -eq 'ABSENT')
    )

    if ($allTarget) {
        $plan.PrepState = 'ALREADY_APPLIED'
        $plan.MutationResult = 'ALREADY_APPLIED'
        $plan.FilesPlanned = @($ownedPaths | Sort-Object)
        return $plan
    }

    if ($hasTarget -and $hasPredecessorOrAbsent) {
        return (Set-PrepareVersionConflict -Plan $plan -Reason 'MIXED_OWNED_STATE' -PrepState 'MIXED' -FilesPlanned $ownedPaths)
    }

    if (-not $allEligible) {
        return (Set-PrepareVersionConflict -Plan $plan -Reason 'UNEXPECTED_OWNED_STATE' -FilesPlanned $ownedPaths)
    }

    $newContracts = Set-ContractsVersionInText -Text $contractsText -Version $TargetVersion
    if ($null -eq $newContracts) {
        return (Set-PrepareVersionConflict -Plan $plan -Reason 'CONTRACTS_REWRITE_FAILED')
    }
    $newOpenApi = Set-OpenApiVersionInText -Text $openapiText -Version $TargetVersion
    if ($null -eq $newOpenApi) {
        return (Set-PrepareVersionConflict -Plan $plan -Reason 'OPENAPI_REWRITE_FAILED')
    }

    try {
        $scaffold = New-PrepareVersionPendingReleaseRecordText -Version $TargetVersion -Platforms $confirmedPlatforms
    }
    catch {
        return (Set-PrepareVersionConflict -Plan $plan -Reason 'PLATFORM_SCAFFOLD_FAILED')
    }
    $writes = @(
        @{ Path = $contractsRel; Content = $newContracts }
        @{ Path = $openapiRel; Content = $newOpenApi }
        @{ Path = $recordRel; Content = $scaffold }
    )

    $plan.PrepState = 'ELIGIBLE'
    $plan.FilesPlanned = @($ownedPaths | Sort-Object)
    $plan.FileWrites = $writes

    if (-not $Execute) {
        $plan.MutationResult = 'NOT_ATTEMPTED'
        return $plan
    }

    $changed = New-Object System.Collections.Generic.List[string]
    $plan.MutationAttempted = 'TRUE'
    try {
        foreach ($write in $writes) {
            $fullPath = Join-Path $RepoRoot $write.Path
            Invoke-PrepareVersionOwnedFileWrite -Path $fullPath -Content $write.Content
            [void]$changed.Add($write.Path)
        }
    }
    catch {
        # Partial local writes may remain; do not claim APPLIED. Next invocation fail-closes on MIXED.
        $plan.MutationPerformed = 'FALSE'
        $plan.MutationResult = 'INCOMPLETE'
        $plan.PrepState = 'CONFLICT'
        $plan.Reason = 'OWNED_FILE_WRITE_FAILED'
        $plan.FilesChanged = @($changed | Sort-Object)
        return $plan
    }

    $plan.MutationPerformed = 'TRUE'
    $plan.MutationResult = 'APPLIED'
    $plan.FilesChanged = @($changed | Sort-Object)
    return $plan
}

function Format-ReleasePrepareVersionLines {
    param($Plan, [string]$Version)
    $map = [ordered]@{}
    $map['COMMAND'] = 'PREPARE_VERSION'
    $map['VERSION'] = $Version
    $map['PREP_STATE'] = $Plan.PrepState
    $map['CONTRACTS_STATE'] = $Plan.ContractsState
    $map['OPENAPI_STATE'] = $Plan.OpenApiState
    $map['RELEASE_RECORD_STATE'] = $Plan.ReleaseRecordState
    $map['CHANGELOG_BOUNDARY'] = $Plan.ChangelogBoundary
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
    $map['CURRENT_PUBLIC_PRESERVED'] = $Plan.CurrentPublicPreserved
    $map['FOLLOWERS_PRESERVED'] = $Plan.FollowersPreserved
    $map['EXTERNAL_MUTATION'] = $Plan.ExternalMutation
    $map['HUMAN_AUTHORIZATION_REQUIRED'] = 'TRUE'

    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($key in $script:PrepareVersionKeys) {
        $value = ([string]$map[$key]) -replace '[\r\n]+', ' '
        [void]$lines.Add(('{0}={1}' -f $key, $value))
    }
    return $lines
}

function Invoke-ReleasePrepareVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version,
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        [switch]$Execute,
        [switch]$Quiet
    )

    if (-not (Test-ReleaseVersion $Version)) {
        throw 'Version must be X.Y.Z; the client does not infer or accept a v-prefix.'
    }

    # Version Preparation never resolves or invokes external mutation executors.
    $plan = Get-ReleasePrepareVersionPlan -RepoRoot $RepoRoot -TargetVersion $Version -Execute:$Execute

    if (-not $Quiet) {
        Write-ReleaseStderr ('release-client: prepare-version VERSION={0} (local version preparation only)' -f $Version)
        Write-ReleaseStderr ('release-client: PREP_STATE={0} MUTATION_RESULT={1} MUTATION_ATTEMPTED={2} MUTATION_PERFORMED={3}' -f $plan.PrepState, $plan.MutationResult, $plan.MutationAttempted, $plan.MutationPerformed)
        Write-ReleaseStderr 'release-client: CHANGELOG_BOUNDARY=REVIEWED_ENTRY_REQUIRED (prepare-version does not fabricate CHANGELOG prose)'
        Write-ReleaseStderr 'release-client: prepare-version does not advance current-public, publish artifacts, or create commits/PRs/tags'
        foreach ($line in (Format-ReleasePrepareVersionLines -Plan $plan -Version $Version)) {
            [Console]::Out.WriteLine($line)
        }
    }

    return [pscustomobject]@{
        Plan = $plan
    }
}
