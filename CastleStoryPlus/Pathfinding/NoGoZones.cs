using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using Brix.Assets;
using Brix.Engine;
using Brix.Game;
using Brix.Game.Network;
using Brix.Network;
using CastleStoryPlus.Core;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Pathfinding;

// No-go zones for routes (#200, point 2): rectangles of voxel columns a player draws on the map (NoGoZonesTool) that
// the units of his team route round, without any wall. A zone changes the route's price, not where a unit can go: a
// unit walks right beside it, and one whose goal or start is inside, or that has no other way, still goes in.
//   Zones: per faction, as rectangles that do not overlap (NoGoRules.Add, Erase), every height of the columns. A unit
//   heeds the zones of its own faction and of the factions allied to it (its team); enemies and the AI ignore them.
//   Routes (Unity 6 port): the game's route search (SearchPathRequest's coarse-to-fine A*) pays NoGoRules.StepPenalty
//   for every step into and inside a zone (NoGoZonesSearchPatch); the navigation mesh has the zones as areas of their
//   own that cost a unit of the zone's team NoGoRules.MeshCost per metre (NavMeshExperiment, NavMeshRoutes); a
//   straight line pulled across open ground does not cut into a zone the route kept out of (StraightPaths).
//   Saved beside the world's save (castlestoryplus_nogo.json) by the host. Multiplayer: the host decides; a client's
//   drawing goes to the host as a command (its own faction's zones only), and the host sends every faction's zones to
//   the other players when they change and every few seconds (players who join get them then).
[Feature(Features.NoGoZones, Features.NoGoZonesInfo)]
internal sealed class NoGoZones : MonoBehaviour
{
	private const string SaveFile = "castlestoryplus_nogo.json";

	// The game's own messages end at 63; Castle Story Plus uses 200-216 (ModMessages).
	private const short ZonesMessage = 216;

	private const int EditHash = 1129595281;

	private const float BroadcastSeconds = 5f;

	// A team's merged zones are made again this often (alliances can change without a zone changing).
	private const float MapSeconds = 5f;

	internal const byte OpAdd = 1;

	internal const byte OpErase = 2;

	internal const byte OpClear = 3;

	internal static ConfigEntry<int> Strength;

	// The host's strength on a client (null on the host and in single player), so routes weigh zones alike.
	private static int? _hostStrength;

	// Every faction's zones (rectangles that do not overlap).
	private static readonly Dictionary<Faction, List<NoGoRect>> Zones = new Dictionary<Faction, List<NoGoRect>>();

	// Factions in the order their first zone came (their navigation mesh area, NavMeshArea).
	private static readonly List<Faction> Owners = new List<Faction>();

	// The merged zones of each team (by the faction asking), and when each was made.
	private static readonly Dictionary<Faction, NoGoMap> Maps = new Dictionary<Faction, NoGoMap>();

	private static readonly Dictionary<Faction, int> MapVersions = new Dictionary<Faction, int>();

	private static readonly Dictionary<Faction, float> MapTimes = new Dictionary<Faction, float>();

	private static int _version;

	private static bool _changed;

	private static float _nextBroadcast;

	private static NetworkClient _client;

	// Something to say when a drawing was refused (too many rectangles), for the tool.
	internal static string LastRefusal;

	// The zones changed (version), for the tool's drawing and the navigation mesh.
	internal static int Version => _version;

	internal static event Action<NoGoRect> Changed;

	private sealed class ZonesNetMessage : MessageBase
	{
		public int Strength;

		public readonly List<string> Names = new List<string>();

		public readonly List<List<NoGoRect>> Rects = new List<List<NoGoRect>>();

		public override void Serialize(NetworkWriter writer)
		{
			writer.WritePackedUInt32((uint)Strength);
			writer.WritePackedUInt32((uint)Names.Count);
			for (int i = 0; i < Names.Count; i++)
			{
				writer.Write(Names[i]);
				writer.WritePackedUInt32((uint)Rects[i].Count);
				foreach (NoGoRect rect in Rects[i])
				{
					writer.Write(rect.MinX);
					writer.Write(rect.MinZ);
					writer.Write(rect.MaxX);
					writer.Write(rect.MaxZ);
				}
			}
		}

		public override void Deserialize(NetworkReader reader)
		{
			Strength = (int)reader.ReadPackedUInt32();
			int count = (int)reader.ReadPackedUInt32();
			for (int i = 0; i < count; i++)
			{
				Names.Add(reader.ReadString());
				int rects = (int)reader.ReadPackedUInt32();
				List<NoGoRect> list = new List<NoGoRect>(rects);
				for (int j = 0; j < rects; j++)
				{
					int minX = reader.ReadInt32();
					int minZ = reader.ReadInt32();
					int maxX = reader.ReadInt32();
					int maxZ = reader.ReadInt32();
					list.Add(new NoGoRect(minX, minZ, maxX, maxZ));
				}
				Rects.Add(list);
			}
		}
	}

