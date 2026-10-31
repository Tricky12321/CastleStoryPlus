#if UNITY6_PLAYER && PLAYER_COMPUTE
using System.Collections.Generic;
using System.Reflection;
using Brix.Engine;
using Brix.Game;
using CastleStoryPlus.Compute;
using CastleStoryPlus.Core;
using HarmonyLib;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using TrackingGroup = Brix.Game.Components.Tracking.TrackingRegistry.TrackingGroup;

namespace CastleStoryPlus.Performance;

// VisionScan's detection step (which tracking groups a faction sees fully or partly) as a Burst job of the player
// (CastleStoryPlus.Compute.VisionDetect, see PlayerJobs). At step 2 of a faction's pass the visions' buckets and the
// groups' cells are copied into native arrays and the job is scheduled on Unity's worker threads; the statuses are
// applied at step 3, the next call of the pass (at the 60 steps a second of VisionRate mostly a frame later), before
// anything reads them. The groups and statuses are the same as VisionScan's; only the order in which newly seen groups
// are listed within a pass follows the registry instead of the buckets.
internal static class VisionJobs
{
	private const int FullId = -1;

	private static NativeArray<int3> _cells;

	private static NativeArray<int3> _points;

	private static NativeArray<VisionBucket> _buckets;

	private static NativeArray<sbyte> _status;

	private static TrackingGroup[] _groups = new TrackingGroup[0];

	private static int _groupCount;

	private static int _bucketCount;

	private static JobHandle _handle;

	// The faction whose statuses the running job works out; null when none runs.
	private static SharedFactionVision _pending;

	private static int _verified;

	private static int _mismatches;

	private static readonly AccessTools.FieldRef<SharedFactionVision, List<TrackingGroup>> TrackingGroups = AccessTools.FieldRefAccess<SharedFactionVision, List<TrackingGroup>>("_trackingGroups");

	internal static void Schedule(SharedFactionVision shared, Dictionary<XYZ, TrackingGroup> groups, List<VisionScan.Bucket> used)
	{
		// One job at a time: a pass left unfinished (a faction gone) is applied first.
		Finish(_pending);
		int members = 0;
		foreach (VisionScan.Bucket bucket in used)
		{
			members += bucket.Members.Count;
		}
		Ensure(ref _buckets, used.Count);
		Ensure(ref _points, members);
		Ensure(ref _cells, groups.Count);
		Ensure(ref _status, groups.Count);
		if (_groups.Length < groups.Count)
		{
			_groups = new TrackingGroup[groups.Count * 2];
		}
		XYZ[] points = VisionScan.Points;
		int first = 0;
		for (int b = 0; b < used.Count; b++)
		{
			VisionScan.Bucket bucket = used[b];
			_buckets[b] = new VisionBucket
			{
				Lo = new int3(bucket.LoX, bucket.LoY, bucket.LoZ),
				Hi = new int3(bucket.HiX, bucket.HiY, bucket.HiZ),
				Range = bucket.Range,
				First = first,
				Count = bucket.Members.Count,
			};
			foreach (int member in bucket.Members)
			{
				XYZ p = points[member];
				_points[first++] = new int3(p.x, p.y, p.z);
			}
		}
		_groupCount = 0;
		foreach (KeyValuePair<XYZ, TrackingGroup> pair in groups)
		{
			if (pair.Value == null)
			{
				continue;
			}
			XYZ g = pair.Value.Position;
			_cells[_groupCount] = new int3(g.x, g.y, g.z);
			_groups[_groupCount++] = pair.Value;
		}
		if (_groupCount == 0 || used.Count == 0)
		{
			return;
		}
		_bucketCount = used.Count;
		_handle = VisionDetect.Schedule(_cells, _groupCount, _buckets, _bucketCount, _points, _status);
		_pending = shared;
		// The workers start on it now, not when something waits on it.
		JobHandle.ScheduleBatchedJobs();
	}

	// The statuses of the faction's job, when one is pending: waits for it and applies them as VisionScan.Detect does.
	internal static void Finish(SharedFactionVision shared)
	{
		if (_pending == null || !ReferenceEquals(_pending, shared))
		{
			return;
		}
		_handle.Complete();
		_pending = null;
		if (PlayerJobs.Verify)
		{
			Check();
		}
		List<TrackingGroup> tracked = TrackingGroups(shared);
		for (int i = 0; i < _groupCount; i++)
		{
			sbyte status = _status[i];
			TrackingGroup group = _groups[i];
			_groups[i] = null;
			if (status == 0 || group == null || group.visionScanId == FullId)
			{
				continue;
			}
			if (group.visionScanId == 0)
			{
				tracked.Add(group);
			}
			group.visionScanId = status;
		}
		_groupCount = 0;
	}

	// A job left running when the game is left: waited for and dropped.
	internal static void Drop()
	{
		_handle.Complete();
		_pending = null;
		System.Array.Clear(_groups, 0, _groups.Length);
		_groupCount = 0;
	}

	internal static void Dispose()
	{
		Drop();
		Free(ref _cells);
		Free(ref _points);
		Free(ref _buckets);
		Free(ref _status);
	}

	private static void Ensure<T>(ref NativeArray<T> array, int count) where T : struct
	{
		if (array.IsCreated && array.Length >= count)
		{
			return;
		}
		Free(ref array);
		array = new NativeArray<T>(System.Math.Max(256, count * 2), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
	}

	private static void Free<T>(ref NativeArray<T> array) where T : struct
	{
		if (array.IsCreated)
		{
			array.Dispose();
		}
		array = default;
	}

	// Every group's status worked out again the game's way (every vision against the group, its integer sums) and
	// compared with the job's; logged every 200 checks.
	private static void Check()
	{
		for (int i = 0; i < _groupCount; i++)
		{
			int3 o = _cells[i] * 16;
			int expected = 0;
			for (int b = 0; b < _bucketCount && expected != FullId; b++)
			{
				VisionBucket bucket = _buckets[b];
				int r2 = bucket.Range * bucket.Range;
				for (int k = bucket.First; k < bucket.First + bucket.Count; k++)
				{
					int3 p = _points[k];
					int3 a = o - p;
					int3 c = p - 15 - o;
					int3 m = math.max(math.max(a, c), 0);
					if (math.dot(m, m) > r2)
					{
						continue;
					}
					int3 n = math.min(a, c);
					if (math.dot(n, n) <= r2)
					{
						expected = FullId;
						break;
					}
					expected = -2;
				}
			}
			if (expected != _status[i])
			{
				_mismatches++;
			}
		}
		_verified++;
		if (_verified % 200 == 0)
		{
			Plugin.Log.LogInfo("VisionJobs: " + _verified + " passes checked against the game's sums, " + _mismatches + " groups differed" + (PlayerJobs.Burst ? " (Burst)" : " (Mono)"));
		}
	}
}

// Step 3 of a faction's pass (RemoveNoLongerDetectedTrackingGroups) reads the statuses: the job's are applied first.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch]
internal static class VisionJobsApplyPatch
{
	private static MethodBase TargetMethod()
	{
		return VisionScan.Nested("RefreshActiveTrackingGroupsEnumerator", "RemoveNoLongerDetectedTrackingGroups");
	}

	private static void Prefix(SharedFactionVision ____sharedVision)
	{
		VisionJobs.Finish(____sharedVision);
	}
}
#endif
