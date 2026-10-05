using System.Collections.Generic;
using System.Text;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.UI.Icons;
using CastleStoryPlus.Core;

namespace CastleStoryPlus.Upgrades;

internal enum UpgradeHall
{
	Smithy,
	Armoury
}

internal sealed class Ingredient
{
	public Ressource Resource;

	public int Count;

	public string Name;

	public IconKey Icon;
}

// One upgrade line (e.g. Chainmail) with three tiers: Iron, Steel, Crystal. Each tier is a crafting recipe in its
// building; finishing it unlocks the tier for the whole faction.
internal sealed class UpgradeLine
{
	public int Index;

	public string Key;

	public string Name;

	public UpgradeHall Hall;

	public string Unit;

	public string Text;

	// Effect per tier, 1-based: Value[0] is no upgrade.
	public float[] Value;

	public string[] Effect;

	public Ingredient[][] Cost;

	public RecipeInfo[] Recipe = new RecipeInfo[UpgradeLines.MaxTier + 1];
}

// The upgrade lines, their tier effects and costs, and the crafting recipes that research them.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
internal static class UpgradeLines
{
	public const int MaxTier = 3;

	public const int SharpenedBlades = 0;

	public const int HeavyBlades = 1;

	public const int Fletching = 2;

	public const int SteadyAim = 3;

	public const int Winch = 4;

	public const int Chainmail = 5;

	public const int Gambeson = 6;

	public const int Helmet = 7;

	public const int ShieldRims = 8;

	public const int WardedPlate = 9;

	public static readonly string[] TierNames = { string.Empty, "Iron", "Steel", "Crystal" };

	// Work (hammer strokes) per tier.
	private static readonly int[] Work = { 0, 20, 35, 50 };

	public static readonly List<UpgradeLine> All = new List<UpgradeLine>();

	private static readonly Dictionary<int, KeyValuePair<UpgradeLine, int>> ByRecipe = new Dictionary<int, KeyValuePair<UpgradeLine, int>>();

