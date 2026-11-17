using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Brix.Engine;
using Brix.Game.AI;
using Brix.Network;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.WorkerAI;

// How many bricktrons a task's crew can hold (#88). The game fixes 8: a task's crew size (its WorkForceProvider's
// MaxPopulation, a network value the clients get from the host) starts at 8 for every kind of task, and the game's
// own "assign to a full task" (UNetProjectCmd.CmdChangeProject: one more place, then hire) clamps it to 8 in
// Project.MaximumCrewSize, so a ninth bricktron put on a task was turned away. [Workers] MaxBricktronsPerTask (1 to
// 31, default 15) replaces that clamp: a task still starts with the game's 8 (or the maximum when lower), and putting
// more bricktrons on it grows its crew up to the maximum. The game does not save the crew size (SaveUpdater19to20
// dropped it), so loaded tasks start the same way as new ones. In a multiplayer game the host's setting applies (chosen in the lobby, TaskCrewSizeLobby); it is sent to
// the other players (message 208) for their lobby and the task's "Workers at a time" steps (WorkLimit). Labor's
// _maxConcurrentBricktrons (also 8) only spreads the workers' decisions over the physics ticks; it caps nothing.
[Feature(Features.TaskCrewSize, Features.TaskCrewSizeInfo)]
internal static class TaskCrewSize
{
	// The game's crew size and limit: what a task starts with.
	internal const int GameValue = 8;

	// The maximum when nothing is set.
	internal const int DefaultMost = 15;

	// WorkLimit keeps its limit in 5 bits.
	internal const int Most = WorkLimit.Most;

	private const short SyncMessage = 208;

	private const float BroadcastSeconds = 3f;

	// [Workers] MaxBricktronsPerTask: single player and the host of a multiplayer game.
	internal static ConfigEntry<int> Setting;

	// A client: what the host sent last, 0 before anything came.
	private static int _hostValue;

	// The host: the crew size the tasks in the world were given, 0 when not in a game.
	private static int _applied;

	private static int _sent;

	private static float _nextBroadcast;

	private static NetworkClient _client;

	private static readonly AccessTools.FieldRef<WorkForceProvider, int> MaxPopulationField = AccessTools.FieldRefAccess<WorkForceProvider, int>("_maxPopulation");

	internal static readonly AccessTools.FieldRef<Project, WorkForceProvider> ProviderField = AccessTools.FieldRefAccess<Project, WorkForceProvider>("workforceProvider");

	private sealed class CrewSizeMessage : MessageBase
	{
		public int Value;

		public override void Serialize(NetworkWriter writer)
		{
			writer.WritePackedUInt32((uint)Value);
		}

		public override void Deserialize(NetworkReader reader)
		{
			Value = (int)reader.ReadPackedUInt32();
		}
	}

	private static void Enable()
	{
		Setting = Plugin.Cfg.Bind("Workers", "MaxBricktronsPerTask", DefaultMost, new ConfigDescription("The most bricktrons a task's crew can hold (the game: 8). A task starts with 8; putting more bricktrons on it grows its crew up to this. In a multiplayer game the host's setting applies; the host can also change it in the lobby.", new AcceptableValueRange<int>(1, Most)));
		Plugin.Root.AddComponent<TaskCrewSizeDriver>();
		GameSession.OnLeave(() => _applied = 0);
	}

	// The most a task's crew can hold: the game's 8 with the feature off, the host's on a multiplayer client.
	internal static int Value
	{
		get
		{
			if (Setting == null || !Plugin.IsEnabled(Features.TaskCrewSize))
			{
				return GameValue;
			}
			if (!NetworkServer.active && _hostValue > 0)
			{
				return _hostValue;
			}
			return Mathf.Clamp(Setting.Value, 1, Most);
		}
	}

	// Whether this player chooses the value: single player, or the host. A client shows the host's.
	internal static bool CanChoose => Setting != null && (NetworkServer.active || !IsClient);

	private static bool IsClient => !NetworkServer.active && UNetManager.instance != null && UNetManager.instance.LocalNetworkClient != null;

	// Host or single player: a new value, saved in the settings; the tasks in the world take it at once.
	internal static void Set(int value)
	{
		if (!CanChoose)
		{
			return;
		}
		value = Mathf.Clamp(value, 1, Most);
		if (Setting.Value != value)
		{
			Setting.Value = value;
			Plugin.Log.LogInfo("TaskCrewSize: at most " + value + " bricktrons per task");
		}
	}

	// The tasks whose crew size this sets: every task of the task list except the idle group and raids.
	internal static bool Applies(Project project)
	{
		return project != null && !(project is IdleProject) && !(project is RaidProject2);
	}

