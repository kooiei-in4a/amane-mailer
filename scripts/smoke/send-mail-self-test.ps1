<#
.SYNOPSIS
  Runs the official PowerShell smoke client against a local no-send HTTP fixture.

.DESCRIPTION
  This is a contract self-test. It never contacts Mailer, ACS, Mailpit, or a
  remote host. The fixture records only temporary test data and the test removes
  it before returning.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$ClientPath = Join-Path $RepoRoot 'scripts\smoke\send-mail.ps1'
$TempRoot = Join-Path ([IO.Path]::GetTempPath()) ("amane-mailer-smoke-" + [guid]::NewGuid().ToString('N'))
$LogPath = Join-Path $TempRoot 'requests.jsonl'
$StopPath = Join-Path $TempRoot 'stop.fixture'
$ReadyPath = Join-Path $TempRoot 'fixture.ready'
$StdoutPath = Join-Path $TempRoot 'client.stdout'
$StderrPath = Join-Path $TempRoot 'client.stderr'
$Secret = 'amk_fixture.secret-must-not-be-printed'
$Recipient = 'recipient-canary@example.invalid'
$Subject = 'subject-canary-must-not-be-printed'
$TextBody = 'body-canary-must-not-be-printed'
$script:ServerJob = $null
$script:OldEnvironment = @{}

function Assert-Condition {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (-not $Condition) {
        throw $Message
    }
}

function Save-EnvironmentValue {
    param([Parameter(Mandatory = $true)][string]$Name)
    $script:OldEnvironment[$Name] = [Environment]::GetEnvironmentVariable($Name)
}

function Restore-Environment {
    foreach ($name in $script:OldEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $script:OldEnvironment[$name], 'Process')
    }
}

