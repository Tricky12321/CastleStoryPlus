using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BaseUnityPlugin
{
	public const string Guid = "com.tricky12321.castlestoryplus";

	public const string Name = "Castle Story Plus";

	public const string Version = "0.2.0";

	internal static ManualLogSource Log;

	internal static ConfigFile Cfg;

	// The BepInEx manager object (survives scene loads); features add their MonoBehaviours here.
	internal static UnityEngine.GameObject Root;

	internal static ConfigEntry<float> EnergyMultiplier;

	private void Awake()
	{
		Log = Logger;
		Cfg = Config;
		Root = gameObject;
		EnergyMultiplier = Config.Bind("Economy", "EnergyMultiplier", 1.3f, "Energy carried by each brewed firefly, relative to the blue crystal it costs. 1.3 = new workers arrive 30% faster.");
		Harmony harmony = new Harmony(Guid);
		Dictionary<string, bool> features = new Dictionary<string, bool>();
		int patched = 0;
		foreach (Type type in typeof(Plugin).Assembly.GetTypes())
		{
			FeatureAttribute feature = (FeatureAttribute)Attribute.GetCustomAttribute(type, typeof(FeatureAttribute));
			if (feature == null)
			{
				continue;
			}
			// Infrastructure used by several features has no name and is always on.
			bool enabled = true;
			if (feature.Name != null && !features.TryGetValue(feature.Name, out enabled))
			{
				enabled = Config.Bind("Features", feature.Name, true, feature.Description).Value;
				features[feature.Name] = enabled;
			}
			if (!enabled)
			{
				continue;
			}
			try
			{
				if (Attribute.IsDefined(type, typeof(HarmonyPatch)))
				{
					harmony.CreateClassProcessor(type).Patch();
					patched++;
				}
				type.GetMethod("Enable", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)?.Invoke(null, null);
			}
			catch (Exception ex)
			{
				Log.LogError("Feature '" + feature.Name + "' failed in " + type.Name + ": " + ex);
			}
		}
		Log.LogInfo(Name + " " + Version + " loaded (" + patched + " patch classes)");
	}
}
