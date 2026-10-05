using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Brix.Components;
using Brix.Engine;
using Brix.External.Factories;
using Brix.External.Factories.Templates;
using Brix.Game.AI;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using Brix.Game.Semantique;
using Brix.Game.Storage;
using Brix.Game.Utils;
using Brix.Lifecycle.Pooling;
using Brix.NewUI.Tooltips;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CastleStoryPlus.Building;

// Large stockpile: a 3 x 3 pallet holding three times as much as the game's 2 x 2 one. Added to the game's
// factories when they load, cloned from the pallet (blueprint Blueprints/Pallet, building ObjetsDynamiques/Palette)
// and grown to 3 x 3 around its centre voxel. It has its own key (saves and the network need one template per key),
// so everywhere the game finds stockpiles by the pallet's key it is added as one: the instance lists, the workers'
// storage search, the faction storage count, the "accepts one resource" rule and the hover circle.
[Feature(Features.LargeStockpile, Features.LargeStockpileInfo)]
[HarmonyPatch(typeof(Factory), nameof(Factory.Awake))]
internal static class LargeStockpile
{
	private const string BlueprintSource = "Pallet";

	private const string BuildingSource = "Palette";

	internal const string Name = "LargePalette";

	private const int Size = 3;

	private const int CapacityMultiplier = 3;

	private const int CostMultiplier = 2;

	// How much wider the model is than the pallet's (3 / 2), set when the templates are grown.
	internal static float ModelScale = 1f;

	internal static readonly Factory.AssetKey BlueprintKey = new Factory.AssetKey("Blueprints", Name);

	internal static readonly Factory.AssetKey BuildingKey = new Factory.AssetKey("ObjetsDynamiques", Name);

	private static void Enable()
	{
		LuaInjection.AddPatch(Features.LargeStockpile, "LUI/Meta/Meta_Structure.lua", "Hotkey = \"project_Stockpile\",\t\tgroupId = 1 })\n", LuaInjection.Mode.InsertAfter,
			"_t.Add(AssetKey.New(\"Blueprints\", \"" + Name + "\"),\t\t{ Name = ||\"Large stockpile\",\t\tIcon = ||IconKeys._Stockpiled_Brick:Get64(),\tHotkey = \"\",\t\tgroupId = 1 })\n");
		// The workers' storage search and pick-up index only look at these keys.
		HashSet<Factory.AssetKey> storage = (HashSet<Factory.AssetKey>)AccessTools.Field(typeof(Knowledge), "storageObjects").GetValue(null);
		storage.Add(BuildingKey);
		Knowledge.InterestingResourceObject.Add(BuildingKey);
		CursorTooltip.titles[BlueprintKey] = "##cursortip_stockpile";
	}

	internal static bool IsStockpileKey(Factory.AssetKey key)
	{
		return key == ObjetsDynamiques.Palette || key == BuildingKey;
	}

	internal static bool IsLarge(Component component)
	{
		FactoryImprint imprint = (component != null) ? component.GetComponent<FactoryImprint>() : null;
		return imprint != null && imprint.AssetKey == BuildingKey;
	}

	// Factories with the same name are merged: a later one moves its templates into the first and is destroyed
	// inside its own Awake. So look at the registered factories (not the instance) after every Awake.
	private static void Postfix()
	{
		TryCreate(BlueprintKey.Factory, BlueprintSource, blueprint: true);
		TryCreate(BuildingKey.Factory, BuildingSource, blueprint: false);
	}

	private static void TryCreate(string name, string source, bool blueprint)
	{
		if (!Factory.Factories.TryGetValue(name, out Factory factory) || factory == null)
		{
			return;
		}
		try
		{
			if (factory.cachedTemplates.Count == 0)
			{
				factory.ResetTemplateCache();
			}
			if (factory.cachedTemplates.ContainsKey(Name) || !factory.cachedTemplates.ContainsKey(source))
			{
				return;
			}
			Create(factory, source, blueprint);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("LargeStockpile: could not add the large stockpile to factory " + name + ": " + ex);
		}
	}

	private static void Create(Factory factory, string source, bool blueprint)
	{
		GameObject template = factory.GetTemplate(source).gameObject;
		GameObject large = Object.Instantiate(template, factory.transform);
		large.name = Name;
		Grow(large);
		ReplaceFitter(large);
		Recepteur recepteur = large.GetComponent<Recepteur>();
		if (blueprint)
		{
			large.GetComponent<Blueprint>().BlueprintResult = BuildingKey;
			Multiply(recepteur, CostMultiplier, "blueprint cost");
		}
		else
		{
			Multiply(recepteur, CapacityMultiplier, "capacity");
			ScaleDisplayThresholds(large);
		}
		FactoryImprint imprint = large.GetComponent<FactoryImprint>();
		if (imprint != null)
		{
			imprint.AssetKey = new Factory.AssetKey(factory.name, Name);
		}
		Factory.AssetKey key = factory.AddAsset<CloneTemplate>(large);
		Brix.Network.FactoryRegistrator.RegisterHandlerForTemplate(key);
		if (blueprint)
		{
			AddBlueprintMapping();
		}
		Plugin.Log.LogInfo("LargeStockpile: added " + key);
	}

