param(
    [Parameter(Mandatory = $true)]
    [string] $SourceRoot,

    [Parameter(Mandatory = $true)]
    [string] $AllowlistPath,

    [switch] $Update
)

$ErrorActionPreference = 'Stop'

$resolvedRoot = [System.IO.Path]::GetFullPath($SourceRoot).TrimEnd('\', '/')
$resolvedAllowlist = [System.IO.Path]::GetFullPath($AllowlistPath)

$localizedMemberPattern = '\b(?:ChineseText|EnglishText|ChineseDescription|EnglishDescription|ChineseUpgradeDescription|EnglishUpgradeDescription|ChineseName|EnglishName)\b'
$semanticStringCallPattern = '\b(?:text|chinese|english|description|effectiveText|triggerText|operationText)\s*\.\s*(?:Contains|StartsWith|EndsWith|IndexOf|Replace|Trim|TrimStart|TrimEnd)\s*\('
$semanticRegexPattern = '\bRegex\s*\.\s*(?:IsMatch|Match|Matches|Replace)\s*\(\s*(?:text|chinese|english|description|effectiveText|triggerText|operationText)\b'

$entries = [System.Collections.Generic.List[string]]::new()
$sourceDirectories = @(
    [System.IO.Path]::Combine($resolvedRoot, 'ChaosCardGenerator'),
    [System.IO.Path]::Combine($resolvedRoot, 'ChaosMode')
)

foreach ($directory in $sourceDirectories) {
    if (-not [System.IO.Directory]::Exists($directory)) {
        throw "Localized-text boundary source directory does not exist: $directory"
    }

    foreach ($file in [System.IO.Directory]::EnumerateFiles($directory, '*.cs', [System.IO.SearchOption]::AllDirectories)) {
        $relative = $file.Substring($resolvedRoot.Length).TrimStart('\', '/').Replace('\', '/')
        if ($relative -match '/(?:bin|obj)/') { continue }

        foreach ($line in [System.IO.File]::ReadLines($file)) {
            $normalized = [System.Text.RegularExpressions.Regex]::Replace($line.Trim(), '\s+', ' ')
            if ($normalized.Length -eq 0) { continue }
            if (($normalized -notmatch $localizedMemberPattern) -and
                ($normalized -notmatch $semanticStringCallPattern) -and
                ($normalized -notmatch $semanticRegexPattern)) { continue }
            $entries.Add("$relative|$normalized")
        }
    }
}

$current = @($entries | Sort-Object)
$utf8 = [System.Text.UTF8Encoding]::new($false)

if ($Update) {
    $parent = [System.IO.Path]::GetDirectoryName($resolvedAllowlist)
    if ($parent) { [System.IO.Directory]::CreateDirectory($parent) | Out-Null }
    [System.IO.File]::WriteAllLines($resolvedAllowlist, $current, $utf8)
    Write-Host "Updated localized-text boundary allowlist: $($current.Count) entries."
    exit 0
}

if (-not [System.IO.File]::Exists($resolvedAllowlist)) {
    throw "Localized-text boundary allowlist is missing: $resolvedAllowlist"
}

$allowed = @([System.IO.File]::ReadAllLines($resolvedAllowlist, $utf8) | Sort-Object)
$difference = Compare-Object -ReferenceObject $allowed -DifferenceObject $current
if ($difference) {
    Write-Host "ERROR: Localized-text boundary changed. Runtime semantics must not acquire new localized-text dependencies. Review the entries below and deliberately update the allowlist only for compiler, renderer, validation, logging, or legacy-migration code."
    $difference | ForEach-Object {
        $kind = if ($_.SideIndicator -eq '=>') { 'ADDED' } else { 'REMOVED' }
        Write-Host "$kind $($_.InputObject)"
    }
    exit 1
}

Write-Host "Localized-text boundary verified: $($current.Count) reviewed entries."
