using CastleStoryPlus.Building;
using UnityEngine;
using static CastleStoryPlus.Building.BoxModel;

namespace CastleStoryPlus.Upgrades;

// The smithy, built from boxes: a stone floor and back wall with a hearth, its fire behind an iron grill and a
// chimney, a lean-to roof on two posts, an anvil with a hammer, a quench barrel and finished swords of the three
// tiers leaning on the wall. 4 x 3 blocks, centred on the origin with the open side facing +z.
internal static class SmithyModel
{
	public static GameObject Build(Transform parent, Vector3 centre, Material baseMaterial, Material ghost)
	{
		BoxModel m = new BoxModel("Smithy", 11);
		// Floor and back wall of stone blocks.
		for (int x = 0; x < 4; x++)
		{
			for (int z = 0; z < 3; z++)
			{
				m.Box(0.98f, 0.3f, 0.98f, Stone, x - 1.5f, 0.15f, z - 1f);
			}
			for (int y = 0; y < 2; y++)
			{
				m.Box(0.98f, 0.98f, 0.98f, Stone, x - 1.5f, 0.8f + y, -1f);
			}
		}
		// Hearth with its fire behind an iron grill, and the chimney above it.
		m.Box(1.2f, 0.6f, 0.9f, Brick, -1.4f, 0.6f, -0.05f);
		m.Box(0.8f, 0.25f, 0.6f, CrystalOrange, -1.4f, 0.95f, -0.05f);
		for (int i = 0; i < 5; i++)
		{
			m.Box(0.06f, 0.32f, 0.06f, Metal, -1.8f + i * 0.2f, 1.05f, 0.42f);
		}
		m.Box(0.95f, 0.06f, 0.08f, Metal, -1.4f, 1.22f, 0.42f);
		m.Box(0.98f, 0.98f, 0.98f, Stone, -1.5f, 2.8f, -1f);
		m.Box(0.98f, 0.98f, 0.98f, Stone, -1.5f, 3.78f, -1f);
		m.Box(1.12f, 0.16f, 1.12f, Brick, -1.5f, 4.35f, -1f);
		// Lean-to roof from the top of the back wall up to a beam on two posts at the front.
		m.Box(0.2f, 2.6f, 0.2f, WoodDark, 1.9f, 1.6f, 1.4f);
		m.Box(0.2f, 2.6f, 0.2f, WoodDark, -1.9f, 1.6f, 1.4f);
		m.Box(4.2f, 0.2f, 0.2f, Wood, 0f, 2.95f, 1.4f);
		m.Slope(-1.55f, 2.38f, 1.8f, 3.21f, 4.4f, 0.12f, Roof);
		m.Box(4.5f, 0.2f, 0.1f, WoodDark, 0f, 3.12f, 1.83f);
		Anvil(m, 0.4f, 0.3f);
		// Quench barrel with water.
		m.AddCylinder(Wood, new Vector3(1.5f, 0.65f, 0.6f), Quaternion.identity, new Vector3(0.7f, 0.35f, 0.7f));
		m.AddCylinder(Metal, new Vector3(1.5f, 0.5f, 0.6f), Quaternion.identity, new Vector3(0.72f, 0.03f, 0.72f));
		m.AddCylinder(Metal, new Vector3(1.5f, 0.85f, 0.6f), Quaternion.identity, new Vector3(0.72f, 0.03f, 0.72f));
		m.AddCylinder(CrystalBlue, new Vector3(1.5f, 0.98f, 0.6f), Quaternion.identity, new Vector3(0.6f, 0.02f, 0.6f));
		// Finished swords of the three tiers leaning on the wall.
		Surface[] blades = { Metal, Iron, CrystalBlue };
		for (int i = 0; i < 3; i++)
		{
			float x = 0.6f + i * 0.3f;
			m.Box(0.1f, 0.9f, 0.03f, blades[i], x, 0.85f, -0.42f, -11f);
			m.Box(0.3f, 0.05f, 0.06f, Metal, x, 1.32f, -0.5f, -11f);
			m.Box(0.06f, 0.24f, 0.06f, WoodDark, x, 1.47f, -0.53f, -11f);
		}
		return m.Build(parent, centre, baseMaterial, ghost);
	}

	// Anvil on a stone block, with a hammer resting on it.
	private static void Anvil(BoxModel m, float x, float z)
	{
		const float floor = 0.3f;
		m.Box(0.8f, 0.12f, 0.6f, Brick, x, floor + 0.06f, z);
		m.Box(0.6f, 0.35f, 0.5f, Iron, x, floor + 0.3f, z);
		m.Box(0.35f, 0.3f, 0.3f, Iron, x, floor + 0.62f, z);
		m.Box(0.9f, 0.22f, 0.4f, Iron, x, floor + 0.87f, z);
		m.Box(0.3f, 0.16f, 0.25f, Iron, x + 0.55f, floor + 0.9f, z, 0f, 0f, -11f);
		m.Box(0.6f, 0.08f, 0.08f, WoodDark, x - 0.1f, floor + 1.03f, z + 0.05f, 0f, 0f, -16f);
		m.Box(0.14f, 0.14f, 0.3f, Metal, x - 0.38f, floor + 1.1f, z + 0.05f, 0f, 0f, -16f);
	}
}
