using System.Collections.Generic;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Network;
using Brix.Legacy;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace CastleStoryPlus.Army;

// Two UNET commands on the player's UNetProjectCmd: set or end a ranged soldier's guard (RangedGuard), and set or
// clear a unit's post (SoldierPosts). Registered by hand, like the game's weaved commands. The host decides and syncs
// the result with the unit's stats.
[Feature]
internal static class SoldierOrdersNetwork
{
	private const int GuardHash = 1129595270;

	private const int PostHash = 1129595271;

	private static void Enable()
	{
		NetworkBehaviour.RegisterCommandDelegate(typeof(UNetProjectCmd), GuardHash, InvokeGuard);
		NetworkBehaviour.RegisterCommandDelegate(typeof(UNetProjectCmd), PostHash, InvokePost);
	}

	public static void SendGuard(GameObject laborGO, bool on)
	{
		UNetProjectCmd cmd = (User.LocalUser != null) ? User.LocalUser.GetComponent<UNetProjectCmd>() : null;
		if (cmd == null || laborGO == null)
		{
			return;
		}
		if (cmd.isServer)
		{
			Guard(cmd, laborGO, on);
			return;
		}
		NetworkWriter writer = Begin(cmd, GuardHash);
		writer.Write(laborGO);
		writer.Write(on);
		cmd.SendCommandInternal(writer, 0, "CmdSetRangedGuard");
	}

	public static void SendSetPost(GameObject laborGO, bool set, Vector3 position)
	{
		UNetProjectCmd cmd = (User.LocalUser != null) ? User.LocalUser.GetComponent<UNetProjectCmd>() : null;
		if (cmd == null || laborGO == null)
		{
			return;
		}
		if (cmd.isServer)
		{
			Post(cmd, laborGO, set, position);
			return;
		}
		NetworkWriter writer = Begin(cmd, PostHash);
		writer.Write(laborGO);
		writer.Write(set);
		writer.Write(position);
		cmd.SendCommandInternal(writer, 0, "CmdSetSoldierPost");
	}

	private static NetworkWriter Begin(UNetProjectCmd cmd, int hash)
	{
		NetworkWriter writer = new NetworkWriter();
		writer.Write((short)0);
		writer.Write((short)5);
		writer.WritePackedUInt32((uint)hash);
		writer.Write(cmd.GetComponent<NetworkIdentity>().netId);
		return writer;
	}

	private static void InvokeGuard(NetworkBehaviour obj, NetworkReader reader)
	{
		if (!NetworkServer.active)
		{
			return;
		}
		GameObject laborGO = reader.ReadGameObject();
		bool on = reader.ReadBoolean();
		Guard((UNetProjectCmd)obj, laborGO, on);
	}

	private static void InvokePost(NetworkBehaviour obj, NetworkReader reader)
	{
		if (!NetworkServer.active)
		{
			return;
		}
		GameObject laborGO = reader.ReadGameObject();
		bool set = reader.ReadBoolean();
		Vector3 position = reader.ReadVector3();
		Post((UNetProjectCmd)obj, laborGO, set, position);
	}

	private static void Guard(UNetProjectCmd cmd, GameObject laborGO, bool on)
	{
		if (laborGO == null || !Affiliation.IsOwnedBy(laborGO, cmd.gameObject) || !Plugin.IsEnabled(Features.RangedGuard))
		{
			return;
		}
		Labor labor = laborGO.GetComponent<Labor>();
		if (labor != null && (!on || RangedGuard.IsRanged(labor)))
		{
			RangedGuard.Set(labor, on);
		}
	}

	private static void Post(UNetProjectCmd cmd, GameObject laborGO, bool set, Vector3 position)
	{
		if (laborGO == null || !Affiliation.IsOwnedBy(laborGO, cmd.gameObject) || !SoldierPosts.Enabled)
		{
			return;
		}
		SoldierPosts.Set(laborGO.GetComponent<Labor>(), set, position);
	}
}

// The buttons in the units' action wheels (the game's wheel files, Info/Livre, as they are read):
// - ranged soldiers: "Guard here" / "Stop guarding" in a free slot of their wheel over the ground, over themselves and
//   anywhere (RangedGuard);
// - knights and ranged soldiers, wheel over themselves: "Set post", "Back to post", "Clear post"; a mixed group, wheel
//   over the ground: "Set post", "Back to post" (SoldierPosts).
[Feature]
[HarmonyPatch(typeof(Livre), nameof(Livre.Split))]
internal static class SoldierOrdersWheel
{
	private const string GuardButton = "PlusRangedGuard";

	private const string SetPostButton = "PlusSetPost";

	private const string BackToPostButton = "PlusBackToPost";

	private const string ClearPostButton = "PlusClearPost";

	private static void Prefix(ref string contenu)
	{
		if (contenu != null)
		{
			contenu = AddToWheel(contenu);
		}
	}

	// The selected units of the local player.
	private static List<Labor> Selected()
	{
		List<Labor> result = new List<Labor>();
		if (Instructions.sujets == null)
		{
			return result;
		}
		foreach (MonoBehaviour subject in Instructions.sujets)
		{
			Labor labor = (subject != null) ? subject.GetComponent<Labor>() : null;
			if (labor != null && !result.Contains(labor) && Affiliation.BelongsToMe(labor.gameObject))
			{
				result.Add(labor);
			}
		}
		return result;
	}

	private static List<Labor> SelectedRanged()
	{
		return Selected().FindAll(RangedGuard.IsRanged);
	}

