# Shared preflight for release smoke (issue #506).
# Dot-source from scripts/release-smoke.ps1. No docker mutations here.

function Write-ReleaseSmokePreflightError {
    param([string]$Message)
    Write-Host "[error] $Message" -ForegroundColor Red
}

function Exit-ReleaseSmokePreflightError {
    param([string]$Message)
    Write-ReleaseSmokePreflightError -Message $Message
    throw [System.InvalidOperationException]::new($Message)
}

function Get-ReleaseSmokeTrimmedValue {
    param([string]$Value)
    if ($null -eq $Value) { return '' }
    return $Value.Trim()
}

function Test-ReleaseSmokeDockerTagSyntax {
    param([string]$Tag)
    return ($Tag -match '^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$')
}

function Test-ReleaseSmokeDigestSyntax {
    param([string]$Digest)
    return ($Digest -match '^sha256:[0-9a-f]{64}$')
}

function Resolve-ReleaseSmokeArtifactReference {
    $repository = Get-ReleaseSmokeTrimmedValue -Value $env:MAILER_IMAGE_REPOSITORY
    if ([string]::IsNullOrEmpty($repository)) {
        $repository = 'ghcr.io/kooiei-in4a/amane-mailer'
        $env:MAILER_IMAGE_REPOSITORY = $repository
    }

    $tag = Get-ReleaseSmokeTrimmedValue -Value $env:MAILER_IMAGE_TAG
    $digest = Get-ReleaseSmokeTrimmedValue -Value $env:MAILER_IMAGE_DIGEST
    $tagSet = -not [string]::IsNullOrEmpty($tag)
    $digestSet = -not [string]::IsNullOrEmpty($digest)

    if (-not $tagSet -and -not $digestSet) {
        Exit-ReleaseSmokePreflightError -Message 'MAILER_IMAGE_TAG or MAILER_IMAGE_DIGEST is required (exactly one)'
    }
    if ($tagSet -and $digestSet) {
        Exit-ReleaseSmokePreflightError -Message 'MAILER_IMAGE_TAG and MAILER_IMAGE_DIGEST are mutually exclusive'
    }

    if ($tagSet) {
        if ($tag -eq 'latest') {
            Exit-ReleaseSmokePreflightError -Message 'MAILER_IMAGE_TAG=latest is not allowed for release smoke'
        }
        if (-not (Test-ReleaseSmokeDockerTagSyntax -Tag $tag)) {
            Exit-ReleaseSmokePreflightError -Message 'MAILER_IMAGE_TAG has invalid Docker tag syntax'
        }
        $script:ReleaseSmokeImageReference = "${repository}:${tag}"
        return
    }

    if (-not (Test-ReleaseSmokeDigestSyntax -Digest $digest)) {
        Exit-ReleaseSmokePreflightError -Message 'MAILER_IMAGE_DIGEST must match sha256:<64-lowercase-hex>'
    }
    $script:ReleaseSmokeImageReference = "${repository}@${digest}"
}

function Test-ReleaseSmokeProjectName {
    param([string]$ProjectName)

    $project = Get-ReleaseSmokeTrimmedValue -Value $ProjectName
    if ([string]::IsNullOrEmpty($project)) {
        Exit-ReleaseSmokePreflightError -Message 'RELEASE_SMOKE_PROJECT must not be empty'
    }
    if ($project -eq '.' -or $project -eq '..') {
        Exit-ReleaseSmokePreflightError -Message 'RELEASE_SMOKE_PROJECT is invalid'
    }
    if ($project.Contains('/') -or $project.Contains('\')) {
        Exit-ReleaseSmokePreflightError -Message 'RELEASE_SMOKE_PROJECT is invalid'
    }
    if ($project -match '\s') {
        Exit-ReleaseSmokePreflightError -Message 'RELEASE_SMOKE_PROJECT is invalid'
    }
    if ($project -notmatch '^amane-mailer-release-smoke(?:-[a-z0-9][a-z0-9-]{0,40})?$') {
        Exit-ReleaseSmokePreflightError -Message 'RELEASE_SMOKE_PROJECT is invalid'
    }

    $script:ReleaseSmokeProject = $project
    $env:RELEASE_SMOKE_PROJECT = $project
}

function Test-ReleaseSmokeComposeFile {
    param([string]$ComposeFilePath)
    if ([string]::IsNullOrWhiteSpace($ComposeFilePath) -or -not (Test-Path -LiteralPath $ComposeFilePath)) {
        Exit-ReleaseSmokePreflightError -Message 'release smoke compose file is missing'
    }
    $script:ReleaseSmokeComposeFile = $ComposeFilePath
}

function Test-ReleaseSmokeRequiredTools {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        Exit-ReleaseSmokePreflightError -Message 'missing required tools: docker'
    }
    & docker compose version *> $null
    if ($LASTEXITCODE -ne 0) {
        Exit-ReleaseSmokePreflightError -Message "'docker compose' plugin is not available"
    }
}

