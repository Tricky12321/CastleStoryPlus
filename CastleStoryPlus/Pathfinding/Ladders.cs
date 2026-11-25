using System.Collections.Generic;
using Brix.Components;
using Brix.Engine;
using Brix.Engine.Blocks;
using Brix.Game.AI;
using Brix.Game.Locomotion;
using Brix.Pathfinding;
using Brix.Pathfinding.Test;
using BepInEx.Configuration;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Pathfinding;

// Wooden ladders (the block is made by CustomBlocks): bricktrons climb straight up and down them, one block per
// ladder block, slower than stairs and only with free hands (or a bag), as for climbing a ledge.
//   Blocks: a ladder does not fill its block (floor at the bottom), so a bricktron can stand in it. The voxel above
//   the top ladder (the exit) is a place to stand too, level with the top of the wall the ladder leans on.
//   Graph: ladder blocks and the exit get vertical branches of the game's old Ladder type, moved between climbing and
//   stepping down, so units that may climb use them and loaded ones do not. Ladder blocks above the bottom one
//   connect up and down, and sideways to a place to stand beside them at their level (stepping onto the ladder
//   from a wall or ledge beside it halfway up), as ladder steps too.
//   Walking: the game still has the ladder state of its old ladder (animation included), but nothing reached it. A
//   bricktron that comes to a run of ladder nodes now climbs to the last one in one go, at ClimbSpeed, then walks on.
[Feature(Features.Ladders, Features.LaddersInfo)]
internal static class Ladders
{
	internal const string Name = "Wood_Ladder";

	// Kept in saves; never change.
	internal const int Id = 9009;

	internal static ConfigEntry<float> Speed;

	// How far from the end of a climb the bricktron lets go of the ladder (the getting-off animation starts).
	internal const float LetGoHeight = 0.5f;

	internal static ConfigEntry<int> Weight;

	// Ladder blocks in the world; only a gate, the block engine has the last word.
	private static readonly HashSet<XYZ> Positions = new HashSet<XYZ>();

	private static void Enable()
	{
		Speed = Plugin.Cfg.Bind("Ladders", "ClimbSpeed", 3f, "How fast bricktrons climb a ladder, in blocks per second (walking is about 3).");
		Weight = Plugin.Cfg.Bind("Ladders", "RouteCost", 60, "Route search cost of one block of ladder (a step of stairs is about 10, climbing a ledge 70 with the default [Pathfinding] ClimbCost): the higher, the more bricktrons prefer stairs.");
		// The old type sorted below climbing, so every unit refused it; between climbing (8) and stepping down (16) it
		// is taken by units that may climb (and the reachability maps), as climbing a ledge is.
		PathBranch.BranchType.Ladder = new PathBranch.BranchType("Ladder", 9, 9);
		GameSession.OnLeave(Positions.Clear);
	}

	internal static void Added(XYZ position)
	{
		Positions.Add(position);
	}

	internal static void Removed(XYZ position)
	{
		Positions.Remove(position);
	}

	internal static IEnumerable<XYZ> All => Positions;

	internal static bool IsLadder(XYZ position)
	{
		if (Positions.Count == 0 || !Positions.Contains(position))
		{
			return false;
		}
		BlockEngine engine = BrixSingleton<BlockEngine>.Instance;
		return engine && engine.GetBlockID(position) == Id;
	}

	// The voxel above the top ladder block, where a bricktron gets on and off at the top.
	internal static bool IsExit(XYZ position)
	{
		return Positions.Count > 0 && IsLadder(position - XYZ.up) && !Voxel.IsNonEmpty(position);
	}

	// A ladder block with ladder below: halfway up, only up and down.
	// The four places beside a place, level with it.
	internal static readonly XYZ[] Sides = new XYZ[4] { XYZ.right, -XYZ.right, XYZ.forward, -XYZ.forward };

	internal static bool IsMidLadder(XYZ position)
	{
		return IsLadder(position) && IsLadder(position - XYZ.up);
	}

	// Where a bricktron can be on a ladder: in a ladder block with ladder or nothing above, or on the exit with room.
	internal static bool IsStandable(XYZ position)
	{
		if (IsLadder(position))
		{
			XYZ above = position + XYZ.up;
			return !Voxel.IsNonEmpty(above) || IsLadder(above);
		}
		return IsExit(position) && !Voxel.IsNonEmpty(position + XYZ.up);
	}

