using System.Collections.Generic;
using System.Reflection;
using Brix.Components;
using Brix.Engine;
using Brix.Game;
using Brix.Game.Components;
using Brix.Game.Components.Tracking;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using TrackingEntry = Brix.Game.Components.Tracking.TrackingRegistry.TrackingEntry;
using TrackingGroup = Brix.Game.Components.Tracking.TrackingRegistry.TrackingGroup;

namespace CastleStoryPlus.Performance;

// Two steps of the faction vision pass (SharedFactionVision, see VisionRate) cost the most, and the same results are
// now worked out with less work.
// - Which tracking groups (16 x 16 x 16 cells holding units and items) a faction sees fully or partly: for every one
//   of its visions (every bricktron and building, hundreds in a big base) the game went over every group of the map
//   (its "dense" lookup is never chosen: it wants radius³ x 20480 groups), up to 9 ms in one frame. A group's status
//   is the best any vision gives it, so the visions are put in buckets by cell and range: a bucket's box of
//   positions settles most groups at once (all of them out of range, or all of them seeing the group fully), only
//   the groups at its edge are tested vision by vision, with exactly the game's sums. A bucket looks up only the
//   cells within its reach when that is fewer than all groups.
// - Who sees each unit of a partly seen group: every vision's position was read from Unity again for each unit of
//   the group (and for each group, ten a step). The positions are read once per step.
// The groups and units seen, and the vision credited with seeing each unit, are the same as before; only the order
// in which the groups are listed within a pass differs.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
internal static class VisionScan
{
	private const int Cell = 16;

	private const int FullId = -1;

	private const int PartialId = -2;

	private sealed class Bucket
	{
		public int LoX, LoY, LoZ, HiX, HiY, HiZ, Range;

		public readonly List<int> Members = new List<int>();
	}

	private static readonly AccessTools.FieldRef<SharedFactionVision, List<TrackingGroup>> TrackingGroups = AccessTools.FieldRefAccess<SharedFactionVision, List<TrackingGroup>>("_trackingGroups");

	private static readonly AccessTools.FieldRef<SharedFactionVision, List<LocalFactionVision>> WhoSeesMe = AccessTools.FieldRefAccess<SharedFactionVision, List<LocalFactionVision>>("_whoSeesMe");

	private static readonly AccessTools.FieldRef<SharedFactionVision, List<LocalFactionVision>> TrackingCollisions = AccessTools.FieldRefAccess<SharedFactionVision, List<LocalFactionVision>>("_trackingCollisions");

	private static readonly AccessTools.FieldRef<TrackingRegistry, Dictionary<XYZ, TrackingGroup>> Groups = AccessTools.FieldRefAccess<TrackingRegistry, Dictionary<XYZ, TrackingGroup>>("_groups");

	private static readonly Dictionary<long, Bucket> ByKey = new Dictionary<long, Bucket>();

	private static readonly List<Bucket> Used = new List<Bucket>();

	private static readonly Stack<Bucket> Spare = new Stack<Bucket>();

	private static XYZ[] _points = new XYZ[256];

	// Counted up by every SharedFactionVision.BrixUpdate: one step of the pass runs per call.
	internal static int Step;

	// The visions' positions read for one step of one faction's pass.
	private static Vector3[] _positions = new Vector3[256];

	private static XYZ[] _cells = new XYZ[256];

	private static bool[] _alive = new bool[256];

	private static SharedFactionVision _snapshotOf;

	private static int _snapshotStep = -1;

	private static int _snapshotCount = -1;

	private static readonly List<int> Edge = new List<int>();

	internal static bool CanRun()
	{
		return BrixSingleton<TrackingRegistry>.Instance != null;
	}