	private static void Enable()
	{
		Strength = Plugin.Cfg.Bind("NoGoZones", "Strength", NoGoRules.DefaultStrength, new ConfigDescription("About how many blocks longer a route may get to keep out of a no-go zone; a zone is never closed, only dear. In multiplayer the host's value counts.", new AcceptableValueRange<int>(NoGoRules.MinStrength, NoGoRules.MaxStrength)));
		Strength.SettingChanged += (object sender, EventArgs args) =>
		{
			// Routes read it at every search; the other players get it with the zones.
			_changed = true;
			_version++;
		};
		NetworkBehaviour.RegisterCommandDelegate(typeof(UNetProjectCmd), EditHash, InvokeEdit);
		GameSession.OnLevelReady(LevelReady);
		GameSession.OnLeave(Clear);
		Plugin.Root.AddComponent<NoGoZones>();
	}

	// On while the feature is (FeatureToggle switches this component with it).
	internal static bool Active => _on;

	private static bool _on;

	private void OnEnable()
	{
		_on = true;
		_version++;
	}

	private void OnDisable()
	{
		_on = false;
		_version++;
	}

	// The strength routes use: the host's on a client.
	internal static int CurrentStrength => NoGoRules.ClampStrength(_hostStrength ?? ((Strength != null) ? Strength.Value : NoGoRules.DefaultStrength));

	private static void Clear()
	{
		Zones.Clear();
		Owners.Clear();
		Maps.Clear();
		MapVersions.Clear();
		MapTimes.Clear();
		_hostStrength = null;
		_changed = false;
		_client = null;
		LastRefusal = null;
		_version++;
	}

	private static void LevelReady()
	{
		Clear();
		Asset_Map map = GameParam.Map;
		if (!NetworkServer.active || map == null || map.IsNewGame() || string.IsNullOrEmpty(map.path))
		{
			return;
		}
		string file = Path.Combine(map.path, SaveFile);
		if (!File.Exists(file))
		{
			return;
		}
		try
		{
			Dictionary<string, List<NoGoRect>> saved = NoGoRules.FromJson(JObject.Parse(File.ReadAllText(file)));
			int count = 0;
			foreach (KeyValuePair<string, List<NoGoRect>> entry in saved)
			{
				Faction faction = Faction.FindByName(entry.Key);
				if (faction == null)
				{
					Plugin.Log.LogWarning("NoGoZones: the save's faction " + entry.Key + " is not in this game; its zones are left out");
					continue;
				}
				Set(faction, entry.Value);
				count += entry.Value.Count;
			}
			Plugin.Log.LogInfo("NoGoZones: " + count + " zone rectangles from the save");
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("NoGoZones: could not read the zones: " + ex.Message);
		}
	}

	// Host: called when the game has written a save into its folder. A world without zones gets no file (one left from
	// before is emptied, so zones all erased stay erased).
	internal static void Write(string folder)
	{
		string file = Path.Combine(folder, SaveFile);
		try
		{
			if (Zones.Count == 0 && !File.Exists(file))
			{
				return;
			}
			List<KeyValuePair<string, List<NoGoRect>>> named = new List<KeyValuePair<string, List<NoGoRect>>>();
			foreach (KeyValuePair<Faction, List<NoGoRect>> entry in Zones)
			{
				if (entry.Key != null)
				{
					named.Add(new KeyValuePair<string, List<NoGoRect>>(entry.Key.name, entry.Value));
				}
			}
			File.WriteAllText(file, NoGoRules.ToJson(named).ToString(Newtonsoft.Json.Formatting.None));
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("NoGoZones: could not save the zones: " + ex.Message);
		}
	}

	// ---- the zones

	internal static IEnumerable<KeyValuePair<Faction, List<NoGoRect>>> All => Zones;

	internal static bool Any => Zones.Count > 0;

	internal static List<NoGoRect> Of(Faction faction)
	{
		return (faction != null && Zones.TryGetValue(faction, out List<NoGoRect> list)) ? list : null;
	}

	// The navigation mesh area slot (0 to NavMeshAreas - 1) of a faction's zones; factions after the first few share.
	internal static int Slot(Faction faction, int slots)
	{
		int index = Owners.IndexOf(faction);
		return (index < 0 || slots <= 0) ? 0 : index % slots;
	}

