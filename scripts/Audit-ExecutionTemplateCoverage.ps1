param(
    [string] $SourceRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath($SourceRoot).TrimEnd('\', '/')
$project = [System.IO.Path]::Combine($root, 'ChaosCardGenerator', 'ChaosCardGenerator.csproj')
$executorPath = [System.IO.Path]::Combine($root, 'ChaosMode', 'ChaosOperationExecutor.cs')
$characters = @('Ironclad', 'Silent', 'Defect', 'Necrobinder', 'Regent', 'Colorless')

$rows = [System.Collections.Generic.List[object]]::new()
foreach ($character in $characters) {
    $output = & dotnet run --project $project -c $Configuration --no-build -- `
        --dump-runtime-templates --character $character
    if ($LASTEXITCODE -ne 0) { throw "Runtime-template catalog failed for $character. Exit code: $LASTEXITCODE" }
    foreach ($line in $output) {
        $parts = $line -split "`t", 4
        if ($parts.Count -ne 4) { throw "Malformed runtime-template row: $line" }
        $rows.Add([pscustomobject]@{
            Template = $parts[0]
            Scope = $parts[1]
            Opcode = $parts[2]
            Variant = $parts[3]
        })
    }
}

$executor = [System.IO.File]::ReadAllText($executorPath)
$mentioned = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($match in [System.Text.RegularExpressions.Regex]::Matches(
    $executor, '"((?:[A-Z][A-Z_]*:[A-Za-z0-9_]+)|(?:N_[A-Z0-9_]+))"')) {
    [void] $mentioned.Add($match.Groups[1].Value)
}

$structuredOpcodes = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
foreach ($opcode in @('deal_damage', 'gain_block', 'draw_cards', 'gain_energy', 'gain_stars',
    'lose_hp', 'heal', 'discard_card', 'apply_power')) {
    [void] $structuredOpcodes.Add($opcode)
}

$metadataScopes = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
foreach ($scope in @('Modifier', 'AbilityTrigger', 'ConditionalTrigger', 'AbilityRule')) {
    [void] $metadataScopes.Add($scope)
}

# These Independent operations are payload metadata consumed by the next-Attack hook. They intentionally do not
# execute as ordinary card-play commands; treating them as such would replay or modify the generated source card.
$linkedEffectTemplates = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
foreach ($template in @('I:ReplayAttack', 'I:SetCostZero')) {
    [void] $linkedEffectTemplates.Add($template)
}

$uniqueRows = $rows | Sort-Object Template, Scope, Opcode, Variant -Unique
$missing = @($uniqueRows | Where-Object {
    $_.Template.IndexOf(':Proxy', [System.StringComparison]::Ordinal) -lt 0 -and
    -not $metadataScopes.Contains($_.Scope) -and
    -not $structuredOpcodes.Contains($_.Opcode) -and
    -not $linkedEffectTemplates.Contains($_.Template) -and
    -not $mentioned.Contains($_.Template)
})

if ($missing.Count -gt 0) {
    Write-Host "ERROR: $($missing.Count) catalog operation route(s) have no structured, metadata, proxy, or ASCII Template execution path."
    $missing | Format-Table Template, Scope, Opcode, Variant -AutoSize
    exit 1
}

Write-Host "Execution-template coverage verified: $($uniqueRows.Count) catalog routes; 0 missing."
