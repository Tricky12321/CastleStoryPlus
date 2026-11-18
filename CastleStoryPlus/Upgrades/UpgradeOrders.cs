using System.Collections.Generic;
using System.IO;
using Path = System.IO.Path;
using Brix.Game;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Network;
using CastleStoryPlus.Core;
using UnityEngine;

namespace CastleStoryPlus.Upgrades;

// What the upgrade windows do with a station: queue a line's next tier, take a research out of the queue, and what
// they show of it (the queued tier, the tier's icon, the free stock of a material). Used by the upgrade window
// (UpgradePanel) and the research station's window in the C# menu.
internal static class UpgradeOrders
{
	// Most researches a station queues.
	internal const int MaxQueue = 5;

	private static readonly Dictionary<string, Sprite> Icons = new Dictionary<string, Sprite>();

	private static UNetProjectCmd Commands => (User.LocalUser != null) ? User.LocalUser.GetComponent<UNetProjectCmd>() : null;

	// The crystal the research station's studies are paid in.
	internal static Ressource ResearchCrystal => Economy.DarkCrystals.IsOn() ? Economy.DarkCrystals.Resource : Adjectif.blueCrystal;

	internal static Faction FactionOf(UpgradeStation station)
	{
		return (station != null) ? Faction.GetFaction(station.gameObject) : null;
	}

	// Queues the line's next tier; false when it is done, already queued, the queue is full or the host would refuse
	// it (locked at another station, or the stockpiles cannot pay it).
	internal static bool Research(UpgradeStation station, UpgradeLine line)
	{
		if (station == null || Commands == null || !UpgradeLines.IsEnabled(line))
		{
			return false;
		}
		Faction faction = FactionOf(station);
		int next = TeamUpgrades.Tier(faction, line.Index) + 1;
		List<RecipeInfo> queue = station.QueuedRecipes;
		if (next > line.Tiers || line.Recipe[next] == null || QueuedTier(queue, line) > 0 || (queue != null && queue.Count >= MaxQueue))
		{
			return false;
		}
		string reason = ResearchReservations.RefusalReason(faction, line, next, station, fresh: true);
		if (reason != null)
		{
			Plugin.Log.LogInfo("Upgrades: " + line.Name + " tier " + next + " not started: " + reason);
			return false;
		}
		Commands.CallCmdcraftingQueueRecipe(station.gameObject, line.Recipe[next].id, 1);
		ResearchReservations.Invalidate(faction);
		return true;
	}

	internal static void RemoveAt(UpgradeStation station, int index)
	{
		if (station != null && Commands != null)
		{
			Commands.CallCmdcraftingRemoveAt(station.gameObject, index);
			ResearchReservations.Invalidate(FactionOf(station));
		}
	}

	// The tier of the line waiting in the queue, 0 if none.
	internal static int QueuedTier(List<RecipeInfo> queue, UpgradeLine line)
	{
		if (queue == null)
		{
			return 0;
		}
		foreach (RecipeInfo recipe in queue)
		{
			if (UpgradeLines.TryGetResearch(recipe, out UpgradeLine queuedLine, out int tier) && queuedLine == line)
			{
				return tier;
			}
		}
		return 0;
	}

	// What the team's stockpiles hold of the resource that no research has reserved (0 or more).
	internal static int Free(Faction faction, Ressource resource)
	{
		return Mathf.Max(0, ResearchReservations.Free(faction, resource));
	}

	// Whether the stockpiles hold the whole cost besides what is reserved (starting reserves it all at once).
	internal static bool Affordable(Faction faction, Ingredient[] cost)
	{
		foreach (Ingredient ingredient in cost)
		{
			if (Free(faction, ingredient.Resource) < ingredient.Count)
			{
				return false;
			}
		}
		return true;
	}

	// How far the station's current research is (0 to 1): from the strokes while a worker is at it, else from the
	// work left; -1 when nothing is being researched.
	internal static float Progress(UpgradeStation station)
	{
		Recipe current = (station != null && station.craftingLabor != null) ? station.CurrentRecipe : null;
		if (current == null || current.Info == null)
		{
			return -1f;
		}
		float progress = ResearchTimePatch.Progress(station);
		if (progress >= 0f)
		{
			return progress;
		}
		int total = current.Info.work + current.Info.heat;
		if (total <= 0)
		{
			return 0f;
		}
		int left = Mathf.Max(0, current.work) + Mathf.Max(0, current.heat);
		return Mathf.Clamp01(1f - (float)left / total);
	}

	// The tier's icon from Assets/Upgrades (made by tools/make_upgrade_icons.py), or none if it is missing.
	internal static Sprite IconFor(UpgradeLine line, int tier)
	{
		string name = line.Key + "_t" + tier;
		if (Icons.TryGetValue(name, out Sprite sprite))
		{
			return sprite;
		}
		string path = Path.Combine(Path.Combine(Path.Combine(Plugin.Directory, "Assets"), "Upgrades"), name + ".png");
		Texture2D texture = new Texture2D(2, 2, TextureFormat.ARGB32, false);
		if (File.Exists(path) && texture.LoadImage(File.ReadAllBytes(path)))
		{
			texture.name = "Upgrade " + name;
			texture.filterMode = FilterMode.Bilinear;
			sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
		}
		else
		{
			Plugin.Log.LogWarning("Upgrades: icon " + path + " not found");
			Object.Destroy(texture);
			sprite = null;
		}
		Icons[name] = sprite;
		return sprite;
	}
}
