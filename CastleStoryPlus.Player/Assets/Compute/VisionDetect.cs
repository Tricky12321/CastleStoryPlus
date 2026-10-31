using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace CastleStoryPlus.Compute
{
	// Visions (bricktrons, buildings) of one faction put together by cell and range, as the mod's VisionScan does: a
	// box around their positions, one range, and their positions at First .. First + Count - 1 of the points.
	public struct VisionBucket
	{
		public int3 Lo;

		public int3 Hi;

		public int Range;

		public int First;

		public int Count;
	}

	// Which tracking groups (16 x 16 x 16 cells) a faction's visions see fully or partly, the step of the game's faction
	// vision pass (SharedFactionVision, AssignDetectionStatusFromAllVisions) that went over every group for every vision.
	// A group's status is the best any vision gives it: FullId when some vision has the group's farthest corner within
	// range, PartialId when some vision has its nearest point within range, else 0. Exactly the game's integer sums.
	// The mod snapshots the positions on the main thread, schedules this and applies the statuses later in the frame or in
	// the next one.
	public static class VisionDetect
	{
		public const sbyte FullId = -1;

		public const sbyte PartialId = -2;

		private const int Cell = 16;

		// Groups per job batch: a group is tested against every bucket, some hundreds of sums.
		private const int Batch = 32;

		public static JobHandle Schedule(NativeArray<int3> groups, int groupCount, NativeArray<VisionBucket> buckets, int bucketCount, NativeArray<int3> points, NativeArray<sbyte> status, JobHandle dependsOn = default)
		{
			var job = new DetectJob
			{
				Groups = groups,
				Buckets = buckets,
				BucketCount = bucketCount,
				Points = points,
				Status = status,
			};
			return job.Schedule(groupCount, Batch, dependsOn);
		}

		// The same on the calling thread (and with Burst when it is on): for checks and small counts.
		public static void Run(NativeArray<int3> groups, int groupCount, NativeArray<VisionBucket> buckets, int bucketCount, NativeArray<int3> points, NativeArray<sbyte> status)
		{
			var job = new DetectJob
			{
				Groups = groups,
				Buckets = buckets,
				BucketCount = bucketCount,
				Points = points,
				Status = status,
			};
			job.Run(groupCount);
		}

		[BurstCompile]
		private struct DetectJob : IJobParallelFor
		{
			[ReadOnly]
			public NativeArray<int3> Groups;

			[ReadOnly]
			public NativeArray<VisionBucket> Buckets;

			public int BucketCount;

			[ReadOnly]
			public NativeArray<int3> Points;

			[WriteOnly]
			public NativeArray<sbyte> Status;

			public void Execute(int index)
			{
				int3 o = Groups[index] * Cell;
				int3 far = o + (Cell - 1);
				sbyte status = 0;
				for (int b = 0; b < BucketCount; b++)
				{
					VisionBucket bucket = Buckets[b];
					int r2 = bucket.Range * bucket.Range;
					// Nearest the bucket's box comes to the group's box: none of its visions sees the group.
					int3 n = math.max(0, math.max(o - bucket.Hi, bucket.Lo - far));
					if (math.dot(n, n) > r2)
					{
						continue;
					}
					// Farthest any point of the bucket's box is from the group's far corner: every vision sees it fully.
					int3 f = math.max(bucket.Hi - o, far - bucket.Lo);
					if (math.dot(f, f) <= r2)
					{
						status = FullId;
						break;
					}
					int end = bucket.First + bucket.Count;
					for (int i = bucket.First; i < end; i++)
					{
						int3 p = Points[i];
						int3 a = o - p;
						int3 c = p - far;
						int3 m = math.max(0, math.max(a, c));
						if (math.dot(m, m) > r2)
						{
							continue;
						}
						status = PartialId;
						int3 k = math.min(a, c);
						if (math.dot(k, k) <= r2)
						{
							status = FullId;
							break;
						}
					}
					if (status == FullId)
					{
						break;
					}
				}
				Status[index] = status;
			}
		}
	}
}
