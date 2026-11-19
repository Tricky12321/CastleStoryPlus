using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Components;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.InstructionType;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Lifecycle.Pooling;
using Brix.UI.Icons;
using Brix.Utils;
using CastleStoryPlus.Army;
using CastleStoryPlus.Core;
using CastleStoryPlus.Upgrades;
using Motus.Behavior;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.WorkerAI;

// Idle chore (#80): idle builders empty the storage holding the least of a resource into the one of the same resource
// that is fullest, so storages are freed for other resources until the team has a warehouse, without the player
// moving things by hand. Only offered by IdleProject, which workers reach when no real project has work for them, and
// interruptible until something is picked up, so real work always wins. Host only (the clients see the game's own
// pick up and store).
// - Storages: the team's stockpiles (also mixed ones) and, separately, its weapon stands (gear only goes from stand to
//   stand, through each stand's room, so RackFilter's limits hold). Warehouses are left out both ways: they are the
//   team's big store and take every resource, so they would be the target of every move and stockpiles the player
//   placed by a building site on purpose would be emptied into them; and the warehouse is never the smaller store.
// - The choice is ConsolidationRules': a source is emptied over as many trips and workers as it takes (or until
//   nothing more fits anywhere), into a target holding clearly more ([Storage.Consolidation] MinLead) within
//   MaxDistance, only trips of MinTripItems or the source's last items.
// - Never moved: items other workers are on their way to take (ResourceReservation: tasks, blueprints, workshops),
//   stands someone is on the way to take a kit from or put one on (KitReservation), storages the game reserves, and
//   resources a research has reserved (ResearchReservations: its pick up is capped to what is free).
// - A storage given a load is not emptied for ReceivedSeconds, one being emptied is no target, a planned move holds
//   its items and room for ClaimSeconds at most. After a look that found nothing the team's storages are not looked at
//   again for ScanSeconds, so idle workers asking every tick cost nothing.
internal static class StockpileConsolidation
{
	private class Move
	{
		public Recepteur Source;

		public Recepteur Target;

		public int Amount;

		public float ClaimedUntil;
	}

	private const string Section = "Storage.Consolidation";

	private static readonly ConsolidationSettings Settings = new ConsolidationSettings();

	private static ConfigEntry<int> _minLead;

	private static ConfigEntry<float> _maxDistance;

	private static ConfigEntry<float> _maxWalk;

	private static ConfigEntry<int> _minTripItems;

	private static ConfigEntry<float> _receivedSeconds;

	private static ConfigEntry<float> _claimSeconds;

	private static ConfigEntry<float> _scanSeconds;

	private static ConfigEntry<bool> _racks;

	private static readonly Dictionary<Labor, Move> _moves = new Dictionary<Labor, Move>();

	private static readonly Dictionary<Recepteur, float> _receivedAt = new Dictionary<Recepteur, float>();

	private static readonly Dictionary<Recepteur, float> _drainUntil = new Dictionary<Recepteur, float>();

	private static readonly Dictionary<Faction, float> _nextScan = new Dictionary<Faction, float>();

	// Buffers of a look, kept between looks (no allocation per idle tick).
	private static readonly List<Recepteur> _stores = new List<Recepteur>();

	private static readonly List<Ressource> _resources = new List<Ressource>();

	private static readonly HashSet<System.Type> _seen = new HashSet<System.Type>();

	private static readonly List<ConsolidationPile> _piles = new List<ConsolidationPile>();

	private static readonly List<ConsolidationPile> _pool = new List<ConsolidationPile>();

	private static readonly Dictionary<Recepteur, bool> _reach = new Dictionary<Recepteur, bool>();

	private static readonly List<Labor> _expiredLabors = new List<Labor>();

	private static readonly List<Recepteur> _expiredStores = new List<Recepteur>();

	private static readonly System.Predicate<Labor> IsConsolidating = (Labor worker) => worker != null && _moves.ContainsKey(worker);

	static StockpileConsolidation()
	{
		GameSession.OnLeave(() =>
		{
			_moves.Clear();
			_receivedAt.Clear();
			_drainUntil.Clear();
			_nextScan.Clear();
			_stores.Clear();
			_piles.Clear();
			_reach.Clear();
		});
	}

