using System.Collections.Generic;
using Brix.Network;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Invasion;

// The scoreboard's waves (the enemies left of each wave, of as many as were seen) for the clients. They are counted
// from the enemy faction's raid projects and their crews, which only the host has: a raid is an object of the enemy
// faction that the game never sends to a player, so a client's scoreboard showed the kills but no wave. The host
// sends its rows (eleven numbers each: wave, then left and seen of minitrons, corruptrons, healtrons, biftrons and magitrons)
// when they change and every few seconds; a client shows the host's rows. The Healtrons killed (counted by the host
// only, the game counts them as corruptrons) go with them.
internal static class WaveCountsSync
{
	// The game's own messages end at 63; Castle Story Plus uses 200-205.
	private const short WavesMessage = 206;

	private const float ResendSeconds = 5f;

	// Numbers in a row: the wave, then what is left and what was seen of each of the five enemy kinds.
	internal const int RowLength = 11;

	// The payload's layout: 1 = the Healtrons killed before the rows.
	private const int Layout = 1;

	// The first number of a payload, with the row length and layout in it: a payload without it is from a version with
	// other rows and is ignored (its rows would show as wrong numbers).
	private const int Marker = 0x40000000 | (Layout << 8) | RowLength;

	private static NetworkClient _client;

	private static int[] _sent;

	private static float _nextSend;

	// The host's rows as last received on a client; null before any (or on the host).
	internal static int[] Rows { get; private set; }

	// The host's count of Healtrons killed as last received on a client; -1 before any (or on the host).
	internal static int HealtronKills { get; private set; } = -1;

	private static int _sentKills;

	private sealed class WavesNetMessage : MessageBase
	{
		public int[] Rows = new int[0];

		public override void Serialize(NetworkWriter writer)
		{
			writer.WritePackedUInt32((uint)Rows.Length);
			foreach (int value in Rows)
			{
				writer.WritePackedUInt32((uint)Mathf.Max(0, value));
			}
		}

		public override void Deserialize(NetworkReader reader)
		{
			int count = (int)reader.ReadPackedUInt32();
			Rows = new int[count];
			for (int i = 0; i < count; i++)
			{
				Rows[i] = (int)reader.ReadPackedUInt32();
			}
		}
	}

	// Every refresh of the scoreboard: the client's handler (once per connection).
	internal static void Listen()
	{
		NetworkClient client = (UNetManager.instance != null) ? UNetManager.instance.LocalNetworkClient : null;
		if (client == null || client == _client)
		{
			return;
		}
		_client = client;
		if (client.handlers.ContainsKey(WavesMessage))
		{
			client.UnregisterHandler(WavesMessage);
		}
		client.RegisterHandler(WavesMessage, Received);
	}

	// The host: its rows to the other players, when they changed or once in a while (for a player who joined).
	internal static void Send(int[] rows, int healtronKills)
	{
		if (!NetworkServer.active || !Brix.Engine.Neo.multiplayer)
		{
			return;
		}
		if (_sent != null && Same(_sent, rows) && _sentKills == healtronKills && Time.unscaledTime < _nextSend)
		{
			return;
		}
		_sent = rows;
		_sentKills = healtronKills;
		_nextSend = Time.unscaledTime + ResendSeconds;
		int[] payload = new int[rows.Length + 2];
		payload[0] = Marker;
		payload[1] = healtronKills;
		rows.CopyTo(payload, 2);
		NetworkServer.SendToAll(WavesMessage, new WavesNetMessage { Rows = payload });
	}

	internal static void Forget()
	{
		Rows = null;
		HealtronKills = -1;
		_sentKills = 0;
		_sent = null;
		_client = null;
	}

	private static void Received(NetworkMessage netMsg)
	{
		WavesNetMessage message = netMsg.ReadMessage<WavesNetMessage>();
		if (NetworkServer.active)
		{
			return;
		}
		if (message.Rows.Length < 2 || message.Rows[0] != Marker)
		{
			return;
		}
		int[] rows = new int[message.Rows.Length - 2];
		System.Array.Copy(message.Rows, 2, rows, 0, rows.Length);
		HealtronKills = message.Rows[1];
		Rows = rows;
	}

	private static bool Same(IList<int> a, IList<int> b)
	{
		if (a.Count != b.Count)
		{
			return false;
		}
		for (int i = 0; i < a.Count; i++)
		{
			if (a[i] != b[i])
			{
				return false;
			}
		}
		return true;
	}
}
