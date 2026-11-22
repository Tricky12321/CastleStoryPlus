using System.Collections.Generic;
using Brix.Game;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Game.AI;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Economy;

// A looping workshop queue with several recipes takes turns instead of letting one starve the rest: when the recipe
// it works on has waited StallSeconds for an ingredient that no stockpile holds, and another recipe in the queue has
// all its ingredients in stock (and is not stopped by its loop limit), the waiting recipe goes to the back of the
// queue and the one that can be made starts. The ingredients already in the workshop are carried back out as when the
// player reorders the queue. Host only; the queue itself is synced by the game.
[Feature(Features.CraftRoundRobin, Features.CraftRoundRobinInfo)]
internal sealed class CraftRoundRobin : MonoBehaviour
{
	private const float CheckSeconds = 2f;

	private const float StallSeconds = 20f;

	// Per workshop: the recipe it waited on and since when.
	private static readonly Dictionary<CraftingLabor, KeyValuePair<Recipe, float>> Waiting = new Dictionary<CraftingLabor, KeyValuePair<Recipe, float>>();

	private readonly List<CraftingLabor> _labors = new List<CraftingLabor>();

	private float _next;

	private static void Enable()
	{
		GameSession.OnLeave(Waiting.Clear);
		Plugin.Root.AddComponent<CraftRoundRobin>();
	}

	private void Update()
	{
		if (!NetworkServer.active || Time.time < _next)
		{
			return;
		}
		_next = Time.time + CheckSeconds;
		Live<CraftingLabor>.Active(_labors);
		foreach (CraftingLabor labor in _labors)
		{
			try
			{
				Check(labor);
			}
			catch (System.Exception ex)
			{
				Plugin.Log.LogWarning("CraftRoundRobin: " + ex.Message);
				Waiting.Remove(labor);
			}
		}
	}

	private static void Check(CraftingLabor labor)
	{
		Recipe current = labor.CurrentRecipe;
		CraftingStation station = labor.GetComponent<CraftingStation>();
		if (current == null || station == null || !labor.LoopRecipe || labor.QueuedRecipes.Count < 2)
		{
			Waiting.Remove(labor);
			return;
		}
		Faction faction = Faction.GetFaction(labor.gameObject);
		if (current.HasIngredientsIn(station.recepteur) || InStock(current.Info, station.recepteur, faction))
		{
			Waiting.Remove(labor);
			return;
		}
		if (!Waiting.TryGetValue(labor, out KeyValuePair<Recipe, float> since) || since.Key != current)
		{
			Waiting[labor] = new KeyValuePair<Recipe, float>(current, Time.time);
			return;
		}
		if (Time.time - since.Value < StallSeconds)
		{
			return;
		}
		List<RecipeInfo> queue = labor.QueuedRecipes;
		int next = -1;
		for (int i = 1; i < queue.Count; i++)
		{
#if !ESSENTIALS
			if (queue[i].id != current.Info.id && InStock(queue[i], station.recepteur, faction) && !CraftLoopLimit.HasEnough(labor, queue[i], faction))
#else
			if (queue[i].id != current.Info.id && InStock(queue[i], station.recepteur, faction))
#endif
			{
				next = i;
				break;
			}
		}
		if (next < 0)
		{
			return;
		}
		Waiting.Remove(labor);
		RecipeInfo stalled = queue[0];
		// The game's own "remove the first order": drops the work on it.
		labor.RemoveRecipeAt(0);
		queue = labor.QueuedRecipes;
		List<RecipeInfo> turned = new List<RecipeInfo>(queue.Count + 1);
		turned.AddRange(queue.GetRange(next - 1, queue.Count - (next - 1)));
		turned.AddRange(queue.GetRange(0, next - 1));
		turned.Add(stalled);
		labor.state.SetRecipes(turned);
		Plugin.Log.LogInfo("CraftRoundRobin: " + labor.name + " waits for ingredients of recipe " + stalled.id + ", recipe " + turned[0].id + " goes first");
	}

	// Whether the workshop and the team's stockpiles hold every ingredient of the recipe.
	private static bool InStock(RecipeInfo recipe, Recepteur workshop, Faction faction)
	{
		Description needs = new Description();
		recipe.AddTo(needs);
		foreach (Adjectif adjectif in needs.DicoAdjectif.Values)
		{
			if (adjectif is Ressource resource && resource.quantifiable != null)
			{
				int have = workshop.ContentDescription.Value(resource) + TeamStock.Count(faction, resource, cached: true);
				if (have < resource.quantifiable.valeur)
				{
					return false;
				}
			}
		}
		return true;
	}
}

[Feature(Features.CraftRoundRobin, Features.CraftRoundRobinInfo)]
[HarmonyPatch(typeof(CraftingLabor), nameof(CraftingLabor.Initialize))]
internal static class CraftRoundRobinTrackPatch
{
	private static void Postfix(CraftingLabor __instance)
	{
		Live<CraftingLabor>.Add(__instance);
	}
}
