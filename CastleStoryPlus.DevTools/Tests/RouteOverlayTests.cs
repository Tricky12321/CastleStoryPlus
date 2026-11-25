#if UNITY6
using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Game;
using Brix.Game.AI;
using Brix.UI.GameSelector;
using CastleStoryPlus.Core;
using CastleStoryPlus.DevTools.Pathing;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.Pathfinding;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// The route overlay (RouteOverlay, #200): the rest of a selected bricktron's route is drawn while the overlay is shown,
// with the climb onto a step drawn as climbing, and nothing is drawn for a bricktron that is not selected, once it has
// arrived, or with the overlay hidden. Not parallel: it selects a bricktron, and the selection is the player's.
internal static class RouteOverlayTests
{
	[GameTest("Pathfinding", "The route overlay draws the rest of a selected walking bricktron's route, the climb onto a step as climbing, and nothing once it arrives, for an unselected one or with the overlay hidden (#200)", TimeoutSeconds = 120f)]
	private static IEnumerator DrawsSelectedRoute(TestContext t)
	{
		if (!Plugin.IsEnabled(Features.RouteOverlay) || RouteOverlay.Shown == null)
		{
			t.Skip("RouteOverlay is switched off");
		}
		// Flat ground, and east of the middle a step one block high: the walk from the west climbs onto it.
		XYZ center = Scenario.Site(t, 17, 13, 4);
		List<XYZ> blocks = new List<XYZ>();
		for (int x = center.x; x <= center.x + 5; x++)
		{
			for (int z = center.z - 4; z <= center.z + 4; z++)
			{
				blocks.Add(new XYZ(x, center.y, z));
			}
		}
		TestWorld.PlaceBlocks(t, blocks);
		yield return TestWorld.WaitForGraph(t);
		if (!PathProbe.Ground(center.x + 2, center.z, out XYZ ground) || ground.y != center.y + 1)
		{
			t.Skip("The step was not built where it should be (the ground there is at " + ground + ")");
		}
		XYZ bottom = new XYZ(center.x - 6, center.y, center.z);
		XYZ top = new XYZ(center.x + 3, center.y + 1, center.z);
		Labor walker = Scenario.Bricktron(t, Occupation.Type.Builder, bottom);
		Labor other = Scenario.Bricktron(t, Occupation.Type.Builder, new XYZ(center.x - 6, center.y, center.z + 2));
		yield return t.Frames(5);
		walker.Autonomy = AutonomyStatus.WaitForOrders;
		other.Autonomy = AutonomyStatus.WaitForOrders;
		bool shown = RouteOverlay.Shown.Value;
		t.Cleanup(() =>
		{
			RouteOverlay.Shown.Value = shown;
			if (walker != null)
			{
				UIGameSelector.Unselect(walker.gameObject);
			}
		});
		UIGameSelector.UnselectEverything();
		UIGameSelector.Select(walker.gameObject);
		yield return t.Frames(2);
		t.True(UIGameSelector.IsSelected(walker.gameObject), "The walker could not be selected");

		// Hidden: nothing is drawn, also on the way.
		RouteOverlay.Shown.Value = false;
		Send(walker, top);
		Send(other, new XYZ(center.x - 1, center.y, center.z + 2));
		bool drawnHidden = false;
		yield return t.UntilGame(() =>
		{
			drawnHidden |= RouteOverlay.DrawnPoints(walker) > 0;
			return Units.VoxelOf(walker) == top && walker.navigation.Locomotion.InPlace;
		}, 40f, "the walker to reach the top of the step with the overlay hidden");
		t.False(drawnHidden, "A route was drawn with the overlay hidden");

		// Shown: the walk back down and up again is drawn; the unselected bricktron's is not.
		RouteOverlay.Shown.Value = true;
		Send(walker, bottom);
		yield return t.UntilGame(() => Units.VoxelOf(walker) == bottom && walker.navigation.Locomotion.InPlace, 40f, "the walker to get back down");
		Send(walker, top);
		Send(other, new XYZ(center.x - 6, center.y, center.z + 2));
		int most = 0;
		bool climbing = false;
		bool walking = false;
		bool otherDrawn = false;
		yield return t.UntilGame(() =>
		{
			most = Mathf.Max(most, RouteOverlay.DrawnPoints(walker));
			climbing |= RouteOverlay.Drawn(walker, RouteStepState.Climbing);
			walking |= RouteOverlay.Drawn(walker, RouteStepState.Walking);
			otherDrawn |= RouteOverlay.DrawnPoints(other) > 0;
			return Units.VoxelOf(walker) == top && walker.navigation.Locomotion.InPlace;
		}, 40f, "the walker to reach the top of the step with the overlay shown");
		t.Record("mostPoints", most);
		t.AtLeast(3, most, "Route points drawn for the selected walker (itself and the nodes ahead)");
		t.True(walking, "No walking part of the route was drawn");
		t.True(climbing, "The climb onto the step was not drawn as climbing");
		t.False(otherDrawn, "A route was drawn for a bricktron that is not selected");
		// Arrived: the route is gone.
		yield return t.Frames(5);
		t.Equal(0, RouteOverlay.DrawnPoints(walker), "Route points drawn after the walker arrived");
	}

	private static void Send(Labor labor, XYZ goal)
	{
		labor.AssignToIdle();
		Order.Package.OfLocation(Order.Move.RunToward, Location.Of(goal), Labor.DecisionSource.User).Send(labor);
	}
}
#endif
