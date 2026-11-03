using System;
using System.Collections.Generic;
using System.Net;

// Unity 5.6's low-level networking API (LLAPI), which the game's UnityEngine.Networking.dll (the UNet high-level API)
// and the game itself call into. Unity 6 no longer has it. These are the members the game uses, with the same names,
// so CodePatcher only has to point the game's references here. The transport itself is LiteNetLib (LlapiHost): UDP
// with the channels' delivery, connection ids and LAN discovery broadcasts as UNet had them. It does not speak Unity
// 5.6's wire protocol, so the port plays with the port only.
namespace UnityEngine.Networking
{
	public enum QosType
	{
		Unreliable,
		UnreliableFragmented,
		UnreliableSequenced,
		Reliable,
		ReliableFragmented,
		ReliableSequenced,
		StateUpdate,
		ReliableStateUpdate,
		AllCostDelivery,
	}

	public enum ConnectionAcksType
	{
		Acks32 = 1,
		Acks64,
		Acks96,
		Acks128,
	}

	public enum NetworkError
	{
		Ok,
		WrongHost,
		WrongConnection,
		WrongChannel,
		NoResources,
		BadMessage,
		Timeout,
		MessageToLong,
		WrongOperation,
		VersionMismatch,
		CRCMismatch,
		DNSFailure,
		UsageError,
	}

	public enum NetworkEventType
	{
		DataEvent,
		ConnectEvent,
		DisconnectEvent,
		Nothing,
		BroadcastEvent,
	}

	public class ChannelQOS
	{
		public ChannelQOS(QosType value)
		{
			QOS = value;
		}

		public QosType QOS { get; }
	}

	public class ConnectionConfig
	{
		private readonly List<ChannelQOS> channels = new List<ChannelQOS>();

		public ushort PacketSize { get; set; } = 1440;
		public ushort FragmentSize { get; set; } = 500;
		public uint MinUpdateTimeout { get; set; } = 10;
		public ushort MaxSentMessageQueueSize { get; set; } = 512;
		public ConnectionAcksType AcksType { get; set; } = ConnectionAcksType.Acks32;
		public bool UsePlatformSpecificProtocols { get; set; }
		public List<ChannelQOS> Channels => channels;
		public int ChannelCount => channels.Count;

		public byte AddChannel(QosType value)
		{
			channels.Add(new ChannelQOS(value));
			return (byte)(channels.Count - 1);
		}
	}

	public class HostTopology
	{
		public HostTopology(ConnectionConfig defaultConfig, int maxDefaultConnections)
		{
			DefaultConfig = defaultConfig;
			MaxDefaultConnections = maxDefaultConnections;
		}

		public ConnectionConfig DefaultConfig { get; }
		public int MaxDefaultConnections { get; }
	}

	public class GlobalConfig
	{
		public uint ThreadAwakeTimeout { get; set; } = 1;
		public ushort MaxPacketSize { get; set; } = 2000;
	}

	public class ConnectionSimulatorConfig : IDisposable
	{
		public ConnectionSimulatorConfig(int outMinDelay, int outAvgDelay, int inMinDelay, int inAvgDelay, float packetLossPercentage)
		{
		}

		public void Dispose()
		{
		}
	}

	public static class Utility
	{
		public static Types.SourceID GetSourceID()
		{
			return (Types.SourceID)(ulong)SystemInfo.deviceUniqueIdentifier.GetHashCode();
		}
	}

	public sealed class NetworkTransport
	{
		private static readonly Dictionary<int, LlapiHost> hosts = new Dictionary<int, LlapiHost>();

		private static int nextHost;

		// The host that broadcasts this game on the LAN (one at a time, as in UNet).
		private static LlapiHost broadcaster;

		private NetworkTransport()
		{
		}

		public static bool IsStarted { get; private set; }

		public static void Init()
		{
			IsStarted = true;
		}

		public static void Init(GlobalConfig config)
		{
			IsStarted = true;
		}

		public static void Shutdown()
		{
			foreach (LlapiHost host in hosts.Values)
			{
				host.Close();
			}
			hosts.Clear();
			broadcaster = null;
			IsStarted = false;
		}

		public static int AddHost(HostTopology topology, int port) => AddHost(topology, port, null);

		public static int AddHost(HostTopology topology, int port, string ip)
		{
			LlapiHost host = LlapiHost.Open(topology, port, ip);
			if (host == null)
			{
				return -1;
			}
			int id = nextHost++;
			hosts[id] = host;
			return id;
		}

