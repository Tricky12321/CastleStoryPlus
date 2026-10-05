using System;
using System.Collections.Generic;
using System.Reflection;
using Brix.Engine;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Network;
using Brix.UI.Builder.Menu;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace CastleStoryPlus.Memory;

// Fixes for memory leaks in the game's own code. Each patch names what grew and how often.

// Every goal a worker rejects adds an error to its project's store, and the list is only emptied when the worker
// accepts a goal in that project. Nothing ever reads it (the readers have no callers), so a worker that keeps
// failing in a project, e.g. builders waiting for unreachable materials, grows it on every selection pass.
// Keep the one visible effect (a temporary thought for errors marked as direct) and drop the store.
[Feature(Features.GameLeakFixes, Features.GameLeakFixesInfo)]
[HarmonyPatch(typeof(AIErrorsStore), nameof(AIErrorsStore.AddError))]
internal static class AIErrorsStorePatch
{
	private static bool Prefix(AIErrorsStore __instance, AIError error, Labor labor)
	{
		if (__instance.DirectErrors.Contains(error))
		{
			labor.SetTemporaryThought(2f, error.thought);
		}
		return false;
	}
}

// Every removed voxel or block was also added to a "whole game" list that is never read or cleared, and on a
// multiplayer client the outgoing list is never sent (only the server drains it), so both grew for the whole game.
[Feature(Features.GameLeakFixes, Features.GameLeakFixesInfo)]
[HarmonyPatch(typeof(UnetDeblocMessageHandler), nameof(UnetDeblocMessageHandler.AddDeblocToSendQueue))]
internal static class DeblocListPatch
{
	private static bool Prefix(UnetDeblocMessageHandler __instance, XYZ position, int id)
	{
		if (NetworkServer.active)
		{
			__instance.DeblocDataListDelta.Add(new UnetDeblocMessageHandler.DeblocData(position, (short)id));
		}
		return false;
	}
}

// UnregisterMenu removed the entry of the newest id instead of the menu's own, so every closed Lua menu (and its
// panels, which are never unregistered) stayed in the id map for the session, and the newest live entry could be
// removed by mistake.
[Feature(Features.GameLeakFixes, Features.GameLeakFixesInfo)]
[HarmonyPatch(typeof(LuaIdUtility), nameof(LuaIdUtility.UnregisterMenu))]
internal static class LuaMenuIdPatch
{
	private static bool Prefix(IMenu menu)
	{
		if (menu.LuaId == 0)
		{
			return false;
		}
		LuaIdUtility.EntryMap.Remove(menu.LuaId);
		menu.LuaId = 0;
		if (menu is BaseMenu baseMenu && baseMenu.Panels != null)
		{
			foreach (IPanel panel in baseMenu.Panels)
			{
				if (panel != null && panel.LuaId != 0)
				{
					LuaIdUtility.EntryMap.Remove(panel.LuaId);
					panel.LuaId = 0;
				}
			}
		}
		return false;
	}
}

// A highlight (hovered or selected block or object) builds a new combined mesh every time its pooled object is
// reused, and the previous one was never destroyed: one mesh per highlight, all game long.
[Feature(Features.GameLeakFixes, Features.GameLeakFixesInfo)]
[HarmonyPatch(typeof(Highlight), nameof(Highlight.CombinedMeshFromChildren))]
internal static class HighlightMeshPatch
{
	private static void Prefix(MeshFilter filter)
	{
		Mesh previous = (filter != null) ? filter.sharedMesh : null;
		// Meshes made at runtime have negative instance ids; never destroy an asset.
		if (previous != null && previous.GetInstanceID() < 0)
		{
			UnityEngine.Object.Destroy(previous);
		}
	}
}

// Pie menu segments get a new material on every build (each open of a pie menu or submenu), and neither the
// replaced material nor the ones of destroyed segments were destroyed.
[Feature(Features.GameLeakFixes, Features.GameLeakFixesInfo)]
[HarmonyPatch]
internal static class RadialMaterialPatch
{
	private static IEnumerable<MethodBase> TargetMethods()
	{
		foreach (Type type in typeof(RadialLayoutElement).Assembly.GetTypes())
		{
			if (typeof(RadialLayoutElement).IsAssignableFrom(type))
			{
				MethodInfo method = type.GetMethod(nameof(RadialLayoutElement.InitBgRawImage), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
				if (method != null)
				{
					yield return method;
				}
			}
		}
	}

	private static void Postfix(RadialLayoutElement __instance)
	{
		RuntimeMaterials materials = __instance.GetComponent<RuntimeMaterials>();
		if (materials == null)
		{
			materials = __instance.gameObject.AddComponent<RuntimeMaterials>();
		}
		materials.Track(__instance.bgRawImage);
		if (__instance is RadialLayoutElementCore core)
		{
			materials.Track(core.bgMidRawImage);
			materials.Track(core.bgTopRawImage);
		}
	}
}

// Destroys the runtime materials of a few UI images when they are replaced or the owner is destroyed.
internal class RuntimeMaterials : MonoBehaviour
{
	private readonly Dictionary<Graphic, Material> _tracked = new Dictionary<Graphic, Material>();

	internal void Track(Graphic graphic)
	{
		if (graphic == null)
		{
			return;
		}
		Material current = graphic.material;
		// A new image draws with Unity's shared default UI material until the game gives it its own. The base
		// InitBgRawImage runs (and is patched) before the override assigns that, so the default must never be tracked:
		// destroying it crashes every UI drawn with it.
		if (IsDefault(current))
		{
			return;
		}
		if (_tracked.TryGetValue(graphic, out Material previous) && previous != current)
		{
			DestroyRuntime(previous);
		}
		_tracked[graphic] = current;
	}

	private void OnDestroy()
	{
		foreach (Material material in _tracked.Values)
		{
			DestroyRuntime(material);
		}
		_tracked.Clear();
	}

	private static void DestroyRuntime(Material material)
	{
		if (material != null && material.GetInstanceID() < 0 && !IsDefault(material))
		{
			Destroy(material);
		}
	}

	private static bool IsDefault(Material material)
	{
		return material == null || material == Graphic.defaultGraphicMaterial || material == Canvas.GetDefaultCanvasMaterial();
	}
}

// Leaving a game cleared each faction's project lists but kept the factions themselves as keys, and switch-task
// cooldowns pending when the game ended were never removed: both kept objects of every game played.
[Feature(Features.GameLeakFixes, Features.GameLeakFixesInfo)]
[HarmonyPatch(typeof(ProjectDatabase), nameof(ProjectDatabase.ClearAll))]
internal static class LeaveGameStaticsPatch
{
	private static void Postfix()
	{
		ProjectDatabase.All.Clear();
		ConflictResolver.SwitchTaskCooldowns.Clear();
	}
}
