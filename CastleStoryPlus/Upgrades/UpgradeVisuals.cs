using System.Collections.Generic;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Upgrades;

// Researched tiers show on the bricktrons' gear: swords and poleaxes take the highest smithy blade tier, bows and
// crossbows the highest ranged tier, helmets, kettle hats, kepis and caps the highest armoury armour tier, shields the
// shield rims tier. Tier 1 (iron) is a little warmer, tier 2 (steel) brighter and cooler, tier 3 (crystal) blue with a
// glow. The gear's own materials are tinted (one copy per material and tier, shared by all units), so the game's
// models and textures stay. Gear is pooled, so it gets its own materials back when unequipped.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(Toolbag), "OnEquipTool")]
internal static class UpgradeVisuals
{
	// Multiplies the material's _Color, per tier.
	private static readonly Color[] Tints = new Color[4]
	{
		Color.white,
		new Color(1.1f, 1.05f, 1f),
		new Color(1.5f, 1.6f, 1.75f),
		new Color(0.8f, 1.4f, 2.2f)
	};

	// Added as _EmissionColor where the shader has it.
	private static readonly Color CrystalGlow = new Color(0f, 0.15f, 0.35f);

	private static readonly Dictionary<Material, Material[]> Tinted = new Dictionary<Material, Material[]>();

	private static void Enable()
	{
		TeamUpgrades.Changed += Refresh;
	}

	private static void Postfix(Toolbag __instance, GameObject item)
	{
		Apply(item, TierFor(__instance.gameObject, item));
	}

	// The tier the gear shows for the unit's faction, or -1 for gear without an upgrade line (tools, bags, scarves).
	internal static int TierFor(GameObject unit, GameObject item)
	{
		FactoryImprint imprint = (item != null) ? item.GetComponent<FactoryImprint>() : null;
		if (imprint == null)
		{
			return -1;
		}
		Factory.AssetKey key = imprint.AssetKey;
		if (key == Accessories.Sword || key == Accessories.Poleaxe)
		{
			return Highest(unit, UpgradeLines.SharpenedBlades, UpgradeLines.HeavyBlades);
		}
		if (key == Accessories.Bow || key == Accessories.Crossbow)
		{
			return Highest(unit, UpgradeLines.Fletching, UpgradeLines.SteadyAim, UpgradeLines.Winch);
		}
		if (key == Accessories.Helmet || key == Accessories.KettleHat || key == Accessories.Kepi || key == Accessories.Cap)
		{
			return Highest(unit, UpgradeLines.Helmet, UpgradeLines.Chainmail, UpgradeLines.Gambeson, UpgradeLines.WardedPlate);
		}
		if (key == Accessories.Shield)
		{
			return TeamUpgrades.Tier(unit, UpgradeLines.ShieldRims);
		}
		return -1;
	}

	private static int Highest(GameObject unit, params int[] lines)
	{
		int tier = 0;
		foreach (int line in lines)
		{
			tier = Mathf.Max(tier, TeamUpgrades.Tier(unit, line));
		}
		return tier;
	}

	internal static void Apply(GameObject item, int tier)
	{
		if (item == null || tier < 0)
		{
			return;
		}
		UpgradeTint tint = item.GetComponent<UpgradeTint>();
		if (tint == null)
		{
			if (tier == 0)
			{
				return;
			}
			tint = item.AddComponent<UpgradeTint>();
			tint.Capture();
		}
		tint.Show(tier);
	}

	internal static Material TintedMaterial(Material original, int tier)
	{
		if (original == null || tier <= 0)
		{
			return original;
		}
		if (!Tinted.TryGetValue(original, out Material[] tiers))
		{
			tiers = new Material[Tints.Length];
			Tinted[original] = tiers;
		}
		if (tiers[tier] == null)
		{
			Material material = new Material(original);
			material.name = original.name + " (" + UpgradeLines.TierNames[tier] + ")";
			if (material.HasProperty("_Color"))
			{
				Color color = original.GetColor("_Color");
				Color tintColor = Tints[tier];
				material.SetColor("_Color", new Color(color.r * tintColor.r, color.g * tintColor.g, color.b * tintColor.b, color.a));
			}
			if (tier == UpgradeLines.MaxTier && material.HasProperty("_EmissionColor"))
			{
				material.SetColor("_EmissionColor", original.GetColor("_EmissionColor") + CrystalGlow);
			}
			tiers[tier] = material;
		}
		return tiers[tier];
	}

	// A tier was researched (or tiers arrived from the host or a save): retint the faction's equipped gear.
	private static void Refresh(Faction faction)
	{
		List<GameObject> tools = new List<GameObject>();
		foreach (Toolbag toolbag in Object.FindObjectsOfType<Toolbag>())
		{
			if (toolbag == null || Faction.GetFaction(toolbag.gameObject) != faction)
			{
				continue;
			}
			tools.Clear();
			toolbag.Tools(ref tools, includeNullTools: false, includeAesthetic: true);
			foreach (GameObject tool in tools)
			{
				Apply(tool, TierFor(toolbag.gameObject, tool));
			}
		}
	}
}

// Unequipped gear (dropped, put on a rack, back to the pool) shows its own materials again.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(Toolbag), "OnUnequipTool")]
internal static class UpgradeVisualsUnequipPatch
{
	private static void Postfix(GameObject item)
	{
		UpgradeVisuals.Apply(item, 0);
	}
}

// The gear's own materials, kept so a tint can be undone.
internal class UpgradeTint : MonoBehaviour
{
	private Renderer[] _renderers;

	private Material[][] _originals;

	private int _tier;

	public void Capture()
	{
		_renderers = GetComponentsInChildren<Renderer>(true);
		_originals = new Material[_renderers.Length][];
		for (int i = 0; i < _renderers.Length; i++)
		{
			_originals[i] = _renderers[i].sharedMaterials;
		}
	}

	public void Show(int tier)
	{
		if (tier == _tier)
		{
			return;
		}
		_tier = tier;
		for (int i = 0; i < _renderers.Length; i++)
		{
			if (_renderers[i] == null)
			{
				continue;
			}
			Material[] materials = new Material[_originals[i].Length];
			for (int j = 0; j < materials.Length; j++)
			{
				materials[j] = UpgradeVisuals.TintedMaterial(_originals[i][j], tier);
			}
			_renderers[i].sharedMaterials = materials;
		}
	}
}
