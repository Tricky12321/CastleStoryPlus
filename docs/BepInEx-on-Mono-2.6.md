# BepInEx and HarmonyX on Castle Story's Mono 2.6

Castle Story runs on Unity's very old **Mono 2.6.5** runtime. BepInEx 5 starts on it, but out of the box HarmonyX cannot patch a single method there on Linux. This page explains why, and the small bootstrap (`CastleStoryPlus.Bootstrap`) that makes it work.

## The symptom

With a plain BepInEx 5.4.23.5 install, BepInEx loads and the plugin's `Awake` runs, but every Harmony patch fails with a `NullReferenceException` thrown in MonoMod's `NativeDetour`, reached from HarmonyX's patcher. No patch is applied, so no feature of the mod works.

## Why it fails

HarmonyX does not patch IL in place. It builds a new method and redirects the original to it with a native jump, written by **MonoMod.RuntimeDetour**. Writing that jump needs the code pages to be writable, which MonoMod does through a *native platform* (`DetourHelper.Native`, an `IDetourNativePlatform`). Two things go wrong on Castle Story:

1. **MonoMod's own platform does not work on Mono 2.6.** On Linux and macOS, MonoMod's libc platform calls `mprotect`, but it reads the page size from `Environment.SystemPageSize`. That property was added after Mono 2.6, so the platform cannot be created. MonoMod is left without a working platform.
2. **BepInEx then sets the platform to `null`.** BepInEx's terminal fix (`XTermFix`) temporarily swaps `DetourHelper.Native` to patch a terminal method, and afterwards puts back what it found. On Mono 2.6 that is nothing, so it assigns `null`. From then on every `NativeDetour` dereferences a null platform, which is the exception above.

On Windows MonoMod uses `VirtualProtect` instead, which works on Mono 2.6, so Windows does not have the first problem.

## The fix: a bootstrap in front of BepInEx

Doorstop (the injector that BepInEx ships) loads one assembly when the game starts: its `target_assembly`, normally `BepInEx/core/BepInEx.Preloader.dll`. The mod points it at its own assembly, `BepInEx/core/CastleStoryPlus.Bootstrap.dll`, which fixes MonoMod first and then starts BepInEx as usual.

```
Castle Story starts
  -> doorstop loads CastleStoryPlus.Bootstrap.dll  (target_assembly)
       1. registers an assembly resolver for BepInEx/core
       2. Linux/macOS: installs MprotectPlatform as DetourHelper.Native
       3. hooks the DetourHelper.Native setter so the platform cannot be replaced
       4. loads BepInEx.Preloader.dll and calls its entry point
  -> BepInEx starts normally (XTermFix's null assignment is ignored)
  -> CastleStoryPlus.dll loads, Harmony patches apply
```

The code is two files in `CastleStoryPlus.Bootstrap/`.

### `MprotectPlatform.cs`: a native platform that works on Mono 2.6

`MprotectPlatform` implements `IDetourNativePlatform`:

- The jump itself (`Create`, `Apply`, `Copy`, `Free`, `FlushICache`, `MemAlloc`, `MemFree`) is passed on to MonoMod's own `DetourNativeX86Platform`, which works fine on Mono 2.6.
- The memory permission calls (`MakeWritable`, `MakeExecutable`, `MakeReadWriteExecutable`) call libc's `mprotect` directly with read, write and execute. The address range is widened to whole pages first.
- The page size comes from `sysconf(_SC_PAGESIZE)` (30 on Linux, 29 on macOS) instead of `Environment.SystemPageSize`, falling back to 4096.

`Install()` sets this platform as `DetourHelper.Native`, and then hooks the property's setter with a MonoMod `Hook`. The hook (`KeepPlatform`) always passes the bootstrap's platform to the original setter, whatever value was assigned. That is what defeats XTermFix's `null`. The hook itself works because the platform is already installed when it is created.

The platform is only installed on Unix (`PlatformID` 4), macOS (6) and the value 128 that very old Mono uses for Unix. On Windows the bootstrap changes nothing and only starts BepInEx.

### `Entrypoint.cs`: starting BepInEx afterwards

