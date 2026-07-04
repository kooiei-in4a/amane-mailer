<#
.SYNOPSIS
  Zero-Admin local first-mail smoke for PowerShell (issue #147).

.DESCRIPTION
  Builds and starts local Mailer + Mailpit from infra/docker/docker-compose.local.yml,
  then verifies health/readiness, one accepted POST, and Mailpit delivery.
  Narrower than scripts/release-smoke.ps1: no Admin UI, ACS, Dead Letter, or 401/403/409 checks.

  Use this script on Windows with Docker Desktop so smoke runs against the same
  Docker CLI context as PowerShell (no WSL /var/run/docker.sock mismatch).

  Dependencies: PowerShell 5.1+, docker (with the compose plugin).

  Config via environment (all optional):
    MAILER_HTTP_PORT       default 5280
    MAILPIT_HTTP_PORT      default 8025
    MAIL_SERVICE_TOKEN     default local-mail-service-token
    LOCAL_FIRST_MAIL_SMOKE_KEEP  reserved (compose is left running after the script exits)

.EXAMPLE
  .\scripts\local-first-mail-smoke.ps1

.EXAMPLE
  $env:MAILER_HTTP_PORT = '5280'; $env:MAILPIT_HTTP_PORT = '8025'; .\scripts\local-first-mail-smoke.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ComposeFile = Join-Path $RepoRoot 'infra\docker\docker-compose.local.yml'

function Get-EnvOrDefault {
    param(
        [string]$Name,
        [string]$Default
    )
    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrEmpty($value)) { return $Default }
    return $value
}

$env:MAILER_HTTP_PORT = Get-EnvOrDefault 'MAILER_HTTP_PORT' '5280'
$env:MAILPIT_HTTP_PORT = Get-EnvOrDefault 'MAILPIT_HTTP_PORT' '8025'
$env:MAIL_SERVICE_TOKEN = Get-EnvOrDefault 'MAIL_SERVICE_TOKEN' 'local-mail-service-token'

$MailerUrl = "http://127.0.0.1:$($env:MAILER_HTTP_PORT)"
$MailpitUrl = "http://127.0.0.1:$($env:MAILPIT_HTTP_PORT)"

$TENANT_ID = '00000000-0000-0000-0000-000000000101'
$SOURCE_SERVICE = 'example-service'
$PURPOSE = 'FormResponseNotification'
$TO_EMAIL = 'admin@example.com'
$SUBJECT = 'New response'
$TEXT_BODY = 'A new response arrived.'
# Matches docs/ops/first-mail-quickstart.md and README consumer quickstart example.
$PAYLOAD_HASH = '7c6d491cc70ac1b48fcc770d90ff80ae8a13c0e5ed3284fd1de9705d7e801ea9'

$script:PassCount = 0
$script:FailCount = 0
$script:ExitCode = 0
$script:HttpStatus = 0
$script:RespBody = ''

function Write-Log {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Message)
    Write-Host ($Message -join ' ')
}

function Write-Pass {
    param([string]$Message)
    $script:PassCount++
    Write-Host "[PASS] $Message"
}

function Write-Fail {
    param(
        [string]$Message,
        [string]$Detail
    )
    $script:FailCount++
    Write-Host "[FAIL] $Message -- $Detail"
}

function Test-RequiredDeps {
    $missing = New-Object System.Collections.Generic.List[string]
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        [void]$missing.Add('docker')
    }
    else {
        & docker compose version *> $null
        if ($LASTEXITCODE -ne 0) {
            Write-Log "[error] 'docker compose' plugin is not available"
            exit 2
        }
    }
    if ($missing.Count -gt 0) {
        Write-Log "[error] missing required tools: $($missing -join ', ')"
        exit 2
    }
}

function Invoke-LocalCompose {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$ComposeArgs)
    & docker compose -f $ComposeFile @ComposeArgs
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose failed: $($ComposeArgs -join ' ')"
    }
}

function Show-FailureContext {
    Write-Log ''
    Write-Log '[diagnostics] docker compose ps'
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & docker compose -f $ComposeFile ps
        Write-Log ''
        Write-Log '[diagnostics] mailer logs (tail 50)'
        & docker compose -f $ComposeFile logs mailer --no-color --tail 50
        Write-Log ''
        Write-Log '[diagnostics] mailpit logs (tail 30)'
        & docker compose -f $ComposeFile logs mailpit --no-color --tail 30
    }
    finally {
        $ErrorActionPreference = $prevEap
    }
}

