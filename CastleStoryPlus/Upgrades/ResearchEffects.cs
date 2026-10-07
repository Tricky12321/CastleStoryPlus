using System;
using System.Collections.Generic;
using System.Reflection;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Locomotion;
using Brix.Game.Rules;
using Brix.Game.Semantique;
using Brix.Lua;
using CastleStoryPlus.Core;
using CastleStoryPlus.Experience;
using CastleStoryPlus.Giant;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;

namespace CastleStoryPlus.Upgrades;

// What the research station's tiers do. The game's rules for the home crystal (the bricktron limit, the energy a
// new bricktron costs) are global, so the faction they are asked for is set while the home crystal works them out;
// without one (the game's interface) it is the local player's faction.
internal static class ResearchEffects
{
	// The faction whose home crystal is working out its spawns, or null.
	internal static Faction Context;

	internal static Faction RulesFaction => Context ?? ((User.LocalUser != null) ? User.LocalUser.faction : null);

	internal static float Value(Faction faction, int line)
	{
		int tier = TeamUpgrades.Tier(faction, line);
		return (tier > 0) ? UpgradeLines.All[line].Value[tier] : 0f;
	}

	// Housing: the bricktron limit plus the researched bricktrons, read from the rules' field (the getter is patched
	// as well, for the interface).
	internal static int Cap(GeneralRules rules)
	{
		return rules._bricktronCap + Mathf.RoundToInt(Value(RulesFaction, UpgradeLines.Housing));
	}

	internal static bool ExperienceOn => Plugin.Cfg.Bind("Features", Features.Experience, true, Features.ExperienceInfo).Value;
}

// Housing, for the interface (the population count in the game menu reads the rules from Lua).
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(GeneralRules), nameof(GeneralRules.bricktronCap), MethodType.Getter)]
internal static class HousingCapPatch
{
	private static void Postfix(GeneralRules __instance, ref int __result)
	{
		__result = ResearchEffects.Cap(__instance);
	}
}

// Housing, for the home crystal: its checks read the limit through ResearchEffects.Cap (a getter this small may be
// inlined, which would skip the patch above).
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch]
internal static class HousingSpawnPatch
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(FireflyNest), "SpawnNamedFireflies");
		yield return AccessTools.Method(typeof(FireflyNest.Brewery), "CanBrew");
	}

	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		MethodInfo getter = AccessTools.PropertyGetter(typeof(GeneralRules), nameof(GeneralRules.bricktronCap));
		MethodInfo cap = AccessTools.Method(typeof(ResearchEffects), nameof(ResearchEffects.Cap));
		foreach (CodeInstruction instruction in instructions)
		{
			if (instruction.Calls(getter))
			{
				instruction.opcode = System.Reflection.Emit.OpCodes.Call;
				instruction.operand = cap;
			}
			yield return instruction;
		}
	}
}

// The home crystal's faction while it spawns bricktrons or works out their cost.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch]
internal static class ResearchNestContextPatch
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(FireflyNest), "SpawnNamedFireflies");
		yield return AccessTools.Method(typeof(FireflyNest), "ProcessHomeSpawns");
		yield return AccessTools.PropertyGetter(typeof(FireflyNest), nameof(FireflyNest.NewFireflyRequiredEnergy));
	}

	private static void Prefix(FireflyNest __instance, out Faction __state)
	{
		__state = ResearchEffects.Context;
		ResearchEffects.Context = __instance.faction;
	}

	private static Exception Finalizer(Exception __exception, Faction __state)
	{
		ResearchEffects.Context = __state;
		return __exception;
	}
}

[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(FireflyNest.Brewery), "CanBrew")]
internal static class ResearchBreweryContextPatch
{
	private static void Prefix(FireflyNest.Brewery __instance, out Faction __state)
	{
		__state = ResearchEffects.Context;
		ResearchEffects.Context = (__instance.Nest != null) ? __instance.Nest.faction : null;
	}

	private static Exception Finalizer(Exception __exception, Faction __state)
	{
		ResearchEffects.Context = __state;
		return __exception;
	}
}

// Crystal Attunement: a new bricktron costs less energy.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(GeneralRules), nameof(GeneralRules.GetHomeCrystalSpawnRequiredEnergy))]
internal static class CrystalAttunementPatch
{
	private static void Postfix(ref int __result)
	{
		float less = ResearchEffects.Value(ResearchEffects.RulesFaction, UpgradeLines.CrystalAttunement);
		if (less > 0f)
		{
			__result = Mathf.Max(1, Mathf.RoundToInt(__result * (1f - less)));
		}
	}
}

// Firefly Lore: each brewed firefly carries more energy.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(FireflyNest.Brewery), nameof(FireflyNest.Brewery.ProcessFirefly))]
internal static class FireflyLorePatch
{
	private static void Postfix(FireflyNest.Brewery __instance, GameObject go)
	{
		Firefly firefly = (go != null) ? go.GetComponent<Firefly>() : null;
		float more = (__instance.Nest != null) ? ResearchEffects.Value(__instance.Nest.faction, UpgradeLines.FireflyLore) : 0f;
		if (firefly != null && more > 0f)
		{
			firefly.pureEnergy = Mathf.RoundToInt(firefly.pureEnergy * (1f + more));
		}
	}
}