	// The faction owning a slot's zones that a unit of this faction heeds (its own or an ally's): true when any does.
	internal static bool HeedsSlot(Faction unit, int slot, int slots)
	{
		if (unit == null || unit.isAI)
		{
			return false;
		}
		for (int i = 0; i < Owners.Count; i++)
		{
			if (i % slots == slot && Heeds(unit, Owners[i]))
			{
				return true;
			}
		}
		return false;
	}

	// Whether a unit of the faction heeds the owner's zones: its own and its allies'.
	internal static bool Heeds(Faction unit, Faction owner)
	{
		if (unit == null || owner == null || unit.isAI)
		{
			return false;
		}
		return unit == owner || Affiliation.IsAllied(unit, owner);
	}

	// The zones a unit of the faction routes round (its team's), or null when there are none.
	internal static NoGoMap MapFor(Faction faction)
	{
		if (Zones.Count == 0 || faction == null || faction.isAI || !Active)
		{
			return null;
		}
		if (Maps.TryGetValue(faction, out NoGoMap cached) && MapVersions.TryGetValue(faction, out int version) && version == _version && MapTimes.TryGetValue(faction, out float at) && Time.time - at < MapSeconds)
		{
			return cached;
		}
		NoGoMap map = new NoGoMap();
		foreach (KeyValuePair<Faction, List<NoGoRect>> entry in Zones)
		{
			if (!Heeds(faction, entry.Key))
			{
				continue;
			}
			foreach (NoGoRect rect in entry.Value)
			{
				map.Add(rect);
			}
		}
		if (map.IsEmpty)
		{
			map = null;
		}
		Maps[faction] = map;
		MapVersions[faction] = _version;
		MapTimes[faction] = Time.time;
		return map;
	}

	// The faction's zones replaced (the save, the host's message).
	private static void Set(Faction faction, List<NoGoRect> rects)
	{
		List<NoGoRect> before = Of(faction);
		if (before != null)
		{
			foreach (NoGoRect rect in before)
			{
				Touch(rect);
			}
		}
		if (rects == null || rects.Count == 0)
		{
			Zones.Remove(faction);
		}
		else
		{
			Zones[faction] = new List<NoGoRect>(rects);
			if (!Owners.Contains(faction))
			{
				Owners.Add(faction);
			}
			foreach (NoGoRect rect in rects)
			{
				Touch(rect);
			}
		}
	}

	private static void Touch(NoGoRect rect)
	{
		_version++;
		try
		{
			Changed?.Invoke(rect);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("NoGoZones: " + ex.Message);
		}
	}

	// Host (or single player): a change to the faction's zones. False when nothing changed or it was refused.
	internal static bool Apply(Faction faction, byte op, NoGoRect rect)
	{
		if (faction == null || faction.isAI || faction.IsSpectator)
		{
			return false;
		}
		List<NoGoRect> list = Of(faction);
		bool changed;
		switch (op)
		{
			case OpAdd:
				if (list == null)
				{
					list = new List<NoGoRect>();
				}
				changed = NoGoRules.Add(list, rect);
				if (!changed)
				{
					LastRefusal = "Too many no-go zones (at most " + NoGoRules.MaxRects + " rectangles); erase some first";
					return false;
				}
				Zones[faction] = list;
				if (!Owners.Contains(faction))
				{
					Owners.Add(faction);
				}
				Touch(rect);
				break;
			case OpErase:
				if (list == null || !NoGoRules.Erase(list, rect))
				{
					return false;
				}
				if (list.Count == 0)
				{
					Zones.Remove(faction);
				}
				Touch(rect);
				break;
			case OpClear:
				if (list == null)
				{
					return false;
				}
				Set(faction, null);
				break;
			default:
				return false;
		}
		LastRefusal = null;
		if (NetworkServer.active)
		{
			_changed = true;
		}
		return true;
	}

	// The local player: draws or erases a zone of his faction, here (host, single player) or on the host (client).
	internal static void Request(byte op, NoGoRect rect)
	{
		Faction faction = (User.LocalUser != null) ? User.LocalUser.faction : null;
		if (faction == null)
		{
			return;
		}
		UNetProjectCmd cmd = User.LocalUser.GetComponent<UNetProjectCmd>();
		if (NetworkServer.active || cmd == null || cmd.isServer)
		{
			Apply(faction, op, rect);
			return;
		}
		// Shown at once; the host's answer replaces it.
		ApplyLocally(faction, op, rect);
		NetworkWriter writer = new NetworkWriter();
		writer.Write((short)0);
		writer.Write((short)5);
		writer.WritePackedUInt32((uint)EditHash);
		writer.Write(cmd.GetComponent<NetworkIdentity>().netId);
		writer.Write(op);
		writer.Write(rect.MinX);
		writer.Write(rect.MinZ);
		writer.Write(rect.MaxX);
		writer.Write(rect.MaxZ);
		cmd.SendCommandInternal(writer, 0, "CmdNoGoZoneEdit");
	}

