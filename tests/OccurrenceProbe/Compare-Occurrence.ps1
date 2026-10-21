param([string]$Prefix = 'occurrence-0128-stage4')
$ErrorActionPreference = 'Stop'
foreach ($character in 'Ironclad','Silent','Regent','Necrobinder','Defect','Colorless') {
    $path = Join-Path $PSScriptRoot "../../audits/$Prefix-$character.txt"
    if (!(Test-Path -LiteralPath $path)) { continue }
    $inFamily = $false
    $rows = @()
    foreach ($line in Get-Content -LiteralPath $path) {
        if ($line.StartsWith("family`t")) { $inFamily = $true; continue }
        if (!$inFamily) { continue }
        if ([string]::IsNullOrWhiteSpace($line)) { break }
        $fields = $line.Split("`t")
        if ($fields.Count -eq 5 -and $fields[4] -ne 'n/a') {
            $rows += [pscustomobject]@{ Family = $fields[0]; Ratio = [double]::Parse($fields[4], [cultureinfo]::InvariantCulture) }
        }
    }
    $minimum = if ($character -eq 'Colorless') { 0.6 } else { 0.8 }
    $below = @($rows | Where-Object Ratio -LT $minimum)
    "${character}: $($rows.Count - $below.Count)/$($rows.Count) raw families meet $minimum (before intentional exceptions)"
    $below | Format-Table -AutoSize
}
