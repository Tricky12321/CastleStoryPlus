using BepInEx.Configuration;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Nature;

// The fog of war lifts [FogOfWar] VisionMultiplier times as far around every unit that sees by the game's rule
// (bricktrons and the rest that use GeneralRules.FogOfWarDistance: 40, 60 or 80 by map size). The map's distance is
// scaled when read, so the value the game saves stays its own.
[Feature(Features.WiderVision, Features.WiderVisionInfo)]
[HarmonyPatch(typeof(LocalFactionVisionConfigurator), "FogDistance", MethodType.Getter)]
internal static class WiderVision
{
	private const float DefaultMultiplier = 1.1f;

	internal static ConfigEntry<float> Multiplier;

	private static void Enable()
	{
		Multiplier = Plugin.Cfg.Bind("FogOfWar", "VisionMultiplier", DefaultMultiplier, new ConfigDescription("How far units see through the fog of war, times the game's distance for the map size.", new AcceptableValueRange<float>(0.5f, 3f)));
	}

	private static void Postfix(ref int __result)
	{
		if (Multiplier != null && __result > 0)
		{
			float multiplier = Multiplier.Value;
			// The fog is each player's own view: in a multiplayer game without cheats no player sees further than the
			// default lets everyone (CheatGuard).
			if (multiplier > DefaultMultiplier && Multiplayer.CheatGuard.Restricted)
			{
				multiplier = DefaultMultiplier;
			}
			__result = UnityEngine.Mathf.RoundToInt(__result * multiplier);
		}
	}
}

// The fog of war is on in every game mode by default: the sandbox mode switched it off when it started
// (Rules.ToggleFog(false) in its serverside.lua). [FogOfWar] AlwaysOn = false leaves it to the game mode again; the
// debug menu's switch still turns it off and on.
[Feature]
[HarmonyPatch(typeof(LocalFactionVisionConfigurator), "lua_ToggleFog")]
internal static class FogAlwaysOn
{
	private static ConfigEntry<bool> _alwaysOn;

	private static void Enable()
	{
		_alwaysOn = Plugin.Cfg.Bind("FogOfWar", "AlwaysOn", true, "Keep the fog of war on in every game mode (the sandbox switches it off otherwise).");
	}

	private static bool Prefix(MoonSharp.Interpreter.CallbackArguments args, ref MoonSharp.Interpreter.DynValue __result)
	{
		if (_alwaysOn == null || !_alwaysOn.Value || args.Count == 0 || args[0].CastToBool())
		{
			return true;
		}
		__result = MoonSharp.Interpreter.DynValue.Void;
		return false;
	}
}
