param(
    [Parameter(Mandatory = $true)]
    [string]$AuditDirectory,
    [double]$LowerRatio = 0.5,
    [double]$UpperRatio = 2.0
)

$ErrorActionPreference = "Stop"
$AuditDirectory = (Resolve-Path -LiteralPath $AuditDirectory).Path
$cardsPath = Join-Path $AuditDirectory "cards.tsv"
$occurrencesPath = Join-Path $AuditDirectory "component_occurrences.tsv"
if (-not (Test-Path -LiteralPath $cardsPath) -or -not (Test-Path -LiteralPath $occurrencesPath)) {
    throw "Native valuation audit files are missing from: $AuditDirectory"
}

function Median([double[]]$Values) {
    if ($Values.Count -eq 0) { return [double]::NaN }
    $ordered = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($ordered.Count / 2)
    if ($ordered.Count % 2 -eq 1) { return $ordered[$middle] }
    return ($ordered[$middle - 1] + $ordered[$middle]) / 2.0
}

function F([double]$Value) {
    if ([double]::IsNaN($Value)) { return "—" }
    return $Value.ToString("0.###", [Globalization.CultureInfo]::InvariantCulture)
}

function Escape([string]$Value) {
    return ($Value -replace '\|', '\|' -replace "`r?`n", ' ')
}

$cards = @(Import-Csv -LiteralPath $cardsPath -Delimiter "`t")
$outliers = @($cards | Where-Object {
    $ratio = [double]$_.generatorRatio
    $ratio -lt $LowerRatio -or $ratio -gt $UpperRatio
})
$specialCardConfigurations = @{
    'Silent:WraithForm' = '持续获得无实体并永久失去敏捷，正负两侧都不能按普通即时组件同侪归一化。'
    'Necrobinder:Dirge' = 'X费按X=1最低支付审计；召唤X与灵魂X必须继续和非X单位价值一致。'
    'Regent:VoidForm' = '前两张牌免费、立即结束回合和虚无已按原卡3费稀有中心整体反推。'
    'Colorless:RollingBoulder' = '永久增伤与回合触发次数按原卡整体二次增长曲线反推。'
    'Silent:GrandFinale' = '抽牌堆为空是极低可用率条件，原卡刻意远离普通0费稀有同侪。'
}
$ordinaryOutliers = @($outliers | Where-Object {
    -not $specialCardConfigurations.ContainsKey("$($_.character):$($_.cardId)")
})
$cardByKey = @{}
foreach ($card in $outliers) { $cardByKey["$($card.character):$($card.cardId)"] = $card }

$occurrences = @(Import-Csv -LiteralPath $occurrencesPath -Delimiter "`t" | Where-Object {
    $cardByKey.ContainsKey($_.card)
})

# The audit emits one occurrence row per member of a linked package. Collapse those rows before suggesting a
# package value, otherwise a three-part trigger chain would be counted three times.
$cardPackages = @($occurrences | Group-Object card, package | ForEach-Object {
    $rows = @($_.Group)
    $card = $cardByKey[$rows[0].card]
    $normalized = [double]$card.normalizedValue
    $target = [double]$card.generatorTarget
    $cardScale = if ($normalized -gt 0) { $target / $normalized } else { [double]::NaN }
    $currentMarginal = [double]$rows[0].currentMarginal
    [pscustomobject]@{
        CardKey = $rows[0].card
        Character = $card.character
        CardId = $card.cardId
        ChineseTitle = $card.zhTitle
        Rarity = $card.rarity
        EffectiveCost = [double]$card.effectiveCost
        CardRatio = [double]$card.generatorRatio
        Package = $rows[0].package
        Templates = (@($rows.template | Sort-Object -Unique) -join ' + ')
        Members = @($rows.template | Sort-Object -Unique).Count
        CurrentMarginal = $currentMarginal
        SuggestedMarginal = if ($currentMarginal -gt 0 -and -not [double]::IsNaN($cardScale)) {
            $currentMarginal * $cardScale
        } else { [double]::NaN }
        SuggestedScale = $cardScale
        Negative = @($rows | Where-Object { $_.negative -eq 'True' }).Count -gt 0
    }
})

