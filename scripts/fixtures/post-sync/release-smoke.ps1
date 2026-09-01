# Required: MAILER_IMAGE_TAG or MAILER_IMAGE_DIGEST (exactly one)
# Invoke-ReleaseSmokePreflight resolves MAILER_IMAGE_REFERENCE
. (Join-Path $PSScriptRoot 'lib\release-smoke-preflight.ps1')
