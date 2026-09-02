param(
  [string]$GameDir = $env:STS2_GAME_DIR,
  [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoDir = (Resolve-Path (Join-Path $projectDir "..")).Path
$localCandidates = @(
  "F:\SteamLibrary\steamapps\common\Slay the Spire 2",
  "D:\Steam\steamapps\common\Slay the Spire 2",
  "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
)
if ([string]::IsNullOrWhiteSpace($GameDir)) {
  $GameDir = $localCandidates | Where-Object {
    Test-Path -LiteralPath (Join-Path $_ "data_sts2_windows_x86_64\sts2.dll")
  } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($GameDir)) {
  throw "STS2 install not found. Pass -GameDir or set STS2_GAME_DIR."
}
$gameDll = Join-Path $GameDir "data_sts2_windows_x86_64\sts2.dll"
if (-not (Test-Path -LiteralPath $gameDll)) {
  throw "STS2 v111 DLL not found: $gameDll"
}

$outputDir = Join-Path $projectDir "build\AutoAnthony"
$pckRoot = Join-Path $outputDir "_pck_src"
$resolvedProject = (Resolve-Path -LiteralPath $projectDir).Path
if (Test-Path -LiteralPath $outputDir) {
  $resolvedOutput = (Resolve-Path -LiteralPath $outputDir).Path
  if (-not $resolvedOutput.StartsWith($resolvedProject + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clear output outside the project: $resolvedOutput"
  }
  Remove-Item -LiteralPath $outputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $pckRoot -Force | Out-Null

$generatorProject = Join-Path $repoDir "ChaosCardGenerator\ChaosCardGenerator.csproj"
dotnet build $generatorProject -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "generator build failed with exit code $LASTEXITCODE" }
$structuredCatalogAudit = Join-Path $repoDir "scripts\Verify-StructuredComponentCatalog.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -File $structuredCatalogAudit `
  -SourceRoot $repoDir -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "structured component catalog audit failed with exit code $LASTEXITCODE" }
$runtimeSpecAudit = Join-Path $repoDir "scripts\Verify-CatalogRuntimeSpecs.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -File $runtimeSpecAudit `
  -SourceRoot $repoDir -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "catalog RuntimeSpec audit failed with exit code $LASTEXITCODE" }
$executionAudit = Join-Path $repoDir "scripts\Audit-ExecutionTemplateCoverage.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -File $executionAudit `
  -SourceRoot $repoDir -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "execution-template audit failed with exit code $LASTEXITCODE" }
$apiContractProject = Join-Path $repoDir "ApiContractSmoke\ApiContractSmoke.csproj"
dotnet build $apiContractProject -c $Configuration "/p:Sts2GameDir=$GameDir"
if ($LASTEXITCODE -ne 0) { throw "component API contract build failed with exit code $LASTEXITCODE" }

dotnet build (Join-Path $projectDir "ChaosMode.csproj") -c $Configuration "/p:Sts2GameDir=$GameDir"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

$binDir = Join-Path $projectDir "bin\$Configuration\net9.0"
Copy-Item -LiteralPath (Join-Path $binDir "AutoAnthony.dll") -Destination $outputDir
Copy-Item -LiteralPath (Join-Path $projectDir "mod_manifest.json") -Destination (Join-Path $outputDir "AutoAnthony.json")
Copy-Item -LiteralPath (Join-Path $projectDir "mod_manifest.json") -Destination (Join-Path $pckRoot "mod_manifest.json")

$locDestination = Join-Path $pckRoot "AutoAnthony\localization"
New-Item -ItemType Directory -Path $locDestination -Force | Out-Null
Copy-Item -Path (Join-Path $projectDir "assets\localization\*") -Destination $locDestination -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectDir "assets\mod_image.png") -Destination (Join-Path $pckRoot "AutoAnthony\mod_image.png") -Force

$packer = Join-Path $repoDir "tools\pack_godot_pck.py"
if (-not (Test-Path -LiteralPath $packer)) {
  throw "Godot PCK packer not found: $packer"
}
$pckPath = Join-Path $outputDir "AutoAnthony.pck"
python $packer $pckRoot -o $pckPath --engine-version 4.5.1 --pack-version 3
if ($LASTEXITCODE -ne 0) { throw "PCK packing failed with exit code $LASTEXITCODE" }
# The unpacked staging tree is a build-only input. Keeping it beside the four distributable files makes a
# whole-folder deployment recurse into localization JSON as if they were mod manifests, and can destabilize
# startup when the same resources are also mounted from the PCK.
$resolvedPckRoot = (Resolve-Path -LiteralPath $pckRoot).Path
$resolvedOutput = (Resolve-Path -LiteralPath $outputDir).Path
if (-not $resolvedPckRoot.StartsWith($resolvedOutput + [IO.Path]::DirectorySeparatorChar,
    [StringComparison]::OrdinalIgnoreCase)) {
  throw "Refusing to clear PCK staging tree outside build output: $resolvedPckRoot"
}
Remove-Item -LiteralPath $resolvedPckRoot -Recurse -Force

Write-Host "Built AutoAnthony for STS2 v111: $outputDir"