$manualPackages = @{
    'A:turnStart -> N:AllD -> CL:IncreaseRollingDamage' = '保持6460；滚石系数已按原卡与3费稀有中心反推。'
    'A:VoidFormFirstCardsFree' = '保持7364/张；与结束回合2.00倍及虚无1.14倍共同反推《虚空形态》的3费稀有中心。'
    'R:EndTurn' = '保持显式2.00倍整卡倍率；《虚空形态》已与免费出牌和虚无共同反推。'
    'C:playableIfDrawPileEmpty -> N:AllD' = '保持；华丽收场条件按极低可用率提供巨额收益。'
    'CL:DieOnUnblockedAttack' = '保持专属致死负面规则，不换算为普通线性价值。'
    'CL:NoBlockFromCards' = '保持专属大负面规则，不单独线性定价。'
    'R:FillHandWithDebris' = '保持专属迫降大负面规则。'
    'KEYWORD:Sly' = '保持；奇巧的费用与可用条件不能由边际正收益反推。'
    'KEYWORD:Strike' = '保持；标签不应承担整卡残差。'
    'KEYWORD:Exhaust' = '保持显式1.50倍整卡倍率；不要用低估的限制效果反推消耗。'
    'KEYWORD:Ethereal' = '保持显式1.14倍整卡倍率；需结合整卡而非作为正收益调整。'
    'KEYWORD:Retain' = '暂保持400；多张离群牌方向冲突，等待全样本关键词专项。'
    'T:D' = '保持普通单体伤害基准；这些整卡的离群均由相邻计数、状态或资源效果主导。'
    'N:AllD' = '保持全体伤害倍率；迫降与星灭分别含专属大负面和资源/减力上下文。'
    'N:B' = '保持普通格挡基准；涉及致死负面、禁格挡、奇巧与蓝星收益，方向互相冲突。'
    'T:Apply' = '按结构化状态variant分别计价；易伤/虚弱以单层550/470为锚点，并按层数平方根递减。'
    'N:Draw' = '保持普通抽牌基准；本次唯一离群证据来自奇巧牌本能反应。'
    'N:E' = '保持回费基准；战术大师的奇巧可用条件不能按3费普通牌同侪反推。'
    'N:Intangible' = '暂保持每层1050；幽魂形态残差同时受到持续减敏与先古档同侪影响。'
    'R:GainStars' = '暂保持每颗260；收集光辉与明耀打击均有资源/回牌复合上下文。'
    'R:PutThisOnDraw' = '保持580；按占用下一次抽牌但提供重复使用机会的共享牌本体移动效果计价。'
    'A:turnStart -> N:LoseDex' = '作为幽魂形态的持续负面单独专项，不从零/负边际直接反推。'
    'NCR:Summon' = '按要求暂不调整；继续使用多张原版牌反推的每点180线性价值。'
    'NCR:SummonX' = '不按挽歌单卡残差调整；强制与非X召唤共享每点180价值。'
    'NCR:CreateSoulInDrawX' = '不按挽歌单卡残差调整；强制与非X灵魂生成共享每张450价值。'
}

$summaryRows = @($cardPackages | Group-Object Package | ForEach-Object {
    $rows = @($_.Group)
    $positiveRows = @($rows | Where-Object { $_.CurrentMarginal -gt 0 -and -not [double]::IsNaN($_.SuggestedMarginal) })
    $low = @($rows | Where-Object { $_.CardRatio -lt $LowerRatio }).Count
    $high = @($rows | Where-Object { $_.CardRatio -gt $UpperRatio }).Count
    $current = Median @($positiveRows | ForEach-Object { [double]$_.CurrentMarginal })
    $suggested = Median @($positiveRows | ForEach-Object { [double]$_.SuggestedMarginal })
    $scale = if (-not [double]::IsNaN($current) -and $current -gt 0) { $suggested / $current } else { [double]::NaN }
    $manual = $manualPackages[$_.Name]
    $classification = if ($manual) { '特殊/保持' }
        elseif ($low -gt 0 -and $high -gt 0) { '上下文冲突' }
        elseif ($positiveRows.Count -eq 0) { '负面/零边际复核' }
        elseif ($scale -lt 0.8 -or $scale -gt 1.25) { '建议调整' }
        else { '暂不调整' }
    [pscustomobject]@{
        Package = $_.Name
        Templates = (@($rows.Templates | Sort-Object -Unique) -join ' / ')
        Cards = (@($rows | ForEach-Object { "$($_.Character):$($_.ChineseTitle)" } | Sort-Object -Unique) -join '、')
        Samples = @($rows.CardKey | Sort-Object -Unique).Count
        LowCards = $low
        HighCards = $high
        CurrentMarginal = $current
        SuggestedMarginal = $suggested
        SuggestedScale = $scale
        Classification = $classification
        Note = if ($manual) { $manual } elseif ($rows.Count -eq 1) { '单卡证据，仅作为下一轮人工校准起点。' } else { '' }
    }
} | Sort-Object @{ Expression = {
        switch ($_.Classification) {
            '建议调整' { 0 }; '上下文冲突' { 1 }; '负面/零边际复核' { 2 }; '特殊/保持' { 3 }; default { 4 }
        }
    } }, @{ Expression = { if ([double]::IsNaN($_.SuggestedScale)) { 99 } else { $_.SuggestedScale } } })

