using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using Brix.Assets;
using Brix.Engine;
using Brix.External.Factories;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.AI.Damage;
using Brix.Game.AI.InstructionType;
using Brix.Game.AI.KnowledgeSpace;
using Brix.Game.AI.Nodes;
using Brix.Game.Components;
using Brix.Game.Components.Volumes;
using Brix.Game.Factories;
using Brix.Game.Network;
using Brix.Game.Semantique;
using Brix.Input;
using Brix.Legacy;
using Brix.Lifecycle.Pooling;
using Brix.Transactions;
using Brix.UI.Icons;
using Brix.Utils;
using CastleStoryPlus.Core;
using CastleStoryPlus.Diagnostics;
using CastleStoryPlus.WorkerAI;
using HarmonyLib;
using Motus.Behavior;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Building;

// Demolition as a task of its own (#200, point 1). The player holds the demolish key (Delete) and left-clicks one of
// the team's placed buildings (workshop, stockpile, weapon stand, door, ...): the host puts a demolition blueprint over
// it into the team's build task, as the game's remove tool does (UNetBlueprint.CmdPlaceAnti), and commits it at once.
// The task's builders take it like any blueprint: one walks over and knocks the building down over time (the game's
// BuildGoal.AntiBlueprintNode). What it held is dropped, and the whole material cost of the building comes back as
// items on the spot (the rule MoveStructure uses for a moved building's old one); the worker that took it down then
// carries them to storage, load after load, and idle workers carry what is left (DemolishHaul). The same click on a
// marked building (or on its orange demolition ghost) takes the mark away again; so do the remove tool and deleting
// the blueprint, as for any demolition blueprint. Decided by the host: a client sends its click (only for its own
// buildings), the demolition blueprint reaches everyone the game's way. The marks and what they give back are saved
// next to the save (castlestoryplus_demolitions.json), so a demolition ordered before saving still gives the
// materials back after loading.
[Feature(Features.Demolish, Features.DemolishInfo)]
internal static class Demolish
{
	private const int RequestHash = 1145392460;

	private static ConfigEntry<KeyCode> _key;

	// Server: a building marked for demolition.
	internal class Mark
	{
		public Blueprint Anti;

		public Description Cost;

		// The last worker that took the demolition: it carries the materials away.
		public Labor Demolisher;

		// When the demolition blueprint was first seen gone while the building still stood (-1: not gone).
		public float AntiGoneSince = -1f;

		// What the building held when it was knocked down (dropped by StockpileSpill or here): carried away too.
		public List<GameObject> Contents;

		// A unit of another faction hit it after it was marked: it gives nothing back (DemolishRules.HitByOther).
		public bool HitByOther;
	}

	private static readonly Dictionary<GameObject, Mark> Marked = new Dictionary<GameObject, Mark>();

	private static readonly Dictionary<Blueprint, GameObject> ByAnti = new Dictionary<Blueprint, GameObject>();

	// Server: demolishers to send to their leftovers a moment after the building fell (not inside its release). The
	// game learns where new items lie only when it next maps what workers can reach (KnowledgeUpdater, every 200 ticks),
	// so the first tries find the leftovers out of reach; the demolisher is tried again until they can be reached.
	private class PendingHaul
	{
		public Labor Labor;

		public Vector3 Site;

		public float At;

		public float Until;

		public bool Told;
	}

	// How long a demolisher is tried for its leftovers, and how often.
	private const float HaulRetrySeconds = 120f;

	private const float HaulRetryInterval = 1f;

	private static readonly List<PendingHaul> PendingHauls = new List<PendingHaul>();

	private static readonly List<GameObject> Stale = new List<GameObject>();

	private static float _nextCheck;

	private static void Enable()
	{
		_key = Plugin.Cfg.Bind("Building", "DemolishKey", KeyCode.Delete, "Hold this key and left-click one of your buildings to have the builders take it down (its materials go back to storage); the same on a marked building cancels it.");
		NetworkBehaviour.RegisterCommandDelegate(typeof(UNetBlueprint), RequestHash, InvokeRequest);
		GameSession.OnLeave(() =>
		{
			Marked.Clear();
			ByAnti.Clear();
			PendingHauls.Clear();
			DemolishHaul.Clear();
		});
		GameSession.OnLevelReady(RestorePending);
	}

	internal static int Count => Marked.Count;

	internal static IEnumerable<KeyValuePair<GameObject, Mark>> All => Marked;

	internal static bool IsMarked(GameObject building)
	{
		return building != null && Marked.ContainsKey(building);
	}

	// ---- client: the key and the click

	// Every frame from InputModeController.Update (host and clients).
	internal static void Update()
	{
		Check();
		if (_key == null || !Input.GetKey(_key.Value) || !Input.GetMouseButtonDown(0) || InputModeController.DefaultMode != InputMode.command)
		{
			return;
		}
		if (Picking.Instance == null || Picking.Instance.UILocked || InputModeController.IsPaused() || InputModeController.PickerLocked || InputModeController.BuildModeLocked)
		{
			return;
		}
		GameObject building = BuildingUnderCursor();
		if (building == null)
		{
			return;
		}
		bool mark = FindAnti(building) == null;
		Request(building, mark);
		Brix.Audio.SoundEngine.Play(mark ? "Brick_Place" : "Brick_Cancel");
	}

	// The demolish key is held over a building: the game's own key action on that key must not fire.
	internal static bool KeyClaimed()
	{
		if (_key == null || !Input.GetKey(_key.Value) || InputModeController.DefaultMode != InputMode.command)
		{
			return false;
		}
		return BuildingUnderCursor() != null;
	}

