<#
.SYNOPSIS
  Official PowerShell smoke client for the Amane Mailer v2 Consumer API.

.DESCRIPTION
  Sends one explicit v2 mail request and polls its delivery status until a
  contract terminal state or a bounded timeout. The managed API key is read
  from MAILER_API_KEY or an echo-free secure prompt; it is intentionally not a
  command-line parameter.

  Dependencies: Windows PowerShell 5.1+ or PowerShell 7+.

  Required input:
    -Recipient or MAILER_RECIPIENT_EMAIL

  Optional input:
    -BaseUrl / MAILER_BASE_URL (default http://127.0.0.1:5280/)
    -Subject / MAILER_SUBJECT
    -Body / MAILER_TEXT_BODY
    -Purpose / MAILER_PURPOSE
    -RequestId (random UUID when omitted)
    -TimeoutSeconds / MAILER_TIMEOUT_SECONDS
    -PollTimeoutSeconds / MAILER_POLL_TIMEOUT_SECONDS
    -PollIntervalSeconds / MAILER_POLL_INTERVAL_SECONDS
#>
[CmdletBinding()]
param(
    [string]$BaseUrl,
    [string]$Recipient,
    [string]$Subject,
    [string]$Body,
    [string]$Purpose,
    [string]$RequestId,
    [string]$TimeoutSeconds,
    [string]$PollTimeoutSeconds,
    [string]$PollIntervalSeconds
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$DefaultBaseUrl = 'http://127.0.0.1:5280/'
$DefaultPurpose = 'SmokeTest'
$DefaultSubject = 'Amane Mailer smoke'
$DefaultBody = 'Amane Mailer smoke request.'
$DefaultTimeoutSeconds = 10.0
$DefaultPollTimeoutSeconds = 30.0
$DefaultPollIntervalSeconds = 1.0
$TerminalStatuses = @('delivered', 'failed', 'dead_lettered', 'cancelled', 'delivery_unknown')
$NonTerminalStatuses = @('queued', 'processing')

function Get-ValueOrEnvironment {
    param(
        [AllowNull()]
        [string]$Value,
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [AllowNull()]
        [string]$Default
    )

    if (-not [string]::IsNullOrWhiteSpace($Value)) {
        return $Value
    }
    $environmentValue = [Environment]::GetEnvironmentVariable($Name)
    if (-not [string]::IsNullOrWhiteSpace($environmentValue)) {
        return $environmentValue
    }
    return $Default
}

function Convert-ToFiniteSeconds {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value,
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [bool]$AllowZero
    )

    try {
        $parsed = [double]::Parse(
            $Value,
            [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        throw "$Name must be a number."
    }

    if ([double]::IsNaN($parsed) -or [double]::IsInfinity($parsed)) {
        throw "$Name must be a finite number."
    }
    if ($parsed -lt 0 -or ($parsed -eq 0 -and -not $AllowZero)) {
        $qualifier = if ($AllowZero) { 'zero or greater' } else { 'greater than zero' }
        throw "$Name must be $qualifier."
    }
    return $parsed
}

function Resolve-BaseUri {
    param([Parameter(Mandatory = $true)][string]$Value)

    try {
        $uri = [Uri]$Value
    }
    catch {
        throw 'MAILER_BASE_URL is not a valid URL.'
    }

    if (-not $uri.IsAbsoluteUri -or $uri.Scheme -notin @('http', 'https') -or [string]::IsNullOrWhiteSpace($uri.Host)) {
        throw 'MAILER_BASE_URL must use an http or https URL.'
    }
    if (-not [string]::IsNullOrEmpty($uri.UserInfo) -or -not [string]::IsNullOrEmpty($uri.Query) -or -not [string]::IsNullOrEmpty($uri.Fragment)) {
        throw 'MAILER_BASE_URL must not contain credentials, a query, or a fragment.'
    }

    return ([Uri]($uri.AbsoluteUri.TrimEnd('/') + '/'))
}

function Get-ApiKey {
    $apiKey = [Environment]::GetEnvironmentVariable('MAILER_API_KEY')
    if (-not [string]::IsNullOrEmpty($apiKey)) {
        return $apiKey
    }

    if ([Console]::IsInputRedirected) {
        throw 'MAILER_API_KEY is not set; set it for non-interactive use or run from a terminal.'
    }

    $secureKey = $null
    $keyPointer = [IntPtr]::Zero
    try {
        $secureKey = Read-Host 'Mailer API key (input hidden)' -AsSecureString
        if ($null -eq $secureKey) {
            throw 'A managed API key is required.'
        }
        $keyPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
        $apiKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($keyPointer)
    }
    catch [System.Management.Automation.PipelineStoppedException] {
        throw 'API key prompt was cancelled.'
    }
    catch {
        if ($_.Exception.Message -eq 'A managed API key is required.') {
            throw
        }
        throw 'API key prompt failed.'
    }
    finally {
        if ($keyPointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($keyPointer)
        }
        if ($null -ne $secureKey) {
            $secureKey.Dispose()
        }
    }

    if ([string]::IsNullOrEmpty($apiKey)) {
        throw 'A managed API key is required.'
    }
    return $apiKey
}

function Get-RequestId {
    param([AllowNull()][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return [guid]::NewGuid().ToString('D')
    }

    $parsed = [guid]::Empty
    if (-not [guid]::TryParse($Value, [ref]$parsed)) {
        throw '-RequestId must be a UUID.'
    }
    return $parsed.ToString('D')
}

function Get-HttpTimeoutSeconds {
    param([Parameter(Mandatory = $true)][double]$Seconds)
    return [Math]::Max(1, [int][Math]::Ceiling($Seconds))
}

function Invoke-MailerHttp {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('GET', 'POST')]
        [string]$Method,
        [Parameter(Mandatory = $true)]
        [Uri]$Uri,
        [Parameter(Mandatory = $true)]
        [string]$ApiKey,
        [AllowNull()]
        [string]$JsonBody,
        [Parameter(Mandatory = $true)]
        [int]$TimeoutSec
    )

    $headers = @{
        Authorization = "Bearer $ApiKey"
        Accept = 'application/json'
    }
    $invokeArgs = @{
        UseBasicParsing = $true
        Method = $Method
        Uri = $Uri.AbsoluteUri
        Headers = $headers
        TimeoutSec = $TimeoutSec
        MaximumRedirection = 0
        ErrorAction = 'Stop'
    }
    if ($Method -eq 'POST') {
        $invokeArgs.Body = $JsonBody
        $invokeArgs.ContentType = 'application/json'
    }

    if ($PSVersionTable.PSVersion.Major -ge 7) {
        try {
            $response = Invoke-WebRequest @invokeArgs -SkipHttpErrorCheck
            return [pscustomobject]@{
                StatusCode = [int]$response.StatusCode
                Body = [string]$response.Content
            }
        }
        catch {
            throw 'Mailer request could not reach Mailer.'
        }
    }

    try {
        $response = Invoke-WebRequest @invokeArgs
        return [pscustomobject]@{
            StatusCode = [int]$response.StatusCode
            Body = [string]$response.Content
        }
    }
    catch {
        $webResponse = $_.Exception.Response
        if ($null -eq $webResponse) {
            throw 'Mailer request could not reach Mailer.'
        }

        $reader = $null
        try {
            $reader = New-Object System.IO.StreamReader($webResponse.GetResponseStream())
            $responseBody = $reader.ReadToEnd()
        }
        catch {
            $responseBody = ''
        }
        finally {
            if ($null -ne $reader) {
                $reader.Dispose()
            }
        }
        return [pscustomobject]@{
            StatusCode = [int]$webResponse.StatusCode
            Body = [string]$responseBody
        }
    }
}

function Get-SafeErrorCode {
    param([AllowNull()][string]$ResponseBody)

    if ([string]::IsNullOrWhiteSpace($ResponseBody)) {
        return 'unknown'
    }
    try {
        $payload = $ResponseBody | ConvertFrom-Json
        $code = $payload.code
        if ($code -is [string] -and $code -match '\A[A-Z][A-Z0-9_]{0,63}\z') {
            return $code
        }
    }
    catch {
    }
    return 'unknown'
}

function Format-HttpFailure {
    param(
        [Parameter(Mandatory = $true)][int]$StatusCode,
        [Parameter(Mandatory = $true)][string]$Code
    )

    $reason = switch ($StatusCode) {
        400 { 'Bad Request' }
        401 { 'Unauthorized' }
        403 { 'Forbidden' }
        404 { 'Not Found' }
        409 { 'Conflict' }
        413 { 'Payload Too Large' }
        422 { 'Unprocessable Entity' }
        429 { 'Too Many Requests' }
        503 { 'Service Unavailable' }
        default { 'unexpected response' }
    }
    return "HTTP $StatusCode $reason (code=$Code)"
}

function Send-MailRequest {
    param(
        [Parameter(Mandatory = $true)][Uri]$BaseUri,
        [Parameter(Mandatory = $true)][string]$ApiKey,
        [Parameter(Mandatory = $true)][string]$RequestId,
        [Parameter(Mandatory = $true)][string]$Purpose,
        [Parameter(Mandatory = $true)][string]$Recipient,
        [Parameter(Mandatory = $true)][string]$Subject,
        [Parameter(Mandatory = $true)][string]$TextBody,
        [Parameter(Mandatory = $true)][int]$TimeoutSec
    )

    $requestObject = [ordered]@{
        mail_request_id = $RequestId
        purpose = $Purpose
        to = @([ordered]@{ email = $Recipient })
        subject = $Subject
        text_body = $TextBody
    }
    $requestJson = $requestObject | ConvertTo-Json -Compress -Depth 5
    $endpoint = [Uri]::new($BaseUri, 'api/mail-requests')
    $response = Invoke-MailerHttp -Method POST -Uri $endpoint -ApiKey $ApiKey -JsonBody $requestJson -TimeoutSec $TimeoutSec
    if ($response.StatusCode -ne 202) {
        throw (Format-HttpFailure -StatusCode $response.StatusCode -Code (Get-SafeErrorCode -ResponseBody $response.Body))
    }

    try {
        $payload = $response.Body | ConvertFrom-Json
    }
    catch {
        throw 'Mailer returned invalid JSON for the acceptance response.'
    }
    if ($null -eq $payload -or $payload.mail_request_id -ne $RequestId) {
        throw 'Mailer acceptance response returned a different request ID.'
    }
    if ($payload.status -notin @('accepted', 'already_accepted')) {
        throw 'Mailer returned an unknown acceptance status.'
    }
    return [string]$payload.status
}

function Get-MailRequestStatus {
    param(
        [Parameter(Mandatory = $true)][Uri]$BaseUri,
        [Parameter(Mandatory = $true)][string]$ApiKey,
        [Parameter(Mandatory = $true)][string]$RequestId,
        [Parameter(Mandatory = $true)][double]$PollTimeout,
        [Parameter(Mandatory = $true)][double]$PollInterval,
        [Parameter(Mandatory = $true)][int]$TimeoutSec
    )

    $endpoint = [Uri]::new($BaseUri, "api/mail-requests/$RequestId")
    $deadline = [DateTime]::UtcNow.AddSeconds($PollTimeout)
    $lastStatus = 'not queried'

    while ($true) {
        $response = Invoke-MailerHttp -Method GET -Uri $endpoint -ApiKey $ApiKey -JsonBody $null -TimeoutSec $TimeoutSec
        if ($response.StatusCode -eq 200) {
            try {
                $payload = $response.Body | ConvertFrom-Json
            }
            catch {
                throw 'Mailer returned invalid JSON for the status response.'
            }
            if ($null -eq $payload -or $payload.mail_request_id -ne $RequestId) {
                throw 'Mailer status response returned a different request ID.'
            }
            $lastStatus = [string]$payload.status
            if ($lastStatus -notin $NonTerminalStatuses -and $lastStatus -notin $TerminalStatuses) {
                throw 'Mailer returned an unknown delivery status.'
            }
            Write-Host "GET /api/mail-requests/{id} -> $lastStatus"
            if ($lastStatus -in $TerminalStatuses) {
                return $lastStatus
            }
        }
        elseif ($response.StatusCode -in @(429, 503)) {
            $code = Get-SafeErrorCode -ResponseBody $response.Body
            Write-Host "GET /api/mail-requests/{id} -> $(Format-HttpFailure -StatusCode $response.StatusCode -Code $code); retrying within bound"
        }
        else {
            throw (Format-HttpFailure -StatusCode $response.StatusCode -Code (Get-SafeErrorCode -ResponseBody $response.Body))
        }

        $remaining = ($deadline - [DateTime]::UtcNow).TotalSeconds
        if ($remaining -le 0) {
            throw "Status polling timed out after $PollTimeout`s (last_status=$lastStatus)."
        }
        $sleepMilliseconds = [int][Math]::Min($PollInterval * 1000, $remaining * 1000)
        if ($sleepMilliseconds -gt 0) {
            Start-Sleep -Milliseconds $sleepMilliseconds
        }
    }
}

try {
    $resolvedBaseUrl = Get-ValueOrEnvironment -Value $BaseUrl -Name 'MAILER_BASE_URL' -Default $DefaultBaseUrl
    $resolvedRecipient = Get-ValueOrEnvironment -Value $Recipient -Name 'MAILER_RECIPIENT_EMAIL' -Default $null
    $resolvedSubject = Get-ValueOrEnvironment -Value $Subject -Name 'MAILER_SUBJECT' -Default $DefaultSubject
    $resolvedBody = Get-ValueOrEnvironment -Value $Body -Name 'MAILER_TEXT_BODY' -Default $DefaultBody
    $resolvedPurpose = Get-ValueOrEnvironment -Value $Purpose -Name 'MAILER_PURPOSE' -Default $DefaultPurpose
    $resolvedTimeout = Get-ValueOrEnvironment -Value $TimeoutSeconds -Name 'MAILER_TIMEOUT_SECONDS' -Default ([string]$DefaultTimeoutSeconds)
    $resolvedPollTimeout = Get-ValueOrEnvironment -Value $PollTimeoutSeconds -Name 'MAILER_POLL_TIMEOUT_SECONDS' -Default ([string]$DefaultPollTimeoutSeconds)
    $resolvedPollInterval = Get-ValueOrEnvironment -Value $PollIntervalSeconds -Name 'MAILER_POLL_INTERVAL_SECONDS' -Default ([string]$DefaultPollIntervalSeconds)

    if ([string]::IsNullOrWhiteSpace($resolvedRecipient)) {
        throw '-Recipient or MAILER_RECIPIENT_EMAIL is required.'
    }
    if ([string]::IsNullOrEmpty($resolvedSubject) -or [string]::IsNullOrEmpty($resolvedBody) -or [string]::IsNullOrEmpty($resolvedPurpose)) {
        throw 'Subject, body, and purpose must not be empty.'
    }

    $baseUri = Resolve-BaseUri -Value $resolvedBaseUrl
    $timeout = Convert-ToFiniteSeconds -Value $resolvedTimeout -Name '-TimeoutSeconds' -AllowZero $false
    $pollTimeout = Convert-ToFiniteSeconds -Value $resolvedPollTimeout -Name '-PollTimeoutSeconds' -AllowZero $true
    $pollInterval = Convert-ToFiniteSeconds -Value $resolvedPollInterval -Name '-PollIntervalSeconds' -AllowZero $true
    $resolvedRequestId = Get-RequestId -Value $RequestId
    $apiKey = Get-ApiKey
    $timeoutSec = Get-HttpTimeoutSeconds -Seconds $timeout

    Write-Host 'POST /api/mail-requests'
    $acceptanceStatus = Send-MailRequest `
        -BaseUri $baseUri `
        -ApiKey $apiKey `
        -RequestId $resolvedRequestId `
        -Purpose $resolvedPurpose `
        -Recipient $resolvedRecipient `
        -Subject $resolvedSubject `
        -TextBody $resolvedBody `
        -TimeoutSec $timeoutSec
    Write-Host "HTTP 202 Accepted - status: $acceptanceStatus"
    Write-Host "mail_request_id: $resolvedRequestId"

    $deliveryStatus = Get-MailRequestStatus `
        -BaseUri $baseUri `
        -ApiKey $apiKey `
        -RequestId $resolvedRequestId `
        -PollTimeout $pollTimeout `
        -PollInterval $pollInterval `
        -TimeoutSec $timeoutSec
    if ($deliveryStatus -eq 'delivered') {
        Write-Host 'Delivery status: delivered'
        exit 0
    }
    Write-Host "Delivery status: $deliveryStatus (not a successful smoke result)" -ForegroundColor Yellow
    exit 1
}
catch {
    Write-Host "[error] $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
