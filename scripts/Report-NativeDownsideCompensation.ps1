param(
    [Parameter(Mandatory = $true)]
    [string]$AuditDirectory,
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$auditRoot = if ([IO.Path]::IsPathRooted($AuditDirectory)) {
    [IO.Path]::GetFullPath($AuditDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $projectRoot $AuditDirectory))
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = $auditRoot }
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$cardsPath = Join-Path $auditRoot "cards.tsv"
$occurrencesPath = Join-Path $auditRoot "component_occurrences.tsv"
$catalogPath = Join-Path $projectRoot "ChaosCardGenerator\Data\catalog_recipes.json"
foreach ($path in $cardsPath, $occurrencesPath, $catalogPath) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required audit input is missing: $path" }
}

$cards = @(Import-Csv -LiteralPath $cardsPath -Delimiter "`t")
$occurrences = @(Import-Csv -LiteralPath $occurrencesPath -Delimiter "`t")
$catalogDocument = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
# Windows PowerShell 5 returns a top-level JSON array as one pipeline object when ConvertFrom-Json is nested
# directly inside @(...). Assigning first lets array enumeration behave consistently with PowerShell 7.
$recipes = @($catalogDocument)
$recipeByKey = @{}
foreach ($recipe in $recipes) { $recipeByKey["$($recipe.Character):$($recipe.Id)"] = $recipe }

function Get-Median([object[]]$Values) {
    $ordered = @($Values | ForEach-Object { [double]$_ } | Sort-Object)
    if ($ordered.Count -eq 0) { return $null }
    if (($ordered.Count % 2) -eq 1) { return $ordered[[int](($ordered.Count - 1) / 2)] }
    return ($ordered[$ordered.Count / 2 - 1] + $ordered[$ordered.Count / 2]) / 2
}

function Get-Percentile([object[]]$Values, [double]$Percentile) {
    $ordered = @($Values | ForEach-Object { [double]$_ } | Sort-Object)
    if ($ordered.Count -eq 0) { return $null }
    $boundedPercentile = [Math]::Max(0, [Math]::Min(1, $Percentile))
    $position = $boundedPercentile * ($ordered.Count - 1)
    $lower = [int][Math]::Floor($position)
    $upper = [int][Math]::Ceiling($position)
    if ($lower -eq $upper) { return $ordered[$lower] }
    return $ordered[$lower] + ($ordered[$upper] - $ordered[$lower]) * ($position - $lower)
}

function Get-CostTier([double]$Cost) {
    if ($Cost -lt 0.25) { return "0" }
    if ($Cost -lt 0.75) { return "0.5" }
    if ($Cost -lt 1.25) { return "1" }
    if ($Cost -lt 1.75) { return "1.5" }
    if ($Cost -lt 2.25) { return "2" }
    if ($Cost -lt 2.75) { return "2.5" }
    if ($Cost -lt 3.25) { return "3" }
    if ($Cost -lt 3.75) { return "3.5" }
    return "4+"
}

function Get-DownsideFamily([string]$Template) {
    switch -Regex ($Template) {
        '^KEYWORD:Exhaust$' { return 'ExhaustKeyword' }
        '^KEYWORD:Ethereal$' { return 'EtherealKeyword' }
        '^N:HP-$' { return 'SelfHpLoss' }
        '^N:Discard$' { return 'DiscardChosen' }
        '^N:DiscardAll$' { return 'DiscardAll' }
        '^(N:Exhaust|D:ExhaustSelectedHandCard|NCR:ExhaustSelectedDrawCard|CL:ExhaustUpToHandCards|I:ExhaustRandomAttack)$' { return 'ExhaustOtherCards' }
        '^I:PreventDrawThisTurn$' { return 'PreventDrawThisTurn' }
        '^D:CreateDazedInDiscard$' { return 'GenerateDazed' }
        '^D:CreateBurnInDiscard$' { return 'GenerateBurn' }
        '^D:CreateSlimeInDiscard$' { return 'GenerateSlime' }
        '^D:CreateTwoWoundsInDiscard$' { return 'GenerateWounds' }
        '^D:CreateVoidInDiscard$' { return 'GenerateVoid' }
        '^R:AddDebrisToHand$' { return 'GenerateDebris' }
        '^R:FillHandWithDebris$' { return 'FillHandWithDebris' }
        '^NCR:IncreaseAllCardCostsThisTurn$' { return 'IncreaseAllCostsThisTurn' }
        '^D:IncreaseThisCardCost$' { return 'IncreaseThisCardCost' }
        '^D:LoseTemporaryFocus$' { return 'LoseTemporaryFocus' }
        '^D:LoseFocus$' { return 'LosePermanentFocus' }
        '^D:LoseOrbSlots$' { return 'LoseOrbSlots' }
        '^N:LoseDex$' { return 'LoseDexterity' }
        '^NCR:LoseStrength$' { return 'LoseStrength' }
        '^NCR:KillOsty$' { return 'KillOsty' }
        '^NCR:ApplySelfDoom$' { return 'ApplySelfDoom' }
        '^CL:NoBlockFromCards$' { return 'NoBlockFromCards' }
        '^CL:DieOnUnblockedAttack$' { return 'DieOnUnblockedAttack' }
        '^T:Apply$' { return 'EnemyGainStrength' }
        '^M:DamageMinusPerCardInHand$' { return 'DamagePenaltyPerCard' }
        '^D:DrawAndDiscardNonZero$' { return 'DiscardDrawnNonZeroCards' }
        default { return $Template }
    }
}

