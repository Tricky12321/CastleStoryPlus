using BepInEx.Configuration;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Rules;
using Brix.Lua;
using CastleStoryPlus.Core;
using UnityEngine;

namespace CastleStoryPlus.Workers;

// "Auto spawn bricktrons" (the home crystal's popup): while on, a new bricktron is made as soon as the home crystal has
// the energy for one and the team is under its bricktron limit, as a click on the crystal's spawn button does. Per
// player ([Crystal] AutoSpawn), so in multiplayer each player decides for their own crystal.
[Feature]
internal sealed class AutoSpawn : MonoBehaviour
{
	// Seconds between two spawns: the crystal's count of spawnable bricktrons drops once the host has made the last.
	private const float Interval = 3f;

	private static ConfigEntry<bool> _on;

	private float _nextAt;

	internal static bool On
	{
		get { return _on != null && _on.Value; }
		set
		{
			if (_on != null)
			{
				_on.Value = value;
			}
		}
	}

	private static void Enable()
	{
		_on = Plugin.Cfg.Bind("Crystal", "AutoSpawn", false, "Make a new bricktron whenever the home crystal has the energy for one (the home crystal's \"Auto spawn bricktrons\").");
		Plugin.Root.AddComponent<AutoSpawn>();
	}

	private void Update()
	{
		if (!On || Time.unscaledTime < _nextAt)
		{
			return;
		}
		_nextAt = Time.unscaledTime + Interval;
		try
		{
			FireflyNest nest = FireflyNest.GetAlliedFireflyNest();
			if (nest == null || nest.newSpawnableFireflyCount < 1f)
			{
				return;
			}
			int count = (UIGameObserver.bricktrons != null) ? UIGameObserver.bricktrons.CheckCreation().Count : 0;
			if (count >= GeneralRules.Main.bricktronCap)
			{
				return;
			}
			nest.AssignJobRequest(Occupation.Type.Builder, null);
		}
		catch (System.Exception)
		{
			// Not in a game (no crystal yet, or the menu): nothing to spawn.
		}
	}
}