	// Grows the pallet's volume, collider and model from 2 x 2 to 3 x 3. The 2 x 2 pallet sits on the corner between
	// four voxels; the large one sits on the centre voxel of its 3 x 3, so its pivot moves to that voxel's centre.
	// Each cell of the new volume copies the old cell in the same place relative to the body: inside the body, a
	// body cell; around it (where workers stand), the cell beside the old body on the same side; below it, the
	// support layers.
	private static void Grow(GameObject go)
	{
		const long body = (long)(VPropertyMask.obstacle | VPropertyMask.fantome);
		VolumeDataComponent volume = go.GetComponent<VolumeDataComponent>();
		BoxCollider collider = go.GetComponent<BoxCollider>();
		VoxelTransform voxelTransform = go.GetComponent<VoxelTransform>();
		Dictionary<XYZ, long> cells = volume.IndexedVolumetricProperties;
		List<string> dump = new List<string>();
		int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
		int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
		foreach (KeyValuePair<XYZ, long> cell in cells)
		{
			dump.Add(cell.Key.x + "," + cell.Key.y + "," + cell.Key.z + "=" + (VPropertyMask)cell.Value);
			if ((cell.Value & body) == 0)
			{
				continue;
			}
			minX = Mathf.Min(minX, cell.Key.x);
			minY = Mathf.Min(minY, cell.Key.y);
			minZ = Mathf.Min(minZ, cell.Key.z);
			maxX = Mathf.Max(maxX, cell.Key.x);
			maxY = Mathf.Max(maxY, cell.Key.y);
			maxZ = Mathf.Max(maxZ, cell.Key.z);
		}
		Plugin.Log.LogInfo("LargeStockpile: " + go.name + " source volume " + string.Join(" ", dump.ToArray())
			+ (collider != null ? (" collider " + collider.center + " " + collider.size) : string.Empty)
			+ (voxelTransform != null ? (" centerOffset " + voxelTransform.InitialCenterOffset) : string.Empty)
			+ " children " + ChildNames(go.transform));
		if (minX == int.MaxValue)
		{
			Plugin.Log.LogWarning("LargeStockpile: no body cells in the source volume, size unchanged");
			return;
		}
		// Where the old body's centre was, in the object's own space: the model is moved from there to the pivot.
		Vector3 oldCentre = (collider != null) ? new Vector3(collider.center.x, 0f, collider.center.z) : Vector3.zero;
		int half = Size / 2;
		IndexedVolumetricProperties grown = new IndexedVolumetricProperties();
		HashSet<int> levels = new HashSet<int>();
		foreach (KeyValuePair<XYZ, long> cell in cells)
		{
			levels.Add(cell.Key.y);
		}
		for (int x = -half - 1; x <= half + 1; x++)
		{
			for (int z = -half - 1; z <= half + 1; z++)
			{
				int fromX = (x < -half) ? minX - 1 : ((x > half) ? maxX + 1 : minX);
				int fromZ = (z < -half) ? minZ - 1 : ((z > half) ? maxZ + 1 : minZ);
				foreach (int y in levels)
				{
					if (cells.TryGetValue(new XYZ(fromX, y, fromZ), out long mask))
					{
						grown[new XYZ(x, y, z)] = mask;
					}
				}
			}
		}
		volume._indexedVolumetricProperties = grown;
		float scale = Size / (float)(maxX - minX + 1);
		ModelScale = scale;
		if (voxelTransform != null)
		{
			Vector3 offset = voxelTransform.InitialCenterOffset;
			voxelTransform.InitialCenterOffset = new Vector3(0f, offset.y, 0f);
		}
		if (collider != null)
		{
			collider.center = new Vector3(0f, collider.center.y, 0f);
			collider.size = new Vector3(collider.size.x * scale, collider.size.y, collider.size.z * scale);
		}
		// The model (pallet boards and the resource piles shown on it) is spread over the larger area.
		foreach (Transform child in go.transform)
		{
			Vector3 position = child.localPosition - oldCentre;
			child.localPosition = new Vector3(position.x * scale, position.y, position.z * scale);
			ScaleHorizontal(child, scale);
		}
	}

