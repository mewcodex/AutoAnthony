param(
    [string]$Sts2GameDir = $env:STS2_GAME_DIR,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot "ChaosMode\build.ps1") `
    -GameDir $Sts2GameDir `
    -Configuration $Configuration
