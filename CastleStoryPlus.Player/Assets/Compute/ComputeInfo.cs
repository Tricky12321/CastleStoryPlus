using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace CastleStoryPlus.Compute
{
	// The player's own jobs (CastleStoryPlus.Compute): Burst compiles them ahead of time into the player
	// (Plugins/lib_burst_generated.so), and the mod schedules them through the plain API of each class. Without that
	// library the same jobs still run, as Mono code on the job threads.
	public static class ComputeInfo
	{
		// Raised when the API of this assembly changes, so a mod built against a newer one can tell.
		public const int Version = 1;

		private static int _burst = -1;

		// Whether the jobs run as Burst code: a tiny job that writes 1 when compiled, 2 when it runs as Mono code.
		public static bool BurstActive
		{
			get
			{
				if (_burst < 0)
				{
					var result = new NativeArray<int>(1, Allocator.TempJob);
					try
					{
						new ProbeJob { Result = result }.Schedule().Complete();
						_burst = result[0] == 1 ? 1 : 0;
					}
					finally
					{
						result.Dispose();
					}
				}
				return _burst == 1;
			}
		}

		[BurstCompile]
		private struct ProbeJob : IJob
		{
			public NativeArray<int> Result;

			public void Execute()
			{
				int value = 1;
				Managed(ref value);
				Result[0] = value;
			}

			// Burst leaves this call out.
			[BurstDiscard]
			private static void Managed(ref int value)
			{
				value = 2;
			}
		}
	}
}
