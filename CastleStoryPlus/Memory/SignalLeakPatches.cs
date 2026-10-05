using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Brix.Engine;
using Brix.External.Signals;
using Brix.Game.Components;
using Brix.Utils;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Memory;

// Fixes for game objects that connect to signals living for the whole session and never disconnect, so every
// game played left its observers (and all they track) behind, still called on every later event.

// Replaces the discarded connection of every 'signal.Connect(...)' in a method with a call that keeps it.
internal static class KeptConnections
{
	internal static IEnumerable<CodeInstruction> KeepDiscarded(IEnumerable<CodeInstruction> instructions, MethodInfo keep, bool passThis)
	{
		List<CodeInstruction> code = new List<CodeInstruction>(instructions);
		for (int i = 0; i < code.Count - 1; i++)
		{
			if (code[i].operand is MethodInfo method && method.ReturnType == typeof(SignalConnection) && !IsGameSignals(method.DeclaringType) && code[i + 1].opcode == OpCodes.Pop)
			{
				List<CodeInstruction> replacement = new List<CodeInstruction>();
				if (passThis)
				{
					replacement.Add(new CodeInstruction(OpCodes.Ldarg_0));
				}
				replacement.Add(new CodeInstruction(OpCodes.Call, keep));
				replacement[0].labels.AddRange(code[i + 1].labels);
				replacement[0].blocks.AddRange(code[i + 1].blocks);
				code.RemoveAt(i + 1);
				code.InsertRange(i + 1, replacement);
			}
		}
		return code;
	}

	// GameSignals are cleared with every scene already.
	private static bool IsGameSignals(System.Type type)
	{
		for (System.Type t = type; t != null; t = t.DeclaringType)
		{
			if (t == typeof(GameSignals))
			{
				return true;
			}
		}
		return false;
	}
}

// UIGameObserver's world editor objects, drag handles and the four "dynamic objects" lists connect to static
// signals (objects created by a factory, drag handles requested, trees and plants hovered) without disconnecting.
// Their connections are kept and dropped when the observer is destroyed with its scene.
[Feature(Features.GameLeakFixes, Features.GameLeakFixesInfo)]
[HarmonyPatch]
internal static class ObserverSignalsPatch
{
	private static readonly List<SignalConnection> Connections = new List<SignalConnection>();

	private static IEnumerable<MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(UIGameObserver.WorldEditorObjects), "ConnectToSignals2");
		yield return AccessTools.Method(typeof(UIGameObserver.Handles), "ConnectBaseSignals");
		yield return AccessTools.Method(typeof(UIGameObserver.DynamicObjects), "ConnectBaseSignals");
	}

	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		return KeptConnections.KeepDiscarded(instructions, AccessTools.Method(typeof(ObserverSignalsPatch), nameof(Keep)), passThis: false);
	}

	private static void Keep(SignalConnection connection)
	{
		Connections.Add(connection);
	}

	internal static void DisconnectAll()
	{
		foreach (SignalConnection connection in Connections)
		{
			connection.Disconnect();
		}
		Connections.Clear();
	}
}

[Feature(Features.GameLeakFixes, Features.GameLeakFixesInfo)]
[HarmonyPatch(typeof(UIGameObserver), "OnDestroy")]
internal static class ObserverDestroyedPatch
{
	private static void Postfix()
	{
		ObserverSignalsPatch.DisconnectAll();
	}
}

// Each game scene's overhead parenter connects to the static "parent me" signal of name tags and never
// disconnects, so the parenters of all earlier games stayed alive and were called for every new name tag.
[Feature(Features.GameLeakFixes, Features.GameLeakFixesInfo)]
[HarmonyPatch(typeof(OverheadParenter), "Awake")]
internal static class OverheadParenterPatch
{
	private static readonly Dictionary<OverheadParenter, SignalConnection> Connections = new Dictionary<OverheadParenter, SignalConnection>();

	private static bool Prefix(OverheadParenter __instance)
	{
		List<OverheadParenter> destroyed = new List<OverheadParenter>();
		foreach (KeyValuePair<OverheadParenter, SignalConnection> pair in Connections)
		{
			if (pair.Key == null)
			{
				pair.Value.Disconnect();
				destroyed.Add(pair.Key);
			}
		}
		foreach (OverheadParenter parenter in destroyed)
		{
			Connections.Remove(parenter);
		}
		Connections[__instance] = OverheadDisplayDriver.ParentMe.Connect(__instance.ParentOverhead);
		return false;
	}
}

// Highlighter: a hover highlight connects to the block's "released" signal and that connection was never removed
// when the highlight went away, so every hover added one to the block. Highlights by position also kept one
// entry (and pooled list) for every voxel ever highlighted, including every voxel of dig and volume zones.
[Feature(Features.GameLeakFixes, Features.GameLeakFixesInfo)]
[HarmonyPatch(typeof(Highlighter), nameof(Highlighter.HighlightAt))]
internal static class HighlighterConnectionPatch
{
	internal static readonly Dictionary<int, SignalConnection> ByRequest = new Dictionary<int, SignalConnection>();

	private static void Enable()
	{
		GameSession.OnLeave(ByRequest.Clear);
	}

	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		return KeptConnections.KeepDiscarded(instructions, AccessTools.Method(typeof(HighlighterConnectionPatch), nameof(Keep)), passThis: true);
	}

	private static void Keep(SignalConnection connection, Highlighter highlighter)
	{
		ByRequest[highlighter.currentID] = connection;
	}
}

[Feature(Features.GameLeakFixes, Features.GameLeakFixesInfo)]
[HarmonyPatch(typeof(Highlighter), nameof(Highlighter.Request))]
internal static class HighlighterRequestPatch
{
	private static void Prefix(int i)
	{
		if (HighlighterConnectionPatch.ByRequest.TryGetValue(i, out SignalConnection connection))
		{
			HighlighterConnectionPatch.ByRequest.Remove(i);
			connection.Disconnect();
		}
	}
}

[Feature(Features.GameLeakFixes, Features.GameLeakFixesInfo)]
[HarmonyPatch(typeof(Highlighter), nameof(Highlighter._RemoveHighlight))]
internal static class HighlighterPositionPatch
{
	private static void Prefix(Highlight highlight, out XYZ __state)
	{
		__state = (highlight != null) ? highlight.TargetPosition : default;
	}

	private static void Postfix(Highlighter __instance, Highlight highlight, XYZ __state)
	{
		if (highlight == null)
		{
			return;
		}
		if (__instance.HighlightsByPos.TryGetValue(__state, out List<GameObject> list) && list.Count == 0)
		{
			__instance.HighlightsByPos.Remove(__state);
			list.Dispose();
		}
	}
}
