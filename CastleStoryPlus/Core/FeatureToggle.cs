using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Core;

// A feature switched on or off ([Features], in the settings) takes effect at once, not after a restart: its Harmony
// patches are applied or taken off while the game runs, and its MonoBehaviours on the mod's object are switched on or
// off. Its Enable() (settings, hooks, components) runs the first time it is switched on in a session only. What a
// feature changes in the game's Lua menus (LuaInjection) is put in when a menu loads, so those parts follow the next
// time the menu loads (another game, or the main menu).
internal static class FeatureToggle
{
	private static Harmony _harmony;

	// The classes of each feature (by its name).
	private static readonly Dictionary<string, List<Type>> Types = new Dictionary<string, List<Type>>();

	// Features whose Enable() has run in this session.
	private static readonly HashSet<string> Started = new HashSet<string>();

	internal static void Register(Harmony harmony, string feature, Type type)
	{
		_harmony = harmony;
		if (!Types.TryGetValue(feature, out List<Type> types))
		{
			types = new List<Type>();
			Types[feature] = types;
		}
		types.Add(type);
	}

	internal static void MarkStarted(string feature)
	{
		Started.Add(feature);
	}

	// The switch's changes go to Set.
	internal static void Watch(ConfigEntry<bool> entry, string feature)
	{
		entry.SettingChanged += (object sender, EventArgs args) => Set(feature, entry.Value);
	}

	internal static void Set(string feature, bool on)
	{
		if (_harmony == null || !Types.TryGetValue(feature, out List<Type> types))
		{
			return;
		}
		if (Plugin.LoadedFeatures.TryGetValue(feature, out bool loaded) && loaded == on)
		{
			return;
		}
		Plugin.LoadedFeatures[feature] = on;
		int patches = 0;
		foreach (Type type in types)
		{
			try
			{
				if (Attribute.IsDefined(type, typeof(HarmonyPatch)))
				{
					if (on)
					{
						_harmony.CreateClassProcessor(type).Patch();
					}
					else
					{
						Unpatch(type);
					}
					patches++;
				}
			}
			catch (Exception ex)
			{
				Plugin.Log.LogError("FeatureToggle: " + feature + " (" + type.Name + "): " + ex.Message);
			}
		}
		if (on && Started.Add(feature))
		{
			foreach (Type type in types)
			{
				try
				{
					type.GetMethod("Enable", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)?.Invoke(null, null);
				}
				catch (Exception ex)
				{
					Plugin.Log.LogError("FeatureToggle: " + feature + " (" + type.Name + ") Enable: " + ex.Message);
				}
			}
		}
		else
		{
			SwitchComponents(types, on);
		}
		Plugin.Log.LogInfo("FeatureToggle: " + feature + " switched " + (on ? "on" : "off") + " (" + patches + " patch classes)");
	}

	// Takes off every patch this class (or a class nested in it) made, and only those.
	private static void Unpatch(Type type)
	{
		foreach (MethodBase original in new List<MethodBase>(Harmony.GetAllPatchedMethods()))
		{
			Patches info = Harmony.GetPatchInfo(original);
			if (info == null)
			{
				continue;
			}
			foreach (IList<Patch> list in new IList<Patch>[] { info.Prefixes, info.Postfixes, info.Transpilers, info.Finalizers })
			{
				foreach (Patch patch in list)
				{
					if (patch.owner == Plugin.Guid && Within(patch.PatchMethod.DeclaringType, type))
					{
						_harmony.Unpatch(original, patch.PatchMethod);
					}
				}
			}
		}
	}

	private static bool Within(Type inner, Type outer)
	{
		for (Type t = inner; t != null; t = t.DeclaringType)
		{
			if (t == outer)
			{
				return true;
			}
		}
		return false;
	}

	// The feature's MonoBehaviours on the mod's object, on or off.
	private static void SwitchComponents(List<Type> types, bool on)
	{
		if (Plugin.Root == null)
		{
			return;
		}
		foreach (Type type in types)
		{
			if (!typeof(Behaviour).IsAssignableFrom(type))
			{
				continue;
			}
			foreach (Component component in Plugin.Root.GetComponents(type))
			{
				((Behaviour)component).enabled = on;
			}
		}
	}
}