	public static readonly LaborInstruction<GameObject> Consolidate = new LaborInstruction<GameObject>("ConsolidateStockpiles", IconKeys.PickUp, (Labor labor, GameObject item) => MoveNode(labor, item));

	// Called by the feature's patch class (StockpileConsolidationPatch.Enable).
	internal static void BindConfig()
	{
		_minLead = Plugin.Cfg.Bind(Section, "MinLead", ConsolidationSettings.DefaultMinLead, new ConfigDescription("A resource is only moved into a stockpile (or weapon stand) holding at least this many more of it than the one it is taken from, so two about as full are left alone and nothing is carried back.", new AcceptableValueRange<int>(1, 100)));
		_maxDistance = Plugin.Cfg.Bind(Section, "MaxDistance", ConsolidationSettings.DefaultMaxDistance, new ConfigDescription("Farthest (blocks) a load is carried from one stockpile to the other.", new AcceptableValueRange<float>(4f, 300f)));
		_maxWalk = Plugin.Cfg.Bind(Section, "MaxWalk", ConsolidationSettings.DefaultMaxWalk, new ConfigDescription("Farthest (blocks) an idle worker walks to the stockpile it empties.", new AcceptableValueRange<float>(4f, 300f)));
		_minTripItems = Plugin.Cfg.Bind(Section, "MinTripItems", ConsolidationSettings.DefaultMinTripItems, new ConfigDescription("Fewest items a trip carries, unless they are the last ones of the stockpile it empties.", new AcceptableValueRange<int>(1, 20)));
		_receivedSeconds = Plugin.Cfg.Bind(Section, "ReceivedSeconds", ConsolidationSettings.DefaultReceivedSeconds, new ConfigDescription("A stockpile that was given a load is not emptied for this many seconds.", new AcceptableValueRange<float>(0f, 1800f)));
		_claimSeconds = Plugin.Cfg.Bind(Section, "ClaimSeconds", ConsolidationSettings.DefaultClaimSeconds, new ConfigDescription("Longest (seconds) a planned move holds its items and room, in case its worker is taken off without letting go.", new AcceptableValueRange<float>(10f, 600f)));
		_scanSeconds = Plugin.Cfg.Bind(Section, "ScanSeconds", ConsolidationSettings.DefaultScanSeconds, new ConfigDescription("After a look at the stockpiles found nothing to move, they are not looked at again for this many seconds.", new AcceptableValueRange<float>(0f, 60f)));
		_racks = Plugin.Cfg.Bind(Section, "WeaponStands", true, "Weapon stands are consolidated too (gear only goes from stand to stand, within each stand's class filter).");
	}

	private static void ReadSettings()
	{
		Settings.MinLead = (_minLead != null) ? _minLead.Value : ConsolidationSettings.DefaultMinLead;
		Settings.MaxDistance = (_maxDistance != null) ? _maxDistance.Value : ConsolidationSettings.DefaultMaxDistance;
		Settings.MaxWalk = (_maxWalk != null) ? _maxWalk.Value : ConsolidationSettings.DefaultMaxWalk;
		Settings.MinTripItems = (_minTripItems != null) ? _minTripItems.Value : ConsolidationSettings.DefaultMinTripItems;
		Settings.ReceivedSeconds = (_receivedSeconds != null) ? _receivedSeconds.Value : ConsolidationSettings.DefaultReceivedSeconds;
		Settings.ClaimSeconds = (_claimSeconds != null) ? _claimSeconds.Value : ConsolidationSettings.DefaultClaimSeconds;
		Settings.ScanSeconds = (_scanSeconds != null) ? _scanSeconds.Value : ConsolidationSettings.DefaultScanSeconds;
	}