	// The local player's building under the mouse: hit directly, or through its demolition ghost.
	private static GameObject BuildingUnderCursor()
	{
		VoxelRaycastHit hit = new VoxelRaycastHit();
		LayerBundle layers = new LayerBundle((int)UnityLayer.Terrain, (int)UnityLayer.Blueprints, (int)UnityLayer.FreeBlocks, (int)UnityLayer.DynamicObjects);
		if (Camera.main == null || !hit.PlaceAtRaycast(Camera.main.ScreenPointToRay(Input.mousePosition), Picking.Instance.MaxDistance, layers))
		{
			return null;
		}
		GameObject go = hit.HitGameObject;
		if (go == null || go.layer == (int)UnityLayer.Terrain)
		{
			return null;
		}
		GameObject building;
		Blueprint blueprint = go.GetComponentInParent<Blueprint>();
		if (blueprint != null)
		{
			building = blueprint.IsAnti ? BuildingOf(blueprint) : null;
		}
		else
		{
			MovableVolume volume = go.GetComponentInParent<MovableVolume>();
			building = (volume != null) ? volume.gameObject : null;
		}
		if (building == null || DemolishKey(building) == null)
		{
			return null;
		}
		Faction local = (User.LocalUser != null) ? User.LocalUser.faction : null;
		return (local != null && local.IsSame(building)) ? building : null;
	}

	// ---- buildings and their demolition blueprints

	// The blueprint key a placed building is demolished with; null when it is not one of the buildings this task takes
	// down (the firefly nest, rope bridges, things carried or still a blueprint).
	internal static Factory.AssetKey DemolishKey(GameObject building)
	{
		if (building == null || building.IsNullOrReleased() || building.GetComponent<Blueprint>() != null || building.GetComponent<RopeBridgeConfigurator>() != null)
		{
			return null;
		}
		MovableVolume volume = building.GetComponent<MovableVolume>();
		FactoryImprint imprint = building.GetComponent<FactoryImprint>();
		if (volume == null || imprint == null || building.GetComponent<GameComponent>() == null || !CopyPaste.IsPlacedBuilding(volume))
		{
			return null;
		}
		Factory.AssetKey key = BlueprintAssetKeyResolver.Resolve(imprint.AssetKey);
		return key.IsNullOrInvalid() ? null : key;
	}

	// The demolition blueprint over the building (this task's, the remove tool's or a move's), or null.
	internal static Blueprint FindAnti(GameObject building)
	{
		if (building == null)
		{
			return null;
		}
		if (Marked.TryGetValue(building, out Mark mark) && IsLive(mark.Anti))
		{
			return mark.Anti;
		}
		Factory.AssetKey key = DemolishKey(building);
		if (key == null)
		{
			return null;
		}
		Vector3 at = building.transform.position;
		foreach (Blueprint blueprint in UnityEngine.Object.FindObjectsOfType<Blueprint>())
		{
			if (IsLive(blueprint) && blueprint.AssetKey == key && SameSpot(blueprint.transform.position, at))
			{
				return blueprint;
			}
		}
		return null;
	}

	// The building a demolition blueprint stands on, or null.
	internal static GameObject BuildingOf(Blueprint anti)
	{
		if (!IsLive(anti))
		{
			return null;
		}
		if (ByAnti.TryGetValue(anti, out GameObject known) && known != null && !known.IsNullOrReleased())
		{
			return known;
		}
		Vector3 at = anti.transform.position;
		foreach (MovableVolume volume in UnityEngine.Object.FindObjectsOfType<MovableVolume>())
		{
			if (volume != null && SameSpot(volume.transform.position, at) && DemolishKey(volume.gameObject) == anti.AssetKey)
			{
				return volume.gameObject;
			}
		}
		return null;
	}

	private static bool IsLive(Blueprint anti)
	{
		return anti != null && !anti.Released && anti.IsAnti;
	}

	private static bool SameSpot(Vector3 a, Vector3 b)
	{
		return DemolishRules.SameSpot(a.x - b.x, a.y - b.y, a.z - b.z);
	}

	// ---- the request: client to host

	// Client or host: asks for the building's demolition (mark) or its cancel; the host decides. On the host, null
	// when done, else why not; a client's request is sent (null) and the host's answer is only in its log.
	internal static string Request(GameObject building, bool mark)
	{
		UNetBlueprint cmd = (User.LocalUser != null) ? User.LocalUser.GetComponent<UNetBlueprint>() : null;
		if (cmd == null || building == null)
		{
			return "refused: no player or no building";
		}
		if (cmd.isServer)
		{
			string refused = Apply(User.LocalUser, building, mark);
			if (refused != null)
			{
				Plugin.Log.LogInfo("Demolish: " + building.name + " " + refused);
			}
			return refused;
		}
		NetworkWriter writer = new NetworkWriter();
		writer.Write((short)0);
		writer.Write((short)5);
		writer.WritePackedUInt32((uint)RequestHash);
		writer.Write(cmd.GetComponent<NetworkIdentity>().netId);
		writer.Write(building);
		writer.Write(mark);
		cmd.SendCommandInternal(writer, 0, "CmdPlusDemolish");
		return null;
	}

	private static void InvokeRequest(NetworkBehaviour obj, NetworkReader reader)
	{
		if (!NetworkServer.active)
		{
			return;
		}
		GameObject building = reader.ReadGameObject();
		bool mark = reader.ReadBoolean();
		User user = (obj != null) ? obj.GetComponent<User>() : null;
		string refused = Apply(user, building, mark);
		if (refused != null)
		{
			Plugin.Log.LogInfo("Demolish: request of " + ((user != null) ? user.name : "?") + " for " + ((building != null) ? building.name : "a gone building") + " " + refused);
		}
	}

	// Server: marks the building for demolition or takes the mark away. Null when done (or nothing to do), else why not.
	// The demolition goes into the team's build task (the one with the most workers), or into the given one (tests).
	internal static string Apply(User user, GameObject building, bool mark, BuildProject into = null)
	{
		if (!NetworkServer.active)
		{
			return "refused: only the host decides demolitions";
		}
		if (user == null || user.faction == null)
		{
			return "refused: no player";
		}
		if (building == null || building.IsNullOrReleased())
		{
			return "refused: the building is gone";
		}
		Factory.AssetKey key = DemolishKey(building);
		Blueprint anti = FindAnti(building);
		DemolishRules.Decision decision = DemolishRules.Decide(mark, anti != null, user.faction.IsSame(building), key != null, MoveStructure.IsMoving(building));
		switch (decision)
		{
			case DemolishRules.Decision.Refuse:
				return "refused: not the player's own building, or not one that can be demolished";
			case DemolishRules.Decision.Moving:
				return "refused: it is being moved";
			case DemolishRules.Decision.Nothing:
				return null;
			case DemolishRules.Decision.Cancel:
				Cancel(building, anti);
				return null;
			default:
				return Place(user, building, key, into);
		}
	}

