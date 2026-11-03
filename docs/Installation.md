# Installation

Castle Story Plus runs on the Unity 6 port: Castle Story on a current Unity, made from your own Castle Story install the
first time it starts (see [Unity 6 port](Unity6Port.md)). You must own and install Castle Story on Steam; nothing of the
game is in the packages.

Download the package for your system from the [latest release](https://github.com/Tricky12321/CastleStoryPlus/releases/latest). Each package is complete: installer, the port with Castle Story Plus, its launcher and BepInEx 5.4.23.5.

| System | Package | How |
|---|---|---|
| Windows | `CastleStoryPlus-<version>-windows.zip` | Unpack it and double-click `install.bat`. |
| Linux | `CastleStoryPlus-<version>-linux.zip` | Unpack it and run `bash install.sh`. |

The installer finds Castle Story in your Steam libraries and:
- puts the port in the game's folder, `CastleStoryPlus-Unity6` (its mod settings, `BepInEx/config`, are kept on an
  update);
- installs BepInEx 5 in the game's folder if it is missing, and points its doorstop (`run_bepinex.sh` on Linux,
  `doorstop_config.ini` on Windows) at `BepInEx/core/CastleStoryPlus.PortLauncher.dll`;
- removes Castle Story Plus for the original game (0.8.0 and before), which the port replaces.

Play in Steam then starts the original game, whose doorstop runs the launcher before anything of the game: it starts
the port's player, waits for it (also when the first start converts the game and starts the player again) and ends
with it, so Steam shows you playing for as long as the port runs. The first start converts the game for Unity 6 (a
minute or so), and again after each update. Saves and settings (`Info/`) are the original game's. The launcher writes
`BepInEx/CastleStoryPlus.PortLauncher.log` in the game's folder, the port's install `CastleStoryPlus-Unity6/install.log`.

> **Linux launch option:** the game needs the Steam launch option `./run_bepinex.sh %command%`. If Steam is closed while the installer runs, it sets the option itself, in every Steam user's `localconfig.vdf`, and keeps a `.castlestoryplus.bak` backup. Otherwise set it once yourself: right-click Castle Story > Properties > Launch Options. On Windows BepInEx's doorstop loads by itself (`winhttp.dll`).

You can also run the installer straight from the repository without downloading a package first; it then downloads the latest package itself.

Linux:

```sh
curl -fsSL https://raw.githubusercontent.com/Tricky12321/CastleStoryPlus/main/installer/install.sh | bash
```

Windows (PowerShell):

```powershell
& ([scriptblock]::Create((New-Object Net.WebClient).DownloadString('https://raw.githubusercontent.com/Tricky12321/CastleStoryPlus/main/installer/install.ps1')))
```

Installer options (`install.sh` / `install.bat`):

| Linux | Windows | Effect |
|---|---|---|
| `--tag v0.8.1` | `-Tag v0.8.1` | install a specific release |
| `--game-dir DIR` | `-GameDir DIR` | game folder, if it is not found automatically |
| `--force` | `-Force` | reinstall even if up to date |
| `--uninstall` | `-Uninstall` | remove Castle Story Plus: the port and the launcher (BepInEx and its config stay) |

## Updates

Every release is a version tag (`v0.1.0`). At startup the plugin asks GitHub for the releases and picks the newest published release (not a draft or pre-release) that has a package for your system. If that release is newer than the installed plugin, the main menu shows a notice with **Update and restart**.

Update and restart first downloads the installer from that tag's source (`installer/` in the repository), falling back to the one shipped in `BepInEx/plugins/CastleStoryPlus/installer/`. The installer waits for the game to close, installs exactly that tag and starts the game again through Steam. Its output goes to `BepInEx/CastleStoryPlus.Update.log` (in the port's folder). Turn the check off with `[Features] UpdateCheck = false`.

Castle Story Plus 0.8.0 and older (the mod for the original game) find 0.8.1 the same way: the release's packages have the names they look for, and the 0.8.1 installer they run moves the game over to the port. The port only updates when it was installed in the game's folder by the installer; unpacked elsewhere by hand, the notice opens the release page.

## Manual installation

1. Unpack **BepInEx 5.4.23.5** (`BepInEx_linux_x64_5.4.23.5.zip` or `BepInEx_win_x64_5.4.23.5.zip` from the [BepInEx releases](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5)) into the Castle Story folder (the one that contains `Castle Story_Data`), unless it is there already.
2. Copy the release's `CastleStoryPlus-Unity6` folder into the Castle Story folder, and `launcher/CastleStoryPlus.PortLauncher.dll` into its `BepInEx/core`.
3. Point BepInEx's doorstop at the launcher:
   - **Linux:** in `run_bepinex.sh`, set `target_assembly="BepInEx/core/CastleStoryPlus.PortLauncher.dll"` and use the Linux launch option above.
   - **Windows:** in `doorstop_config.ini`, set `target_assembly = BepInEx\core\CastleStoryPlus.PortLauncher.dll`.
4. Start the game from Steam. The port's first start converts the game; `CastleStoryPlus-Unity6/BepInEx/LogOutput.log` should then contain `Castle Story Plus <version> loaded`.

The port can also be started on its own: `CastleStoryPlus-Unity6/run_bepinex.sh` on Linux, `CastleStoryPlus.exe` on Windows (Steam must be running for the online games).

## Why a bootstrap? (the original game)

Castle Story runs Unity's very old **Mono 2.6**. On Linux, MonoMod (the library behind HarmonyX) cannot change memory protection on this runtime, because its `mprotect` platform needs `Environment.SystemPageSize`, which Mono 2.6 does not have. On top of that, BepInEx's terminal fix resets the platform to `null`. As a result, every Harmony patch fails with a `NullReferenceException` in `NativeDetour`.

`CastleStoryPlus.Bootstrap.dll` is a small doorstop entry point that fixes this before BepInEx starts:
- it installs a MonoMod native platform that calls `mprotect` directly;
- it keeps that platform when BepInEx tries to reset it;
- it then starts the normal BepInEx preloader.

On Windows the bootstrap only starts BepInEx and changes nothing else.

The full story (the failing call chain, how the platform and the setter hook work, doorstop 3 and 4, and the assembly loading trap on Mono 2.6) is in [BepInEx on Mono 2.6](BepInEx-on-Mono-2.6.md).

## Multiplayer

The mod adds network data and commands, so every player in a game must run the same version of the mod.
