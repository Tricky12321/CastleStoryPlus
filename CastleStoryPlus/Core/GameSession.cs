using System;
using System.Collections.Generic;
using Brix.Engine;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace CastleStoryPlus.Core;

// Static state that belongs to one game (keyed by bricktrons, goals, items or projectiles) must be dropped when
// the game is left. Otherwise the destroyed objects, and everything they reference, stay in memory for every game
// played in the session.
internal static class GameSession
{
	internal static readonly List<Action> LevelReadyActions = new List<Action>();

	public static void OnLeave(Action clear)
	{
		SceneManager.activeSceneChanged += (Scene from, Scene to) => clear();
	}

	// Each time a level is ready (GameSignals.LevelReady). The game clears its signals' connections when a scene
	// starts and ends, so one made when the plugin starts is gone by the time a level loads.
	public static void OnLevelReady(Action action)
	{
		LevelReadyActions.Add(action);
	}
}

[Feature]
[HarmonyPatch(typeof(GameSignals), nameof(GameSignals.Invoke), typeof(GameSignals.GameSignal))]
internal static class GameSessionLevelReadyPatch
{
	private static void Postfix(GameSignals.GameSignal gs)
	{
		if (gs != GameSignals.LevelReady)
		{
			return;
		}
		foreach (Action action in GameSession.LevelReadyActions)
		{
			try
			{
				action();
			}
			catch (Exception ex)
			{
				Plugin.Log.LogError("Level ready: " + ex);
			}
		}
	}
}