	// Scales the local axes that lie flat in the parent's space. The models are rotated (imported with z up),
	// so local x and z are not always the horizontal ones.
	internal static void ScaleHorizontal(Transform transform, float factor)
	{
		Vector3 size = transform.localScale;
		Quaternion rotation = transform.localRotation;
		transform.localScale = new Vector3(
			size.x * Factor(rotation * Vector3.right, factor),
			size.y * Factor(rotation * Vector3.up, factor),
			size.z * Factor(rotation * Vector3.forward, factor));
	}

	private static float Factor(Vector3 axis, float factor)
	{
		return Mathf.Abs(axis.y) > 0.5f ? 1f : factor;
	}

	private static string ChildNames(Transform root)
	{
		List<string> names = new List<string>();
		foreach (Transform child in root)
		{
			names.Add(child.name + " " + child.localPosition);
		}
		return string.Join(", ", names.ToArray());
	}

	// The pallet's fitter snaps it onto a voxel corner, right for 2 x 2; the large one is placed centred on a voxel.
	private static void ReplaceFitter(GameObject go)
	{
		StockpileFitter fitter = go.GetComponent<StockpileFitter>();
		if (fitter == null)
		{
			return;
		}
		DefaultFitter centred = go.AddComponent<DefaultFitter>();
		centred.CloneInternals(fitter);
		Object.DestroyImmediate(fitter);
	}

	// Resources and bulk times the multiplier; tools stay as they are (stockpiles do not take them).
	private static void Multiply(Recepteur recepteur, int multiplier, string what)
	{
		if (recepteur == null)
		{
			return;
		}
		Description capacity = recepteur._baseCapacity;
		List<string> parts = new List<string>();
		foreach (KeyValuePair<Type, Adjectif> entry in capacity.DicoAdjectif)
		{
			Adjectif adjectif = entry.Value;
			if (adjectif.quantifiable == null || adjectif.GetType() == Adjectif.outil.GetType())
			{
				continue;
			}
			if (adjectif is Ressource || adjectif.GetType() == Adjectif.encombrement.GetType())
			{
				adjectif.quantifiable.valeur *= multiplier;
				parts.Add(entry.Key.Name + "=" + adjectif.quantifiable.valeur);
			}
		}
		Plugin.Log.LogInfo("LargeStockpile: " + what + " " + string.Join(", ", parts.ToArray()));
	}

	// The piles on the pallet appear at fill thresholds; with three times the room they appear at three times the count.
	private static void ScaleDisplayThresholds(GameObject go)
	{
		foreach (RecepteurDisplay display in go.GetComponentsInChildren<RecepteurDisplay>(includeInactive: true))
		{
			foreach (RecepteurDisplay.DisplayElement element in display.displayElements)
			{
				foreach (RecepteurDisplay.DisplayElement.ConditionElement condition in element.conditions)
				{
					AdjectiveKey key = condition.relatedAdjective;
					if (key != null && key.GetAdjectif() is Ressource && condition.conditionNumber > 1)
					{
						condition.conditionNumber *= CapacityMultiplier;
					}
				}
			}
		}
	}

	// The game maps blueprints to buildings once; add the large stockpile if that has already happened.
	private static void AddBlueprintMapping()
	{
		BlueprintMapper mapper = BrixSingleton<BlueprintMapper>.Instance;
		if (mapper == null)
		{
			return;
		}
		if (!mapper.blueprint2Concrete.ContainsKey(BlueprintKey))
		{
			mapper.blueprint2Concrete.Add(BlueprintKey, BuildingKey);
		}
		if (!mapper.concrete2Blueprint.ContainsKey(BuildingKey))
		{
			mapper.concrete2Blueprint.Add(BuildingKey, BlueprintKey);
		}
	}
}

// Large stockpiles are also listed as pallets, so every lookup of the game's (and the mod's) stockpiles finds them.
[Feature(Features.LargeStockpile, Features.LargeStockpileInfo)]
[HarmonyPatch(typeof(AutoList), nameof(AutoList.AddToAutoList))]
internal static class LargeStockpileListAddPatch
{
	private static void Postfix(AutoList __instance, Factory.AssetKey key, GameObject go)
	{
		if (key == LargeStockpile.BuildingKey && go != null)
		{
			__instance.GetInstances(ObjetsDynamiques.Palette).Add(go);
		}
	}
}

[Feature(Features.LargeStockpile, Features.LargeStockpileInfo)]
[HarmonyPatch(typeof(AutoList), nameof(AutoList.RemoveFromAutoList), new Type[] { typeof(Factory.AssetKey), typeof(GameObject) })]
internal static class LargeStockpileListRemovePatch
{
	private static void Postfix(AutoList __instance, Factory.AssetKey key, GameObject go)
	{
		if (key == LargeStockpile.BuildingKey)
		{
			__instance.GetInstances(ObjetsDynamiques.Palette).Remove(go);
		}
	}
}