	// Finds a move for this worker and claims its items and room. Returns the item to pick up, or null.
	public static GameObject TryPlan(Labor labor)
	{
		if (!NetworkServer.active || labor == null || labor.recepteur == null || !labor.recepteur.IsEmpty() || labor.Profession == null || labor.Profession.OccupationType != Occupation.Type.Builder)
		{
			return null;
		}
		Faction faction = labor.faction;
		if (faction == null)
		{
			return null;
		}
		float now = Time.time;
		if (_nextScan.TryGetValue(faction, out float next) && now < next)
		{
			return null;
		}
		ReadSettings();
		ExpireClaims(now);
		_reach.Clear();
		GameObject item = PlanIn(labor, faction, now, racks: false);
		if (item == null && (_racks == null || _racks.Value))
		{
			item = PlanIn(labor, faction, now, racks: true);
		}
		_stores.Clear();
		_reach.Clear();
		if (item == null)
		{
			_nextScan[faction] = now + Settings.ScanSeconds;
		}
		return item;
	}

	private static GameObject PlanIn(Labor labor, Faction faction, float now, bool racks)
	{
		CollectStores(faction, racks);
		if (_stores.Count < 2)
		{
			return null;
		}
		CollectResources(faction);
		Vector3 at = labor.transform.position;
		foreach (Ressource resource in _resources)
		{
			Recepteur sample = FillPiles(labor, resource, now, racks);
			if (sample == null || _piles.Count < 2 || !sample.FirstObjectOf(resource, out GameObject sampleItem))
			{
				continue;
			}
			GameComponent sampleComponent = sampleItem.GetComponent<GameComponent>();
			if (sampleComponent == null)
			{
				continue;
			}
			Factory.AssetKey key = sampleComponent.AssetKey;
			int hand = labor.recepteur.CanFitUpTo(key);
			if (hand <= 0)
			{
				continue;
			}
			foreach (ConsolidationPile pile in _piles)
			{
				pile.Room = RoomFor(_stores[pile.Id], key);
			}
			if (!ConsolidationRules.Plan(_piles, hand, at.x, at.y, at.z, Settings, out ConsolidationPlan plan))
			{
				continue;
			}
			Recepteur source = _stores[plan.Source];
			Recepteur target = _stores[plan.Target];
			if (!source.FirstObjectOf(resource, out GameObject item) || item == null)
			{
				continue;
			}
			_moves[labor] = new Move
			{
				Source = source,
				Target = target,
				Amount = plan.Amount,
				ClaimedUntil = now + Settings.ClaimSeconds
			};
			_receivedAt[target] = now;
			_drainUntil[source] = now + Settings.DrainSeconds;
			return item;
		}
		return null;
	}

	// The team's stockpiles (racks false) or weapon stands (racks true) that hold something, warehouses left out.
	private static void CollectStores(Faction faction, bool racks)
	{
		_stores.Clear();
		AutoList list = BrixSingleton<AutoList>.Instance;
		if (list == null)
		{
			return;
		}
		if (racks)
		{
			AddStores(list.GetInstances(ObjetsDynamiques.Toolrack), faction, true);
			AddStores(list.GetInstances(ObjetsDynamiques.SingleToolrack), faction, true);
		}
		else
		{
			AddStores(list.GetInstances(ObjetsDynamiques.Palette), faction, false);
		}
	}

	private static void AddStores(HashSet<GameObject> instances, Faction faction, bool racks)
	{
		if (instances == null)
		{
			return;
		}
		foreach (GameObject go in instances)
		{
			if (go == null || !go.activeInHierarchy)
			{
				continue;
			}
			GameComponent component = go.GetComponent<GameComponent>();
			Recepteur recepteur = go.GetComponent<Recepteur>();
			if (component == null || recepteur == null || component.faction != faction || recepteur.Fonction != FonctionDeContenant.entrepot || recepteur.IsEmpty())
			{
				continue;
			}
			if (Building.Warehouse.IsWarehouse(go.transform) || Building.MixedRacks.IsRack(recepteur) != racks)
			{
				continue;
			}
			_stores.Add(recepteur);
		}
	}

	// The resources the storages hold (a mixed stockpile holds several), each once; none a research has reserved.
	private static void CollectResources(Faction faction)
	{
		_resources.Clear();
		_seen.Clear();
		foreach (Recepteur store in _stores)
		{
			foreach (KeyValuePair<System.Type, Adjectif> pair in store.ContentDescription.DicoAdjectif)
			{
				Ressource resource = pair.Value as Ressource;
				if (resource == null || resource.quantifiable.valeur <= 0 || !_seen.Add(pair.Key))
				{
					continue;
				}
				if (ResearchReservations.IsReserved(faction, pair.Key))
				{
					continue;
				}
				_resources.Add(resource);
			}
		}
	}