	private static string Place(User user, GameObject building, Factory.AssetKey key, BuildProject into)
	{
		BuildProject team = (into != null && into.IsStillAlive && into.faction == user.faction) ? into : GlobalBuildJob.TeamProject(user);
		BuildGoalProvider provider = (team != null) ? team.GetComponent<BuildGoalProvider>() : null;
		if (provider == null)
		{
			return "refused: no build task";
		}
		// As the remove tool's CmdPlaceAnti.
		GameObject go = Transactor.SpawnUNet(key, user.faction, building.transform.position, building.transform.rotation);
		Blueprint anti = (go != null) ? go.GetComponent<Blueprint>() : null;
		if (anti == null)
		{
			return "refused: the game made no demolition blueprint for " + key;
		}
		anti.Network_isAnti = true;
		Description cost = (anti.recepteur != null) ? anti.recepteur.BaseCapacity.Clone() : null;
		provider.AddObject(go);
		// Committed at once, whether or not the build task is selected: it is worked on right away.
		GlobalBuildJob.Commit(anti);
		// The task itself must be committed too, or its builders are never given the demolition.
		bool committedTask = GlobalBuildJob.CommitProject(team);
		PauseTasks.ResumeForNewWork(team);
		Marked[building] = new Mark { Anti = anti, Cost = cost };
		ByAnti[anti] = building;
		Plugin.Log.LogInfo("Demolish: " + building.name + " marked for demolition in " + team.name + " (" + team.CrewCount() + " workers, task " + team.Commited + (committedTask ? ", committed now" : string.Empty) + (team.Editing() ? ", being edited: no work until the player leaves it" : string.Empty) + ")");
		return null;
	}

	private static void Cancel(GameObject building, Blueprint anti)
	{
		Forget(building);
		if (IsLive(anti))
		{
			// As the game takes a blueprint away (UNetBlueprint._UnspawnBlueprint).
			NetworkServer.UnSpawn(anti.gameObject);
			ObjectPoolSingleton.Release(anti.gameObject);
		}
		Plugin.Log.LogInfo("Demolish: demolition of " + building.name + " cancelled");
	}

	private static void Forget(GameObject building)
	{
		if (building != null && Marked.TryGetValue(building, out Mark mark))
		{
			Marked.Remove(building);
			if (mark.Anti != null)
			{
				ByAnti.Remove(mark.Anti);
			}
		}
	}

	// ---- server: bookkeeping

	// Once a second: a mark whose demolition blueprint was taken away (remove tool, deleted task) while the building
	// stands is cancelled; demolishers are sent to their leftovers.
	private static void Check()
	{
		if (!NetworkServer.active)
		{
			return;
		}
		float now = Time.time;
		for (int i = PendingHauls.Count - 1; i >= 0; i--)
		{
			PendingHaul pending = PendingHauls[i];
			if (now < pending.At)
			{
				continue;
			}
			string why = DemolishHaul.Start(pending.Labor, pending.Site);
			if (why == null)
			{
				PendingHauls.RemoveAt(i);
				continue;
			}
			bool gone = pending.Labor == null || pending.Labor.IsNullOrReleased() || DemolishHaul.Count == 0;
			if (gone || now >= pending.Until)
			{
				PendingHauls.RemoveAt(i);
				Plugin.Log.LogInfo("Demolish: " + ((pending.Labor != null) ? pending.Labor.name : "the demolisher") + " does not carry the leftovers of its demolition (" + why + ")" + (gone ? string.Empty : " after " + HaulRetrySeconds + " s of tries") + "; " + DemolishHaul.Count + " leftovers are left for idle workers");
				continue;
			}
			if (!pending.Told)
			{
				pending.Told = true;
				Plugin.Log.LogInfo("Demolish: " + pending.Labor.name + " cannot carry the leftovers of its demolition yet (" + why + "); tried again every " + HaulRetryInterval + " s");
			}
			pending.At = now + HaulRetryInterval;
		}
		if (now < _nextCheck || Marked.Count == 0)
		{
			return;
		}
		_nextCheck = now + 1f;
		Stale.Clear();
		foreach (KeyValuePair<GameObject, Mark> pair in Marked)
		{
			bool standing = pair.Key != null && !pair.Key.IsNullOrReleased();
			if (!standing)
			{
				Stale.Add(pair.Key);
				continue;
			}
			bool antiAlive = IsLive(pair.Value.Anti);
			if (antiAlive)
			{
				pair.Value.AntiGoneSince = -1f;
				continue;
			}
			if (pair.Value.AntiGoneSince < 0f)
			{
				pair.Value.AntiGoneSince = now;
			}
			if (DemolishRules.Cancelled(antiAlive, standing, now, pair.Value.AntiGoneSince))
			{
				Stale.Add(pair.Key);
				Plugin.Log.LogInfo("Demolish: the demolition blueprint of " + pair.Key.name + " was taken away; its demolition is cancelled");
			}
		}
		foreach (GameObject building in Stale)
		{
			Forget(building);
		}
		Stale.Clear();
	}

	// Server: a builder takes the demolition of a marked building; it is the one to carry the materials away.
	internal static void OnWork(BuildGoal goal, Labor labor)
	{
		if (!NetworkServer.active || goal == null || labor == null || goal.IsNullOrReleased())
		{
			return;
		}
		Blueprint anti = goal.Blueprint;
		if (anti != null && ByAnti.TryGetValue(anti, out GameObject building) && building != null && Marked.TryGetValue(building, out Mark mark))
		{
			mark.Demolisher = labor;
		}
	}

