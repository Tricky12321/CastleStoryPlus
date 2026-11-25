using System.Collections;
using Brix.Engine;
using Brix.Game.AI;
using Brix.Game.Components;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.WorkerAI;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// A bricktron put on a task the moment the task ended froze for good (#199): joining a crew moves the bricktron into
// the task's crew holder, and a task that has ended is switched off in the game's pool, the bricktron with it.
internal static class VanishedTaskTests
{
	private static readonly AccessTools.FieldRef<Project, WorkForceProvider> ProviderField = AccessTools.FieldRefAccess<Project, WorkForceProvider>("workforceProvider");

	[GameTest("Tasks", "A bricktron is not put on a task that has just ended: it stays with the free workers and keeps going (#199)", TimeoutSeconds = 60f)]
	private static IEnumerator NoJoiningATaskThatIsGone(TestContext t)
	{
		XYZ site = Scenario.Site(t, 6, 6);
		Labor worker = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 2, site.y, site.z));
		BuildProject project = Scenario.BuildProject(t, site);
		yield return t.Frames(5);
		t.True(VanishedTask.MayJoin(project), "The new build task takes no one");
		// The task ends (deleted, as its player's client does with an emptied build task) ...
		Scenario.Groupe.CmdDelete(project.gameObject);
		yield return t.Frames(1);
		t.True(!project.IsStillAlive, "The deleted build task is still there");
		t.True(!VanishedTask.MayJoin(project), "The deleted build task still takes bricktrons");
		// ... and the bricktron is hired into it a moment later, as the game's search does with the task it chose before.
		bool hired = project.Hire(worker);
		t.Record("hired", hired);
		t.True(!hired, "The bricktron was hired into a task that is gone");
		yield return t.Frames(3);
		t.True(worker.gameObject.activeInHierarchy, "The bricktron went into the game's pool with the task");
		t.True(worker.Project is IdleProject, "The bricktron is not with the free workers: " + ((worker.Project != null) ? worker.Project.GetType().Name : "no task"));
	}

	[GameTest("Tasks", "A bricktron left in the crew of a task that has ended is sent to the free workers within seconds (#199)", TimeoutSeconds = 60f)]
	private static IEnumerator StrandedBricktronIsFreed(TestContext t)
	{
		XYZ site = Scenario.Site(t, 6, 6);
		Labor worker = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 2, site.y, site.z));
		BuildProject project = Scenario.BuildProject(t, site);
		yield return t.Frames(5);
		WorkForceProvider provider = ProviderField(project);
		t.NotNull(provider, "The build task's crew");
		Transform holder = provider.ContainerFor(worker.gameObject);
		t.NotNull(holder, "The build task's crew holder");
		Scenario.Groupe.CmdDelete(project.gameObject);
		yield return t.Frames(1);
		t.True(!project.IsStillAlive, "The deleted build task is still there");
		// Where a bricktron that joined at the wrong moment ended up: in the crew holder of the task in the pool.
		worker.transform.SetParent(holder, true);
		t.Record("activeInPool", worker.gameObject.activeInHierarchy);
		yield return t.Until(() => worker.gameObject.activeInHierarchy && worker.Project is IdleProject, VanishedTaskRules.CheckSeconds * 3f + 2f, "the stranded bricktron to be sent to the free workers");
		t.True(worker.transform.parent != holder, "The bricktron is still in the crew holder of the task that is gone");
	}
}