// "Is this a pallet?" in the recepteur (no tools on stockpiles, one resource type per stockpile) also for large ones.
[Feature(Features.LargeStockpile, Features.LargeStockpileInfo)]
[HarmonyPatch]
internal static class LargeStockpileRecepteurPatch
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(Recepteur), "RegenerateDescriptionsWithExclusions");
		yield return AccessTools.Method(typeof(Recepteur), "DealSingleResourceRecepteur");
	}

	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
	{
		FieldInfo palette = AccessTools.Field(typeof(ObjetsDynamiques), nameof(ObjetsDynamiques.Palette));
		MethodInfo equality = AccessTools.Method(typeof(Factory.AssetKey), "op_Equality");
		MethodInfo inequality = AccessTools.Method(typeof(Factory.AssetKey), "op_Inequality");
		List<CodeInstruction> code = new List<CodeInstruction>(instructions);
		int replaced = 0;
		for (int i = 0; i < code.Count - 1; i++)
		{
			if (!code[i].LoadsField(palette) || !(code[i + 1].operand is MethodInfo method))
			{
				continue;
			}
			if (method == equality)
			{
				code[i + 1] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(LargeStockpileRecepteurPatch), nameof(IsPallet))).MoveLabelsFrom(code[i + 1]);
				replaced++;
			}
			else if (method == inequality)
			{
				code[i + 1] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(LargeStockpileRecepteurPatch), nameof(IsNotPallet))).MoveLabelsFrom(code[i + 1]);
				replaced++;
			}
		}
		if (replaced == 0)
		{
			Plugin.Log.LogWarning("LargeStockpile: no pallet key check found in Recepteur." + __originalMethod.Name);
		}
		return code;
	}

	// Called as "key == ObjetsDynamiques.Palette" with the pallet key second.
	private static bool IsPallet(Factory.AssetKey key, Factory.AssetKey palette)
	{
		return key == palette || key == LargeStockpile.BuildingKey;
	}

	private static bool IsNotPallet(Factory.AssetKey key, Factory.AssetKey palette)
	{
		return !IsPallet(key, palette);
	}
}

// The observer feeding the faction storage count tracks own pallets; large stockpiles too.
[Feature(Features.LargeStockpile, Features.LargeStockpileInfo)]
[HarmonyPatch(typeof(UIGameObserver), nameof(UIGameObserver.ResetAll))]
internal static class LargeStockpileObserverPatch
{
	private static void Postfix()
	{
		if (UIGameObserver.storage == null)
		{
			return;
		}
		Predicate<Transform> pallets = UIGameObserver.storage.condition;
		UIGameObserver.storage.condition = (Transform t) => (pallets != null && pallets(t)) || (LargeStockpile.IsLarge(t) && UIGameObserver.IsMine(t));
	}
}

[Feature(Features.LargeStockpile, Features.LargeStockpileInfo)]
[HarmonyPatch(typeof(ObjectsHighlightDriver), "BuildObjectDynamiquesRadius")]
internal static class LargeStockpileHighlightPatch
{
	private static void Postfix(Dictionary<Factory.AssetKey, float> __result)
	{
		if (__result != null)
		{
			__result[LargeStockpile.BuildingKey] = 3f;
		}
	}
}

// The blueprint's state renderers (the ghost while placing and the blueprint while building) reset their own scale
// to the blueprint material's scale on every state change, which undid the wider model.
[Feature(Features.LargeStockpile, Features.LargeStockpileInfo)]
[HarmonyPatch(typeof(StateRenderer), "UpdateRenderersMaterial")]
internal static class LargeStockpileGhostPatch
{
	private static void Postfix(StateRenderer __instance)
	{
		Blueprint blueprint = __instance._blueprint;
		// Only the model parts: the root's collider and volume are grown already.
		if (blueprint == null || blueprint.AssetKey != LargeStockpile.BlueprintKey || __instance.transform == blueprint.transform)
		{
			return;
		}
		LargeStockpile.ScaleHorizontal(__instance.transform, LargeStockpile.ModelScale);
	}
}

// The large stockpile is mapped to its building as soon as it is created; when the game maps all blueprints
// afterwards it must skip it, or the duplicate key stops the game's factory loading.
[Feature(Features.LargeStockpile, Features.LargeStockpileInfo)]
[HarmonyPatch(typeof(BlueprintMapper), nameof(BlueprintMapper.AddMappings))]
internal static class LargeStockpileBlueprintMappingPatch
{
	private static bool Prefix(BlueprintMapper __instance, Blueprint bp)
	{
		return !__instance.blueprint2Concrete.ContainsKey(bp.AssetKey);
	}
}