	// Server: a marked building is hit; a hit by a unit of another faction forfeits its refund.
	internal static void OnAttack(GameObject building, GameObject attacker)
	{
		if (!NetworkServer.active || building == null || !Marked.TryGetValue(building, out Mark mark) || mark.HitByOther)
		{
			return;
		}
		bool known = !attacker.IsNullOrReleased();
		GameComponent own = building.GetComponent<GameComponent>();
		GameComponent other = known ? attacker.GetComponent<GameComponent>() : null;
		bool same = own != null && other != null && own.faction == other.faction;
		if (DemolishRules.HitByOther(known && other != null, same))
		{
			mark.HitByOther = true;
			Plugin.Log.LogInfo("Demolish: " + building.name + " was hit by " + attacker.name + " of another faction; it gives nothing back when it falls");
		}
	}

	// Server: a marked building is knocked down; what it holds is noted before StockpileSpill drops it.
	internal static void OnKill(GameObject building)
	{
		if (!NetworkServer.active || building == null || !Marked.TryGetValue(building, out Mark mark))
		{
			return;
		}
		Recepteur recepteur = building.GetComponent<Recepteur>();
		if (recepteur == null || !recepteur.CarriesSomething())
		{
			return;
		}
		mark.Contents = new List<GameObject>();
		foreach (IDescriptor stored in recepteur.StoredItems)
		{
			if (stored != null && stored.GameObject != null)
			{
				mark.Contents.Add(stored.GameObject);
			}
		}
	}

	// Server: a marked building is gone: what it held is dropped, its cost comes back as items on the spot, and all of
	// it is left for the demolisher (and then idle workers) to carry to storage.
	internal static void OnRelease(GameObject building)
	{
		if (!NetworkServer.active || building == null || !Marked.TryGetValue(building, out Mark mark))
		{
			return;
		}
		Forget(building);
		Vector3 center = building.transform.position;
		GameComponent component = building.GetComponent<GameComponent>();
		Faction faction = (component != null) ? component.faction : null;
		List<GameObject> leftovers = new List<GameObject>();
		if (mark.Contents != null)
		{
			leftovers.AddRange(mark.Contents);
		}
		if (component != null && component.recepteur != null && component.recepteur.CarriesSomething())
		{
			foreach (IDescriptor stored in component.recepteur.StoredItems)
			{
				if (stored != null && stored.GameObject != null && !leftovers.Contains(stored.GameObject))
				{
					leftovers.Add(stored.GameObject);
				}
			}
			component.recepteur.DropAll();
		}
		int contents = leftovers.Count;
		List<GameObject> refunded = new List<GameObject>();
		List<KeyValuePair<Factory.AssetKey, int>> refund = DemolishRules.GivesBack(mark.HitByOther) ? Refund(mark.Cost) : new List<KeyValuePair<Factory.AssetKey, int>>();
		int total = 0;
		foreach (KeyValuePair<Factory.AssetKey, int> entry in refund)
		{
			total += entry.Value;
		}
		// Each item gets its own place on the spot (SpawnSpread), not all one point: items dropped in one point
		// overlap, and the physics pushed them apart so hard that some flew off the island into the void (a test
		// lost 2 of 6 planks so). Recepteur.ScatterObjects is not used: it puts every item back on the one point.
		foreach (KeyValuePair<Factory.AssetKey, int> entry in refund)
		{
			for (int i = 0; i < entry.Value; i++)
			{
				GameObject go = Transactor.SpawnUNet(entry.Key, faction, SpawnSpread.Place(center, refunded.Count, total), Quaternion.identity);
				if (go != null)
				{
					SpawnSpread.Calm(go);
					refunded.Add(go);
				}
			}
		}
		leftovers.AddRange(refunded);
		DemolishHaul.Add(leftovers, faction, center);
		Labor demolisher = mark.Demolisher;
		if (demolisher != null && !demolisher.IsNullOrReleased() && leftovers.Count > 0)
		{
			PendingHauls.Add(new PendingHaul { Labor = demolisher, Site = center, At = Time.time + 0.5f, Until = Time.time + HaulRetrySeconds });
		}
		Plugin.Log.LogInfo("Demolish: " + building.name + " demolished: " + refunded.Count + " material items given back, " + contents + " items of its contents dropped" + ((demolisher != null) ? ", " + demolisher.name + " carries them to storage" : string.Empty));
	}

	// Server: the items (and how many of each) a marked building gives back when it is demolished; empty when it is
	// not marked.
	internal static List<KeyValuePair<Factory.AssetKey, int>> RefundOf(GameObject building)
	{
		return (building != null && Marked.TryGetValue(building, out Mark mark)) ? Refund(mark.Cost) : new List<KeyValuePair<Factory.AssetKey, int>>();
	}

	private static List<KeyValuePair<Factory.AssetKey, int>> Refund(Description cost)
	{
		List<KeyValuePair<Factory.AssetKey, int>> result = new List<KeyValuePair<Factory.AssetKey, int>>();
		if (cost == null)
		{
			return result;
		}
		Dictionary<string, Ressource> kinds = new Dictionary<string, Ressource>();
		List<KeyValuePair<string, int>> counts = new List<KeyValuePair<string, int>>();
		foreach (Ressource ressource in cost.GetRessources().ToList())
		{
			if (ressource == null || ressource.quantifiable == null)
			{
				continue;
			}
			string name = ressource.GetType().FullName;
			kinds[name] = ressource;
			counts.Add(new KeyValuePair<string, int>(name, (int)ressource.quantifiable.valeur));
		}
		foreach (KeyValuePair<string, int> entry in DemolishRules.Refund(counts))
		{
			Factory.AssetKey item = Description.RepresentativeOf(kinds[entry.Key]);
			if (item != null)
			{
				result.Add(new KeyValuePair<Factory.AssetKey, int>(item, entry.Value));
			}
		}
		return result;
	}

	// ---- saving

	private const string SaveFile = "castlestoryplus_demolitions.json";

