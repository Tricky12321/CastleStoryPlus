using System.Collections.Generic;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using CastleStoryPlus.Combat;
using CastleStoryPlus.Core;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.WorkerAI;

// A bricktron whose legs no longer take orders (#156): it stands, every task it gets (its own, a player's move, the
// tasks after "Reset bricktrons") waits for good, and enemies kill it where it stands.
// Every task of the game starts with Labor.GetReadyForNextTask: the legs (Locomotion4) are asked to get ready and the
// task waits, without a time limit, until they can move (InPlace, TurnOnSpot, Walking, Running or Halting). The legs'
// state machine (host only) follows the animation: it leaves the hurt, stunned, thrown and fighting states only when
// the animator enters a standing state and its state behaviour reports it. A blow in a fight (two biftrons hitting a
// builder one after the other) whose stand-up entry is missed (an interrupted transition) leaves the legs in their
// airborne state while the animation stands in place: the bricktron looks idle, cannot move, and every task, reset
// or order after that waits behind it. The task watchdogs (StuckReset, BricktronRecovery) only cancelled the task;
// the next one waited again.
// Now, for the players' bricktrons:
// - legs out of step with the animation (they cannot move while the animation stands in place, not in a transition)
//   at RecoveryRules.OutOfStepChecks checks a second apart are told the animation stands, as the missed entry would
//   have done;
// - a task that has waited for the legs longer than RecoveryRules.LegsWaitLimit (2 s for a player's order, 8 s for
//   any other task) puts them back on their feet: the animation flags that hold a pose (stunned, flailing, hurt,
//   combat stance, sitting, trapped...) are cleared, the animator is put in its standing state and the legs are
//   told so. Not for a dead bricktron, one held in a hand, one on a ladder, climbing or jumping down, or one still
//   tumbling through the air;
// - "Reset bricktrons" puts the legs of every worker it resets back on their feet at once, also those of a worker
//   left marked as fighting.
// Host only (the legs' state machine runs on the host; the animation flags are sent to the clients). Enemies keep the
// game's behaviour.
[Feature(Features.StuckReset, Features.StuckResetInfo)]
internal sealed class LocomotionRecovery : MonoBehaviour
{
	private const float CheckSeconds = 1f;

	// The checks in a row that found each bricktron's legs out of step.
	private static readonly Dictionary<Labor, int> OutOfStep = new Dictionary<Labor, int>();

	// How often legs were put back (DevTools tests and the log).
	internal static int LegsReset;

	internal static int LegsResynced;

	// DevTools tests: the out-of-step check is left out, so a task's wait is what puts the legs back. Not saved.
	internal static bool OutOfStepCheckOff;

	private readonly List<Labor> _labors = new List<Labor>();

	private readonly List<Labor> _gone = new List<Labor>();

	private float _next;

	// One task's wait for the legs.
	private sealed class Wait
	{
		public float Since;

		public bool PlayerOrder;

		public void Begin(Labor labor)
		{
			Since = Time.time;
			PlayerOrder = PlayerOrdersFirst.IsPlayerTask(labor.CurrentTask);
			Locomotion4 legs = LegsOf(labor);
			if (legs != null)
			{
				legs.GetReadyToMove();
			}
		}

		public bool Ready(Labor labor)
		{
			Locomotion4 legs = LegsOf(labor);
			if (legs == null || legs.CanMove())
			{
				return true;
			}
			float waited = Time.time - Since;
			if (waited < RecoveryRules.LegsWaitLimit(PlayerOrder))
			{
				return false;
			}
			// The next try (if this one is refused or does not take) after another full wait.
			Since = Time.time;
			string task = (labor.CurrentTask != null) ? labor.CurrentTask.Description : "a task";
			try
			{
				return ResetLegs(labor, "'" + task + "' waited " + Mathf.RoundToInt(waited) + " s for its legs") && legs.CanMove();
			}
			catch (System.Exception ex)
			{
				// Never into the task's behaviour tree: the task waits on, as the game's did.
				Plugin.Log.LogWarning("LocomotionRecovery: " + labor.name + ": " + ex.Message);
				return false;
			}
		}
	}

