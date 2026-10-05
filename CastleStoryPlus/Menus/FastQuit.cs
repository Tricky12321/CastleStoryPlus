using System.Collections;
using System.Diagnostics;
using Brix.Utils;
using CastleStoryPlus.Core;
using CastleStoryPlus.Saving;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Menus;

// Exit (main menu or in a game) closes the game at once. The game's quit did the slow work twice: it deactivated and
// destroyed every object of every scene and of the object pools one by one (each running its own clean-up), and
// then ended the process with CloseMainWindow, which on Linux (Mono) is SIGTERM, so Unity then shut itself down
// in full and unloaded every loaded object and asset again. All of it is thrown away with the process. Now the
// settings are saved (PlayerPrefs; the BepInEx config is saved whenever it changes), a running autosave may finish
// (up to 30 s), and the process is ended right away.
[Feature(Features.FastQuit, Features.FastQuitInfo)]
[HarmonyPatch(typeof(ApplicationQuitter), nameof(ApplicationQuitter.Quit))]
internal static class FastQuit
{
	private const float AutosaveWaitSeconds = 30f;

	private static bool _quitting;

	private static bool Prefix()
	{
		if (_quitting)
		{
			return false;
		}
		_quitting = true;
		Plugin.Log.LogInfo("Quitting to desktop");
		PlayerPrefs.Save();
		Plugin.Root.AddComponent<FastQuitRunner>();
		return false;
	}

	internal class FastQuitRunner : MonoBehaviour
	{
		private IEnumerator Start()
		{
			float until = Time.realtimeSinceStartup + AutosaveWaitSeconds;
			while (AutoSave.IsSaving && Time.realtimeSinceStartup < until)
			{
				yield return null;
			}
			Plugin.Log.LogInfo("Ending the process");
			// SIGKILL: no shutdown of the engine (CloseMainWindow would be SIGTERM and a full Unity shutdown).
			Process.GetCurrentProcess().Kill();
		}
	}
}
