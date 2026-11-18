using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.UI.GameSelector;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.WorkerAI;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// The red rings over tasks no worker can reach (UnreachableTasks) show only while their task is selected (#101): a
// deep quarry's marked voxels covered the view. Not parallel: it selects a task, and the selection is the player's.
internal static class UnreachableRingTests
{
	[GameTest("Tasks", "A marked thing of a task gets its red ring only while the task is selected (Rings = SelectedTask), and always with Rings = All (#101)", TimeoutSeconds = 60f)]
	private static IEnumerator RingOnlyForSelectedTask(TestContext t)
	{
		t.NotNull(UnreachableTasks.Rings, "The Rings setting");
		UnreachableTasks.RingMode mode = UnreachableTasks.Rings.Value;
		UnreachableTasks.Rings.Value = UnreachableTasks.RingMode.SelectedTask;
		XYZ site = Scenario.Site(t, 4, 4);
		BuildProject project = Scenario.BuildProject(t, site);
		List<GameObject> items = Scenario.Items(t, RawResources.RawStone, 1, site.ToVector3());
		t.True(items.Count == 1, "The stone was not made");
		GameObject stone = items[0];
		t.Cleanup(() =>
		{
			UnreachableTasks.Forget(stone);
			UnreachableTasks.Rings.Value = mode;
			if (project != null && project.selection)
			{
				UIGameSelector.Unselect(project.gameObject);
			}
		});
		UIGameSelector.UnselectEverything();
		UnreachableTasks.MarkByHand(stone, project, "test");
		// Rings are drawn every 15 frames.
		yield return t.Frames(40);
		t.True(UnreachableTasks.IsMarked(stone), "The stone is not marked");
		t.True(!UnreachableTasks.IsRinged(stone), "The stone has a ring while its task is not selected");
		UIGameSelector.Select(project.gameObject);
		yield return t.Frames(2);
		t.True(project.selection, "The task could not be selected");
		yield return t.Until(() => UnreachableTasks.IsRinged(stone), 5f, "the ring while the task is selected");
		UIGameSelector.Unselect(project.gameObject);
		yield return t.Frames(2);
		t.True(!project.selection, "The task could not be unselected");
		yield return t.Until(() => !UnreachableTasks.IsRinged(stone), 5f, "the ring to go when the task is no longer selected");
		UnreachableTasks.Rings.Value = UnreachableTasks.RingMode.All;
		yield return t.Until(() => UnreachableTasks.IsRinged(stone), 5f, "the ring with Rings = All");
		UnreachableTasks.Rings.Value = UnreachableTasks.RingMode.Off;
		yield return t.Until(() => !UnreachableTasks.IsRinged(stone), 5f, "the ring to go with Rings = Off");
	}
}
