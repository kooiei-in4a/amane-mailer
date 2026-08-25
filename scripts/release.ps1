<#
.SYNOPSIS
  Canonical PowerShell release client (Issue #664).

.DESCRIPTION
  RO-1 implements read-only `status` only. The client reconstructs current
  release state from local git identity and public GitHub / GHCR / NuGet
  observations. It does not update refs, dispatch workflows, or publish.

.PARAMETER Command
  Release command. RO-1 accepts only `status`.

.PARAMETER Version
  Required target version as X.Y.Z. The client never infers this value.

.EXAMPLE
  .\scripts\release.ps1 status -Version 1.3.4
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory = $true)]
    [string]$Command,

    [Parameter()]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modulePath = Join-Path $PSScriptRoot 'release-client.psm1'
Import-Module -Force -DisableNameChecking $modulePath

if ($Command -ne 'status') {
    [Console]::Error.WriteLine("release.ps1: command '$Command' is not implemented in RO-1 (status only).")
    exit 2
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    [Console]::Error.WriteLine('release.ps1 status requires -Version X.Y.Z (version is never inferred).')
    exit 2
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
try {
    $null = Invoke-ReleaseStatus -Version $Version -RepoRoot $repoRoot
    exit 0
}
catch {
    [Console]::Error.WriteLine(('release.ps1: ' + $_.Exception.Message))
    exit 1
}