	private static void Enable()
	{
		GameSession.OnLeave(() =>
		{
			OutOfStep.Clear();
		});
		Plugin.Root.AddComponent<LocomotionRecovery>();
	}

	internal static Locomotion4 LegsOf(Labor labor)
	{
		if (labor == null)
		{
			return null;
		}
		Locomotion4 legs = (labor.navigation != null) ? labor.navigation.Locomotion : null;
		return (legs != null) ? legs : labor.GetComponent<Locomotion4>();
	}

	// The players' bricktrons on the host (the legs' state machine runs there only).
	internal static bool Watched(Labor labor)
	{
		return NetworkServer.active && labor != null && labor.faction != null && !labor.faction.isAI;
	}

	// Labor.GetReadyForNextTask for a player's bricktron: the game's wait, with a time limit.
	internal static Node ReadyForNextTask(Labor labor)
	{
		Wait wait = new Wait();
		return (StaticNode)Node.Sequence.Do((Labor self) =>
		{
			wait.Begin(self);
		}, labor).Do(Node.WaitFor.Condition((Labor self) => wait.Ready(self), labor));
	}

	private static bool MayReset(Labor labor, Locomotion4 legs)
	{
		Locomotion4.State state = legs.state;
		bool climbing = legs.IsUsingLadder || state == Locomotion4.State.Ladder || state == Locomotion4.State.ClimbUp || state == Locomotion4.State.StepDown
			|| state == Locomotion4.State.JumpingOff || state == Locomotion4.State.JumpingOffJumping || state == Locomotion4.State.JumpingOffFalling || state == Locomotion4.State.JumpingOffLanding;
		// Tumbling: the body is thrown about by the physics (its flailing body is not kinematic only while it flails). The
		// speed the flailing last saw is not used: it stays from the last flail, also once the legs wait to stand up.
		bool flailing = false;
		float speed = 0f;
		Rigidbody body = legs.FlailingColliderRigidbody;
		if (legs.IsAirborne && body != null && !body.isKinematic)
		{
			flailing = true;
			speed = body.velocity.magnitude;
		}
		return RecoveryRules.LegsMayBeReset(legs.IsDead, legs.IsGrabbed, labor.IsPhasedOut, climbing, flailing, speed);
	}

	// Host: puts the legs of a bricktron that cannot move back on their feet. False when they may not be (see
	// RecoveryRules.LegsMayBeReset) or there is nothing to do.
	internal static bool ResetLegs(Labor labor, string why)
	{
		if (!NetworkServer.active || labor.IsNullOrReleased())
		{
			return false;
		}
		Locomotion4 legs = LegsOf(labor);
		if (legs == null || legs.CanMove() || !MayReset(labor, legs))
		{
			return false;
		}
		GridLocomotionControllerInterface controller = legs.controller;
		if (controller == null || controller.InPlace == null)
		{
			return false;
		}
		Locomotion4.State before = legs.state;
		// The flags that hold the animation in a pose; each is set again by whatever really needs it.
		BoolParameter[] poses =
		{
			controller.IsFlailing, controller.IsStabilizing, controller.IsStunned, controller.IsHardHurt, controller.IsSoftHurt,
			controller.IsTakingCombatStance, controller.IsAttacking, controller.IsTakingArcheryStance, controller.IsAiming,
			controller.IsShooting, controller.IsShieldBlocking, controller.IsCombatStepping, controller.IsRunStriking, controller.IsSitting,
			controller.IsTaking, controller.IsUnburrowing, controller.IsGettingTrapped, controller.IsLeavingTrap, controller.IsBeingGrabbed,
			controller.IsJumpingOff
		};
		foreach (BoolParameter pose in poses)
		{
			if (pose != null)
			{
				pose.Set(b: false);
			}
		}
		legs.Common.Trapped.TimeToLeaveTrap = 0f;
		AnimatorStateInfo info = default;
		if (legs.animator != null)
		{
			legs.animator.Play(controller.InPlace.StateHash, 0, 0f);
			info = legs.animator.GetCurrentAnimatorStateInfo(0);
		}
		// The state behaviour's report of the standing animation: the legs leave an airborne state for the ground, and
		// on the ground take up standing in place.
		controller.InPlace.EnterState(info);
		if (!legs.CanMove())
		{
			controller.InPlace.EnterState(info);
		}
		LegsReset++;
		OutOfStep.Remove(labor);
		Plugin.Log.LogInfo("LocomotionRecovery: " + labor.name + " (" + why + ", legs " + before + (legs.IsAirborne ? ", airborne" : string.Empty) + "): put back on its feet" + (legs.CanMove() ? string.Empty : ", still cannot move"));
		return true;
	}

