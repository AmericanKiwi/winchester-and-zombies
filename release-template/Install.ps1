# Installs Winchester & Zombies into Nomad Drive. Run Install.bat (it calls this).
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$payload = Join-Path $here 'files'

function Find-Game {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    $libraries = @()
    if ($steam) {
        $libraries += $steam
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $libraries += $m.Groups[1].Value -replace '\\\\', '\'
            }
        }
    }
    foreach ($lib in $libraries | Select-Object -Unique) {
        $dir = Join-Path $lib 'steamapps\common\Nomad Drive'
        if (Test-Path (Join-Path $dir 'Nomad Drive.exe')) { return $dir }
    }
    return $null
}

# Refuse to install a payload that differs from what was packaged: every file must match CHECKSUMS.txt,
# and nothing extra (an added DLL, say) may be in files\.
function Test-Payload {
    $sums = Join-Path $here 'CHECKSUMS.txt'
    if (-not (Test-Path $sums)) { throw 'CHECKSUMS.txt is missing. This is not an official copy; download the mod again from the official page.' }
    $expected = @{}
    foreach ($line in Get-Content $sums) {
        if ($line -match '^([0-9a-fA-F]{64})\s+(.+)$') { $expected[$Matches[2].Trim()] = $Matches[1].ToUpper() }
    }
    $problems = @()
    $root = (Resolve-Path $payload).Path.TrimEnd('\') + '\'
    foreach ($f in Get-ChildItem $payload -Recurse -File -Force) {
        $rel = $f.FullName.Substring($root.Length)
        if (-not $expected.ContainsKey($rel)) { $problems += "unexpected file: $rel"; continue }
        if ((Get-FileHash $f.FullName -Algorithm SHA256).Hash -ne $expected[$rel]) { $problems += "changed: $rel" }
        $expected.Remove($rel)
    }
    foreach ($rel in $expected.Keys) { $problems += "missing: $rel" }
    if ($problems.Count) {
        $problems | ForEach-Object { Write-Host "  $_" }
        throw 'These files do not match the official release. Nothing was installed. Download the mod again from the official page.'
    }
}
Test-Payload
Write-Host 'All files match the official release.'

$game = Find-Game
if (-not $game) {
    Write-Host 'Could not find Nomad Drive automatically.'
    $game = Read-Host 'Paste the full path of your Nomad Drive folder (the one with "Nomad Drive.exe")'
}
if (-not (Test-Path (Join-Path $game 'Nomad Drive.exe'))) { throw "Nomad Drive.exe not found in '$game'." }
if (Get-Process -Name 'Nomad Drive' -ErrorAction SilentlyContinue) { throw 'Please close Nomad Drive first, then run Install.bat again.' }

Write-Host "Installing into $game"
Copy-Item (Join-Path $payload '*') $game -Recurse -Force
Write-Host ''
Write-Host 'Done! Launch Nomad Drive from Steam as normal.'
Write-Host 'Everyone in a multiplayer session needs this mod (same version).'
