<#
.SYNOPSIS
  Clean-state release smoke for the published Mailer image (issue #11, #53).

.DESCRIPTION
  Pulls ghcr.io/kooiei-in4a/amane-mailer:v0.1.1, starts Mailer + Mailpit from a
  clean compose project and named volume, and exercises the public release
  runtime path end to end:

    - GET  /healthz                        -> 200
    - GET  /readyz                         -> 200
    - POST /internal/mail-requests (ok)    -> 202 accepted
    - Mailpit receives the message
    - same id + same payload               -> 202 already_accepted
    - same id + different payload          -> 409 IDEMPOTENCY_CONFLICT
    - invalid token                        -> 401 UNAUTHORIZED_TENANT
    - unknown source_service               -> 403 SOURCE_SERVICE_NOT_ALLOWED

  Each check prints [PASS]/[FAIL] with the failing detail, and the compose
  project + volume are removed on exit (including on failure).

  Use this script on Windows with Docker Desktop so smoke runs against the same
  Docker CLI context as PowerShell (no WSL /var/run/docker.sock mismatch).

  Dependencies: PowerShell 5.1+, docker (with the compose plugin).

  Config via environment (all optional):
    MAILER_IMAGE_REPOSITORY  default ghcr.io/kooiei-in4a/amane-mailer
    MAILER_IMAGE_TAG         default v0.1.1
    MAILER_IMAGE_PLATFORM    default linux/amd64
    MAILER_PULL_POLICY       default always   (set "missing" to reuse a local image)
    MAILPIT_IMAGE            default axllent/mailpit:latest
    MAILER_HTTP_PORT         default 15280
    MAILPIT_HTTP_PORT        default 18025
    MAIL_SERVICE_TOKEN       default local-mail-service-token
    RELEASE_SMOKE_PROJECT    default amane-mailer-release-smoke
    RELEASE_SMOKE_KEEP       set to 1 to skip cleanup (debugging only)

.EXAMPLE
  .\scripts\release-smoke.ps1

.EXAMPLE
  $env:MAILER_IMAGE_TAG = 'sha-<git-sha>'; .\scripts\release-smoke.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ComposeFile = Join-Path $RepoRoot 'infra\docker\docker-compose.release-smoke.yml'

function Get-EnvOrDefault {
    param(
        [string]$Name,
        [string]$Default
    )
    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrEmpty($value)) { return $Default }
    return $value
}

$env:MAILER_IMAGE_REPOSITORY = Get-EnvOrDefault 'MAILER_IMAGE_REPOSITORY' 'ghcr.io/kooiei-in4a/amane-mailer'
$env:MAILER_IMAGE_TAG = Get-EnvOrDefault 'MAILER_IMAGE_TAG' 'v0.1.1'
$env:MAILER_IMAGE_PLATFORM = Get-EnvOrDefault 'MAILER_IMAGE_PLATFORM' 'linux/amd64'
$env:MAILER_PULL_POLICY = Get-EnvOrDefault 'MAILER_PULL_POLICY' 'always'
$env:MAILPIT_IMAGE = Get-EnvOrDefault 'MAILPIT_IMAGE' 'axllent/mailpit:latest'
$env:MAILER_HTTP_PORT = Get-EnvOrDefault 'MAILER_HTTP_PORT' '15280'
$env:MAILPIT_HTTP_PORT = Get-EnvOrDefault 'MAILPIT_HTTP_PORT' '18025'
$env:MAIL_SERVICE_TOKEN = Get-EnvOrDefault 'MAIL_SERVICE_TOKEN' 'local-mail-service-token'
$env:RELEASE_SMOKE_PROJECT = Get-EnvOrDefault 'RELEASE_SMOKE_PROJECT' 'amane-mailer-release-smoke'

$MailerUrl = "http://127.0.0.1:$($env:MAILER_HTTP_PORT)"
$MailpitUrl = "http://127.0.0.1:$($env:MAILPIT_HTTP_PORT)"
$ReleaseSmokeProject = $env:RELEASE_SMOKE_PROJECT