	// Where the wall is from a ladder block (or the exit above it): the block's front, where its ladder is drawn.
	internal static Vector3 WallDirection(XYZ position)
	{
		XYZ block = IsLadder(position) ? position : (IsLadder(position - XYZ.up) ? position - XYZ.up : position);
		if (!IsLadder(block))
		{
			return Vector3.zero;
		}
		IBlockData data = BrixSingleton<BlockEngine>.Instance.GetBlockData(block);
		if (data == null)
		{
			return Vector3.zero;
		}
		Vector3 front = data.Rotation * Vector3.forward;
		front.y = 0f;
		return (front.sqrMagnitude < 0.01f) ? Vector3.zero : front.normalized;
	}

	// One block of ladder between a and the voxel above it.
	internal static bool Connects(XYZ lower)
	{
		XYZ upper = lower + XYZ.up;
		return IsLadder(lower) && (IsLadder(upper) || IsExit(upper));
	}
}

// Keeps the ladder positions.
[Feature(Features.Ladders, Features.LaddersInfo)]
[HarmonyPatch(typeof(BlockEngine), nameof(BlockEngine.OnBlockDataCreated))]
internal static class LaddersBlockAddedPatch
{
	private static void Postfix(IBlockData bd)
	{
		if (bd != null && bd.BlockInfo != null && bd.BlockInfo.ID == Ladders.Id)
		{
			Ladders.Added(bd.Position);
		}
	}
}

[Feature(Features.Ladders, Features.LaddersInfo)]
[HarmonyPatch(typeof(BlockEngine), "OnBlockDataReleased")]
internal static class LaddersBlockRemovedPatch
{
	private static void Prefix(IBlockData ibd)
	{
		if (ibd != null && ibd.BlockInfo != null && ibd.BlockInfo.ID == Ladders.Id)
		{
			Ladders.Removed(ibd.Position);
		}
	}
}

// The ladder block's floor is at its bottom (the game's default is a full block).
[Feature(Features.Ladders, Features.LaddersInfo)]
[HarmonyPatch(typeof(BlockInfo), nameof(BlockInfo.CalculateNiveaux))]
internal static class LaddersNiveauxPatch
{
	private static void Postfix(BlockInfo __instance, ref int __result)
	{
		if (__instance.ID == Ladders.Id)
		{
			__result = 0;
		}
	}
}

// Node test (also used when blocks change and while walking): ladder blocks and the exit.
[Feature(Features.Ladders, Features.LaddersInfo)]
[HarmonyPatch(typeof(Voxel), nameof(Voxel.IsOpenAndWalkable))]
internal static class LaddersWalkablePatch
{
	private static void Postfix(XYZ position, ref bool __result)
	{
		if (!__result && Ladders.IsStandable(position))
		{
			__result = true;
		}
	}
}

// A bricktron inside the ladder column is not inside the ground (the game pushed it out to the floor).
[Feature(Features.Ladders, Features.LaddersInfo)]
[HarmonyPatch(typeof(Voxel), nameof(Voxel.IsOpen))]
internal static class LaddersOpenPatch
{
	private static void Postfix(XYZ position, ref bool __result)
	{
		if (!__result && Ladders.IsStandable(position))
		{
			__result = true;
		}
	}
}

// The node build when a map loads makes no node in a ladder block under another one, nor on the exit: they are
// added before the graph is linked.
[Feature(Features.Ladders, Features.LaddersInfo)]
[HarmonyPatch(typeof(TestPathfinding), "InitPathfinding")]
internal static class LaddersLoadPatch
{
	private static void Prefix(TestPathfinding __instance)
	{
		List<XYZ> candidates = new List<XYZ>();
		foreach (XYZ ladder in Ladders.All)
		{
			candidates.Add(ladder);
			candidates.Add(ladder + XYZ.up);
		}
		int added = 0;
		foreach (XYZ position in candidates)
		{
			if (__instance.TerrainNodes.ContainsKey(position) || !Ladders.IsStandable(position))
			{
				continue;
			}
			__instance.TerrainNodes[position] = new TerrainPathNode(position, Voxel.GetNiveaux(position));
			__instance.IncreaseAccessCount(position);
			added++;
		}
		if (added > 0)
		{
			Plugin.Log.LogInfo("Ladders: added " + added + " ladder nodes");
		}
	}
}

