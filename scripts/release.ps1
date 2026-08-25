<#
.SYNOPSIS
  Canonical PowerShell release client (Issue #664).

.DESCRIPTION
  RO-2 implements read-only `status` and `preflight`. Neither command updates
  refs, dispatches workflows, or publishes artifacts.

.PARAMETER Command
  Release command. RO-2 accepts `status` and `preflight`.

.PARAMETER Version
  Required target version as X.Y.Z. The client never infers this value.

.PARAMETER ReleaseCommitSha
  Required for `preflight`. Exact 40-lowercase-hex frozen source SHA.

.EXAMPLE
  .\scripts\release.ps1 status -Version 1.3.4

.EXAMPLE
  .\scripts\release.ps1 preflight -Version 1.3.5 -ReleaseCommitSha 528c73498136182810841009db4878364daa9fb1
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory = $true)]
    [string]$Command,

    [Parameter()]
    [string]$Version,

    [Parameter()]
    [string]$ReleaseCommitSha
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modulePath = Join-Path $PSScriptRoot 'release-client.psm1'
Import-Module -Force -DisableNameChecking $modulePath

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

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
    if ([string]::IsNullOrWhiteSpace($Version)) {
        [Console]::Error.WriteLine('release.ps1 preflight requires -Version X.Y.Z (version is never inferred).')
        exit 2
    }
    if ([string]::IsNullOrWhiteSpace($ReleaseCommitSha)) {
        [Console]::Error.WriteLine('release.ps1 preflight requires -ReleaseCommitSha <40-lowercase-hex> (source is never inferred).')
        exit 2
    }
    try {
        $null = Invoke-ReleasePreflight -Version $Version -ReleaseCommitSha $ReleaseCommitSha -RepoRoot $repoRoot
        exit 0
    }
    catch {
        [Console]::Error.WriteLine(('release.ps1: ' + $_.Exception.Message))
        exit 1
    }
}

[Console]::Error.WriteLine("release.ps1: command '$Command' is not implemented in RO-2 (status and preflight only).")
exit 2
