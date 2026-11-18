using System.Collections.Generic;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;

namespace CastleStoryPlus.WorkerAI;

// Idle workers ask every project for work each round of LookForWork, and the mod made those rounds five times as
// frequent (every 2 ticks instead of 10) so a worker picks up its next task at once. A worker with nothing to do kept
// asking at that pace for as long as it stood idle: a dozen idle workers cost close to a millisecond a frame finding
// nothing. A worker that has looked in vain for a while now asks less often: every round for the first ShortSeconds,
// every 3rd round until LongSeconds, then every 5th (about the game's own pace, some 0.4 s). The instinct (fights) is
// checked every round as before, and a worker coming off a task starts at full pace again.
[Feature(Features.WorkerAI, Features.WorkerAIInfo)]
[HarmonyPatch(typeof(Labor), "CheckProjectForWork")]
[HarmonyPriority(Priority.High)]
internal static class IdleSearchBackoff
{
	private const float ShortSeconds = 3f;

	private const float LongSeconds = 15f;

	// A gap this long between two rounds means the worker was busy in between.
	private const float BusyGapSeconds = 1f;

	private sealed class Idle
	{
		internal float Since;

		internal float LastRound;

		internal int Rounds;
	}

	private static readonly Dictionary<Labor, Idle> Workers = new Dictionary<Labor, Idle>();

	private static bool Prefix(Labor __instance, ref Node __result)
	{
		// The job board skips the asks that would come to nothing; a blind backoff on top would only delay work.
		if (JobBoard.IsOn)
		{
			return true;
		}
		float now = Time.time;
		if (!Workers.TryGetValue(__instance, out Idle idle))
		{
			idle = new Idle { Since = now };
			Workers[__instance] = idle;
		}
		else if (now - idle.LastRound > BusyGapSeconds)
		{
			idle.Since = now;
			idle.Rounds = 0;
		}
		idle.LastRound = now;
		idle.Rounds++;
		float idleFor = now - idle.Since;
		int every = (idleFor < ShortSeconds) ? 1 : ((idleFor < LongSeconds) ? 3 : 5);
		if (idle.Rounds % every == 0)
		{
			return true;
		}
		__result = Node.Empty;
		return false;
	}

	internal static void Forget(Labor labor)
	{
		Workers.Remove(labor);
	}
}

[Feature(Features.WorkerAI, Features.WorkerAIInfo)]
[HarmonyPatch(typeof(Labor), "OnDestroy")]
internal static class IdleSearchBackoffPrune
{
	private static void Postfix(Labor __instance)
	{
		IdleSearchBackoff.Forget(__instance);
	}
}
