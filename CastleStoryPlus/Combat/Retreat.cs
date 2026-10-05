using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Components;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.Damage;
using Brix.Game.AI.InstructionType;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Input;
using Brix.Lifecycle.Pooling;
using Brix.UI.Icons;
using CastleStoryPlus.Core;
using CastleStoryPlus.Giant;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Combat;

// Melee fighters (knights, halberdiers, builders without a ranged weapon) that drop below [Retreat] HealthThreshold
// while in combat run to the nearest own healing ward, or else the home crystal, instead of fighting to the death.
// On the way they ignore enemies; once there they wait and heal, and only fight back against enemies in melee
// range. They return to normal at [Retreat] ResumeThreshold health, or after a timeout. A new order from the
// player overrides the retreat. Host only; not in the world editor.
[Feature(Features.Retreat, Features.RetreatInfo)]
internal class Retreat : MonoBehaviour
{
	private enum Phase
	{
		Fleeing,
		Recovering
	}

	private class State
	{
		public Phase Phase;

		public Vector3 Destination;

		public float Since;
	}

	private const float CheckSeconds = 0.5f;

	private const float ArriveDistance = 5f;

	private const float MaxWardDistance = 60f;

	// Fleeing ends after this even if the destination was not reached (blocked path, order replaced).
	private const float FleeSeconds = 45f;

	// Without (enough) healing the unit goes back to normal after this.
	private const float RecoverSeconds = 90f;

	internal static ConfigEntry<float> HealthThreshold;

	internal static ConfigEntry<float> ResumeThreshold;

	private static readonly Dictionary<Labor, State> Retreating = new Dictionary<Labor, State>();

	private static readonly LaborInstruction<Vector3> RetreatOrder = new LaborInstruction<Vector3>("Retreat", IconKeys.DropThere, (Labor labor, Vector3 destination) => RetreatNode(labor, destination));

	private float _nextCheck;

	private static void Enable()
	{
		HealthThreshold = Plugin.Cfg.Bind("Retreat", "HealthThreshold", 0.3f, "Melee fighters retreat when their health drops below this fraction (0.3 = 30 %).");
		ResumeThreshold = Plugin.Cfg.Bind("Retreat", "ResumeThreshold", 0.8f, "Retreating fighters return to normal at this fraction of their health.");
		GameSession.OnLeave(Retreating.Clear);
		Plugin.Root.AddComponent<Retreat>();
	}

	// Server: called after a bricktron took damage.
	public static void OnDamaged(BricktronDamageReceiver receiver)
	{
		if (!NetworkServer.active || InputModeController.DefaultMode == InputMode.worldEditor || receiver == null)
		{
			return;
		}
		Labor labor = receiver.Labor;
		if (labor == null || Retreating.ContainsKey(labor) || !IsMeleeFighter(labor) || !labor.IsInDanger)
		{
			return;
		}
		if (receiver.CurrentHP <= 0f || receiver.MaxHP <= 0f || receiver.CurrentHP / receiver.MaxHP >= HealthThreshold.Value)
		{
			return;
		}
		if (!FindSafety(labor, out Vector3 destination))
		{
			return;
		}
		State state = new State { Destination = destination, Since = Time.time };
		Retreating[labor] = state;
		if ((labor.transform.position - destination).sqrMagnitude <= ArriveDistance * ArriveDistance)
		{
			state.Phase = Phase.Recovering;
		}
		else
		{
			state.Phase = Phase.Fleeing;
		}
		RetreatOrder.Send(labor, destination, Labor.DecisionSource.User);
	}

	// While fleeing a unit ignores enemies; while recovering it only fights enemies in melee range.
	public static bool SuppressFight(Labor labor)
	{
		if (!Retreating.TryGetValue(labor, out State state))
		{
			return false;
		}
		if (state.Phase == Phase.Fleeing)
		{
			return true;
		}
		LaborInstinct instinct = labor.instinct;
		return instinct == null || instinct.validEnemy == null || instinct.enemyIsClose != LaborInstinct.EnemyProximity.Close;
	}

	public static bool IsRetreating(Labor labor)
	{
		return labor != null && Retreating.ContainsKey(labor);
	}

	public static void Forget(Labor labor)
	{
		Retreating.Remove(labor);
	}

	private static bool IsMeleeFighter(Labor labor)
	{
		Profession profession = labor.Profession;
		if (profession == null || profession.IsCorruptron() || !profession.HasMeleeAttack() || profession.ProjectileAttackAvailable())
		{
			return false;
		}
		Occupation.Type type = profession.OccupationType;
		return type != Occupation.Type.Biftron && type != Occupation.Type.Minitron && type != Occupation.Type.Corruptron;
	}

