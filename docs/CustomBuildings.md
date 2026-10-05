# Adding a custom building with custom textures

How to add a new building to Castle Story from the plugin, with its own size, model and textures. The two
working examples are the market (`Market/MarketBuilding.cs`, `Market/MarketModel.cs`) and the large stockpile
(`Building/LargeStockpile.cs`). Read them alongside this guide.

## How a building exists in the game

A building is two factory templates with the same name:

| Template | Factory key | Purpose |
|---|---|---|
| Blueprint | `Blueprints/<Name>` | The ghost while placing it and the project the workers build. It holds the build cost. |
| Building | `ObjetsDynamiques/<Name>` | The finished building: its volume, storage, station and model. |

The game knows the pair through the `BlueprintMapper` (`blueprint2Concrete` / `concrete2Blueprint`). Saves and
the network refer to both templates by their factory key, so the key (`<Name>`) must never change once released.

A building cannot be built from nothing. Clone the existing building closest to the new one (the workbench for a
crafting station, the pallet for storage), then change size, model, cost and behaviour. All of the game's
components (volume, collider, recepteur, AI interest, network identity) come with the clone.

## Checklist

1. Feature constants: add `<Name>` and `<Name>Info` to `Core/Features.cs`, and put `[Feature(...)]` on every
   patch class of the building.
