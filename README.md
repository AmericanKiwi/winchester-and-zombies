# Winchester and Zombies (Nomad Drive mod) - source

Source code for **Winchester and Zombies**, a BepInEx 5 mod for Nomad Drive.
Official download: https://www.nexusmods.com/nomaddrive/mods/44

This repository is public so the mod can be reviewed: it holds everything that
becomes `WinchesterZombies.dll`, plus the scripts that build the asset bundle
and package the release. See [LICENSE.txt](LICENSE.txt): the code may be read
and built for checking, but not redistributed or reused.

## What's here

| Path | What it is |
| --- | --- |
| `src/WinchesterZombies/` | The plugin (C#, netstandard2.1). Builds `WinchesterZombies.dll`. |
| `BundleProject/Assets/Editor/` | Unity editor scripts that build the `winchesterzombies` asset bundle (models, sounds, icons). |
| `release-template/` | Installer/uninstaller (`Install.bat` -> `Install.ps1`), README and licence shipped in the zip. |
| `build.ps1` | Builds the bundle (Unity, batch mode) and the plugin, optionally installs into the game. |
| `package.ps1` | Builds and zips a release, writing `CHECKSUMS.txt` and the zip's SHA-256. |

The release zip also contains, unmodified, BepInEx 5.4.23.6 (x64, Mono) and the
unstripped Mono class libraries BepInEx needs for this game. Those are not in
this repository; get them from https://github.com/BepInEx/BepInEx/releases.

The art (rifle and zombie models and textures) is not in this repository; the
asset bundle in the release is data only and holds no code.

## Building the plugin

Requirements:
- .NET SDK 8 (or any SDK that builds netstandard2.1)
- Nomad Drive installed (the build references the game's own managed DLLs; none are copied)
- BepInEx 5.4.23.6 x64 (for `BepInEx.dll` and `0Harmony.dll`)

```powershell
dotnet build src/WinchesterZombies/WinchesterZombies.csproj -c Release `
  -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Nomad Drive" `
  -p:BepInExCore="C:\path\to\BepInEx_win_x64_5.4.23.6\BepInEx\core"
```

Output: `src/WinchesterZombies/bin/Release/netstandard2.1/WinchesterZombies.dll`
(the same file as `BepInEx/plugins/WinchesterZombies/WinchesterZombies.dll` in the release).

## What the plugin does at runtime

- Loads its asset bundle and adds a Winchester rifle, ammo boxes and zombies to the game.
- Patches four game methods with Harmony: the save loader (to respawn the mod's
  rifles and ammo boxes from saves) and three placement checks (so ammo boxes
  can't be placed on walls or on empty fitting spots in vehicles).
- Talks only to other players in the same game session, through the game's own
  Mirror networking. It makes no web requests and touches no files outside the
  game folder: it reads its asset bundle from its plugin folder, and its only
  file write is its settings file under `BepInEx/config` (renaming an older
  settings file of its own there, if one exists).

## Installer

`Install.bat` runs `Install.ps1`, which:
1. checks every file in the zip against `CHECKSUMS.txt` (refuses to install a modified copy);
2. finds the Steam copy of Nomad Drive from the Steam library folders (or asks for the path);
3. copies the `files` folder into the game folder.

`Uninstall.ps1` removes the mod's files, and BepInEx if no other plugins use it.