	private static bool AllGuarding(List<Labor> units)
	{
		if (units.Count == 0)
		{
			return false;
		}
		foreach (Labor labor in units)
		{
			if (!RangedGuard.IsGuarding(labor))
			{
				return false;
			}
		}
		return true;
	}

	private static bool AnyPost(List<Labor> units)
	{
		foreach (Labor labor in units)
		{
			if (SoldierPosts.Has(labor))
			{
				return true;
			}
		}
		return false;
	}

	internal static void RegisterWheel()
	{
		Instructions.conditions["Condition_" + GuardButton] = () => SelectedRanged().Count > 0;
		Instructions.conditions["Condition_" + GuardButton + "Label"] = () => AllGuarding(SelectedRanged()) ? "Stop guarding" : "Guard here";
		Instructions.commendes["Event_" + GuardButton] = () =>
		{
			List<Labor> units = SelectedRanged();
			// Mixed selection: all start guarding; all guarding: all stop.
			bool on = !AllGuarding(units);
			foreach (Labor labor in units)
			{
				SoldierOrdersNetwork.SendGuard(labor.gameObject, on);
			}
		};
		Instructions.conditions["Condition_" + SetPostButton] = () => Selected().Count > 0;
		Instructions.commendes["Event_" + SetPostButton] = () =>
		{
			int count = SoldierPosts.SetHere(Selected());
			Plugin.Log.LogInfo("SoldierPosts: post set for " + count + " units");
		};
		Instructions.conditions["Condition_" + BackToPostButton] = () => AnyPost(Selected());
		Instructions.commendes["Event_" + BackToPostButton] = () =>
		{
			SoldierPosts.SendBack(Selected());
		};
		Instructions.conditions["Condition_" + ClearPostButton] = () => AnyPost(Selected());
		Instructions.commendes["Event_" + ClearPostButton] = () =>
		{
			SoldierPosts.Clear(Selected());
		};
	}

	private static string Button(string name, string label, string labelCondition, string icon, string tooltip)
	{
		string text = "\n\nBUTTON " + name + "\nName : " + label + "\n";
		if (labelCondition != null)
		{
			text += "Label : " + labelCondition + "\n";
		}
		return text + "Condition : Condition_" + name + "\nEffect : Event_" + name + "\nIcon : " + icon + "\nSize : Native\nTooltip : " + tooltip + "\n";
	}

	internal static string AddToWheel(string text)
	{
		RegisterWheel();
		bool guard = Plugin.IsEnabled(Features.RangedGuard);
		bool posts = Plugin.IsEnabled(Features.SoldierPosts);
		if (!guard && !posts)
		{
			return text;
		}
		if (text.Contains("BUTTON HoldFireON") && !text.Contains("BUTTON " + GuardButton))
		{
			text += Button(GuardButton, "Guard here", "Condition_" + GuardButton + "Label", "_Defend_Area", "Stay on this place and shoot every enemy in range from here: no running out to the enemy or to a better shot (or again to stop guarding).");
			text += Button(SetPostButton, "Set post", null, "_UI_Flag", "Each selected unit keeps the place it stands on as its post. A call to arms sends it there, and Back to post too.");
			text += Button(BackToPostButton, "Back to post", null, "_Bricktron_MoveTo", "Send the selected units to their posts.");
			text += Button(ClearPostButton, "Clear post", null, "Cancel", "Forget the posts of the selected units: a call to arms sends them to the rally point of their class again.");
			return text;
		}
		if (!text.Contains("MENU ArcherSelf") || text.Contains("Button: " + GuardButton) || text.Contains("Button: " + SetPostButton))
		{
			return text;
		}
		List<string> lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
		if (guard)
		{
			FillHoles(lines, "ArcherMain", new[] { GuardButton });
			FillHoles(lines, "ArcherAnywhere", new[] { GuardButton });
			FillHoles(lines, "Artificer", new[] { GuardButton });
		}
		if (posts)
		{
			FillHoles(lines, "KnightSelf", new[] { SetPostButton, BackToPostButton, ClearPostButton });
			FillHoles(lines, "MixedBricktronsVoxel", new[] { SetPostButton, BackToPostButton });
		}
		FillHoles(lines, "ArcherSelf", new[] { posts ? SetPostButton : null, posts ? BackToPostButton : null, posts ? ClearPostButton : null, guard ? GuardButton : null });
		return string.Join("\n", lines.ToArray());
	}

	// Puts the buttons in the menu's first free slots in order (null keeps that slot free).
	private static void FillHoles(List<string> lines, string menu, string[] buttons)
	{
		int start = lines.FindIndex((string l) => l.Trim() == "MENU " + menu);
		if (start < 0)
		{
			Plugin.Log.LogWarning("SoldierOrders: no wheel " + menu);
			return;
		}
		int next = 0;
		for (int i = start + 1; i < lines.Count && next < buttons.Length && !lines[i].Trim().StartsWith("MENU "); i++)
		{
			if (lines[i].Trim() != "Hole")
			{
				continue;
			}
			if (buttons[next] != null)
			{
				lines[i] = "\tButton: " + buttons[next];
			}
			next++;
		}
		if (next < buttons.Length)
		{
			Plugin.Log.LogWarning("SoldierOrders: not enough free slots in the wheel " + menu);
		}
	}
}

// The wheel's conditions and commands are (re)built by the game's Instructions.Batire.
[Feature]
[HarmonyPatch(typeof(Instructions), nameof(Instructions.Batire))]
internal static class SoldierOrdersWheelCommandsPatch
{
	private static void Postfix()
	{
		SoldierOrdersWheel.RegisterWheel();
	}
}
