# Fixture self-test for release-smoke preflight (issue #506). No registry access required.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'lib\release-smoke-preflight.ps1')

$PassCount = 0
$FailCount = 0
$FakeBin = $null
$FakeLog = $null

function Write-Pass {
    param([string]$Name)
    Write-Host "[PASS] $Name"
    $script:PassCount++
}

function Write-FailCase {
    param([string]$Name)
    Write-Host "[FAIL] $Name" -ForegroundColor Red
    $script:FailCount++
}

function Initialize-FakeDocker {
    param([string]$Endpoint = 'unix:///var/run/docker.sock')

    $script:FakeBin = Join-Path ([System.IO.Path]::GetTempPath()) ('amane-mailer-fake-docker-' + [Guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $script:FakeBin -Force | Out-Null
    $script:FakeLog = Join-Path $script:FakeBin 'docker.log'
    Set-Content -LiteralPath $script:FakeLog -Value '' -Encoding Ascii

    $dockerScript = @"
#!/usr/bin/env bash
set -eu
case "`${1:-}" in
  compose)
    shift
    if [[ "`${1:-}" == "version" ]]; then
      exit 0
    fi
    printf '%s\n' "`$*" >> "$($script:FakeLog -replace '\\','/')"
    exit 0
    ;;
  context)
    if [[ "`${2:-}" == "inspect" ]]; then
      printf '%s\n' "$Endpoint"
      exit 0
    fi
    ;;
esac
exit 1
"@
    $dockerPath = Join-Path $script:FakeBin 'docker'
    Set-Content -LiteralPath $dockerPath -Value $dockerScript -Encoding Ascii
    if ($IsLinux -or $IsMacOS) {
        & chmod +x $dockerPath
    }
    $env:PATH = "$script:FakeBin" + [IO.Path]::PathSeparator + $env:PATH
    $env:RELEASE_SMOKE_SKIP_DOCKER_ENDPOINT_CHECK = '0'
}

function Reset-ReleaseSmokeEnv {
    Remove-Item Env:MAILER_IMAGE_DIGEST -ErrorAction SilentlyContinue
    Remove-Item Env:COMPOSE_PROJECT_NAME -ErrorAction SilentlyContinue
    Remove-Item Env:COMPOSE_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:DOCKER_HOST -ErrorAction SilentlyContinue
    $env:MAILER_IMAGE_TAG = 'v1.3.6'
    $env:RELEASE_SMOKE_PROJECT = 'amane-mailer-release-smoke'
    Initialize-FakeDocker
}

function Test-ExpectPreflightFail {
    param([string]$Name, [scriptblock]$Action)
    try {
        & $Action | Out-Null
        Write-FailCase -Name "expected failure: $Name"
    }
    catch {
        Write-Pass -Name $Name
    }
}

try {
    Reset-ReleaseSmokeEnv
    Remove-Item Env:MAILER_IMAGE_TAG -ErrorAction SilentlyContinue
    Test-ExpectPreflightFail -Name 'N1 missing artifact' -Action { Invoke-ReleaseSmokePreflight -RepoRoot $RepoRoot }

    Reset-ReleaseSmokeEnv
    $env:MAILER_IMAGE_TAG = 'latest'
    Test-ExpectPreflightFail -Name 'N2 latest tag rejected' -Action { Invoke-ReleaseSmokePreflight -RepoRoot $RepoRoot }

    Reset-ReleaseSmokeEnv
    $env:MAILER_IMAGE_TAG = 'v1.3.6'
    $env:MAILER_IMAGE_DIGEST = 'sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
    Test-ExpectPreflightFail -Name 'N3 tag and digest both supplied' -Action { Invoke-ReleaseSmokePreflight -RepoRoot $RepoRoot }

    Reset-ReleaseSmokeEnv
    Remove-Item Env:MAILER_IMAGE_TAG -ErrorAction SilentlyContinue
    $env:MAILER_IMAGE_DIGEST = 'sha256:NOTVALID'
    Test-ExpectPreflightFail -Name 'N4 malformed digest' -Action { Invoke-ReleaseSmokePreflight -RepoRoot $RepoRoot }

    Reset-ReleaseSmokeEnv
    $env:RELEASE_SMOKE_PROJECT = 'unrelated-canary'
    Test-ExpectPreflightFail -Name 'N5 invalid RELEASE_SMOKE_PROJECT' -Action { Invoke-ReleaseSmokePreflight -RepoRoot $RepoRoot }

    Reset-ReleaseSmokeEnv
    Initialize-FakeDocker -Endpoint 'tcp://127.0.0.1:2375'
    Test-ExpectPreflightFail -Name 'N8 remote docker endpoint' -Action { Invoke-ReleaseSmokePreflight -RepoRoot $RepoRoot }

    Reset-ReleaseSmokeEnv
    Invoke-ReleaseSmokePreflight -RepoRoot $RepoRoot
    Write-Pass -Name 'preflight success with explicit tag'

    if ($env:MAILER_IMAGE_REFERENCE -eq 'ghcr.io/kooiei-in4a/amane-mailer:v1.3.6') {
        Write-Pass -Name 'MAILER_IMAGE_REFERENCE resolved from tag'
    }
    else {
        Write-FailCase -Name "MAILER_IMAGE_REFERENCE resolved from tag (got $($env:MAILER_IMAGE_REFERENCE))"
    }

    $env:COMPOSE_PROJECT_NAME = 'unrelated-canary'
    $env:COMPOSE_FILE = 'unrelated-compose.yml'
    Invoke-ReleaseSmokeCompose ps | Out-Null
    $logged = Get-Content -LiteralPath $script:FakeLog -Raw
    if ($logged -match '-p amane-mailer-release-smoke' -and $logged -match 'docker-compose\.release-smoke\.yml' -and $logged -notmatch 'unrelated-canary' -and $logged -notmatch 'unrelated-compose\.yml') {
        Write-Pass -Name 'N6/N7 compose argv ignores COMPOSE_PROJECT_NAME and COMPOSE_FILE'
    }
    else {
        Write-FailCase -Name 'N6/N7 compose argv ignores COMPOSE_PROJECT_NAME and COMPOSE_FILE'
    }
}
finally {
    if ($null -ne $FakeBin -and (Test-Path -LiteralPath $FakeBin)) {
        Remove-Item -LiteralPath $FakeBin -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($FailCount -ne 0) {
    Write-Host "[info] release-smoke-preflight-self-test.ps1 failures: $FailCount (passes=$PassCount)" -ForegroundColor Red
    exit 1
}

Write-Host "[info] release-smoke-preflight-self-test.ps1 passed (passes=$PassCount fails=$FailCount)"