	private static void Enable()
	{
		Add(SharpenedBlades, "sharpened_blades", "Sharpened Blades", UpgradeHall.Smithy, "Melee fighters", "Keener edges on swords and axes. Every melee hit deals more damage.",
			new[] { 0f, 0.15f, 0.3f, 0.5f }, "+{0}% melee damage",
			Cost(Adjectif.iron, 6, Adjectif.plankBlock, 4), SteelCost(6, Adjectif.stoneBlock, 6), Cost(Adjectif.iron, 8, Crystal, 4));
		Add(HeavyBlades, "heavy_blades", "Heavy Blades", UpgradeHall.Smithy, "All bricktrons", "Heavier heads for breaking walls, gates and buildings.",
			new[] { 0f, 0.25f, 0.5f, 0.75f }, "+{0}% damage to blocks and buildings",
			Cost(Adjectif.iron, 6, Adjectif.woodBlock, 4), SteelCost(6, Adjectif.stoneBlock, 8), Cost(Adjectif.iron, 10, Crystal, 4));
		Add(Fletching, "fletching", "Fletching", UpgradeHall.Smithy, "Archers", "Better arrowheads and feathers. Arrows deal more damage.",
			new[] { 0f, 0.15f, 0.3f, 0.5f }, "+{0}% arrow damage",
			Cost(Adjectif.plankBlock, 8, Adjectif.plant, 10), SteelCost(3, Adjectif.plankBlock, 8), Cost(Adjectif.iron, 4, Adjectif.plankBlock, 6, Crystal, 4));
		Add(SteadyAim, "steady_aim", "Steady Aim", UpgradeHall.Smithy, "Archers", "Archery drill. Shots that are not straight at a visible target spread less.",
			new[] { 0f, 0.25f, 0.5f, 0.75f }, "-{0}% arrow spread",
			Cost(Adjectif.plankBlock, 6, Adjectif.rope, 2), SteelCost(2, Adjectif.rope, 4), Cost(Adjectif.glass, 2, Crystal, 3));
		Add(Winch, "winch", "Winch", UpgradeHall.Smithy, "Arbalists", "A geared crank on the crossbow. Faster reload between bolts.",
			new[] { 0f, 0.1f, 0.2f, 0.3f }, "-{0}% reload time",
			Cost(Adjectif.iron, 4, Adjectif.rope, 2), SteelCost(3, Adjectif.cog, 4), Cost(Adjectif.cog, 6, Crystal, 4));
		Add(Chainmail, "chainmail", "Chainmail", UpgradeHall.Armoury, "Soldiers", "Riveted rings under the tabard. Takes the edge off sword and axe hits.",
			new[] { 0f, 0.1f, 0.2f, 0.3f }, "-{0}% melee damage taken",
			Cost(Adjectif.iron, 8), SteelCost(7, Adjectif.rope, 2), Cost(Adjectif.iron, 10, Crystal, 4));
		Add(Gambeson, "gambeson", "Padded Gambeson", UpgradeHall.Armoury, "Soldiers", "Quilted layers that catch arrows, bolts and thrown stones.",
			new[] { 0f, 0.1f, 0.2f, 0.3f }, "-{0}% arrow and bolt damage taken",
			Cost(Adjectif.fabric, 4, Adjectif.plant, 10), SteelCost(2, Adjectif.fabric, 8, Adjectif.rope, 4), Cost(Adjectif.fabric, 6, Crystal, 4));
		Add(Helmet, "helmet", "Reinforced Helmet", UpgradeHall.Armoury, "Soldiers", "A thicker helm. Soldiers have more health.",
			new[] { 0f, 0.1f, 0.2f, 0.3f }, "+{0}% max health",
			Cost(Adjectif.iron, 6), SteelCost(5, Adjectif.fabric, 2), Cost(Adjectif.iron, 8, Crystal, 4));
		Add(ShieldRims, "shield_rims", "Shield Rims", UpgradeHall.Armoury, "Knights", "Metal rims and bosses. Blocks cost less stamina and cover a wider arc.",
			new[] { 0f, 0.1f, 0.2f, 0.3f }, "-{0}% stamina per block, +{1} degrees block arc",
			Cost(Adjectif.iron, 4, Adjectif.plankBlock, 6), SteelCost(5, Adjectif.plankBlock, 6), Cost(Adjectif.iron, 8, Crystal, 4));
		Add(WardedPlate, "warded_plate", "Warded Plate", UpgradeHall.Armoury, "Soldiers", "A breastplate with a warding rune against magic and blasts.",
			new[] { 0f, 0.05f, 0.1f, 0.15f }, "-{0}% magic and explosion damage taken",
			Cost(Adjectif.iron, 8, Adjectif.orangeCrystal, 6), SteelCost(7, Adjectif.orangeCrystal, 10), Cost(Adjectif.iron, 10, Crystal, 6, Adjectif.glass, 2));
		RegisterRecipes();
	}

	// Degrees added to the block arc per Shield Rims tier.
	public const float ShieldRimsDegreesPerTier = 15f;

	private static void Add(int index, string key, string name, UpgradeHall hall, string unit, string text, float[] value, string effect, params Ingredient[][] cost)
	{
		UpgradeLine line = new UpgradeLine
		{
			Index = index,
			Key = key,
			Name = name,
			Hall = hall,
			Unit = unit,
			Text = text,
			Value = value,
			Effect = new string[MaxTier + 1],
			Cost = new Ingredient[MaxTier + 1][]
		};
		for (int tier = 1; tier <= MaxTier; tier++)
		{
			line.Effect[tier] = string.Format(effect, UnityEngine.Mathf.RoundToInt(value[tier] * 100f), UnityEngine.Mathf.RoundToInt(tier * ShieldRimsDegreesPerTier));
			line.Cost[tier] = cost[tier - 1];
		}
		All.Add(line);
	}

	// The steel tier: steel and the other materials. Without the Metallurgy feature (no steel) it costs twice as much
	// iron instead, as before.
	// The crystal tier costs dark crystals (DarkCrystals), or blue crystal without that feature.
	private static Ressource Crystal => Economy.DarkCrystals.IsOn() ? Economy.DarkCrystals.Resource : Adjectif.blueCrystal;