function Invoke-MailRequest {
    param([string]$Json)

    $headers = @{
        Authorization = "Bearer $($env:MAIL_SERVICE_TOKEN)"
        'Content-Type' = 'application/json'
    }
    $uri = "$MailerUrl/internal/mail-requests"

    if ($PSVersionTable.PSVersion.Major -ge 7) {
        $response = Invoke-WebRequest -UseBasicParsing -Method Post `
            -Uri $uri `
            -Headers $headers `
            -Body $Json `
            -TimeoutSec 30 `
            -SkipHttpErrorCheck
        $script:HttpStatus = [int]$response.StatusCode
        $script:RespBody = $response.Content
        return
    }

    try {
        $response = Invoke-WebRequest -UseBasicParsing -Method Post `
            -Uri $uri `
            -Headers $headers `
            -Body $Json `
            -TimeoutSec 30
        $script:HttpStatus = [int]$response.StatusCode
        $script:RespBody = $response.Content
    }
    catch {
        $webResponse = $_.Exception.Response
        if ($null -ne $webResponse) {
            $script:HttpStatus = [int]$webResponse.StatusCode
            $reader = $null
            try {
                $reader = New-Object System.IO.StreamReader($webResponse.GetResponseStream())
                $script:RespBody = $reader.ReadToEnd()
            }
            finally {
                if ($null -ne $reader) { $reader.Dispose() }
            }
        }
        else {
            $script:HttpStatus = 0
            $script:RespBody = $_.Exception.Message
        }
    }
}

function Test-MailpitReceivedSubject {
    param([string]$Subject)
    for ($i = 1; $i -le 30; $i++) {
        try {
            $body = (Invoke-WebRequest -UseBasicParsing -Uri "$MailpitUrl/api/v1/messages" -TimeoutSec 15).Content
            if ($body.Contains($Subject)) {
                return $true
            }
        }
        catch {
            # Mailpit may not be ready yet.
        }
        Start-Sleep -Seconds 1
    }
    return $false
}

function Clear-MailpitInbox {
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($PSVersionTable.PSVersion.Major -ge 7) {
            Invoke-WebRequest -UseBasicParsing -Method Delete `
                -Uri "$MailpitUrl/api/v1/messages" `
                -TimeoutSec 15 `
                -SkipHttpErrorCheck | Out-Null
        }
        else {
            Invoke-WebRequest -UseBasicParsing -Method Delete `
                -Uri "$MailpitUrl/api/v1/messages" `
                -TimeoutSec 15 | Out-Null
        }
    }
    catch {
        # Inbox may already be empty or Mailpit not ready yet.
    }
    finally {
        $ErrorActionPreference = $prevEap
    }
}

function Finish-Smoke {
    Write-Log ''
    Write-Log "First-mail smoke: $($script:PassCount) passed, $($script:FailCount) failed"
    if ($script:FailCount -gt 0) {
        Show-FailureContext
        $script:ExitCode = 1
    }
}

try {
    Write-Log '== Amane Mailer local first-mail smoke =='
    Write-Log "compose: $ComposeFile"
    Write-Log "mailer:  $MailerUrl"
    Write-Log "mailpit: $MailpitUrl"
    Write-Log ''

    Test-RequiredDeps

    Write-Log '[setup] starting Mailer + Mailpit (build if needed)'
    try {
        Invoke-LocalCompose up -d --build --wait mailer
        Write-Pass 'compose up --wait mailer'
    }
    catch {
        Write-Fail 'compose up' 'Mailer did not become healthy'
        Finish-Smoke
        exit $script:ExitCode
    }

    Clear-MailpitInbox

    try {
        $healthBody = (Invoke-WebRequest -UseBasicParsing -Uri "$MailerUrl/healthz" -TimeoutSec 15).Content
        if ($healthBody -match '"healthy"\s*:\s*true') {
            Write-Pass 'GET /healthz -> healthy'
        }
        else {
            Write-Fail 'GET /healthz' "expected {{`"healthy`":true}} from $MailerUrl/healthz"
        }
    }
    catch {
        Write-Fail 'GET /healthz' "expected {{`"healthy`":true}} from $MailerUrl/healthz"
    }

    try {
        $readyBody = (Invoke-WebRequest -UseBasicParsing -Uri "$MailerUrl/readyz" -TimeoutSec 15).Content
        if ($readyBody -match '"ready"\s*:\s*true') {
            Write-Pass 'GET /readyz -> ready'
        }
        else {
            Write-Fail 'GET /readyz' "expected {{`"ready`":true}} from $MailerUrl/readyz"
        }
    }
    catch {
        Write-Fail 'GET /readyz' "expected {{`"ready`":true}} from $MailerUrl/readyz"
    }

    $requestId = [guid]::NewGuid().ToString()
    $json = ('{{"tenant_id":"{0}","source_service":"{1}","mail_request_id":"{2}","purpose":"{3}","to":[{{"email":"{4}"}}],"subject":"{5}","text_body":"{6}","payload_hash":"{7}"}}' -f
        $TENANT_ID, $SOURCE_SERVICE, $requestId, $PURPOSE, $TO_EMAIL, $SUBJECT, $TEXT_BODY, $PAYLOAD_HASH)

    Invoke-MailRequest -Json $json
    if ($script:HttpStatus -eq 202 -and $script:RespBody -match '"status"\s*:\s*"accepted"') {
        Write-Pass 'POST /internal/mail-requests -> 202 accepted'
    }
    else {
        Write-Fail 'POST /internal/mail-requests' "expected 202 accepted, got $($script:HttpStatus) body=$($script:RespBody)"
    }

    if (Test-MailpitReceivedSubject -Subject $SUBJECT) {
        Write-Pass "Mailpit received '$SUBJECT'"
    }
    else {
        Write-Fail 'Mailpit delivery' "message '$SUBJECT' not found in Mailpit within 30 seconds"
    }

    Finish-Smoke
}
catch {
    $script:ExitCode = 1
    Write-Log $_.Exception.Message
    Finish-Smoke
}

exit $script:ExitCode
