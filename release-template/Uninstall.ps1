# Removes Winchester & Zombies (and BepInEx, if no other mods use it). Run Uninstall.bat.
$ErrorActionPreference = 'Stop'
$steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
$candidates = @()
if ($steam) {
    $candidates += Join-Path $steam 'steamapps\common\Nomad Drive'
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
            $candidates += Join-Path ($m.Groups[1].Value -replace '\\\\', '\') 'steamapps\common\Nomad Drive'
        }
    }
}
$game = $candidates | Where-Object { Test-Path (Join-Path $_ 'Nomad Drive.exe') } | Select-Object -First 1
if (-not $game) { $game = Read-Host 'Paste the full path of your Nomad Drive folder' }
if (Get-Process -Name 'Nomad Drive' -ErrorAction SilentlyContinue) { throw 'Please close Nomad Drive first.' }

Remove-Item (Join-Path $game 'BepInEx\plugins\WinchesterZombies') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $game 'BepInEx\config\com.*.nomaddrive.winchesterzombies.cfg') -Force -ErrorAction SilentlyContinue

$others = Get-ChildItem (Join-Path $game 'BepInEx\plugins') -ErrorAction SilentlyContinue
if (-not $others) {
    foreach ($f in 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'BepInEx') {
        Remove-Item (Join-Path $game $f) -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Host 'Removed the mod and BepInEx. The game is back to normal.'
} else {
    Write-Host 'Removed the mod. BepInEx was left in place because other mods use it.'
}
