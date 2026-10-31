using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using BepInEx.Configuration;
using Brix.Engine;
using Brix.Game.AI;
using Brix.Game.Components.Volumes;
using Brix.Game.Locomotion;
using Brix.Legacy;
using Brix.Pathfinding;
using Brix.Pathfinding.Test;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Pathfinding;

// Bricktrons walked open ground voxel by voxel: the route graph links a voxel only to its 4 sides, so the search
// returns a staircase, and often an L (all the steps of one direction first) where a straight line was free. They now
// walk straight lines across flat open ground.
//   Route: after the game has checked the route (PathUnroll.RemoveInvalidNodes), runs of plain flat steps are pulled
//   tight: from a kept voxel, the furthest later voxel of the route that a straight line reaches is found, and the
//   voxels in between are replaced by the voxels that line passes through (a 4-connected supercover line). The line
//   is taken only if every voxel it passes is a route node at the same height and surface, each step between them is
//   a plain walk branch, none holds a door or other obstacle or a unit the route search would avoid (the route's own
//   presence test, which knows allies from enemies), and the voxels the bricktron's body brushes beside the line
//   (both neighbours of a corner the line passes) are flat ground at that height too. Climbing, stepping down,
//   ladders, forests, doors and anything that is not a plain step stay as the search found them, as does the end.
//   Walking: the route still holds every voxel the bricktron walks through, so everything the game checks voxel by
//   voxel while walking (who is in the next voxel, doors, whether the step is still walkable, the floor height, the
//   repath when the route is blocked or the ground changed) works as before. The bricktron only aims differently on
//   a pulled run: at a point a little ahead on the line, kept inside the next voxel of the route (that voxel and the
//   one it stands in form a 1 x 2 box, so it never steps into a voxel off the route), facing along the line.
[Feature(Features.Pathfinding, Features.PathfindingInfo)]
internal static class StraightPaths
{
	// The longest run pulled tight in one piece, in voxels of the original route (bounds the work per route).
	private const int MaxRun = 24;

	// Half the width of a bricktron's body: the voxels within it beside the line must be flat ground too.
	private const float BodyRadius = 0.3f;

	// How far ahead on the line the bricktron aims.
	private const float Lookahead = 1f;

	// The aim stays this far inside the next voxel, so the bricktron surely crosses into it.
	private const float Margin = 0.1f;

	// Steering data per walk node; dropped wholesale if it ever grows this big (nodes the pool let go of).
	private const int MaxNodes = 50000;

	internal static ConfigEntry<bool> Enabled;

	// The line a pulled run follows, from its start to its end voxel (flat).
	internal struct Line
	{
		public Vector3 Start;

		public Vector3 Direction;

		public float Length;
	}

	private sealed class ByReference : IEqualityComparer<WalkNode>
	{
		public bool Equals(WalkNode a, WalkNode b)
		{
			return ReferenceEquals(a, b);
		}

		public int GetHashCode(WalkNode node)
		{
			return RuntimeHelpers.GetHashCode(node);
		}
	}

	private static readonly Dictionary<WalkNode, Line> Lines = new Dictionary<WalkNode, Line>(new ByReference());

	// The pulled route and the line of each of its branches (null: not on a pulled run), for the walk nodes made from it.
	internal static readonly List<PathBranch> Pulled = new List<PathBranch>();

	private static readonly List<Line?> PulledLines = new List<Line?>();

	private static readonly List<XYZ> Cells = new List<XYZ>();

	// Measurements for DevTools (/api/path/stats).
	internal static int Routes;

	internal static int RoutesPulled;

	internal static int RunsPulled;

	internal static int VoxelsReplaced;

	internal static int Steered;

	internal static int Blocked;

	internal static int BlockedOnLine;

	internal static double Milliseconds;

	internal static double SteerMilliseconds;

	// DevTools: what happens to one unit's routes and walk, for a test to report.
	internal static Transform Watched;

	internal static readonly List<string> WatchLog = new List<string>();