	// Server: a task's crew size, with any crew over it sent to the free workers (as the game's "remove worker").
	internal static void SetCrewSize(Project project, int size)
	{
		WorkForceProvider provider = ProviderField(project);
		if (provider == null || provider.MaxPopulation == size)
		{
			return;
		}
		provider.MaxPopulation = size;
		List<Labor> crew = new List<Labor>(project.Crew());
		for (int i = crew.Count - 1; i >= size; i--)
		{
			crew[i].AssignToIdle();
			crew[i].Autonomy = AutonomyStatus.FreeAgent;
		}
	}

	// A task that starts on the server (made, loaded, or taken from the pool) gets the game's crew size of 8, or the
	// maximum when it is lower. Set as the
	// network value's field: the provider's workforce may not be made yet, and the initial state sent to the
	// clients follows.
	internal static void Started(WorkForceProvider provider)
	{
		Project project = provider.GetComponent<Project>();
		if (!Applies(project))
		{
			return;
		}
		int value = Mathf.Min(GameValue, Value);
		if (MaxPopulationField(provider) != value)
		{
			MaxPopulationField(provider) = value;
			provider.SetDirtyBit(1u);
		}
	}

	// The game's clamp to 8 when an assign grows a full task, or anything else sets the crew size; raids keep 8.
	internal static int Clamp(Project project, int value)
	{
		return Mathf.Clamp(value, 0, Applies(project) ? Value : GameValue);
	}

	// Every frame: the client's handler, the host's broadcast, and a changed value applied to the tasks.
	internal static void Update()
	{
		NetworkClient client = (UNetManager.instance != null) ? UNetManager.instance.LocalNetworkClient : null;
		if (client == null && !NetworkServer.active)
		{
			_hostValue = 0;
			_client = null;
		}
		if (client != null && client != _client)
		{
			_client = client;
			if (client.handlers.ContainsKey(SyncMessage))
			{
				client.UnregisterHandler(SyncMessage);
			}
			client.RegisterHandler(SyncMessage, Received);
		}
		if (!NetworkServer.active)
		{
			return;
		}
		int value = Value;
		if (Neo.IsMultiplayer && (value != _sent || Time.unscaledTime >= _nextBroadcast))
		{
			_sent = value;
			_nextBroadcast = Time.unscaledTime + BroadcastSeconds;
			NetworkServer.SendToAll(SyncMessage, new CrewSizeMessage { Value = value });
		}
		if (!Architecte.commence)
		{
			return;
		}
		if (_applied == 0)
		{
			// The tasks of a new or loaded game got it as they started.
			_applied = value;
			return;
		}
		if (_applied != value)
		{
			ApplyAll(_applied, value);
			_applied = value;
		}
	}

	// Host: a lower maximum brings the tasks above it down to it; a higher one leaves the tasks as they are (they
	// grow as bricktrons are put on them).
	private static void ApplyAll(int old, int value)
	{
		int changed = 0;
		foreach (Project project in UnityEngine.Object.FindObjectsOfType<Project>())
		{
			if (!Applies(project))
			{
				continue;
			}
			int size = project.MaximumCrewSize;
			if (size > value)
			{
				SetCrewSize(project, value);
				changed++;
			}
		}
		Plugin.Log.LogInfo("TaskCrewSize: at most " + value + " bricktrons per task (was " + old + "), " + changed + " tasks made smaller");
	}

	private static void Received(NetworkMessage netMsg)
	{
		CrewSizeMessage message = netMsg.ReadMessage<CrewSizeMessage>();
		if (NetworkServer.active || message.Value < 1)
		{
			return;
		}
		if (_hostValue != message.Value)
		{
			Plugin.Log.LogInfo("TaskCrewSize: the host's tasks take at most " + message.Value + " bricktrons");
		}
		_hostValue = Mathf.Min(message.Value, Most);
	}
}

internal class TaskCrewSizeDriver : MonoBehaviour
{
	private void Update()
	{
		try
		{
			TaskCrewSize.Update();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("TaskCrewSize: " + ex.Message);
		}
	}
}

// The game clamps a task's crew size to 8; here to the setting.
[Feature(Features.TaskCrewSize, Features.TaskCrewSizeInfo)]
[HarmonyPatch(typeof(Project), nameof(Project.MaximumCrewSize), MethodType.Setter)]
internal static class TaskCrewSizeClampPatch
{
	private static bool Prefix(Project __instance, int value)
	{
		TaskCrewSize.ProviderField(__instance).MaxPopulation = TaskCrewSize.Clamp(__instance, value);
		return false;
	}
}

// A task starting on the server takes the crew size.
[Feature(Features.TaskCrewSize, Features.TaskCrewSizeInfo)]
[HarmonyPatch(typeof(WorkForceProvider), nameof(WorkForceProvider.OnStartServer))]
internal static class TaskCrewSizeStartPatch
{
	private static void Postfix(WorkForceProvider __instance)
	{
		TaskCrewSize.Started(__instance);
	}
}
