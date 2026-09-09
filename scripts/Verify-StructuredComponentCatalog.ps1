param(
    [Parameter(Mandatory = $true)]
    [string] $SourceRoot,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath($SourceRoot)
$project = [System.IO.Path]::Combine($root, 'ChaosCardGenerator', 'ChaosCardGenerator.csproj')
$registry = [System.IO.Path]::Combine($root, 'ChaosCardGenerator', 'Data', 'catalog_recipes.json')
$temporary = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(),
    'autoanthony_structured_catalog_' + [System.Guid]::NewGuid().ToString('N') + '.json')

try {
    & dotnet run --project $project -c $Configuration --no-build -- `
        --write-structured-catalog $temporary
    if ($LASTEXITCODE -ne 0) {
        throw "Structured component catalog export failed with exit code $LASTEXITCODE."
    }
    if (-not [System.IO.File]::Exists($registry)) {
        throw "Reviewed structured component catalog is missing: $registry"
    }
    # The reviewed file is often touched by editors that preserve one terminal newline while the deterministic
    # exporter intentionally omits it. Normalize only trailing CR/LF bytes; every semantic byte remains exact.
    $expectedText = [System.IO.File]::ReadAllText($registry).Replace("`r`n", "`n").TrimEnd("`r", "`n")
    $actualText = [System.IO.File]::ReadAllText($temporary).Replace("`r`n", "`n").TrimEnd("`r", "`n")
    $expected = [System.Text.Encoding]::UTF8.GetBytes($expectedText)
    $actual = [System.Text.Encoding]::UTF8.GetBytes($actualText)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $expectedHash = [System.BitConverter]::ToString($sha.ComputeHash($expected))
        $actualHash = [System.BitConverter]::ToString($sha.ComputeHash($actual))
    }
    finally {
        $sha.Dispose()
    }
    if ($expectedHash -ne $actualHash) {
        throw 'Structured component catalog is stale. Review the authoring-source change and deliberately regenerate catalog_recipes.json.'
    }
    # Windows PowerShell 5.1 does not ship System.Text.Json. These property names occur exactly once per
    # recipe/operation in the deterministic export, so counting them avoids a slow ConvertFrom-Json pass.
    $jsonText = [System.Text.Encoding]::UTF8.GetString($actual)
    $recipes = [System.Text.RegularExpressions.Regex]::Matches($jsonText, '(?m)^    "Character":').Count
    $operations = [System.Text.RegularExpressions.Regex]::Matches($jsonText, '(?m)^        "SemanticId":').Count
    if ($recipes -ne 481 -or $operations -ne 931) {
        throw "Structured catalog totals drifted: recipes=$recipes operations=$operations."
    }
    Write-Host "Structured component catalog verified: $recipes recipes, $operations operations."
}
finally {
    if ([System.IO.File]::Exists($temporary)) {
        Remove-Item -LiteralPath $temporary -Force
    }
}