function Get-ReleaseSmokeContextEndpoint {
    param([string]$ContextName)

    if (-not [string]::IsNullOrWhiteSpace($ContextName)) {
        $inspect = & docker context inspect $ContextName --format '{{.Endpoints.docker.Host}}' 2>$null
    }
    else {
        $inspect = & docker context inspect --format '{{.Endpoints.docker.Host}}' 2>$null
    }
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($inspect)) {
        return ''
    }
    return [string]$inspect
}

function Test-ReleaseSmokeLocalDockerEndpoint {
    $endpoint = ''

    if (-not [string]::IsNullOrWhiteSpace($env:DOCKER_CONTEXT)) {
        $endpoint = Get-ReleaseSmokeContextEndpoint -ContextName $env:DOCKER_CONTEXT
        if ([string]::IsNullOrWhiteSpace($endpoint)) {
            Exit-ReleaseSmokePreflightError -Message 'remote Docker endpoint is not allowed for release smoke'
        }
    }
    elseif (-not [string]::IsNullOrWhiteSpace($env:DOCKER_HOST)) {
        $endpoint = [string]$env:DOCKER_HOST
    }
    else {
        $endpoint = Get-ReleaseSmokeContextEndpoint -ContextName ''
        if ([string]::IsNullOrWhiteSpace($endpoint)) {
            Exit-ReleaseSmokePreflightError -Message 'remote Docker endpoint is not allowed for release smoke'
        }
    }

    if ($endpoint -notmatch '^(unix://|npipe://)') {
        Exit-ReleaseSmokePreflightError -Message 'remote Docker endpoint is not allowed for release smoke'
    }
}

function Invoke-ReleaseSmokePreflight {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        [string]$ComposeFilePath
    )

    if ([string]::IsNullOrWhiteSpace($ComposeFilePath)) {
        $ComposeFilePath = Join-Path $RepoRoot 'infra/docker/docker-compose.release-smoke.yml'
    }

    Resolve-ReleaseSmokeArtifactReference
    $env:MAILER_IMAGE_REFERENCE = $script:ReleaseSmokeImageReference

    $project = if ([string]::IsNullOrWhiteSpace($env:RELEASE_SMOKE_PROJECT)) {
        'amane-mailer-release-smoke'
    }
    else {
        $env:RELEASE_SMOKE_PROJECT
    }
    Test-ReleaseSmokeProjectName -ProjectName $project
    Test-ReleaseSmokeComposeFile -ComposeFilePath $ComposeFilePath
    Test-ReleaseSmokeRequiredTools
    Test-ReleaseSmokeLocalDockerEndpoint
}

function Get-ReleaseSmokeComposeArgs {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$ComposeArgs)
    $args = @('compose', '-p', $script:ReleaseSmokeProject, '-f', $script:ReleaseSmokeComposeFile)
    if ($ComposeArgs.Count -gt 0) {
        $args += $ComposeArgs
    }
    return $args
}

function Invoke-ReleaseSmokeCompose {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$ComposeArgs)
    $argv = Get-ReleaseSmokeComposeArgs @ComposeArgs
    & docker @argv
    if ($LASTEXITCODE -ne 0) {
        throw 'docker compose command failed'
    }
}

function Invoke-ReleaseSmokeComposeQuiet {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$ComposeArgs)
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $argv = Get-ReleaseSmokeComposeArgs @ComposeArgs
        & docker @argv 2>&1 | Out-Null
    }
    finally {
        $ErrorActionPreference = $prevEap
    }
}
