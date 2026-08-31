<#
.SYNOPSIS
  Canonical PowerShell release client (Issue #664).

.DESCRIPTION
  RO-3 implements read-only `status`, `preflight`, and `verify`.
  M-1 adds guarded mutation commands that require explicit `-Execute`.
  A-1 adds local `prepare-post-sync` that requires explicit `-Execute` for file writes.
  #675 adds guarded digest-preserving `promote-latest`.
  #685 adds local `prepare-version` for Version Preparation (Contracts/OpenAPI/PENDING record).

.PARAMETER Command
  Release command. Read-only: `status`, `preflight`, `verify`.
  Mutations (M-1): `publish-image`, `create-tag`, `publish-nuget`, `create-github-release`.
  Latest closeout (#675): `promote-latest`.
  Post-sync (A-1): `prepare-post-sync`.
  Version preparation (#685): `prepare-version`.

.PARAMETER Version
  Required target version as X.Y.Z. The client never infers this value.

.PARAMETER ReleaseCommitSha
  Required for `preflight`, `verify`, mutation commands, and `prepare-post-sync`.
  Not used by `prepare-version`.

.PARAMETER ExpectedDigest
  Required for `promote-latest`. Explicit sha256 digest; never inferred from a version tag.

.PARAMETER Execute
  Opt-in for mutation commands and local writers (`prepare-version`, `prepare-post-sync`).
  Without this switch, no executor calls / file writes occur and MUTATION_ATTEMPTED=FALSE.

.PARAMETER ReleaseNotesPath
  Required for `create-github-release`. Explicit repository-relative or absolute path
  to release notes content.

.EXAMPLE
  .\scripts\release.ps1 status -Version X.Y.Z

.EXAMPLE
  .\scripts\release.ps1 prepare-version -Version X.Y.Z

.EXAMPLE
  .\scripts\release.ps1 prepare-version -Version X.Y.Z -Execute

.EXAMPLE
  .\scripts\release.ps1 preflight -Version X.Y.Z -ReleaseCommitSha <40-lowercase-hex>

.EXAMPLE
  .\scripts\release.ps1 publish-image -Version X.Y.Z -ReleaseCommitSha <40-lowercase-hex> -Execute

.EXAMPLE
  .\scripts\release.ps1 promote-latest -Version X.Y.Z -ReleaseCommitSha <40-lowercase-hex> -ExpectedDigest sha256:<64-lowercase-hex>

.EXAMPLE
  .\scripts\release.ps1 prepare-post-sync -Version X.Y.Z -ReleaseCommitSha <40-lowercase-hex> -ObservedEvidencePath <json>
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory = $true)]
    [string]$Command,

    [Parameter()]
    [string]$Version,

    [Parameter()]
    [string]$ReleaseCommitSha,

    [Parameter()]
    [string]$ExpectedDigest,

    [Parameter()]
    [switch]$Execute,

    [Parameter()]
    [string]$ReleaseNotesPath,

    [Parameter()]
    [string]$ObservedEvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modulePath = Join-Path $PSScriptRoot 'release-client.psm1'
Import-Module -Force -DisableNameChecking $modulePath

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Test-MutationParameters {
    param(
        [string]$CommandName,
        [string]$VersionValue,
        [string]$ShaValue
    )
    if ([string]::IsNullOrWhiteSpace($VersionValue)) {
        [Console]::Error.WriteLine(("release.ps1 {0} requires -Version X.Y.Z (version is never inferred)." -f $CommandName))
        exit 2
    }
    if ([string]::IsNullOrWhiteSpace($ShaValue)) {
        [Console]::Error.WriteLine(("release.ps1 {0} requires -ReleaseCommitSha <40-lowercase-hex> (source is never inferred)." -f $CommandName))
        exit 2
    }
}

if ($Command -eq 'status') {
    if ([string]::IsNullOrWhiteSpace($Version)) {
        [Console]::Error.WriteLine('release.ps1 status requires -Version X.Y.Z (version is never inferred).')
        exit 2
    }
    try {
        $null = Invoke-ReleaseStatus -Version $Version -RepoRoot $repoRoot
        exit 0
    }
    catch {
        [Console]::Error.WriteLine(('release.ps1: ' + $_.Exception.Message))
        exit 1
    }
}

if ($Command -eq 'preflight') {
    Test-MutationParameters -CommandName 'preflight' -VersionValue $Version -ShaValue $ReleaseCommitSha
    try {
        $null = Invoke-ReleasePreflight -Version $Version -ReleaseCommitSha $ReleaseCommitSha -RepoRoot $repoRoot
        exit 0
    }
    catch {
        [Console]::Error.WriteLine(('release.ps1: ' + $_.Exception.Message))
        exit 1
    }
}

if ($Command -eq 'verify') {
    Test-MutationParameters -CommandName 'verify' -VersionValue $Version -ShaValue $ReleaseCommitSha
    try {
        $null = Invoke-ReleaseVerify -Version $Version -ReleaseCommitSha $ReleaseCommitSha -RepoRoot $repoRoot
        exit 0
    }
    catch {
        [Console]::Error.WriteLine(('release.ps1: ' + $_.Exception.Message))
        exit 1
    }
}

if ($Command -eq 'publish-image') {
    Test-MutationParameters -CommandName 'publish-image' -VersionValue $Version -ShaValue $ReleaseCommitSha
    try {
        $null = Invoke-ReleasePublishImage -Version $Version -ReleaseCommitSha $ReleaseCommitSha -RepoRoot $repoRoot -Execute:$Execute
        exit 0
    }
    catch {
        [Console]::Error.WriteLine(('release.ps1: ' + $_.Exception.Message))
        exit 1
    }
}

if ($Command -eq 'create-tag') {
    Test-MutationParameters -CommandName 'create-tag' -VersionValue $Version -ShaValue $ReleaseCommitSha
    try {
        $null = Invoke-ReleaseCreateTag -Version $Version -ReleaseCommitSha $ReleaseCommitSha -RepoRoot $repoRoot -Execute:$Execute
        exit 0
    }
    catch {
        [Console]::Error.WriteLine(('release.ps1: ' + $_.Exception.Message))
        exit 1
    }
}

if ($Command -eq 'publish-nuget') {
    Test-MutationParameters -CommandName 'publish-nuget' -VersionValue $Version -ShaValue $ReleaseCommitSha
    try {
        $null = Invoke-ReleasePublishNuget -Version $Version -ReleaseCommitSha $ReleaseCommitSha -RepoRoot $repoRoot -Execute:$Execute
        exit 0
    }
    catch {
        [Console]::Error.WriteLine(('release.ps1: ' + $_.Exception.Message))
        exit 1
    }
}

if ($Command -eq 'create-github-release') {
    Test-MutationParameters -CommandName 'create-github-release' -VersionValue $Version -ShaValue $ReleaseCommitSha
    if ([string]::IsNullOrWhiteSpace($ReleaseNotesPath)) {
        [Console]::Error.WriteLine('release.ps1 create-github-release requires -ReleaseNotesPath <file> (explicit release notes file).')
        exit 2
    }
    try {
        $null = Invoke-ReleaseCreateGitHubRelease -Version $Version -ReleaseCommitSha $ReleaseCommitSha -RepoRoot $repoRoot -ReleaseNotesPath $ReleaseNotesPath -Execute:$Execute
        exit 0
    }
    catch {
        [Console]::Error.WriteLine(('release.ps1: ' + $_.Exception.Message))
        exit 1
    }
}

if ($Command -eq 'promote-latest') {
    Test-MutationParameters -CommandName 'promote-latest' -VersionValue $Version -ShaValue $ReleaseCommitSha
    if ([string]::IsNullOrWhiteSpace($ExpectedDigest)) {
        [Console]::Error.WriteLine('release.ps1 promote-latest requires -ExpectedDigest sha256:<64-lowercase-hex> (digest is never inferred from a version tag).')
        exit 2
    }
    try {
        $null = Invoke-ReleasePromoteLatest -Version $Version -ReleaseCommitSha $ReleaseCommitSha -ExpectedDigest $ExpectedDigest -RepoRoot $repoRoot -Execute:$Execute
        exit 0
    }
    catch {
        [Console]::Error.WriteLine(('release.ps1: ' + $_.Exception.Message))
        exit 1
    }
}

if ($Command -eq 'prepare-post-sync') {
    Test-MutationParameters -CommandName 'prepare-post-sync' -VersionValue $Version -ShaValue $ReleaseCommitSha
    if ([string]::IsNullOrWhiteSpace($ObservedEvidencePath)) {
        [Console]::Error.WriteLine('release.ps1 prepare-post-sync requires -ObservedEvidencePath <json> (canonical observed-evidence manifest).')
        exit 2
    }
    try {
        $null = Invoke-ReleasePreparePostSync -Version $Version -ReleaseCommitSha $ReleaseCommitSha -RepoRoot $repoRoot -ObservedEvidencePath $ObservedEvidencePath -Execute:$Execute
        exit 0
    }
    catch {
        [Console]::Error.WriteLine(('release.ps1: ' + $_.Exception.Message))
        exit 1
    }
}

if ($Command -eq 'prepare-version') {
    if ([string]::IsNullOrWhiteSpace($Version)) {
        [Console]::Error.WriteLine('release.ps1 prepare-version requires -Version X.Y.Z (version is never inferred).')
        exit 2
    }
    try {
        $null = Invoke-ReleasePrepareVersion -Version $Version -RepoRoot $repoRoot -Execute:$Execute
        exit 0
    }
    catch {
        [Console]::Error.WriteLine(('release.ps1: ' + $_.Exception.Message))
        exit 1
    }
}

[Console]::Error.WriteLine("release.ps1: command '$Command' is not implemented (status, preflight, verify, publish-image, create-tag, publish-nuget, create-github-release, promote-latest, prepare-post-sync, prepare-version).")
exit 2