- **Doorstop 3 and 4.** Doorstop 3 (BepInEx 5.4.22 and older) calls a static `Doorstop.Entrypoint.Main()`; doorstop 4 (5.4.23 and newer) calls `Start()`. The bootstrap has both. When it hands over, it looks for `BepInEx.Preloader.Entrypoint.Main` first and `Doorstop.Entrypoint.Start` second, so it works with either BepInEx version.
- **`DOORSTOP_INVOKE_DLL_PATH`.** The BepInEx preloader reads this variable to find its own folder. Doorstop set it to the bootstrap, so the bootstrap sets it to `BepInEx.Preloader.dll` before handing over.
- **One copy of every assembly.** Mono 2.6 loads a *second* copy of an assembly when `Assembly.LoadFile` is called again for the same file. If BepInEx got its own copy of `MonoMod.RuntimeDetour`, it would have a separate, empty `DetourHelper.Native`, and the fix would be lost. The bootstrap's `AssemblyResolve` handler therefore first returns an already loaded assembly with the same name, and only loads from `BepInEx/core` when there is none. The handler stays registered for the whole session.
- **MonoMod is loaded late.** `InstallNativePlatform` is a separate method marked `NoInlining`, so `MonoMod.RuntimeDetour` is only loaded when that method runs, after the resolver is registered.
- **Errors do not stop the game.** If installing the platform fails, the error is written to `CastleStoryPlus.Bootstrap.log` in the game folder and BepInEx is started anyway.

## How the bootstrap is wired up

| | Linux | Windows |
|---|---|---|
| Doorstop config | `run_bepinex.sh` | `doorstop_config.ini` |
| Setting | `target_assembly="BepInEx/core/CastleStoryPlus.Bootstrap.dll"` | `target_assembly = BepInEx\core\CastleStoryPlus.Bootstrap.dll` |
| Starting the game | Steam launch option `./run_bepinex.sh %command%` | automatic (`winhttp.dll`) |

The installers (`installer/install.sh`, `installer/install.ps1`) copy the bootstrap to `BepInEx/core/` and set `target_assembly`. Uninstalling sets it back to `BepInEx.Preloader.dll`, so BepInEx keeps working without the mod.

When building, `CastleStoryPlus.Bootstrap.csproj` references `MonoMod.RuntimeDetour.dll` from the game's `BepInEx/core/` (not copied) and copies the built bootstrap into that folder.

## Checking that it works

- `BepInEx/LogOutput.log` contains `Castle Story Plus <version> loaded (N patch classes)` with no `NativeDetour` exceptions after it.
- There is no `CastleStoryPlus.Bootstrap.log` in the game folder, or it has no new lines.
- On Linux, if the log shows BepInEx starting but every patch fails, check that `run_bepinex.sh` still points at the bootstrap. A BepInEx reinstall puts back the default `target_assembly`.

## Other things that follow from Mono 2.6

Not part of the bootstrap, but the same old runtime shapes the rest of the mod:

- **Target framework `net35`.** The plugin, the bootstrap and the developer tools are built for .NET 3.5 (`Directory.Build.props`), with a modern C# language version. Language features that need newer runtime types cannot be used.
- **Built against the original game DLLs.** The plugin references `Castle Story_Data/Managed.orig/` and uses `BepInEx.AssemblyPublicizer.MSBuild` (`Publicize="true"`), so patches can reach private members without reflection. No game code is shipped.
- **No new fields on game classes.** Harmony cannot add fields, so data per object (worker XP, call to arms role, 3x tier) lives in side tables and is saved as extra JSON properties that the unmodded game ignores.
- **Network commands registered by hand.** New UNet commands (for example the move command) are registered with `NetworkBehaviour.RegisterCommandDelegate` and a fixed hash, since the game's generated network code cannot be extended.
- **No TLS 1.2.** Mono 2.6's own HTTP stack cannot reach GitHub, so the update check uses Unity's `WWW`, which uses the engine's native networking.
- **Lua.** Much of the game's UI is MoonSharp Lua; the mod changes it with text patches at anchors in the game's Lua files, not with Harmony.