	// The storages holding the resource, as piles (Id: index into _stores); returns one of them, or null.
	private static Recepteur FillPiles(Labor labor, Ressource resource, float now, bool racks)
	{
		_piles.Clear();
		Recepteur sample = null;
		for (int i = 0; i < _stores.Count; i++)
		{
			Recepteur store = _stores[i];
			int held = store.ContentDescription.Value(resource);
			if (held <= 0)
			{
				continue;
			}
			GameObject go = store.gameObject;
			bool reachable = CanReach(labor, store);
			bool kitBusy = racks && KitReservation.HasAny(go);
			ConsolidationPile pile = NextPile();
			pile.Id = i;
			pile.Held = held;
			pile.Reserved = ResourceReservation.ReservedAt(go, labor, IsConsolidating);
			pile.ClaimedOut = Claimed(store, outgoing: true);
			pile.ClaimedIn = Claimed(store, outgoing: false);
			pile.Room = 0;
			pile.Locked = !reachable || kitBusy || !Recepteur.CharactersCouldGrabFrom(go) || Knowledge.Instance.IsReserved(go);
			pile.Refuses = !reachable || kitBusy;
			pile.Draining = _drainUntil.TryGetValue(store, out float until) && now < until;
			pile.ReceivedAgo = _receivedAt.TryGetValue(store, out float at) ? now - at : float.MaxValue;
			Vector3 position = store.transform.position;
			pile.X = position.x;
			pile.Y = position.y;
			pile.Z = position.z;
			_piles.Add(pile);
			sample = sample ?? store;
		}
		return sample;
	}

	private static ConsolidationPile NextPile()
	{
		int index = _piles.Count;
		while (_pool.Count <= index)
		{
			_pool.Add(new ConsolidationPile());
		}
		return _pool[index];
	}

	private static bool CanReach(Labor labor, Recepteur store)
	{
		if (!_reach.TryGetValue(store, out bool reachable))
		{
			GameComponent component = store.GetComponent<GameComponent>();
			reachable = component != null && Knowledge.Instance != null && Knowledge.Instance.CanReach(labor, component);
			_reach[store] = reachable;
		}
		return reachable;
	}

	private static int Claimed(Recepteur store, bool outgoing)
	{
		int amount = 0;
		foreach (Move move in _moves.Values)
		{
			if ((outgoing ? move.Source : move.Target) == store)
			{
				amount += move.Amount;
			}
		}
		return amount;
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

	// Room the stockpile really has for the item: the resource's own room can be left on a mixed stockpile whose
	// columns are all taken.
	private static int RoomFor(Recepteur pile, Factory.AssetKey key)
	{
		if (pile.IsFull() || !pile.HasRoomFor(key))
		{
			return 0;
		}
		return pile.CanFitUpTo(key);
	}

	private static void ExpireClaims(float now)
	{
		_expiredLabors.Clear();
		foreach (KeyValuePair<Labor, Move> pair in _moves)
		{
			if (pair.Key == null || pair.Value.Source == null || pair.Value.Target == null || now > pair.Value.ClaimedUntil)
			{
				_expiredLabors.Add(pair.Key);
			}
		}
		foreach (Labor labor in _expiredLabors)
		{
			_moves.Remove(labor);
		}
		_expiredLabors.Clear();
		Expire(_receivedAt, now, Settings.ReceivedSeconds, false);
		Expire(_drainUntil, now, 0f, true);
	}

	// Drops entries of gone storages and old times (until: the value is an end time, else a start time plus seconds).
	private static void Expire(Dictionary<Recepteur, float> times, float now, float seconds, bool until)
	{
		_expiredStores.Clear();
		foreach (KeyValuePair<Recepteur, float> pair in times)
		{
			if (pair.Key == null || (until ? now >= pair.Value : now - pair.Value >= seconds))
			{
				_expiredStores.Add(pair.Key);
			}
		}
		foreach (Recepteur store in _expiredStores)
		{
			times.Remove(store);
		}
		_expiredStores.Clear();
	}
}
