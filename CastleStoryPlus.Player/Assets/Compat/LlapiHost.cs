using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LiteNetLib;

namespace UnityEngine.Networking
{
	// One host of the low-level API (NetworkTransport.AddHost) on a LiteNetLib manager: a UDP socket with its
	// connections, the events waiting for ReceiveFromHost, and the LAN discovery broadcasts. LiteNetLib does the
	// reliable and sequenced delivery, the fragmenting and the round trip times on a thread of its own; the events are
	// handed to the game on the main thread, when it polls the host, as Unity 5.6's transport did.
	internal sealed class LlapiHost : INetEventListener
	{
		// Both ends must give the same key; a game of another version is turned away.
		private const string ConnectKey = "CastleStoryPlus-UNet-1";

		private const int MaxChannels = 64;

		private static readonly byte[] BroadcastMagic = { (byte)'C', (byte)'S', (byte)'P', (byte)'B' };

		private const int BroadcastHeader = 16;

		private struct Event
		{
			public NetworkEventType Type;

			public int Connection;

			public int Channel;

			public byte[] Data;

			public NetworkError Error;

			public IPEndPoint From;
		}

		private readonly NetManager manager;

		private readonly QosType[] channels;

		private readonly int maxConnections;

		private readonly Queue<Event> events = new Queue<Event>();

		// Connection ids as UNet hands them out: the lowest free one from 1 up (the game's server keeps its connections
		// in a list by id, and 0 is its local client).
		private readonly Dictionary<int, NetPeer> peers = new Dictionary<int, NetPeer>();

		// Connections over the relay (LlapiRelay), with ids from the same range.
		private readonly Dictionary<int, LlapiRelay.Link> relays = new Dictionary<int, LlapiRelay.Link>();

		private bool listensForBroadcasts;

		private int broadcastKey;

		private int broadcastVersion;

		private int broadcastSubversion;

		private Timer broadcastTimer;

		private IPEndPoint lastBroadcastFrom;

		private byte[] lastBroadcast;

		private LlapiHost(HostTopology topology)
		{
			ConnectionConfig config = topology.DefaultConfig;
			channels = new QosType[Math.Max(1, Math.Min(MaxChannels, config.ChannelCount))];
			for (int i = 0; i < config.ChannelCount && i < channels.Length; i++)
			{
				channels[i] = config.Channels[i].QOS;
			}
			maxConnections = Math.Max(1, topology.MaxDefaultConnections);
			manager = new NetManager(this)
			{
				ChannelsCount = (byte)channels.Length,
				AutoRecycle = true,
				IPv6Enabled = false,
				DisconnectTimeout = 10000,
				// Passed on to the game only once SetBroadcastCredentials made it a LAN discovery listener.
				BroadcastReceiveEnabled = true,
			};
		}

		// Null when the port cannot be had.
		internal static LlapiHost Open(HostTopology topology, int port, string ip)
		{
			LlapiHost host = new LlapiHost(topology);
			IPAddress address = IPAddress.Any;
			if (!string.IsNullOrEmpty(ip) && !IPAddress.TryParse(ip, out address))
			{
				address = IPAddress.Any;
			}
			bool started;
			try
			{
				started = host.manager.Start(address, IPAddress.IPv6Any, port);
			}
			catch (SocketException)
			{
				started = false;
			}
			if (!started)
			{
				host.manager.Stop();
				return null;
			}
			if (port != 0)
			{
				// A server's host: peers may also connect to it over the relay.
				LlapiRelay.Listen(host);
			}
			return host;
		}

		internal int Port => manager.LocalPort;

		internal void Close()
		{
			StopBroadcasting();
			LlapiRelay.Unlisten(this);
			manager.Stop(true);
			events.Clear();
			peers.Clear();
			relays.Clear();
		}

		internal int Connect(string address, int port, out NetworkError error)
		{
			if (LlapiRelay.TryParse(address, out ulong relayPeer))
			{
				return ConnectRelay(relayPeer, port, out error);
			}
			IPAddress ip;
			if (!IPAddress.TryParse(address, out ip))
			{
				try
				{
					ip = Array.Find(Dns.GetHostAddresses(address), a => a.AddressFamily == AddressFamily.InterNetwork);
				}
				catch (SocketException)
				{
					ip = null;
				}
				if (ip == null)
				{
					error = NetworkError.DNSFailure;
					return 0;
				}
			}
			if (ip.IsIPv4MappedToIPv6)
			{
				ip = ip.MapToIPv4();
			}
			int id = FreeId();
			if (id == 0)
			{
				error = NetworkError.NoResources;
				return 0;
			}
			NetPeer peer = manager.Connect(new IPEndPoint(ip, port), ConnectKey);
			if (peer == null)
			{
				error = NetworkError.WrongOperation;
				return 0;
			}
			peer.Tag = id;
			peers[id] = peer;
			error = NetworkError.Ok;
			return id;
		}

