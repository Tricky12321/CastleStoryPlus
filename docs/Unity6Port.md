# Unity 6 port

Castle Story on Unity 6 (6000.6), without distributing anything of the game. You must own and install the original
game: the port is made from that install on your own machine, and does not run without it.

## What is distributed

Only our own code and files Unity or we generate. Nothing taken or derived from the game goes in the repository or in
a release: no assets, no code, no shaders, and not the game's project settings either.

- `CastleStoryPlus.Player`: a Unity 6 project. Its only scene is an empty bootstrap scene. It holds our own code:
  - Bootstrap: loads the converted content, starts BepInEx's plugins (`ModLoader`) and the game's first scene.
  - Compat (`CastleStoryPlus.Compat.dll`): engine API that Unity 6 dropped and the game uses, written from scratch.
    This covers UNet's low-level transport (no sockets), legacy `Network`/`BitStream`, `GUIText`/`GUITexture`, and
    redirects for changed members.
  - `ProjectSettings`: Unity's defaults, plus our build settings in `PlayerBuild` (Mono, linear color space; OpenGL
    Core on Linux, `PlayerBuild.Linux`, and Direct3D 11 on Windows, `PlayerBuild.Windows`). The build also writes `engine-types.assets`, Unity's default settings and a shader of our own
    (`Assets/Types/EngineTypes.shader`) in the player's own format. The installer takes their type trees and the
    compiled shader format from it.
  - `Assets/Dev`: probe shaders for comparing Unity 6's compiled shader format (`ShaderProbe.Build` for Linux,
    `ShaderProbe.BuildWindows` for Windows; development only).
