using System.Collections.Generic;
using System.Text;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.UI.Icons;
using CastleStoryPlus.Core;

namespace CastleStoryPlus.Upgrades;

internal enum UpgradeHall
{
	Smithy,
	Armoury,
	Research
}

internal sealed class Ingredient
{
	public Ressource Resource;

	public int Count;

	public string Name;

	public IconKey Icon;
}

// One upgrade line (e.g. Chainmail) with three tiers: Iron, Steel, Crystal (Novice, Adept, Master for research). Each
// tier is a crafting recipe in its building; finishing it unlocks the tier for the whole faction.
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

	// How many tiers the line has: three, or one for a line that only unlocks something.
	public int Tiers = UpgradeLines.MaxTier;

	public RecipeInfo[] Recipe = new RecipeInfo[UpgradeLines.MaxTier + 1];

	// A line added after the market's coal and steel trades existed: its recipes are made after those trades, so
	// their recipe ids (kept in saved queues) stay as they were.
	public bool Late;
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

	// Research station: lines for the whole colony, paid in dark crystals.
	public const int Housing = 10;

	public const int WorkMethods = 11;

	public const int Stacking = 12;

	public const int CrystalAttunement = 13;

	public const int LightBoots = 14;

	public const int FireflyLore = 15;

	public const int Giants = 16;

	public const int Scouting = 17;

	public const int EfficientMining = 18;

	public static readonly string[] TierNames = { string.Empty, "Iron", "Steel", "Crystal" };

	public static readonly string[] ResearchTierNames = { string.Empty, "Novice", "Adept", "Master" };

	// Work (hammer strokes) per tier.
	private static readonly int[] Work = { 0, 20, 35, 50 };

	// Research station: seconds of work per tier (ResearchTimePatch counts it in time, not in strokes).
	public const int ResearchSeconds = 300;

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
		Add(Housing, "housing", "Housing", UpgradeHall.Research, "The colony", "Plans for denser living quarters. The home crystal keeps more bricktrons.",
			new[] { 0f, 5f, 10f, 15f }, "+{2} bricktrons",
			Cost(Crystal, 30), Cost(Crystal, 60), Cost(Crystal, 120));
		Add(WorkMethods, "work_methods", "Work Methods", UpgradeHall.Research, "All bricktrons", "Studied ways to dig, chop, build and craft. Every bricktron works faster.",
			new[] { 0f, 0.1f, 0.2f, 0.3f }, "+{0}% work speed",
			Cost(Crystal, 20), Cost(Crystal, 40), Cost(Crystal, 80));
		Add(Stacking, "stacking", "Stacking", UpgradeHall.Research, "Stockpiles", "Better ways to stack goods. Every stockpile and warehouse holds more.",
			new[] { 0f, 1f / 3f, 2f / 3f, 1f }, "+{0}% storage room",
			Cost(Crystal, 20), Cost(Crystal, 40), Cost(Crystal, 80));
		Add(CrystalAttunement, "crystal_attunement", "Crystal Attunement", UpgradeHall.Research, "Home crystal", "The home crystal shapes new bricktrons from less energy.",
			new[] { 0f, 0.15f, 0.3f, 0.45f }, "-{0}% energy for a new bricktron",
			Cost(Crystal, 20), Cost(Crystal, 40), Cost(Crystal, 80));
		Add(LightBoots, "light_boots", "Light Boots", UpgradeHall.Research, "All bricktrons", "Lighter boots with better soles. Bricktrons walk and run faster.",
			new[] { 0f, 0.05f, 0.1f, 0.15f }, "+{0}% walking speed",
			Cost(Crystal, 20), Cost(Crystal, 40), Cost(Crystal, 80));
		Add(FireflyLore, "firefly_lore", "Firefly Lore", UpgradeHall.Research, "Home crystal", "Knowledge of fireflies. Each brewed firefly carries more energy.",
			new[] { 0f, 0.1f, 0.2f, 0.3f }, "+{0}% energy per firefly",
			Cost(Crystal, 20), Cost(Crystal, 40), Cost(Crystal, 80));
		Add(Giants, "giants", "Giant Bricktrons", UpgradeHall.Research, "Two workers", "How to merge two workers into one giant bricktron. Needed before any giant can be made.",
			new[] { 0f, 1f }, "giant bricktrons can be made",
			Cost(Crystal, 60));
		Add(Scouting, "scouting", "Scouting", UpgradeHall.Research, "Invasion", "Scouts watch the corruptron spawn points and warn the colony early. More time between waves.",
			new[] { 0f, 0.15f, 0.3f, 0.5f }, "+{0}% time between waves",
			Cost(Crystal, 20), Cost(Crystal, 40), Cost(Crystal, 80));
		Add(EfficientMining, "efficient_mining", "Efficient Mining", UpgradeHall.Research, "Diggers", "Better ways to break rock. Digging out terrain and mining rocks and ore deposits gives more.",
			new[] { 0f, 0.1f, 0.2f, 0.3f }, "+{0}% stone, ore and crystal from digging and mining",
			Cost(Crystal, 20), Cost(Crystal, 40), Cost(Crystal, 80));
		All[EfficientMining].Late = true;
		RegisterRecipes(UpgradeHall.Smithy, UpgradeHall.Armoury);
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
			Cost = new Ingredient[MaxTier + 1][],
			Tiers = cost.Length
		};
		for (int tier = 1; tier <= line.Tiers; tier++)
		{
			line.Effect[tier] = string.Format(effect, UnityEngine.Mathf.RoundToInt(value[tier] * 100f), UnityEngine.Mathf.RoundToInt(tier * ShieldRimsDegreesPerTier), UnityEngine.Mathf.RoundToInt(value[tier]));
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

	// The tier's name as the upgrade window shows it.
	public static string TierName(UpgradeLine line, int tier)
	{
		if (line.Tiers == 1)
		{
			return "Unlocked";
		}
		return (line.Hall == UpgradeHall.Research) ? ResearchTierNames[tier] : TierNames[tier];
	}

	// Whether the faction has researched the line's first tier. Always true without the upgrades (the line is not
	// there), so what it unlocks is open as before.
	public static bool IsResearched(Faction faction, int line)
	{
		return line >= All.Count || TeamUpgrades.Tier(faction, line) > 0;
	}

	public static bool IsResearch(RecipeInfo recipe)
	{
		return TryGetResearch(recipe, out UpgradeLine line, out int _) && line.Hall == UpgradeHall.Research;
	}

	// One crafting recipe per tier. The recipe ids are made in the same order on every machine (and kept in the
	// workshops' queues and saves, see WideRecipeIds), after the game's own recipes (and the market's
	// trades). The smithy's and the armoury's are made when the plugin starts; the research station's after the
	// metallurgy recipes (ResearchRecipesPatch), so the ids of the recipes made before it stay as they were. The
	// recipe produces nothing (UpgradeResearchPatch unlocks the tier instead); Creates only has to be a valid item,
	// and dropping on completion skips the check for room to store it.
	internal static void RegisterRecipes(params UpgradeHall[] halls)
	{
		RegisterRecipes(false, halls);
	}

	// The late lines' recipes (see UpgradeLine.Late), after every other recipe.
	internal static void RegisterLateRecipes()
	{
		RegisterRecipes(true, UpgradeHall.Smithy, UpgradeHall.Armoury, UpgradeHall.Research);
	}

	private static void RegisterRecipes(bool late, params UpgradeHall[] halls)
	{
		if (CookBook.Sword == null)
		{
			return;
		}
		int before = ByRecipe.Count;
		foreach (UpgradeLine line in All)
		{
			if (System.Array.IndexOf(halls, line.Hall) < 0 || line.Recipe[1] != null || line.Late != late)
			{
				continue;
			}
			for (int tier = 1; tier <= line.Tiers; tier++)
			{
				RecipeMaker maker = new RecipeMaker
				{
					Work = (line.Hall == UpgradeHall.Research) ? ResearchSeconds : Work[tier],
					Heat = 0,
					Creates = Accessories.Sword,
					QteCreated = 1,
					DropOnCompletion = true,
					Icon = HallIcon(line.Hall),
					Drawing = HallIcon(line.Hall)
				};
				foreach (Ingredient ingredient in line.Cost[tier])
				{
					maker.With(ingredient.Resource, ingredient.Count);
				}
				RecipeInfo recipe = maker.Make();
				if (recipe.id > Core.WideRecipeIds.MaxId)
				{
					Plugin.Log.LogError("Upgrades: recipe id " + recipe.id + " is too high for the crafting queue, upgrades left out");
					return;
				}
				line.Recipe[tier] = recipe;
				ByRecipe[recipe.id] = new KeyValuePair<UpgradeLine, int>(line, tier);
			}
		}
		if (ByRecipe.Count == before)
		{
			return;
		}
		Plugin.Log.LogInfo("Upgrades: " + (ByRecipe.Count - before) + " research recipes registered (" + string.Join(", ", System.Array.ConvertAll(halls, (UpgradeHall hall) => hall.ToString())) + ")");
	}

	private static IconKey HallIcon(UpgradeHall hall)
	{
		switch (hall)
		{
		case UpgradeHall.Smithy:
			return IconKeys._Sword;
		case UpgradeHall.Armoury:
			return IconKeys._Shield;
		default:
			return IconKeys._Lab;
		}
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
			for (int tier = 1; tier <= line.Tiers; tier++)
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
