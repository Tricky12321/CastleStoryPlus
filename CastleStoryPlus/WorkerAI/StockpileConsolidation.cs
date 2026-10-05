using System.Collections.Generic;
using Brix.Components;
using Brix.Game.AI;
using Brix.External.Factories;
using Brix.Game.Components;
using Brix.Game.AI.InstructionType;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.AI.Nodes;
using Brix.Game.Semantique;
using Brix.Lifecycle.Pooling;
using Brix.UI.Icons;
using Brix.Utils;
using CastleStoryPlus.Core;
using Motus.Behavior;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.WorkerAI;

// Idle chore: move resources out of the least-filled stockpile into the stockpile of the same resource
// that has the least free room left, so storage stays compact. Only offered by IdleProject, which workers
// reach when no real project has work for them.
internal static class StockpileConsolidation
{
	private class Move
	{
		public Recepteur Source;

		public Recepteur Target;

		public int Amount;

		public float ClaimedUntil;
	}

	// A claim expires on its own in case a task is dropped without running its Finally.
	private const float ClaimSeconds = 60f;

	private static readonly Dictionary<Labor, Move> _moves = new Dictionary<Labor, Move>();

	static StockpileConsolidation()
	{
		GameSession.OnLeave(_moves.Clear);
	}

	public static readonly LaborInstruction<GameObject> Consolidate = new LaborInstruction<GameObject>("ConsolidateStockpiles", IconKeys.PickUp, (Labor labor, GameObject item) => MoveNode(labor, item));

	// Finds a move for this worker and claims both stockpiles. Returns the item to pick up, or null.
	public static GameObject TryPlan(Labor labor)
	{
		if (!NetworkServer.active || labor == null || !labor.recepteur.IsEmpty())
		{
			return null;
		}
		ExpireClaims();
		Dictionary<Ressource, List<Recepteur>> byResource = StockpilesByResource(labor);
		foreach (KeyValuePair<Ressource, List<Recepteur>> pair in byResource)
		{
			List<Recepteur> piles = pair.Value;
			if (piles.Count < 2)
			{
				continue;
			}
			Ressource resource = pair.Key;
			Recepteur source = null;
			int sourceCount = int.MaxValue;
			foreach (Recepteur pile in piles)
			{
				int count = pile.ContentDescription.Value(resource);
				if (count > 0 && count < sourceCount && !IsClaimed(pile) && Recepteur.CharactersCouldGrabFrom(pile.gameObject))
				{
					source = pile;
					sourceCount = count;
				}
			}
			if (source == null)
			{
				continue;
			}
			// Target: the pile with the least free room that can still take this resource,
			// and holds at least as much as the source (never spread a fuller pile into an emptier one).
			Recepteur target = null;
			int targetRoom = int.MaxValue;
			foreach (Recepteur pile in piles)
			{
				if (pile == source || IsClaimed(pile))
				{
					continue;
				}
				int room = pile.CurrentCapacity.Value(resource);
				int count = pile.ContentDescription.Value(resource);
				if (room > 0 && count >= sourceCount && room < targetRoom)
				{
					target = pile;
					targetRoom = room;
				}
			}
			if (target == null)
			{
				continue;
			}
			if (!source.FirstObjectOf(resource, out GameObject item))
			{
				continue;
			}
			Factory.AssetKey key = item.GetComponent<GameComponent>().AssetKey;
			int amount = Mathf.Min(sourceCount, targetRoom, labor.recepteur.CanFitUpTo(key));
			if (amount <= 0)
			{
				continue;
			}
			if (!Knowledge.Instance.CanReach(labor, source.GetComponent<GameComponent>()) || !Knowledge.Instance.CanReach(labor, target.GetComponent<GameComponent>()))
			{
				continue;
			}
			_moves[labor] = new Move
			{
				Source = source,
				Target = target,
				Amount = amount,
				ClaimedUntil = Time.time + ClaimSeconds
			};
			return item;
		}
		return null;
	}

	public static void Release(Labor labor)
	{
		if (labor != null)
		{
			_moves.Remove(labor);
		}
	}

	// The worker is carrying a load from one stockpile to the other.
	public static bool IsMoving(Labor labor)
	{
		return labor != null && labor.CurrentTask != null && _moves.ContainsKey(labor);
	}

	private static Node MoveNode(Labor labor, GameObject item)
	{
		if (!_moves.TryGetValue(labor, out Move move) || item == null)
		{
			return Node.Empty;
		}
		GameComponent itemComponent = item.GetComponent<GameComponent>();
		GameComponent targetComponent = move.Target.GetComponent<GameComponent>();
		return Node.Sequence.Label("Consolidate stockpiles")
			.Do(labor.GoPickUp(itemComponent, move.Amount))
			.Label("Pick up from small stockpile")
			.Do((Labor l) =>
			{
				// Carrying now: stop being interruptible so the load is not dumped back by GoStore.
				l.Activity = Activity.Idle;
			}, labor)
			.Do((Labor l) => l.GoStoreIn(targetComponent), labor)
			.Label("Store in fullest stockpile")
			.Finally((Labor l) =>
			{
				Release(l);
			}, labor);
	}

	private static Dictionary<Ressource, List<Recepteur>> StockpilesByResource(Labor labor)
	{
		Dictionary<Ressource, List<Recepteur>> result = new Dictionary<Ressource, List<Recepteur>>();
		HashSet<GameObject> stockpiles = BrixSingleton<AutoList>.Instance.GetInstances(ObjetsDynamiques.Palette);
		if (stockpiles == null)
		{
			return result;
		}
		foreach (GameObject go in stockpiles)
		{
			if (go == null || !go.activeInHierarchy)
			{
				continue;
			}
			GameComponent component = go.GetComponent<GameComponent>();
			Recepteur recepteur = go.GetComponent<Recepteur>();
			if (component == null || recepteur == null || component.faction != labor.faction || recepteur.Fonction != FonctionDeContenant.entrepot || recepteur.IsEmpty())
			{
				continue;
			}
			// The warehouse is the team's big store: nothing is moved out of it, and stockpiles are not emptied into it.
			if (Building.Warehouse.IsWarehouse(go.transform))
			{
				continue;
			}
			// A mixed stockpile (MixedStockpiles) is listed under every resource it holds.
			foreach (KeyValuePair<System.Type, Adjectif> pair in recepteur.ContentDescription.DicoAdjectif)
			{
				Ressource resource = pair.Value as Ressource;
				if (resource == null || resource.quantifiable.valeur <= 0)
				{
					continue;
				}
				if (!result.TryGetValue(resource, out List<Recepteur> list))
				{
					list = new List<Recepteur>();
					result.Add(resource, list);
				}
				list.Add(recepteur);
			}
		}
		return result;
	}

	private static bool IsClaimed(Recepteur pile)
	{
		foreach (Move move in _moves.Values)
		{
			if (move.Source == pile || move.Target == pile)
			{
				return true;
			}
		}
		return false;
	}

	private static void ExpireClaims()
	{
		List<Labor> expired = null;
		foreach (KeyValuePair<Labor, Move> pair in _moves)
		{
			if (pair.Key == null || pair.Value.Source == null || pair.Value.Target == null || Time.time > pair.Value.ClaimedUntil)
			{
				if (expired == null)
				{
					expired = new List<Labor>();
				}
				expired.Add(pair.Key);
			}
		}
		if (expired == null)
		{
			return;
		}
		foreach (Labor labor in expired)
		{
			_moves.Remove(labor);
		}
	}
}
