using System;
using System.Collections.Generic;
using Brix.External.Signals;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.Components;
using CastleStoryPlus.Core;
using UnityEngine;
using Tuple2 = Smooth.Algebraics.Tuple<UnityEngine.GameObject, UnityEngine.GameObject>;

namespace CastleStoryPlus.Memory;

// Every half minute, drops entries the game itself never removes during a game:
// - "A cannot reach B" exclusions, added for every object around a failed path and only reset after the worker
//   succeeds at something; entries for destroyed or pooled-away objects are useless and stay forever.
// - Reservation connections, one per pick-up or harvest, kept in a list until the game ends even after firing.
[Feature(Features.GameLeakFixes, Features.GameLeakFixesInfo)]
internal class GameLeakSweeper : MonoBehaviour
{
	private const float SweepSeconds = 30f;

	private float _nextSweep;

	private static void Enable()
	{
		Plugin.Root.AddComponent<GameLeakSweeper>();
	}

	private void Update()
	{
		if (Time.unscaledTime < _nextSweep)
		{
			return;
		}
		_nextSweep = Time.unscaledTime + SweepSeconds;
		Knowledge knowledge = Knowledge.Instance;
		if (knowledge == null)
		{
			return;
		}
		try
		{
			SweepExclusions(knowledge.knowledgeExcluder);
			SweepReservations(knowledge.knowledgeReserver);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("GameLeakSweeper: " + ex.Message);
		}
	}

	private static void SweepExclusions(KnowledgeExcluder excluder)
	{
		if (excluder == null || excluder.Exclusions2.Count == 0)
		{
			return;
		}
		List<Tuple2> dead = new List<Tuple2>();
		foreach (Tuple2 pair in excluder.Exclusions2.Keys)
		{
			if (pair._1.IsNullOrReleased() || pair._2.IsNullOrReleased())
			{
				dead.Add(pair);
			}
		}
		foreach (Tuple2 pair in dead)
		{
			excluder.Exclusions2.Remove(pair);
		}
	}

	private static void SweepReservations(KnowledgeReserver reserver)
	{
		if (reserver == null)
		{
			return;
		}
		reserver.connections.RemoveAll((SignalConnection connection) => !connection.IsConnected);
		reserver.Reservations.RemoveWhere((GameObject go) => go.IsNullOrReleased());
	}
}