// Light Boots: faster walking and running.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch]
internal static class LightBootsPatch
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.PropertyGetter(typeof(Walking), "SpeedMperS");
		yield return AccessTools.PropertyGetter(typeof(Running), "SpeedMperS");
	}

	private static void Postfix(LocomotionWalkableCycleState __instance, ref float __result)
	{
		Locomotion4 locomotion = __instance.Locomotion;
		if (locomotion != null)
		{
			__result *= 1f + TeamUpgrades.Value(locomotion.gameObject, UpgradeLines.LightBoots);
		}
	}
}

// Work Methods: faster work. The work speed is the animation speed while working (as for the work level and the
// giant bricktron); set after them, from the same parts, so it is never multiplied twice.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(Locomotion4), nameof(Locomotion4.DoWork))]
[HarmonyPriority(Priority.Last)]
internal static class WorkMethodsStartPatch
{
	private static void Postfix(Locomotion4 __instance)
	{
		Labor labor = __instance.GetComponent<Labor>();
		float more = TeamUpgrades.Value(__instance.gameObject, UpgradeLines.WorkMethods);
		if (labor == null || more <= 0f || __instance.animator == null)
		{
			return;
		}
		float speed = GiantBricktron.SpeedFactor(labor);
		if (ResearchEffects.ExperienceOn)
		{
			speed *= WorkerExperience.Multiplier(WorkerExperience.WorkLevel(labor));
		}
		__instance.animator.speed = speed * (1f + more);
	}
}

[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(Locomotion4), nameof(Locomotion4.StopWork))]
[HarmonyPriority(Priority.Last)]
internal static class WorkMethodsStopPatch
{
	private static void Prefix(Locomotion4 __instance)
	{
		Labor labor = __instance.GetComponent<Labor>();
		if (labor != null && __instance.animator != null && TeamUpgrades.Tier(__instance.gameObject, UpgradeLines.WorkMethods) > 0)
		{
			__instance.animator.speed = GiantBricktron.SpeedFactor(labor);
		}
	}
}

// Stacking: stockpiles and warehouses hold more. Their room for each resource (and their bulk limit) is scaled from
// what they had before; checked every few seconds on every machine, so new stockpiles and new tiers are picked up.
// A third more per tier, twice the room at the top: goods a stockpile stacks in layers (blocks, planks, ingots,
// barrels...) go from 2 layers up to 4 (StackingVisual draws the extra layers, and the crates' heaps higher).
// Tool racks keep their room for tools.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
internal class Stacking : MonoBehaviour
{
	private const float CheckSeconds = 3f;

	private static readonly Dictionary<Recepteur, Dictionary<Type, int>> Original = new Dictionary<Recepteur, Dictionary<Type, int>>();

	private static readonly Dictionary<Recepteur, float> Applied = new Dictionary<Recepteur, float>();

	private float _next;

	private static void Enable()
	{
		Plugin.Root.AddComponent<Stacking>();
		GameSession.OnLeave(() =>
		{
			Original.Clear();
			Applied.Clear();
		});
	}

	private void Update()
	{
		if (Time.unscaledTime < _next)
		{
			return;
		}
		_next = Time.unscaledTime + CheckSeconds;
		if (User.LocalUser == null)
		{
			return;
		}
		foreach (Recepteur recepteur in Live<Recepteur>.Active())
		{
			if (recepteur == null || recepteur.Fonction != FonctionDeContenant.entrepot)
			{
				continue;
			}
			float factor = 1f + TeamUpgrades.Value(recepteur.gameObject, UpgradeLines.Stacking);
			if (!(Applied.TryGetValue(recepteur, out float was) ? Mathf.Approximately(was, factor) : Mathf.Approximately(factor, 1f)))
			{
				Apply(recepteur, factor);
			}
			StackingVisual.Refresh(recepteur);
		}
		Prune();
	}

	private static void Apply(Recepteur recepteur, float factor)
	{
		if (!Original.TryGetValue(recepteur, out Dictionary<Type, int> original))
		{
			original = new Dictionary<Type, int>();
			foreach (KeyValuePair<Type, Adjectif> entry in recepteur._baseCapacity.DicoAdjectif)
			{
				if (entry.Value is Ressource || entry.Key == Adjectif.encombrement.GetType())
				{
					original[entry.Key] = entry.Value.quantifiable.valeur;
				}
			}
			Original[recepteur] = original;
		}
		Description capacity = recepteur._baseCapacity;
		foreach (KeyValuePair<Type, int> entry in original)
		{
			// A new entry, not a changed one: the old Adjectif may be shared with the stockpile's template.
			capacity.DicoAdjectif.Remove(entry.Key);
			capacity.DicoAdjectif.Add(Adjectif.New(entry.Key, StackingVisual.Room(recepteur, entry.Key, entry.Value, factor)));
		}
		capacity.DicoAdjectif.Preloaded = false;
		recepteur.ResetDescriptions();
		Applied[recepteur] = factor;
	}

