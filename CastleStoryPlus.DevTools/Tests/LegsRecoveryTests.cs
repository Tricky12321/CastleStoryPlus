using System.Collections;
using Brix.Engine;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using CastleStoryPlus.DevTools.Testing;
using CastleStoryPlus.WorkerAI;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Tests;

// A bricktron whose legs no longer take orders (#156): the legs' state machine left in its airborne state (stunned,
// then standing up) while the animation stands in place, as when the stand-up entry of a blow in a fight is missed.
// The game's every task then waited for good for the legs. Not parallel: one test switches the out-of-step check off.
internal static class LegsRecoveryTests
{
	// Leaves the worker's legs airborne while its animation stays in place: the legs' state machine is told the
	// animation entered the stunned state (it goes airborne) and then the stand-up state, which it waits in for the
	// animation to stand. The animator never left its standing state, so it never reports standing again.
	private static void Strand(Labor worker)
	{
		Locomotion4 legs = LocomotionRecovery.LegsOf(worker);
		GridLocomotionControllerInterface controller = legs.controller;
		AnimatorStateInfo info = legs.animator.GetCurrentAnimatorStateInfo(0);
		controller.Stunned.EnterState(info);
		controller.StandingUp.EnterState(info);
	}

	private static string Describe(Labor worker)
	{
		Locomotion4 legs = LocomotionRecovery.LegsOf(worker);
		return worker.name + " at " + XYZ.FromVector3(worker.transform.position) + ", legs " + legs.state + (legs.IsAirborne ? " airborne" : string.Empty) + (legs.CanMove() ? " can move" : " cannot move") + ", task " + (worker.CurrentTask != null ? worker.CurrentTask.Description : "none");
	}

	private static IEnumerator StrandedWorker(TestContext t, XYZ at, System.Action<Labor> found)
	{
		if (!Plugin.IsEnabled(Features.StuckReset))
		{
			t.Skip("StuckReset is off");
		}
		Labor worker = Scenario.Bricktron(t, Occupation.Type.Builder, at);
		yield return t.Frames(5);
		worker.Autonomy = AutonomyStatus.WaitForOrders;
		worker.AssignToIdle();
		Locomotion4 legs = LocomotionRecovery.LegsOf(worker);
		t.NotNull(legs, "The worker's legs");
		yield return t.UntilGame(() => legs.CanMove(), 20f, "the worker to stand", 5f, () => t.Log(Describe(worker)));
		Strand(worker);
		t.False(legs.CanMove(), "The worker's legs could not be stranded: " + Describe(worker));
		t.True(legs.IsAirborne, "The worker's legs are not airborne after the strand: " + Describe(worker));
		t.Log("Stranded: " + Describe(worker));
		found(worker);
	}

	[GameTest("Tasks", "Legs left airborne while the bricktron stands in place are put back in step by themselves (#156)", TimeoutSeconds = 90f)]
	private static IEnumerator OutOfStepLegsAreResynced(TestContext t)
	{
		XYZ site = Scenario.Site(t, 8, 6);
		Labor worker = null;
		yield return StrandedWorker(t, site, (Labor w) => worker = w);
		int before = LocomotionRecovery.LegsResynced;
		Locomotion4 legs = LocomotionRecovery.LegsOf(worker);
		yield return t.UntilGame(() => legs.CanMove(), 6f, "the legs to be put back in step", 2f, () => t.Log(Describe(worker)));
		t.True(LocomotionRecovery.LegsResynced > before, "The legs can move again, but not through the out-of-step check");
		t.False(legs.IsAirborne, "The legs are still airborne: " + Describe(worker));
	}

	[GameTest("Tasks", "A player's move order frees a bricktron whose legs were left airborne while it stood (#156)", TimeoutSeconds = 120f)]
	private static IEnumerator MoveOrderFreesStrandedLegs(TestContext t)
	{
		// Only the task's wait may put the legs back here.
		t.Cleanup(() => LocomotionRecovery.OutOfStepCheckOff = false);
		LocomotionRecovery.OutOfStepCheckOff = true;
		XYZ site = Scenario.Site(t, 14, 6);
		Labor worker = null;
		yield return StrandedWorker(t, new XYZ(site.x - 5, site.y, site.z), (Labor w) => worker = w);
		int before = LocomotionRecovery.LegsReset;
		XYZ goal = new XYZ(site.x + 5, site.y, site.z);
		Vector3 start = worker.transform.position;
		Order.Package.OfLocation(Order.Move.RunToward, Location.Of(goal), Labor.DecisionSource.User).Send(worker);
		yield return t.UntilGame(() => LocomotionRecovery.LegsReset > before, 10f, "the move order's wait to put the legs back", 2f, () => t.Log(Describe(worker)));
		yield return t.UntilGame(() => (worker.transform.position - goal.ToVector3()).sqrMagnitude <= 4f, 30f, "the worker to run to the ordered place", 5f, () => t.Log(Describe(worker)));
		t.True((worker.transform.position - start).sqrMagnitude > 25f, "The worker did not move: " + Describe(worker));
	}

	[GameTest("Tasks", "Reset bricktrons puts the legs of a worker left airborne while it stood back on their feet at once (#156)", TimeoutSeconds = 90f)]
	private static IEnumerator ResetFreesStrandedLegs(TestContext t)
	{
		t.Cleanup(() => LocomotionRecovery.OutOfStepCheckOff = false);
		LocomotionRecovery.OutOfStepCheckOff = true;
		XYZ site = Scenario.Site(t, 8, 6);
		Labor worker = null;
		yield return StrandedWorker(t, site, (Labor w) => worker = w);
		worker.Activity = Activity.Fighting;
		int before = LocomotionRecovery.LegsReset;
		int count = BricktronRecovery.ResetTeam(Scenario.Player);
		t.True(count >= 1, "The reset reached " + count + " bricktrons");
		Locomotion4 legs = LocomotionRecovery.LegsOf(worker);
		t.True(LocomotionRecovery.LegsReset > before, "The reset did not put the legs back: " + Describe(worker));
		t.True(legs.CanMove(), "The legs cannot move after the reset: " + Describe(worker));
		t.True(worker.Activity != Activity.Fighting, "The worker is still marked as fighting after the reset");
	}
}
