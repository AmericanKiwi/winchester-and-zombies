# Builds everything and produces release\WinchesterZombies_v<version>.zip
# -OfficialUrl is the one page people should download from; it goes into the readme, licence and log.
param([switch]$SkipBundle, [string]$OfficialUrl = '')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
& "$root\build.ps1" -SkipBundle:$SkipBundle

$version = ([xml](Get-Content "$root\src\WinchesterZombies\WinchesterZombies.csproj")).Project.PropertyGroup.Version | Select-Object -First 1
$name = "WinchesterZombies_v$version"
$stage = "$root\release\$name"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
$files = "$stage\files"
New-Item -ItemType Directory -Force "$files\BepInEx\core", "$files\BepInEx\config", "$files\BepInEx\unstripped_corlib", "$files\BepInEx\plugins\WinchesterZombies" | Out-Null

# BepInEx loader (with the corlib override this game needs) and its core.
$bx = "$root\bepinex"
Copy-Item "$bx\winhttp.dll", "$bx\doorstop_config.ini", "$bx\.doorstop_version" $files
Copy-Item "$bx\BepInEx\core\*" "$files\BepInEx\core\" -Exclude '*.xml'
Copy-Item "$bx\BepInEx\unstripped_corlib\*" "$files\BepInEx\unstripped_corlib\"
Copy-Item "$bx\changelog.txt" "$files\BepInEx\BepInEx_changelog.txt"
Set-Content "$files\BepInEx\config\BepInEx.cfg" "[Chainloader]`r`n`r`n## Keep plugin objects alive across the game's scene loads.`r`nHideManagerGameObject = true`r`n" -Encoding utf8

# The mod.
Copy-Item "$root\src\WinchesterZombies\bin\Release\netstandard2.1\WinchesterZombies.dll" "$files\BepInEx\plugins\WinchesterZombies\"
Copy-Item "$root\out\bundle\winchesterzombies" "$files\BepInEx\plugins\WinchesterZombies\"

# Installer, readme and licence, with the official page and version filled in.
Copy-Item "$root\release-template\*" $stage
$url = if ($OfficialUrl) { $OfficialUrl } else { "(ask whoever sent you this for the author's official page)" }
foreach ($doc in 'README.txt', 'LICENSE.txt') {
    $text = (Get-Content "$stage\$doc" -Raw).Replace('{{OFFICIAL_URL}}', $url).Replace('{{VERSION}}', $version)
    Set-Content "$stage\$doc" $text -Encoding utf8
}

# SHA-256 of every installed file; Install.ps1 refuses to install if any differ.
$filesRoot = (Resolve-Path $files).Path.TrimEnd('\') + '\'
Get-ChildItem $files -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash)  $($_.FullName.Substring($filesRoot.Length))"
} | Set-Content "$stage\CHECKSUMS.txt" -Encoding ascii

$zip = "$root\release\$name.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path "$stage\*" -DestinationPath $zip
# Publish this hash on the official page so people can check their download.
$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash
Set-Content "$zip.sha256" "$zipHash  $name.zip" -Encoding ascii
Write-Host "Packaged $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
Write-Host "SHA-256: $zipHash"