		internal bool Disconnect(int connectionId, out NetworkError error)
		{
			if (relays.TryGetValue(connectionId, out LlapiRelay.Link link))
			{
				LlapiRelay.Disconnect(link);
				error = NetworkError.Ok;
				return true;
			}
			if (!peers.TryGetValue(connectionId, out NetPeer peer))
			{
				error = NetworkError.WrongConnection;
				return false;
			}
			// LiteNetLib reports it as a disconnection of the peer, which comes back from ReceiveFromHost as UNet's did.
			manager.DisconnectPeer(peer);
			error = NetworkError.Ok;
			return true;
		}

		internal bool Send(int connectionId, int channelId, byte[] buffer, int size, out NetworkError error)
		{
			if (relays.TryGetValue(connectionId, out LlapiRelay.Link link))
			{
				if (channelId < 0 || channelId >= channels.Length)
				{
					error = NetworkError.WrongChannel;
					return false;
				}
				DeliveryMethod kind = Delivery(channels[channelId]);
				bool reliable = kind != DeliveryMethod.Unreliable && kind != DeliveryMethod.Sequenced;
				if (!LlapiRelay.SendData(link, channelId, buffer, size, reliable))
				{
					error = NetworkError.WrongConnection;
					return false;
				}
				error = NetworkError.Ok;
				return true;
			}
			if (!peers.TryGetValue(connectionId, out NetPeer peer) || peer.ConnectionState != ConnectionState.Connected)
			{
				error = NetworkError.WrongConnection;
				return false;
			}
			if (channelId < 0 || channelId >= channels.Length)
			{
				error = NetworkError.WrongChannel;
				return false;
			}
			DeliveryMethod method = Delivery(channels[channelId]);
			try
			{
				// UNet split a big message on an unreliable channel; LiteNetLib only splits reliable ones.
				if (method == DeliveryMethod.Unreliable && size + 1 <= peer.GetMaxSinglePacketSize(method))
				{
					// LiteNetLib's unreliable packets have no channel: the first byte carries it.
					byte[] packet = new byte[size + 1];
					packet[0] = (byte)channelId;
					Buffer.BlockCopy(buffer, 0, packet, 1, size);
					peer.Send(packet, 0, packet.Length, 0, method);
				}
				else
				{
					if (method != DeliveryMethod.ReliableOrdered && size > peer.GetMaxSinglePacketSize(method))
					{
						method = DeliveryMethod.ReliableOrdered;
					}
					peer.Send(buffer, 0, size, (byte)channelId, method);
				}
			}
			catch (Exception)
			{
				error = NetworkError.MessageToLong;
				return false;
			}
			error = NetworkError.Ok;
			return true;
		}

		internal NetworkEventType Receive(out int connectionId, out int channelId, byte[] buffer, int bufferSize, out int receivedSize, out NetworkError error)
		{
			connectionId = 0;
			channelId = 0;
			receivedSize = 0;
			error = NetworkError.Ok;
			LlapiRelay.Poll();
			if (events.Count == 0)
			{
				manager.PollEvents();
				if (events.Count == 0)
				{
					return NetworkEventType.Nothing;
				}
			}
			Event next = events.Dequeue();
			connectionId = next.Connection;
			channelId = next.Channel;
			error = next.Error;
			if (next.Type == NetworkEventType.DataEvent)
			{
				if (next.Data.Length > bufferSize)
				{
					error = NetworkError.MessageToLong;
					receivedSize = 0;
				}
				else
				{
					Buffer.BlockCopy(next.Data, 0, buffer, 0, next.Data.Length);
					receivedSize = next.Data.Length;
				}
			}
			else if (next.Type == NetworkEventType.BroadcastEvent)
			{
				lastBroadcastFrom = next.From;
				lastBroadcast = next.Data;
				receivedSize = next.Data.Length;
			}
			return next.Type;
		}

