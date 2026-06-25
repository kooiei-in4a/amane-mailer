<#
.SYNOPSIS
  Bootstrap and verify the shared Mailer deploy compose stack on local Docker.

.DESCRIPTION
  Prepares infra/deploy/.env and tenants.json from checked-in examples (when missing),
  optionally builds a local image, runs mailer-migrate and mailer, then checks
  /healthz, /readyz, CLI healthcheck, tenant token env vars, and the external Docker network DNS.

  Does not run ACS live-send drills or touch deploy host resources.

.PARAMETER Build
  Build amane-mailer:local-rehearsal from infra/docker/Dockerfile instead of pulling GHCR.

.PARAMETER RunSmoke
  Opt in to MAIL-05a no-send smoke after the core rehearsal checks. Requires bash,
  python3, and the same Docker context as this PowerShell session. Not run by default.

.EXAMPLE
  .\scripts\local-rehearsal.ps1 -Build

.EXAMPLE
  .\scripts\local-rehearsal.ps1 -Build -RunSmoke
#>
[CmdletBinding()]
param(
    [switch]$Build,
    [switch]$RunSmoke
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$DeployDir = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$RepoRoot = (Resolve-Path (Join-Path $DeployDir '..\..')).Path
$EnvFile = Join-Path $DeployDir '.env'
$TenantsFile = Join-Path $DeployDir 'tenants.json'
$DataDir = Join-Path $DeployDir 'data'
$Port = 5281
$BaseUrl = "http://127.0.0.1:$Port"

$ComposeFiles = @(
    '-f', (Join-Path $DeployDir 'compose.yml'),
    '-f', (Join-Path $DeployDir 'compose.local-rehearsal.yml')
)
if ($Build) {
    $ComposeFiles += '-f', (Join-Path $DeployDir 'compose.local-rehearsal.build.yml')
}

function Get-ContainerEnvPresent {
    param([string]$Name)
    $containerId = (& docker compose --env-file $EnvFile @ComposeFiles ps -q mailer).Trim()
    if ([string]::IsNullOrWhiteSpace($containerId)) {
        throw 'mailer container not running'
    }
    $lines = docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' $containerId
    foreach ($line in $lines) {
        if ($line -match "^$([regex]::Escape($Name))=(.+)$") {
            if ([string]::IsNullOrEmpty($Matches[1])) { return $false }
            return $true
        }
    }
    return $false
}

function Get-EnvValue {
    param([string]$Name)
    if (-not (Test-Path -LiteralPath $EnvFile)) {
        return ''
    }

    $line = Get-Content -LiteralPath $EnvFile |
        Where-Object { $_ -match "^$([regex]::Escape($Name))=" } |
        Select-Object -First 1
    if (-not $line) {
        return ''
    }

    $value = $line -replace '^[^=]*=', ''
    if ($value.Length -ge 2 -and $value[0] -eq $value[$value.Length - 1] -and ($value[0] -eq '"' -or $value[0] -eq "'")) {
        return $value.Substring(1, $value.Length - 2)
    }

    return $value
}

function Invoke-DeployCompose {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Args)
    Push-Location $DeployDir
    try {
        & docker compose --env-file $EnvFile @ComposeFiles @Args
        if ($LASTEXITCODE -ne 0) { throw "docker compose failed: $($Args -join ' ')" }
    }
    finally {
        Pop-Location
    }
}

function Get-Mail05aComposeExtra {
    $extras = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $ComposeFiles.Length; $i++) {
        if ($ComposeFiles[$i] -ne '-f') { continue }
        $path = $ComposeFiles[$i + 1]
        $name = Split-Path -Leaf $path
        if ($name -ne 'compose.yml') {
            [void]$extras.Add($name)
        }
    }
    return ($extras -join ' ')
}

function Get-BashDeployDir {
    $forward = ($DeployDir -replace '\\', '/')
    $uname = (& bash -lc 'uname -r 2>/dev/null' 2>$null | Out-String).Trim()
    if ($uname -match '(?i)microsoft|wsl') {
        $escaped = $DeployDir.Replace("'", "''")
        $wslPath = (& bash -lc "wslpath -u '$escaped' 2>/dev/null" 2>$null | Out-String).Trim()
        if ($wslPath) { return $wslPath }
        if ($forward -match '^([A-Za-z]):/(.*)$') {
            return "/mnt/$($Matches[1].ToLower())/$($Matches[2])"
        }
    }
    return $forward
}

function Test-BashDeployDirAccessible {
    param([string]$DeployDirBash)
    $escaped = $DeployDirBash.Replace("'", "'\\''")
    $check = (& bash -lc "cd '$escaped' && echo OK" 2>&1 | Out-String).Trim()
    return ($check -eq 'OK')
}