// Branches: halfway up a ladder only up and down; ladder blocks and the exit linked one block at a time, both ways
// with the same type and weight (the coarser levels of the graph only group nodes linked alike both ways).
[Feature(Features.Ladders, Features.LaddersInfo)]
[HarmonyPatch(typeof(TerrainPathNode), nameof(TerrainPathNode.Init))]
internal static class LaddersBranchPatch
{
	private static void Postfix(TerrainPathNode __instance, TestTerrainLod pathLod)
	{
		XYZ position = __instance.position;
		bool mid = Ladders.IsMidLadder(position);
		TerrainPathNode below = Ladders.Connects(position - XYZ.up) ? pathLod.GetPathNode(position - XYZ.up) as TerrainPathNode : null;
		TerrainPathNode above = Ladders.Connects(position) ? pathLod.GetPathNode(position + XYZ.up) as TerrainPathNode : null;
		bool midTarget = false;
		if (!mid)
		{
			foreach (PathBranch branch in __instance.siblings)
			{
				if (branch.Node is TerrainPathNode target && Ladders.IsMidLadder(target.position))
				{
					midTarget = true;
					break;
				}
			}
		}
		// Stepping onto (or off) a ladder from the side halfway up: a place level with a ladder block above the bottom
		// one, beside it, links to it as a ladder step (both ways, decided by where they are, so the link is the same
		// from either end).
		List<TerrainPathNode> sides = new List<TerrainPathNode>();
		foreach (XYZ side in Ladders.Sides)
		{
			XYZ at = position + side;
			if (mid || Ladders.IsMidLadder(at))
			{
				if (pathLod.GetPathNode(at) is TerrainPathNode sideNode && !sideNode.IsEmpty)
				{
					sides.Add(sideNode);
				}
			}
		}
		if (!mid && !midTarget && below == null && above == null && sides.Count == 0)
		{
			return;
		}
		List<PathBranch> branches = new List<PathBranch>();
		if (!mid)
		{
			foreach (PathBranch branch in __instance.siblings)
			{
				if (!(branch.Node is TerrainPathNode target && Ladders.IsMidLadder(target.position)))
				{
					branches.Add(branch);
				}
			}
		}
		int weight = Ladders.Weight.Value;
		if (below != null && !below.IsEmpty)
		{
			branches.Add(new PathBranch(below, PathBranch.BranchType.Ladder, weight));
		}
		if (above != null && !above.IsEmpty)
		{
			branches.Add(new PathBranch(above, PathBranch.BranchType.Ladder, weight));
		}
		foreach (TerrainPathNode side in sides)
		{
			branches.RemoveAll((PathBranch b) => b.Node == side);
			branches.Add(new PathBranch(side, PathBranch.BranchType.Ladder, weight));
		}
		__instance.siblings = branches.ToArray();
	}
}

// The step into a ladder node is a ladder step (the game's test always said no).
[Feature(Features.Ladders, Features.LaddersInfo)]
[HarmonyPatch(typeof(WalkNode), nameof(WalkNode.IsLadder))]
internal static class LaddersWalkNodePatch
{
	private static void Postfix(WalkNode __instance, ref bool __result)
	{
		__result = __instance._branchType == PathBranch.BranchType.Ladder;
	}
}

// Climbing. A bricktron standing still on its path (the in place state) that comes to ladder nodes jumps its path to
// the last of them and climbs there; the game's ladder state (with its animation) takes over when the animator gets
// there, else the climb is driven from here. At the end it walks on.
[Feature(Features.Ladders, Features.LaddersInfo)]
[HarmonyPatch(typeof(InPlace), nameof(InPlace.Update))]
internal static class LaddersClimbPatch
{
	private sealed class Climb
	{
		public Vector3 Destination;

		public Quaternion Facing;

		public Vector3 LastAt;

		public float LastMoved;

		public bool Up;
	}

	private const float StuckSeconds = 2f;

	private static readonly Dictionary<Locomotion4, Climb> Climbing = new Dictionary<Locomotion4, Climb>();

	private static void Enable()
	{
		GameSession.OnLeave(Climbing.Clear);
	}