$negativeByCard = @{}
foreach ($occurrence in $occurrences | Where-Object negative -eq 'True') {
    if (-not $negativeByCard.ContainsKey($occurrence.card)) {
        $negativeByCard[$occurrence.card] = [Collections.Generic.List[object]]::new()
    }
    $negativeByCard[$occurrence.card].Add([pscustomobject]@{
        Template = $occurrence.template
        Family = Get-DownsideFamily $occurrence.template
    })
}

# A clean native cohort is deliberately computed without the current downside multiplier. This keeps the reverse
# inference independent from the very compensation table being audited. Colorless is separate because its effects
# have a distinct pool multiplier; all five character pools share one value model.
$cleanBaselines = @{}
$cleanCards = @($cards | Where-Object {
    -not $negativeByCard.ContainsKey("$($_.character):$($_.cardId)")
})
foreach ($group in $cleanCards | Group-Object {
    $pool = if ($_.character -eq 'Colorless') { 'Colorless' } else { 'Role' }
    "$pool|$($_.rarity)|$(Get-CostTier ([double]$_.effectiveCost))"
}) {
    $cleanBaselines[$group.Name] = Get-Median @($group.Group | ForEach-Object {
        [double]$_.positiveValue / [Math]::Max(1, [double]$_.powerFactor)
    })
}

$rows = [Collections.Generic.List[object]]::new()
foreach ($card in $cards) {
    $cardKey = "$($card.character):$($card.cardId)"
    if (-not $negativeByCard.ContainsKey($cardKey)) { continue }
    $families = @($negativeByCard[$cardKey].Family | Sort-Object -Unique)
    $templates = @($negativeByCard[$cardKey].Template | Sort-Object -Unique)
    $pool = if ($card.character -eq 'Colorless') { 'Colorless' } else { 'Role' }
    $baselineKey = "$pool|$($card.rarity)|$(Get-CostTier ([double]$card.effectiveCost))"
    $cleanExpected = $cleanBaselines[$baselineKey]
    if ($null -eq $cleanExpected) { $cleanExpected = [double]$card.configuredTarget }
    $payload = [double]$card.positiveValue / [Math]::Max(1, [double]$card.powerFactor)
    $recipe = $recipeByKey[$cardKey]
    $text = if ($null -eq $recipe) { '' } else {
        (@($recipe.Atoms.ChineseText) + @($recipe.Tags | ForEach-Object { "[$_]" })) -join ''
    }
    foreach ($family in $families) {
        $detailTemplates = @($negativeByCard[$cardKey] | Where-Object Family -eq $family |
            Select-Object -ExpandProperty Template -Unique)
        $detailText = if ($null -eq $recipe) { $detailTemplates -join '+' } else {
            $atomText = @($recipe.Atoms | Where-Object Template -in $detailTemplates |
                Select-Object -ExpandProperty ChineseText)
            if ($family -eq 'ExhaustKeyword') { $atomText += '[Exhaust]' }
            if ($family -eq 'EtherealKeyword') { $atomText += '[Ethereal]' }
            $atomText -join ''
        }
        $rows.Add([pscustomobject]@{
            Family = $family
            Card = $cardKey
            Title = $card.zhTitle
            Character = $card.character
            Rarity = $card.rarity
            EffectiveCost = [double]$card.effectiveCost
            DownsideFamilyCount = $families.Count
            Downside = $detailText
            CardText = $text
            PositivePayload = [Math]::Round($payload, 3)
            ConfiguredTarget = [double]$card.configuredTarget
            CleanNativeTarget = [Math]::Round($cleanExpected, 3)
            PositiveVsConfigured = [Math]::Round($payload / [Math]::Max(1, [double]$card.configuredTarget), 4)
            PositiveVsCleanNative = [Math]::Round($payload / [Math]::Max(1, $cleanExpected), 4)
            CurrentCompensation = [Math]::Round([double]$card.downsidePercent / 100, 4)
        })
    }
}