function Test-BashDockerSeesMailer {
    param(
        [string]$ComposeExtra,
        [ref]$FailureKind
    )
    $deployDirBash = Get-BashDeployDir
    if (-not (Test-BashDeployDirAccessible -DeployDirBash $deployDirBash)) {
        $FailureKind.Value = 'path'
        return $false
    }

    $extraArgs = ''
    if ($ComposeExtra) {
        $extraArgs = ($ComposeExtra -split ' ' | ForEach-Object { "-f $_" }) -join ' '
    }
    $escaped = $deployDirBash.Replace("'", "'\\''")
    $bashCmd = "cd '$escaped' && docker compose --env-file .env -f compose.yml $extraArgs ps -q mailer 2>/dev/null | tr -d '\r\n'"
    $result = (& bash -lc $bashCmd 2>$null | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($result)) {
        $FailureKind.Value = 'docker'
        return $false
    }
    $FailureKind.Value = ''
    return $true
}

function Ensure-FileFromTemplate {
    param(
        [string]$Target,
        [string]$Template,
        [scriptblock]$Customize
    )
    if (Test-Path -LiteralPath $Target) {
        Write-Host "[skip] exists: $Target"
        return
    }
    if (-not (Test-Path -LiteralPath $Template)) {
        throw "Missing template: $Template"
    }
    $content = Get-Content -LiteralPath $Template -Raw
    if ($null -ne $Customize) {
        $content = & $Customize $content
    }
    Set-Content -LiteralPath $Target -Value $content -NoNewline -Encoding utf8
    Write-Host "[created] $Target"
}

Write-Host "=== Local deploy rehearsal ==="
Write-Host "Deploy dir: $DeployDir"

Ensure-FileFromTemplate -Target $TenantsFile -Template (Join-Path $RepoRoot 'config\mailer\tenants.shared.example.json')

Ensure-FileFromTemplate -Target $EnvFile -Template (Join-Path $DeployDir '.env.example') -Customize {
    param($content)
  if ($Build) {
    $content = $content -replace '(?m)^MAILER_IMAGE_REPOSITORY=.*$', 'MAILER_IMAGE_REPOSITORY=amane-mailer'
    $content = $content -replace '(?m)^MAILER_IMAGE_TAG=.*$', 'MAILER_IMAGE_TAG=local-rehearsal'
    $content = $content -replace '(?m)^MAILER_PULL_POLICY=.*$', 'MAILER_PULL_POLICY=never'
  }
  $content = $content -replace 'replace-with-private-develop-token', 'local-rehearsal-develop-token'
  $content = $content -replace 'replace-with-private-staging-token', 'local-rehearsal-staging-token'
  $content = $content -replace 'replace-with-private-production-token', 'local-rehearsal-production-token'
  $content = $content -replace 'replace-with-private-token', 'local-rehearsal-placeholder-token'
  $content
}

New-Item -ItemType Directory -Force -Path $DataDir | Out-Null

Write-Host "[step] docker compose config"
Invoke-DeployCompose config --quiet

if ($Build) {
    Write-Host "[step] docker compose build mailer mailer-migrate"
    Invoke-DeployCompose build mailer mailer-migrate
}

Write-Host "[step] mailer-migrate"
Invoke-DeployCompose --profile ops run --rm mailer-migrate

Write-Host "[step] mailer up"
Invoke-DeployCompose up -d --wait mailer

Write-Host "[step] GET /healthz"
$health = Invoke-WebRequest -UseBasicParsing "$BaseUrl/healthz"
if ($health.StatusCode -ne 200 -or $health.Content -notmatch '"healthy"\s*:\s*true') {
    throw "Unexpected /healthz: $($health.StatusCode) $($health.Content)"
}
Write-Host $health.Content

Write-Host "[step] GET /readyz"
$ready = Invoke-WebRequest -UseBasicParsing "$BaseUrl/readyz"
if ($ready.StatusCode -ne 200 -or $ready.Content -notmatch '"ready"\s*:\s*true') {
    throw "Unexpected /readyz: $($ready.StatusCode) $($ready.Content)"
}
Write-Host $ready.Content

Write-Host "[step] CLI healthcheck"
Invoke-DeployCompose exec -T mailer /app/Amane.Mailer healthcheck

Write-Host "[step] tenant token env vars"
$tokenChecks = @(
    @{ Env = 'MAIL_SERVICE_TOKEN_DEVELOP'; TokenEnv = 'MAIL_SERVICE_TOKEN_DEVELOP' },
    @{ Env = 'MAIL_SERVICE_TOKEN_STAGING'; TokenEnv = 'MAIL_SERVICE_TOKEN_STAGING' },
    @{ Env = 'MAIL_SERVICE_TOKEN_PRODUCTION'; TokenEnv = 'MAIL_SERVICE_TOKEN_PRODUCTION' }
)
$tenantsJson = Get-Content -LiteralPath $TenantsFile -Raw | ConvertFrom-Json
foreach ($check in $tokenChecks) {
    $tenant = $tenantsJson.tenants | Where-Object { $_.token_env -eq $check.TokenEnv } | Select-Object -First 1
    if (-not $tenant) {
        throw "tenants.json has no tenant with token_env=$($check.TokenEnv)"
    }
    if (-not (Get-ContainerEnvPresent -Name $check.Env)) {
        throw "Container missing $($check.Env)"
    }
    $expectedLine = Get-Content -LiteralPath $EnvFile | Where-Object { $_ -match "^$($check.Env)=" } | Select-Object -First 1
    if (-not $expectedLine -or $expectedLine -match '^[^=]+=\s*$') {
        throw ".env missing value for $($check.Env)"
    }
    Write-Host "  OK $($check.Env) -> tenant $($tenant.name)"
}

