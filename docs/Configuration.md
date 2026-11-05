# Configuration

The first start creates `BepInEx/config/com.tricky12321.castlestoryplus.cfg`. Edit it while the game is closed, or use the in-game settings window: **Settings > Castle Story Plus settings**. Changes there are written to the config file at once; `[Features]` switches apply after a restart.

| Setting | Default | Meaning |
|---|---|---|
| `[Features]` | | one `true`/`false` switch per feature (see [Features](Features.md)) |
| `[Economy] EnergyMultiplier` | `1.3` | energy per brewed firefly, relative to the blue crystal it costs |
| `[Saving] AutosaveMinutes` | `2` | minutes of play between autosaves, `0` = off |
| `[Building] MoveStructureKey`, `CopyKey`, `PasteKey` | M, Ctrl+C, Ctrl+V | keys for moving buildings and copy/paste |
| `[Debug] DebugMode` | off | opens the debug windows (F7, F8, F9) |
| `[Debug] XRayKey` | F6 | turns the semi x-ray on and off, also without debug mode |

**Debug mode** can be switched in the settings window or with the **Enable debug mode** checkbox in the game's Options > Gameplay (title screen and in game); it applies at once. The X-ray key does not work while placing blocks (X places a block by its next cell there), in a menu or while typing.

Call to arms settings (soldiers per class) are stored in the game's own player preferences. Rally points are set in-game and last for the current map.

The full list of `[Features]` switches, and the settings that belong to each feature, are in [Features](Features.md).