$families = foreach ($group in $rows | Group-Object Family) {
    $single = @($group.Group | Where-Object DownsideFamilyCount -eq 1)
    $reference = if ($single.Count -gt 0) { $single } else { @($group.Group) }
    $configuredRatios = @($reference.PositiveVsConfigured)
    $nativeRatios = @($reference.PositiveVsCleanNative)
    $implied = [Math]::Max(1, (Get-Median $configuredRatios))
    [pscustomobject]@{
        Family = $group.Name
        Cards = $group.Count
        SingleDownsideAnchors = $single.Count
        ConfiguredRatioMedian = [Math]::Round((Get-Median $configuredRatios), 4)
        ConfiguredRatioP25 = [Math]::Round((Get-Percentile $configuredRatios 0.25), 4)
        ConfiguredRatioP75 = [Math]::Round((Get-Percentile $configuredRatios 0.75), 4)
        CleanNativeRatioMedian = [Math]::Round((Get-Median $nativeRatios), 4)
        CurrentCompensationMedian = [Math]::Round((Get-Median @($reference.CurrentCompensation)), 4)
        ImpliedCompensationPercent = [int](5 * [Math]::Round(($implied * 100) / 5))
        Confidence = if ($single.Count -ge 5) { 'medium' } elseif ($single.Count -ge 2) { 'low' } else { 'anchor-only' }
    }
}

$rows | Sort-Object Family, Card | Export-Csv -LiteralPath (Join-Path $outputRoot 'downside_cards.tsv') `
    -Delimiter "`t" -NoTypeInformation -Encoding utf8
$families | Sort-Object Family | Export-Csv -LiteralPath (Join-Path $outputRoot 'downside_families.tsv') `
    -Delimiter "`t" -NoTypeInformation -Encoding utf8

$summary = [Text.StringBuilder]::new()
[void]$summary.AppendLine('# Native downside compensation reverse audit')
[void]$summary.AppendLine()
[void]$summary.AppendLine('- `PositiveVsConfigured`: positive payload after removing downside value / balanced configured center for the same rarity and effective cost.')
[void]$summary.AppendLine('- `PositiveVsCleanNative`: positive payload / median native card with no downside in the same pool, rarity and cost tier.')
[void]$summary.AppendLine('- Aggregation prefers cards with exactly one downside family. Multi-downside cards remain in the per-card detail only.')
[void]$summary.AppendLine('- `ImpliedCompensationPercent` is a diagnostic anchor, not an automatic tuning write-back. Sparse and synergistic cards require manual review.')
[void]$summary.AppendLine()
[void]$summary.AppendLine('| Downside family | Native cards | Single-downside anchors | Positive/template median | P25-P75 | Current compensation median | Implied compensation | Confidence |')
[void]$summary.AppendLine('|---|---:|---:|---:|---:|---:|---:|---|')
foreach ($family in $families | Sort-Object Family) {
    [void]$summary.AppendLine("| $($family.Family) | $($family.Cards) | $($family.SingleDownsideAnchors) | " +
        "$($family.ConfiguredRatioMedian) | $($family.ConfiguredRatioP25)-$($family.ConfiguredRatioP75) | " +
        "$($family.CurrentCompensationMedian) | $($family.ImpliedCompensationPercent)% | $($family.Confidence) |")
}
[IO.File]::WriteAllText((Join-Path $outputRoot 'downside_summary.md'), $summary.ToString(),
    [Text.UTF8Encoding]::new($false))

Write-Host "Native downside compensation report: $outputRoot; cards=$($rows.Count); families=$($families.Count)"