	// Server: called when the game has written a save into its folder.
	internal static void WritePending(string folder)
	{
		string file = System.IO.Path.Combine(folder, SaveFile);
		JArray marks = new JArray();
		foreach (KeyValuePair<GameObject, Mark> pair in Marked)
		{
			FactoryImprint imprint = (pair.Key != null) ? pair.Key.GetComponent<FactoryImprint>() : null;
			if (imprint == null || pair.Key.IsNullOrReleased() || !IsLive(pair.Value.Anti))
			{
				continue;
			}
			Vector3 position = pair.Key.transform.position;
			JArray cost = new JArray();
			if (pair.Value.Cost != null)
			{
				foreach (KeyValuePair<Type, Adjectif> entry in pair.Value.Cost.DicoAdjectif)
				{
					if (entry.Value is Ressource && entry.Value.quantifiable != null)
					{
						cost.Add(new JObject { ["type"] = entry.Key.FullName, ["count"] = entry.Value.quantifiable.valeur });
					}
				}
			}
			marks.Add(new JObject
			{
				["factory"] = imprint.AssetKey.Factory,
				["name"] = imprint.AssetKey.Name,
				["x"] = position.x,
				["y"] = position.y,
				["z"] = position.z,
				["cost"] = cost
			});
		}
		try
		{
			if (marks.Count == 0)
			{
				if (File.Exists(file))
				{
					File.Delete(file);
				}
				return;
			}
			File.WriteAllText(file, marks.ToString());
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("Demolish: could not save the demolitions: " + ex.Message);
		}
	}

	// Server, when a level is ready: the demolitions of the loaded save (its demolition blueprints are in the save).
	private static void RestorePending()
	{
		Asset_Map map = GameParam.Map;
		if (!NetworkServer.active || map == null || string.IsNullOrEmpty(map.path))
		{
			return;
		}
		string file = System.IO.Path.Combine(map.path, SaveFile);
		if (!File.Exists(file))
		{
			return;
		}
		try
		{
			FactoryImprint[] buildings = UnityEngine.Object.FindObjectsOfType<FactoryImprint>();
			int restored = 0;
			foreach (JToken entry in JArray.Parse(File.ReadAllText(file)))
			{
				Factory.AssetKey key = new Factory.AssetKey((string)entry["factory"], (string)entry["name"]);
				Vector3 position = new Vector3((float)entry["x"], (float)entry["y"], (float)entry["z"]);
				GameObject building = Find(buildings, key, position);
				Blueprint anti = (building != null) ? FindAnti(building) : null;
				if (anti == null || Marked.ContainsKey(building))
				{
					continue;
				}
				Description cost = new Description();
				foreach (JToken item in entry["cost"])
				{
					Type type = typeof(Adjectif).Assembly.GetType((string)item["type"]);
					if (type != null)
					{
						cost.Add(Adjectif.New(type, (int)item["count"]));
					}
				}
				Marked[building] = new Mark { Anti = anti, Cost = cost };
				ByAnti[anti] = building;
				restored++;
			}
			Plugin.Log.LogInfo("Demolish: restored " + restored + " demolitions");
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("Demolish: could not read the demolitions: " + ex.Message);
		}
	}

	private static GameObject Find(FactoryImprint[] buildings, Factory.AssetKey key, Vector3 position)
	{
		foreach (FactoryImprint imprint in buildings)
		{
			if (imprint != null && !imprint.Released && imprint.AssetKey == key && SameSpot(imprint.transform.position, position))
			{
				return imprint.gameObject;
			}
		}
		return null;
	}
}

// Carrying a demolition's leftovers (its contents and its materials) to storage: first by the worker that took the
// building down, load after load, then as an idle chore for workers on no task. A worker picks up one item and then
// the others of the leftovers round it that fit in its hands, and stores the load where the game would.
[Feature(Features.Demolish, Features.DemolishInfo)]
internal static class DemolishHaul
{
	private class Leftover
	{
		public Faction Faction;

		public Vector3 Site;
	}

	private class Claim
	{
		public Labor Labor;

		public float At;
	}

	private const float ClaimSeconds = 90f;

	// Leftovers further than this from a worker's first item are not added to its load.
	private const float LoadRadius = 6f;

	// Items picked up after the first for one load.
	private const int MaxExtraPerLoad = 30;

	// Loads a demolisher carries on its own before it goes back to its task (idle workers take the rest).
	private const int MaxLoads = 12;

	// Leftovers looked at per plan (nearest first).
	private const int MaxTries = 24;

	private static readonly Dictionary<GameObject, Leftover> Leftovers = new Dictionary<GameObject, Leftover>();

	private static readonly Dictionary<GameObject, Claim> Claims = new Dictionary<GameObject, Claim>();

	private static readonly List<GameObject> Candidates = new List<GameObject>();

	private static readonly List<GameObject> Gone = new List<GameObject>();

	// What each worker picked up for its current load, to log where it was stored.
	private static readonly Dictionary<Labor, List<GameObject>> Loads = new Dictionary<Labor, List<GameObject>>();

	public static readonly LaborInstruction<GameObject> Carry = new LaborInstruction<GameObject>("DemolitionLeftovers", IconKeys.PickUp, (Labor labor, GameObject item) => HaulNode(labor, item, 1));

	internal static int Count => Leftovers.Count;

	internal static void Clear()
	{
		Leftovers.Clear();
		Claims.Clear();
		Loads.Clear();
	}

	internal static void Add(List<GameObject> items, Faction faction, Vector3 site)
	{
		foreach (GameObject item in items)
		{
			if (item != null && !item.IsNullOrReleased())
			{
				Leftovers[item] = new Leftover { Faction = faction, Site = site };
			}
		}
	}

	// Whether the worker carries a load of leftovers now.
	internal static bool IsCarrying(Labor labor)
	{
		if (labor == null || labor.CurrentTask == null)
		{
			return false;
		}
		foreach (Claim claim in Claims.Values)
		{
			if (claim.Labor == labor)
			{
				return true;
			}
		}
		return false;
	}