function Get-FreeLocalPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try {
        return ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Start-Fixture {
    param(
        [Parameter(Mandatory = $true)][int]$Port,
        [Parameter(Mandatory = $true)][string]$FixtureStopPath,
        [Parameter(Mandatory = $true)][string]$FixtureReadyPath
    )

    $script:ServerJob = Start-Job -ScriptBlock {
        param($FixturePort, $FixtureLogPath, $FixtureStopPath, $FixtureReadyPath)

        $listener = New-Object System.Net.HttpListener
        $listener.Prefixes.Add("http://127.0.0.1:$FixturePort/")
        $listener.Start()
        New-Item -ItemType File -Path $FixtureReadyPath -Force | Out-Null
        $postCount = 0
        $successPollCount = 0

        try {
            while ($true) {
                if (Test-Path -LiteralPath $FixtureStopPath) {
                    break
                }
                $contextTask = $listener.GetContextAsync()
                if (-not $contextTask.Wait(500)) {
                    continue
                }
                $context = $contextTask.Result
                $request = $context.Request
                $reader = $null
                try {
                    $reader = New-Object System.IO.StreamReader($request.InputStream, [Text.Encoding]::UTF8)
                    $requestBody = $reader.ReadToEnd()
                }
                finally {
                    if ($null -ne $reader) {
                        $reader.Dispose()
                    }
                }

                $method = $request.HttpMethod
                $path = $request.Url.AbsolutePath
                $authorization = $request.Headers['Authorization']
                $record = [ordered]@{
                    method = $method
                    path = $path
                    authorization = $authorization
                    body = $requestBody
                }
                Add-Content -LiteralPath $FixtureLogPath -Value ($record | ConvertTo-Json -Compress -Depth 5)

                if ($method -eq 'POST') {
                    $postCount++
                    $requestPayload = $requestBody | ConvertFrom-Json
                    $responseStatus = 202
                    $responseBody = @{ mail_request_id = $requestPayload.mail_request_id; status = 'accepted' } | ConvertTo-Json -Compress
                }
                elseif ($method -eq 'GET') {
                    $requestId = $path.Substring($path.LastIndexOf('/') + 1)
                    if ($postCount -eq 1) {
                        $successPollCount++
                        $deliveryStatus = if ($successPollCount -eq 1) { 'queued' } else { 'delivered' }
                    }
                    else {
                        $deliveryStatus = 'queued'
                    }
                    $responseStatus = 200
                    $responseBody = @{
                        mail_request_id = $requestId
                        status = $deliveryStatus
                        attempt_count = 1
                        max_attempts = 5
                        accepted_at = '2026-09-05T00:00:00Z'
                    } | ConvertTo-Json -Compress
                }
                else {
                    $responseStatus = 404
                    $responseBody = @{ code = 'NOT_FOUND' } | ConvertTo-Json -Compress
                }

                $responseBytes = [Text.Encoding]::UTF8.GetBytes($responseBody)
                $context.Response.StatusCode = $responseStatus
                $context.Response.ContentType = 'application/json'
                $context.Response.ContentLength64 = $responseBytes.Length
                $context.Response.OutputStream.Write($responseBytes, 0, $responseBytes.Length)
                $context.Response.Close()
            }
        }
        finally {
            $listener.Stop()
            $listener.Close()
        }
    } -ArgumentList $Port, $LogPath, $FixtureStopPath, $FixtureReadyPath

    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if (Test-Path -LiteralPath $FixtureReadyPath) {
            Start-Sleep -Milliseconds 100
            return
        }
        if ($script:ServerJob.State -in @('Failed', 'Stopped', 'Completed')) {
            throw 'local fixture failed to start.'
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'local fixture did not start within the self-test timeout.'
}

function Invoke-ClientProcess {
    param(
        [Parameter(Mandatory = $true)][string]$PollTimeout,
        [Parameter(Mandatory = $true)][string]$ExpectedExitCode
    )

    $engine = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($null -eq $engine) {
        $engine = Get-Command powershell.exe -ErrorAction SilentlyContinue
    }
    Assert-Condition ($null -ne $engine) 'PowerShell executable is required for the self-test.'

    $arguments = @(
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        $ClientPath,
        '-PollTimeoutSeconds',
        $PollTimeout,
        '-PollIntervalSeconds',
        '0'
    )
    $process = Start-Process -FilePath $engine.Source `
        -ArgumentList $arguments `
        -RedirectStandardOutput $StdoutPath `
        -RedirectStandardError $StderrPath `
        -PassThru `
        -Wait
    Assert-Condition ($process.ExitCode -eq [int]$ExpectedExitCode) "client returned an unexpected exit code for poll timeout $PollTimeout."

    $output = ''
    if (Test-Path -LiteralPath $StdoutPath) {
        $output += Get-Content -LiteralPath $StdoutPath -Raw
    }
    if (Test-Path -LiteralPath $StderrPath) {
        $output += Get-Content -LiteralPath $StderrPath -Raw
    }
    Assert-Condition (-not $output.Contains($Secret)) 'client output exposed the API key.'
    Assert-Condition (-not $output.Contains($Recipient)) 'client output exposed the recipient.'
    Assert-Condition (-not $output.Contains($Subject)) 'client output exposed the subject.'
    Assert-Condition (-not $output.Contains($TextBody)) 'client output exposed the body.'
    return $output
}

try {
    Assert-Condition (Test-Path -LiteralPath $ClientPath) 'official PowerShell smoke client is missing.'
    $source = Get-Content -LiteralPath $ClientPath -Raw
    Assert-Condition ($source.Contains('Read-Host')) 'secure prompt fallback is missing.'
    Assert-Condition ($source.Contains('AsSecureString')) 'secure prompt must be echo-free.'
    Assert-Condition ($source.Contains('[guid]::NewGuid()')) 'random request ID generation is missing.'
    Assert-Condition ($source.Contains('MaximumRedirection = 0')) 'redirect refusal is missing.'
    Assert-Condition ($source.Contains('delivery_unknown')) 'delivery_unknown terminal handling is missing.'
    $parameterSection = $source.Substring(0, $source.IndexOf('Set-StrictMode'))
    Assert-Condition (-not ($parameterSection -match '(?im)^\s*\[string\]\$ApiKey\s*$')) 'API key must not be a top-level CLI parameter.'

    New-Item -ItemType Directory -Path $TempRoot -Force | Out-Null
    $port = Get-FreeLocalPort
    foreach ($name in @(
        'MAILER_BASE_URL',
        'MAILER_API_KEY',
        'MAILER_RECIPIENT_EMAIL',
        'MAILER_SUBJECT',
        'MAILER_TEXT_BODY',
        'MAILER_POLL_TIMEOUT_SECONDS',
        'MAILER_POLL_INTERVAL_SECONDS'
    )) {
        Save-EnvironmentValue -Name $name
    }
    $env:MAILER_BASE_URL = "http://127.0.0.1:$port/"
    $env:MAILER_API_KEY = $Secret
    $env:MAILER_RECIPIENT_EMAIL = $Recipient
    $env:MAILER_SUBJECT = $Subject
    $env:MAILER_TEXT_BODY = $TextBody
    Remove-Item -LiteralPath $LogPath -Force -ErrorAction SilentlyContinue
    Start-Fixture -Port $port -FixtureStopPath $StopPath -FixtureReadyPath $ReadyPath

    $successOutput = Invoke-ClientProcess -PollTimeout '2' -ExpectedExitCode '0'
    Assert-Condition ($successOutput.Contains('HTTP 202 Accepted')) 'client did not report acceptance.'
    Assert-Condition ($successOutput.Contains('Delivery status: delivered')) 'client did not report delivery.'
    $timeoutOutput = Invoke-ClientProcess -PollTimeout '0.05' -ExpectedExitCode '1'
    Assert-Condition ($timeoutOutput.Contains('Status polling timed out')) 'client did not report the bounded timeout.'

    $records = @(Get-Content -LiteralPath $LogPath | ForEach-Object { $_ | ConvertFrom-Json })
    Assert-Condition ($records.Count -ge 5) 'fixture did not receive both POST requests and bounded GET polls.'
    $firstPost = $records | Where-Object { $_.method -eq 'POST' } | Select-Object -First 1
    Assert-Condition ($null -ne $firstPost) 'fixture did not receive a POST.'
    Assert-Condition ($firstPost.authorization -eq "Bearer $Secret") 'client did not send the managed API key as a Bearer header.'
    $payload = $firstPost.body | ConvertFrom-Json
    $fieldNames = @($payload.PSObject.Properties.Name)
    Assert-Condition ($fieldNames.Count -eq 5) 'PowerShell client sent an unexpected number of request fields.'
    foreach ($field in @('mail_request_id', 'purpose', 'to', 'subject', 'text_body')) {
        Assert-Condition ($fieldNames -contains $field) "PowerShell client omitted v2 field $field."
    }
    foreach ($legacyField in @('tenant_id', 'source_service', 'payload_hash')) {
        Assert-Condition (-not ($fieldNames -contains $legacyField)) "PowerShell client sent legacy field $legacyField."
    }
    $requestId = [guid]::Empty
    Assert-Condition ([guid]::TryParse([string]$payload.mail_request_id, [ref]$requestId)) 'PowerShell client did not generate a UUID request ID.'
    $getRecords = @($records | Where-Object { $_.method -eq 'GET' })
    Assert-Condition ($getRecords.Count -ge 3) 'PowerShell client did not poll status.'
    Assert-Condition (($getRecords | Where-Object { $_.path.EndsWith([string]$requestId) }).Count -ge 2) 'status polling did not use the generated request ID.'

    Write-Host 'send-mail.ps1 self-test: PASS'
    exit 0
}
catch {
    Write-Host "send-mail.ps1 self-test: FAIL -- $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    if ($null -ne $script:ServerJob) {
        New-Item -ItemType File -Path $StopPath -Force | Out-Null
        Stop-Job -Job $script:ServerJob -ErrorAction SilentlyContinue
        Remove-Job -Job $script:ServerJob -Force -ErrorAction SilentlyContinue
    }
    Restore-Environment
    if (Test-Path -LiteralPath $TempRoot) {
        Remove-Item -LiteralPath $TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
