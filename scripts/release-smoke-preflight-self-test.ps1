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
    param(
        [string]$CurrentEndpoint = 'unix:///var/run/docker.sock',
        [hashtable]$ContextEndpoints = @{},
        [switch]$ComposeFails
    )

    $script:FakeBin = Join-Path ([System.IO.Path]::GetTempPath()) ('amane-mailer-fake-docker-' + [Guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $script:FakeBin -Force | Out-Null
    $script:FakeLog = Join-Path $script:FakeBin 'docker.log'
    [System.IO.File]::WriteAllText($script:FakeLog, '')

    $contextMapPath = Join-Path $script:FakeBin 'context-map'
    Set-Content -LiteralPath $contextMapPath -Value '' -Encoding Ascii
    foreach ($key in $ContextEndpoints.Keys) {
        Add-Content -LiteralPath $contextMapPath -Value ("{0}:{1}" -f $key, $ContextEndpoints[$key]) -Encoding Ascii
    }

    $composeExit = if ($ComposeFails) { '1' } else { '0' }
    $contextMapEscaped = ($contextMapPath -replace '\\', '/')

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
    exit $composeExit
    ;;
  context)
    if [[ "`${2:-}" == "inspect" ]]; then
      shift 2
      ctx_name=""
      while [[ `$# -gt 0 ]]; do
        case "`$1" in
          --format) shift 2 ;;
          *) ctx_name="`$1"; shift ;;
        esac
      done
      if [[ -n "`$ctx_name" ]]; then
        endpoint=`$(grep "^`${ctx_name}:" "$contextMapEscaped" 2>/dev/null | head -1 | cut -d: -f2- || true)
        if [[ -z "`$endpoint" ]]; then
          exit 1
        fi
      else
        endpoint="$CurrentEndpoint"
      fi
      printf '%s\n' "`$endpoint"
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
}