		public static int AddHostWithSimulator(HostTopology topology, int minTimeout, int maxTimeout) => AddHost(topology, 0, null);
		public static int AddHostWithSimulator(HostTopology topology, int minTimeout, int maxTimeout, int port) => AddHost(topology, port, null);
		public static int AddWebsocketHost(HostTopology topology, int port) => -1;
		public static int AddWebsocketHost(HostTopology topology, int port, string ip) => -1;

		public static bool RemoveHost(int hostId)
		{
			if (!hosts.TryGetValue(hostId, out LlapiHost host))
			{
				return false;
			}
			if (broadcaster == host)
			{
				broadcaster = null;
			}
			host.Close();
			hosts.Remove(hostId);
			return true;
		}

		public static int Connect(int hostId, string address, int port, int exceptionConnectionId, out byte error)
		{
			if (!hosts.TryGetValue(hostId, out LlapiHost host))
			{
				error = (byte)NetworkError.WrongHost;
				return 0;
			}
			int connection = host.Connect(address, port, out NetworkError result);
			error = (byte)result;
			return connection;
		}

		public static int ConnectEndPoint(int hostId, EndPoint endPoint, int exceptionConnectionId, out byte error)
		{
			if (endPoint is IPEndPoint ip)
			{
				return Connect(hostId, ip.Address.ToString(), ip.Port, exceptionConnectionId, out error);
			}
			error = (byte)NetworkError.WrongOperation;
			return 0;
		}

		public static int ConnectWithSimulator(int hostId, string address, int port, int exceptionConnectionId, out byte error, ConnectionSimulatorConfig conf)
		{
			return Connect(hostId, address, port, exceptionConnectionId, out error);
		}

		// Unity's relay service, shut down years ago.
		public static int ConnectToNetworkPeer(int hostId, string address, int port, int exceptionConnectionId, int relaySlotId, Types.NetworkID network, Types.SourceID source, Types.NodeID node, out byte error)
		{
			error = (byte)NetworkError.WrongOperation;
			return 0;
		}

		public static void ConnectAsNetworkHost(int hostId, string address, int port, Types.NetworkID network, Types.SourceID source, Types.NodeID node, out byte error)
		{
			error = (byte)NetworkError.WrongOperation;
		}

		public static bool Disconnect(int hostId, int connectionId, out byte error)
		{
			if (!hosts.TryGetValue(hostId, out LlapiHost host))
			{
				error = (byte)NetworkError.WrongHost;
				return false;
			}
			bool done = host.Disconnect(connectionId, out NetworkError result);
			error = (byte)result;
			return done;
		}

		public static bool Send(int hostId, int connectionId, int channelId, byte[] buffer, int size, out byte error)
		{
			if (!hosts.TryGetValue(hostId, out LlapiHost host))
			{
				error = (byte)NetworkError.WrongHost;
				return false;
			}
			bool sent = host.Send(connectionId, channelId, buffer, size, out NetworkError result);
			error = (byte)result;
			return sent;
		}

		public static NetworkEventType ReceiveFromHost(int hostId, out int connectionId, out int channelId, byte[] buffer, int bufferSize, out int receivedSize, out byte error)
		{
			if (!hosts.TryGetValue(hostId, out LlapiHost host))
			{
				connectionId = 0;
				channelId = 0;
				receivedSize = 0;
				error = (byte)NetworkError.WrongHost;
				return NetworkEventType.Nothing;
			}
			NetworkEventType type = host.Receive(out connectionId, out channelId, buffer, bufferSize, out receivedSize, out NetworkError result);
			error = (byte)result;
			return type;
		}

		public static NetworkEventType ReceiveRelayEventFromHost(int hostId, out byte error)
		{
			error = (byte)NetworkError.Ok;
			return NetworkEventType.Nothing;
		}

		public static int GetCurrentRTT(int hostId, int connectionId, out byte error)
		{
			if (!hosts.TryGetValue(hostId, out LlapiHost host))
			{
				error = (byte)NetworkError.WrongHost;
				return 0;
			}
			int rtt = host.RoundTripTime(connectionId, out NetworkError result);
			error = (byte)result;
			return rtt;
		}

		public static void GetConnectionInfo(int hostId, int connectionId, out string address, out int port, out Types.NetworkID network, out Types.NodeID dstNode, out byte error)
		{
			network = Types.NetworkID.Invalid;
			dstNode = Types.NodeID.Invalid;
			if (!hosts.TryGetValue(hostId, out LlapiHost host))
			{
				address = string.Empty;
				port = 0;
				error = (byte)NetworkError.WrongHost;
				return;
			}
			host.Address(connectionId, out address, out port, out NetworkError result);
			error = (byte)result;
		}

		public static bool DoesEndPointUsePlatformProtocols(EndPoint endPoint) => false;