	private static void Enable()
	{
		Enabled = Plugin.Cfg.Bind("Pathfinding", "StraightPaths", true, "Bricktrons walk straight lines across flat open ground instead of the route graph's voxel-by-voxel staircase.");
		GameSession.OnLeave(Lines.Clear);
	}

	private static void Disable()
	{
		Lines.Clear();
	}

	internal static void ResetStats()
	{
		Routes = 0;
		RoutesPulled = 0;
		RunsPulled = 0;
		VoxelsReplaced = 0;
		Steered = 0;
		Blocked = 0;
		BlockedOnLine = 0;
		Milliseconds = 0;
		SteerMilliseconds = 0;
	}

	internal static int TrackedNodes => Lines.Count;

	internal static double Since(long started)
	{
		return (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
	}

	internal static bool IsOnLine(WalkNode node)
	{
		return Lines.Count > 0 && Lines.ContainsKey(node);
	}

	// Pulls the route tight into Pulled/PulledLines; false when nothing changed.
	internal static bool Pull(List<PathBranch> branches, TestTerrainLod lod, Func<XYZ, bool> presenceTest, GameObject walker)
	{
		_walker = walker;
		Pulled.Clear();
		PulledLines.Clear();
		int count = branches.Count;
		if (count < 3 || lod == null)
		{
			return false;
		}
		bool changed = false;
		Pulled.Add(branches[0]);
		PulledLines.Add(null);
		int i = 0;
		while (i < count - 1)
		{
			int best = i + 1;
			TerrainPathNode start = branches[i].Node as TerrainPathNode;
			if (start != null && !start.IsEmpty)
			{
				XYZ from = start.RepresentativePosition;
				float surface = start.GetSurfaceHeightDelta(0f, 0f);
				for (int j = i + 1; j < count && j - i <= MaxRun; j++)
				{
					if (!IsPlainStep(branches[j], from.y, surface))
					{
						break;
					}
					if (j - i < 2)
					{
						continue;
					}
					if (!IsLineClear(lod, from, branches[j].Node.RepresentativePosition, surface, presenceTest))
					{
						break;
					}
					best = j;
				}
			}
			if (best >= i + 2)
			{
				EmitLine(lod, branches, i, best);
				changed = true;
			}
			else
			{
				Pulled.Add(branches[i + 1]);
				PulledLines.Add(null);
				best = i + 1;
			}
			i = best;
		}
		return changed;
	}

	// The cells of the line (already checked by IsLineClear) replace the route's branches i+1..j.
	private static void EmitLine(TestTerrainLod lod, List<PathBranch> branches, int i, int j)
	{
		XYZ from = branches[i].Node.RepresentativePosition;
		XYZ to = branches[j].Node.RepresentativePosition;
		Supercover(from, to, Cells);
		Vector3 a = from.ToVector3();
		Vector3 d = Flat(to.ToVector3() - a);
		Line line = new Line { Start = a, Direction = d.normalized, Length = d.magnitude };
		bool same = Cells.Count == j - i;
		for (int k = 0; k < Cells.Count; k++)
		{
			if (k == Cells.Count - 1)
			{
				Pulled.Add(branches[j]);
			}
			else
			{
				Pulled.Add(new PathBranch(lod.GetPathNode(Cells[k]), PathBranch.BranchType.Full, branches[i + 1].weight));
				if (same && Cells[k] != branches[i + 1 + k].Node.RepresentativePosition)
				{
					same = false;
				}
			}
			PulledLines.Add(line);
		}
		RunsPulled++;
		if (!same)
		{
			VoxelsReplaced += j - i - 1;
		}
	}

	// A step the line may replace: a plain flat walk onto a route node at the run's height and surface.
	private static bool IsPlainStep(PathBranch branch, int y, float surface)
	{
		if (branch.Type != PathBranch.BranchType.Full)
		{
			return false;
		}
		TerrainPathNode node = branch.Node as TerrainPathNode;
		if (node == null || node.IsEmpty || node.RepresentativePosition.y != y)
		{
			return false;
		}
		return Mathf.Abs(node.GetSurfaceHeightDelta(0f, 0f) - surface) < 0.01f;
	}

	private static bool IsLineClear(TestTerrainLod lod, XYZ from, XYZ to, float surface, Func<XYZ, bool> presenceTest)
	{
		// The voxels the bricktron walks through: each a flat route node, linked to the one before by a plain step,
		// free of obstacles and of units the route search would avoid.
		Supercover(from, to, Cells);
		TerrainPathNode previous = lod.GetPathNode(from) as TerrainPathNode;
		for (int k = 0; k < Cells.Count; k++)
		{
			XYZ cell = Cells[k];
			TerrainPathNode node = lod.GetPathNode(cell) as TerrainPathNode;
			if (!IsFlatGround(node, from.y, surface))
			{
				return Fail(cell, "not flat ground at the line's height and surface");
			}
			if (!HasPlainStep(previous, node))
			{
				return Fail(cell, "no plain step to it from the voxel before");
			}
			if (k < Cells.Count - 1 && HasObstacle(cell))
			{
				return Fail(cell, "a door, or an obstacle the route search would avoid, in it");
			}
			if (k < Cells.Count - 1 && presenceTest != null && !presenceTest(cell))
			{
				return Fail(cell, "the route's presence test refuses it (a unit or blocker)");
			}
			previous = node;
		}
		// The voxels beside the line the body brushes (the corners it passes): flat ground at the same height, no
		// obstacle, so the line neither cuts a wall's corner nor runs along an edge or a drop.
		Vector3 a = from.ToVector3();
		Vector3 b = to.ToVector3();
		Vector3 d = Flat(b - a).normalized;
		Vector3 side = new Vector3(-d.z, 0f, d.x) * BodyRadius;
		return IsSideClear(lod, a + side, b + side, from.y, surface) && IsSideClear(lod, a - side, b - side, from.y, surface);
	}

	private static bool IsSideClear(TestTerrainLod lod, Vector3 a, Vector3 b, int y, float surface)
	{
		int x = Mathf.RoundToInt(a.x);
		int z = Mathf.RoundToInt(a.z);
		int endX = Mathf.RoundToInt(b.x);
		int endZ = Mathf.RoundToInt(b.z);
		float dx = b.x - a.x;
		float dz = b.z - a.z;
		int stepX = (dx > 0f) ? 1 : -1;
		int stepZ = (dz > 0f) ? 1 : -1;
		// Distance along the segment (0..1) to the next voxel border in x and z, and between borders.
		float deltaX = (Mathf.Abs(dx) > 1e-6f) ? Mathf.Abs(1f / dx) : float.MaxValue;
		float deltaZ = (Mathf.Abs(dz) > 1e-6f) ? Mathf.Abs(1f / dz) : float.MaxValue;
		float nextX = (Mathf.Abs(dx) > 1e-6f) ? (x + 0.5f * stepX - a.x) / dx : float.MaxValue;
		float nextZ = (Mathf.Abs(dz) > 1e-6f) ? (z + 0.5f * stepZ - a.z) / dz : float.MaxValue;
		for (int guard = 0; guard < 4 * MaxRun + 8; guard++)
		{
			XYZ cell = new XYZ(x, y, z);
			if (!IsFlatGround(lod.GetPathNode(cell) as TerrainPathNode, y, surface) || HasObstacle(cell))
			{
				return Fail(cell, "beside the line: not flat open ground");
			}
			if (x == endX && z == endZ)
			{
				return true;
			}
			if (nextX > 1f && nextZ > 1f)
			{
				return true;
			}
			// Through a corner (or within a hair of one) both voxels beside it count.
			if (Mathf.Abs(nextX - nextZ) < 1e-4f)
			{
				XYZ corner = new XYZ(x + stepX, y, z);
				if (!IsFlatGround(lod.GetPathNode(corner) as TerrainPathNode, y, surface) || HasObstacle(corner))
				{
					return Fail(corner, "beside a corner of the line: not flat open ground");
				}
				z += stepZ;
				nextZ += deltaZ;
			}
			else if (nextX < nextZ)
			{
				x += stepX;
				nextX += deltaX;
			}
			else
			{
				z += stepZ;
				nextZ += deltaZ;
			}
		}
		return false;
	}

	// The bricktron whose route is pulled: obstacles the route search lets it through do not stop a line either.
	private static GameObject _walker;

	private static readonly Func<IVolume, bool> BlocksLine = (IVolume volume) =>
	{
		IPassThrough passThrough = volume.PassThrough;
		return passThrough == null || passThrough is Porte || _walker == null || !passThrough.CanSearchThrough(_walker);
	};

	// A door (kept as the route found it) or an obstacle the route search would not take the bricktron through.
	private static bool HasObstacle(XYZ cell)
	{
		return VolumeRegistry.HasVolumesWithPropertyAt(cell, VProperty.obstacle, BlocksLine);
	}

	// Why the last line test failed, kept only while Explain runs (DevTools).
	private static string _why;

	private static bool _explaining;

	private static bool Fail(XYZ cell, string reason)
	{
		if (_explaining)
		{
			_why = cell + ": " + reason;
		}
		return false;
	}

	// Whether a straight walk from one voxel to another passes the line test, and if not, where and why.
	internal static string Explain(XYZ from, XYZ to, Func<XYZ, bool> presenceTest, GameObject walker)
	{
		_walker = walker;
		TestTerrainLod lod = (TestPathfinding.Instance != null) ? TestPathfinding.Instance.terrainLod : null;
		if (lod == null)
		{
			return "no navigation graph";
		}
		TerrainPathNode start = lod.GetPathNode(from) as TerrainPathNode;
		if (start == null || start.IsEmpty)
		{
			return from + ": no route node";
		}
		_explaining = true;
		_why = null;
		try
		{
			return IsLineClear(lod, from, to, start.GetSurfaceHeightDelta(0f, 0f), presenceTest) ? "clear" : (_why ?? "not clear");
		}
		finally
		{
			_explaining = false;
		}
	}

	private static Vector3 Flat(Vector3 v)
	{
		return new Vector3(v.x, 0f, v.z);
	}

	private static bool IsFlatGround(TerrainPathNode node, int y, float surface)
	{
		return node != null && !node.IsEmpty && node.RepresentativePosition.y == y && Mathf.Abs(node.GetSurfaceHeightDelta(0f, 0f) - surface) < 0.01f;
	}

	private static bool HasPlainStep(TerrainPathNode from, TerrainPathNode to)
	{
		if (from == null || to == null)
		{
			return false;
		}
		PathBranch[] siblings = from.siblings;
		if (siblings == null)
		{
			return false;
		}
		for (int k = 0; k < siblings.Length; k++)
		{
			if (siblings[k].node == to)
			{
				return siblings[k].Type == PathBranch.BranchType.Full;
			}
		}
		return false;
	}

	// The voxels a line between two voxel centres passes through, in order, each a side neighbour of the one before
	// (the route graph has no diagonal links). Through an exact corner it steps in x first. The start is left out,
	// the end is included.
	internal static void Supercover(XYZ from, XYZ to, List<XYZ> cells)
	{
		cells.Clear();
		int dx = to.x - from.x;
		int dz = to.z - from.z;
		int nx = Math.Abs(dx);
		int nz = Math.Abs(dz);
		int sx = Math.Sign(dx);
		int sz = Math.Sign(dz);
		int x = from.x;
		int z = from.z;
		int ix = 0;
		int iz = 0;
		while (ix < nx || iz < nz)
		{
			// The next border crossed: in x at (ix + 0.5) / nx of the way, in z at (iz + 0.5) / nz.
			long compare = (long)(1 + 2 * ix) * nz - (long)(1 + 2 * iz) * nx;
			if (iz >= nz || (ix < nx && compare <= 0))
			{
				x += sx;
				ix++;
			}
			else
			{
				z += sz;
				iz++;
			}
			cells.Add(new XYZ(x, from.y, z));
		}
	}

	// Called with the walk nodes made from Pulled (one each, in order, from index first).
	internal static void Attach(List<WalkNode> nodes, int first)
	{
		if (Lines.Count > MaxNodes)
		{
			Lines.Clear();
		}
		for (int k = first; k < nodes.Count && k - first < PulledLines.Count; k++)
		{
			Line? line = PulledLines[k - first];
			if (line.HasValue)
			{
				Lines[nodes[k]] = line.Value;
			}
		}
	}

	internal static void Forget(WalkNode node)
	{
		if (Lines.Count > 0)
		{
			Lines.Remove(node);
		}
	}

	internal static void Copy(WalkNode node, WalkNode other)
	{
		if (Lines.Count > 0 && other != null && Lines.TryGetValue(other, out Line line))
		{
			Lines[node] = line;
		}
	}

	// The aim of a bricktron walking a pulled run, or false to leave it to the game.
	internal static bool Steer(LocomotionWalkableCycleState state)
	{
		if (Lines.Count == 0 || !Enabled.Value)
		{
			return false;
		}
		Locomotion4 locomotion = state.Locomotion;
		WalkNode current = locomotion.PathIterator.CurrentActive;
		if (current == null || !Lines.TryGetValue(current, out Line line))
		{
			return false;
		}
		Transform trf = locomotion.trf;
		Vector3 position = trf.position;
		XYZ standing = XYZ.FromVector3(position);
		XYZ next = current.Voxel;
		// Only from the voxel next to it, where the game's own walk would be too.
		if (Math.Abs(standing.x - next.x) + Math.Abs(standing.z - next.z) != 1)
		{
			return false;
		}
		Vector3 offset = Flat(position - line.Start);
		float along = Vector3.Dot(offset, line.Direction);
		Vector3 aim = line.Start + line.Direction * Mathf.Clamp(along + Lookahead, 0f, line.Length);
		aim.x = Mathf.Clamp(aim.x, next.x - 0.5f + Margin, next.x + 0.5f - Margin);
		aim.z = Mathf.Clamp(aim.z, next.z - 0.5f + Margin, next.z + 0.5f - Margin);
		aim.y = next.y;
		Quaternion facing = Quaternion.LookRotation(line.Direction);
		float time = (aim - position).magnitude / state.SpeedMperS;
		float rotationTime = Mathf.Min(time, Quaternion.Angle(trf.rotation, facing) / (state.TurningTopSpeed * 360f));
		state.OverrideMatchTarget(time, rotationTime, aim, facing);
		Steered++;
		return true;
	}
}

// Pulls the route tight while the walk nodes are made from it (the route Navigation copies, and PathUnroll's own
// enumerator), then gives the nodes of each pulled run their line.
[Feature(Features.Pathfinding, Features.PathfindingInfo)]
[HarmonyPatch(typeof(PathUnroll), "ConvertVoxelListToPath")]
internal static class StraightPathsConvertPatch
{
	private static void Prefix(PathUnroll __instance, ref List<PathBranch> branches, List<WalkNode> outLocations, out int __state)
	{
		__state = -1;
		if (StraightPaths.Enabled == null || !StraightPaths.Enabled.Value || branches == null)
		{
			return;
		}
		long started = Stopwatch.GetTimestamp();
		StraightPaths.Routes++;
		bool pulled = StraightPaths.Pull(branches, __instance.terrainLod, __instance.presenceTest, (__instance.transform != null) ? __instance.transform.gameObject : null);
		if (StraightPaths.Watched != null && __instance.transform == StraightPaths.Watched)
		{
			List<PathBranch> route = pulled ? StraightPaths.Pulled : branches;
			StringBuilder cells = new StringBuilder();
			for (int i = 0; i < route.Count; i++)
			{
				XYZ p = route[i].Node.RepresentativePosition;
				cells.Append(' ').Append(p.x).Append(',').Append(p.y).Append(',').Append(p.z).Append(route[i].Type == PathBranch.BranchType.Full ? "" : ("(" + route[i].Type + ")"));
			}
			StraightPaths.WatchLog.Add(Time.frameCount + ": route of " + branches.Count + " voxels" + (pulled ? ", pulled:" : ", not pulled:") + cells);
		}
		if (pulled)
		{
			StraightPaths.RoutesPulled++;
			branches = StraightPaths.Pulled;
			__state = outLocations.Count;
		}
		StraightPaths.Milliseconds += StraightPaths.Since(started);
	}

