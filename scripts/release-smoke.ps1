<#
.SYNOPSIS
  Clean-state release smoke for the published Mailer image (issue #11, #506).

.DESCRIPTION
  Pulls an explicitly supplied Mailer release artifact, starts Mailer + Mailpit from a
  clean compose project and named volume, and exercises the public release
  runtime path end to end.

  Each check prints [PASS]/[FAIL] with the failing detail, and the compose
  project + volume are removed on exit (including on failure).

  Operational canonical release verification runs on Linux local Docker only
  (scripts/release-smoke.sh).

  This PowerShell script mirrors the shell contract for parity. It is not a
  supported release gate platform: Windows Docker Desktop live smoke is out of
  scope. Validate the contract on Linux via release-smoke-preflight-self-test.ps1
  and release-client-self-test.ps1.

  Dependencies: PowerShell 5.1+, docker (with the compose plugin).

  Required environment (exactly one image selector):
    MAILER_IMAGE_TAG         e.g. v1.3.6 or sha-<40hex>
    MAILER_IMAGE_DIGEST      e.g. sha256:<64-lowercase-hex>

  Required authentication:
    MAILER_API_KEY           managed API key for a Sender already provisioned
                             in the target data volume (do not bootstrap here)

  Optional environment:
    MAILER_IMAGE_REPOSITORY  default ghcr.io/kooiei-in4a/amane-mailer
    MAILER_IMAGE_PLATFORM    default linux/amd64
    MAILER_PULL_POLICY       default always
    MAILPIT_IMAGE            default axllent/mailpit:latest
    MAILER_HTTP_PORT         default 15280
    MAILPIT_HTTP_PORT        default 18025
    RELEASE_SMOKE_PROJECT    default amane-mailer-release-smoke
    RELEASE_SMOKE_KEEP       set to 1 to skip cleanup (debugging only)

.EXAMPLE
  $env:MAILER_IMAGE_TAG = 'v1.3.6'; $env:MAILER_API_KEY = '<managed-key>'; .\scripts\release-smoke.ps1

.EXAMPLE
  $env:MAILER_IMAGE_DIGEST = 'sha256:<digest>'; $env:MAILER_API_KEY = '<managed-key>'; .\scripts\release-smoke.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ComposeFile = Join-Path $RepoRoot 'infra\docker\docker-compose.release-smoke.yml'
. (Join-Path $PSScriptRoot 'lib\release-smoke-preflight.ps1')

function Get-EnvOrDefault {
    param(
        [string]$Name,
        [string]$Default
    )
    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrEmpty($value)) { return $Default }
    return $value
}

$env:MAILER_IMAGE_PLATFORM = Get-EnvOrDefault 'MAILER_IMAGE_PLATFORM' 'linux/amd64'
$env:MAILER_PULL_POLICY = Get-EnvOrDefault 'MAILER_PULL_POLICY' 'always'
$env:MAILPIT_IMAGE = Get-EnvOrDefault 'MAILPIT_IMAGE' 'axllent/mailpit:latest'
$env:MAILER_HTTP_PORT = Get-EnvOrDefault 'MAILER_HTTP_PORT' '15280'
$env:MAILPIT_HTTP_PORT = Get-EnvOrDefault 'MAILPIT_HTTP_PORT' '18025'

if ([string]::IsNullOrEmpty($env:MAILER_API_KEY)) {
    Write-Host '[error] MAILER_API_KEY is required.' -ForegroundColor Red
    Write-Host 'Provide a managed API key for the Sender used by this smoke test.' -ForegroundColor Red
    exit 2
}

try {
    Invoke-ReleaseSmokePreflight -RepoRoot $RepoRoot -ComposeFilePath $ComposeFile
}
catch {
    Write-Host "[error] $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}

$MailerUrl = "http://127.0.0.1:$($env:MAILER_HTTP_PORT)"
$MailpitUrl = "http://127.0.0.1:$($env:MAILPIT_HTTP_PORT)"

$TO_EMAIL = 'release-smoke@example.invalid'
$PURPOSE = 'ReleaseSmoke'
$TEXT_BODY = 'Amane release smoke. Mailpit delivery only.'
$SUBJECT_OK = 'Amane release smoke'
$SUBJECT_CONFLICT = 'Amane release smoke (conflict)'
$REQUEST_ID_OK = '00000000-0000-0000-0000-000000000201'
$REQUEST_ID_401 = '00000000-0000-0000-0000-000000000202'

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

function Get-RequestJson {
    param(
        [string]$MailRequestId,
        [string]$Subject
    )
    return ('{{"mail_request_id":"{0}","purpose":"{1}","to":[{{"email":"{2}"}}],"subject":"{3}","text_body":"{4}"}}' -f
        $MailRequestId, $PURPOSE, $TO_EMAIL, $Subject, $TEXT_BODY)
}

function Invoke-MailRequest {
    param(
        [string]$ApiKey,
        [string]$Json
    )

    $headers = @{
        Authorization = "Bearer $ApiKey"
        'Content-Type' = 'application/json'
    }
    $uri = "$MailerUrl/api/mail-requests"

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

function Get-HttpStatus {
    param([string]$Path)
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri "$MailerUrl$Path" -TimeoutSec 15
        return [string]$response.StatusCode
    }
    catch {
        return '000'
    }
}

function Wait-ForHttp {
    param([string]$Path)
    for ($i = 1; $i -le 30; $i++) {
        if ((Get-HttpStatus -Path $Path) -eq '200') {
            return $true
        }
        Start-Sleep -Seconds 2
    }
    return $false
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
        }
        Start-Sleep -Seconds 1
    }
    return $false
}