	// RefreshActiveTrackingGroupsEnumerator.AssignDetectionStatusFromAllVisions: each group the faction sees gets
	// FullId or PartialId (the best any vision gives it) and is listed once in _trackingGroups.
	internal static void AssignDetection(SharedFactionVision shared)
	{
		Dictionary<XYZ, TrackingGroup> groups = Groups(BrixSingleton<TrackingRegistry>.Instance);
		List<TrackingGroup> tracked = TrackingGroups(shared);
		List<LocalFactionVision> visions = shared.AllVisions;
		if (_points.Length < visions.Count)
		{
			_points = new XYZ[visions.Count * 2];
		}
		for (int i = 0; i < visions.Count; i++)
		{
			LocalFactionVision vision = visions[i];
			if (vision == null)
			{
				continue;
			}
			XYZ p = XYZ.FromVector3(vision.transform.position);
			_points[i] = p;
			int range = vision.LineOfSightRange;
			long key = ((long)((p.x >> 4) & 0xFFFF) << 48) | ((long)((p.y >> 4) & 0xFFFF) << 32) | ((long)((p.z >> 4) & 0xFFFF) << 16) | (long)(range & 0xFFFF);
			if (!ByKey.TryGetValue(key, out Bucket bucket))
			{
				bucket = (Spare.Count > 0) ? Spare.Pop() : new Bucket();
				bucket.Members.Clear();
				bucket.Range = range;
				bucket.LoX = bucket.HiX = p.x;
				bucket.LoY = bucket.HiY = p.y;
				bucket.LoZ = bucket.HiZ = p.z;
				ByKey[key] = bucket;
				Used.Add(bucket);
			}
			else
			{
				bucket.LoX = Mathf.Min(bucket.LoX, p.x);
				bucket.LoY = Mathf.Min(bucket.LoY, p.y);
				bucket.LoZ = Mathf.Min(bucket.LoZ, p.z);
				bucket.HiX = Mathf.Max(bucket.HiX, p.x);
				bucket.HiY = Mathf.Max(bucket.HiY, p.y);
				bucket.HiZ = Mathf.Max(bucket.HiZ, p.z);
			}
			bucket.Members.Add(i);
		}
		try
		{
			foreach (Bucket bucket in Used)
			{
				int r = bucket.Range;
				// The keys of the groups whose cell box can be within reach of the bucket's box.
				int gx0 = CeilDiv(bucket.LoX - r - (Cell - 1)), gx1 = (bucket.HiX + r) >> 4;
				int gy0 = CeilDiv(bucket.LoY - r - (Cell - 1)), gy1 = (bucket.HiY + r) >> 4;
				int gz0 = CeilDiv(bucket.LoZ - r - (Cell - 1)), gz1 = (bucket.HiZ + r) >> 4;
				long cells = (long)(gx1 - gx0 + 1) * (gy1 - gy0 + 1) * (gz1 - gz0 + 1);
				if (cells < groups.Count)
				{
					XYZ at = default;
					for (at.x = gx0; at.x <= gx1; at.x++)
					{
						for (at.y = gy0; at.y <= gy1; at.y++)
						{
							for (at.z = gz0; at.z <= gz1; at.z++)
							{
								if (groups.TryGetValue(at, out TrackingGroup group) && group != null)
								{
									Detect(group, bucket, tracked);
								}
							}
						}
					}
				}
				else
				{
					foreach (KeyValuePair<XYZ, TrackingGroup> pair in groups)
					{
						if (pair.Value != null)
						{
							Detect(pair.Value, bucket, tracked);
						}
					}
				}
			}
		}
		finally
		{
			foreach (Bucket bucket in Used)
			{
				Spare.Push(bucket);
			}
			Used.Clear();
			ByKey.Clear();
		}
	}

	private static int CeilDiv(int a)
	{
		return -((-a) >> 4);
	}

