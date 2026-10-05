using System;
using System.Collections.Generic;
using System.Reflection;
using Brix.Assets;
using Brix.Engine;
using Brix.External.Signals;
using Brix.UI.Builder.Menu;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Menus;

// Title screen: clones the Play button into a CONTINUE button right below it. The title buttons are
// defined in the scene, so the item is added at runtime.
[Feature(Features.ContinueButton, Features.ContinueButtonInfo)]
[HarmonyPatch(typeof(ContainerGenericPopulator), nameof(ContainerGenericPopulator.OnEnable))]
internal static class ContinueButtonPatch
{
	private const string Title = "&&CONTINUE";

	private static void Postfix(ContainerGenericPopulator __instance)
	{
		Container container = __instance.container;
		// The menu scene is still loading here, so the active scene is the previous one: use the button's own scene.
		if (container == null || __instance.gameObject.scene.name != "SceneMenu" || container.GetItem(MenuOperations.ContinueGame) != null)
		{
			return;
		}
		int playIdx = container.items.FindIndex((Container.Item i) => i.titleText != null && i.titleText.Equals("##mainmenu_play", StringComparison.OrdinalIgnoreCase));
		if (playIdx < 0)
		{
			if (container.GetItem(LegacyBaseMenu.Operation.QuitApplication) != null)
			{
				Plugin.Log.LogWarning("Continue: Play button not found in " + __instance.name + ": " + string.Join(", ", container.items.ConvertAll((Container.Item i) => i.titleText).ToArray()));
			}
			return;
		}
		Container.Item play = container.items[playIdx];
		// A copy of every setting of Play (state, group, layout, contexts...): an item left in the default Closed
		// state is hidden by the container.
		Container.Item item = new Container.Item();
		foreach (FieldInfo field in typeof(Container.Item).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
		{
			field.SetValue(item, field.GetValue(play));
		}
		item.instance = null;
		item.titleText = Title;
		item.localizeTitle = string.Empty;
		item.actionItems = new List<Container.Item.ActionItem>();
		item.isDefaultInGroup = false;
		item.doClick = false;
		item.containerPopulatorOperation = MenuOperations.ContinueGame;
		if (!play.isDynamic)
		{
			if (play.instance == null)
			{
				return;
			}
			GameObject clone = UnityEngine.Object.Instantiate(play.instance.gameObject, play.instance.transform.parent, worldPositionStays: false);
			clone.name = "Continue";
			clone.transform.SetSiblingIndex(play.instance.transform.GetSiblingIndex() + 1);
			item.instance = clone.GetComponent<Containee>();
			item.instance.container = container;
			if (item.instance.title != null)
			{
				item.instance.title.text = "CONTINUE";
			}
		}
		container.items.Insert(playIdx + 1, item);
		Plugin.Log.LogInfo("Continue button added to " + __instance.name);
		if (play.isDynamic && container.isActiveAndEnabled)
		{
			container.Populate(isChain: false);
		}
	}
}

// Loads the newest single-player save, like picking it in Load Game and pressing Play.
[Feature(Features.ContinueButton, Features.ContinueButtonInfo)]
[HarmonyPatch(typeof(Container), nameof(Container.ItemClick))]
internal static class ContinueClickPatch
{
	private static bool Prefix(Container __instance, int itemIdx)
	{
		if (itemIdx < 0 || itemIdx >= __instance.items.Count || __instance.items[itemIdx].containerPopulatorOperation != MenuOperations.ContinueGame)
		{
			return true;
		}
		ContinueLatestSave();
		return false;
	}

	private static void ContinueLatestSave()
	{
		Asset_Map newest = null;
		foreach (Asset_Map save in AssetDispensary.saves.SendQuery(AssetQueryParameters.AllSPSources | AssetQueryParameters.RefreshCache, AssetPlayableContext.Singleplayer))
		{
			if (save != null && save.IsCurrentVersion() && (newest == null || save.dateTimeCreated > newest.dateTimeCreated))
			{
				newest = save;
			}
		}
		if (newest != null)
		{
			BaseMapLibraryMenu.SetSelectedMap(newest, AssetPlayableContext.Singleplayer, BaseAssetMenu.MenuGameContext.Normal, BaseAssetMenu.MenuTypeContext.Existing);
		}
		if (newest == null || GameParam.SelectedGamemode == null)
		{
			BaseMapLibraryMenu.EmptySelected();
			GameSignals.Invoke(GameSignals.OnDisplayGenericDialog, new OkDialogContainerPopulator.GenericDialog("CONTINUE", "No save game found.", new OkDialogContainerPopulator.GenericDialog.Btn[1]
			{
				new OkDialogContainerPopulator.GenericDialog.Btn("&&OK", () =>
				{
					GameSignals.Invoke(GameSignals.OnCloseDialog);
				}, string.Empty)
			}));
			return;
		}
		BaseMapLibraryMenu.SetSelectedAsLoaded();
		GameParam.StartGame(() =>
		{
			BaseMapLibraryMenu.EmptySelected();
		});
		MenuSound.PlaySound(MenuSound.Prefix.Title, MenuSound.Suffix.GameStart);
	}
}
