#if UNITY6_PLAYER && PLAYER_COMPUTE
using System;
using System.IO;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using CastleStoryPlus.Compute;
using UnityEngine;

namespace CastleStoryPlus.Performance;

// The Unity 6 port's player carries jobs of its own (CastleStoryPlus.Compute, CastleStoryPlus.Player/Assets/Compute),
// compiled by Burst when the player is built (Plugins/lib_burst_generated.so). The mod hands them data-parallel work
// that ran on the main thread: snapshots taken on the main thread, the job on Unity's worker threads, the results
// applied later in the frame or in the next one, the same as the main thread's results. A player built before them
// has no CastleStoryPlus.Compute.dll, and the work stays on the main thread. [Performance] PlayerJobs switches it off.
internal static class PlayerJobs
{
	private static ConfigEntry<bool> _entry;

	// -1 not looked yet, 0 the player has no jobs, 1 it has.
	private static int _available = -1;

	internal static bool Burst { get; private set; }

	// The player's jobs were found and loaded (looked for at the first use).
	internal static bool Loaded => _available == 1;

	// Results checked against the main thread's own sums (CSP_COMPUTE_VERIFY=1): for measuring and testing only.
	internal static readonly bool Verify = Environment.GetEnvironmentVariable("CSP_COMPUTE_VERIFY") == "1";

	internal static bool Use
	{
		get
		{
			if (_available < 0)
			{
				_available = Probe() ? 1 : 0;
			}
			return _available == 1 && Entry.Value;
		}
	}

	internal static ConfigEntry<bool> Entry
	{
		get
		{
			if (_entry == null)
			{
				_entry = Plugin.Cfg.Bind("Performance", "PlayerJobs", true, "Unity 6 port: the faction vision's scan runs as a Burst job of the player on worker threads instead of on the main thread (the results are the same). Off: on the main thread.");
			}
			return _entry;
		}
	}

	private static bool Probe()
	{
		_ = Entry;
		if (!File.Exists(Path.Combine(Path.Combine(Application.dataPath, "Managed"), "CastleStoryPlus.Compute.dll")))
		{
			Plugin.Log.LogInfo("PlayerJobs: the player has no CastleStoryPlus.Compute; the work stays on the main thread");
			return false;
		}
		try
		{
			return Load();
		}
		catch (Exception e)
		{
			Plugin.Log.LogWarning("PlayerJobs: CastleStoryPlus.Compute did not load (" + e.GetType().Name + ": " + e.Message + "); the work stays on the main thread");
			return false;
		}
	}

	// Kept out of Probe, so a player without the assembly fails here and not when Probe is compiled.
	[MethodImpl(MethodImplOptions.NoInlining)]
	private static bool Load()
	{
		if (ComputeInfo.Version < 1)
		{
			return false;
		}
		Burst = ComputeInfo.BurstActive;
		Plugin.Log.LogInfo("PlayerJobs: CastleStoryPlus.Compute " + ComputeInfo.Version + ", Burst " + (Burst ? "on" : "off (the jobs run as Mono code)") + (Verify ? ", results verified" : string.Empty));
		return true;
	}
}
#endif
