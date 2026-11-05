#if UNITY6
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using BepInEx.Configuration;
using Brix.Engine;
using Brix.Game;
using Brix.Network;
using CastleStoryPlus.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Multiplayer;

// Desync protection for multiplayer: the players' worlds are compared now and then, and where they differ the
// majority's world is taken, the host's when the vote is even. The host simulates the bricktrons, their work and
// what they carry and sends it out, so those cannot differ; but every peer changes its own copy of the world: a
// structure that is no longer held up falls apart on each peer by what that peer's world says, terrain damage and
// some removals are only made where they happen, a batch of removed blocks that cannot be read is dropped, and a
// player who joins later never hears of blocks removed before. Once the worlds differ they stay different.
//
// Every [Multiplayer] DesyncCheckSeconds the host asks for the hash of every chunk of the world (WorldChunks: 16 x 16
// columns, its terrain changes and placed blocks); each peer hashes its chunks a few at a time, every frame, and
// leaves out the chunks that changed in the last seconds (a change may still be on its way to the others). A chunk
// whose hashes differ twice in a row, while no peer's hash of it changed in between, is out of sync. The peers vote
// with their hash: the most votes win, and the host's hash when the votes are even. The losers get the winners'
// snapshot of the chunk and make their chunk match it; when the host lost, it first takes the snapshot of a winner.
// A corrected peer is told in the chat, and the log has every correction. Peers without this feature do not answer
// and are left out of the vote.
[Feature(Features.DesyncProtection, Features.DesyncProtectionInfo)]
internal sealed class DesyncProtection : MonoBehaviour
{
	// The game's own messages end at 63; Castle Story Plus uses 200 and 201 (WildPlants, MarketQueue).
	private const short ToClients = 202;

	private const short ToHost = 203;

	private const byte KindHashRequest = 1;

	private const byte KindHashes = 2;

	private const byte KindSnapshotRequest = 3;

	private const byte KindSnapshots = 4;

	// Message parts stay under the size the game's own reliable channel takes in one packet.
	private const int PartBytes = 1000;

	// A chunk changed this recently is left out of the vote.
	private const float QuietSeconds = 4f;

	// Hashing is spread over frames: the whole world once in half the time between two comparisons, and never more
	// than this long a frame.
	private const double BudgetMilliseconds = 1.0;

	private const float AnswerSeconds = 8f;

	private static ConfigEntry<float> _interval;

	private static DesyncProtection _instance;

	// ---- every peer: its own hashes, kept up to date a few chunks a frame.
	private readonly Dictionary<int, ulong> _hashes = new Dictionary<int, ulong>();

	private readonly Dictionary<int, float> _lastChange = new Dictionary<int, float>();

	private readonly Dictionary<int, float> _hashedAt = new Dictionary<int, float>();

	private List<int> _sweep = new List<int>();

	private int _sweepAt;

	// Chunks due to be hashed (a fraction of the world each second).
	private float _due;

	private bool _active;

	private readonly List<Brix.External.Signals.SignalConnection> _connections = new List<Brix.External.Signals.SignalConnection>();

	// Message parts on their way in, by sender (connection id; -1 the host) and transfer.
	private readonly Dictionary<long, byte[][]> _incoming = new Dictionary<long, byte[][]>();

	private int _nextTransfer;

	// ---- the host: the rounds of the vote.
	private sealed class Round
	{
		public int Id;

		public float Started;

		public readonly Dictionary<int, Dictionary<int, ulong>> Hashes = new Dictionary<int, Dictionary<int, ulong>>();

		public readonly HashSet<int> Asked = new HashSet<int>();

		public bool Done;
	}

	private Round _round;

	private int _nextRound = 1;

	private float _nextCheck;

	// Chunks out of sync in the last round, with every voter's hash then.
	private Dictionary<int, Dictionary<int, ulong>> _suspects = new Dictionary<int, Dictionary<int, ulong>>();

	// Snapshots the host asked a winner for: chunk -> the connections that lost the vote.
	private readonly Dictionary<int, List<int>> _waitingFor = new Dictionary<int, List<int>>();

	private const int HostVoter = -1;

	// For DevTools: rounds voted, chunks found out of sync, chunks this peer corrected, the last vote's outcome.
	internal static int Rounds;

	internal static int OutOfSync;

	internal static int Corrected;

	internal static string LastVote = string.Empty;

