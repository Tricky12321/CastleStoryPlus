using System;
using System.Collections.Generic;
using UnityEngine.SceneManagement;

namespace CastleStoryPlus.Workers;

// Mod data for one bricktron. The game has no field for it, so it lives here, keyed by the bricktron's
// CharacterState, and is saved with that component (WorkerStatsSave) and synced with it (WorkerStatsNetwork).
internal class WorkerStats
{
	private static readonly Dictionary<CharacterState, WorkerStats> All = new Dictionary<CharacterState, WorkerStats>();

	public int WorkXp;

	public int CombatXp;

	// Call to arms role, see CallToArms.Role*.
	public int CallToArmsRole;

	// 0 = normal bricktron, 1 = 3x bricktron (see GiantBricktron).
	public int Tier;

	// Raised on every peer when a bricktron's stats change (locally on the server, or received by a client).
	public static event Action<CharacterState> Changed;

	public static WorkerStats For(CharacterState state)
	{
		if (state == null)
		{
			return null;
		}
		if (!All.TryGetValue(state, out WorkerStats stats))
		{
			stats = new WorkerStats();
			All[state] = stats;
		}
		return stats;
	}

	public static IEnumerable<KeyValuePair<CharacterState, WorkerStats>> Entries => All;

	// Entries are dropped when a pooled bricktron is reused (WorkerStatsReset), but bricktrons destroyed with the
	// scene of a left game are never reused. Their entries would keep the old game's objects in memory, so they go
	// once the scene is unloaded. Not cleared outright: that could run after a save has loaded new stats.
	static WorkerStats()
	{
		SceneManager.sceneUnloaded += (Scene scene) => PruneDestroyed();
	}

	private static void PruneDestroyed()
	{
		List<CharacterState> destroyed = null;
		foreach (CharacterState state in All.Keys)
		{
			if (state == null)
			{
				if (destroyed == null)
				{
					destroyed = new List<CharacterState>();
				}
				destroyed.Add(state);
			}
		}
		if (destroyed == null)
		{
			return;
		}
		foreach (CharacterState state in destroyed)
		{
			All.Remove(state);
		}
	}

	public static WorkerStats Peek(CharacterState state)
	{
		return (state != null && All.TryGetValue(state, out WorkerStats stats)) ? stats : null;
	}

	public static void Forget(CharacterState state)
	{
		if (state != null)
		{
			All.Remove(state);
		}
	}

	public static void RaiseChanged(CharacterState state)
	{
		Changed?.Invoke(state);
	}

	// Server side: change the stats, sync them to clients and notify local listeners.
	public static void Modify(CharacterState state, Action<WorkerStats> change)
	{
		WorkerStats stats = For(state);
		if (stats == null)
		{
			return;
		}
		change(stats);
		state.SetDirtyBit(WorkerStatsNetwork.DirtyBit);
		RaiseChanged(state);
	}
}