# Fixed example-tenant values from config/mailer/tenants.example.json.
$TENANT_ID = '00000000-0000-0000-0000-000000000101'
$SOURCE_SERVICE = 'example-service'
$TO_EMAIL = 'release-smoke@example.invalid'
$PURPOSE = 'ReleaseSmoke'
$TEXT_BODY = 'Amane release smoke. Mailpit delivery only.'
$SUBJECT_OK = 'Amane release smoke'
$SUBJECT_CONFLICT = 'Amane release smoke (conflict)'
$REQUEST_ID_OK = '00000000-0000-0000-0000-000000000201'
$REQUEST_ID_401 = '00000000-0000-0000-0000-000000000202'
$REQUEST_ID_403 = '00000000-0000-0000-0000-000000000203'

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

function Get-CanonicalPayload {
    param(
        [string]$Subject,
        [string]$SourceService
    )
    return ('{{"purpose":"{0}","source_service":"{1}","subject":"{2}","text_body":"{3}","to":[{{"email":"{4}"}}]}}' -f
        $PURPOSE, $SourceService, $Subject, $TEXT_BODY, $TO_EMAIL)
}

function Get-PayloadHash {
    param([string]$CanonicalJson)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($CanonicalJson)
    $hash = [System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes)
    return -join ($hash | ForEach-Object { $_.ToString('x2') })
}

function Get-RequestJson {
    param(
        [string]$MailRequestId,
        [string]$SourceService,
        [string]$Subject,
        [string]$PayloadHash
    )
    return ('{{"tenant_id":"{0}","source_service":"{1}","mail_request_id":"{2}","purpose":"{3}","to":[{{"email":"{4}"}}],"subject":"{5}","text_body":"{6}","payload_hash":"{7}"}}' -f
        $TENANT_ID, $SourceService, $MailRequestId, $PURPOSE, $TO_EMAIL, $Subject, $TEXT_BODY, $PayloadHash)
}

function Invoke-MailRequest {
    param(
        [string]$Token,
        [string]$Json
    )

    $headers = @{
        Authorization = "Bearer $Token"
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
            $stream = $webResponse.GetResponseStream()
            try {
                $reader = New-Object System.IO.StreamReader($stream)
                $script:RespBody = $reader.ReadToEnd()
            }
            finally {
                if ($null -ne $stream) { $stream.Dispose() }
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
            # Mailpit may not be ready yet.
        }
        Start-Sleep -Seconds 1
    }
    return $false
}

function Invoke-ReleaseCompose {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Args)
    & docker compose -f $ComposeFile @Args
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose failed: $($Args -join ' ')"
    }
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

function Invoke-DockerComposeQuiet {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Args)
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & docker compose -f $ComposeFile @Args 2>&1 | Out-Null
    }
    finally {
        $ErrorActionPreference = $prevEap
    }
}

function Invoke-Cleanup {
    if ($env:RELEASE_SMOKE_KEEP -eq '1') {
        Write-Log ''
        Write-Log "[cleanup] RELEASE_SMOKE_KEEP=1 set; leaving project '$ReleaseSmokeProject' running."
    }
    else {
        Write-Log ''
        Write-Log "[cleanup] removing compose project '$ReleaseSmokeProject' and its volume"
        Invoke-DockerComposeQuiet down -v --remove-orphans
    }
}