	// Time spent hashing and frames that hashed, since the game started.
	internal static double HashMilliseconds;

	internal static long HashFrames;

	internal static int HashedChunks => _instance != null ? _instance._hashes.Count : 0;

	internal static int Suspects => _instance != null ? _instance._suspects.Count : 0;

	// The host asks for the hashes at once (as soon as the round before is done).
	internal static void CheckNow()
	{
		if (_instance != null)
		{
			_instance._nextCheck = 0f;
		}
	}

	private static void Enable()
	{
		_interval = Plugin.Cfg.Bind("Multiplayer", "DesyncCheckSeconds", 10f, new ConfigDescription("Seconds between two comparisons of the players' worlds in a multiplayer game (the host asks; 0: no comparisons). A chunk is corrected after it differed in two comparisons in a row.", new AcceptableValueRange<float>(0f, 600f)));
		_instance = Plugin.Root.AddComponent<DesyncProtection>();
		GameSession.OnLevelReady(() => _instance.Begin());
		GameSession.OnLeave(() => _instance.End());
	}

	private void Begin()
	{
		End();
		NetworkClient client = UNetManager.instance?.LocalNetworkClient;
		if (client == null && !NetworkServer.active)
		{
			return;
		}
		if (client != null)
		{
			if (client.handlers.ContainsKey(ToClients))
			{
				client.UnregisterHandler(ToClients);
			}
			client.RegisterHandler(ToClients, msg => Received(msg, HostVoter));
		}
		if (NetworkServer.active)
		{
			NetworkServer.UnregisterHandler(ToHost);
			NetworkServer.RegisterHandler(ToHost, msg => Received(msg, msg.conn.connectionId));
		}
		_active = true;
		_nextCheck = Time.realtimeSinceStartup + Math.Max(_interval.Value, 5f);
		_connections.Add(GameSignals.Connect(GameSignals.VoxelChanged, Changed));
		_connections.Add(GameSignals.Connect(GameSignals.BlockTypeChanged, Changed));
	}

	private void End()
	{
		foreach (Brix.External.Signals.SignalConnection connection in _connections)
		{
			connection.Disconnect();
		}
		_connections.Clear();
		_active = false;
		_hashes.Clear();
		_lastChange.Clear();
		_hashedAt.Clear();
		_sweep.Clear();
		_incoming.Clear();
		_round = null;
		_suspects.Clear();
		_waitingFor.Clear();
	}

	private void Changed(XYZ p)
	{
		if (WorldChunks.Ready)
		{
			_lastChange[WorldChunks.KeyOf(p)] = Time.realtimeSinceStartup;
		}
	}

	private bool Multiplayer => NetworkServer.active ? NetworkServer.connections.Any(c => c != null && c.connectionId != 0) : UNetManager.instance?.LocalNetworkClient != null;

