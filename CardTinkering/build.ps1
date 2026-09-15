param(
  [string]$GameDir = $env:STS2_GAME_DIR,
  [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoDir = (Resolve-Path (Join-Path $projectDir "..")).Path
. (Join-Path $repoDir "scripts\Resolve-Sts2GameDir.ps1")
$GameDir = Resolve-Sts2GameDir -ExplicitPath $GameDir
if ([string]::IsNullOrWhiteSpace($GameDir)) {
  throw "STS2 install not found. Pass -GameDir or set STS2_GAME_DIR."
}

dotnet build (Join-Path $projectDir "CardTinkering.csproj") -c $Configuration "/p:Sts2GameDir=$GameDir"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

$outputDir = Join-Path $projectDir "build\AutoAnthonyCardTinkering"
$resolvedProject = (Resolve-Path -LiteralPath $projectDir).Path
if (Test-Path -LiteralPath $outputDir) {
  $resolvedOutput = (Resolve-Path -LiteralPath $outputDir).Path
  if (-not $resolvedOutput.StartsWith($resolvedProject + [IO.Path]::DirectorySeparatorChar,
      [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clear output outside the project: $resolvedOutput"
  }
  Remove-Item -LiteralPath $outputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectDir "bin\$Configuration\net9.0\AutoAnthonyCardTinkering.dll") -Destination $outputDir
Copy-Item -LiteralPath (Join-Path $projectDir "mod_manifest.json") -Destination (Join-Path $outputDir "AutoAnthonyCardTinkering.json")
Write-Host "Built Auto-Anthonyology: Card Tinkering: $outputDir"
