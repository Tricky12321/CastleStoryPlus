using System.Collections.Generic;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Legacy;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Nature;

// The enemy waves could not get through trees: a place beside a tree is a "forest" step of the route graph, which only
// a unit already standing in a forest may take, and the enemies break blocks but trees are not blocks. Now that trees
// can be planted, a row of them shut the enemies out of a base for good. Now an enemy that breaks its way (corruptron,
// biftron, minitron, magitron) may plan its route through trees, and fells a tree only when it is in its way: while
// it follows a route and has not moved for StuckChecks checks in a row, the tree beside it that lies towards its next
// route step is struck every StrikeSeconds for StrikeDamage, as a bricktron's axe strikes it (a tree has 180), so it
// falls and drops its logs like any felled tree. An enemy walking past trees, fighting, breaking blocks or waiting
// leaves them standing. Host only; the tree's fall is the game's own and reaches every player.
[Feature(Features.EnemyTreeClearing, Features.EnemyTreeClearingInfo)]
internal class EnemyTreeClearing : MonoBehaviour
{
	private const float CheckSeconds = 0.5f;

	private const float StrikeSeconds = 1.2f;

	private const int StrikeDamage = 60;

	// Moved less than this in a check: not getting on.
	private const float StuckDistance = 0.2f;

	// Checks in a row without getting on before a tree in the way is struck (1 s).
	private const int StuckChecks = 2;

	// How nearly towards the next route step a tree must lie (cosine of the angle).
	private const float AheadCosine = 0.5f;

	private sealed class Watch
	{
		internal Vector3 Last;

		internal int Stuck;

		internal float NextStrike;
	}

	private static readonly Dictionary<Labor, Watch> Watched = new Dictionary<Labor, Watch>();

	private static readonly List<Labor> Gone = new List<Labor>();

	private float _nextCheck;

	private static void Enable()
	{
		GameSession.OnLeave(() => Watched.Clear());
		Plugin.Root.AddComponent<EnemyTreeClearing>();
	}

	// An enemy wave unit that breaks its way.
	internal static bool IsClearer(Labor labor)
	{
		Faction faction = (labor != null) ? labor.faction : null;
		return faction != null && faction.isAI && labor.Profession != null && labor.Profession.CanMakeWay() && Treasure.Beasts.KindOf(labor) == null;
	}

	private void Update()
	{
		float now = Time.time;
		if (!NetworkServer.active || now < _nextCheck)
		{
			return;
		}
		_nextCheck = now + CheckSeconds;
		TreeRegistry registry = TreeRegistry.Instance;
		if (registry == null)
		{
			return;
		}
		foreach (Labor labor in Live<Labor>.Active())
		{
			if (labor.IsNullOrReleased() || !IsClearer(labor))
			{
				continue;
			}
			Vector3 position = labor.transform.position;
			if (!Watched.TryGetValue(labor, out Watch watch))
			{
				watch = new Watch { Last = position };
				Watched[labor] = watch;
			}
			bool still = (position - watch.Last).sqrMagnitude < StuckDistance * StuckDistance;
			watch.Last = position;
			// Only on its way somewhere: not while it fights, breaks a block or waits.
			if (!still || !NextStep(labor, position, out Vector3 heading))
			{
				watch.Stuck = 0;
				continue;
			}
			watch.Stuck++;
			if (watch.Stuck < StuckChecks || now < watch.NextStrike)
			{
				continue;
			}
			XYZ cell = XYZ.FromVector3(position);
			if (!registry.IsNearATree(cell))
			{
				continue;
			}
			if (!FindTree(registry, cell, position, heading, out XYZ tree))
			{
				continue;
			}
			watch.NextStrike = now + StrikeSeconds;
			Strike(labor, tree);
		}
		foreach (KeyValuePair<Labor, Watch> entry in Watched)
		{
			if (entry.Key == null)
			{
				Gone.Add(entry.Key);
			}
		}
		foreach (Labor labor in Gone)
		{
			Watched.Remove(labor);
		}
		Gone.Clear();
	}

	// The direction to the next step of the route the unit follows; false when it follows none.
	private static bool NextStep(Labor labor, Vector3 position, out Vector3 heading)
	{
		heading = Vector3.zero;
		DestinationIterator path = (labor.navigation != null && labor.navigation.Locomotion != null) ? labor.navigation.Locomotion.PathIterator : null;
		if (path == null || !path.HasCurrent)
		{
			return false;
		}
		for (int i = 0; i < 3; i++)
		{
			WalkNode node = path.GetNth(path.CurrentIndex + i);
			if (node == null)
			{
				break;
			}
			Vector3 to = node.Position - position;
			to.y = 0f;
			if (to.sqrMagnitude > 0.25f)
			{
				heading = to;
				return true;
			}
		}
		return false;
	}

	// The tree beside the unit most nearly towards its next route step.
	private static bool FindTree(TreeRegistry registry, XYZ cell, Vector3 position, Vector3 heading, out XYZ tree)
	{
		tree = XYZ.zero;
		float best = AheadCosine;
		bool found = false;
		Vector3 flat = new Vector3(heading.x, 0f, heading.z);
		if (flat.sqrMagnitude > 0.0001f)
		{
			flat.Normalize();
		}
		foreach (XYZ offset in TreeRegistry.TreeNeighborhood)
		{
			XYZ at = cell + offset;
			if (!registry.HasTree(at))
			{
				continue;
			}
			Vector3 to = at.ToVector3() - position;
			to.y = 0f;
			float ahead = (to.sqrMagnitude > 0.0001f) ? Vector3.Dot(flat, to.normalized) : 1f;
			if (ahead > best)
			{
				best = ahead;
				tree = at;
				found = true;
			}
		}
		return found;
	}

	private static void Strike(Labor labor, XYZ position)
	{
		DamagedTree tree = DamagedTree.GetAtOrCreate(position);
		if (tree.IsNullOrReleased() || tree.IsDying)
		{
			return;
		}
		Faction faction = labor.affiliation.faction;
		tree.GetOrAddComponent<AffiliationComponent>().faction = faction;
		labor.transform.rotation = Quaternion.LookRotation(Flat(position.ToVector3() - labor.transform.position, labor.transform.forward));
		tree.Strike(position.ToScalaire(), StrikeDamage);
	}

	private static Vector3 Flat(Vector3 v, Vector3 fallback)
	{
		v.y = 0f;
		return (v.sqrMagnitude > 0.0001f) ? v : fallback;
	}
}

// Enemies that break their way may plan routes through trees (which they fell on the way).
[Feature(Features.EnemyTreeClearing, Features.EnemyTreeClearingInfo)]
[HarmonyPatch(typeof(Profession), nameof(Profession.CanWalkInForest))]
internal static class EnemyTreeClearingRoutePatch
{
	private static void Postfix(Profession __instance, ref bool __result)
	{
		if (!__result && EnemyTreeClearing.IsClearer(__instance.GetComponent<Labor>()))
		{
			__result = true;
		}
	}
}
