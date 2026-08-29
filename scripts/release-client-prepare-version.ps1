# A-0 / Issue #685: guarded local Version Preparation (prepare-version).
# Without -Execute: zero file writes, zero external executors, zero Git/GitHub/public mutations.
# With -Execute: only Contracts, OpenAPI, and PENDING release-record scaffold may change.
# current-public authority and governed followers are never advanced by this command.

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

$script:PrepareVersionOwnedPaths = @(
    'src/Amane.Mailer.Contracts/Amane.Mailer.Contracts.csproj'
    'docs/api/openapi.yaml'
)

$script:PrepareVersionPreservedPaths = @(
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

function Get-PrepareVersionReleaseRecordRelativePath {
    param([string]$Version)
    return ('docs/releases/v{0}.md' -f $Version)
}

function New-PrepareVersionPendingReleaseRecordText {
    param([string]$Version)

    $tag = 'v' + $Version
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

function Get-PrepareVersionFileFingerprint {
    param(
        [string]$RepoRoot,
        [string]$RelativePath
    )
    $fullPath = Join-Path $RepoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $fullPath)) {
        return 'ABSENT'
    }
    try {
        $bytes = [System.IO.File]::ReadAllBytes($fullPath)
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hash = $sha.ComputeHash($bytes)
            return ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
        }
        finally {
            $sha.Dispose()
        }
    }
    catch {
        return 'INCOMPLETE'
    }
}

function Get-PrepareVersionPreservedFingerprintMap {
    param([string]$RepoRoot)
    $map = [ordered]@{}
    foreach ($relativePath in $script:PrepareVersionPreservedPaths) {
        $map[$relativePath] = (Get-PrepareVersionFileFingerprint -RepoRoot $RepoRoot -RelativePath $relativePath)
    }
    return $map
}

function Test-PrepareVersionFingerprintsUnchanged {
    param(
        $Before,
        $After
    )
    foreach ($key in @($Before.Keys)) {
        if ([string]$Before[$key] -ne [string]$After[$key]) {
            return $false
        }
    }
    return $true
}

function Normalize-PrepareVersionText {
    param([string]$Text)
    if ($null -eq $Text) { return '' }
    $normalized = $Text -replace "`r`n", "`n" -replace "`r", "`n"
    return $normalized
}

function Get-PrepareVersionOwnedFileState {
    param(
        [string]$ObservedVersion,
        [string]$TargetVersion,
        [bool]$FilePresent
    )
    if (-not $FilePresent) { return 'ABSENT' }
    if ([string]::IsNullOrWhiteSpace($ObservedVersion)) { return 'INCOMPLETE' }
    if (-not (Test-ReleaseVersion $ObservedVersion)) { return 'CONFLICT' }
    if ($ObservedVersion -eq $TargetVersion) { return 'TARGET' }
    return 'PREDECESSOR'
}

