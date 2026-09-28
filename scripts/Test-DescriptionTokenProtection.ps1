$ErrorActionPreference = 'Stop'
# Compile the actual formatter without loading Godot or changing a user's save.
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../ChaosMode/ChaosCardModel.cs') -Raw
$start = $source.IndexOf('internal static class ChaosTextFormatter')
$end = $source.IndexOf('internal static class ChaosDerivativeTextStyle', $start)
if ($start -lt 0 -or $end -lt 0) { throw 'Formatter source not found.' }
$formatter = $source.Substring($start, $end - $start)
$formatter = $formatter.Substring(0, $formatter.LastIndexOf('}') + 1)
Add-Type -TypeDefinition ("using System; using System.Collections.Generic;`n" + $formatter.Replace('internal static class', 'public static class'))
$templates = @(
    '{TargetType:choose(AllEnemies):对所有敌人|}造成{Damage:diff()}点伤害{Repeat:choose(1):|{}次}。{GainsBlock:cond:' + "`n" + '获得{CalculatedBlock:diff()}点[gold]格挡[/gold].|}',
    '{GainsBlock:cond:Gain {CalculatedBlock:diff()} [gold]Block[/gold].|}',
    '{Strength:cond:{Block:choose(1):{}|{Damage:diff()}}|}',
    '{Damage:diff()} {Repeat:choose(1):|{} times} {}'
)
foreach ($template in $templates) {
    $actual = [ChaosTextFormatter]::Format($template, $true)
    if ($actual -cne $template) { throw "SmartFormat template changed:`n$template`n$actual" }
}
$plain = [ChaosTextFormatter]::Format('获得3点格挡。 Gain 4 Block.', $true)
if ($plain -cne '获得[blue]3[/blue]点[gold]格挡[/gold]。 Gain [blue]4[/blue] [gold]Block[/gold].') {
    throw "Plain-text highlighting changed: $plain"
}
Write-Output 'Description token protection regression passed.'
