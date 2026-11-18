using System.Collections.Generic;
using System.Reflection;
using Brix.Game.AI;
using Brix.Game.AI.Damage;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Treasure;

// Guardians are peaceful until attacked: their instinct finds no enemy to fight while they are calm, so bricktrons
// can walk past them. A hit from a player's faction provokes the beast that took it and every beast within PackRadius
// of it (the whole guard pack); provoked beasts fight the players' units until ProvokedSeconds after the last hit on
// one of them. The beasts and the AI's own units (corruptrons and the rest of the invasion) leave each other alone:
// neither picks the other as an enemy, beasts never pick another beast, and the AI's hits do not provoke. Server
// side, where the instinct and the damage run; nothing is saved (a loaded world starts calm).
internal static class BeastTemper
{
	private const float ProvokedSeconds = 40f;

	private const float PackRadius = 25f;

	private static readonly Dictionary<Labor, float> ProvokedUntil = new Dictionary<Labor, float>();

	private static readonly List<Labor> Units = new List<Labor>();

	private static readonly List<Labor> Gone = new List<Labor>();

	internal static bool IsCalm(Labor labor)
	{
		return Beasts.KindOf(labor) != null && !(ProvokedUntil.TryGetValue(labor, out float until) && Time.time < until);
	}

	internal static void Provoke(Labor victim, GameObject attacker)
	{
		if (victim == null || attacker == null || Beasts.KindOf(victim) == null)
		{
			return;
		}
		Labor by = attacker.GetComponent<Labor>();
		if (by != null && (by.faction == victim.faction || Beasts.KindOf(by) != null || !IsPlayers(by)))
		{
			return;
		}
		bool wasCalm = IsCalm(victim);
		float until = Time.time + ProvokedSeconds;
		Live<Labor>.Active(Units);
		Vector3 at = victim.transform.position;
		foreach (Labor labor in Units)
		{
			if (labor != null && Beasts.KindOf(labor) != null && (labor.transform.position - at).sqrMagnitude <= PackRadius * PackRadius)
			{
				ProvokedUntil[labor] = until;
				if (wasCalm)
				{
					// Host only (the temper is not synced): other players hear the roar when it goes into combat.
					BeastBody body = labor.GetComponent<BeastBody>();
					if (body != null && body.Voice != null)
					{
						body.Voice.Provoked();
					}
				}
			}
		}
		ProvokedUntil[victim] = until;
		if (wasCalm)
		{
			Plugin.Log.LogInfo("Treasure: " + victim.gameObject.name + " was attacked by " + attacker.name + "; the pack fights back");
		}
		Prune();
	}

	// A unit of a faction a player plays (not the AI's corruptrons, nor the neutral faction).
	internal static bool IsPlayers(Labor labor)
	{
		return labor != null && labor.faction != null && labor.faction.HasOwner();
	}

	// Whether the seeker may fight the enemy its instinct found: beasts fight only players' units (and only when
	// provoked), and the AI's units do not fight beasts.
	internal static bool MayFight(Labor seeker, GameObject enemy)
	{
		Labor target = (enemy != null) ? enemy.GetComponent<Labor>() : null;
		bool seekerBeast = Beasts.KindOf(seeker) != null;
		bool targetBeast = target != null && Beasts.KindOf(target) != null;
		if (seekerBeast)
		{
			return !IsCalm(seeker) && !targetBeast && (target == null || IsPlayers(target));
		}
		if (targetBeast)
		{
			return IsPlayers(seeker);
		}
		return true;
	}

	private static void Prune()
	{
		Gone.Clear();
		foreach (KeyValuePair<Labor, float> entry in ProvokedUntil)
		{
			if (entry.Key == null || Time.time > entry.Value + 5f)
			{
				Gone.Add(entry.Key);
			}
		}
		foreach (Labor labor in Gone)
		{
			ProvokedUntil.Remove(labor);
		}
	}

	internal static void Clear()
	{
		ProvokedUntil.Clear();
	}
}

// A calm beast's instinct sees no enemy worth fighting; a provoked one only the players' units; the AI's units do
// not see beasts as enemies.
[Feature(Features.Treasure, Features.TreasureInfo)]
[HarmonyPatch]
internal static class BeastCalmPatch
{
	private static MethodBase TargetMethod()
	{
		foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(LaborInstinct)))
		{
			ParameterInfo[] parameters = method.GetParameters();
			if (method.Name == "FightableEnemy" && parameters.Length == 3 && parameters[0].IsOut)
			{
				return method;
			}
		}
		return null;
	}

	private static void Postfix(LaborInstinct __instance, ref GameObject validEnemy)
	{
		if (validEnemy != null && NetworkServer.active && !BeastTemper.MayFight(__instance.labor, validEnemy))
		{
			validEnemy = null;
		}
	}
}

// A hit from a player's unit provokes the beast and its pack.
[Feature(Features.Treasure, Features.TreasureInfo)]
[HarmonyPatch(typeof(BricktronDamageReceiver), nameof(BricktronDamageReceiver.ApplyDamage), new[] { typeof(float), typeof(DamageType), typeof(DamageReaction), typeof(Vector3), typeof(Vector3), typeof(float), typeof(GameObject) })]
internal static class BeastProvokePatch
{
	private static void Prefix(BricktronDamageReceiver __instance, float value, GameObject attacker)
	{
		if (value > 0f && NetworkServer.active)
		{
			BeastTemper.Provoke(__instance.Labor, attacker);
		}
	}
}
