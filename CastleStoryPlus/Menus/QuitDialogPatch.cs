using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Brix.Assets;
using Brix.Engine;
using Brix.External.Signals;
using Brix.Game;
using Brix.Network;
using Brix.UI.Builder.Menu;
using Brix.Utils;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Menus;

// Adds SAVE & LEAVE to the Quit Game dialog. A loaded manual save is overwritten; anything else
// (new map, quicksave, autosave) becomes a new manual save named after the map and the time.
[Feature(Features.SaveAndLeave, Features.SaveAndLeaveInfo)]
[HarmonyPatch(typeof(Container), nameof(Container.ItemClick))]
internal static class QuitDialogPatch
{
	private static bool Prefix(Container __instance, int itemIdx)
	{
		if (__instance.GetComponent<ContainerGenericPopulator>() == null || itemIdx < 0 || itemIdx >= __instance.items.Count)
		{
			return true;
		}
		if (__instance.items[itemIdx].containerPopulatorOperation != LegacyBaseMenu.Operation.QuitGame || !CanSaveBeforeLeaving())
		{
			return true;
		}
		ShowQuitDialog();
		return false;
	}

	private static void ShowQuitDialog()
	{
		List<OkDialogContainerPopulator.GenericDialog.Btn> buttons = new List<OkDialogContainerPopulator.GenericDialog.Btn>();
		// "&&" makes I2Helper.TryGet return the literal text instead of looking up a term.
		buttons.Add(new OkDialogContainerPopulator.GenericDialog.Btn("&&SAVE & LEAVE", () =>
		{
			GameSignals.Invoke(GameSignals.OnCloseDialog);
			if (SaveBeforeLeaving())
			{
				Architecte.QuitToMainMenu();
			}
		}, string.Empty));
		buttons.Add(new OkDialogContainerPopulator.GenericDialog.Btn(I2Helper.TryGet("##gamemenu_quit_exitdesktop").ToUpper(), () =>
		{
			GameSignals.Invoke(GameSignals.QuitThroughMenu);
			ApplicationQuitter.Quit();
		}, string.Empty));
		buttons.Add(new OkDialogContainerPopulator.GenericDialog.Btn(I2Helper.TryGet("##gamemenu_quit_leavegame").ToUpper(), () =>
		{
			GameSignals.Invoke(GameSignals.OnCloseDialog);
			Architecte.QuitToMainMenu();
		}, string.Empty));
		buttons.Add(new OkDialogContainerPopulator.GenericDialog.Btn(I2Helper.TryGet("##gamemenu_cancel").ToUpper(), () =>
		{
			GameSignals.Invoke(GameSignals.OnCloseDialog);
		}, string.Empty));
		GameSignals.Invoke(GameSignals.OnDisplayGenericDialog, new OkDialogContainerPopulator.GenericDialog(I2Helper.TryGet("##gamemenu_quit_game").ToUpper(), I2Helper.TryGet("##gamemenu_quit_message"), buttons.ToArray()));
	}

	// Only the host can save in multiplayer.
	private static bool CanSaveBeforeLeaving()
	{
		if (GameParam.Map == null)
		{
			return false;
		}
		return !Neo.IsMultiplayer || NetworkServer.active;
	}

	private static bool SaveBeforeLeaving()
	{
		try
		{
			StatsTracker.SaveStats();
			Asset_Map loadedMap = GameParam.Map;
			if (loadedMap.mapType == Asset_Map.MapType.Save && loadedMap.saveType == Asset_Map.SaveType.Manualsave && !loadedMap.IsShell())
			{
				loadedMap.Disk_Overwrite();
				return true;
			}
			string baseName = !string.IsNullOrEmpty(loadedMap.customName) ? loadedMap.customName : loadedMap.rawDisplayName;
			if (string.IsNullOrEmpty(baseName))
			{
				baseName = I2Helper.TryGet("##gamemenu_savegame_untitled_filename");
			}
			foreach (char invalid in Path.GetInvalidFileNameChars())
			{
				baseName = baseName.Replace(invalid, '_');
			}
			Asset_Map save = Asset_Map.CreateShellFrom(loadedMap, Asset_Map.MapType.Save);
			save.customName = baseName.Trim('-', ' ') + " " + DateTime.Now.ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture);
			save.saveType = Asset_Map.SaveType.Manualsave;
			save.Disk_WriteAsNew(AssetAuthorType.Official);
			return true;
		}
		catch (Exception ex)
		{
			Debug.LogException(ex);
			GameSignals.Invoke(GameSignals.OnDisplayGenericDialog, new OkDialogContainerPopulator.GenericDialog(I2Helper.TryGet("##gamemenu_savegame_failure").ToUpper(), I2Helper.TryGet("##gamemenu_savegame_failure_message") + "\n\n\"" + ex.Message + "\"", new OkDialogContainerPopulator.GenericDialog.Btn[1]
			{
				new OkDialogContainerPopulator.GenericDialog.Btn(I2Helper.TryGet("##gamemenu_savegame_failure_ok").ToUpper(), () =>
				{
					GameSignals.Invoke(GameSignals.OnCloseDialog);
				}, string.Empty)
			}));
			return false;
		}
	}
}