		public static bool StartBroadcastDiscovery(int hostId, int broadcastPort, int key, int version, int subversion, byte[] buffer, int size, int timeout, out byte error)
		{
			if (!hosts.TryGetValue(hostId, out LlapiHost host))
			{
				error = (byte)NetworkError.WrongHost;
				return false;
			}
			if (broadcaster != null)
			{
				broadcaster.StopBroadcasting();
			}
			broadcaster = host;
			host.StartBroadcasting(broadcastPort, key, version, subversion, buffer, size, timeout);
			error = (byte)NetworkError.Ok;
			return true;
		}

		public static void StopBroadcastDiscovery()
		{
			if (broadcaster != null)
			{
				broadcaster.StopBroadcasting();
				broadcaster = null;
			}
		}

		public static void SetBroadcastCredentials(int hostId, int key, int version, int subversion, out byte error)
		{
			if (!hosts.TryGetValue(hostId, out LlapiHost host))
			{
				error = (byte)NetworkError.WrongHost;
				return;
			}
			host.ListenForBroadcasts(key, version, subversion);
			error = (byte)NetworkError.Ok;
		}

		public static void GetBroadcastConnectionInfo(int hostId, out string address, out int port, out byte error)
		{
			if (!hosts.TryGetValue(hostId, out LlapiHost host))
			{
				address = null;
				port = 0;
				error = (byte)NetworkError.WrongHost;
				return;
			}
			host.LastBroadcastSender(out address, out port);
			error = (byte)NetworkError.Ok;
		}

		public static void GetBroadcastConnectionMessage(int hostId, byte[] buffer, int bufferSize, out int receivedSize, out byte error)
		{
			if (!hosts.TryGetValue(hostId, out LlapiHost host))
			{
				receivedSize = 0;
				error = (byte)NetworkError.WrongHost;
				return;
			}
			receivedSize = host.LastBroadcastMessage(buffer, bufferSize);
			error = (byte)NetworkError.Ok;
		}
	}
}

namespace UnityEngine.Networking.Types
{
	[System.ComponentModel.DefaultValue(Invalid)]
	public enum NetworkID : ulong
	{
		Invalid = ulong.MaxValue,
	}

	[System.ComponentModel.DefaultValue(Invalid)]
	public enum NodeID : ushort
	{
		Invalid = 0,
	}

	[System.ComponentModel.DefaultValue(Invalid)]
	public enum SourceID : ulong
	{
		Invalid = ulong.MaxValue,
	}
}

namespace UnityEngine.Networking.Match
{
	// Unity's matchmaking service, shut down years ago: every request fails.
	public class MatchInfo
	{
		public string address { get; private set; }
		public int port { get; private set; }
		public int domain { get; private set; }
		public Types.NetworkID networkId { get; private set; }
		public Types.NodeID nodeId { get; private set; }
		public bool usingRelay { get; private set; }
	}

	public class MatchInfoSnapshot
	{
		public string name { get; private set; }
		public Types.NetworkID networkId { get; private set; }
	}

	public class NetworkMatch : MonoBehaviour
	{
		public delegate void BasicResponseDelegate(bool success, string extendedInfo);

		public delegate void DataResponseDelegate<T>(bool success, string extendedInfo, T responseData);

		public Uri baseUri { get; set; } = new Uri("https://mm.unet.unity3d.com");

		public Coroutine CreateMatch(string matchName, uint matchSize, bool matchAdvertise, string matchPassword, string publicClientAddress, string privateClientAddress, int eloScoreForMatch, int requestDomain, DataResponseDelegate<MatchInfo> callback)
		{
			callback?.Invoke(false, "Matchmaking is not available", null);
			return null;
		}

		public Coroutine JoinMatch(Types.NetworkID netId, string matchPassword, string publicClientAddress, string privateClientAddress, int eloScoreForClient, int requestDomain, DataResponseDelegate<MatchInfo> callback)
		{
			callback?.Invoke(false, "Matchmaking is not available", null);
			return null;
		}

		public Coroutine ListMatches(int startPageNumber, int resultPageSize, string matchNameFilter, bool filterOutPrivateMatchesFromResults, int eloScoreTarget, int requestDomain, DataResponseDelegate<List<MatchInfoSnapshot>> callback)
		{
			callback?.Invoke(false, "Matchmaking is not available", null);
			return null;
		}

		public Coroutine DropConnection(Types.NetworkID netId, Types.NodeID dropNodeId, int requestDomain, BasicResponseDelegate callback)
		{
			callback?.Invoke(false, "Matchmaking is not available");
			return null;
		}
	}
}