Write-Host "[step] MAILER_NETWORK_NAME network and alias mailer"
$networkName = (Get-Content -LiteralPath $EnvFile | Where-Object { $_ -match '^MAILER_NETWORK_NAME=' } | Select-Object -First 1) -replace '^MAILER_NETWORK_NAME=', ''
if ([string]::IsNullOrWhiteSpace($networkName)) { $networkName = 'amane_mailer' }
$networkInspect = docker network inspect $networkName 2>$null | ConvertFrom-Json
if (-not $networkInspect) {
    throw "Docker network not found: $networkName"
}
Write-Host "  network=$networkName"

$internalNetwork = (Get-Content -LiteralPath $EnvFile | Where-Object { $_ -match '^COMPOSE_PROJECT_NAME=' } | Select-Object -First 1) -replace '^COMPOSE_PROJECT_NAME=', ''
if ([string]::IsNullOrWhiteSpace($internalNetwork)) { $internalNetwork = 'amane-mailer' }
$internalNetworkName = "${internalNetwork}_internal"
$internalInspect = docker network inspect $internalNetworkName 2>$null | ConvertFrom-Json
if (-not $internalInspect) {
    throw "Docker internal network not found: $internalNetworkName"
}
if (-not $internalInspect[0].Internal) {
    throw "Expected internal=true on $internalNetworkName"
}
Write-Host "  $internalNetworkName internal=true"

$mailerHttpPort = Get-EnvValue "MAILER_HTTP_PORT"
if ([string]::IsNullOrWhiteSpace($mailerHttpPort)) { $mailerHttpPort = "8080" }
$aliasHealth = docker run --rm --network $networkName curlimages/curl:8.11.1 -fsS "http://mailer:${mailerHttpPort}/healthz"
Write-Host "  DNS alias healthz: $aliasHealth"

if ($RunSmoke) {
    Write-Host "[step] MAIL-05a no-send smoke (opt-in)"
    $bash = Get-Command bash -ErrorAction SilentlyContinue
    if (-not $bash) {
        throw '-RunSmoke requires bash in PATH (Git Bash recommended on Windows).'
    }

    $psContext = (docker context show 2>$null | Out-String).Trim()
    Write-Host "  docker context (PowerShell): $psContext"

    $composeExtra = Get-Mail05aComposeExtra
    $failureKind = ''
    if (-not (Test-BashDockerSeesMailer -ComposeExtra $composeExtra -FailureKind ([ref]$failureKind))) {
        if ($failureKind -eq 'path') {
            $bashDir = Get-BashDeployDir
            throw @"
-RunSmoke preflight failed: bash cannot cd to the deploy directory.
PowerShell path: $DeployDir
Bash path tried: $bashDir
On Windows, use native Git Bash (not WSL bash.exe) with Docker Desktop, or run smoke from WSL after copying the repo under the Linux filesystem.
See docs/ops/local-deploy-rehearsal-runbook.md
"@
        }
        throw @"
-RunSmoke preflight failed: bash docker compose cannot see the running mailer container.
PowerShell docker context: $psContext
Use Git Bash linked to Docker Desktop (not WSL with a separate daemon), or run smoke manually after aligning contexts.
See docs/ops/local-deploy-rehearsal-runbook.md
"@
    }

    $drill = Join-Path $DeployDir 'drills\mail-05a-no-send-smoke.sh'
    if (-not (Test-Path -LiteralPath $drill)) {
        throw "Drill script not found: $drill"
    }

    $env:MAIL05A_COMPOSE_DIR = Get-BashDeployDir
    if ($composeExtra) {
        $env:MAIL05A_COMPOSE_EXTRA = $composeExtra
    }
    try {
        Push-Location $DeployDir
        & bash ./drills/mail-05a-no-send-smoke.sh
        if ($LASTEXITCODE -ne 0) { throw "MAIL-05a no-send smoke failed with exit code $LASTEXITCODE" }
    }
    finally {
        Pop-Location
        Remove-Item Env:MAIL05A_COMPOSE_DIR -ErrorAction SilentlyContinue
        Remove-Item Env:MAIL05A_COMPOSE_EXTRA -ErrorAction SilentlyContinue
    }
}

Write-Host ""
Write-Host "Local deploy rehearsal passed."
$teardownFiles = New-Object System.Collections.Generic.List[string]
for ($i = 0; $i -lt $ComposeFiles.Length; $i++) {
    if ($ComposeFiles[$i] -ne '-f') { continue }
    [void]$teardownFiles.Add("-f $([System.IO.Path]::GetFileName($ComposeFiles[$i + 1]))")
}
Write-Host "Tear down: docker compose --env-file .env $($teardownFiles -join ' ') down"