function Reset-ReleaseSmokeEnv {
    param(
        [string]$CurrentEndpoint = 'unix:///var/run/docker.sock',
        [hashtable]$ContextEndpoints = @{},
        [switch]$ComposeFails
    )

    Remove-Item Env:MAILER_IMAGE_DIGEST -ErrorAction SilentlyContinue
    Remove-Item Env:COMPOSE_PROJECT_NAME -ErrorAction SilentlyContinue
    Remove-Item Env:COMPOSE_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:DOCKER_HOST -ErrorAction SilentlyContinue
    Remove-Item Env:DOCKER_CONTEXT -ErrorAction SilentlyContinue
    $env:MAILER_IMAGE_TAG = 'v1.3.6'
    $env:RELEASE_SMOKE_PROJECT = 'amane-mailer-release-smoke'
    Initialize-FakeDocker -CurrentEndpoint $CurrentEndpoint -ContextEndpoints $ContextEndpoints -ComposeFails:$ComposeFails
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

function Test-ZeroComposeMutations {
    param([string]$Name)
    $logged = if (Test-Path -LiteralPath $script:FakeLog) {
        (Get-Content -LiteralPath $script:FakeLog -Raw -ErrorAction SilentlyContinue)
    }
    else {
        ''
    }
    if ([string]::IsNullOrWhiteSpace($logged)) {
        Write-Pass -Name $Name
    }
    else {
        Write-FailCase -Name $Name
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

    Reset-ReleaseSmokeEnv -CurrentEndpoint 'tcp://127.0.0.1:2375'
    Test-ExpectPreflightFail -Name 'N8 remote docker endpoint' -Action { Invoke-ReleaseSmokePreflight -RepoRoot $RepoRoot }

    Reset-ReleaseSmokeEnv -CurrentEndpoint 'tcp://127.0.0.1:2375'
    $env:RELEASE_SMOKE_SKIP_DOCKER_ENDPOINT_CHECK = '1'
    Test-ExpectPreflightFail -Name 'N9 legacy bypass env ignored' -Action { Invoke-ReleaseSmokePreflight -RepoRoot $RepoRoot }
    Test-ZeroComposeMutations -Name 'N9 legacy bypass zero docker compose mutations'

    Reset-ReleaseSmokeEnv -CurrentEndpoint 'unix:///var/run/docker.sock'
    $env:DOCKER_HOST = 'tcp://example.invalid:2376'
    Test-ExpectPreflightFail -Name 'D1 DOCKER_HOST remote with local context' -Action { Invoke-ReleaseSmokePreflight -RepoRoot $RepoRoot }
    Test-ZeroComposeMutations -Name 'D1 zero docker compose mutations'

    Reset-ReleaseSmokeEnv -CurrentEndpoint 'unix:///var/run/docker.sock' -ContextEndpoints @{ 'remote-context' = 'tcp://example.invalid:2376' }
    $env:DOCKER_CONTEXT = 'remote-context'
    $env:DOCKER_HOST = 'unix:///var/run/docker.sock'
    Test-ExpectPreflightFail -Name 'D2 DOCKER_CONTEXT remote overrides DOCKER_HOST local' -Action { Invoke-ReleaseSmokePreflight -RepoRoot $RepoRoot }

    Reset-ReleaseSmokeEnv -CurrentEndpoint 'tcp://example.invalid:2376' -ContextEndpoints @{ 'local-context' = 'unix:///var/run/docker.sock' }
    $env:DOCKER_CONTEXT = 'local-context'
    $env:DOCKER_HOST = 'tcp://example.invalid:2376'
    try {
        Invoke-ReleaseSmokePreflight -RepoRoot $RepoRoot
        Write-Pass -Name 'D3 DOCKER_CONTEXT local overrides DOCKER_HOST remote'
    }
    catch {
        Write-FailCase -Name 'D3 DOCKER_CONTEXT local overrides DOCKER_HOST remote'
    }

    Reset-ReleaseSmokeEnv -CurrentEndpoint 'ssh://example.invalid'
    Remove-Item Env:DOCKER_CONTEXT -ErrorAction SilentlyContinue
    Remove-Item Env:DOCKER_HOST -ErrorAction SilentlyContinue
    Test-ExpectPreflightFail -Name 'D4 current context remote' -Action { Invoke-ReleaseSmokePreflight -RepoRoot $RepoRoot }

    Reset-ReleaseSmokeEnv -CurrentEndpoint 'unix:///var/run/docker.sock'
    Remove-Item Env:DOCKER_CONTEXT -ErrorAction SilentlyContinue
    Remove-Item Env:DOCKER_HOST -ErrorAction SilentlyContinue
    try {
        Invoke-ReleaseSmokePreflight -RepoRoot $RepoRoot
        Write-Pass -Name 'D5 current context local'
    }
    catch {
        Write-FailCase -Name 'D5 current context local'
    }

    Reset-ReleaseSmokeEnv
    $env:MAILER_IMAGE_TAG = ' v1.3.6 '
    try {
        Invoke-ReleaseSmokePreflight -RepoRoot $RepoRoot
        if ($env:MAILER_IMAGE_REFERENCE -eq 'ghcr.io/kooiei-in4a/amane-mailer:v1.3.6') {
            Write-Pass -Name 'W1 tag whitespace trimmed in MAILER_IMAGE_REFERENCE'
        }
        else {
            Write-FailCase -Name "W1 tag whitespace trimmed (got $($env:MAILER_IMAGE_REFERENCE))"
        }
    }
    catch {
        Write-FailCase -Name 'W1 tag whitespace trimmed in MAILER_IMAGE_REFERENCE'
    }

    Reset-ReleaseSmokeEnv
    Remove-Item Env:MAILER_IMAGE_TAG -ErrorAction SilentlyContinue
    $env:MAILER_IMAGE_DIGEST = ' sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb '
    try {
        Invoke-ReleaseSmokePreflight -RepoRoot $RepoRoot
        if ($env:MAILER_IMAGE_REFERENCE -eq 'ghcr.io/kooiei-in4a/amane-mailer@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb') {
            Write-Pass -Name 'W2 digest whitespace trimmed in MAILER_IMAGE_REFERENCE'
        }
        else {
            Write-FailCase -Name "W2 digest whitespace trimmed (got $($env:MAILER_IMAGE_REFERENCE))"
        }
    }
    catch {
        Write-FailCase -Name 'W2 digest whitespace trimmed in MAILER_IMAGE_REFERENCE'
    }

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

    # E1: compose failure must not leak private paths or env values
    $canaryProject = 'amane-mailer-release-smoke-e1-canary'
    $canaryToken = 'canary-token-506-must-not-leak'
    $canaryComposeDir = Join-Path ([System.IO.Path]::GetTempPath()) ('amane-mailer-compose-canary-' + [Guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $canaryComposeDir -Force | Out-Null
    $canaryComposePath = Join-Path $canaryComposeDir 'docker-compose.release-smoke.yml'
    Set-Content -LiteralPath $canaryComposePath -Value 'services: {}' -Encoding Ascii

    Reset-ReleaseSmokeEnv -CurrentEndpoint 'tcp://example.invalid:2376' -ContextEndpoints @{ 'local-context' = 'unix:///var/run/docker.sock' } -ComposeFails
    $env:DOCKER_CONTEXT = 'local-context'
    $env:DOCKER_HOST = 'tcp://canary-docker-host.invalid:2376'
    $env:RELEASE_SMOKE_PROJECT = $canaryProject
    $env:MAIL_SERVICE_TOKEN = $canaryToken
    Invoke-ReleaseSmokePreflight -RepoRoot $RepoRoot -ComposeFilePath $canaryComposePath

    $composeError = $null
    try {
        Invoke-ReleaseSmokeCompose ps | Out-Null
    }
    catch {
        $composeError = $_.Exception.Message
    }

    if ($null -eq $composeError) {
        Write-FailCase -Name 'E1 compose failure raised exception'
    }
    elseif ($composeError -notmatch 'docker compose command failed') {
        Write-FailCase -Name 'E1 compose failure message is value-free generic'
    }
    elseif ($composeError -match [regex]::Escape($canaryComposePath) -or $composeError -match 'amane-mailer-release-smoke-e1-canary' -or $composeError -match 'canary-token-506' -or $composeError -match 'canary-docker-host') {
        Write-FailCase -Name 'E1 compose failure does not leak canary values'
    }
    else {
        Write-Pass -Name 'E1 compose failure is value-free'
    }

    Remove-Item -LiteralPath $canaryComposeDir -Recurse -Force -ErrorAction SilentlyContinue
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
