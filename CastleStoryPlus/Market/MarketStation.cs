using Brix.Game.AI;
using Brix.Game.Components;
using Brix.UI.Icons;
using CastleStoryPlus.Building;
using UnityEngine;

namespace CastleStoryPlus.Market;

// The market's crafting station: a trade is a recipe that a worker fills with the resource given, works on, and
// stores what comes out. Put on the market building in place of the workbench station it is cloned from.
internal class MarketStation : CraftingStation
{
	public override bool CanStackOrders => false;

	public override IconKey Icon => IconKeys._Machine_Shop;

	public override string PieMenuTag => "Market";

	public override ProjectPriority PriorityWeights => ProjectPriority.WorkBench;

	public override void Awake()
	{
		// Plugin components are not guaranteed to keep their serialized fields through the factory's clone.
		if (state == null)
		{
			state = GetComponent<CraftingState>();
		}
		if (craftingLabor == null)
		{
			craftingLabor = GetComponent<CraftingLabor>();
		}
		CustomBuilding.PointSiblingsAtSelf(gameObject, this);
		base.Awake();
	}

	public override void SetDefault()
	{
		AutoStore = true;
	}
}
