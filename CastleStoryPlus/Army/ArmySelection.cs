using System;
using System.Collections.Generic;
using Brix.Game;
using Brix.Game.AI;
using Brix.UI;
using Brix.UI.GameSelector;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Army;

// The task list's melee and ranged units (and the game's Lua menu's): a click selects every soldier of the melee or
// the ranged jobs, the ones the row shows. The game took the idle group's crew whose gear makes them an archer at
// that moment (Profession.InferedArchetype: a ranged weapon with ammunition and the stamina for a shot). A client
// works that out from its own copy of the weapon, whose ammunition is not sent over the network (only the
// bricktron's total is), and from the idle group's crew as it has tracked it, so there a click picked two or three
// of the ranged units. The job is the same for every player.
[Feature]
[HarmonyPatch(typeof(UIGameActor), nameof(UIGameActor.SelectByOccupation))]
internal static class ArmySelection
{
	internal static readonly Occupation.Job[] MeleeJobs = new Occupation.Job[2] { Occupation.Job.Halberdier, Occupation.Job.Knight };

	internal static readonly Occupation.Job[] RangedJobs = new Occupation.Job[4] { Occupation.Job.Archer, Occupation.Job.Arbalist, Occupation.Job.Artificer, Occupation.Job.Alchemist };

	private static bool Prefix(Occupation.Type occupation, Faction faction, bool visibleOnly)
	{
		if (occupation != Occupation.Type.Knight && occupation != Occupation.Type.Archer)
		{
			return true;
		}
		Select(occupation == Occupation.Type.Archer ? RangedJobs : MeleeJobs, faction, visibleOnly);
		return false;
	}

	// Selects the faction's bricktrons (as the player's interface lists them) of the given jobs; answers how many.
	internal static int Select(Occupation.Job[] jobs, Faction faction, bool visibleOnly)
	{
		List<GameObject> units = new List<GameObject>();
		if (UIGameObserver.bricktrons != null)
		{
			foreach (Labor labor in UIGameObserver.bricktrons.CheckCreation())
			{
				if (labor == null || labor.Occupation == null || Array.IndexOf(jobs, labor.Occupation.CurrentJob) < 0)
				{
					continue;
				}
				if ((faction != null && !faction.IsSame(labor.gameObject)) || (visibleOnly && !UIGameObserver.bricktrons.GetScreenFlag(labor)))
				{
					continue;
				}
				units.Add(labor.gameObject);
			}
		}
		UIGameSelector.Select(units);
		return units.Count;
	}
}
