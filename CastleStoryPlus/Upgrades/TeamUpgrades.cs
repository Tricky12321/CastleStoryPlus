using System;
using System.Collections.Generic;
using System.Text;
using Brix.Game;
using Brix.Game.AI;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace CastleStoryPlus.Upgrades;

// The researched tier of every upgrade line, per faction. The game has no field for it, so it lives here, keyed by
// the Faction component, and is saved with it (TeamUpgradesSave) and synced with it (TeamUpgradesNetwork).
internal static class TeamUpgrades
{
	private static readonly Dictionary<Faction, int[]> All = new Dictionary<Faction, int[]>();

	// Raised on every peer when a faction's tiers change (on the server, or received by a client).
	public static event Action<Faction> Changed;

	// Entries of factions destroyed with the scene of a left game go once the scene is unloaded. Not cleared
	// outright: that could run after a save has loaded new tiers.
	static TeamUpgrades()
	{
		SceneManager.sceneUnloaded += (Scene scene) => PruneDestroyed();
	}

	public static int Tier(Faction faction, int line)
	{
		if (faction == null || !All.TryGetValue(faction, out int[] tiers))
		{
			return 0;
		}
		return tiers[line];
	}

	public static int Tier(GameObject unit, int line)
	{
		return (unit != null) ? Tier(Faction.GetFaction(unit), line) : 0;
	}

	// The line's effect for the unit's faction, e.g. 0.3 for +30%.
	public static float Value(GameObject unit, int line)
	{
		int tier = Tier(unit, line);
		return (tier > 0) ? UpgradeLines.All[line].Value[tier] : 0f;
	}

	// Server: a research finished. Raises the tier (never lowers it), syncs it to clients and updates the health
	// of the faction's bricktrons if the helmet changed.
	public static void Unlock(Faction faction, int line, int tier)
	{
		if (faction == null || !NetworkServer.active || Tier(faction, line) >= tier)
		{
			return;
		}
		TiersOf(faction)[line] = tier;
		faction.SetDirtyBit(TeamUpgradesNetwork.DirtyBit);
		Plugin.Log.LogInfo("Upgrades: " + faction.name + " researched " + UpgradeLines.All[line].Name + " tier " + tier);
		if (line == UpgradeLines.Helmet)
		{
			RefreshHealth(faction);
		}
		Changed?.Invoke(faction);
	}

	// Max health comes from the tool statistics (Profession.SetHpBonus); recompile them for the faction's units.
	private static void RefreshHealth(Faction faction)
	{
		foreach (Toolbag toolbag in UnityEngine.Object.FindObjectsOfType<Toolbag>())
		{
			if (toolbag != null && Faction.GetFaction(toolbag.gameObject) == faction)
			{
				toolbag.CompileToolStatistics();
			}
		}
	}

	// "2,1,0,..." in line order; empty when nothing is researched.
	public static string Encode(Faction faction)
	{
		if (faction == null || !All.TryGetValue(faction, out int[] tiers))
		{
			return string.Empty;
		}
		StringBuilder text = new StringBuilder();
		for (int i = 0; i < tiers.Length; i++)
		{
			if (i > 0)
			{
				text.Append(',');
			}
			text.Append(tiers[i]);
		}
		return text.ToString();
	}

	public static void Decode(Faction faction, string text)
	{
		if (faction == null || string.IsNullOrEmpty(text))
		{
			return;
		}
		int[] tiers = TiersOf(faction);
		string[] parts = text.Split(',');
		for (int i = 0; i < parts.Length && i < tiers.Length; i++)
		{
			if (int.TryParse(parts[i], out int tier))
			{
				tiers[i] = Mathf.Clamp(tier, 0, UpgradeLines.MaxTier);
			}
		}
		Changed?.Invoke(faction);
	}

	// Network: the tiers as one byte each, always all lines.
	public static void Write(Faction faction, NetworkWriter writer)
	{
		All.TryGetValue(faction, out int[] tiers);
		for (int i = 0; i < UpgradeLines.All.Count; i++)
		{
			writer.Write((byte)((tiers != null) ? tiers[i] : 0));
		}
	}

	public static void Read(Faction faction, NetworkReader reader)
	{
		bool changed = false;
		int[] tiers = TiersOf(faction);
		for (int i = 0; i < UpgradeLines.All.Count; i++)
		{
			int tier = reader.ReadByte();
			if (tiers[i] != tier)
			{
				tiers[i] = tier;
				changed = true;
			}
		}
		if (changed)
		{
			Changed?.Invoke(faction);
		}
	}

	private static int[] TiersOf(Faction faction)
	{
		if (!All.TryGetValue(faction, out int[] tiers))
		{
			tiers = new int[UpgradeLines.All.Count];
			All[faction] = tiers;
		}
		return tiers;
	}

	private static void PruneDestroyed()
	{
		List<Faction> destroyed = null;
		foreach (Faction faction in All.Keys)
		{
			if (faction == null)
			{
				if (destroyed == null)
				{
					destroyed = new List<Faction>();
				}
				destroyed.Add(faction);
			}
		}
		if (destroyed == null)
		{
			return;
		}
		foreach (Faction faction in destroyed)
		{
			All.Remove(faction);
		}
	}
}