	// Server: the demolisher goes for the leftovers of its demolition at once. Null when it went, else why not (the
	// caller tries again later: new items are out of reach until the game maps them).
	internal static string Start(Labor labor, Vector3 site)
	{
		if (labor == null || labor.IsNullOrReleased() || !labor.gameObject.activeInHierarchy)
		{
			return "the demolisher is gone";
		}
		GameObject item = TryPlan(labor, site, out string why);
		if (item == null)
		{
			return why;
		}
		Carry.Send(labor, item, Labor.DecisionSource.Project);
		Plugin.Log.LogInfo("Demolish: " + labor.name + " carries the leftovers of its demolition to storage, first " + item.name + " (" + Leftovers.Count + " leftovers)");
		return null;
	}

	// An unclaimed leftover of the worker's team it can reach, carry and store, nearest to the place first; null when
	// there is none, with why not. The worker's hands must be empty. Claims it for the worker.
	internal static GameObject TryPlan(Labor labor, Vector3 near, out string why)
	{
		if (!NetworkServer.active || labor == null || labor.recepteur == null)
		{
			why = "not the host, or no worker";
			return null;
		}
		if (!labor.recepteur.IsEmpty())
		{
			why = "its hands are full";
			return null;
		}
		if (Leftovers.Count == 0)
		{
			why = "no leftovers";
			return null;
		}
		Prune();
		Candidates.Clear();
		foreach (KeyValuePair<GameObject, Leftover> pair in Leftovers)
		{
			if (pair.Value.Faction == labor.faction && !IsClaimedByOther(pair.Key, labor))
			{
				Candidates.Add(pair.Key);
			}
		}
		if (Candidates.Count == 0)
		{
			why = (Leftovers.Count == 0) ? "the leftovers were picked up or are gone" : "none of the " + Leftovers.Count + " leftovers is the worker's team's and unclaimed";
			return null;
		}
		Candidates.Sort((GameObject a, GameObject b) => (a.transform.position - near).sqrMagnitude.CompareTo((b.transform.position - near).sqrMagnitude));
		int tries = 0;
		int noRoom = 0;
		int unreachable = 0;
		int noStorage = 0;
		foreach (GameObject item in Candidates)
		{
			if (tries++ >= MaxTries)
			{
				break;
			}
			GameComponent component = item.GetComponent<GameComponent>();
			if (component == null || !labor.recepteur.HasRoomFor(item))
			{
				noRoom++;
				continue;
			}
			if (!Knowledge.Instance.CanReach(labor, component))
			{
				unreachable++;
				continue;
			}
			if (labor.BestRecepteurToStore(item) == null)
			{
				noStorage++;
				continue;
			}
			Claims[item] = new Claim { Labor = labor, At = Time.time };
			why = null;
			return item;
		}
		why = "of " + Candidates.Count + " leftovers " + noRoom + " do not fit in its hands, " + unreachable + " are out of its reach (not yet mapped), " + noStorage + " have no stockpile with room";
		return null;
	}

	// Pick up the item and the leftovers round it that fit, store the load, and (for the demolisher) go on with the
	// next load.
	private static Node HaulNode(Labor labor, GameObject item, int load)
	{
		GameComponent component = (item != null) ? item.GetComponent<GameComponent>() : null;
		if (component == null || labor == null)
		{
			Release(labor);
			return Node.Empty;
		}
		return Node.Sequence.Label("Demolition leftovers")
			.Do(labor.GoPickUp(component, -1))
			.Label("Pick up a demolition leftover")
			.Do((Labor l) =>
			{
				// Carrying now: an idle chore is no longer interruptible, so the load is not dumped again.
				if (l.Activity == Activity.Chilling)
				{
					l.Activity = Activity.Idle;
				}
			}, labor)
			.Do((Func<Labor, Vector3, int, Node>)PickMore, labor, item.transform.position, 0)
			.Do((Labor l, int n) =>
			{
				NotePickedUp(l, n);
			}, labor, load)
			.Do(labor.GoStoreInBestStorage())
			.Label("Store demolition leftovers")
			.Do((Labor l, int n) =>
			{
				NoteStored(l, n);
			}, labor, load)
			.Do((Func<Labor, Vector3, int, Node>)NextLoad, labor, item.transform.position, load)
			.Finally((Labor l) =>
			{
				Release(l);
			}, labor);
	}

	// One more leftover near the first that fits in the worker's hands, then the next.
	private static Node PickMore(Labor labor, Vector3 around, int picked)
	{
		if (picked >= MaxExtraPerLoad || labor == null || labor.IsNullOrReleased())
		{
			return Node.Empty;
		}
		GameObject next = null;
		float best = LoadRadius * LoadRadius;
		foreach (KeyValuePair<GameObject, Leftover> pair in Leftovers)
		{
			GameObject item = pair.Key;
			if (item == null || item.IsNullOrReleased() || pair.Value.Faction != labor.faction || IsClaimedByOther(item, labor) || !IsLoose(item))
			{
				continue;
			}
			float distance = (item.transform.position - around).sqrMagnitude;
			if (distance < best && labor.recepteur.HasRoomFor(item))
			{
				best = distance;
				next = item;
			}
		}
		if (next == null)
		{
			return Node.Empty;
		}
		Claims[next] = new Claim { Labor = labor, At = Time.time };
		return Node.Sequence.Do(labor.GoPickUp(next.GetComponent<GameComponent>(), -1)).Do((Func<Labor, Vector3, int, Node>)PickMore, labor, around, picked + 1);
	}

