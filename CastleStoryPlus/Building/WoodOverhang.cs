using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Engine;
using Brix.Game;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Building;

// Wood blocks (logs) and the mod's wooden slabs can be built out sideways from other wood: a wood block or slab with
// nothing under it holds when every one of its blocks lies within [WoodOverhang] MaxOverhang blocks, counted sideways
// through wood (logs or slabs) and its own blocks, of wood that stands on something (so a slab laid in line with a
// slab walkway holds). So a row of logs can hang up to 5 blocks out into the air from a supported log, and a 1 x 5 slab
// in line with a walkway reaches those 5 blocks by itself. The distance was counted in pieces of wood before, so long
// slabs in a row hung far out into the air (#141). Blueprints follow the same rule (a hanging blueprint is valid next
// to a wood blueprint, and buildable once the wood it hangs from is built), so workers build such a row outward from
// its supported end. The wood a block hangs from is not one of its support cells, so the game did not look at the
// block again when that wood went: when wood is removed or falls, the logs and slabs within MaxOverhang of it are
// validated again (WoodOverhangRemovedPatch, WoodOverhangRevalidatePatch), and what no longer holds falls (a
// blueprint turns red).
[Feature(Features.WoodOverhang, Features.WoodOverhangInfo)]
internal static class WoodOverhang
{
	internal static ConfigEntry<int> MaxOverhang;

	private static readonly XYZ[] Sides = new XYZ[4]
	{
		new XYZ(1, 0, 0),
		new XYZ(-1, 0, 0),
		new XYZ(0, 0, 1),
		new XYZ(0, 0, -1)
	};

	// Set while the game's own rule is asked for the wood a block hangs from.
	private static bool _inner;

	// The cells of wood removed since the last check (WoodOverhangRevalidatePatch).
	private static readonly HashSet<XYZ> Removed = new HashSet<XYZ>();

	// More removed cells than this at once (a large collapse, a world unloading): only these are looked round.
	private const int MaxRemovedCells = 4096;

	private static void Enable()
	{
		MaxOverhang = Plugin.Cfg.Bind("WoodOverhang", "MaxOverhang", 5, new ConfigDescription("How many wood blocks may hang out sideways into the air from a supported wood block.", new AcceptableValueRange<int>(1, 10)));
	}

	internal static int Max => (MaxOverhang != null) ? MaxOverhang.Value : 5;

	internal static bool IsWood(AbstractValidator validator)
	{
		return validator is LogBlockValidator || validator is LogBPValidator;
	}

	// A log, or one of the mod's wooden slabs (their validator is the brick's, so the volume tells).
	internal static bool IsWood(AbstractValidator validator, IVolume volume)
	{
		return IsWood(validator) || CustomBlocks.IsWoodSlab(volume);
	}

	// Whether the block hangs from supported wood within MaxOverhang blocks. built: only placed wood counts (a blueprint
	// becomes buildable); otherwise wood blueprints count as well (a blueprint is valid). The wood at the end must
	// stand by the game's rule (asked afresh, without this one), so hanging wood never holds other hanging wood up.
	internal static bool HangsFromWood(ref ValidationData vData, bool built)
	{
		if (_inner || vData.Volume == null)
		{
			return false;
		}
		IVolume self = vData.Volume;
		Faction faction = vData.Faction;
		Dictionary<XYZ, IVolume> wood = new Dictionary<XYZ, IVolume>();
		Dictionary<IVolume, bool> stands = new Dictionary<IVolume, bool>();
		return BlockSupportRules.HangsWithin(Cells(self), Neighbours, cell =>
		{
			IVolume found = WoodAt(cell, built, faction);
			if (found == null || found == self)
			{
				return false;
			}
			wood[cell] = found;
			return true;
		}, cell =>
		{
			IVolume found = wood[cell];
			if (!stands.TryGetValue(found, out bool standing))
			{
				standing = StandsOnItsOwn(found);
				stands[found] = standing;
			}
			return standing;
		}, Max);
	}

	private static IEnumerable<XYZ> Neighbours(XYZ cell)
	{
		foreach (XYZ side in Sides)
		{
			yield return cell + side;
		}
	}

	private static List<XYZ> Cells(IVolume volume)
	{
		List<XYZ> cells = new List<XYZ>();
		foreach (XYZ cell in volume.GetCases(VPropertyMask.occupy | VPropertyMask.fantome))
		{
			cells.Add(cell);
		}
		return cells;
	}

	// Placed wood at the cell (anyone's: a built block holds whoever placed it), or (unless built) a wood blueprint of
	// the same faction.
	private static IVolume WoodAt(XYZ cell, bool built, Faction faction)
	{
		foreach (IVolume volume in VolumeRegistry.VolumesWithPropertyAt(cell, VProperty.occupy))
		{
			if (IsWoodVolume(volume, null))
			{
				return volume;
			}
		}
		if (built)
		{
			return null;
		}
		foreach (IVolume volume in VolumeRegistry.VolumesWithPropertyAt(cell, VProperty.fantome))
		{
			if (IsWoodVolume(volume, faction))
			{
				return volume;
			}
		}
		return null;
	}

	private static bool IsWoodVolume(IVolume volume, Faction faction)
	{
		if (volume == null || volume.GameObject == null)
		{
			return false;
		}
		ValidatorComponent component = volume.GameObject.GetComponent<ValidatorComponent>();
		if (component == null || !IsWood(component.Validator, volume))
		{
			return false;
		}
		if (faction == null)
		{
			return true;
		}
		ValidationData data = component.VData;
		return data.Faction == null || data.Faction == faction;
	}

