<p align="center"><img src="CastleStoryPlus/Assets/logo.png" alt="Castle Story Plus" width="480"></p>

<p align="center">
  <a href="https://github.com/Tricky12321/CastleStoryPlus/releases/latest">Download</a> ·
  <a href="CHANGELOG.md">Changelog</a> ·
  <a href="docs/Gallery.md">Gallery</a> ·
  <a href="https://github.com/Tricky12321/CastleStoryPlus/issues">Report a bug</a>
</p>

**Castle Story Plus** is a quality-of-life and gameplay mod for [Castle Story](https://store.steampowered.com/app/227860/Castle_Story/). It makes workers smarter, gives you more control over your army, adds new buildings, blocks, resources and research, and smooths out map loading and the UI. It is a [BepInEx 5](https://github.com/BepInEx/BepInEx) plugin that uses [HarmonyX](https://github.com/BepInEx/HarmonyX) to patch the game in memory at runtime, and every feature can be switched off on its own.

- **No game files are changed.** To uninstall, delete the plugin folder.
- **No game code is in this repository.** It contains only the mod's own code. Castle Story is © Sauropod Studio.
- **Saves stay compatible.** Saves made with the mod can still be loaded by the unmodded game.


## Highlights

- **Smarter workers:** they find work on their own, reserve tasks and resources, skip unreachable jobs and dig themselves out of holes.
- **Task control:** pause tasks, limit crew size, give a blueprint top priority, or send selected bricktrons straight to a task.
- **Mining and metallurgy:** coal, steel, mine shafts, ore mines and per-resource quarry limits.
- **New buildings and blocks:** warehouse, large and mixed stockpiles, market hall, smithy, armoury, research station, long bricks, wooden slabs and ladders.
- **Building tools:** copy and paste, move buildings, eyedropper and a single build task per team.
- **Research and upgrades:** weapon, armour and colony upgrades in iron, steel and crystal tiers.
- **Army:** call to arms with rally points, smarter archers, retreating and healing fighters, critical hits and a new healing enemy.
- **Experience:** separate work and combat XP with levels, and giant bricktrons.
- **UI:** resource list, terrain minimap with zoom, world search, wave warnings and C# game menus.
- **Saving and performance:** autosave, non-freezing saves, faster loading, more frames per second and fixes for the game's memory leaks.
- **Every feature can be switched off** in the config file or the in-game settings.

## Install

Castle Story Plus runs on the Unity 6 port: Castle Story on a current Unity, made from your own Castle Story install the first time it starts (you must own the game on Steam; nothing of the game is in the packages). Download the package for your system from the [latest release](https://github.com/Tricky12321/CastleStoryPlus/releases/latest). Each package is complete: installer, the port with Castle Story Plus and BepInEx 5.4.23.5.

| System | Package | How |
|---|---|---|
| Windows | `CastleStoryPlus-<version>-windows.zip` | Unpack it and double-click `install.bat`. |
| Linux | `CastleStoryPlus-<version>-linux.zip` | Unpack it and run `bash install.sh`. |

The installer finds Castle Story in your Steam libraries and puts the port in its folder; Play in Steam then starts the port. Run it again at any time to update; the game also offers updates from the main menu, and Castle Story Plus 0.8.0 and older move over to the port that way. In multiplayer every player needs the same version.

> **Linux launch option:** the game needs the Steam launch option `./run_bepinex.sh %command%`. If Steam is closed while the installer runs, it sets the option itself. Otherwise set it once yourself: right-click Castle Story > Properties > Launch Options.

One-line installers, installer options, updates and manual installation: see [Installation](docs/Installation.md).

## Documentation

| Page | Contents |
|---|---|
| [Features](docs/Features.md) | every feature per area, with its `[Features]` switch |
| [Installation](docs/Installation.md) | installers and options, updates, manual installation, why a bootstrap, multiplayer |
| [Configuration](docs/Configuration.md) | the config file, in-game settings and debug mode |
| [Gallery](docs/Gallery.md) | pictures of every building, block, armour stand and stockpile |
| [Contributing](docs/Contributing.md) | reporting bugs, pull requests and the roadmap |
| [Contributors](CONTRIBUTORS.md) | everyone who has contributed |
| [Roadmap](docs/Roadmap.md) | features planned over time: animals (taming, stables, training) and portals |
| [Development](docs/Development.md) | building from source, testing, releasing, project layout, how it works |
| [DevTools](docs/DevTools.md) | the developer plugin, HTTP API, in-game tests and MCP server |
| [Performance](docs/Performance.md) | measuring frame time and memory, what was found and changed |
| [Lua memory leaks](docs/LuaMemoryLeaks.md) | how the leaks of the game's Lua interface were found and fixed |
| [Custom buildings](docs/CustomBuildings.md) | adding a building with its own model and textures |
| [Generated structures](docs/GeneratedStructures.md) | rules for structures the mod builds from blocks: 1x2 bricks in a bond, crust, building order |
| [Guardians](docs/Guardians.md) | rules for the treasure guardians: how they look, model, icon, gait and attack |
| [BepInEx on Mono 2.6](docs/BepInEx-on-Mono-2.6.md) | why and how the bootstrap makes HarmonyX work on the game's runtime |
| [Changelog](CHANGELOG.md) | all changes |

## License

Castle Story Plus is free software under the [GNU General Public License v3](LICENSE) (or any later version): you may
use, copy, change and share it, and whoever shares a changed version must share its full source code under the same
license and keep the copyright notice ([NOTICE](NOTICE)). It covers only the mod's own code, not Castle Story itself,
which belongs to Sauropod Studio; [NOTICE](NOTICE) also allows combining the mod with the game, Unity and BepInEx.

---

Unofficial fan mod, not affiliated with Sauropod Studio. Use it at your own risk and back up your saves.
