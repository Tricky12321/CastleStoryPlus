using System.Collections.Generic;
using Brix.Audio;
using Brix.Game.AI;
using Brix.NewUI.OverheadDisplay;
using CastleStoryPlus.Core;
using CastleStoryPlus.Workers;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Experience;

// Name tag: "Bob [Lv 3/2]" (work/combat), the XP bars and the level-up effect.
[Feature(Features.Experience, Features.ExperienceInfo)]
[HarmonyPatch(typeof(BricktronOverheadDisplayDriver), nameof(BricktronOverheadDisplayDriver.InitValues))]
internal static class OverheadXpPatch
{
	// A save being loaded (or a client receiving the initial state) shows up as a big XP jump;
	// real progress arrives a few points at a time. Only the latter gets the level-up effect.
	private const int MaxXpStepForEffect = 10;

	private class Shown
	{
		public BricktronOverheadDisplayDriver Driver;

		public int WorkXp;

		public int CombatXp;
	}

	private static readonly Dictionary<CharacterState, Shown> Drivers = new Dictionary<CharacterState, Shown>();

	private static void Enable()
	{
		WorkerStats.Changed += OnStatsChanged;
		GameSession.OnLeave(Drivers.Clear);
	}

	private static void Postfix(BricktronOverheadDisplayDriver __instance)
	{
		if (__instance.state == null)
		{
			return;
		}
		WorkerStats stats = WorkerStats.Peek(__instance.state);
		Drivers[__instance.state] = new Shown
		{
			Driver = __instance,
			WorkXp = stats?.WorkXp ?? 0,
			CombatXp = stats?.CombatXp ?? 0
		};
		Refresh(__instance);
	}

	internal static string NameWithLevel(BricktronOverheadDisplayDriver driver, string name)
	{
		return " " + name + WorkerExperience.LevelSuffix(driver.labor);
	}

	internal static void Refresh(BricktronOverheadDisplayDriver driver)
	{
		if (driver == null || driver.overheadDisplay == null || !driver.overheadDisplay.NameTag.IsSpawned)
		{
			return;
		}
		NameTag tag = driver.overheadDisplay.NameTag.Get;
		tag.SetName(NameWithLevel(driver, driver.gameObject.name));
		WorkerStats stats = WorkerStats.Peek(driver.state);
		NameTagXp.On(tag).SetXpProgress(WorkerExperience.Progress(stats?.WorkXp ?? 0), WorkerExperience.Progress(stats?.CombatXp ?? 0));
	}

	private static void OnStatsChanged(CharacterState state)
	{
		if (!Drivers.TryGetValue(state, out Shown shown) || shown.Driver == null || shown.Driver.state != state)
		{
			Drivers.Remove(state);
			return;
		}
		Refresh(shown.Driver);
		WorkerStats stats = WorkerStats.Peek(state);
		int workXp = stats?.WorkXp ?? 0;
		int combatXp = stats?.CombatXp ?? 0;
		int work = WorkerExperience.LevelFor(workXp);
		int combat = WorkerExperience.LevelFor(combatXp);
		bool workUp = work > WorkerExperience.LevelFor(shown.WorkXp) && workXp - shown.WorkXp <= MaxXpStepForEffect;
		bool combatUp = combat > WorkerExperience.LevelFor(shown.CombatXp) && combatXp - shown.CombatXp <= MaxXpStepForEffect;
		shown.WorkXp = workXp;
		shown.CombatXp = combatXp;
		string text = (workUp && combatUp) ? ("LEVEL UP! Work " + work + " / Combat " + combat) : (workUp ? ("LEVEL UP! Work " + work) : (combatUp ? ("LEVEL UP! Combat " + combat) : null));
		if (text == null)
		{
			return;
		}
		NameTagXp.On(shown.Driver.overheadDisplay.NameTag.Get).PlayLevelUp(text);
		Labor labor = shown.Driver.labor;
		if (labor != null && labor.Sounds != null)
		{
			SoundEngine.Play(labor.Sounds.TaskCompleted, shown.Driver.gameObject);
		}
	}
}

// Renaming keeps the level suffix.
[Feature(Features.Experience, Features.ExperienceInfo)]
[HarmonyPatch(typeof(BricktronOverheadDisplayDriver), nameof(BricktronOverheadDisplayDriver.OnNameChanged))]
internal static class OverheadRenamePatch
{
	private static void Postfix(BricktronOverheadDisplayDriver __instance, GameObject instance)
	{
		if (__instance != null && instance == __instance.gameObject)
		{
			OverheadXpPatch.Refresh(__instance);
		}
	}
}

// A pooled name tag that is reused starts clean.
[Feature(Features.Experience, Features.ExperienceInfo)]
[HarmonyPatch(typeof(NameTag), nameof(NameTag.OnRequested))]
internal static class NameTagResetPatch
{
	private static void Postfix(NameTag __instance)
	{
		NameTagXp xp = __instance.GetComponent<NameTagXp>();
		if (xp != null)
		{
			xp.ResetTag();
		}
	}
}