	// Nearest own healing ward within reach, else the home crystal. Factions without a home crystal (AI) never retreat.
	private static bool FindSafety(Labor labor, out Vector3 destination)
	{
		destination = Vector3.zero;
		Faction faction = labor.faction;
		FireflyNest nest = (faction != null) ? GiantBricktron.HomeNest(faction) : null;
		if (nest == null)
		{
			return false;
		}
		Vector3 position = labor.transform.position;
		destination = nest.transform.position;
		float best = MaxWardDistance * MaxWardDistance;
		HashSet<GameObject> wards = BrixSingleton<AutoList>.Instance?.GetInstances(ObjetsDynamiques.HealingWard);
		if (wards == null)
		{
			return true;
		}
		foreach (GameObject ward in wards)
		{
			if (ward == null || !ward.activeInHierarchy || !faction.IsSame(ward))
			{
				continue;
			}
			float distance = (ward.transform.position - position).sqrMagnitude;
			if (distance < best)
			{
				best = distance;
				destination = ward.transform.position;
			}
		}
		return true;
	}

	// Run to safety without stopping for enemies, then stay there until healed (the retreat ends).
	private static Node RetreatNode(Labor labor, Vector3 destination)
	{
		return (StaticNode)Node.Sequence.Do(labor.RunToward(Location.Of(destination), precise: false, aggressive: false)).Do((Labor self) => Arrived(self), labor).Do(Node.WaitFor.Condition((Labor self) => !IsRetreating(self), labor));
	}

	private static void Arrived(Labor labor)
	{
		if (Retreating.TryGetValue(labor, out State state) && state.Phase == Phase.Fleeing)
		{
			state.Phase = Phase.Recovering;
			state.Since = Time.time;
		}
	}

	private void Update()
	{
		if (Retreating.Count == 0 || Time.unscaledTime < _nextCheck)
		{
			return;
		}
		_nextCheck = Time.unscaledTime + CheckSeconds;
		List<Labor> done = new List<Labor>();
		foreach (KeyValuePair<Labor, State> entry in Retreating)
		{
			Labor labor = entry.Key;
			State state = entry.Value;
			BricktronDamageReceiver receiver = (labor != null) ? labor.GetComponent<BricktronDamageReceiver>() : null;
			if (receiver == null || !labor.gameObject.activeInHierarchy || receiver.CurrentHP <= 0f)
			{
				done.Add(labor);
				continue;
			}
			if (receiver.MaxHP > 0f && receiver.CurrentHP / receiver.MaxHP >= ResumeThreshold.Value)
			{
				done.Add(labor);
				continue;
			}
			float elapsed = Time.time - state.Since;
			if (state.Phase == Phase.Fleeing)
			{
				bool arrived = (labor.transform.position - state.Destination).sqrMagnitude <= ArriveDistance * ArriveDistance;
				if (arrived || elapsed > FleeSeconds)
				{
					state.Phase = Phase.Recovering;
					state.Since = Time.time;
				}
			}
			else if (elapsed > RecoverSeconds)
			{
				done.Add(labor);
			}
		}
		foreach (Labor labor in done)
		{
			Retreating.Remove(labor);
		}
	}
}

[Feature(Features.Retreat, Features.RetreatInfo)]
[HarmonyPatch(typeof(BricktronDamageReceiver), nameof(BricktronDamageReceiver.ApplyDamage), new[] { typeof(float), typeof(DamageType), typeof(DamageReaction), typeof(Vector3), typeof(Vector3), typeof(float), typeof(GameObject) })]
internal static class RetreatDamagePatch
{
	private static void Postfix(BricktronDamageReceiver __instance)
	{
		Retreat.OnDamaged(__instance);
	}
}

// The instinct ("an enemy is near, fight it") is skipped while a unit retreats.
[Feature(Features.Retreat, Features.RetreatInfo)]
[HarmonyPatch(typeof(Labor), "HasToFight")]
internal static class RetreatInstinctPatch
{
	private static bool Prefix(Labor __instance, ref bool __result)
	{
		if (Retreat.SuppressFight(__instance))
		{
			__result = false;
			return false;
		}
		return true;
	}
}

// A unit taken from the pool starts fresh.
[Feature(Features.Retreat, Features.RetreatInfo)]
[HarmonyPatch(typeof(Labor), nameof(Labor.OnRequested))]
internal static class RetreatResetPatch
{
	private static void Prefix(Labor __instance)
	{
		Retreat.Forget(__instance);
	}
}