	private static void ApplyLocally(Faction faction, byte op, NoGoRect rect)
	{
		List<NoGoRect> list = new List<NoGoRect>(Of(faction) ?? new List<NoGoRect>());
		if (op == OpAdd)
		{
			NoGoRules.Add(list, rect);
		}
		else if (op == OpErase)
		{
			NoGoRules.Erase(list, rect);
		}
		else if (op == OpClear)
		{
			list.Clear();
		}
		Set(faction, list);
	}

	// Host: a client's drawing, for his own faction only.
	private static void InvokeEdit(NetworkBehaviour obj, NetworkReader reader)
	{
		if (!NetworkServer.active || !Active)
		{
			return;
		}
		byte op = reader.ReadByte();
		int x0 = reader.ReadInt32();
		int z0 = reader.ReadInt32();
		int x1 = reader.ReadInt32();
		int z1 = reader.ReadInt32();
		User user = obj.GetComponent<User>();
		Faction faction = (user != null) ? user.faction : null;
		if (faction == null)
		{
			return;
		}
		if (!Apply(faction, op, NoGoRules.Drawn(x0, z0, x1, z1)))
		{
			// The client showed it already: it gets the host's zones back.
			_changed = true;
		}
	}

	// ---- the host's zones to the other players

	private void Update()
	{
		try
		{
			Sync();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("NoGoZones: " + ex.Message);
		}
	}

	private static void Sync()
	{
		NetworkClient client = (UNetManager.instance != null) ? UNetManager.instance.LocalNetworkClient : null;
		if (client != null && client != _client)
		{
			_client = client;
			if (client.handlers.ContainsKey(ZonesMessage))
			{
				client.UnregisterHandler(ZonesMessage);
			}
			client.RegisterHandler(ZonesMessage, Received);
		}
		if (!NetworkServer.active || !Neo.multiplayer)
		{
			return;
		}
		if (!_changed && Time.unscaledTime < _nextBroadcast)
		{
			return;
		}
		_changed = false;
		_nextBroadcast = Time.unscaledTime + BroadcastSeconds;
		ZonesNetMessage message = new ZonesNetMessage { Strength = CurrentStrength };
		foreach (KeyValuePair<Faction, List<NoGoRect>> entry in Zones)
		{
			if (entry.Key != null)
			{
				message.Names.Add(entry.Key.name);
				message.Rects.Add(entry.Value);
			}
		}
		NetworkServer.SendToAll(ZonesMessage, message);
	}

	// A client: the host's zones replace its own.
	private static void Received(NetworkMessage netMsg)
	{
		if (NetworkServer.active)
		{
			return;
		}
		ZonesNetMessage message = netMsg.ReadMessage<ZonesNetMessage>();
		int strength = NoGoRules.ClampStrength(message.Strength);
		if (_hostStrength != strength)
		{
			_hostStrength = strength;
			_version++;
		}
		HashSet<Faction> seen = new HashSet<Faction>();
		for (int i = 0; i < message.Names.Count; i++)
		{
			Faction faction = Faction.FindByName(message.Names[i]);
			if (faction == null)
			{
				continue;
			}
			seen.Add(faction);
			List<NoGoRect> now = Of(faction);
			if (now == null || !Same(now, message.Rects[i]))
			{
				Set(faction, message.Rects[i]);
			}
		}
		foreach (Faction faction in new List<Faction>(Zones.Keys))
		{
			if (!seen.Contains(faction))
			{
				Set(faction, null);
			}
		}
	}

	private static bool Same(List<NoGoRect> a, List<NoGoRect> b)
	{
		if (a.Count != b.Count)
		{
			return false;
		}
		for (int i = 0; i < a.Count; i++)
		{
			if (!a[i].Equals(b[i]))
			{
				return false;
			}
		}
		return true;
	}
}

// Saves the zones with the game.
[Feature(Features.NoGoZones, Features.NoGoZonesInfo)]
[HarmonyPatch(typeof(Asset_Map), "Disk_Save")]
internal static class NoGoZonesSavePatch
{
	private static void Postfix(Asset_Map __instance)
	{
		if (NetworkServer.active && !string.IsNullOrEmpty(__instance.path))
		{
			NoGoZones.Write(__instance.path);
		}
	}
}
