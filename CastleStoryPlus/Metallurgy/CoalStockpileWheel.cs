using System.Collections.Generic;
using Brix.Game;
using Brix.Game.Network;
using Brix.Game.Semantique;
using Brix.Legacy;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Metallurgy;

// The stockpile wheel's "accept only" ring lists the game's own resources from its wheel files, so coal (the game's
// Clay) had no button. The button is added to the files as they are read, next to raw iron, with the same command
// the game's own buttons use (Event_Stockpile_Exclude_<resource>): the stockpile is made exclusive on the host and
// the clients, saved with it and kept by the workers' storage search like any other "accept only" resource. The
// stockpile wheel is the same for the pallet, the large stockpile and the warehouse. Without Metallurgy the files
// are left as they are, so there is no button.
[Feature(Features.Metallurgy, Features.MetallurgyInfo)]
internal static class CoalStockpileWheel
{
	internal const string Button = "ExclusionCoal";

	internal const string Event = "Event_Stockpile_Exclude_Clay";

	private const string Hub = "Submenu: StockpileExclusivityHub";

	private const string After = "Button: ExclusionIron";

	internal static void RegisterCommand()
	{
		Instructions.commendes[Event] = () =>
		{
			Instructions.Try<Recepteur>((Recepteur r) =>
			{
				UNetProjectCmd cmd = (User.LocalUser != null) ? User.LocalUser.GetComponent<UNetProjectCmd>() : null;
				if (cmd != null && r != null)
				{
					cmd.CallCmdStockpileAcceptOnly(r.gameObject, "Clay");
				}
			}, Instructions.ForSubjects<Recepteur>, Instructions.ForTarget<Recepteur>);
		};
	}

	// The wheel files as they are read: the button's definition after the game's own exclusion buttons, and the
	// button in the "accept only" ring after raw iron.
	internal static string AddToWheel(string text)
	{
		if (text.Contains("BUTTON ExclusionOrangeCrystal") && !text.Contains("BUTTON " + Button))
		{
			Metallurgy.EnsureIcons();
			string icon = (Metallurgy.CoalIcon != null) ? Metallurgy.CoalIcon.Name : "_Raw_Iron";
			return text + "\n\nBUTTON " + Button + "\nName : Coal\nEffect: " + Event + "\nCondition : Condition_CanChangeSettings\nIcon : " + icon + "\nTooltip : Only coal is brought to this stockpile.\n";
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
		Plugin.Log.LogWarning("Metallurgy: raw iron not found in the stockpile's accept-only wheel");
		return text;
	}
}

[Feature(Features.Metallurgy, Features.MetallurgyInfo)]
[HarmonyPatch(typeof(Instructions), nameof(Instructions.Batire))]
internal static class CoalStockpileWheelCommandPatch
{
	private static void Postfix()
	{
		CoalStockpileWheel.RegisterCommand();
	}
}

[Feature(Features.Metallurgy, Features.MetallurgyInfo)]
[HarmonyPatch(typeof(Livre), nameof(Livre.Split))]
internal static class CoalStockpileWheelFilesPatch
{
	private static void Prefix(ref string contenu)
	{
		if (contenu != null)
		{
			contenu = CoalStockpileWheel.AddToWheel(contenu);
		}
	}
}