- `CastleStoryPlus.Port`: the converter (netstandard2.0, runs inside the player or via `CastleStoryPlus.Port.Cli`).
- `CastleStoryPlus.Port/Data/game-files.sha256`: SHA-256, size and name of every file of a clean `Castle Story_Data`
  (Steam build); `Port.Cli manifest` makes it from a clean install. `game-files-windows.sha256` is the same for the
  Windows build, whose data files, plugins and Wwise banks differ (the game's own assemblies are the same). Hashes
  only, nothing of the files.
- Not in git (`.gitignore`), kept locally in `CastleStoryPlus.Port/Data`: `classdata.tpk`, Unity type trees (UABEA
  class database). It goes into the release.

## What the install step does (`Installer`)

1. Checks the install against `game-files.sha256`. It stops if any file is missing or different.
2. Patches the game's assemblies with Cecil into the player's `Managed` folder (`CodePatcher`). It also registers
   them in `ScriptingAssemblies.json` and records them in `game-assemblies.txt`. The patches:
   - Engine types Unity 6 no longer has move to `CastleStoryPlus.Compat`, under the same names.
   - `PhysicMaterial` is renamed to `PhysicsMaterial`.
   - Changed calls are redirected to `ImageConversion` or `Compat.Redirects`: `LoadScene(int)`, `Scene.buildIndex`,
     `InputField.onEndEdit`, `Camera.hdr`, `DrawProceduralIndirect`.
   - `GameObject.AddComponent` (generic and by type) goes to `Redirects.GameObject_AddComponent`, which first adds the
     components the script's `[RequireComponent]` asks for. Unity 6's player does not do it for the game's scripts,
     and the game's placed-block templates (made at load time) then had no `NetworkIdentity`. A generic method is
     redirected for every type argument through `GenericRedirects`.
   - Two string literals are changed: `MonoMethod` and `Brix.Common`.
   - Smooth's generic T/int unions get a sequential layout and compare through `EqualityComparer<T>.Default`.
   - Calls redirected inside one method only (`ScopedRedirects`):
     - `ScreenSignaler` gets frustum planes from `GeometryUtility.CalculateFrustumPlanes`, not the internal
       `Internal_ExtractPlanes`.
     - Brix's `UnityLogAppender` logs through `Debug.LogFormat`, not the internal `DebugLogHandler.Internal_Log`.
     - `LuaProperty` compiles the property accessors of structs. Current Mono refuses open delegates on a struct's
       instance method.
     - `LuaDriverHQ` scans the `UnityEngine.*Module` assemblies for component types, because `UnityEngine.dll` only
       forwards to them.
     - `CloneTemplate` clones through `Redirects.CloneTemplate_Instantiate`, which puts `NestedCanvasRect` on every
       canvas in the clone. Unity 6 sizes a root screen-space canvas to the screen as soon as it is enabled, and the
       game moves the clone under its canvas only after that. The guard puts the template's layout back once the
       canvas is nested; without it, the small arrows of the bricktrons' name tags covered the screen.
   - Methods that worker threads run at the same time are made synchronized (`Synchronized`):
     `FlatWorld.Common.GetProperlySizedNoiseTexColors`, whose Dictionary the distant terrain's drawers fill. The
     current Mono's Dictionary throws where Mono 2.6's got away with it.
3. Copies the game's project settings into the player's `globalgamemanagers` (`GameSettings`). This covers tags and
   layers, physics, 2D physics, time, input, quality, audio and nav mesh areas. Fields are matched by name. The
   player's layouts come from `settings-types.assets`, and its own file is kept as `globalgamemanagers.player`, so
   installing again starts from it.
4. Copies `Plugins` (Wwise, Steam) and `StreamingAssets`.
5. Converts the data files into asset bundles in `Content/` (`DataConverter`, `BundleConverter`):
   - `levelN` and `sharedassetsN.assets` become the scene bundle `BuildPlayer-<scene>`.
   - `resources.assets` becomes `CAB-resources` and `globalgamemanagers.assets` becomes `CAB-globalgamemanagers`.
   - Every type gets its 5.6 type tree, and MonoBehaviours get theirs from the game's assemblies. Unity 6 reads the
     objects field by field and upgrades them.
   - A script's `Matrix4x4` field is written as Unity's `Matrix4x4f` (floats `e00` to `e33`, row by row; `TypeTrees`).
     The template generator names it after the C# fields (`m00`, ...), which Unity 6 does not match, so the grip and
     socket matrices of what units carry and hold stayed identity.
   - External references become `archive:/` paths, and `.resS` data moves into the bundles.
   - MonoScript assembly names lose `.dll`.
   - Animation clips: a transform's Euler rotation binding gets custom type 4 (5.6 wrote 14, which Unity 6 does not
     bind; the sky's sun and moon orbits are such clips).
   - The AssetBundle object goes at path id 1 in the first file of the bundle. For resources it goes in a small
     index file.
   - Preload tables come from PreloadData, or from walking each asset's PPtr references.
   - Shaders are converted to Unity 6's format (`ShaderConverter`), keeping the programs for the player's graphics
     APIs (read from `engine-types.assets`): OpenGL Core from a Linux install, Direct3D 11 from a Windows install,
     and Vulkan as well when the player was built with it (see below).
     Both versions keep a GL program as GLSL (hlslcc output) and a Direct3D 11 program as DXBC, so the code is taken
     as it is. Each program gets the new format version and its vertex channels in Unity 6's order.
     - GL: all of a shader's programs share the empty parameter entry. 5.6's GL programs list no parameters, and the
       engine looks the uniforms up in the linked GLSL. Unity 6 keeps the whole GL program with the vertex stage.
     - Direct3D 11: the bytecode's header grows from 6 to 38 bytes (format 2, the same texture, constant buffer and
       sampler counts). Each program also lists the input each vertex channel feeds (position 0, normal 1, tangent 2,
       color 3, uvN 5+N). 5.6 keeps the parameters after the code (groups per constant buffer with each uniform's
       type, size and offset; textures with slot, sampler slot and dimension; constant buffers and structured
       buffers with their slots). Unity 6 keeps them in a parameter entry of their own, which programs with the same
       parameters share. Shader Model 5 programs (the eye adaptation) get Unity 6's Shader Model 5 requirements.
     - In the parsed form the variants become player subprograms, with indices into the shader's own keyword list.
     - A player built with more than one API (`PlayerBuild.VulkanTestLinux`: Vulkan and OpenGL Core) gets each
       shader's programs for all of them, laid out as Unity 6 lays out its own shader in that player's
       `engine-types.assets`: `platforms`, `offsets`, `compressedLengths`, `decompressedLengths` and `stageCounts` have
       an entry per platform in the player's order (15 GL Core, 18 Vulkan; each one blob, compressed one after the
       other), and each program's player subprograms list the GL ones first, then the Vulkan ones, in the same tier;
       blob and parameter indices point into the subprogram's own platform blob. Unity takes the programs of the API
       it runs on, so one install starts with `-force-glcore` as well as `-force-vulkan`. A shader whose Vulkan programs
       cannot all be made (a compile failure, or a GL variant without a Vulkan one) keeps its GL programs only; the
       install log says so per shader.
     - Vulkan (experimental, a player built by `PlayerBuild.VulkanTestLinux`): the Linux 5.6 data
       also holds SPIR-V (stored as SMOL-V) for 144 of its 179 shaders. These programs are repacked: the code header
       grows from 0x2C to 0xB0 bytes (taken from the player's own Vulkan program), the SMOL-V streams are copied as
       they are, vertex inputs move up by one and the parameters go through the Direct3D 11 path with their binding
       slots. On Vulkan Unity records the draw calls on worker threads (native graphics jobs), which OpenGL Core cannot.
       Unity 6's code header also holds the render targets the fragment stage writes (offset 0x4C: 1 for one, 15 for
       the deferred G-buffer's four); 5.6 does not record it, so it is read from the same variant's GL program.
     - The other 35 shaders (Shader Forge, rock, wood, metal, bricktron face, crystal, sky) have no SPIR-V in 5.6. Their
       GL programs are rewritten to Vulkan GLSL (`GlslToVulkan`: version 450, uniforms in std140 blocks in set 1,
       textures in set 0, explicit locations, the depth range and the y flip of Unity's own Vulkan programs), compiled
       with shaderc (`Shaderc`, native library shipped in `Installer/`) and stored as SMOL-V (`SmolV`). The install
       log lists per shader what each platform got (GL programs; Vulkan programs reused, compiled and failed).
       `CSP_VULKAN_GLSL=<regex>` sends matching shaders through the compiler even when 5.6 has SPIR-V for them
       (debugging).
       Vulkan stays opt-in until these are fixed:
       - Converted forward shaders other than the crystals (glass, sky, bricktron face) are not checked against
         OpenGL yet. The crystals were fixed on 31-10-2026: the converted vertex stage now takes its clip position from
         Unity's own Vulkan matrix (the OpenGL round trip `w - 2z` and back lost precision at world coordinates around
         512, and whole triangles failed the depth test against the depth written before the forward pass), and forward
         passes move their depth 0.01 % nearer (found by testing; G-buffer passes are left alone). The vertex code of
         these shaders runs twice (once for the position, once for fog and the other outputs the OpenGL way).
       - Shadow bias of the compiled shaders is not checked yet.
       - Not yet faster than OpenGL Core: about even with native graphics jobs in the initial benchmark (see
         [Performance](Performance.md#vulkan-initial-benchmark-31-10-2026)).
       - The shaderc licence text is not in the port release yet.
   - The Windows game keeps its 64-bit plugins directly in `Plugins`; they are copied to `Plugins/x86_64`, where Unity
     6's Windows player loads them.
   - `catalog.txt` lists the bundles, the scenes in the game's build order and the Resources paths. `game.txt` holds
     the game's folder. `graphics.txt` lists, a line each, the graphics APIs the converted shaders have programs for,
     with Unity's `GraphicsDeviceType` names (`OpenGLCore`, `Vulkan`, `Direct3D11`); Vulkan only when every shader got
     its Vulkan programs (otherwise the install log says which did not). The mod reads it to offer the renderer
     choice. `installed.sha256` lists the converted files.

At start the player loads every bundle and every MonoScript, as the 5.6 player did; without that the scripts do not
bind. It routes `Resources.Load` and `Shader.Find` to the bundles (`ContentResources`). `UNetUpdate` runs UNet's message pump
(`NetworkIdentity.UNetStaticUpdate`) at the start of Update, as the 5.6 engine did; without it a local game never
starts. It runs in the game's own
folder, so `Info/` (saves, settings, Lua, maps) is shared with the original game.

## First start (`InstallerScreen`)

A player without complete content starts its installer screen instead of the game. Complete means that `Content`
holds every file `installed.sha256` lists, at its size. `ScriptingAssemblies.json` must also register the game's
assemblies; a new player unpacked over an old one has its own list. The screen:
- finds the game in Steam's libraries (`GameLocator`): `libraryfolders.vdf` under the usual Steam folders on Linux
  (with Flatpak), or under the registry's `SteamPath` and `Program Files (x86)\Steam` on Windows. The folder can also be
  typed in, either the game's folder or its `Castle Story_Data`.
- runs `CastleStoryPlus.Port`'s `Installer` on a worker thread, with a progress bar and the last lines of its log.
  The full log goes to `install.log` next to the player. The installer and its libraries (AssetsTools.NET, Cecil)
  are kept in `Installer/` next to the player, together with `classdata.tpk` and the manifest for the player's
  platform. `tools/player-installer.sh <player build dir>` puts them there. They run in an AppDomain of their own,
  based in `Installer/` (`InstallerDomain`). In the player's own domain BepInEx has already loaded its Cecil (0.10),
  which the installer (built against 0.11) would bind to, and then it could not find AssetsTools.NET.
- starts the player again when it is done, because the game's assemblies and settings are read at start. doorstop's
  state variables (`DOORSTOP_INITIALIZED`, `DOORSTOP_DISABLE` on Windows, ...) are removed from the new process's
  environment, so BepInEx starts in it too.
- shows what is wrong when the install fails. A wrong or changed game file lists the files, and a Windows install
  given to the Linux player (or the other way round) fails there too.

The installer runs inside the player, which has its own `globalgamemanagers` open, and on a second install the game's
assemblies as well. It never writes over a file. It writes next to the file and renames the result into place
(`Installer.MoveInto`), and leaves a file alone when it is already the same. Windows does not let a loaded assembly be
deleted or renamed, but the patched assemblies come out byte for byte the same on every install. `CodePatcher` always
resolves the game's own assemblies from the game's install, never from an earlier install's patched copies, so on
Windows only a changed file is replaced. A file that is in the way is renamed to a unique `.old` name, which the next
install deletes.

## Development

    # player (Unity editor closed; output CastleStoryPlus.Player/Build/Linux)
    ~/Unity/Hub/Editor/6000.6.7f1/Editor/Unity -batchmode -quit -projectPath CastleStoryPlus.Player \
      -executeMethod CastleStoryPlus.Player.Editor.PlayerBuild.Linux -logFile build.log
    # the first-start installer (Installer/ in the build)
    tools/player-installer.sh CastleStoryPlus.Player/Build/Linux
    # or install from the command line, from the Steam install
    dotnet run --project CastleStoryPlus.Port.Cli -c Release -- install CastleStoryPlus.Port/Data/classdata.tpk \
      CastleStoryPlus.Port/Data/game-files.sha256 "<Castle Story>/Castle Story_Data" \
      CastleStoryPlus.Player/Build/Linux/CastleStoryPlus_Data CastleStoryPlus.Player/Build/Linux/Content

For measuring the port without the editor, `PlayerBuild.ProfilingLinux` builds a development player into
`Build/Profiling/Linux` (an empty folder each time); install into it and add BepInEx as above, then attach the
editor's Profiler to it (Profiler window, target list).

A release is made from a fresh player build, never from a folder an install has run in: after an install that
folder holds the game's code, settings and content.

## Release

`tools/package-port.sh [linux|windows]` makes `dist/CastleStoryPlus-Port-<version>-<os>.zip`, one per system. The
Unity editor must be closed, and the development player of that system (`Build/<Linux|Windows>`) must have been
installed into, because the mod is built against the game code patched there. Each zip holds:
- a player built into an empty folder (`PlayerBuild.ReleaseLinux` / `ReleaseWindows`, output `Build/Release/<os>`);
- the first-start installer (`Installer/`: `CastleStoryPlus.Port`, its libraries, `classdata.tpk`, the system's
  manifest);
- BepInEx 5.4.23.5 from its release (cached in `dist/cache`), set up by `tools/player-bepinex.sh`;
- the mod (`BepInEx/plugins/CastleStoryPlus`: plugin, UI assembly, Lua, Assets), without DevTools;
- `README.md` (`installer/README-Port.md`): start with `run_bepinex.sh` on Linux and `CastleStoryPlus.exe` on Windows.

The script stops if the folder holds anything of the game (`Content`, `Info`, the game's assemblies, an install's
leftovers) or anything for development only. The mod's update check is off in the port: its releases install the mod
for the original game.

Other `Port.Cli` commands:
- `tree`, `blob`, `scripts`: look at one object's fields, a shader's decompressed program blob (`*` for every
  shader, a folder each), the scripts on game objects of a name.
- `bindings`: how often each animation binding type (type, custom type, transform attribute) occurs in a data folder.
- `objects`, `names`: in a converted bundle, the game objects whose name holds a text with their components and
  scripts; the names of the objects of a class.
- `settings`: only the project settings step.
- `manifest`: hashes of a clean install.
- `verify`: checks an install against a manifest.
- `scan`: references the game's assemblies make that the target runtime lacks.
- `inspect`, `fields`, `dump`: look at serialized files.

Environment variables for the player:
- `CSP_DIAG=1`: diagnostics.
- `CSP_SHOT_DIR`: screenshots at 20, 35 and 50 s.
- `CSP_GAME_ROOT`: run in another folder, such as a copy of `Info` away from the real saves.
- `CSP_SCENE`: first scene.
- `CSP_PLAYTEST=<map>`: starts a single-player sandbox on that map (for example `TestIsland`) from the title screen.
  It logs the start state every 10 s. Once the level loads it logs the shaders and cameras. With `CSP_SHOT_DIR` it
  takes screenshots 30, 60 and 90 s after the level loads, plus the main camera alone in deferred and forward. Those
  camera shots have no sky (black on Direct3D 11): the sky is the skybox scene's camera, drawn before the main camera,
  which only clears depth. After the level loads it also logs the post-processing every 10 s: the profile's effects
  that are on (fog and color grading; eye adaptation is off), whether `EyeHistogram` loads with its kernel, and the
  auto exposure.
- `CSP_PLAYTEST` also closes the game mode's introduction (the Lua hook `UIGame.hk_CloseModalMenu`), and keeps the
  main camera where the level started it (the game scrolls at the screen's edge, and the mouse is elsewhere).
  `CSP_PLAYTEST_VIEW=horizon` moves that view low over the ground, toward the horizon; `CSP_PLAYTEST_FORWARD=1`
  renders it forward instead of deferred. At the first screenshot it logs the visible renderers and what lies under
  two screen points.
- `CSP_EFFECTS_TEST=1` (with `CSP_PLAYTEST`): a screenshot with the main camera's image effects off, then one per
  effect turned back on.
- `CSP_CONTENT`: content folder (an empty folder starts the installer screen).
- `CSP_GAME_DATA`: the game install the installer screen offers, before the Steam libraries.
- `CSP_AUTO_INSTALL=1`: the installer screen installs from the game it found without a click.

Environment variable for the installer (debugging): `CSP_SHADER_SKIP=<regex>` leaves the programs out of the shaders
whose name it matches, to find a shader that breaks the player.

The Windows player is tested under Proton (DXVK): `proton waitforexitandrun CastleStoryPlus.exe`, with
`STEAM_COMPAT_DATA_PATH` set to a prefix of its own.

## The mod in the port

BepInEx 5 (5.4.23.5, doorstop 4.5) runs in the Unity 6 player as it is; the player's current Mono needs none of the
Mono 2.6 workarounds of `CastleStoryPlus.Bootstrap`.
- `tools/player-bepinex.sh <player build dir> <BepInEx install dir>` puts BepInEx's core and doorstop into a player
  build (on Linux `run_bepinex.sh`, which starts `CastleStoryPlus.x86_64`; on Windows `winhttp.dll` and
  `doorstop_config.ini`). It sets BepInEx's entry point to the player's `ModLoader`. BepInEx's default entry point
  (Unity's `Application`) runs before the game's folder is the working directory, and so early that a plugin adding
  components crashes Unity 6. `Bootstrap` starts `ModLoader` once the game's content is loaded, before the game's first
  scene.
- `dotnet build <project> -p:UnityTarget=player` builds the mod (CastleStoryPlus, UI, DevTools) against the player's
  `Managed` folder after an install: the game code the installer patched, Unity 6's engine modules and
  `CastleStoryPlus.Compat`. It defines `UNITY6` and `UNITY6_PLAYER`, and installs into the player's `BepInEx/plugins`.
  `-p:PlayerPlatform=Windows` builds against `Build/Windows`.
- The mod finds the game's `Info` folder through `UnityCompat.GameFolder`: in the port that is the working directory
  (the game's folder), not BepInEx's game root (the player's folder).
- Under Proton, doorstop needs `WINEDLLOVERRIDES="winhttp=n,b"`.
- DevTools has no allocation endpoints (`/api/allocs`) in the port, because their profiler is `CastleStoryPlus.Bootstrap`'s.
- Fixes for Unity 6 behaviour inside the mod (`CastleStoryPlus/Core`, `#if UNITY6`): `HeightmapBoundsFix` (a map's
  heightmap has no mipmaps, so the new game screen could not read its bounds) and `TargetMatchFix` (units get an
  empty `OnAnimatorMove`, so Unity 6 runs their target matching: climbing up and stepping down a block).
  `TerrainEmptyColliderFix` (`Core/TerrainLodFix.cs`): Unity 6 logs an error for a MeshCollider whose mesh has no
  vertices (5.6 was silent), so terrain sections without solid blocks get no collider mesh instead of an empty one.
  `DuplicateEventSystemFix` (`Core/NestedCanvasFix.cs`) now runs in the player too: the game scene has two
  EventSystems, and the second is switched off.

## Navigation mesh experiment

`[Pathfinding] NavMeshExperiment` (off by default) builds Unity's navigation mesh from the game's route graph on
Unity's worker threads and keeps it up to date, to measure it against the game's A\*; see [NavMesh](NavMesh.md).

## Not done yet

- **Compute shader:** the game's one compute shader (`EyeHistogram`) loads with its kernel, but it has not been seen
  running: the eye adaptation that uses it is off in the game's profile.
- **Log noise** that the original game has too: GameAnalytics, which fails to reach its server and, when the game
  quits, its `GA_Settings`. (The missing scripts on the factory templates, `StorageLocator` and others, are left out
  by the converter since 31-10-2026; content converted before that still warns.)
- **Audio** (Wwise 2015; it starts under Windows/Proton), and a test on a real Windows machine.
- **Publishing:** `tools/release.sh` does not upload the port's zips yet.