function Remove-ReleaseSmokeVolumeIfPresent {
    $volumeName = "${script:ReleaseSmokeProject}_mailer-data"
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & docker volume inspect $volumeName 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { return }
        & docker volume rm $volumeName 2>&1 | Out-Null
    }
    finally {
        $ErrorActionPreference = $prevEap
    }
}

function Invoke-ReleaseSmokeTeardown {
    Invoke-ReleaseSmokeComposeQuiet down -v --remove-orphans
    Remove-ReleaseSmokeVolumeIfPresent
}

function Invoke-Cleanup {
    if ($env:RELEASE_SMOKE_KEEP -eq '1') {
        Write-Log ''
        Write-Log "[cleanup] RELEASE_SMOKE_KEEP=1 set; leaving project '$($script:ReleaseSmokeProject)' running."
    }
    else {
        Write-Log ''
        Write-Log "[cleanup] removing compose project '$($script:ReleaseSmokeProject)' and its volume"
        Invoke-ReleaseSmokeTeardown
    }
}

try {
    Write-Log '== Amane Mailer release smoke =='
    Write-Log "image:   $($env:MAILER_IMAGE_REFERENCE)"
    Write-Log "project: $($script:ReleaseSmokeProject)"
    Write-Log "mailer:  $MailerUrl"
    Write-Log "mailpit: $MailpitUrl"
    Write-Log ''

    Write-Log "[setup] removing any previous '$($script:ReleaseSmokeProject)' project"
    Invoke-ReleaseSmokeTeardown

    Write-Log "[setup] starting Mailer + Mailpit (pull policy: $($env:MAILER_PULL_POLICY))"
    try {
        Invoke-ReleaseSmokeCompose up -d --wait
    }
    catch {
        Write-Fail 'compose up' 'Mailer/Mailpit did not become healthy; recent logs follow'
        Invoke-ReleaseSmokeCompose ps
        Invoke-ReleaseSmokeCompose logs --no-color --tail 60
        Write-Log ''
        Write-Log "Smoke result: 0 passed, $($script:FailCount) failed"
        $script:ExitCode = 1
        return
    }

    if (Wait-ForHttp -Path '/healthz') {
        Write-Pass 'GET /healthz -> 200'
    }
    else {
        Write-Fail 'GET /healthz' "no 200 from $MailerUrl/healthz within timeout"
    }

    if (Wait-ForHttp -Path '/readyz') {
        Write-Pass 'GET /readyz -> 200'
    }
    else {
        Write-Fail 'GET /readyz' "no 200 from $MailerUrl/readyz within timeout"
    }

    $jsonOk = Get-RequestJson -MailRequestId $REQUEST_ID_OK -Subject $SUBJECT_OK
    Invoke-MailRequest -ApiKey $env:MAILER_API_KEY -Json $jsonOk
    if ($script:HttpStatus -eq 202 -and $script:RespBody -match '"status"\s*:\s*"accepted"') {
        Write-Pass 'POST /api/mail-requests -> 202 accepted'
    }
    else {
        Write-Fail 'POST /api/mail-requests' "expected 202 accepted, got $($script:HttpStatus) body=$($script:RespBody)"
    }

    if (Test-MailpitReceivedSubject -Subject $SUBJECT_OK) {
        Write-Pass "Mailpit received '$SUBJECT_OK'"
    }
    else {
        Write-Fail 'Mailpit delivery' "message '$SUBJECT_OK' not found in Mailpit within timeout"
    }

    Invoke-MailRequest -ApiKey $env:MAILER_API_KEY -Json $jsonOk
    if ($script:HttpStatus -eq 202 -and $script:RespBody -match '"status"\s*:\s*"already_accepted"') {
        Write-Pass 'Repost same id+payload -> 202 already_accepted'
    }
    else {
        Write-Fail 'Repost same id+payload' "expected 202 already_accepted, got $($script:HttpStatus) body=$($script:RespBody)"
    }

    $jsonConflict = Get-RequestJson -MailRequestId $REQUEST_ID_OK -Subject $SUBJECT_CONFLICT
    Invoke-MailRequest -ApiKey $env:MAILER_API_KEY -Json $jsonConflict
    if ($script:HttpStatus -eq 409 -and $script:RespBody -match 'IDEMPOTENCY_CONFLICT') {
        Write-Pass 'Repost same id+different payload -> 409 IDEMPOTENCY_CONFLICT'
    }
    else {
        Write-Fail 'Repost same id+different payload' "expected 409 IDEMPOTENCY_CONFLICT, got $($script:HttpStatus) body=$($script:RespBody)"
    }

    $json401 = Get-RequestJson -MailRequestId $REQUEST_ID_401 -Subject $SUBJECT_OK
    Invoke-MailRequest -ApiKey 'invalid-release-smoke-api-key' -Json $json401
    if ($script:HttpStatus -eq 401 -and $script:RespBody -match 'UNAUTHORIZED') {
        Write-Pass 'Invalid API key -> 401 UNAUTHORIZED'
    }
    else {
        Write-Fail 'Invalid API key' "expected 401 UNAUTHORIZED, got $($script:HttpStatus) body=$($script:RespBody)"
    }

    Write-Log ''
    Write-Log "Smoke result: $($script:PassCount) passed, $($script:FailCount) failed"
    if ($script:FailCount -gt 0) {
        $script:ExitCode = 1
    }
}
catch {
    $script:ExitCode = 1
    Write-Log $_.Exception.Message
}
finally {
    Invoke-Cleanup
}

exit $script:ExitCode