try {
    Write-Log '== Amane Mailer release smoke =='
    Write-Log "image:   $($env:MAILER_IMAGE_REPOSITORY):$($env:MAILER_IMAGE_TAG)"
    Write-Log "project: $ReleaseSmokeProject"
    Write-Log "mailer:  $MailerUrl"
    Write-Log "mailpit: $MailpitUrl"
    Write-Log ''

    Test-RequiredDeps

    Write-Log "[setup] removing any previous '$ReleaseSmokeProject' project"
    Invoke-DockerComposeQuiet down -v --remove-orphans

    Write-Log "[setup] starting Mailer + Mailpit (pull policy: $($env:MAILER_PULL_POLICY))"
    try {
        Invoke-ReleaseCompose up -d --wait
    }
    catch {
        Write-Fail 'compose up' 'Mailer/Mailpit did not become healthy; recent logs follow'
        & docker compose -f $ComposeFile ps
        & docker compose -f $ComposeFile logs --no-color --tail 60
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

    $canonOk = Get-CanonicalPayload -Subject $SUBJECT_OK -SourceService $SOURCE_SERVICE
    $hashOk = Get-PayloadHash -CanonicalJson $canonOk
    $jsonOk = Get-RequestJson -MailRequestId $REQUEST_ID_OK -SourceService $SOURCE_SERVICE -Subject $SUBJECT_OK -PayloadHash $hashOk
    Invoke-MailRequest -Token $env:MAIL_SERVICE_TOKEN -Json $jsonOk
    if ($script:HttpStatus -eq 202 -and $script:RespBody -match '"status"\s*:\s*"accepted"') {
        Write-Pass 'POST /internal/mail-requests -> 202 accepted'
    }
    else {
        Write-Fail 'POST /internal/mail-requests' "expected 202 accepted, got $($script:HttpStatus) body=$($script:RespBody)"
    }

    if (Test-MailpitReceivedSubject -Subject $SUBJECT_OK) {
        Write-Pass "Mailpit received '$SUBJECT_OK'"
    }
    else {
        Write-Fail 'Mailpit delivery' "message '$SUBJECT_OK' not found in Mailpit within timeout"
    }

    Invoke-MailRequest -Token $env:MAIL_SERVICE_TOKEN -Json $jsonOk
    if ($script:HttpStatus -eq 202 -and $script:RespBody -match '"status"\s*:\s*"already_accepted"') {
        Write-Pass 'Repost same id+payload -> 202 already_accepted'
    }
    else {
        Write-Fail 'Repost same id+payload' "expected 202 already_accepted, got $($script:HttpStatus) body=$($script:RespBody)"
    }

    $canonConflict = Get-CanonicalPayload -Subject $SUBJECT_CONFLICT -SourceService $SOURCE_SERVICE
    $hashConflict = Get-PayloadHash -CanonicalJson $canonConflict
    $jsonConflict = Get-RequestJson -MailRequestId $REQUEST_ID_OK -SourceService $SOURCE_SERVICE -Subject $SUBJECT_CONFLICT -PayloadHash $hashConflict
    Invoke-MailRequest -Token $env:MAIL_SERVICE_TOKEN -Json $jsonConflict
    if ($script:HttpStatus -eq 409 -and $script:RespBody -match 'IDEMPOTENCY_CONFLICT') {
        Write-Pass 'Repost same id+different payload -> 409 IDEMPOTENCY_CONFLICT'
    }
    else {
        Write-Fail 'Repost same id+different payload' "expected 409 IDEMPOTENCY_CONFLICT, got $($script:HttpStatus) body=$($script:RespBody)"
    }

    $json401 = Get-RequestJson -MailRequestId $REQUEST_ID_401 -SourceService $SOURCE_SERVICE -Subject $SUBJECT_OK -PayloadHash $hashOk
    Invoke-MailRequest -Token 'invalid-release-smoke-token' -Json $json401
    if ($script:HttpStatus -eq 401 -and $script:RespBody -match 'UNAUTHORIZED_TENANT') {
        Write-Pass 'Invalid token -> 401 UNAUTHORIZED_TENANT'
    }
    else {
        Write-Fail 'Invalid token' "expected 401 UNAUTHORIZED_TENANT, got $($script:HttpStatus) body=$($script:RespBody)"
    }

    $unknownService = 'unknown-service'
    $canon403 = Get-CanonicalPayload -Subject $SUBJECT_OK -SourceService $unknownService
    $hash403 = Get-PayloadHash -CanonicalJson $canon403
    $json403 = Get-RequestJson -MailRequestId $REQUEST_ID_403 -SourceService $unknownService -Subject $SUBJECT_OK -PayloadHash $hash403
    Invoke-MailRequest -Token $env:MAIL_SERVICE_TOKEN -Json $json403
    if ($script:HttpStatus -eq 403 -and $script:RespBody -match 'SOURCE_SERVICE_NOT_ALLOWED') {
        Write-Pass 'Unknown source_service -> 403 SOURCE_SERVICE_NOT_ALLOWED'
    }
    else {
        Write-Fail 'Unknown source_service' "expected 403 SOURCE_SERVICE_NOT_ALLOWED, got $($script:HttpStatus) body=$($script:RespBody)"
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