		internal int RoundTripTime(int connectionId, out NetworkError error)
		{
			if (relays.TryGetValue(connectionId, out LlapiRelay.Link link))
			{
				error = NetworkError.Ok;
				return link.RoundTrip;
			}
			if (!peers.TryGetValue(connectionId, out NetPeer peer))
			{
				error = NetworkError.WrongConnection;
				return 0;
			}
			error = NetworkError.Ok;
			return peer.RoundTripTime;
		}

		internal bool Address(int connectionId, out string address, out int port, out NetworkError error)
		{
			if (relays.TryGetValue(connectionId, out LlapiRelay.Link link))
			{
				address = LlapiRelay.RelayAddress(link.Peer);
				port = link.Port;
				error = NetworkError.Ok;
				return true;
			}
			if (!peers.TryGetValue(connectionId, out NetPeer peer))
			{
				address = string.Empty;
				port = 0;
				error = NetworkError.WrongConnection;
				return false;
			}
			address = Text(peer.Address);
			port = peer.Port;
			error = NetworkError.Ok;
			return true;
		}

		// ---- the relay (LlapiRelay calls these from its poll, on the main thread)

		private int ConnectRelay(ulong peer, int port, out NetworkError error)
		{
			if (!LlapiRelay.Available)
			{
				error = NetworkError.WrongOperation;
				return 0;
			}
			int id = FreeId();
			if (id == 0)
			{
				error = NetworkError.NoResources;
				return 0;
			}
			relays[id] = LlapiRelay.Connect(this, id, peer, port);
			error = NetworkError.Ok;
			return id;
		}

		internal bool RelayAccept(LlapiRelay.Link link)
		{
			int id = (peers.Count + relays.Count < maxConnections) ? FreeId() : 0;
			if (id == 0)
			{
				return false;
			}
			link.Id = id;
			link.Host = this;
			relays[id] = link;
			Enqueue(new Event { Type = NetworkEventType.ConnectEvent, Connection = id });
			return true;
		}

		internal void RelayConnected(LlapiRelay.Link link)
		{
			Enqueue(new Event { Type = NetworkEventType.ConnectEvent, Connection = link.Id });
		}

		internal void RelayData(LlapiRelay.Link link, int channel, byte[] data)
		{
			Enqueue(new Event { Type = NetworkEventType.DataEvent, Connection = link.Id, Channel = channel, Data = data });
		}

		internal void RelayClosed(LlapiRelay.Link link, NetworkError error)
		{
			if (relays.TryGetValue(link.Id, out LlapiRelay.Link known) && known == link)
			{
				relays.Remove(link.Id);
				Enqueue(new Event { Type = NetworkEventType.DisconnectEvent, Connection = link.Id, Error = error });
			}
		}

		// ---- LAN discovery

		internal void StartBroadcasting(int port, int key, int version, int subversion, byte[] message, int size, int interval)
		{
			StopBroadcasting();
			byte[] packet = new byte[BroadcastHeader + size];
			Buffer.BlockCopy(BroadcastMagic, 0, packet, 0, BroadcastMagic.Length);
			WriteInt(packet, 4, key);
			WriteInt(packet, 8, version);
			WriteInt(packet, 12, subversion);
			Buffer.BlockCopy(message, 0, packet, BroadcastHeader, size);
			broadcastTimer = new Timer(_ =>
			{
				if (manager.IsRunning)
				{
					manager.SendBroadcast(packet, 0, packet.Length, port);
				}
			}, null, 0, Math.Max(100, interval));
		}

		internal void StopBroadcasting()
		{
			if (broadcastTimer != null)
			{
				broadcastTimer.Dispose();
				broadcastTimer = null;
			}
		}

		internal void ListenForBroadcasts(int key, int version, int subversion)
		{
			broadcastKey = key;
			broadcastVersion = version;
			broadcastSubversion = subversion;
			listensForBroadcasts = true;
			// The game's host for finding LAN games, not a server: the relay's packets to servers are not for it.
			LlapiRelay.Unlisten(this);
		}

		internal void LastBroadcastSender(out string address, out int port)
		{
			address = (lastBroadcastFrom != null) ? Text(lastBroadcastFrom.Address) : null;
			port = (lastBroadcastFrom != null) ? lastBroadcastFrom.Port : 0;
		}

		internal int LastBroadcastMessage(byte[] buffer, int bufferSize)
		{
			if (lastBroadcast == null)
			{
				return 0;
			}
			int size = Math.Min(bufferSize, lastBroadcast.Length);
			Buffer.BlockCopy(lastBroadcast, 0, buffer, 0, size);
			return size;
		}

