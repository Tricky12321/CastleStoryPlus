using Brix.External.Factories;
using CastleStoryPlus.Building;
using UnityEngine;

namespace CastleStoryPlus.Nature;

// The old "Sapling area" marker post, replaced by the tree farm task (TreeFarm). It is no longer in the build menu
// and does nothing; it stays a building of the game only so saves that have one still load (it can be taken down),
// also with the Saplings feature switched off (SavedContent).
internal static class SaplingArea
{
	internal const string Name = "SaplingArea";

	internal static readonly CustomBuilding Building = new CustomBuilding(Name, 1, 1, 1)
	{
		BlueprintSource = "KnightStatue",
		BuildingSource = "KnightStatue",
		KeepChildren = new string[1] { "OverheadTarget" },
		BuildModel = BuildModel,
		BuildCost = (Description cost) => { },
		SetupBuilding = (GameObject go) => { }
	};

	internal static void AddToFactories()
	{
		Building.AddToFactories();
	}

	// A post with a small sign board and a tuft of leaves on top.
	private static GameObject BuildModel(Transform parent, Vector3 centre, Material material, Material ghost)
	{
		BoxModel m = new BoxModel("SaplingArea", 11);
		m.Box(0.14f, 1.3f, 0.14f, BoxModel.WoodDark, 0f, 0.65f, 0f);
		m.Box(0.6f, 0.36f, 0.06f, BoxModel.WoodLight, 0f, 1.05f, 0.1f);
		m.Box(0.36f, 0.24f, 0.36f, BoxModel.LeatherGreen, 0f, 1.42f, 0f);
		return m.Build(parent, centre, material, ghost);
	}
}
