using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Input;
using Brix.Game.Semantique;
using Brix.Legacy;
using CastleStoryPlus.DevTools.Endpoints;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.WorkerAI;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// The reset of all bricktrons from the in-game settings sidebar, also asked for while the game is paused (#142), and a
// failed walk to a dig voxel that marks it unreachable (#143). Not parallel: the paused test stops the game's clock.
internal static class BricktronResetTests
{
	[GameTest("Tasks", "The in-game settings sidebar has a Reset bricktrons button (#142)", TimeoutSeconds = 30f)]
	private static IEnumerator SidebarHasResetButton(TestContext t)
	{
		CastleStoryPlus.GameUi.GameMenuModel model = CastleStoryPlus.GameUi.CsGameMenu.Model;
		t.NotNull(model, "The in-game menu's model");
		t.NotNull(model.Settings, "The settings sidebar");
		CastleStoryPlus.GameUi.UiSection section = model.Settings.Find("bricktrons");
		t.NotNull(section, "The Bricktrons section of the settings sidebar");
		bool found = section.Items.Exists((CastleStoryPlus.GameUi.UiItem item) => item.Id == "resetBricktrons" && item is CastleStoryPlus.GameUi.UiButton);
		t.True(found, "No Reset bricktrons button in the settings sidebar");
		yield break;
	}

	[GameTest("Tasks", "A reset asked for while the game is paused waits and is done on the first frame the game runs again (#142)", TimeoutSeconds = 60f)]
	private static IEnumerator ResetWaitsWhilePaused(TestContext t)
	{
		// The game's own pause (the Escape menu's and the sidebar's), which keeps the chosen speed.
		t.Cleanup(() => ClockConfig.Paused = false);
		ClockConfig.Paused = true;
		yield return t.Frames(2);
		t.True(Time.timeScale <= 0f, "The game could not be paused");
		string answer = BricktronRecovery.Request();
		t.Log("Asked while paused: " + answer);
		t.True(BricktronRecovery.PendingResetCount == 1, "The reset is not waiting for the game to run (" + BricktronRecovery.PendingResetCount + " waiting): " + answer);
		yield return t.Frames(10);
		t.True(BricktronRecovery.PendingResetCount == 1, "The reset ran while the game was paused");
		ClockConfig.Paused = false;
		yield return t.Until(() => BricktronRecovery.PendingResetCount == 0, 5f, "the reset to run once the game runs again");
		t.True(BricktronRecovery.LastResult != null && BricktronRecovery.LastResult.StartsWith("Reset "), "The sidebar's result after the reset: " + BricktronRecovery.LastResult);
	}

	[GameTest("Tasks", "A dig voxel a worker's walk could not find a way to is marked unreachable, not only blueprints (#143)", TimeoutSeconds = 120f)]
	private static IEnumerator FailedWalkMarksDigVoxel(TestContext t)
	{
		XYZ site = Scenario.Site(t, 10, 8);
		int top = site.y - 1;
		Vector3 min = new Vector3(site.x + 2, top, site.z - 2);
		Vector3 max = new Vector3(site.x + 4, top + 2, site.z);
		MineProject quarry = Scenario.NewProject<MineProject>(t, () => Scenario.Groupe.CmdSpawnMineProject(min, Quaternion.identity, false));
		StairOrientation stairs = StairOrientation.Default;
		Scenario.Groupe.CmdSetMine(quarry.gameObject, min, max, stairs.corner.ToVector3(), stairs.direction.ToVector3(), false);
		QuarryData data = quarry.GetComponent<QuarryData>();
		t.NotNull(data, "The quarry's data");
		stairs = data.StairOrientation;
		Scenario.Groupe.CmdSetMine(quarry.gameObject, data.Bounds.min.ToVector3(), data.Bounds.max.ToVector3(), stairs.corner.ToVector3(), stairs.direction.ToVector3(), true);
		ProjectEndpoints.Commit(quarry);
		Labor worker = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(site.x - 1, site.y, site.z - 3));
		DigGoal dig = null;
		yield return t.Until(() =>
		{
			List<Goal> goals = new List<Goal>();
			foreach (IGoalProvider provider in quarry.GetGoalProviders())
			{
				provider?.GetAll(goals);
			}
			dig = (DigGoal)goals.Find((Goal goal) => goal is DigGoal && !goal.IsNullOrReleased() && !UnreachableTasks.IsMarked(goal.gameObject));
			return dig != null;
		}, 30f, "a dig voxel of the quarry");
		GameObject voxel = dig.gameObject;
		t.Cleanup(() => UnreachableTasks.Forget(voxel));
		Exceptions.PathUnfindable failure = Exceptions.PathException.To<Exceptions.PathUnfindable>(Location.Of(voxel));
		UnreachableTasks.OnPathFailed(worker, failure);
		t.True(UnreachableTasks.IsMarked(voxel), "The dig voxel the walk failed for is not marked: " + (UnreachableTasks.WhyMarked(dig) ?? "not marked"));
		t.Log("Marked: " + UnreachableTasks.WhyMarked(dig));
	}
}
