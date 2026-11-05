using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace UnityEngine.Networking
{
	// Connections of the low-level API over a relay that carries packets to a peer by its id (the mod sets it up with
	// Steam's peer-to-peer networking, which goes through Valve's relays when the two cannot reach each other, so no
	// port has to be opened). The game connects to such a peer as to an address: a made-up IPv6 address holding the
	// id (RelayAddress), which UNet's high-level API passes on without looking it up. On top of the relay's packets
	// this keeps UNet's connections: a connect asks the host listening on the port, which accepts or turns it away,
	// data goes on the connection's channel (reliable in order, or unreliable for the unreliable channels), pings
	// give the round trip time and keep the connection alive, and one that is not heard from for 15 s is lost.
	// Packets to the side that hosts and to the side that connected travel apart (toServer), and a computer only
	// takes in the side it has connections of: two copies of the game signed in to the same Steam account (one
	// computer, as when testing) then each get their own packets.
	public static class LlapiRelay
	{
		public delegate bool SendPacket(ulong peer, byte[] data, int size, bool reliable, bool toServer);

		public delegate bool ReceivePacket(byte[] buffer, out int size, out ulong peer, bool toServer);

		// fd53:7465:616d ("Steam") in the unique local range; the id fills the last four groups.
		private const string Prefix = "fd53:7465:616d:0:";

		private const byte KindConnect = 1;

		private const byte KindAccept = 2;

		private const byte KindReject = 3;

		private const byte KindData = 4;

		private const byte KindDisconnect = 5;

		private const byte KindPing = 6;

		private const byte KindPong = 7;

		private const byte Protocol = 1;

		// kind, the receiver's token, the sender's token, the channel.
		private const int Header = 10;

		private const int MaxPacketsPerPoll = 2000;

		private const double ConnectSeconds = 10.0;

		private const double LostSeconds = 15.0;

		private const double PingSeconds = 1.0;

		// Unreliable packets above this go reliable (Steam's unreliable packets are at most 1200 bytes).
		internal const int MaxUnreliable = 1100;

		internal sealed class Link
		{
			public LlapiHost Host;

			public int Id;

			public ulong Peer;

			public int Token;

			public int RemoteToken;

			public bool Connected;

			// Made by Connect here (the peer hosts), not accepted from a peer.
			public bool Outgoing;

			public int Port;

			public double Started;

			public double LastHeard;

			public double LastPing;

			public int RoundTrip;
		}

		private static readonly Dictionary<int, Link> Links = new Dictionary<int, Link>();

		private static readonly List<LlapiHost> Listeners = new List<LlapiHost>();

		private static readonly Stopwatch Clock = Stopwatch.StartNew();

		private static byte[] receiveBuffer = new byte[1 << 20];

		// Tokens start somewhere new each run, so packets of an earlier session never match a new connection.
		private static int nextToken = new System.Random().Next(1, int.MaxValue / 2);

		private static int lastPollFrame = -1;

		// Set by the mod when the relay can be used (Steam signed in); null otherwise.
		public static SendPacket Send;

		public static ReceivePacket Receive;

		// The relay may let go of a peer it no longer has connections with.
		public static Action<ulong> Release;

		public static bool Available => Send != null && Receive != null;

		private static double Now => Clock.Elapsed.TotalSeconds;

		public static string RelayAddress(ulong id)
		{
			return Prefix + ((id >> 48) & 0xffff).ToString("x") + ":" + ((id >> 32) & 0xffff).ToString("x") + ":" + ((id >> 16) & 0xffff).ToString("x") + ":" + (id & 0xffff).ToString("x");
		}

		public static bool TryParse(string address, out ulong id)
		{
			id = 0;
			if (string.IsNullOrEmpty(address) || !address.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			string[] groups = address.Substring(Prefix.Length).Split(':');
			if (groups.Length != 4)
			{
				return false;
			}
			foreach (string group in groups)
			{
				if (!ushort.TryParse(group, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort part))
				{
					return false;
				}
				id = (id << 16) | part;
			}
			return true;
		}

		// ---- for LlapiHost

		internal static void Listen(LlapiHost host)
		{
			if (!Listeners.Contains(host))
			{
				Listeners.Add(host);
			}
		}

		internal static void Unlisten(LlapiHost host)
		{
			Listeners.Remove(host);
			List<int> gone = new List<int>();
			foreach (KeyValuePair<int, Link> pair in Links)
			{
				if (pair.Value.Host == host)
				{
					gone.Add(pair.Key);
				}
			}
			foreach (int token in gone)
			{
				Link link = Links[token];
				if (link.Connected)
				{
					SendControl(link, KindDisconnect);
				}
				Forget(link);
			}
		}

		internal static Link Connect(LlapiHost host, int id, ulong peer, int port)
		{
			Link link = new Link { Host = host, Id = id, Peer = peer, Token = nextToken++, Port = port, Started = Now, LastHeard = Now, Outgoing = true };
			Links[link.Token] = link;
			byte[] packet = new byte[Header + 3];
			Write(packet, KindConnect, 0, link.Token, 0);
			packet[Header] = (byte)port;
			packet[Header + 1] = (byte)(port >> 8);
			packet[Header + 2] = Protocol;
			Send(peer, packet, packet.Length, true, true);
			return link;
		}

		internal static bool SendData(Link link, int channel, byte[] buffer, int size, bool reliable)
		{
			if (!link.Connected || !Available)
			{
				return false;
			}
			byte[] packet = new byte[Header + size];
			Write(packet, KindData, link.RemoteToken, link.Token, (byte)channel);
			Buffer.BlockCopy(buffer, 0, packet, Header, size);
			return SendTo(link, packet, reliable || size > MaxUnreliable);
		}

		// The game closed the connection: the peer is told, and the host hears of it as UNet's own disconnect.
		internal static void Disconnect(Link link)
		{
			if (link.Connected)
			{
				SendControl(link, KindDisconnect);
			}
			Forget(link);
			link.Host.RelayClosed(link, NetworkError.Ok);
		}

		// Once a frame (from whichever host the game polls first): every packet that came in, then the pings and the
		// connections that are lost or never answered.
		internal static void Poll()
		{
			int frame = Time.frameCount;
			if (frame == lastPollFrame || !Available)
			{
				return;
			}
			lastPollFrame = frame;
			if (Listeners.Count > 0)
			{
				ReceiveAll(true);
			}
			foreach (Link link in Links.Values)
			{
				if (link.Outgoing)
				{
					ReceiveAll(false);
					break;
				}
			}
			double now = Now;
			List<Link> lost = null;
			foreach (Link link in Links.Values)
			{
				if (!link.Connected)
				{
					if (now - link.Started > ConnectSeconds)
					{
						(lost = lost ?? new List<Link>()).Add(link);
					}
				}
				else if (now - link.LastHeard > LostSeconds)
				{
					(lost = lost ?? new List<Link>()).Add(link);
				}
				else if (now - link.LastPing >= PingSeconds)
				{
					link.LastPing = now;
					byte[] ping = new byte[Header + 8];
					Write(ping, KindPing, link.RemoteToken, link.Token, 0);
					WriteLong(ping, Header, BitConverter.DoubleToInt64Bits(now));
					SendTo(link, ping, false);
				}
			}
			if (lost != null)
			{
				foreach (Link link in lost)
				{
					Forget(link);
					link.Host.RelayClosed(link, NetworkError.Timeout);
				}
			}
		}

		private static void ReceiveAll(bool toServer)
		{
			for (int i = 0; i < MaxPacketsPerPoll && Receive(receiveBuffer, out int size, out ulong peer, toServer); i++)
			{
				if (size >= Header)
				{
					Handle(peer, receiveBuffer, size);
				}
			}
		}

		private static void Handle(ulong peer, byte[] packet, int size)
		{
			byte kind = packet[0];
			int target = ReadInt(packet, 1);
			int sender = ReadInt(packet, 5);
			if (kind == KindConnect)
			{
				Accept(peer, sender, packet, size);
				return;
			}
			if (!Links.TryGetValue(target, out Link link) || link.Peer != peer)
			{
				return;
			}
			link.LastHeard = Now;
			switch (kind)
			{
				case KindAccept:
					if (!link.Connected)
					{
						link.RemoteToken = sender;
						link.Connected = true;
						link.Host.RelayConnected(link);
					}
					break;
				case KindReject:
					if (!link.Connected)
					{
						Forget(link);
						link.Host.RelayClosed(link, NetworkError.Timeout);
					}
					break;
				case KindData:
					if (link.Connected && sender == link.RemoteToken)
					{
						byte[] data = new byte[size - Header];
						Buffer.BlockCopy(packet, Header, data, 0, data.Length);
						link.Host.RelayData(link, packet[9], data);
					}
					break;
				case KindDisconnect:
					if (sender == link.RemoteToken)
					{
						Forget(link);
						link.Host.RelayClosed(link, NetworkError.Ok);
					}
					break;
				case KindPing:
					if (size >= Header + 8)
					{
						byte[] pong = new byte[Header + 8];
						Write(pong, KindPong, link.RemoteToken, link.Token, 0);
						Buffer.BlockCopy(packet, Header, pong, Header, 8);
						SendTo(link, pong, false);
					}
					break;
				case KindPong:
					if (size >= Header + 8)
					{
						double sent = BitConverter.Int64BitsToDouble(ReadLong(packet, Header));
						link.RoundTrip = (int)Math.Round((Now - sent) * 1000.0);
					}
					break;
			}
		}

		// A peer asks to connect to the host listening on a port.
		private static void Accept(ulong peer, int remoteToken, byte[] packet, int size)
		{
			if (size < Header + 3)
			{
				return;
			}
			int port = packet[Header] | (packet[Header + 1] << 8);
			foreach (Link known in Links.Values)
			{
				if (known.Peer == peer && known.RemoteToken == remoteToken && known.Connected)
				{
					// The answer was lost: it is sent again.
					SendControl(known, KindAccept);
					return;
				}
			}
			LlapiHost host = Listeners.Find(h => h.Port == port);
			Link link = new Link { Peer = peer, RemoteToken = remoteToken, Token = nextToken++, Port = port, Started = Now, LastHeard = Now, Connected = true };
			if (packet[Header + 2] != Protocol || host == null || !host.RelayAccept(link))
			{
				byte[] reject = new byte[Header];
				Write(reject, KindReject, remoteToken, 0, 0);
				Send(peer, reject, reject.Length, true, false);
				return;
			}
			link.Host = host;
			Links[link.Token] = link;
			SendControl(link, KindAccept);
		}

		private static void SendControl(Link link, byte kind)
		{
			byte[] packet = new byte[Header];
			Write(packet, kind, link.RemoteToken, link.Token, 0);
			SendTo(link, packet, true);
		}

		private static bool SendTo(Link link, byte[] packet, bool reliable)
		{
			return Send(link.Peer, packet, packet.Length, reliable, link.Outgoing);
		}

		private static void Forget(Link link)
		{
			Links.Remove(link.Token);
			foreach (Link other in Links.Values)
			{
				if (other.Peer == link.Peer)
				{
					return;
				}
			}
			Release?.Invoke(link.Peer);
		}

		private static void Write(byte[] packet, byte kind, int target, int sender, byte channel)
		{
			packet[0] = kind;
			WriteInt(packet, 1, target);
			WriteInt(packet, 5, sender);
			packet[9] = channel;
		}

		private static void WriteInt(byte[] buffer, int offset, int value)
		{
			buffer[offset] = (byte)value;
			buffer[offset + 1] = (byte)(value >> 8);
			buffer[offset + 2] = (byte)(value >> 16);
			buffer[offset + 3] = (byte)(value >> 24);
		}

		private static int ReadInt(byte[] buffer, int offset)
		{
			return buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24);
		}

		private static void WriteLong(byte[] buffer, int offset, long value)
		{
			WriteInt(buffer, offset, (int)value);
			WriteInt(buffer, offset + 4, (int)(value >> 32));
		}

		private static long ReadLong(byte[] buffer, int offset)
		{
			return (uint)ReadInt(buffer, offset) | ((long)ReadInt(buffer, offset + 4) << 32);
		}
	}
}
