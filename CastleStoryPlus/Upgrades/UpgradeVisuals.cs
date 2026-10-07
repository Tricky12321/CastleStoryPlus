using System.Collections.Generic;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using CastleStoryPlus.UI;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Upgrades;

// Researched tiers show on the bricktrons' gear: swords and poleaxes take the highest smithy blade tier, bows and
// crossbows the highest ranged tier, helmets, kettle hats, kepis and caps the highest armoury armour tier, shields the
// shield rims tier. Tier 1 (iron) dark bluish grey, tier 2 (steel) bright silver, tier 3 (crystal) blue with a glow.
// The game's gear shaders mostly have no colour property, so the metal of the gear's picture is recoloured (its grey,
// unsaturated pixels; wood, leather and team colours stay) into one copy per picture and tier, on a copy of each
// material, shared by all units; the game's models and pictures stay. Gear is pooled, so it gets its own materials
// back when unequipped. Every gear material tinted logs its shader and properties once.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(Toolbag), "OnEquipTool")]
internal static class UpgradeVisuals
{
	// Multiplies the material's colour property, per tier, for a material without a picture.
	private static readonly Color[] Tints = new Color[4]
	{
		Color.white,
		new Color(0.8f, 0.85f, 0.95f),
		new Color(1.5f, 1.6f, 1.75f),
		new Color(0.6f, 1.3f, 2.4f)
	};

	// The metal's colour per tier, times a pixel's brightness: iron dark and bluish grey, steel bright silver,
	// crystal blue.
	private static readonly Color[] Metals = new Color[4]
	{
		Color.white,
		new Color(0.75f, 0.8f, 0.9f),
		new Color(1.45f, 1.5f, 1.6f),
		new Color(0.55f, 1.15f, 2f)
	};

	// Added as _EmissionColor where the shader has it.
	private static readonly Color CrystalGlow = new Color(0f, 0.2f, 0.45f);

	private static readonly string[] TextureProperties = new string[] { "_Diffuse", "_MainTex" };

	private static readonly string[] ColorProperties = new string[] { "_Color", "_TintColor" };

	private static readonly Dictionary<Material, Material[]> Tinted = new Dictionary<Material, Material[]>();

	// A critical hit's weapon: its metal red, with a red glow where the shader has one.
	private static readonly Color CriticalMetal = new Color(1.9f, 0.3f, 0.22f);

	private static readonly Color CriticalGlow = new Color(0.6f, 0.05f, 0.02f);