	private void Update()
	{
		if (!_active || _interval.Value <= 0f || !WorldChunks.Ready || !Multiplayer)
		{
			return;
		}
		try
		{
			HashSome();
			if (NetworkServer.active)
			{
				HostUpdate();
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("DesyncProtection: " + ex);
		}
	}

	// A few chunks each frame, round and round the world; the blocks are collected at the start of each sweep.
	private void HashSome()
	{
		int total = Math.Max(_sweep.Count, 1);
		float perSecond = total / Math.Max(_interval.Value * 0.5f, 2f);
		_due = Math.Min(_due + Time.unscaledDeltaTime * perSecond, perSecond);
		Stopwatch watch = Stopwatch.StartNew();
		while ((_due >= 1f || _sweep.Count == 0) && watch.Elapsed.TotalMilliseconds < BudgetMilliseconds)
		{
			_due -= 1f;
			if (_sweepAt >= _sweep.Count)
			{
				_sweep = WorldChunks.All();
				_sweepAt = 0;
				WorldChunks.CollectBlocks();
			}
			int key = _sweep[_sweepAt++];
			_hashes[key] = WorldChunks.Hash(key);
			_hashedAt[key] = Time.realtimeSinceStartup;
		}
		HashMilliseconds += watch.Elapsed.TotalMilliseconds;
		HashFrames++;
	}

	// The chunks whose hash is up to date and that did not change of late.
	private Dictionary<int, ulong> QuietHashes()
	{
		float now = Time.realtimeSinceStartup;
		Dictionary<int, ulong> quiet = new Dictionary<int, ulong>();
		foreach (KeyValuePair<int, ulong> pair in _hashes)
		{
			float changed = _lastChange.TryGetValue(pair.Key, out float at) ? at : -1000f;
			if (now - changed >= QuietSeconds && _hashedAt[pair.Key] >= changed + 0.5f)
			{
				quiet[pair.Key] = pair.Value;
			}
		}
		return quiet;
	}

	// ---- the host

	private void HostUpdate()
	{
		float now = Time.realtimeSinceStartup;
		if (_round != null && !_round.Done && (_round.Asked.All(c => _round.Hashes.ContainsKey(c)) || now - _round.Started > AnswerSeconds))
		{
			_round.Done = true;
			Vote(_round);
		}
		if (now < _nextCheck || (_round != null && !_round.Done))
		{
			return;
		}
		_nextCheck = now + _interval.Value;
		_round = new Round { Id = _nextRound++, Started = now };
		_round.Hashes[HostVoter] = QuietHashes();
		foreach (NetworkConnection conn in NetworkServer.connections)
		{
			if (conn != null && conn.connectionId != 0 && conn.isReady)
			{
				_round.Asked.Add(conn.connectionId);
				SendTo(conn, ToClients, KindHashRequest, w => w.Write(_round.Id));
			}
		}
	}

	private void Vote(Round round)
	{
		Rounds++;
		if (round.Hashes.Count < 2)
		{
			LastVote = "only the host answered";
			return;
		}
		Dictionary<int, Dictionary<int, ulong>> suspects = new Dictionary<int, Dictionary<int, ulong>>();
		HashSet<int> keys = new HashSet<int>();
		foreach (Dictionary<int, ulong> hashes in round.Hashes.Values)
		{
			keys.UnionWith(hashes.Keys);
		}
		// What each loser needs: chunk -> losers, and from which voter the truth comes.
		Dictionary<int, List<int>> fromHost = new Dictionary<int, List<int>>();
		int outOfSync = 0;
		foreach (int key in keys)
		{
			Dictionary<int, ulong> votes = new Dictionary<int, ulong>();
			foreach (KeyValuePair<int, Dictionary<int, ulong>> voter in round.Hashes)
			{
				if (voter.Value.TryGetValue(key, out ulong hash))
				{
					votes[voter.Key] = hash;
				}
			}
			if (votes.Count < 2 || votes.Values.Distinct().Count() == 1)
			{
				continue;
			}
			// Out of sync only when it was last round too, with the same hashes (nothing on its way).
			if (!_suspects.TryGetValue(key, out Dictionary<int, ulong> before) || !votes.All(v => before.TryGetValue(v.Key, out ulong old) && old == v.Value))
			{
				suspects[key] = votes;
				continue;
			}
			outOfSync++;
			ulong winner = Winner(votes);
			List<int> losers = votes.Where(v => v.Value != winner).Select(v => v.Key).ToList();
			Plugin.Log.LogInfo("DesyncProtection: chunk " + Describe(key) + " differs: " + string.Join(", ", votes.Select(v => Name(v.Key) + " " + v.Value.ToString("x16")).ToArray()) + "; " + (losers.Contains(HostVoter) ? "the host is outvoted" : "the host's world wins"));
			if (!losers.Contains(HostVoter))
			{
				foreach (int loser in losers)
				{
					GetOrAdd(fromHost, loser).Add(key);
				}
			}
			else
			{
				int from = votes.First(v => v.Value == winner).Key;
				_waitingFor[key] = losers;
				NetworkConnection conn = Connection(from);
				if (conn != null)
				{
					SendTo(conn, ToClients, KindSnapshotRequest, w =>
					{
						w.Write(1);
						w.Write(key);
					});
				}
			}
		}
		_suspects = suspects;
		OutOfSync += outOfSync;
		LastVote = round.Hashes.Count + " voters, " + keys.Count + " chunks, " + suspects.Count + " differing once, " + outOfSync + " out of sync";
		foreach (KeyValuePair<int, List<int>> loser in fromHost)
		{
			SendSnapshots(Connection(loser.Key), loser.Value);
		}
		if (outOfSync > 0)
		{
			IEnumerable<string> names = fromHost.Keys.Select(Name);
			Chat("World check: " + outOfSync + " area(s) of the world differed" + (fromHost.Count > 0 ? ", corrected for " + string.Join(", ", names.ToArray()) : string.Empty) + ".");
		}
	}

	// The hash with the most votes; when the votes are even, the host's.
	private static ulong Winner(Dictionary<int, ulong> votes)
	{
		Dictionary<ulong, int> counts = new Dictionary<ulong, int>();
		foreach (ulong hash in votes.Values)
		{
			counts[hash] = counts.TryGetValue(hash, out int n) ? n + 1 : 1;
		}
		int most = counts.Values.Max();
		List<ulong> best = counts.Where(c => c.Value == most).Select(c => c.Key).ToList();
		if (best.Count > 1 && votes.TryGetValue(HostVoter, out ulong host) && best.Contains(host))
		{
			return host;
		}
		return best.Min();
	}

	private void SendSnapshots(NetworkConnection conn, List<int> keys)
	{
		if (conn == null)
		{
			return;
		}
		SendTo(conn, ToClients, KindSnapshots, w =>
		{
			w.Write(keys.Count);
			foreach (int key in keys)
			{
				WorldChunks.Write(w, WorldChunks.Take(key));
			}
		});
	}

	// ---- messages

	private void Received(NetworkMessage message, int sender)
	{
		Part part = message.ReadMessage<Part>();
		long id = ((long)sender << 32) | (uint)part.Transfer;
		if (!_incoming.TryGetValue(id, out byte[][] parts) || parts.Length != part.Count)
		{
			parts = new byte[part.Count][];
			_incoming[id] = parts;
		}
		parts[part.Index] = part.Data;
		if (parts.Any(p => p == null))
		{
			return;
		}
		_incoming.Remove(id);
		byte[] packed = parts.SelectMany(p => p).ToArray();
		try
		{
			using (MemoryStream stream = new MemoryStream(packed))
			using (DeflateStream inflate = new DeflateStream(stream, CompressionMode.Decompress))
			using (BinaryReader reader = new BinaryReader(inflate))
			{
				Handle(part.Kind, reader, sender);
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("DesyncProtection: could not read a message from " + Name(sender) + ": " + ex.Message);
		}
	}

	private void Handle(byte kind, BinaryReader reader, int sender)
	{
		switch (kind)
		{
			case KindHashRequest when !NetworkServer.active:
				{
					int round = reader.ReadInt32();
					Dictionary<int, ulong> quiet = QuietHashes();
					SendToHost(KindHashes, w =>
					{
						w.Write(round);
						w.Write(quiet.Count);
						foreach (KeyValuePair<int, ulong> pair in quiet)
						{
							w.Write(pair.Key);
							w.Write(pair.Value);
						}
					});
					break;
				}
			case KindHashes when NetworkServer.active:
				{
					int round = reader.ReadInt32();
					int count = reader.ReadInt32();
					Dictionary<int, ulong> hashes = new Dictionary<int, ulong>(count);
					for (int i = 0; i < count; i++)
					{
						int key = reader.ReadInt32();
						hashes[key] = reader.ReadUInt64();
					}
					if (_round != null && _round.Id == round && !_round.Done)
					{
						_round.Hashes[sender] = hashes;
					}
					break;
				}
			case KindSnapshotRequest when !NetworkServer.active:
				{
					int count = reader.ReadInt32();
					List<int> keys = new List<int>();
					for (int i = 0; i < count; i++)
					{
						keys.Add(reader.ReadInt32());
					}
					SendToHost(KindSnapshots, w =>
					{
						w.Write(keys.Count);
						foreach (int key in keys)
						{
							WorldChunks.Write(w, WorldChunks.Take(key));
						}
					});
					break;
				}
			case KindSnapshots:
				{
					int count = reader.ReadInt32();
					int changed = 0;
					List<int> keys = new List<int>();
					List<string> where = new List<string>();
					for (int i = 0; i < count; i++)
					{
						WorldChunks.Snapshot snapshot = WorldChunks.Read(reader);
						changed += WorldChunks.Apply(snapshot);
						where.AddRange(WorldChunks.LastChanged.Select(p => p.ToString()));
						keys.Add(snapshot.Key);
						_hashes[snapshot.Key] = WorldChunks.Hash(snapshot.Key);
						_lastChange[snapshot.Key] = Time.realtimeSinceStartup;
					}
					Corrected += count;
					string who = NetworkServer.active ? "The host's" : "Your";
					Plugin.Log.LogInfo("DesyncProtection: " + who.ToLowerInvariant() + " world was corrected in " + count + " chunk(s) (" + changed + " voxels and blocks): " + string.Join(", ", keys.Select(Describe).ToArray()) + "; at " + string.Join(" ", where.Take(8).ToArray()));
					Chat(who + " world differed from the other players' in " + count + " area(s) and was corrected (" + changed + " voxels and blocks).");
					if (NetworkServer.active)
					{
						// The host took a winner's chunks: now the other losers get them from the host.
						Dictionary<int, List<int>> forward = new Dictionary<int, List<int>>();
						foreach (int key in keys)
						{
							if (_waitingFor.TryGetValue(key, out List<int> losers))
							{
								_waitingFor.Remove(key);
								foreach (int loser in losers.Where(l => l != HostVoter))
								{
									GetOrAdd(forward, loser).Add(key);
								}
							}
						}
						foreach (KeyValuePair<int, List<int>> loser in forward)
						{
							SendSnapshots(Connection(loser.Key), loser.Value);
						}
					}
					break;
				}
		}
	}

	private void SendToHost(byte kind, Action<BinaryWriter> write)
	{
		NetworkClient client = UNetManager.instance?.LocalNetworkClient;
		if (client != null && client.connection != null)
		{
			Send(client.connection, ToHost, kind, write);
		}
	}

	private void SendTo(NetworkConnection conn, short msgType, byte kind, Action<BinaryWriter> write)
	{
		Send(conn, msgType, kind, write);
	}

	// Deflated and cut into parts the reliable channel takes.
	private void Send(NetworkConnection conn, short msgType, byte kind, Action<BinaryWriter> write)
	{
		byte[] packed;
		using (MemoryStream stream = new MemoryStream())
		{
			using (DeflateStream deflate = new DeflateStream(stream, CompressionMode.Compress))
			using (BinaryWriter writer = new BinaryWriter(deflate))
			{
				write(writer);
			}
			packed = stream.ToArray();
		}
		int count = Math.Max(1, (packed.Length + PartBytes - 1) / PartBytes);
		int transfer = _nextTransfer++;
		for (int i = 0; i < count; i++)
		{
			int from = i * PartBytes;
			byte[] data = new byte[Math.Min(PartBytes, packed.Length - from)];
			Buffer.BlockCopy(packed, from, data, 0, data.Length);
			conn.Send(msgType, new Part { Kind = kind, Transfer = transfer, Index = (ushort)i, Count = (ushort)count, Data = data });
		}
	}

	private sealed class Part : MessageBase
	{
		public byte Kind;

		public int Transfer;

		public ushort Index;

		public ushort Count;

		public byte[] Data;

		public override void Serialize(NetworkWriter writer)
		{
			writer.Write(Kind);
			writer.Write(Transfer);
			writer.Write(Index);
			writer.Write(Count);
			writer.WriteBytesAndSize(Data, Data.Length);
		}

		public override void Deserialize(NetworkReader reader)
		{
			Kind = reader.ReadByte();
			Transfer = reader.ReadInt32();
			Index = reader.ReadUInt16();
			Count = reader.ReadUInt16();
			Data = reader.ReadBytesAndSize() ?? new byte[0];
		}
	}

	// ---- helpers

	private static NetworkConnection Connection(int id)
	{
		return NetworkServer.connections.FirstOrDefault(c => c != null && c.connectionId == id);
	}

	private static string Name(int voter)
	{
		if (voter == HostVoter)
		{
			return "the host";
		}
		User user = UNetManager.instance?.UserList?.FirstOrDefault(u => u != null && u.connectionToClient != null && u.connectionToClient.connectionId == voter);
		return (user != null && !string.IsNullOrEmpty(user.ScreenName)) ? user.ScreenName : "player " + voter;
	}

	private static string Describe(int key)
	{
		return "(" + (short)(key & 0xFFFF) + ", " + (key >> 16) + ")";
	}

	private static List<int> GetOrAdd(Dictionary<int, List<int>> map, int key)
	{
		if (!map.TryGetValue(key, out List<int> list))
		{
			list = new List<int>();
			map[key] = list;
		}
		return list;
	}

	private static void Chat(string text)
	{
		GameSignals.Invoke(UNetManager.LobbyNewChatMessage, "Castle Story Plus: " + text);
	}
}
#endif