function Get-PrepareVersionReleaseRecordFileState {
    param(
        [string]$RepoRoot,
        [string]$Version
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

    $expected = New-PrepareVersionPendingReleaseRecordText -Version $Version
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

function Get-ReleasePrepareVersionPlan {
    param(
        [string]$RepoRoot,
        [string]$TargetVersion,
        [bool]$Execute
    )

    $plan = [pscustomobject]@{
        MutationResult          = 'NOT_ATTEMPTED'
        MutationAttempted       = 'FALSE'
        MutationPerformed       = 'FALSE'
        PrepState               = 'INCOMPLETE'
        ContractsState          = 'INCOMPLETE'
        OpenApiState            = 'INCOMPLETE'
        ReleaseRecordState      = 'INCOMPLETE'
        ChangelogBoundary       = 'REVIEWED_ENTRY_REQUIRED'
        FilesPlanned            = @()
        FilesChanged            = @()
        FileWrites              = @()
        CurrentPublicPreserved  = 'TRUE'
        FollowersPreserved      = 'TRUE'
        ExternalMutation        = 'FALSE'
        Reason                  = ''
        BeforeFingerprints      = $null
        AfterFingerprints       = $null
    }

    if (-not (Test-ReleaseVersion $TargetVersion)) {
        $plan.Reason = 'INVALID_VERSION'
        $plan.MutationResult = 'INCOMPLETE'
        $plan.PrepState = 'INCOMPLETE'
        return $plan
    }

    $before = Get-PrepareVersionPreservedFingerprintMap -RepoRoot $RepoRoot
    $plan.BeforeFingerprints = $before

    $contractsRel = $script:PrepareVersionOwnedPaths[0]
    $openapiRel = $script:PrepareVersionOwnedPaths[1]
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
    $plan.ContractsState = Get-PrepareVersionOwnedFileState -ObservedVersion $contractsVersion -TargetVersion $TargetVersion -FilePresent $contractsPresent
    $plan.OpenApiState = Get-PrepareVersionOwnedFileState -ObservedVersion $openapiVersion -TargetVersion $TargetVersion -FilePresent $openapiPresent

    $recordObs = Get-PrepareVersionReleaseRecordFileState -RepoRoot $RepoRoot -Version $TargetVersion
    $plan.ReleaseRecordState = $recordObs.State
    $recordRel = $recordObs.RelativePath

    if ($plan.ContractsState -in @('ABSENT', 'INCOMPLETE', 'CONFLICT') -or
        $plan.OpenApiState -in @('ABSENT', 'INCOMPLETE', 'CONFLICT') -or
        $plan.ReleaseRecordState -eq 'INCOMPLETE') {
        $plan.PrepState = 'CONFLICT'
        $plan.MutationResult = 'CONFLICT'
        $plan.Reason = 'OWNED_FILE_UNREADABLE_OR_INVALID'
        $plan.FilesPlanned = @(@($contractsRel, $openapiRel, $recordRel) | Sort-Object)
        return $plan
    }

    if ($plan.ReleaseRecordState -eq 'CONFLICT') {
        $plan.PrepState = 'CONFLICT'
        $plan.MutationResult = 'CONFLICT'
        $plan.Reason = 'RELEASE_RECORD_CONFLICT'
        $plan.FilesPlanned = @(@($contractsRel, $openapiRel, $recordRel) | Sort-Object)
        return $plan
    }

    $states = @($plan.ContractsState, $plan.OpenApiState, $plan.ReleaseRecordState)
    $hasTarget = ($states -contains 'TARGET')
    $hasPredecessorOrAbsent = (($plan.ContractsState -eq 'PREDECESSOR') -or ($plan.OpenApiState -eq 'PREDECESSOR') -or ($plan.ReleaseRecordState -eq 'ABSENT'))
    $allTarget = ($plan.ContractsState -eq 'TARGET' -and $plan.OpenApiState -eq 'TARGET' -and $plan.ReleaseRecordState -eq 'TARGET')
    $allEligible = ($plan.ContractsState -eq 'PREDECESSOR' -and $plan.OpenApiState -eq 'PREDECESSOR' -and $plan.ReleaseRecordState -eq 'ABSENT')

    if ($allTarget) {
        $plan.PrepState = 'ALREADY_APPLIED'
        $plan.MutationResult = 'ALREADY_APPLIED'
        $plan.FilesPlanned = @(@($contractsRel, $openapiRel, $recordRel) | Sort-Object)
        $after = Get-PrepareVersionPreservedFingerprintMap -RepoRoot $RepoRoot
        $plan.AfterFingerprints = $after
        $plan.CurrentPublicPreserved = if ($before['release/current-public.json'] -eq $after['release/current-public.json']) { 'TRUE' } else { 'FALSE' }
        $plan.FollowersPreserved = if (Test-PrepareVersionFingerprintsUnchanged -Before $before -After $after) { 'TRUE' } else { 'FALSE' }
        return $plan
    }

    if ($hasTarget -and $hasPredecessorOrAbsent) {
        $plan.PrepState = 'MIXED'
        $plan.MutationResult = 'CONFLICT'
        $plan.Reason = 'MIXED_OWNED_STATE'
        $plan.FilesPlanned = @(@($contractsRel, $openapiRel, $recordRel) | Sort-Object)
        return $plan
    }

    if (-not $allEligible) {
        $plan.PrepState = 'CONFLICT'
        $plan.MutationResult = 'CONFLICT'
        $plan.Reason = 'UNEXPECTED_OWNED_STATE'
        $plan.FilesPlanned = @(@($contractsRel, $openapiRel, $recordRel) | Sort-Object)
        return $plan
    }

    $planned = New-Object System.Collections.Generic.List[string]
    $writes = New-Object System.Collections.Generic.List[hashtable]

    $newContracts = Set-ContractsVersionInText -Text $contractsText -Version $TargetVersion
    if ($null -eq $newContracts) {
        $plan.PrepState = 'CONFLICT'
        $plan.MutationResult = 'CONFLICT'
        $plan.Reason = 'CONTRACTS_REWRITE_FAILED'
        return $plan
    }
    [void]$planned.Add($contractsRel)
    [void]$writes.Add(@{ Path = $contractsRel; Content = $newContracts })

    $newOpenApi = Set-OpenApiVersionInText -Text $openapiText -Version $TargetVersion
    if ($null -eq $newOpenApi) {
        $plan.PrepState = 'CONFLICT'
        $plan.MutationResult = 'CONFLICT'
        $plan.Reason = 'OPENAPI_REWRITE_FAILED'
        return $plan
    }
    [void]$planned.Add($openapiRel)
    [void]$writes.Add(@{ Path = $openapiRel; Content = $newOpenApi })

    $scaffold = New-PrepareVersionPendingReleaseRecordText -Version $TargetVersion
    [void]$planned.Add($recordRel)
    [void]$writes.Add(@{ Path = $recordRel; Content = $scaffold })

    $plan.PrepState = 'ELIGIBLE'
    $plan.FilesPlanned = @($planned | Sort-Object)
    $plan.FileWrites = @($writes)

    if (-not $Execute) {
        $plan.MutationResult = 'NOT_ATTEMPTED'
        $after = Get-PrepareVersionPreservedFingerprintMap -RepoRoot $RepoRoot
        $plan.AfterFingerprints = $after
        $plan.CurrentPublicPreserved = if ($before['release/current-public.json'] -eq $after['release/current-public.json']) { 'TRUE' } else { 'FALSE' }
        $plan.FollowersPreserved = if (Test-PrepareVersionFingerprintsUnchanged -Before $before -After $after) { 'TRUE' } else { 'FALSE' }
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

    $after = Get-PrepareVersionPreservedFingerprintMap -RepoRoot $RepoRoot
    $plan.AfterFingerprints = $after
    $plan.CurrentPublicPreserved = if ($before['release/current-public.json'] -eq $after['release/current-public.json']) { 'TRUE' } else { 'FALSE' }
    $followersOk = Test-PrepareVersionFingerprintsUnchanged -Before $before -After $after
    $plan.FollowersPreserved = if ($followersOk) { 'TRUE' } else { 'FALSE' }
    if (-not $followersOk -or $plan.CurrentPublicPreserved -ne 'TRUE') {
        $plan.MutationResult = 'CONFLICT'
        $plan.Reason = 'PRESERVATION_VIOLATION'
        $plan.PrepState = 'CONFLICT'
    }
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
        $value = [string]$map[$key]
        $value = $value -replace '[\r\n]+', ' '
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
