using Brix.Game.AI;
using HarmonyLib;

namespace CastleStoryPlus.Core;

// A task's state flags (ProjectState.flags, synced to clients and saved with the task) use only the bits 1 to 32;
// features keep their own settings in the bits above (PauseTasks, WorkLimit). The game rebuilds the flags from
// scratch whenever the task's state changes, which dropped those bits: this keeps them.
[Feature]
[HarmonyPatch(typeof(ProjectState), "Set")]
internal static class ProjectFlags
{
	// The bits the game sets itself.
	private const int GameBits = 63;

	private static readonly AccessTools.FieldRef<ProjectState, int> Flags = AccessTools.FieldRefAccess<ProjectState, int>("flags");

	internal static int Get(ProjectState state)
	{
		return (state != null) ? Flags(state) : 0;
	}

	// Host: synced to clients. Client: shown at once, until the host's value comes.
	internal static void Set(ProjectState state, int flags)
	{
		if (state == null || Flags(state) == flags)
		{
			return;
		}
		if (UnityEngine.Networking.NetworkServer.active)
		{
			state.Networkflags = flags;
		}
		else
		{
			Flags(state) = flags;
		}
	}

	private static bool Prefix(ProjectState __instance, bool full, bool goals, bool editing, bool storage, bool resource, ref int ___flags, ref bool ___initialized)
	{
		int num = 0;
		num |= full ? 1 : 0;
		num |= goals ? 2 : 0;
		num |= editing ? 4 : 0;
		num |= __instance.Commited ? 8 : 0;
		num |= storage ? 16 : 0;
		num |= resource ? 32 : 0;
		num |= ___flags & ~GameBits;
		if (num != ___flags)
		{
			__instance.Networkflags = num;
			___initialized = true;
		}
		return false;
	}
}
