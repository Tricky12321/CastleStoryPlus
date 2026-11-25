using System.Collections.Generic;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CastleStoryPlus.DevTools.Building;

// Cheat for testing and for playing through the API: finishes blueprints at once, as the game does when the last
// material arrives (Blueprint.MorphToObject), without workers or materials. Demolition blueprints remove what they
// cover. Only buildable ones are finished, bottom up; a piece resting on one finished in the same frame is only seen
// as supported a frame later, so what is left is tried again every frame for a few seconds.
internal class InstantBuild : MonoBehaviour
{
	private const int MaxPasses = 64;

	private const float RetrySeconds = 5f;

	private static readonly List<Blueprint> Queue = new List<Blueprint>();

	private static float _retryUntil;

	internal static int Queued => Queue.Count;

	internal sealed class Outcome
	{
		public int Built;

		public int Left;
	}

	internal static Outcome Complete(IEnumerable<Blueprint> blueprints)
	{
		List<Blueprint> pending = new List<Blueprint>();
		foreach (Blueprint blueprint in blueprints)
		{
			if (blueprint != null && !blueprint.Released)
			{
				pending.Add(blueprint);
			}
		}
		pending.Sort((a, b) => a.transform.position.y.CompareTo(b.transform.position.y));
		Outcome outcome = new Outcome();
		for (int pass = 0; pass < MaxPasses && pending.Count > 0; pass++)
		{
			int before = outcome.Built;
			for (int i = 0; i < pending.Count; i++)
			{
				Blueprint blueprint = pending[i];
				if (blueprint == null || blueprint.Released)
				{
					pending.RemoveAt(i--);
					continue;
				}
				ValidatorComponent validator = blueprint.GetComponent<ValidatorComponent>();
				if (!blueprint.IsAnti && validator != null && (validator.GetCurrentState() & ValidationResult.Buildable) == 0)
				{
					continue;
				}
				blueprint.MorphToObject();
				outcome.Built++;
				pending.RemoveAt(i--);
			}
			if (outcome.Built == before)
			{
				break;
			}
		}
		outcome.Left = pending.Count;
		if (pending.Count > 0)
		{
			Queue.AddRange(pending);
			_retryUntil = Time.unscaledTime + RetrySeconds;
		}
		return outcome;
	}

	private void Update()
	{
		if (Queue.Count == 0)
		{
			return;
		}
		if (Time.unscaledTime > _retryUntil)
		{
			Queue.Clear();
			return;
		}
		List<Blueprint> batch = new List<Blueprint>(Queue);
		Queue.Clear();
		float until = _retryUntil;
		Outcome outcome = Complete(batch);
		if (outcome.Built > 0)
		{
			_retryUntil = Time.unscaledTime + RetrySeconds;
		}
		else
		{
			_retryUntil = until;
		}
	}

	// The player's blueprints, optionally of one project.
	// The placed blueprints of every other player (not their demolitions), as Of.
	internal static List<Blueprint> OfOthers(Faction faction, Project project)
	{
		List<Blueprint> result = new List<Blueprint>();
		foreach (Blueprint blueprint in Object.FindObjectsOfType<Blueprint>())
		{
			if (blueprint == null || blueprint.Released || blueprint.faction == faction || blueprint.faction == null || blueprint.IsAnti || !blueprint.isPlaced)
			{
				continue;
			}
			if (project != null && blueprint.Project != project)
			{
				continue;
			}
			result.Add(blueprint);
		}
		return result;
	}

	internal static List<Blueprint> Of(Faction faction, Project project)
	{
		List<Blueprint> result = new List<Blueprint>();
		foreach (Blueprint blueprint in Object.FindObjectsOfType<Blueprint>())
		{
			if (blueprint == null || blueprint.Released || blueprint.faction != faction || !blueprint.isPlaced)
			{
				continue;
			}
			if (project != null && blueprint.Project != project)
			{
				continue;
			}
			result.Add(blueprint);
		}
		return result;
	}
}