	// The demolisher's next load, while leftovers of its team remain near the demolition.
	private static Node NextLoad(Labor labor, Vector3 site, int load)
	{
		Release(labor);
		if (load >= MaxLoads || labor == null || labor.IsNullOrReleased() || labor.Project is IdleProject || labor.Project == null)
		{
			if (labor != null && !(labor.Project is IdleProject))
			{
				Plugin.Log.LogInfo("Demolish: " + labor.name + " goes back to its task after load " + load + " of demolition leftovers; " + Leftovers.Count + " leftovers left");
			}
			// An idle worker asks the idle task again for its next chore.
			return Node.Empty;
		}
		GameObject item = TryPlan(labor, site, out string why);
		if (item == null || (item.transform.position - site).sqrMagnitude > LoadRadius * LoadRadius * 4f)
		{
			if (item != null)
			{
				Claims.Remove(item);
				why = "the next leftover lies too far from the demolition";
			}
			Plugin.Log.LogInfo("Demolish: " + labor.name + " stops carrying demolition leftovers after load " + load + " (" + why + "); " + Leftovers.Count + " leftovers left");
			return Node.Empty;
		}
		Plugin.Log.LogInfo("Demolish: " + labor.name + " carries load " + (load + 1) + " of demolition leftovers");
		return HaulNode(labor, item, load + 1);
	}

	// The load in the worker's hands, logged as picked up; where it went is logged once it is stored.
	private static void NotePickedUp(Labor labor, int load)
	{
		if (labor == null || labor.IsNullOrReleased() || labor.recepteur == null)
		{
			return;
		}
		List<GameObject> items = new List<GameObject>();
		foreach (IDescriptor carried in labor.recepteur.StoredItems)
		{
			if (carried != null && carried.GameObject != null)
			{
				items.Add(carried.GameObject);
			}
		}
		Loads[labor] = items;
		Plugin.Log.LogInfo("Demolish: " + labor.name + " picked up load " + load + " of demolition leftovers: " + labor.recepteur.ContentString() + " (" + items.Count + " items; " + Leftovers.Count + " leftovers tracked)");
	}

	private static void NoteStored(Labor labor, int load)
	{
		if (labor == null || !Loads.TryGetValue(labor, out List<GameObject> items))
		{
			return;
		}
		Loads.Remove(labor);
		Dictionary<string, int> where = new Dictionary<string, int>();
		foreach (GameObject item in items)
		{
			Tally(where, Whereabouts(item));
		}
		Plugin.Log.LogInfo("Demolish: " + labor.name + " stored load " + load + " of demolition leftovers: " + Describe(where));
	}

	// Where a leftover is: in a storage or someone's hands, lying loose, or gone.
	private static string Whereabouts(GameObject item)
	{
		if (item == null || item.IsNullOrReleased())
		{
			return "gone (destroyed, e.g. fallen into the void)";
		}
		GameComponent component = item.GetComponent<GameComponent>();
		Recepteur parent = (component != null) ? component.ParentRecepteur : null;
		if (parent != null)
		{
			return "in " + parent.name + " at " + XYZ.FromVector3(parent.transform.position);
		}
		if (!item.activeInHierarchy)
		{
			return "inactive";
		}
		return "loose at " + XYZ.FromVector3(item.transform.position);
	}

	private static void Tally(Dictionary<string, int> where, string place)
	{
		where[place] = where.TryGetValue(place, out int n) ? n + 1 : 1;
	}

	private static string Describe(Dictionary<string, int> where)
	{
		string text = string.Empty;
		foreach (KeyValuePair<string, int> pair in where)
		{
			text += ((text.Length > 0) ? ", " : string.Empty) + pair.Value + " " + pair.Key;
		}
		return text;
	}

	private static bool IsLoose(GameObject item)
	{
		GameComponent component = item.GetComponent<GameComponent>();
		return component != null && item.activeInHierarchy && component.ParentRecepteur == null;
	}

	private static bool IsClaimedByOther(GameObject item, Labor labor)
	{
		return Claims.TryGetValue(item, out Claim claim) && claim.Labor != labor && claim.Labor != null && !DemolishRules.ClaimLapsed(Time.time, claim.At, ClaimSeconds);
	}

	private static void Release(Labor labor)
	{
		if (labor == null || Claims.Count == 0)
		{
			return;
		}
		Gone.Clear();
		foreach (KeyValuePair<GameObject, Claim> pair in Claims)
		{
			if (pair.Value.Labor == labor)
			{
				Gone.Add(pair.Key);
			}
		}
		foreach (GameObject item in Gone)
		{
			Claims.Remove(item);
		}
		Gone.Clear();
	}

	// Leftovers that were picked up, stored or are gone are no longer tracked; lapsed claims are dropped.
	private static void Prune()
	{
		Gone.Clear();
		foreach (GameObject item in Leftovers.Keys)
		{
			if (item == null || item.IsNullOrReleased() || !IsLoose(item))
			{
				Gone.Add(item);
			}
		}
		Dictionary<string, int> where = null;
		foreach (GameObject item in Gone)
		{
			Leftovers.Remove(item);
			where = where ?? new Dictionary<string, int>();
			Tally(where, Whereabouts(item));
		}
		if (where != null)
		{
			Plugin.Log.LogInfo("Demolish: " + Gone.Count + " demolition leftovers no longer lie loose: " + Describe(where) + "; " + Leftovers.Count + " left");
		}
		Gone.Clear();
		foreach (KeyValuePair<GameObject, Claim> pair in Claims)
		{
			if (pair.Key == null || pair.Value.Labor == null || DemolishRules.ClaimLapsed(Time.time, pair.Value.At, ClaimSeconds))
			{
				Gone.Add(pair.Key);
			}
		}
		foreach (GameObject item in Gone)
		{
			Claims.Remove(item);
		}
		Gone.Clear();
	}
}

[Feature(Features.Demolish, Features.DemolishInfo)]
[HarmonyPatch(typeof(InputModeController), nameof(InputModeController.Update))]
internal static class DemolishInputPatch
{
	private static void Prefix()
	{
		Demolish.Update();
	}
}

// Key actions on the demolish key do not fire while it is held over a building to demolish it.
[Feature(Features.Demolish, Features.DemolishInfo)]
[HarmonyPatch(typeof(Brix.Utils.UI.KeyBindingsUtility), nameof(Brix.Utils.UI.KeyBindingsUtility.RegisterAction))]
internal static class DemolishKeyActionPatch
{
	private static void Prefix(ref Action<Rewired.InputActionEventData> action)
	{
		Action<Rewired.InputActionEventData> inner = action;
		action = (Rewired.InputActionEventData data) =>
		{
			if (!Demolish.KeyClaimed())
			{
				inner(data);
			}
		};
	}
}

