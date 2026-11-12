using System.Collections.Generic;
using Brix.Game.AI;
using Brix.External.Factories;
using Brix.Game.AI.InstructionType;
using Brix.Game.Components;
using Brix.Game.Semantique;
using CastleStoryPlus.Core;
using CastleStoryPlus.Workers;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Army;

// "Guard here" for ranged soldiers (archer, arbalist, alchemist, artificer), as melee soldiers have "Defend position":
// the game's only stance for them was "Hold fire", which stops them shooting. A guarding ranged soldier stays on the
// place it stands on and shoots every enemy it can hit from there: it never leaves it to get a clear shot
// (RangedReposition) or to run at an enemy, and when something pushed it off (a fight hand to hand) it walks back once
// it is idle. A move order from the player takes the guard along: the soldier guards the place it was sent to.
// "Stop guarding" in the same wheel slot ends it. The guard is kept with the unit (saved, synced for the wheel's
// label); the host runs it.
[Feature(Features.RangedGuard, Features.RangedGuardInfo)]
internal class RangedGuard : MonoBehaviour
{
	private const float CheckSeconds = 1f;

	// Further from its place than this, an idle guard walks back.
	private const float ReturnDistance = 1.5f;

	// A walk back that did not get there is not tried again before this.
	private const float RetrySeconds = 5f;

	private class Post
	{
		public bool HasAnchor;

		public Vector3 Anchor;

		public float NextTry;
	}

	// Host: the guards seen so far and the place each guards (set once it stands idle after a player's order).
	private static readonly Dictionary<Labor, Post> Guards = new Dictionary<Labor, Post>();

	private float _nextCheck;

	private static void Enable()
	{
		GameSession.OnLeave(Guards.Clear);
		Plugin.Root.AddComponent<RangedGuard>();
	}

	internal static bool IsRanged(Labor labor)
	{
		return !labor.IsNullOrReleased() && labor.Occupation != null && labor.Occupation.CurrentOccupation == Occupation.Type.Archer;
	}

	// Whether the unit is a ranged soldier set to guard (on every peer).
	public static bool IsGuarding(Labor labor)
	{
		if (!Plugin.IsEnabled(Features.RangedGuard) || !IsRanged(labor))
		{
			return false;
		}
		WorkerStats stats = WorkerStats.Peek(labor.state);
		return stats != null && stats.Guard != 0;
	}

	// Host: starts or ends the guard; it guards where it stands now.
	public static void Set(Labor labor, bool on)
	{
		if (labor == null || labor.state == null)
		{
			return;
		}
		WorkerStats.Modify(labor.state, (WorkerStats s) => s.Guard = on ? 1 : 0);
		if (!on)
		{
			Guards.Remove(labor);
			return;
		}
		Guards[labor] = new Post { HasAnchor = true, Anchor = labor.transform.position };
		// Hold fire is the opposite of guarding (the artificer's same toggle picks fire or heal: left as it is).
		if (labor.Autonomy == AutonomyStatus.HoldingPosition && !labor.toolbag.HasTool(Accessories.Staff))
		{
			labor.Autonomy = AutonomyStatus.InstinctOnly;
		}
	}

	// Host: a player's order moves the guard to where it ends.
	internal static void OnPlayerOrder(Labor labor)
	{
		if (Guards.TryGetValue(labor, out Post post))
		{
			post.HasAnchor = false;
		}
	}

	private void Update()
	{
		if (!NetworkServer.active || Time.time < _nextCheck)
		{
			return;
		}
		_nextCheck = Time.time + CheckSeconds;
		CollectGuards();
		List<Labor> gone = null;
		foreach (KeyValuePair<Labor, Post> entry in Guards)
		{
			Labor labor = entry.Key;
			if (!IsGuarding(labor))
			{
				if (gone == null)
				{
					gone = new List<Labor>();
				}
				gone.Add(labor);
				continue;
			}
			if (labor.CurrentTask != null || labor.PendingTask != null)
			{
				continue;
			}
			Post post = entry.Value;
			Vector3 at = labor.transform.position;
			if (!post.HasAnchor)
			{
				post.HasAnchor = true;
				post.Anchor = at;
				continue;
			}
			if ((at - post.Anchor).sqrMagnitude <= ReturnDistance * ReturnDistance || Time.time < post.NextTry)
			{
				continue;
			}
			post.NextTry = Time.time + RetrySeconds;
			Order.Package.OfLocation(Order.Move.RunToward, Location.Of(post.Anchor), Labor.DecisionSource.Character).Send(labor);
		}
		if (gone != null)
		{
			foreach (Labor labor in gone)
			{
				Guards.Remove(labor);
			}
		}
	}

	// Guards from a save (or set before this host took over) are found among the units' stats.
	private static void CollectGuards()
	{
		foreach (KeyValuePair<CharacterState, WorkerStats> entry in WorkerStats.Entries)
		{
			if (entry.Value.Guard == 0 || entry.Key == null)
			{
				continue;
			}
			Labor labor = entry.Key.GetComponent<Labor>();
			if (labor != null && !Guards.ContainsKey(labor) && IsGuarding(labor))
			{
				Guards[labor] = new Post();
			}
		}
	}
}

// A guard never leaves its place for an enemy: what the instinct decides (run at it, walk to a clear shot, flee when
// outnumbered) becomes holding the place and shooting when it can.
[Feature(Features.RangedGuard, Features.RangedGuardInfo)]
[HarmonyPatch(typeof(LaborInstinct), "GetPackage")]
internal static class RangedGuardInstinctPatch
{
	[HarmonyPriority(Priority.Last)]
	private static void Postfix(Labor ___labor, GameObject enemy, ref OrderPackage<Labor> __result)
	{
		if (enemy == null || !(__result is OrderPackage<Labor, GameObject> package) || package.InstructionValue == Order.Combat.HoldPositionAndAttackWithPriority)
		{
			return;
		}
		if (!RangedGuard.IsGuarding(___labor))
		{
			return;
		}
		__result = Order.Package.OfGameObject(Order.Combat.HoldPositionAndAttackWithPriority, enemy, Labor.DecisionSource.Character);
	}
}

// A player's order: the guard goes along and guards the place where the order ends.
[Feature(Features.RangedGuard, Features.RangedGuardInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.SendOrder))]
internal static class RangedGuardOrderPatch
{
	private static void Prefix(Labor __instance, Labor.DecisionSource source)
	{
		if (source == Labor.DecisionSource.User)
		{
			RangedGuard.OnPlayerOrder(__instance);
		}
	}
}
