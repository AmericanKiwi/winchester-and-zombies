# Builds the asset bundle and plugin, and optionally installs them into the local game for testing.
#   .\build.ps1            build only
#   .\build.ps1 -Install   build and copy into the Nomad Drive install
param([switch]$Install, [switch]$SkipBundle)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$unity = 'C:\Program Files\Unity\Hub\Editor\6000.0.73f1\Editor\Unity.exe'
$game = 'A:\SteamLibrary\steamapps\common\Nomad Drive'
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

if (-not $SkipBundle) {
    $log = "$root\logs\bundle.log"
    # Not -nographics: the ammo box labels are rendered to a texture during the build.
    $p = Start-Process $unity -ArgumentList @('-batchmode', '-quit', '-projectPath', "`"$root\BundleProject`"",
        '-executeMethod', 'BundleBuilder.Build', '-logFile', "`"$log`"") -PassThru -Wait
    if ($p.ExitCode -ne 0 -or -not (Select-String -Path $log -Pattern '\[Bundle\] Built' -Quiet)) { throw "Bundle build failed; see $log" }
    Write-Host 'Bundle built.'
}

& "$env:DOTNET_ROOT\dotnet.exe" build "$root\src\WinchesterZombies\WinchesterZombies.csproj" -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
Write-Host 'Plugin built.'

if ($Install) {
    $dest = "$game\BepInEx\plugins\WinchesterZombies"
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item "$root\src\WinchesterZombies\bin\Release\netstandard2.1\WinchesterZombies.dll" $dest -Force
    Copy-Item "$root\out\bundle\winchesterzombies" $dest -Force
    Write-Host "Installed to $dest"
}
