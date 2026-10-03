using Brix.Game.Rules;
using CastleStoryPlus.Core;

namespace CastleStoryPlus.Pathfinding;

// Path search nodes per tick. Working bricktrons and build projects had 40, so they stood still while searching.
[Feature(Features.Pathfinding, Features.PathfindingInfo)]
internal static class SearchBudgets
{
	private static void Enable()
	{
		GeneralRules._freeAgentSearchPathBudget = 100;
		GeneralRules._buildProjectSearchPathBudget = 100;
	}
}