	// The room a stockpile had for a resource before Stacking, 0 when Stacking has not changed it.
	internal static int OriginalRoom(Recepteur recepteur, Type type)
	{
		return (recepteur != null && Original.TryGetValue(recepteur, out Dictionary<Type, int> original) && original.TryGetValue(type, out int room)) ? room : 0;
	}

	private static void Prune()
	{
		List<Recepteur> gone = null;
		foreach (Recepteur recepteur in Applied.Keys)
		{
			if (recepteur == null)
			{
				if (gone == null)
				{
					gone = new List<Recepteur>();
				}
				gone.Add(recepteur);
			}
		}
		if (gone == null)
		{
			return;
		}
		foreach (Recepteur recepteur in gone)
		{
			Applied.Remove(recepteur);
			Original.Remove(recepteur);
		}
	}
}

// The research station's recipes, after the metallurgy recipes (made first here if their own postfix has not run
// yet), so the ids of every recipe made before stay as they were.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(LuaCrafting), nameof(LuaCrafting.Register))]
[HarmonyPriority(Priority.Low)]
internal static class ResearchRecipesPatch
{
	private static void Postfix()
	{
		Metallurgy.MetallurgyLuaPatch.Run();
		UpgradeLines.RegisterRecipes(UpgradeHall.Research);
	}
}

// The research station counts its work in time: a tier takes ResearchSeconds of work (5 minutes for one worker),
// whatever the speed of the work animation. Each stroke takes off the time since the last stroke; after a break
// (the worker was called away) a stroke counts as one second. The rest is kept in the recipe, so the research goes
// on where it stopped, also after loading a save. Patched in the crafting work step rather than Recipe.WorkOn, which
// is small enough to be inlined.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(CraftingInstructions), "Work")]
internal static class ResearchTimePatch
{
	internal const float BreakSeconds = 3f;

	private static readonly Dictionary<Recipe, float> LastStroke = new Dictionary<Recipe, float>();

	private static readonly Dictionary<Recipe, float> Carry = new Dictionary<Recipe, float>();

	// Stations whose research had a stroke lately, for the progress bar over the worker.
	internal static readonly HashSet<CraftingStation> Working = new HashSet<CraftingStation>();

	// How far the station's current research is (0 to 1), or -1 when nobody worked on it in the last few seconds.
	internal static float Progress(CraftingStation station)
	{
		Recipe recipe = (station != null) ? station.CurrentRecipe : null;
		if (recipe == null || recipe.Info == null || recipe.Info.work <= 0 || !LastStroke.TryGetValue(recipe, out float last) || Time.time - last > BreakSeconds)
		{
			return -1f;
		}
		Carry.TryGetValue(recipe, out float carry);
		return Mathf.Clamp01(1f - (recipe.work - carry) / recipe.Info.work);
	}

	private static bool Prefix(CraftingStation station, ref Node __result)
	{
		Recipe recipe = (station != null) ? station.CurrentRecipe : null;
		if (recipe == null || !UpgradeLines.IsResearch(recipe.Info))
		{
			return true;
		}
		float now = Time.time;
		float seconds = (LastStroke.TryGetValue(recipe, out float last) && now - last <= BreakSeconds) ? Mathf.Max(0f, now - last) : 1f;
		LastStroke[recipe] = now;
		Working.Add(station);
		Carry.TryGetValue(recipe, out float carry);
		carry += seconds;
		int whole = Mathf.FloorToInt(carry);
		Carry[recipe] = carry - whole;
		recipe.work = Mathf.Max(0, recipe.work - whole);
		if (recipe.work == 0)
		{
			LastStroke.Remove(recipe);
			Carry.Remove(recipe);
		}
		station.WorkContinued();
		__result = Node.Empty;
		return false;
	}
}

// Scouting: more time between invasion waves. The wave timer is one for the whole game, so the best tier any team
// has researched counts. Only the timer started when a wave arrives grows; the first wave keeps its time.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
internal static class ScoutingWaves
{
	private static void Enable()
	{
		LuaInjection.AddFunction("WaveTimeFactor", (MoonSharp.Interpreter.ScriptExecutionContext context, MoonSharp.Interpreter.CallbackArguments args) =>
		{
			int tier = TeamUpgrades.HighestTier(UpgradeLines.Scouting);
			float factor = (tier > 0) ? 1f + UpgradeLines.All[UpgradeLines.Scouting].Value[tier] : 1f;
			return MoonSharp.Interpreter.DynValue.NewNumber(factor);
		});
		LuaInjection.AddPatch(Features.Upgrades, "Gamemodes/invasion/invasion.lua", "\t\tRegistry.waveTimer = Timer.New(sv_Settings.waveDuration)\n", LuaInjection.Mode.Replace,
			"\t\t---Castle Story Plus (Upgrades, Scouting): more time between waves\n"
			+ "\t\tRegistry.waveTimer = Timer.New(sv_Settings.waveDuration * CastleStoryPlus.WaveTimeFactor())\n");
	}
}
