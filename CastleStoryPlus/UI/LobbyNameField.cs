#if UNITY6
using System;
using System.Reflection;
using Brix.UI.Builder.Menu;
using Brix.UI.Scripts;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine.UI;

namespace CastleStoryPlus.UI;

// The lobby's left menu refreshes its items whenever the player list changes (another player's name, faction or team,
// a player joining or leaving) and then writes the saved name into the name box. A player typing a new name had it
// replaced by the old one each time another player renamed themselves (or the list changed otherwise), so only one
// player at a time could change their name. While the box is being typed in, what is typed (and the caret) is kept.
[Feature(Features.MenuTextFields, Features.MenuTextFieldsInfo)]
[HarmonyPatch]
internal static class LobbyNameField
{
	internal struct Typed
	{
		public InputField Field;

		public string Text;

		public int Caret;

		public int Anchor;

		public int Focus;
	}

	// The menu's RefreshItems is a static delegate field (a lambda); its method is patched.
	private static MethodBase TargetMethod()
	{
		FieldInfo field = AccessTools.Field(typeof(LegacyLobbyLeftMenu), "RefreshItems");
		Delegate refresh = (field != null) ? field.GetValue(null) as Delegate : null;
		return (refresh != null) ? refresh.Method : null;
	}

	private static void Prefix(object[] __args, out Typed __state)
	{
		__state = default;
		LegacyLobbyLeftMenu menu = (__args != null && __args.Length > 0) ? __args[0] as LegacyLobbyLeftMenu : null;
		if (menu == null || menu.TargetContainer == null)
		{
			return;
		}
		foreach (Container.Item item in menu.TargetContainer.items)
		{
			if (item.containerPopulatorOperation != LegacyBaseMenu.Operation.PlayerNameEdit || item.instance == null)
			{
				continue;
			}
			InputField field = item.instance.actualInputField;
			if (field != null && field.isFocused)
			{
				__state = new Typed { Field = field, Text = field.text, Caret = field.caretPosition, Anchor = field.selectionAnchorPosition, Focus = field.selectionFocusPosition };
			}
			return;
		}
	}

	private static void Postfix(Typed __state)
	{
		InputField field = __state.Field;
		if (field == null || field.text == __state.Text)
		{
			return;
		}
		field.text = __state.Text;
		field.caretPosition = __state.Caret;
		field.selectionAnchorPosition = __state.Anchor;
		field.selectionFocusPosition = __state.Focus;
	}
}
#endif