	private static readonly Dictionary<Material, Material> Critical = new Dictionary<Material, Material>();

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
			LogMaterial(original);
		}
		if (tiers[tier] == null)
		{
			Material material = new Material(original);
			material.name = original.name + " (" + UpgradeLines.TierNames[tier] + ")";
			// The picture itself is recoloured, so the tier shows whatever the shader does with a colour property
			// (the game's own shaders mostly have none); the colour property only when there is no picture.
			string textureName = TextureRecolour.TextureProperty(material);
			Texture2D recoloured = (textureName != null) ? TextureRecolour.Recoloured(material.GetTexture(textureName), Metals[tier], metalOnly: true, UpgradeLines.TierNames[tier]) : null;
			if (recoloured != null)
			{
				material.SetTexture(textureName, recoloured);
			}
			else
			{
				foreach (string colorName in ColorProperties)
				{
					if (material.HasProperty(colorName))
					{
						Color color = original.GetColor(colorName);
						Color tintColor = Tints[tier];
						material.SetColor(colorName, new Color(color.r * tintColor.r, color.g * tintColor.g, color.b * tintColor.b, color.a));
					}
				}
			}
			if (tier == UpgradeLines.MaxTier && material.HasProperty("_EmissionColor"))
			{
				material.SetColor("_EmissionColor", original.GetColor("_EmissionColor") + CrystalGlow);
			}
			tiers[tier] = material;
		}
		return tiers[tier];
	}

	// Once per gear material: its shader and which of the looked-for properties it has.
	private static void LogMaterial(Material material)
	{
		List<string> found = new List<string>();
		foreach (string name in TextureProperties)
		{
			if (material.HasProperty(name))
			{
				found.Add(name + ((material.GetTexture(name) != null) ? string.Empty : " (empty)"));
			}
		}
		foreach (string name in ColorProperties)
		{
			if (material.HasProperty(name))
			{
				found.Add(name);
			}
		}
		if (material.HasProperty("_EmissionColor"))
		{
			found.Add("_EmissionColor");
		}
		Plugin.Log.LogInfo("UpgradeVisuals: " + material.name + " uses " + ((material.shader != null) ? material.shader.name : "no shader") + " (" + ((found.Count > 0) ? string.Join(", ", found.ToArray()) : "none of " + string.Join(", ", TextureProperties) + ", " + string.Join(", ", ColorProperties)) + ")");
	}

	// The gear's material for a critical hit (CriticalHits): its metal red, whatever the tier. One copy per material.
	internal static Material CriticalMaterial(Material original)
	{
		if (original == null)
		{
			return null;
		}
		if (Critical.TryGetValue(original, out Material made))
		{
			return made;
		}
		Material material = new Material(original);
		material.name = original.name + " (critical)";
		string textureName = TextureRecolour.TextureProperty(material);
		Texture2D recoloured = (textureName != null) ? TextureRecolour.Recoloured(material.GetTexture(textureName), CriticalMetal, metalOnly: true, "critical") : null;
		if (recoloured != null)
		{
			material.SetTexture(textureName, recoloured);
		}
		else
		{
			foreach (string colorName in ColorProperties)
			{
				if (material.HasProperty(colorName))
				{
					Color color = original.GetColor(colorName);
					material.SetColor(colorName, new Color(CriticalMetal.r * 0.6f, CriticalMetal.g * 0.6f, CriticalMetal.b * 0.6f, color.a));
				}
			}
		}
		if (material.HasProperty("_EmissionColor"))
		{
			material.SetColor("_EmissionColor", original.GetColor("_EmissionColor") + CriticalGlow);
		}
		Critical[original] = material;
		return material;
	}

	// A critical hit's red on the gear on or off, over its tier; the gear gets its tier's look back after it.
	internal static void SetCritical(GameObject item, bool critical)
	{
		if (item == null)
		{
			return;
		}
		UpgradeTint tint = item.GetComponent<UpgradeTint>();
		if (tint == null)
		{
			if (!critical)
			{
				return;
			}
			tint = item.AddComponent<UpgradeTint>();
			tint.Capture();
		}
		tint.ShowCritical(critical);
	}

	// A tier was researched (or tiers arrived from the host or a save): retint the faction's equipped gear.
	private static void Refresh(Faction faction)
	{
		List<GameObject> tools = new List<GameObject>();
		foreach (Toolbag toolbag in Live<Toolbag>.Active())
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

// Unequipped gear (dropped, put on a rack, back to the pool) shows its own materials again, without a critical hit's red.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(Toolbag), "OnUnequipTool")]
internal static class UpgradeVisualsUnequipPatch
{
	private static void Postfix(GameObject item)
	{
		UpgradeVisuals.SetCritical(item, false);
		UpgradeVisuals.Apply(item, 0);
	}
}

// The gear's own materials, kept so a tint (a tier, a critical hit's red) can be undone.
internal class UpgradeTint : MonoBehaviour
{
	private Renderer[] _renderers;

	private Material[][] _originals;

	private int _tier;

	// A critical hit's red, shown over the tier while it lasts.
	private bool _critical;

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
		Render();
	}

	public void ShowCritical(bool critical)
	{
		if (critical == _critical)
		{
			return;
		}
		_critical = critical;
		Render();
	}

	private void Render()
	{
		for (int i = 0; i < _renderers.Length; i++)
		{
			if (_renderers[i] == null)
			{
				continue;
			}
			Material[] materials = new Material[_originals[i].Length];
			for (int j = 0; j < materials.Length; j++)
			{
				materials[j] = _critical ? UpgradeVisuals.CriticalMaterial(_originals[i][j]) : UpgradeVisuals.TintedMaterial(_originals[i][j], _tier);
			}
			_renderers[i].sharedMaterials = materials;
		}
	}
}