	private static bool Prefix(InPlace __instance)
	{
		Locomotion4 locomotion = __instance.Locomotion;
		DestinationIterator path = locomotion.PathIterator;
		if (!path.HasCurrent)
		{
			End(locomotion, __instance, advance: false);
			return true;
		}
		if (__instance.animator.IsInTransition(0))
		{
			return true;
		}
		Climbing.TryGetValue(locomotion, out Climb climb);
		if (climb == null)
		{
			climb = Start(__instance, locomotion, path);
			if (climb == null)
			{
				return true;
			}
		}
		Transform trf = locomotion.trf;
		Vector3 to = climb.Destination - trf.position;
		// Done at the end, or past it: the walkway at the top of a ladder is half a block above the ladder's top node,
		// so a bricktron going up stands above the end and never came within reach of it (it hung there for
		// StuckSeconds before it gave up).
		bool past = climb.Up ? to.y <= 0.05f : to.y >= -0.05f;
		if ((Mathf.Abs(to.y) < 0.05f || past) && new Vector2(to.x, to.z).magnitude < 0.1f)
		{
			End(locomotion, __instance, advance: true);
			return true;
		}
		// No headway for StuckSeconds (the end cannot be reached): the climb is given up and the bricktron walks on.
		if ((climb.LastAt - trf.position).sqrMagnitude > 0.0004f)
		{
			climb.LastAt = trf.position;
			climb.LastMoved = Time.time;
		}
		else if (Time.time - climb.LastMoved > StuckSeconds)
		{
			Plugin.Log.LogInfo("Ladders: " + locomotion.name + " made no headway climbing to " + climb.Destination + " from " + trf.position + ", gives up");
			End(locomotion, __instance, advance: false);
			locomotion.ClearPath();
			return true;
		}
		locomotion.AutoSnapToFloor = false;
		locomotion.IsUsingLadder = true;
		// The last half block: let go of the ladder, so the animator starts getting off while it climbs the rest.
		if (Mathf.Abs(to.y) < Ladders.LetGoHeight)
		{
			locomotion.controller.IsGrabbingLadderFromBottom.Set(b: false);
			locomotion.controller.IsGrabbingLadderFromTop.Set(b: false);
		}
		__instance.OverrideMatchTarget(to.magnitude / Mathf.Max(0.1f, Ladders.Speed.Value), climb.Destination, climb.Facing);
		return false;
	}

	// The ladder run that starts at the path's next node (after nodes the bricktron already stands on, as the game
	// skips them): the path moves to its last node, the climb's destination.
	private static Climb Start(InPlace state, Locomotion4 locomotion, DestinationIterator path)
	{
		int first = -1;
		for (int i = 0; i < 3; i++)
		{
			WalkNode node = path.GetNext(i);
			if (node == null)
			{
				return null;
			}
			if (node.IsLadder())
			{
				first = i;
				break;
			}
			if (!state.IsOnSpot(node))
			{
				return null;
			}
		}
		if (first < 0)
		{
			return null;
		}
		if (!locomotion.CanClimb)
		{
			Plugin.Log.LogInfo("Ladders: " + locomotion.name + " cannot climb with what it carries");
			locomotion.PathBlockedAt(path.GetNext(first).Position);
			return null;
		}
		int last = first;
		while (path.GetNext(last + 1) != null && path.GetNext(last + 1).IsLadder())
		{
			last++;
		}
		WalkNode before = path.GetNext(first - 1) ?? path.GetNth(path.CurrentIndex - 1);
		WalkNode end = path.GetNext(last);
		WalkNode after = path.GetNext(last + 1);
		bool up = end.Position.y > locomotion.trf.position.y;
		// Facing the ladder (and the wall behind it): the ladder block's front. Without it, going up the way off the
		// top, going down the way the bricktron came onto the top.
		Vector3 wall = Vector3.zero;
		for (int i = first; i <= last && wall == Vector3.zero; i++)
		{
			wall = Ladders.WallDirection(path.GetNext(i).Voxel);
		}
		if (wall == Vector3.zero)
		{
			wall = up ? ((after != null) ? after.Position - end.Position : locomotion.trf.forward)
				: ((before != null) ? (before.Position - (path.GetNth(path.CurrentIndex + first - 2) ?? before).Position) * -1f : -locomotion.trf.forward);
			wall.y = 0f;
		}
		if (wall.sqrMagnitude < 0.01f)
		{
			wall = locomotion.trf.forward;
			wall.y = 0f;
		}
		path.MoveNext(last);
		Climb climb = new Climb { Destination = end.Position, Facing = Quaternion.LookRotation(wall.normalized, Vector3.up), LastAt = locomotion.trf.position, LastMoved = Time.time, Up = up };
		Climbing[locomotion] = climb;
		// Turned to the ladder at once (the game's ladder state keeps the bricktron's own rotation).
		locomotion.trf.rotation = climb.Facing;
		// The game's ladder state, if the animator has it.
		locomotion.Movement.Ladder.UseLadder(climb.Destination);
		return climb;
	}

	internal static void End(Locomotion4 locomotion, InPlace state, bool advance)
	{
		if (!Climbing.Remove(locomotion))
		{
			return;
		}
		locomotion.AutoSnapToFloor = true;
		locomotion.IsUsingLadder = false;
		locomotion.controller.IsGrabbingLadderFromBottom.Set(b: false);
		locomotion.controller.IsGrabbingLadderFromTop.Set(b: false);
		if (advance && locomotion.PathIterator.HasCurrent)
		{
			locomotion.AdvancePath();
		}
	}