		// ---- LiteNetLib's events: delivered inside PollEvents, so on the main thread like everything else here

		void INetEventListener.OnConnectionRequest(ConnectionRequest request)
		{
			if (peers.Count + relays.Count >= maxConnections || FreeId() == 0)
			{
				request.Reject();
				return;
			}
			request.AcceptIfKey(ConnectKey);
		}

		void INetEventListener.OnPeerConnected(NetPeer peer)
		{
			if (!(peer.Tag is int id))
			{
				id = FreeId();
				if (id == 0)
				{
					manager.DisconnectPeer(peer);
					return;
				}
				peer.Tag = id;
				peers[id] = peer;
			}
			Enqueue(new Event { Type = NetworkEventType.ConnectEvent, Connection = id });
		}

		void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
		{
			if (!(peer.Tag is int id) || !peers.TryGetValue(id, out NetPeer known) || known != peer)
			{
				return;
			}
			peers.Remove(id);
			NetworkError error = NetworkError.Ok;
			if (info.Reason != DisconnectReason.RemoteConnectionClose && info.Reason != DisconnectReason.DisconnectPeerCalled)
			{
				error = NetworkError.Timeout;
			}
			Enqueue(new Event { Type = NetworkEventType.DisconnectEvent, Connection = id, Error = error });
		}

		void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
		{
			if (!(peer.Tag is int id))
			{
				return;
			}
			byte[] data = reader.GetRemainingBytes();
			if (deliveryMethod == DeliveryMethod.Unreliable)
			{
				if (data.Length == 0)
				{
					return;
				}
				channelNumber = data[0];
				byte[] rest = new byte[data.Length - 1];
				Buffer.BlockCopy(data, 1, rest, 0, rest.Length);
				data = rest;
			}
			Enqueue(new Event { Type = NetworkEventType.DataEvent, Connection = id, Channel = channelNumber, Data = data });
		}

		void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
		{
			if (!listensForBroadcasts || messageType != UnconnectedMessageType.Broadcast)
			{
				return;
			}
			byte[] packet = reader.GetRemainingBytes();
			if (packet.Length < BroadcastHeader || packet[0] != BroadcastMagic[0] || packet[1] != BroadcastMagic[1] || packet[2] != BroadcastMagic[2] || packet[3] != BroadcastMagic[3])
			{
				return;
			}
			if (ReadInt(packet, 4) != broadcastKey || ReadInt(packet, 8) != broadcastVersion || ReadInt(packet, 12) != broadcastSubversion)
			{
				return;
			}
			byte[] message = new byte[packet.Length - BroadcastHeader];
			Buffer.BlockCopy(packet, BroadcastHeader, message, 0, message.Length);
			Enqueue(new Event { Type = NetworkEventType.BroadcastEvent, Data = message, From = remoteEndPoint });
		}

		void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError)
		{
		}

		void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency)
		{
		}

		// ---- helpers

		private int FreeId()
		{
			for (int id = 1; id <= maxConnections; id++)
			{
				if (!peers.ContainsKey(id) && !relays.ContainsKey(id))
				{
					return id;
				}
			}
			return 0;
		}

		private void Enqueue(Event e)
		{
			events.Enqueue(e);
		}

		// As Unity 5.6 wrote an address: an IPv4 address mapped into IPv6.
		private static string Text(IPAddress address)
		{
			if (address.IsIPv4MappedToIPv6)
			{
				address = address.MapToIPv4();
			}
			return (address.AddressFamily == AddressFamily.InterNetwork) ? "::ffff:" + address : address.ToString();
		}

		private static DeliveryMethod Delivery(QosType qos)
		{
			switch (qos)
			{
				case QosType.Unreliable:
				case QosType.UnreliableFragmented:
					return DeliveryMethod.Unreliable;
				case QosType.UnreliableSequenced:
				case QosType.StateUpdate:
					return DeliveryMethod.Sequenced;
				case QosType.ReliableStateUpdate:
					return DeliveryMethod.ReliableSequenced;
				default:
					// Reliable, fragmented or sequenced, and all-cost delivery: in order, since the high-level API
					// splits big messages of a reliable channel into fragments it puts together in the order they come.
					return DeliveryMethod.ReliableOrdered;
			}
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
	}
}
