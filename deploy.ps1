param(
    [string]$Sts2GameDir = $env:STS2_GAME_DIR,
    [string]$UploaderRoot = "",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoDir = $PSScriptRoot
. (Join-Path $repoDir "scripts\Resolve-Sts2GameDir.ps1")

& (Join-Path $repoDir "build.ps1") -Sts2GameDir $Sts2GameDir -Configuration $Configuration

$Sts2GameDir = Resolve-Sts2GameDir -ExplicitPath $Sts2GameDir
if ([string]::IsNullOrWhiteSpace($Sts2GameDir)) {
    throw "STS2 install not found. Pass -Sts2GameDir or set STS2_GAME_DIR."
}

$packageDir = Join-Path $repoDir "ChaosMode\build\AutoAnthony"
$gameTarget = Join-Path $Sts2GameDir "mods\AutoAnthony"
New-Item -ItemType Directory -Force -Path $gameTarget | Out-Null

$files = @("AutoAnthony.dll", "AutoAnthony.json", "AutoAnthony.pck")
foreach ($file in $files) {
    $source = Join-Path $packageDir $file
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Build output missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $gameTarget $file) -Force
}

if (-not [string]::IsNullOrWhiteSpace($UploaderRoot)) {
    $uploaderContent = Join-Path $UploaderRoot "AutoAnthony\content"
    New-Item -ItemType Directory -Force -Path $uploaderContent | Out-Null
    foreach ($file in $files) {
        Copy-Item -LiteralPath (Join-Path $packageDir $file) `
            -Destination (Join-Path $uploaderContent $file) -Force
    }
    Write-Output "Updated uploader content: $uploaderContent"
}

Write-Output "Deployed game mod: $gameTarget"
