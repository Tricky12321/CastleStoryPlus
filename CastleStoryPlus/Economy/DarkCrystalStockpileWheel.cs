using System.Collections.Generic;
using Brix.Game;
using Brix.Game.Network;
using Brix.Game.Semantique;
using Brix.Legacy;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Economy;

// The stockpile wheel's "accept only" ring lists the game's own resources from its wheel files, so dark crystal (the
// game's PurifiedBlueCrystal) had no button. The button is added to the files as they are read, next to brimstone,
// with the same command the game's own buttons use (Event_Stockpile_Exclude_<resource>).
[Feature(Features.DarkCrystals, Features.DarkCrystalsInfo)]
internal static class DarkCrystalStockpileWheel
{
	internal const string Button = "ExclusionDarkCrystal";

	private const string Event = "Event_Stockpile_Exclude_PurifiedBlueCrystal";

	private const string Hub = "Submenu: StockpileExclusivityHub";

	private const string After = "Button: ExclusionOrangeCrystal";

	internal static void RegisterCommand()
	{
		Instructions.commendes[Event] = () =>
		{
			Instructions.Try<Recepteur>((Recepteur r) =>
			{
				UNetProjectCmd cmd = (User.LocalUser != null) ? User.LocalUser.GetComponent<UNetProjectCmd>() : null;
				if (cmd != null && r != null)
				{
					cmd.CallCmdStockpileAcceptOnly(r.gameObject, "PurifiedBlueCrystal");
				}
			}, Instructions.ForSubjects<Recepteur>, Instructions.ForTarget<Recepteur>);
		};
	}

	// The wheel files as they are read: the button's definition after the game's own exclusion buttons, and the
	// button in the "accept only" ring after brimstone.
	internal static string AddToWheel(string text)
	{
		if (text.Contains("BUTTON ExclusionOrangeCrystal") && !text.Contains("BUTTON " + Button))
		{
			DarkCrystals.EnsureIcons();
			string icon = (DarkCrystals.Icon != null) ? DarkCrystals.Icon.Name : "_Raw_Blue_Crystal";
			return text + "\n\nBUTTON " + Button + "\nName : Dark crystal\nEffect: " + Event + "\nCondition : Condition_CanChangeSettings\nIcon : " + icon + "\nTooltip : Only dark crystal is brought to this stockpile.\n";
		}
		if (!text.Contains(Hub) || text.Contains("Button: " + Button))
		{
			return text;
		}
		List<string> lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
		int hub = lines.FindIndex((string l) => l.Trim() == Hub);
		if (hub < 0)
		{
			return text;
		}
		for (int i = hub + 1; i < lines.Count && lines[i].Trim() != "End"; i++)
		{
			if (lines[i].Trim() == After)
			{
				string indent = lines[i].Substring(0, lines[i].Length - lines[i].TrimStart().Length);
				lines.Insert(i + 1, indent + "Button: " + Button);
				return string.Join("\n", lines.ToArray());
			}
		}
		Plugin.Log.LogWarning("DarkCrystals: brimstone not found in the stockpile's accept-only wheel");
		return text;
	}
}

[Feature(Features.DarkCrystals, Features.DarkCrystalsInfo)]
[HarmonyPatch(typeof(Instructions), nameof(Instructions.Batire))]
internal static class DarkCrystalStockpileWheelCommandPatch
{
	private static void Postfix()
	{
		DarkCrystalStockpileWheel.RegisterCommand();
	}
}

[Feature(Features.DarkCrystals, Features.DarkCrystalsInfo)]
[HarmonyPatch(typeof(Livre), nameof(Livre.Split))]
internal static class DarkCrystalStockpileWheelFilesPatch
{
	private static void Prefix(ref string contenu)
	{
		if (contenu != null)
		{
			contenu = DarkCrystalStockpileWheel.AddToWheel(contenu);
		}
	}
}