$tsvPath = Join-Path $AuditDirectory "outlier_component_suggestions.tsv"
$summaryRows | Select-Object Package, Templates, Cards, Samples, LowCards, HighCards,
    @{ Name = 'CurrentMarginal'; Expression = { F $_.CurrentMarginal } },
    @{ Name = 'SuggestedMarginal'; Expression = { F $_.SuggestedMarginal } },
    @{ Name = 'SuggestedScale'; Expression = { F $_.SuggestedScale } }, Classification, Note |
    Export-Csv -LiteralPath $tsvPath -Delimiter "`t" -NoTypeInformation -Encoding utf8

$markdown = [Text.StringBuilder]::new()
[void]$markdown.AppendLine('# 原版整卡离群组件建议')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine("- 审计区间：生成器整卡评价倍率 ``$LowerRatio-$UpperRatio``；区间外共 $($outliers.Count) 张（偏低 $(@($outliers | Where-Object { [double]$_.generatorRatio -lt $LowerRatio }).Count)，偏高 $(@($outliers | Where-Object { [double]$_.generatorRatio -gt $UpperRatio }).Count)）。")
[void]$markdown.AppendLine("- 排除已明确整体反推的特殊配置后，普通卡离群 **$($ordinaryOutliers.Count)** 张；特殊配置离群 $($outliers.Count - $ordinaryOutliers.Count) 张。")
[void]$markdown.AppendLine("- 覆盖：$($summaryRows.Count) 个合法估值包、$(@($occurrences.template | Sort-Object -Unique).Count) 个组件模板。")
[void]$markdown.AppendLine('- “当前/建议值”是该合法估值包对整卡的边际预算值，不一定等于卡面数字；建议值先按整卡残差等比例分配，再对特殊机制、关键词和方向冲突进行标记。')
[void]$markdown.AppendLine('- 这是一份人工校准清单，不应自动回写。单卡证据可能来自原卡刻意高低模、卡组条件或模型尚未表达的负面。')
[void]$markdown.AppendLine()

foreach ($classification in @('建议调整', '上下文冲突', '负面/零边际复核', '特殊/保持', '暂不调整')) {
    $section = @($summaryRows | Where-Object { $_.Classification -eq $classification })
    if ($section.Count -eq 0) { continue }
    [void]$markdown.AppendLine("## $classification")
    [void]$markdown.AppendLine()
    [void]$markdown.AppendLine('| 估值包 | 涉及离群原卡 | 当前值 | 建议值 | 倍率 | 备注 |')
    [void]$markdown.AppendLine('|---|---|---:|---:|---:|---|')
    foreach ($row in $section) {
        [void]$markdown.AppendLine("| ``$(Escape $row.Package)`` | $(Escape $row.Cards) | $(F $row.CurrentMarginal) | $(F $row.SuggestedMarginal) | $(F $row.SuggestedScale) | $(Escape $row.Note) |")
    }
    [void]$markdown.AppendLine()
}

[void]$markdown.AppendLine('## 逐卡明细')
[void]$markdown.AppendLine()
foreach ($card in $outliers | Sort-Object character, @{ Expression = { [double]$_.generatorRatio } }) {
    $key = "$($card.character):$($card.cardId)"
    [void]$markdown.AppendLine("### $($card.character) · $($card.zhTitle)（``$($card.cardId)``）")
    [void]$markdown.AppendLine()
    $downsideMultiplier = [double]$card.downsidePercent / 100.0
    [void]$markdown.AppendLine("整卡完整评价：(``正收益 $($card.positiveValue) - 线性负面 $($card.linearDownsideValue)``) / ``负面倍率 $(F $downsideMultiplier)`` / ``能力一次性倍率 $($card.powerFactor)`` = ``$($card.normalizedValue)``；生成器预算中心 ``$($card.generatorTarget)``，最终倍率 **$($card.generatorRatio)**。")
    [void]$markdown.AppendLine()
    [void]$markdown.AppendLine('| 估值包 | 当前边际 | 等比例建议 |')
    [void]$markdown.AppendLine('|---|---:|---:|')
    foreach ($unit in $cardPackages | Where-Object { $_.CardKey -eq $key } | Sort-Object Package) {
        [void]$markdown.AppendLine("| ``$(Escape $unit.Package)`` | $(F $unit.CurrentMarginal) | $(F $unit.SuggestedMarginal) |")
    }
    [void]$markdown.AppendLine()
}

$markdownPath = Join-Path $AuditDirectory "outlier_component_suggestions.md"
[IO.File]::WriteAllText($markdownPath, $markdown.ToString(), [Text.UTF8Encoding]::new($false))
Write-Host "outliers=$($outliers.Count); packages=$($summaryRows.Count); markdown=$markdownPath; tsv=$tsvPath"