	private static void Postfix(List<WalkNode> outLocations, int __state)
	{
		if (__state >= 0)
		{
			StraightPaths.Attach(outLocations, __state);
		}
	}
}

// A walk node taken from the pool for another route loses its line; one copied (a repath keeps the rest of the old
// route) keeps it.
[Feature(Features.Pathfinding, Features.PathfindingInfo)]
[HarmonyPatch(typeof(WalkNode), nameof(WalkNode.Reset), typeof(PathBranch))]
internal static class StraightPathsResetBranchPatch
{
	private static void Prefix(WalkNode __instance)
	{
		StraightPaths.Forget(__instance);
	}
}

[Feature(Features.Pathfinding, Features.PathfindingInfo)]
[HarmonyPatch(typeof(WalkNode), nameof(WalkNode.Reset), typeof(WalkablePathNode))]
internal static class StraightPathsResetNodePatch
{
	private static void Prefix(WalkNode __instance)
	{
		StraightPaths.Forget(__instance);
	}
}

[Feature(Features.Pathfinding, Features.PathfindingInfo)]
[HarmonyPatch(typeof(WalkNode), nameof(WalkNode.Reset), typeof(WalkNode))]
internal static class StraightPathsResetCopyPatch
{
	private static void Prefix(WalkNode __instance, WalkNode other)
	{
		StraightPaths.Forget(__instance);
		StraightPaths.Copy(__instance, other);
	}
}

// Counts the walks the game finds blocked, and those blocked on a pulled run (DevTools, to compare with the game's).
[Feature(Features.Pathfinding, Features.PathfindingInfo)]
[HarmonyPatch(typeof(Locomotion4), nameof(Locomotion4.PathBlockedAt))]
internal static class StraightPathsBlockedPatch
{
	private static void Prefix(Locomotion4 __instance, Vector3 position)
	{
		StraightPaths.Blocked++;
		if (StraightPaths.Watched != null && __instance.transform == StraightPaths.Watched)
		{
			StraightPaths.WatchLog.Add(Time.frameCount + ": walk blocked at " + position);
		}
		WalkNode current = (__instance.PathIterator != null) ? __instance.PathIterator.CurrentActive : null;
		if (current != null && StraightPaths.IsOnLine(current))
		{
			StraightPaths.BlockedOnLine++;
			if (StraightPaths.BlockedOnLine <= 20)
			{
				Plugin.Log.LogInfo("StraightPaths: " + __instance.name + " blocked at " + position + " on a straight run, standing at " + __instance.trf.position + ", next voxel " + current.Voxel);
			}
		}
	}
}

// DevTools: a watched unit's repaths (the rest of its route searched again round a blocker).
[Feature(Features.Pathfinding, Features.PathfindingInfo)]
[HarmonyPatch(typeof(Locomotion4), nameof(Locomotion4.RepathMoveNext))]
internal static class StraightPathsRepathPatch
{
	private static void Postfix(Locomotion4 __instance, bool __result)
	{
		if (StraightPaths.Watched != null && __instance.transform == StraightPaths.Watched)
		{
			StraightPaths.WatchLog.Add(Time.frameCount + ": repath " + (__result ? "found" : "failed") + " at " + __instance.trf.position);
		}
	}
}

// Walking and running aim along the line on a pulled run.
[Feature(Features.Pathfinding, Features.PathfindingInfo)]
[HarmonyPatch(typeof(LocomotionWalkableCycleState), "MatchCurrentTarget")]
internal static class StraightPathsSteerPatch
{
	private static bool Prefix(LocomotionWalkableCycleState __instance)
	{
		long started = Stopwatch.GetTimestamp();
		bool steered = StraightPaths.Steer(__instance);
		StraightPaths.SteerMilliseconds += StraightPaths.Since(started);
		return !steered;
	}
}
