using System.Collections.Generic;
using CastleStoryPlus.Achievements;
using CastleStoryPlus.DevTools.Api;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// The mod's achievements: whether they count in this game (and why not), and every achievement with its progress.
// Read only: the API cannot give progress (that would be a cheat).
internal static class AchievementEndpoints
{
	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/achievements", "The achievements: whether they count in this game and why not, whether a cheat was used in it, and each one's counter, progress, goal and unlock date", Get));
	}

	private static object Get(ApiRequest request)
	{
		List<object> list = new List<object>();
		foreach (AchievementInfo achievement in AchievementList.All)
		{
			list.Add(Obj("id", achievement.Id, "name", achievement.Name, "category", achievement.Category, "counter", achievement.Counter, "progress", AchievementTracker.Progress(achievement), "goal", achievement.Goal, "unlocked", AchievementTracker.IsUnlocked(achievement), "unlockedAt", AchievementTracker.UnlockedAt(achievement)));
		}
		string why = AchievementTracker.Why();
		return Obj("counting", why == null, "why", why, "cheated", AchievementTracker.IsCheated, "unlocked", AchievementTracker.UnlockedCount, "total", AchievementList.All.Count, "file", AchievementTracker.FilePath, "achievements", list);
	}
}
