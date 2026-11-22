using Brix.Game;
using Brix.Game.Components;

namespace CastleStoryPlus.Core;

// A team's home crystal (its firefly nest), or null when it has none.
internal static class HomeCrystal
{
	internal static FireflyNest Of(Faction faction)
	{
		foreach (FireflyNest nest in Live<FireflyNest>.Active())
		{
			if (nest != null && nest.isHome && nest.faction == faction && nest.mainRecepteur != null)
			{
				return nest;
			}
		}
		return null;
	}
}