	internal static bool IsClimbing(Locomotion4 locomotion, out Vector3 destination)
	{
		destination = Vector3.zero;
		if (!Climbing.TryGetValue(locomotion, out Climb climb))
		{
			return false;
		}
		destination = climb.Destination;
		return true;
	}

	// A new path (or none) while climbing: the climb is over.
	internal static void Forget(Locomotion4 locomotion)
	{
		if (Climbing.ContainsKey(locomotion))
		{
			Climbing.Remove(locomotion);
			locomotion.AutoSnapToFloor = true;
			locomotion.IsUsingLadder = false;
			// Let go of the ladder too, or the animator stays in the climb (a bricktron that looks like it climbs
			// on the spot).
			locomotion.controller.IsGrabbingLadderFromBottom.Set(b: false);
			locomotion.controller.IsGrabbingLadderFromTop.Set(b: false);
		}
	}
}

// The game's ladder state: our speed, and it lets go of both ladder flags half a block before the end (it cleared the
// wrong one going up, so the animator stayed on the ladder). A bricktron that has not moved for StuckSeconds in the ladder state
// (its climb was cut off, or its end cannot be reached) lets go as well and walks on, instead of climbing on the spot
// for good.
[Feature(Features.Ladders, Features.LaddersInfo)]
[HarmonyPatch(typeof(Ladder), nameof(Ladder.Update))]
internal static class LaddersStatePatch
{
	private const float StuckSeconds = 2f;

	private static readonly Dictionary<Ladder, KeyValuePair<Vector3, float>> LastMove = new Dictionary<Ladder, KeyValuePair<Vector3, float>>();

	private static void Enable()
	{
		GameSession.OnLeave(LastMove.Clear);
	}

	private static void Prefix(Ladder __instance)
	{
		__instance.LadderSpeed = Ladders.Speed.Value;
		Vector3 at = __instance.trf.position;
		float left = Mathf.Abs(__instance.LadderDestination.y - at.y);
		// Done: at the end, or past it (the walkway the ladder leads onto is half a block above its top node, so a
		// bricktron going up stands above the destination and never came within reach of it).
		bool past = __instance.LadderGoingUp ? at.y >= __instance.LadderDestination.y - 0.05f : at.y <= __instance.LadderDestination.y + 0.05f;
		if (left < 0.05f || past)
		{
			if (past)
			{
				// Not pulled back down (or up) to the node it passed.
				__instance.LadderDestination = at;
			}
			LetGo(__instance);
			return;
		}
		// The last half block: both flags go (the animator starts getting off), the climb itself goes on.
		if (left < Ladders.LetGoHeight)
		{
			__instance.controller.IsGrabbingLadderFromBottom.Set(b: false);
			__instance.controller.IsGrabbingLadderFromTop.Set(b: false);
		}
		if (!LastMove.TryGetValue(__instance, out KeyValuePair<Vector3, float> last) || (last.Key - at).sqrMagnitude > 0.0004f)
		{
			if (LastMove.Count > 500)
			{
				LastMove.Clear();
			}
			LastMove[__instance] = new KeyValuePair<Vector3, float>(at, Time.time);
			return;
		}
		if (Time.time - last.Value < StuckSeconds)
		{
			return;
		}
		Plugin.Log.LogInfo("Ladders: " + __instance.trf.name + " stuck on a ladder at " + at + " (to " + __instance.LadderDestination + "), lets go");
		__instance.LadderDestination = at;
		LetGo(__instance);
		LaddersClimbPatch.Forget(__instance.Locomotion);
		__instance.Locomotion.IsUsingLadder = false;
		__instance.Locomotion.AutoSnapToFloor = true;
	}

	private static void LetGo(Ladder state)
	{
		state.controller.IsGrabbingLadderFromBottom.Set(b: false);
		state.controller.IsGrabbingLadderFromTop.Set(b: false);
		LastMove.Remove(state);
	}
}

[Feature(Features.Ladders, Features.LaddersInfo)]
[HarmonyPatch(typeof(Ladder), nameof(Ladder.Enter))]
internal static class LaddersStateEnterPatch
{
	private static void Prefix(Ladder __instance)
	{
		__instance.LadderSpeed = Ladders.Speed.Value;
	}
}

[Feature(Features.Ladders, Features.LaddersInfo)]
[HarmonyPatch(typeof(Locomotion4), nameof(Locomotion4.ClearPath))]
internal static class LaddersClearPathPatch
{
	private static void Postfix(Locomotion4 __instance)
	{
		LaddersClimbPatch.Forget(__instance);
	}
}