// The builder that takes a marked building's demolition blueprint is the one to carry its materials away.
[Feature(Features.Demolish, Features.DemolishInfo)]
[HarmonyPatch(typeof(BuildGoal), nameof(BuildGoal.GetWorkNode))]
internal static class DemolishWorkPatch
{
	private static void Postfix(BuildGoal __instance, Labor labor)
	{
		Demolish.OnWork(__instance, labor);
	}
}

// A hit on a marked building: one by another faction forfeits its refund.
[Feature(Features.Demolish, Features.DemolishInfo)]
[HarmonyPatch(typeof(ObjectDamageReceiver), nameof(ObjectDamageReceiver.ReceiveAttack))]
internal static class DemolishAttackPatch
{
	private static void Prefix(ObjectDamageReceiver __instance, GameObject attacker)
	{
		if (__instance != null)
		{
			Demolish.OnAttack(__instance.gameObject, attacker);
		}
	}
}

// Before a worker of a task is given its work, the game checks that it can find one of the things the task's
// blueprints need (Project.VerifyWorkFor). A demolition blueprint needs nothing, but the game counts its building's
// whole cost among those things (BuildGoalProvider.UniqueItemsNeeded reads every blueprint's base capacity), so with
// none of those materials to be had the builders of the task were never given the demolition: they stood idle in
// the task. A builder that can break things passes the check when a demolition blueprint of the task is ready and no
// other builder is on it (as FreeBlueprintsVerifyPatch does for free blueprints).
[Feature(Features.Demolish, Features.DemolishInfo)]
[HarmonyPatch(typeof(Project), nameof(Project.VerifyWorkFor))]
internal static class DemolishVerifyPatch
{
	private static bool Prefix(Project __instance, Labor labor, ref Node __result, bool __runOriginal)
	{
		if (!__runOriginal || !(__instance is BuildProject) || labor.IsNullOrReleased() || labor.Profession == null || !labor.Profession.CanBreakVoxels())
		{
			return __runOriginal;
		}
		BuildGoalProvider provider = __instance.GetComponent<BuildGoalProvider>();
		if (provider == null)
		{
			return true;
		}
		foreach (Blueprint blueprint in provider.TrackedBlueprints)
		{
			if (blueprint == null || blueprint.Released || !blueprint.IsAnti || !blueprint.State.HasFlag(PlacementFlags.valid) || !blueprint.State.HasFlag(PlacementFlags.buildable))
			{
				continue;
			}
			BuildGoal goal = blueprint.GetComponent<BuildGoal>();
			if (goal != null && goal.Commited && !goal.HasOtherWorkerAssigned(labor))
			{
				__result = Node.Empty;
				return false;
			}
		}
		return true;
	}
}

// What a marked building holds, noted before StockpileSpill (or the game) drops it.
[Feature(Features.Demolish, Features.DemolishInfo)]
[HarmonyPatch(typeof(ObjectDamageReceiver), nameof(ObjectDamageReceiver.Kill))]
[HarmonyPriority(Priority.First)]
internal static class DemolishKillPatch
{
	private static void Prefix(ObjectDamageReceiver __instance)
	{
		if (__instance != null)
		{
			Demolish.OnKill(__instance.gameObject);
		}
	}
}

[Feature(Features.Demolish, Features.DemolishInfo)]
[HarmonyPatch(typeof(ObjectPoolSingleton), nameof(ObjectPoolSingleton.Release), typeof(FactoryImprint))]
internal static class DemolishReleasePatch
{
	private static void Prefix(FactoryImprint imprint)
	{
		if (imprint != null && !imprint.Released)
		{
			Demolish.OnRelease(imprint.gameObject);
		}
	}
}

// Idle workers carry a demolition's leftovers first among the idle chores (they were ordered by the player), before
// the cleanup of loose items (AutoCleanup) and emptying stockpiles. A worker carrying such a load keeps doing it.
[Feature(Features.Demolish, Features.DemolishInfo)]
[HarmonyPatch(typeof(IdleProject), nameof(IdleProject._GetInstruction))]
[HarmonyPriority(Priority.VeryHigh)]
internal static class DemolishIdlePatch
{
	private static bool Prefix(IdleProject __instance, Labor labor, Var<OrderPackage<Labor>> result, bool __runOriginal)
	{
		if (!__runOriginal || labor == null || (DemolishHaul.Count == 0 && !DemolishHaul.IsCarrying(labor)))
		{
			return __runOriginal;
		}
		if (!labor.recepteur.IsEmpty())
		{
			if (!DemolishHaul.IsCarrying(labor))
			{
				return true;
			}
			result.Set(OrderPackage<Labor>.Empty);
			return false;
		}
		if (!__instance.GatherAtIdlePoint(labor) || !AssignedWork.IdleChoresFor(labor))
		{
			return true;
		}
		GameObject item = DemolishHaul.TryPlan(labor, labor.transform.position, out string _);
		if (item == null)
		{
			return true;
		}
		Plugin.Log.LogInfo("Demolish: idle " + labor.name + " carries demolition leftovers to storage, first " + item.name);
		OrderPackage<Labor> package = Order.Package.OfGameObject(DemolishHaul.Carry, item, Labor.DecisionSource.Project);
		// Interruptible until the worker picks something up, so real work always wins.
		package.SetPrepare((BaseLabor worker) => __instance.Prepare(worker as Labor, false, Activity.Chilling));
		result.Set(package);
		return false;
	}
}

// Saves the demolitions with the game.
[Feature(Features.Demolish, Features.DemolishInfo)]
[HarmonyPatch(typeof(Asset_Map), "Disk_Save")]
internal static class DemolishSavePatch
{
	private static void Postfix(Asset_Map __instance)
	{
		if (NetworkServer.active && !string.IsNullOrEmpty(__instance.path))
		{
			Demolish.WritePending(__instance.path);
		}
	}
}
