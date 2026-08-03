param(
    [ValidateSet('F00','F01','F02','F03','F04','F05','F06')]
    [string[]]$Fixture = @('F00','F01','F03','F04'),
    [ValidateSet('buffered','token')]
    [string[]]$Mode = @('buffered','token'),
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\tests\Amane.Mailer.Spike526.Probe\Amane.Mailer.Spike526.Probe.csproj'

foreach ($fixtureId in $Fixture) {
    foreach ($modeName in $Mode) {
        dotnet run --project $project -c $Configuration --no-launch-profile -- warmup $fixtureId $modeName | Out-Null
        dotnet run --project $project -c $Configuration --no-launch-profile -- measure $fixtureId $modeName
    }
}