	// The game's test per vision: nearest point of the group's box within range is seen partly, its farthest
	// corner within range fully. A group seen fully is not looked at again.
	private static void Detect(TrackingGroup group, Bucket bucket, List<TrackingGroup> tracked)
	{
		if (group.visionScanId == FullId)
		{
			return;
		}
		int r2 = bucket.Range * bucket.Range;
		XYZ g = group.Position;
		int ox = g.x * Cell, oy = g.y * Cell, oz = g.z * Cell;
		// Nearest the bucket's box comes to the group's box: none of its visions sees the group.
		int nx = Mathf.Max(0, Mathf.Max(ox - bucket.HiX, bucket.LoX - ox - (Cell - 1)));
		int ny = Mathf.Max(0, Mathf.Max(oy - bucket.HiY, bucket.LoY - oy - (Cell - 1)));
		int nz = Mathf.Max(0, Mathf.Max(oz - bucket.HiZ, bucket.LoZ - oz - (Cell - 1)));
		if (nx * nx + ny * ny + nz * nz > r2)
		{
			return;
		}
		int status;
		// Farthest any point of the bucket's box is from the group's far corner: every vision sees it fully.
		int fx = Mathf.Max(bucket.HiX - ox, ox + (Cell - 1) - bucket.LoX);
		int fy = Mathf.Max(bucket.HiY - oy, oy + (Cell - 1) - bucket.LoY);
		int fz = Mathf.Max(bucket.HiZ - oz, oz + (Cell - 1) - bucket.LoZ);
		if (fx * fx + fy * fy + fz * fz <= r2)
		{
			status = FullId;
		}
		else
		{
			status = 0;
			List<int> members = bucket.Members;
			for (int i = 0; i < members.Count; i++)
			{
				XYZ p = _points[members[i]];
				int ax = ox - p.x, bx = p.x - (Cell - 1) - ox;
				int ay = oy - p.y, by = p.y - (Cell - 1) - oy;
				int az = oz - p.z, bz = p.z - (Cell - 1) - oz;
				int mx = Mathf.Max(0, Mathf.Max(ax, bx)), my = Mathf.Max(0, Mathf.Max(ay, by)), mz = Mathf.Max(0, Mathf.Max(az, bz));
				if (mx * mx + my * my + mz * mz > r2)
				{
					continue;
				}
				status = PartialId;
				int kx = Mathf.Min(ax, bx), ky = Mathf.Min(ay, by), kz = Mathf.Min(az, bz);
				if (kx * kx + ky * ky + kz * kz <= r2)
				{
					status = FullId;
					break;
				}
			}
			if (status == 0)
			{
				return;
			}
		}
		if (group.visionScanId == 0)
		{
			tracked.Add(group);
		}
		group.visionScanId = status;
	}

	// The visions' positions, read once per step (the game read them again for every unit of every group).
	private static void Snapshot(SharedFactionVision shared)
	{
		List<LocalFactionVision> visions = shared.AllVisions;
		if (ReferenceEquals(_snapshotOf, shared) && _snapshotStep == Step && _snapshotCount == visions.Count)
		{
			return;
		}
		_snapshotOf = shared;
		_snapshotStep = Step;
		_snapshotCount = visions.Count;
		if (_positions.Length < visions.Count)
		{
			_positions = new Vector3[visions.Count * 2];
			_cells = new XYZ[visions.Count * 2];
			_alive = new bool[visions.Count * 2];
		}
		for (int i = 0; i < visions.Count; i++)
		{
			LocalFactionVision vision = visions[i];
			_alive[i] = vision != null;
			if (!_alive[i])
			{
				// Skipped by every test below.
				_positions[i] = default;
				_cells[i] = default;
				continue;
			}
			_positions[i] = vision.transform.position;
			_cells[i] = XYZ.FromVector3(_positions[i]);
		}
	}

