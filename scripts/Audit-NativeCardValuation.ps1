param(
    [string]$OutputDirectory = "",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDirectory = Join-Path $projectRoot "audits\native_card_valuation_v111\$stamp"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot $OutputDirectory
}

dotnet run --project (Join-Path $projectRoot "ChaosCardGenerator\ChaosCardGenerator.csproj") `
    --configuration $Configuration -- --native-card-valuation-audit $OutputDirectory

Write-Host "Native-card valuation audit: $OutputDirectory"