2. Clone the templates in a `Factory.Awake` postfix (see [Cloning](#cloning-the-templates)).
3. Set the new key on the clone's `FactoryImprint`, add it with `AddAsset<CloneTemplate>`, and register its
   network handler.
4. Blueprint: point `Blueprint.BlueprintResult` at the building key, set the build cost and add the blueprint
   mapping. Also add the `BlueprintMapper.AddMappings` skip patch.
5. Build menu entry: inject it into `LUI/Meta/Meta_Structure.lua`.
6. Size: rebuild the volume cells, collider, centre offset and fitter.
7. Model: hide the source's model, build your own meshes with normals, tangents and vertex colours, and use the
   game's shader with your textures.
8. Textures: PNGs in `CastleStoryPlus/Assets/<Name>/` (copied by the build). Generate them with a script in
   `tools/`.
9. Behaviour: if the game treats the source type specially by key (storage, stations), alias the new key there.
10. Update the README feature table and CHANGELOG (`[ADD]`). Add every new file to git.
11. Test it, then read the log lines listed under [Testing](#testing).

## Cloning the templates

```csharp
[Feature(Features.X, Features.XInfo)]
[HarmonyPatch(typeof(Factory), nameof(Factory.Awake))]
internal static class XBuilding
{
	private static void Postfix()
	{
		TryCreate("Blueprints", blueprint: true);
		TryCreate("ObjetsDynamiques", blueprint: false);
	}
}
```

- **Factories with the same name are merged.** A later factory moves its templates into the first one and
  destroys itself inside its own `Awake`. So the postfix looks the factory up in `Factory.Factories` by name, not
  through `__instance`, and runs after every `Awake`.
- If `factory.cachedTemplates.Count == 0`, call `factory.ResetTemplateCache()` first.
- Return early if `cachedTemplates` already contains `<Name>`, which happens on later `Awake` calls. Also return
  if it does not contain the source yet.
- Clone with `Object.Instantiate(factory.GetTemplate(source).gameObject, factory.transform)` and set `name`.
- The clone carries the source's `FactoryImprint`. Set `imprint.AssetKey = new Factory.AssetKey(factory.name, Name)`,
  because the template takes its key from it and saves use it.
- `Factory.AssetKey key = factory.AddAsset<CloneTemplate>(clone);`
- `Brix.Network.FactoryRegistrator.RegisterHandlerForTemplate(key);` lets clients spawn it.
- Wrap all of it in try/catch and log. An exception here stops the game's factory loading.

## Blueprint, cost and mapping

- `clone.GetComponent<Blueprint>().BlueprintResult = BuildingKey;`
- The cost is the blueprint's `Recepteur._baseCapacity` (a `Description`):
  1. Remove the source's `Ressource` entries.
  2. Add your own with `Adjectif.New(Adjectif.plankBlock.GetType(), 8)`, and so on.
  - To scale the source's cost instead, multiply `adjectif.quantifiable.valeur`.
- The building's storage is its own `Recepteur._baseCapacity`. Set `_acceptMultipleResouceType` if it should hold
  several resource types.
- Mapping: when the blueprint is created, add both directions to `BrixSingleton<BlueprintMapper>.Instance` if it
  already exists.
- Mapping patch: also add a prefix on `BlueprintMapper.AddMappings` that skips blueprints already mapped. Without
  it, the game's own mapping pass hits the duplicate key and stops loading the factories. Every building needs its
  own copy, under its own feature, so that it still works when the other features are off.

## Build menu entry

The build menu is Lua (`Info/Lua/LUI/Meta/Meta_Structure.lua`). Insert a line after an existing entry in the
group you want:

```csharp
LuaInjection.AddPatch(Features.X, "LUI/Meta/Meta_Structure.lua",
	"Hotkey = \"project_MachineShop\",\tgroupId = 3 })\n", LuaInjection.Mode.InsertAfter,
	"_t.Add(AssetKey.New(\"Blueprints\", \"X\"),\t\t{ Name = ||\"X\",\tIcon = ||IconKeys._Machine_Shop:Get64(),\tHotkey = \"\",\tgroupId = 3 })\n");
```

- Copy the anchor exactly from the file, with its tabs. `LuaInjection` handles the CRLF line endings.
- `groupId` is the build menu group: 1 = storage, 3 = crafting.
- `Icon` must be an existing `IconKeys` entry. Custom icons are not supported yet.
- For the cursor tooltip title, set `CursorTooltip.titles[BlueprintKey] = "##<loc key>"`.

## Size: volume, collider, placement

The building's footprint is not its mesh. It is the `VolumeDataComponent` cell map
(`IndexedVolumetricProperties`, cell → `VPropertyMask`):

| Mask | Meaning |
|---|---|
| `obstacle`, `occupy`, `walkable`, `debloc` | The building's body (building template). |
| `fantome` | The blueprint's body. |
| `access` | Cells beside the body where workers stand to build, work or store. |
| `mainSupportReq` | Cells under the body that must be solid ground. |
| `breakableAccess` | Blueprint only: cells under the access ring. |

To resize, first log the source cells, because the source's layout is not documented anywhere. Both examples log
`"<Name>: ... source volume x,y,z=mask ... collider ... centerOffset ... children ..."`. Then build the new map:

- the body as a box of the new size;
- the support layers under all of it;
- the access ring moved outward on each side.

Assign it with `volume._indexedVolumetricProperties = newMap;`. The market's `Resize` and the large stockpile's
`Grow` are two ways to do this.

Also update:

- `BoxCollider.center` and `size`, used for picking and hovering.
- `VoxelTransform.InitialCenterOffset`: where the pivot sits relative to a voxel. A footprint with even width
  (2 x 2) sits on a voxel corner, offset (-0.5, y, -0.5). An odd one (3 x 3) sits on a voxel centre (0, y, 0).
- The fitter (placement snapping):
  - `StockpileFitter` snaps to a voxel corner, which is right for even sizes.
  - For odd sizes, replace it with a `DefaultFitter`:
    1. `AddComponent<DefaultFitter>()`
    2. `CloneInternals(oldFitter)`
    3. `DestroyImmediate(oldFitter)`
- The hover highlight radius: `ObjectsHighlightDriver.BuildObjectDynamiquesRadius` postfix (see LargeStockpile).

## Model

1. Hide the source's model rather than deleting it, because other components may reference it. For the workbench
   that is the child `Visuals`; the building also carries a `Blueprints` child (the ghost), which must stay hidden.
2. Add your own children with a `MeshFilter` and `MeshRenderer`. Use one mesh per material/texture, merged from all
   parts with that surface (see `MarketModel`).
3. Every mesh needs:
   - **normals**: set them, or call `RecalculateNormals()`;
   - **tangents**: call `mesh.RecalculateTangents()`. The game's shader uses a normal map, and without tangents the
     model renders **completely black**;
   - **vertex colours**: set them to white with `mesh.SetColors(...)`;
   - **UVs**: in world units, one texture repeat per block, so the texture has the same scale on every part.
     `MarketModel` also turns each box's UVs so that wood grain runs along the part's long side.
4. Models in the game's prefabs are often rotated, because they were imported with z up. If you scale an existing
   child, scale the local axes that are horizontal in the parent's space, not local x/z. See
   `LargeStockpile.ScaleHorizontal`.
5. **Blueprint parts are reset by the game.** `StateRenderer.UpdateRenderersMaterial` sets
   `transform.localScale = Vector3.one * scale` on every state change, and swaps the materials of the renderers in
   its list:
   - To scale an existing blueprint part, re-apply the scale in a postfix on that method (`LargeStockpileGhostPatch`).
   - Parts you add yourself are not in its list, so they keep the material you give them. Use the source
     blueprint's ghost material for all of them.

## Materials and the game's shader

- Copy the material from the source building's model, not from the first renderer you find. The building also
  carries the ghost, whose see-through striped shader makes a finished building look unbuilt. Take it from
  `Visuals`, and skip `Blueprints`.
- The game's building shader is **`Castle Story/Wood and Metal`** (ShaderForge). It does **not** use `_MainTex`
  or `_Color`, so `material.mainTexture` has no effect. Its slots are:
  - `_Diffuse`: the colour texture. Set it with `SetTexture`, `SetTextureScale` and `SetTextureOffset("_Diffuse", ...)`.
  - `_Normals`: the normal map. The source's map is laid out for the source's UVs, so give your model a flat one:
    `Color(0.5, 0.5, 1, 0.5)`, linear. That colour reads as flat both as RGB and as DXT5nm.
  - `_Specular`, `_Gloss`, and others.
- Unity 5.6 cannot list a shader's properties. Probe them with `material.HasProperty(name)`, as
  `MarketModel.LogProperties` does. The names can also be found as strings in `Castle Story_Data/sharedassets2.assets`.
- Make one material per texture with `new Material(baseMaterial)`, and cache the materials in a static dictionary.
- **Never destroy Unity's default UI materials** (`Graphic.defaultGraphicMaterial`,
  `Canvas.GetDefaultCanvasMaterial()`). Doing so crashes the game natively. `GameLeakPatches` guards against this;
  keep that guard when touching material cleanup.

## Textures

- Put the PNGs in `CastleStoryPlus/Assets/<Name>/`. The csproj copies `Assets/**/*.png` to
  `BepInEx/plugins/CastleStoryPlus/Assets/` on build, and the installer must ship them too.
- Load them next to the plugin:
  - Path: `Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "Assets", ...)`.
  - Use `new Texture2D(2, 2, TextureFormat.ARGB32, mipmap: true)` with `LoadImage(File.ReadAllBytes(path))`.
  - Set `wrapMode = Repeat`, `filterMode = Trilinear` and `anisoLevel = 4`.
  - Fall back to a plain colour if the file is missing, and log a warning.
- Style that fits the game:
  - 128 x 128 pixels, seamless, covering one block;
  - soft painted look with low-contrast colour variation and clear shapes (boards, stones, shingles), without fine
    noise;
  - wood grain along the texture's width.
- Generate the textures with a script, so they can be tweaked and regenerated. `tools/make_market_textures.py`
  (numpy + Pillow) has seamless value noise and generators for wood, stone/brick, shingles, striped canvas, metal
  and crystal. Copy it, or add your surfaces to it. Run `python3 tools/make_market_textures.py`.
- Preview new textures for the user before building them in, for example as an artifact page.

## Behaviour by key

Much of the game checks the factory key of the source type, not a component. A clone with a new key therefore
loses that behaviour until the key is aliased. For a storage building (see `LargeStockpile`), these places check
the key:

| What | Where |
|---|---|
| Workers' storage search | `Knowledge.storageObjects` (private static set; add via reflection) |
| Pick-up index | `Knowledge.InterestingResourceObject` |
| Instance lists the game and the mod query (`AutoList` per key) | Postfixes on `AutoList.AddToAutoList` / `RemoveFromAutoList` that add the instance under the source key too |
| Storage rules (one resource type, no tools) | Transpiler on `Recepteur.RegenerateDescriptionsWithExclusions` / `DealSingleResourceRecepteur` replacing `== ObjetsDynamiques.Palette` |
| Faction storage totals | `UIGameObserver.storage.condition` (postfix on `ResetAll`) |

For a crafting station, the station component is the important part:

- Replace the source's station component with your own.
- Re-point every serialized reference to it, including those nested in serializable class fields
  (`MarketStation.PointSiblingsAtSelf`). Otherwise selecting the building throws a NullReferenceException.
- Hide it from the game's Lua crafting menu if you show your own UI (`MarketHideFromCraftingMenuPatch`).

Search `GameSource/` for the source's key (for example `ObjetsDynamiques.Palette` or `"Workbench"`) to find all
of these places.

## Saves and compatibility

- Saves store the factory key. A save containing the building can only be loaded while its feature is enabled.
  Say so in the CHANGELOG entry.
- Do not rename the key or change the source type after release.
- The cell map, cost and capacity are rebuilt at every start, so changing them later is safe for existing saves.
  Buildings that already exist simply get the new values.

## Testing

After a build, restart the game and check:

1. It is in the build menu, with name and icon.
2. The ghost has the right size and position, and snaps on the correct voxel (corner or centre).
3. Workers build it: they walk to the access cells and deliver the cost. Hovering shows the cost (BuildNeeds).
4. The finished model shows its textures and is not black, not the source's texture and not the striped ghost.
5. Its behaviour works (storage counts, station UI), and it survives saving and loading.
6. Multiplayer, if relevant: a client sees and can use it.

Log lines (`BepInEx/LogOutput.log`, plus `BepInEx/errors.log` with `[Debug] ErrorLog = true`):

- `<Name>: <template> source volume ...`: the source's cells, collider and children.
- `<Name>: added Blueprints.<Name>` and `added ObjetsDynamiques.<Name>`: both templates were registered.
- `<Name>: blueprint cost ...`, `capacity ...`.
- `Market: shader ... has ...`: which shader slots exist.
- Any `could not add ...` error, or a texture `not found` warning.
