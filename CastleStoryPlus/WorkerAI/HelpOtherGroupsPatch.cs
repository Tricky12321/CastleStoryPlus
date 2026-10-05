using System.Collections.Generic;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Lifecycle.Pooling;
using Brix.Utils;
using CastleStoryPlus.Core;
using HarmonyLib;
using Smooth.Slinq;

namespace CastleStoryPlus.WorkerAI;

// A worker locked to a task group helps the other groups, by priority, when its own group has nothing
// to do, and returns to its own group as soon as that has work again. Raid projects are excluded.
[Feature(Features.WorkerAI, Features.WorkerAIInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.RefreshAvailableProjects))]
internal static class HelpOtherGroupsPatch
{
	// Task group a locked worker belongs to while it helps out in another group.
	internal static readonly Dictionary<Labor, Project> HomeProjects = new Dictionary<Labor, Project>();

	private static void Enable()
	{
		GameSession.OnLeave(HomeProjects.Clear);
	}

	internal static bool CanHelpOtherProjects(Labor labor)
	{
		return labor.Autonomy == AutonomyStatus.LockedToOperable && labor.Operable == null && labor.Project != null && !(labor.Project is IdleProject) && !(labor.Project is RaidProject2);
	}

	private static bool Prefix(Labor __instance, List<IOperable<Labor>> projects)
	{
		Labor self = __instance;
		HomeProjects.TryGetValue(self, out Project home);
		if (home != null && (home == self.Project || home.IsNullOrReleased() || !home.IsStillAlive))
		{
			HomeProjects.Remove(self);
			home = null;
		}
		bool helpOtherProjects = CanHelpOtherProjects(self);
		if (helpOtherProjects && home != null)
		{
			projects.Add(home);
		}
		if (self.Autonomy.TakeOrdersCurrentOperable && !(self.Project is IdleProject))
		{
			projects.Add(self.Project);
		}
		if (self.Autonomy.TakeOrdersCurrentOperable && self.Operable != null && self.Operable.IsStillAlive)
		{
			projects.Add(self.Operable);
		}
		if (self.Autonomy.TakeOrdersFromAnyOperable)
		{
			ProjectDatabase.For(self.faction).AvailableProjects(projects, self);
			projects.SortBy((IOperable<Labor> p, Labor l) => 0f - p.PriorityInput(l) * p.PriorityWeights, self);
		}
		else if (helpOtherProjects)
		{
			// Own task group first, then the other groups by priority.
			List<IOperable<Labor>> others = CollectionPool.RequestList<IOperable<Labor>>();
			ProjectDatabase.For(self.faction).AvailableProjects(others, self);
			others.SortBy((IOperable<Labor> p, Labor l) => 0f - p.PriorityInput(l) * p.PriorityWeights, self);
			foreach (IOperable<Labor> other in others)
			{
				if (!projects.Contains(other) && !(other is RaidProject2))
				{
					projects.Add(other);
				}
			}
			others.Dispose();
		}
		if (self.Autonomy.TakeOrdersFromIdleProject)
		{
			projects.Add(IdleProject.GetMatchingProject(self.faction));
		}
		return false;
	}
}

// Remembers the worker's own group when it starts work in another group.
[Feature(Features.WorkerAI, Features.WorkerAIInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.TryWorkInProject))]
internal static class RememberHomeProjectPatch
{
	private static void Prefix(List<IOperable<Labor>> projects, Var<int> index, out IOperable<Labor> __state)
	{
		int i = index.Get();
		__state = (i < projects.Count) ? projects[i] : null;
	}

	private static void Postfix(Labor __instance, Motus.Behavior.Node __result, IOperable<Labor> __state)
	{
		// Node.Empty means the project was skipped; anything else is a real attempt to work in it.
		if (__state == null || __result == Motus.Behavior.Node.Empty || !HelpOtherGroupsPatch.CanHelpOtherProjects(__instance))
		{
			return;
		}
		if (__state is Project other && other != __instance.Project && !(other is IdleProject) && !HelpOtherGroupsPatch.HomeProjects.ContainsKey(__instance))
		{
			HelpOtherGroupsPatch.HomeProjects[__instance] = __instance.Project;
		}
	}
}

// A new autonomy (e.g. the player unlocking the worker) forgets the remembered group.
[Feature(Features.WorkerAI, Features.WorkerAIInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.Autonomy), MethodType.Setter)]
internal static class ForgetHomeProjectPatch
{
	private static void Postfix(Labor __instance)
	{
		HelpOtherGroupsPatch.HomeProjects.Remove(__instance);
	}
}
