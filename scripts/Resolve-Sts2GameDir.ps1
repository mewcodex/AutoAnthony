function Resolve-Sts2GameDir {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        return $ExplicitPath
    }

    $candidates = @("C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2")
    $steamRoot = (Get-ItemProperty -LiteralPath "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue).SteamPath
    if (-not [string]::IsNullOrWhiteSpace($steamRoot)) {
        $libraryRoots = @($steamRoot)
        $libraryFile = Join-Path $steamRoot "steamapps\libraryfolders.vdf"
        if (Test-Path -LiteralPath $libraryFile) {
            $libraryRoots += Select-String -LiteralPath $libraryFile -Pattern '"path"\s+"([^"]+)"' | ForEach-Object {
                $_.Matches[0].Groups[1].Value -replace '\\\\', '\'
            }
        }
        $candidates += $libraryRoots | ForEach-Object {
            Join-Path $_ "steamapps\common\Slay the Spire 2"
        }
    }

    return $candidates | Select-Object -Unique | Where-Object {
        Test-Path -LiteralPath (Join-Path $_ "data_sts2_windows_x86_64\sts2.dll")
    } | Select-Object -First 1
}