	// ---- legs out of step with the animation

	private void Update()
	{
		if (!NetworkServer.active || OutOfStepCheckOff)
		{
			return;
		}
		if (Time.time < _next)
		{
			return;
		}
		_next = Time.time + CheckSeconds;
		Live<Labor>.Active(_labors);
		foreach (Labor labor in _labors)
		{
			try
			{
				Check(labor);
			}
			catch (System.Exception ex)
			{
				Plugin.Log.LogWarning("LocomotionRecovery: " + ex.Message);
				OutOfStep.Remove(labor);
			}
		}
		Prune();
	}

	private static void Check(Labor labor)
	{
		if (!Watched(labor) || labor.IsPhasedOut)
		{
			OutOfStep.Remove(labor);
			return;
		}
		Locomotion4 legs = LegsOf(labor);
		GridLocomotionControllerInterface controller = (legs != null) ? legs.controller : null;
		if (controller == null || controller.InPlace == null || legs.animator == null || legs.IsDead || legs.IsGrabbed)
		{
			OutOfStep.Remove(labor);
			return;
		}
		bool stands = controller.InPlace.IsActive();
		if (!RecoveryRules.LegsOutOfStep(legs.CanMove(), stands, legs.animator.IsInTransition(0)))
		{
			OutOfStep.Remove(labor);
			return;
		}
		OutOfStep.TryGetValue(labor, out int checks);
		checks++;
		if (checks < RecoveryRules.OutOfStepChecks)
		{
			OutOfStep[labor] = checks;
			return;
		}
		OutOfStep.Remove(labor);
		Locomotion4.State before = legs.state;
		bool airborne = legs.IsAirborne;
		controller.InPlace.EnterState(legs.animator.GetCurrentAnimatorStateInfo(0));
		LegsResynced++;
		Plugin.Log.LogInfo("LocomotionRecovery: " + labor.name + "'s legs waited (" + before + (airborne ? ", airborne" : string.Empty) + ") while it stood in place: back in step" + (legs.CanMove() ? string.Empty : ", still cannot move"));
	}

	private void Prune()
	{
		if (OutOfStep.Count == 0)
		{
			return;
		}
		_gone.Clear();
		foreach (Labor labor in OutOfStep.Keys)
		{
			if (labor == null || !labor.gameObject.activeInHierarchy)
			{
				_gone.Add(labor);
			}
		}
		foreach (Labor labor in _gone)
		{
			OutOfStep.Remove(labor);
		}
		_gone.Clear();
	}
}

// Every task waits for the legs to be ready: for the players' bricktrons with a time limit, after which the legs are
// put back on their feet (#156).
[Feature(Features.StuckReset, Features.StuckResetInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.GetReadyForNextTask))]
internal static class LocomotionRecoveryReadyPatch
{
	private static void Postfix(Labor __instance, ref Node __result)
	{
		if (!LocomotionRecovery.Watched(__instance))
		{
			return;
		}
		__result = LocomotionRecovery.ReadyForNextTask(__instance);
	}
}