	// The game's own rule: the wood stands on something (not on other hanging wood).
	private static bool StandsOnItsOwn(IVolume volume)
	{
		ValidatorComponent component = volume.GameObject.GetComponent<ValidatorComponent>();
		ValidationData data = component.VData;
		_inner = true;
		try
		{
			ValidationResult result = component.Validator.Validate(ref data, null);
			return result == ValidationResult.Valid || result == ValidationResult.Buildable;
		}
		finally
		{
			_inner = false;
		}
	}

	// A volume is being unregistered: if it is wood (a log, plank or slab, placed or a blueprint), its cells are looked
	// round at the next check.
	internal static void OnRemoved(IVolume volume)
	{
		if (volume == null || Removed.Count >= MaxRemovedCells)
		{
			return;
		}
		bool isWood = false;
		foreach (XYZ cell in volume.GetCases(VPropertyMask.logSupport | VPropertyMask.plankSupport))
		{
			isWood = true;
			break;
		}
		if (!isWood)
		{
			return;
		}
		foreach (XYZ cell in volume.GetCases(VPropertyMask.occupy | VPropertyMask.fantome))
		{
			Removed.Add(cell);
		}
	}

	// The logs and slabs within MaxOverhang blocks (sideways, at the same level) of the removed wood are validated
	// again: the game's validation drops a placed one that no longer holds (its fall is a removal too, so the wood
	// beyond it follows a frame later) and turns a blueprint red.
	internal static void RevalidateRemoved()
	{
		if (Removed.Count == 0)
		{
			return;
		}
		List<XYZ> cells = new List<XYZ>(Removed);
		Removed.Clear();
		int max = Max;
		HashSet<IVolume> found = new HashSet<IVolume>();
		foreach (XYZ cell in cells)
		{
			for (int dx = -max; dx <= max; dx++)
			{
				int reach = max - System.Math.Abs(dx);
				for (int dz = -reach; dz <= reach; dz++)
				{
					XYZ near = new XYZ(cell.x + dx, cell.y, cell.z + dz);
					foreach (IVolume volume in VolumeRegistry.VolumesWithPropertyAt(near, VPropertyMask.occupy | VPropertyMask.fantome))
					{
						if (IsWoodVolume(volume, null))
						{
							found.Add(volume);
						}
					}
				}
			}
		}
		if (found.Count == 0)
		{
			return;
		}
		VUtil.NewVID();
		Dictionary<IVolume, BaseVolume> registered = VolumeRegistrator.Instance.Data.RegisteredVolumesData;
		foreach (IVolume volume in found)
		{
			// One that fell while the others were checked is gone already.
			if (!registered.ContainsKey(volume) || volume.GameObject == null)
			{
				continue;
			}
			try
			{
				volume.Validate();
			}
			catch (System.Exception ex)
			{
				Plugin.Log.LogWarning("WoodOverhang: could not validate " + volume.AssetKey + " again: " + ex.Message);
			}
		}
	}
}

// Valid: a placed wood block stays up, a wood blueprint may be placed.
[Feature(Features.WoodOverhang, Features.WoodOverhangInfo)]
[HarmonyPatch(typeof(AbstractValidator), "IsValid")]
internal static class WoodOverhangValidPatch
{
	private static void Postfix(AbstractValidator __instance, ref ValidationData vData, ref bool __result)
	{
		if (__result || !WoodOverhang.IsWood(__instance, vData.Volume))
		{
			return;
		}
		// A placed block hangs only from placed wood; a blueprint may hang from wood blueprints too.
		__result = WoodOverhang.HangsFromWood(ref vData, built: !(__instance is BlueprintValidator));
	}
}

// Buildable: the wood the blueprint hangs from is built.
[Feature(Features.WoodOverhang, Features.WoodOverhangInfo)]
[HarmonyPatch(typeof(BlueprintValidator), "IsBuildable")]
internal static class WoodOverhangBuildablePatch
{
	private static void Postfix(BlueprintValidator __instance, ref ValidationData vData, ref bool __result)
	{
		if (__result || !(__instance is LogBPValidator || CustomBlocks.IsWoodSlab(vData.Volume)))
		{
			return;
		}
		__result = WoodOverhang.HangsFromWood(ref vData, built: true);
	}
}

// Wood being unregistered (taken down, fallen, a blueprint cancelled or built): noted for WoodOverhangRevalidatePatch.
// Before the game's own unregistering, while the volume still has its cells and object.
[Feature(Features.WoodOverhang, Features.WoodOverhangInfo)]
[HarmonyPatch(typeof(VolumeRegistrator), "UnregisterVolume_Internal")]
internal static class WoodOverhangRemovedPatch
{
	private static void Prefix(VolumeRegistrator __instance, IVolume v)
	{
		if (v != null && __instance.Data.RegisteredVolumesData.ContainsKey(v))
		{
			WoodOverhang.OnRemoved(v);
		}
	}
}

// After the game has validated what the frame's changes touched: the wood near removed wood is validated again.
[Feature(Features.WoodOverhang, Features.WoodOverhangInfo)]
[HarmonyPatch(typeof(VolumeRegistryComponent), "LateUpdate")]
internal static class WoodOverhangRevalidatePatch
{
	private static void Postfix()
	{
		WoodOverhang.RevalidateRemoved();
	}
}