	private static Ingredient[] SteelCost(int steel, params object[] pairs)
	{
		object[] all = new object[pairs.Length + 2];
		bool hasSteel = Metallurgy.Metallurgy.IsOn();
		all[0] = hasSteel ? (Ressource)Adjectif.terracotta : Adjectif.iron;
		all[1] = hasSteel ? steel : steel * 2;
		pairs.CopyTo(all, 2);
		return Cost(all);
	}

	private static Ingredient[] Cost(params object[] pairs)
	{
		Ingredient[] ingredients = new Ingredient[pairs.Length / 2];
		for (int i = 0; i < ingredients.Length; i++)
		{
			Ressource resource = (Ressource)pairs[i * 2];
			ingredients[i] = new Ingredient
			{
				Resource = resource,
				Count = (int)pairs[i * 2 + 1],
				Name = ResourceName(resource),
				Icon = ResourceIcon(resource)
			};
		}
		return ingredients;
	}

	// One crafting recipe per tier. The recipe ids are packed in 8 bits by the crafting queue, so they must stay
	// below 256; they are made in the same order on every machine, after the game's own recipes (and the market's
	// trades). The recipe produces nothing (UpgradeResearchPatch unlocks the tier instead); Creates only has to be
	// a valid item, and dropping on completion skips the check for room to store it.
	private static void RegisterRecipes()
	{
		if (CookBook.Sword == null)
		{
			return;
		}
		foreach (UpgradeLine line in All)
		{
			for (int tier = 1; tier <= MaxTier; tier++)
			{
				RecipeMaker maker = new RecipeMaker
				{
					Work = Work[tier],
					Heat = 0,
					Creates = Accessories.Sword,
					QteCreated = 1,
					DropOnCompletion = true,
					Icon = (line.Hall == UpgradeHall.Smithy) ? IconKeys._Sword : IconKeys._Shield,
					Drawing = (line.Hall == UpgradeHall.Smithy) ? IconKeys._Sword : IconKeys._Shield
				};
				foreach (Ingredient ingredient in line.Cost[tier])
				{
					maker.With(ingredient.Resource, ingredient.Count);
				}
				RecipeInfo recipe = maker.Make();
				if (recipe.id > 255)
				{
					Plugin.Log.LogError("Upgrades: recipe id " + recipe.id + " is too high for the crafting queue, upgrades left out");
					return;
				}
				line.Recipe[tier] = recipe;
				ByRecipe[recipe.id] = new KeyValuePair<UpgradeLine, int>(line, tier);
			}
		}
		Plugin.Log.LogInfo("Upgrades: " + ByRecipe.Count + " research recipes registered");
	}

	// The line and tier a recipe researches, or false for any other recipe.
	public static bool TryGetResearch(RecipeInfo recipe, out UpgradeLine line, out int tier)
	{
		line = null;
		tier = 0;
		if (recipe == null || !ByRecipe.TryGetValue(recipe.id, out KeyValuePair<UpgradeLine, int> entry))
		{
			return false;
		}
		line = entry.Key;
		tier = entry.Value;
		return true;
	}

	public static IEnumerable<UpgradeLine> In(UpgradeHall hall)
	{
		foreach (UpgradeLine line in All)
		{
			if (line.Hall == hall)
			{
				yield return line;
			}
		}
	}

	// The most of each resource one tier of the hall's lines needs: what the building must be able to hold.
	public static Dictionary<Ressource, int> Storage(UpgradeHall hall)
	{
		Dictionary<System.Type, Ressource> types = new Dictionary<System.Type, Ressource>();
		Dictionary<Ressource, int> most = new Dictionary<Ressource, int>();
		foreach (UpgradeLine line in In(hall))
		{
			for (int tier = 1; tier <= MaxTier; tier++)
			{
				foreach (Ingredient ingredient in line.Cost[tier])
				{
					if (!types.TryGetValue(ingredient.Resource.GetType(), out Ressource resource))
					{
						resource = ingredient.Resource;
						types[resource.GetType()] = resource;
						most[resource] = 0;
					}
					most[resource] = System.Math.Max(most[resource], ingredient.Count);
				}
			}
		}
		return most;
	}

