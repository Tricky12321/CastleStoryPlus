using Brix.Game.AI;
using Brix.Game.Components;
using Brix.UI.Icons;
using CastleStoryPlus.Building;

namespace CastleStoryPlus.Upgrades;

// The crafting station of the smithy, the armoury and the research station: a research is a recipe that a worker
// fills with its materials and works on; finishing it unlocks the tier for the faction (UpgradeResearchPatch). Put
// on the building in place of the workbench station it is cloned from. One subclass per building, because plugin
// components are not guaranteed to keep their serialized fields through the factory's clone.
internal abstract class UpgradeStation : CraftingStation
{
	public abstract UpgradeHall Hall { get; }

	public override bool CanStackOrders => false;

	public override ProjectPriority PriorityWeights => ProjectPriority.WorkBench;

	public override void Awake()
	{
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

internal class SmithyStation : UpgradeStation
{
	public override UpgradeHall Hall => UpgradeHall.Smithy;

	public override IconKey Icon => IconKeys._Forge;

	public override string PieMenuTag => UpgradeBuildings.SmithyName;
}

internal class ArmouryStation : UpgradeStation
{
	public override UpgradeHall Hall => UpgradeHall.Armoury;

	public override IconKey Icon => IconKeys._Shield;

	public override string PieMenuTag => UpgradeBuildings.ArmouryName;
}

internal class ResearchStation : UpgradeStation
{
	public override UpgradeHall Hall => UpgradeHall.Research;

	public override IconKey Icon => IconKeys._Lab;

	public override string PieMenuTag => UpgradeBuildings.ResearchName;
}
