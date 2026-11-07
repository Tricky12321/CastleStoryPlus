#if UNITY6
using System.Collections.Generic;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine.UI;

namespace CastleStoryPlus.UI;

// A text box in the game's menus (the lobby's player name, ...) is a menu item whose InputField hands what was typed to
// its Containee (Containee.Text, which passes it on to the menu) through a listener stored in the menu's data. The
// port's data lost that listener: Enter and leaving the box did nothing, and the name was never set. Such a box gets
// the listener in code when its item is shown; a box that still has a stored listener is left alone.
[Feature(Features.MenuTextFields, Features.MenuTextFieldsInfo)]
[HarmonyPatch(typeof(Containee), "OnEnable")]
internal static class MenuTextFields
{
	private static readonly HashSet<InputField> Linked = new HashSet<InputField>();

	private static void Postfix(Containee __instance)
	{
		InputField field = (__instance.actualInputField != null) ? __instance.actualInputField : __instance.GetComponent<InputField>();
		if (field == null || field.onEndEdit.GetPersistentEventCount() > 0 || !Linked.Add(field))
		{
			return;
		}
		Containee containee = __instance;
		field.onEndEdit.AddListener((string text) =>
		{
			if (containee != null)
			{
				containee.Text(text);
			}
		});
		Linked.RemoveWhere(f => f == null);
	}
}
#endif