	public static string CostText(Ingredient[] cost)
	{
		StringBuilder text = new StringBuilder();
		foreach (Ingredient ingredient in cost)
		{
			if (text.Length > 0)
			{
				text.Append(", ");
			}
			text.Append(ingredient.Count).Append(' ').Append(ingredient.Name);
		}
		return text.ToString();
	}

	private static string ResourceName(Ressource resource)
	{
		System.Type type = resource.GetType();
		if (type == Adjectif.iron.GetType())
		{
			return "iron";
		}
		if (type == Adjectif.plankBlock.GetType())
		{
			return "planks";
		}
		if (type == Adjectif.woodBlock.GetType())
		{
			return "logs";
		}
		if (type == Adjectif.stoneBlock.GetType())
		{
			return "bricks";
		}
		if (type == Adjectif.plant.GetType())
		{
			return "plant fibre";
		}
		if (type == Adjectif.rope.GetType())
		{
			return "rope";
		}
		if (type == Adjectif.fabric.GetType())
		{
			return "fabric";
		}
		if (type == Adjectif.cog.GetType())
		{
			return "cogs";
		}
		if (type == Adjectif.glass.GetType())
		{
			return "glass";
		}
		if (type == Adjectif.orangeCrystal.GetType())
		{
			return "orange crystal";
		}
		if (type == Adjectif.blueCrystal.GetType())
		{
			return "blue crystal";
		}
		if (type == Adjectif.purifiedBlueCrystal.GetType())
		{
			return "dark crystal";
		}
		if (type == Adjectif.terracotta.GetType())
		{
			return "steel";
		}
		if (type == Adjectif.clay.GetType())
		{
			return "coal";
		}
		return type.Name;
	}

	private static IconKey ResourceIcon(Ressource resource)
	{
		System.Type type = resource.GetType();
		if (type == Adjectif.iron.GetType())
		{
			return IconKeys._Stockpiled_Iron;
		}
		if (type == Adjectif.plankBlock.GetType())
		{
			return IconKeys._Stockpiled_Plank;
		}
		if (type == Adjectif.woodBlock.GetType())
		{
			return IconKeys._Stockpiled_Log;
		}
		if (type == Adjectif.stoneBlock.GetType())
		{
			return IconKeys._Stockpiled_Brick;
		}
		if (type == Adjectif.plant.GetType())
		{
			return IconKeys._Stockpiled_Plants;
		}
		if (type == Adjectif.rope.GetType())
		{
			return IconKeys._Stockpiled_Rope;
		}
		if (type == Adjectif.fabric.GetType())
		{
			return IconKeys._Stockpiled_Fabric;
		}
		if (type == Adjectif.cog.GetType())
		{
			return IconKeys._Stockpiled_Cog;
		}
		if (type == Adjectif.glass.GetType())
		{
			return IconKeys._Stockpiled_Glass;
		}
		if (type == Adjectif.orangeCrystal.GetType())
		{
			return IconKeys._Stockpiled_Raw_Orange_Crystal;
		}
		if (type == Adjectif.blueCrystal.GetType())
		{
			return IconKeys._Stockpiled_Raw_Blue_Crystal;
		}
		if (type == Adjectif.purifiedBlueCrystal.GetType())
		{
			Economy.DarkCrystals.EnsureIcons();
			return Economy.DarkCrystals.StockpiledIcon ?? IconKeys.Missing;
		}
		if (type == Adjectif.terracotta.GetType() || type == Adjectif.clay.GetType())
		{
			Metallurgy.Metallurgy.EnsureIcons();
			return (type == Adjectif.clay.GetType()) ? Metallurgy.Metallurgy.CoalStockpiledIcon : Metallurgy.Metallurgy.SteelStockpiledIcon;
		}
		return IconKeys.Missing;
	}
}
