# Castle Story Plus

Castle Story Plus runs on the Unity 6 port: Castle Story on a current Unity (6000.6), with the mod. Nothing of the game
is in this package: the port is made from your own Castle Story install the first time it starts, so you must own and
install Castle Story (Steam). The game's own files are only read; the port shares its saves and settings (`Info/`).

## Install

- Windows: double-click `install.bat`.
- Linux: `bash install.sh`. If Steam is closed, it also sets the game's launch option. Otherwise set it once yourself
  (right-click Castle Story > Properties > Launch Options): `./run_bepinex.sh %command%`

The installer finds Castle Story in your Steam libraries (or pass its folder: `--game-dir` / `-GameDir`) and puts:
- the port in the game's folder, `CastleStoryPlus-Unity6`;
- BepInEx 5's doorstop in the game's folder, if it is not there yet, pointed at `CastleStoryPlus.PortLauncher.dll`.

Play in Steam then starts the original game, whose doorstop starts the port right away and ends with it, so Steam shows
you playing Castle Story. The first start converts the game for Unity 6 (a minute or so), and again after each update.
A Castle Story Plus for the original game (0.8.0 and before) is removed: the port replaces it.

The port needs the game for its own system: the Linux version on Linux, the Windows version on Windows. If the check of
the game's files fails, verify the game's files in Steam and start again; `CastleStoryPlus-Unity6/install.log` has the
details. `BepInEx/CastleStoryPlus.PortLauncher.log` in the game's folder says how the port was started.

## Update and uninstall

The main menu tells you when a new version is out, and "Update" installs it and starts the game again. Running the
installer again does the same. `install.sh --uninstall` / `install.bat -Uninstall` removes the port and the launcher
(BepInEx and its settings stay; Castle Story then starts without the mod).

## Multiplayer

The port plays with the port, not with the original game. LAN games and typed-in addresses connect directly (port
3000); games in the online list connect through Steam, without opening a port.
