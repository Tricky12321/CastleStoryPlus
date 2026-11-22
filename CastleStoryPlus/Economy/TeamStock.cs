using System;
using System.Collections.Generic;
using Brix.Components;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Lifecycle.Pooling;
using CastleStoryPlus.Core;
using UnityEngine;

namespace CastleStoryPlus.Economy;

// What a team's stockpiles and tool racks hold of a resource, counted at most once a second per team and resource
// when a cached count will do. Used by CraftLoopLimit (stock limits) and CraftRoundRobin (ingredients in stock), so it
// is a file of its own: the Essentials edition has CraftRoundRobin without CraftLoopLimit.
internal static class TeamStock
{
	private const float CacheSeconds = 1f;

	private static readonly Dictionary<KeyValuePair<Faction, Type>, KeyValuePair<float, int>> Cache = new Dictionary<KeyValuePair<Faction, Type>, KeyValuePair<float, int>>();

	private static bool _hooked;

	internal static int Count(Faction faction, Ressource resource, bool cached)
	{
		if (!_hooked)
		{
			_hooked = true;
			GameSession.OnLeave(Cache.Clear);
		}
		KeyValuePair<Faction, Type> key = new KeyValuePair<Faction, Type>(faction, resource.GetType());
		if (cached && Cache.TryGetValue(key, out KeyValuePair<float, int> entry) && Time.time - entry.Key < CacheSeconds)
		{
			return entry.Value;
		}
		int count = 0;
		AutoList list = BrixSingleton<AutoList>.Instance;
		if (list != null)
		{
			HashSet<GameObject> seen = new HashSet<GameObject>();
			foreach (Factory.AssetKey storage in new[] { ObjetsDynamiques.Palette, ObjetsDynamiques.Toolrack, ObjetsDynamiques.SingleToolrack })
			{
				HashSet<GameObject> instances = list.GetInstances(storage);
				if (instances == null)
				{
					continue;
				}
				foreach (GameObject go in instances)
				{
					if (go == null || !go.activeInHierarchy || !seen.Add(go) || Faction.GetFaction(go) != faction)
					{
						continue;
					}
					Recepteur recepteur = go.GetComponent<Recepteur>();
					if (recepteur != null)
					{
						count += recepteur.ContentDescription.Value(resource);
					}
				}
			}
		}
		Cache[key] = new KeyValuePair<float, int>(Time.time, count);
		return count;
	}

	// The cached count of a resource (after an order of it started).
	internal static void Forget(Faction faction, Ressource resource)
	{
		Cache.Remove(new KeyValuePair<Faction, Type>(faction, resource.GetType()));
	}
}