	// RefreshActiveCollisionsEnumerator.AssignCollisionsFromPartialGroup, the same steps with the positions read once.
	internal static void AssignFromPartialGroup(SharedFactionVision shared, TrackingGroup partialGroup)
	{
		if ((partialGroup.trackedFactions & shared.EnemyOrNeutralFactions) == 0)
		{
			return;
		}
		Snapshot(shared);
		List<LocalFactionVision> visions = shared.AllVisions;
		// A crowded group is tested against the visions that reach its box at all, as the game does.
		bool edge = partialGroup.count >= 10;
		if (edge)
		{
			Edge.Clear();
			for (int i = 0; i < visions.Count; i++)
			{
				if (_alive[i] && TrackingRegistry.IsInAtLeastPartialRange(partialGroup, _cells[i], visions[i].LineOfSightRange))
				{
					Edge.Add(i);
				}
			}
		}
		List<LocalFactionVision> whoSeesMe = WhoSeesMe(shared);
		List<LocalFactionVision> collisions = TrackingCollisions(shared);
		foreach (TrackingEntry entry in partialGroup.Entries)
		{
			if (((1 << entry.faction.ID) & shared.EnemyOrNeutralFactions) == 0)
			{
				continue;
			}
			LocalFactionVision vision = entry.Vision;
			if (vision.IsNullOrReleased() || vision.CollisionTrackingId == -1)
			{
				continue;
			}
			if (vision.CollisionTrackingId >= 1 + whoSeesMe.Count)
			{
				Plugin.Log.LogWarning("VisionScan: CollisionTrackingId=" + vision.CollisionTrackingId + ", _whoSeesMe.Count=" + whoSeesMe.Count + ", collisions=" + collisions.Count);
				continue;
			}
			if (!LocalFactionVision.IsResponsibleForVision(vision))
			{
				continue;
			}
			Vector3 target = entry.GameObject.transform.position;
			if (vision.CollisionTrackingId >= 1)
			{
				LocalFactionVision seer = whoSeesMe[vision.CollisionTrackingId - 1];
				if (!seer.IsNullOrReleased() && InRange(target, seer.transform.position, seer.LineOfSightRange))
				{
					vision.CollisionTrackingId = -1;
				}
				else if (FirstSeer(target, visions, edge, ref seer))
				{
					whoSeesMe[vision.CollisionTrackingId - 1] = seer;
					vision.CollisionTrackingId = -1;
				}
			}
			else
			{
				LocalFactionVision seer = null;
				if (FirstSeer(target, visions, edge, ref seer))
				{
					whoSeesMe.Add(seer);
					collisions.Add(vision);
				}
			}
		}
	}

	private static bool InRange(Vector3 target, Vector3 position, int range)
	{
		return (position - target).sqrMagnitude <= (float)(range * range);
	}

	// The first vision (in the faction's order) that has the target within range.
	private static bool FirstSeer(Vector3 target, List<LocalFactionVision> visions, bool edge, ref LocalFactionVision seer)
	{
		int count = edge ? Edge.Count : visions.Count;
		for (int k = 0; k < count; k++)
		{
			int i = edge ? Edge[k] : k;
			if (_alive[i] && InRange(target, _positions[i], visions[i].LineOfSightRange))
			{
				seer = visions[i];
				return true;
			}
		}
		return false;
	}

	internal static MethodBase Nested(string type, string method)
	{
		return AccessTools.Method(AccessTools.Inner(typeof(SharedFactionVision), type), method);
	}
}

[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch]
internal static class VisionScanDetectionPatch
{
	private static MethodBase TargetMethod()
	{
		return VisionScan.Nested("RefreshActiveTrackingGroupsEnumerator", "AssignDetectionStatusFromAllVisions");
	}

	// The struct's own field, read through the method's this.
	private static bool Prefix(SharedFactionVision ____sharedVision)
	{
		if (____sharedVision == null || !VisionScan.CanRun())
		{
			return true;
		}
		VisionScan.AssignDetection(____sharedVision);
		return false;
	}
}

[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch]
internal static class VisionScanPartialPatch
{
	private static MethodBase TargetMethod()
	{
		return VisionScan.Nested("RefreshActiveCollisionsEnumerator", "AssignCollisionsFromPartialGroup");
	}

	private static bool Prefix(SharedFactionVision ____sharedVision, TrackingRegistry.TrackingGroup partialGroup)
	{
		if (____sharedVision == null || partialGroup == null)
		{
			return true;
		}
		VisionScan.AssignFromPartialGroup(____sharedVision, partialGroup);
		return false;
	}
}

// One step of the pass per call: the positions read for a step are not used in the next.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(SharedFactionVision), "BrixUpdate")]
internal static class VisionScanStepPatch
{
	private static void Prefix()
	{
		VisionScan.Step++;
	}
}
