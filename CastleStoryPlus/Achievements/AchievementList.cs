using System;
using System.Collections.Generic;
using Brix.UI.Icons;

namespace CastleStoryPlus.Achievements;

// The counters the achievements read (a hook in the game's code adds to one of them, AchievementTracker.Add/Local...).
// Most are running totals over every game; a few (wave, research) hold the best value reached.
internal static class AchievementCounter
{
	public const string Trades = "trades";

	public const string Research = "research";

	public const string Wave = "wave";

	public const string Guardians = "guardians";

	public const string Chests = "chests";

	public const string Kills = "kills";

	public const string CallToArms = "callToArms";

	public const string Crafted = "crafted";

	public const string Rains = "rains";

	public const string Blocks = "blocks";

	public const string Dug = "dug";

	public const string Gathered = "gathered";
}

// One achievement: unlocked when its counter reaches its target. Added to AchievementList and, if no hook counts the
// counter yet, a hook for it (AchievementHooks).
internal sealed class AchievementInfo
{
	public string Id;

	public string Name;

	public string Text;

	public string Category;

	public IconKey Icon;

	public string Counter;

	// The target; or, when it depends on the game's data (every research), a function that gives it.
	public int Target;

	public Func<int> TargetOf;

	public int Goal => (TargetOf != null) ? Math.Max(1, TargetOf()) : Target;
}

// The table of the mod's achievements. A new feature's achievement is one line here and one hook that counts it.
internal static class AchievementList
{
	public static readonly List<AchievementInfo> All = new List<AchievementInfo>
	{
		New("trade10", "Pedlar", "Trade 10 times at the market.", "Market", IconKeys._Machine_Shop, AchievementCounter.Trades, 10),
		New("trade100", "Merchant", "Trade 100 times at the market.", "Market", IconKeys._Machine_Shop, AchievementCounter.Trades, 100),
		New("trade1000", "Trade baron", "Trade 1000 times at the market.", "Market", IconKeys._Machine_Shop, AchievementCounter.Trades, 1000),
		New("research1", "Curious mind", "Finish a research.", "Research", IconKeys._Lab, AchievementCounter.Research, 1),
		new AchievementInfo
		{
			Id = "researchAll",
			Name = "Colony complete",
			Text = "Research every upgrade of the colony (smithy, armoury and research station).",
			Category = "Research",
			Icon = IconKeys._Forge,
			Counter = AchievementCounter.Research,
			TargetOf = Upgrades.UpgradeLines.TotalTiers
		},
		New("wave10", "Holding the line", "Survive 10 invasion waves in one game.", "Invasion", IconKeys._Shield, AchievementCounter.Wave, 10),
		New("wave20", "Unbroken", "Survive 20 invasion waves in one game.", "Invasion", IconKeys._Shield, AchievementCounter.Wave, 20),
		New("wave40", "Last bastion", "Survive 40 invasion waves in one game.", "Invasion", IconKeys.UI_Crown, AchievementCounter.Wave, 40),
		New("kills100", "Corruption cleaner", "Defeat 100 enemies.", "Combat", IconKeys._Corruptron, AchievementCounter.Kills, 100),
		New("kills1000", "Scourge of the corrupted", "Defeat 1000 enemies.", "Combat", IconKeys._Biftron, AchievementCounter.Kills, 1000),
		New("callToArms10", "To arms!", "Sound the call to arms 10 times.", "Combat", IconKeys._UI_CallToArms, AchievementCounter.CallToArms, 10),
		New("guardian1", "Beast slayer", "Defeat a guardian of a treasure chest.", "Treasure", IconKeys.Skull, AchievementCounter.Guardians, 1),
		New("guardian10", "Guardian hunter", "Defeat 10 guardians.", "Treasure", IconKeys.Skull, AchievementCounter.Guardians, 10),
		New("chest1", "Treasure found", "Open a treasure chest.", "Treasure", IconKeys.UI_Star, AchievementCounter.Chests, 1),
		New("chest10", "Treasure hunter", "Open 10 treasure chests.", "Treasure", IconKeys.UI_Star, AchievementCounter.Chests, 10),
		New("craft100", "Craftsman", "Finish 100 orders in the workshops.", "Workshops", IconKeys.Anvil, AchievementCounter.Crafted, 100),
		New("craft1000", "Master craftsman", "Finish 1000 orders in the workshops.", "Workshops", IconKeys.Anvil, AchievementCounter.Crafted, 1000),
		New("rain5", "Singing in the rain", "Live through 5 rains.", "Nature", IconKeys.UI_Moon, AchievementCounter.Rains, 5),
		New("build1000", "Builder", "Build 1000 blocks.", "Building", IconKeys._UI_Hammer, AchievementCounter.Blocks, 1000),
		New("build10000", "Architect", "Build 10000 blocks.", "Building", IconKeys._UI_Hammer, AchievementCounter.Blocks, 10000),
		New("dig1000", "Miner", "Dig 1000 blocks of ground.", "Mining", IconKeys.Pick, AchievementCounter.Dug, 1000),
		New("gather250", "Gatherer", "Fell 250 trees and harvest plants.", "Nature", IconKeys.UI_Tree, AchievementCounter.Gathered, 250)
	};

	private static readonly Dictionary<string, List<AchievementInfo>> ByCounterMap = new Dictionary<string, List<AchievementInfo>>();

	private static AchievementInfo New(string id, string name, string text, string category, IconKey icon, string counter, int target)
	{
		return new AchievementInfo { Id = id, Name = name, Text = text, Category = category, Icon = icon, Counter = counter, Target = target };
	}

	// Whether the counter holds the best value reached (waves, research) rather than a running total.
	public static bool IsBest(string counter)
	{
		return counter == AchievementCounter.Research || counter == AchievementCounter.Wave;
	}

	public static AchievementInfo Find(string id)
	{
		return All.Find((AchievementInfo a) => a.Id == id);
	}

	// The achievements that read the counter.
	public static List<AchievementInfo> ForCounter(string counter)
	{
		if (!ByCounterMap.TryGetValue(counter, out List<AchievementInfo> list))
		{
			list = All.FindAll((AchievementInfo a) => a.Counter == counter);
			ByCounterMap[counter] = list;
		}
		return list;
	}
}
