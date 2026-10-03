using System;
using System.Collections.Generic;

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
