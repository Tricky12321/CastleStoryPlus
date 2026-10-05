using System;
using UnityEngine.SceneManagement;

namespace CastleStoryPlus.Core;

// Static state that belongs to one game (keyed by bricktrons, goals, items or projectiles) must be dropped when
// the game is left. Otherwise the destroyed objects, and everything they reference, stay in memory for every game
// played in the session.
internal static class GameSession
{
	public static void OnLeave(Action clear)
	{
		SceneManager.activeSceneChanged += (Scene from, Scene to) => clear();
	}
}
