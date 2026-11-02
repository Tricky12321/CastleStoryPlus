using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using HarmonyLib;
using Motus.Behavior;
using UnityEngine;

namespace CastleStoryPlus.Performance;

// An idle worker looks for work every few AI ticks (Labor.CheckProjectForWork): it lists the tasks it may work for,
// sorts them by priority and asks them one by one. One such search is cheap on its own, but the idle workers fall
// into step (a task finishing or a new one placed wakes them all at once), and 20 or 30 searches in one tick made a
// frame of 15-20 ms where the others take 12. At most [Performance] WorkSearchesPerTick (4) workers now search in one
// tick; the others are put off to their next round (two ticks later) and are served first then, in the order they
// were put off, so no worker waits for ever when there are more idle workers than slots. An idle worker finds its
// next task a few ticks later at most; a worker with a task is not touched. 0: as the game, no limit.
// CSP_WORKSEARCH_LOG=1 logs every 5 s how many searches ran, how many were put off and the most in one tick.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
[HarmonyPatch(typeof(Labor), "CheckProjectForWork")]
internal static class WorkSearchBudget
{
	private const int DefaultPerTick = 4;

	private static ConfigEntry<int> _perTick;

	private static float _tick = -1f;

	// Searches run this tick, and the put-off workers at the queue's head whose turn it is this tick and who have not
	// yet come.
	private static int _served;

	private static int _reserved;

	// Workers put off, oldest first.
	private static readonly List<Labor> Queue = new List<Labor>();

	private static readonly bool Log = System.Environment.GetEnvironmentVariable("CSP_WORKSEARCH_LOG") == "1";

	private static int _searches;

	private static int _putOff;

	private static int _mostInTick;

	private static float _nextLog;

	private static void Enable()
	{
		_perTick = Plugin.Cfg.Bind("Performance", "WorkSearchesPerTick", DefaultPerTick, new ConfigDescription("Most idle workers that look for a task in one AI tick; the others are put off to their next round and served first then. Many workers searching in the same tick made single slow frames. 0: no limit, as the game.", new AcceptableValueRange<int>(0, 64)));
		GameSession.OnLeave(() =>
		{
			Queue.Clear();
			_tick = -1f;
		});
	}

	private static void Disable()
	{
		Queue.Clear();
	}

	private static bool Prefix(Labor __instance, ref Node __result)
	{
		int budget = _perTick != null ? _perTick.Value : DefaultPerTick;
		if (budget <= 0)
		{
			return true;
		}
		float tick = Time.fixedTime;
		if (tick != _tick)
		{
			_tick = tick;
			if (_served > _mostInTick)
			{
				_mostInTick = _served;
			}
			_served = 0;
			Prune();
			_reserved = Mathf.Min(budget, Queue.Count);
		}
		int at = Queue.IndexOf(__instance);
		if (at >= 0 && at < _reserved)
		{
			// Its turn: it was put off before the others still waiting.
			Queue.RemoveAt(at);
			_reserved--;
			return Serve();
		}
		if (at < 0 && _served + _reserved < budget)
		{
			return Serve();
		}
		if (at < 0)
		{
			Queue.Add(__instance);
		}
		_putOff++;
		LogNow();
		__result = Node.Empty;
		return false;
	}

	private static bool Serve()
	{
		_served++;
		_searches++;
		LogNow();
		return true;
	}

	// Workers gone (released to the pool, destroyed) leave the queue, so they take no turn.
	private static void Prune()
	{
		for (int i = Queue.Count - 1; i >= 0; i--)
		{
			Labor labor = Queue[i];
			if (labor == null || !labor.isActiveAndEnabled)
			{
				Queue.RemoveAt(i);
			}
		}
	}

	private static void LogNow()
	{
		if (!Log || Time.time < _nextLog)
		{
			return;
		}
		_nextLog = Time.time + 5f;
		Plugin.Log.LogInfo("WorkSearchBudget: " + _searches + " searches, " + _putOff + " put off, most in one tick " + _mostInTick + ", " + Queue.Count + " waiting");
		_searches = 0;
		_putOff = 0;
		_mostInTick = 0;
	}
}
