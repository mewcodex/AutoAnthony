param(
    [Parameter(Mandatory = $true)]
    [string] $SourceRoot,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath($SourceRoot)
$project = [System.IO.Path]::Combine($root, 'ChaosCardGenerator', 'ChaosCardGenerator.csproj')
$registry = [System.IO.Path]::Combine($root, 'ChaosCardGenerator', 'Data', 'catalog_runtime_specs.json')
$temporary = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(),
    'autoanthony_catalog_runtime_specs_' + [System.Guid]::NewGuid().ToString('N') + '.json')

try {
    & dotnet run --project $project -c $Configuration --no-build -- `
        --write-catalog-runtime-specs $temporary
    if ($LASTEXITCODE -ne 0) {
        throw "Catalog RuntimeSpec export failed with exit code $LASTEXITCODE."
    }
    if (-not [System.IO.File]::Exists($registry)) {
        throw "Reviewed catalog RuntimeSpec registry is missing: $registry"
    }
    $expected = [System.IO.File]::ReadAllBytes($registry)
    $actual = [System.IO.File]::ReadAllBytes($temporary)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $expectedHash = [System.BitConverter]::ToString($sha.ComputeHash($expected))
        $actualHash = [System.BitConverter]::ToString($sha.ComputeHash($actual))
    }
    finally {
        $sha.Dispose()
    }
    if ($expectedHash -ne $actualHash) {
        throw 'Catalog RuntimeSpec registry is stale. Review the catalog/compiler change and deliberately regenerate catalog_runtime_specs.json.'
    }
    $jsonText = [System.Text.Encoding]::UTF8.GetString($actual)
    $count = [System.Text.RegularExpressions.Regex]::Matches($jsonText, '(?m)^    "Id":').Count
    Write-Host "Catalog RuntimeSpec registry verified: $count structured operation occurrences."
}
finally {
    if ([System.IO.File]::Exists($temporary)) {
        Remove-Item -LiteralPath $temporary -Force
    }
}
