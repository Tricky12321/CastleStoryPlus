using System;
using System.Collections.Generic;
using System.Diagnostics;
using Brix.Engine;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Performance;

// A tunnel task looks over every voxel of its tunnel every 100 AI ticks (2 game seconds) for blocks to dig and stairs
// to build, both lists in one go: up to 5 ms in one fixed update every 2 s, a stutter while a tunnel is open. The pass
// is now spread over the next ticks, a few dozen voxels at a time, at most TickMilliseconds a tick: goals no longer
// wanted are dropped first as before, then the tunnel's voxels are looked at in the same order. A pass whose tunnel
// changes under it starts again.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(TunnelProject), "ApplyScheduledGoalRefresh")]
internal static class TunnelGoalRefresh
{
	private const double TickMilliseconds = 0.25;

	private const int Chunk = 32;

	private sealed class Pass
	{
		internal IEnumerator<XYZ> Voxels;

		internal readonly List<XYZ> Next = new List<XYZ>(Chunk);
	}

	// Only tunnels with a pass under way (net35 has no ConditionalWeakTable); an ended pass leaves the table.
	private static readonly Dictionary<TunnelProject, Pass> Passes = new Dictionary<TunnelProject, Pass>();

	private static readonly Stopwatch Watch = new Stopwatch();

	private static bool Prefix(TunnelProject __instance)
	{
		VoxelGoalsStorage digs = __instance.goalStorage;
		VoxelGoalsStorage stairs = __instance.stairGoalsStorage;
		if (digs == null || stairs == null)
		{
			return true;
		}
		if (!Passes.TryGetValue(__instance, out Pass pass))
		{
			if (!__instance.ScheduledRefresh)
			{
				return false;
			}
			pass = new Pass();
			Passes[__instance] = pass;
			__instance.ScheduledRefresh = false;
			digs.RemoveOutdatedGoals();
			stairs.RemoveOutdatedGoals();
			pass.Voxels = digs.bounds.Voxels.GetEnumerator();
		}
		Watch.Reset();
		Watch.Start();
		try
		{
			do
			{
				pass.Next.Clear();
				while (pass.Next.Count < Chunk && pass.Voxels.MoveNext())
				{
					pass.Next.Add(pass.Voxels.Current);
				}
				if (pass.Next.Count > 0)
				{
					digs.GetNewGoals(pass.Next, digs.goalKey);
					stairs.GetNewGoals(pass.Next, stairs.goalKey);
				}
				if (pass.Next.Count < Chunk)
				{
					End(__instance, pass);
					return false;
				}
			}
			while (Watch.Elapsed.TotalMilliseconds < TickMilliseconds);
		}
		catch (InvalidOperationException)
		{
			// The tunnel changed under the pass: look again from the start.
			End(__instance, pass);
			__instance.ScheduledRefresh = true;
		}
		return false;
	}

	private static void End(TunnelProject tunnel, Pass pass)
	{
		pass.Voxels.Dispose();
		Passes.Remove(tunnel);
	}
}
