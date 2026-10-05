using UnityEngine;
using static CastleStoryPlus.Building.BoxModel;

namespace CastleStoryPlus.Building;

// The warehouse, built from boxes: a stone plinth carrying a hall of 7 x 6 blocks (brick base, plank walls on a
// timber frame, a gable roof with a vent and a hoist at the gable end, wide cart doors standing open in the front)
// and on the right an open bay of 2 x 6 blocks under a lean-to roof: the brick works, with a stone heap, a brick
// press, a water barrel and a pallet of bricks. 9 x 6 blocks, centred on the origin with the doors facing +z.
// The walls stand inside the outer blocks of the hall, so the 5 x 4 blocks within are free floor (see the layout
// in Warehouse). Designed in the "Forge & Armoury Models" artifact.
internal static class WarehouseModel
{
	private const float Floor = 0.3f;

	private const float Top = 3.3f;

	private const float BrickHeight = 1.1f;

	private const float Wall = 0.24f;

	// The hall spans x -4.5 .. 2.5; its centre.
	private const float HallX = -1f;

	private const float HallHalfWidth = 3.5f;

	private const float HallHalfDepth = 3f;

	public static GameObject Build(Transform parent, Vector3 centre, Material baseMaterial, Material ghost)
	{
		BoxModel m = new BoxModel("Warehouse", 17);
		for (int x = 0; x < 9; x++)
		{
			for (int z = 0; z < 6; z++)
			{
				m.Box(0.98f, Floor, 0.98f, Stone, x - 4f, Floor / 2f, z - 2.5f);
			}
		}
		Hall(m);
		Roof(m);
		Doors(m);
		Stock(m);
		BrickWorks(m);
		return m.Build(parent, centre, baseMaterial, ghost);
	}

	// A wall of brick below and planks above, len long, along x or along z, centred on (x, z).
	private static void WallPiece(BoxModel m, float len, float x, float z, bool alongX)
	{
		float plank = Top - Floor - BrickHeight;
		m.Box(alongX ? len : Wall, BrickHeight, alongX ? Wall : len, Brick, x, Floor + BrickHeight / 2f, z);
		m.Box(alongX ? len : Wall * 0.8f, plank, alongX ? Wall * 0.8f : len, Wood, x, Floor + BrickHeight + plank / 2f, z);
	}

	private static void Hall(BoxModel m)
	{
		float hw = HallHalfWidth, hd = HallHalfDepth;
		WallPiece(m, 2f * hw, HallX, -hd + Wall / 2f, true);
		WallPiece(m, 2f * hd, HallX - hw + Wall / 2f, 0f, false);
		WallPiece(m, 2f * hd, HallX + hw - Wall / 2f, 0f, false);
		// Front: cart doors over three blocks in the middle (x -2.5 .. 0.5), planks above them.
		WallPiece(m, 2f, -3.5f, hd - Wall / 2f, true);
		WallPiece(m, 2f, 1.5f, hd - Wall / 2f, true);
		float lintel = Floor + 2.4f;
		m.Box(3f, Top - lintel, Wall * 0.8f, Wood, -1f, lintel + (Top - lintel) / 2f, hd - Wall / 2f);
		m.Box(3.4f, 0.24f, 0.3f, WoodDark, -1f, lintel + 0.05f, hd - 0.08f);
		// Timber frame: posts at the corners, beside the doors and halfway along the sides, top plates, braces.
		float post = Top - Floor;
		foreach (float x in new float[4] { -4.37f, -2.5f, 0.5f, 2.37f })
		{
			m.Box(0.3f, post, 0.3f, WoodDark, x, Floor + post / 2f, -hd + 0.12f);
			m.Box(0.3f, post, 0.3f, WoodDark, x, Floor + post / 2f, hd - 0.12f);
		}
		m.Box(0.3f, post, 0.3f, WoodDark, -4.37f, Floor + post / 2f, 0f);
		m.Box(0.3f, post, 0.3f, WoodDark, 2.37f, Floor + post / 2f, 0f);
		m.Box(2f * hw + 0.2f, 0.2f, 0.32f, Wood, HallX, Top + 0.1f, -hd + 0.12f);
		m.Box(2f * hw + 0.2f, 0.2f, 0.32f, Wood, HallX, Top + 0.1f, hd - 0.12f);
		m.Box(0.32f, 0.2f, 2f * hd, Wood, -4.37f, Top + 0.1f, 0f);
		m.Box(0.32f, 0.2f, 2f * hd, Wood, 2.37f, Top + 0.1f, 0f);
		float braceY = Floor + BrickHeight + (Top - Floor - BrickHeight) / 2f;
		m.Box(0.14f, 2.15f, 0.06f, WoodDark, -3.45f, braceY, -hd - 0.02f, 0f, 0f, 35f);
		m.Box(0.14f, 2.15f, 0.06f, WoodDark, 1.45f, braceY, -hd - 0.02f, 0f, 0f, -35f);
		// Sign over the doors (a crate and a brick on it) and a lantern beside them.
		m.Box(1.7f, 0.5f, 0.08f, WoodLight, -1f, Floor + 2.85f, hd + 0.1f);
		m.Box(0.3f, 0.3f, 0.06f, Wood, -1.4f, Floor + 2.85f, hd + 0.16f);
		m.Box(0.4f, 0.17f, 0.06f, Brick, -0.62f, Floor + 2.85f, hd + 0.16f);
		m.Box(0.06f, 0.06f, 0.3f, Metal, -2.85f, Floor + 2.15f, hd + 0.15f);
		m.Box(0.18f, 0.24f, 0.18f, CrystalOrange, -2.85f, Floor + 1.98f, hd + 0.3f);
	}

	private static void Roof(BoxModel m)
	{
		float hw = HallHalfWidth, hd = HallHalfDepth;
		float ridge = Top + 1.8f;
		float width = 2f * hw + 0.7f;
		m.Slope(-hd - 0.4f, Top - 0.02f, 0.04f, ridge, width, 0.12f, BoxModel.Roof, HallX);
		m.Slope(-0.04f, ridge, hd + 0.4f, Top - 0.02f, width, 0.12f, BoxModel.Roof, HallX);
		m.Box(width + 0.1f, 0.2f, 0.1f, WoodDark, HallX, Top - 0.08f, -hd - 0.43f);
		m.Box(width + 0.1f, 0.2f, 0.1f, WoodDark, HallX, Top - 0.08f, hd + 0.43f);
		m.Box(width + 0.1f, 0.22f, 0.22f, WoodDark, HallX, ridge + 0.08f, 0f);
		// Gable ends: stacked planks narrowing to the ridge.
		foreach (float x in new float[2] { HallX - hw + 0.06f, HallX + hw - 0.06f })
		{
			for (int i = 0; i < 6; i++)
			{
				float t = (i + 0.5f) / 6f;
				m.Box(0.12f, 1.75f / 6f, 2f * hd * (1f - t), WoodLight, x, Top + 0.1f + 1.75f * t, 0f);
			}
		}
		// Loft door and hoist on the left gable, a crate on the rope.
		float gable = HallX - hw;
		m.Box(0.08f, 0.85f, 0.75f, Wood, gable - 0.05f, Top + 0.68f, 0f);
		m.Box(1.4f, 0.18f, 0.18f, WoodDark, gable - 0.6f, ridge - 0.45f, 0f);
		m.Cylinder(0.24f, 0.08f, Metal, gable - 1.15f, ridge - 0.6f, 0f);
		m.Box(0.03f, 1.45f, 0.03f, WoodLight, gable - 1.15f, ridge - 1.35f, 0f);
		m.Box(0.5f, 0.44f, 0.5f, Wood, gable - 1.15f, ridge - 2.3f, 0f, 0f, 17f, 0f);
		// Vent on the ridge with a small pointed cap.
		m.Box(0.7f, 0.5f, 0.7f, Wood, 0.6f, ridge + 0.25f, 0f);
		for (int i = 0; i < 3; i++)
		{
			m.Box(0.72f, 0.05f, 0.05f, WoodDark, 0.6f, ridge + 0.12f + i * 0.13f, 0.36f);
		}
		m.Box(0.9f, 0.12f, 0.9f, BoxModel.Roof, 0.6f, ridge + 0.56f, 0f);
		m.Box(0.55f, 0.12f, 0.55f, BoxModel.Roof, 0.6f, ridge + 0.68f, 0f);
	}

	// Cart doors: the left one wide open, the right one ajar, both swung outwards.
	private static void Doors(BoxModel m)
	{
		float hd = HallHalfDepth;
		Leaf(m, -2.5f, 1f, -100f);
		Leaf(m, 0.5f, -1f, 66f);
		void Leaf(BoxModel model, float hingeX, float dir, float angle)
		{
			// Turned about the hinge: the leaf's middle is 0.74 from it along the turned direction.
			Quaternion turn = Quaternion.Euler(0f, angle, 0f);
			Vector3 hinge = new Vector3(hingeX, Floor, hd + 0.04f);
			Vector3 along = turn * new Vector3(dir, 0f, 0f);
			Vector3 middle = hinge + along * 0.74f;
			float turnY = angle;
			model.Box(1.48f, 2.3f, 0.1f, Wood, middle.x, Floor + 1.15f, middle.z, 0f, turnY, 0f);
			foreach (float y in new float[2] { 0.35f, 1.95f })
			{
				model.Box(1.4f, 0.16f, 0.06f, WoodDark, middle.x, Floor + y, middle.z, 0f, turnY, 0f);
			}
		}
	}

	// Goods on pallets along the back wall and in the left corner, clear of the floor workers walk on.
	private static void Stock(BoxModel m)
	{
		float z = -2.42f;
		Pallet(m, -3.75f, z);
		for (int i = 0; i < 3; i++)
		{
			m.AddCylinder(WoodDark, new Vector3(-4.02f + i * 0.27f, Floor + 0.25f, z), Quaternion.Euler(90f, 0f, 0f), new Vector3(0.26f, 0.38f, 0.26f));
		}
		Pallet(m, -2.75f, z);
		for (int l = 0; l < 5; l++)
		{
			m.Box(0.8f, 0.07f, 0.7f, WoodLight, -2.75f, Floor + 0.16f + l * 0.075f, z);
		}
		Pallet(m, -1.75f, z);
		m.Box(0.36f, 0.34f, 0.36f, Wood, -1.95f, Floor + 0.3f, z);
		m.Box(0.36f, 0.34f, 0.36f, Wood, -1.55f, Floor + 0.3f, z, 0f, 9f, 0f);
		m.Box(0.36f, 0.34f, 0.36f, Wood, -1.75f, Floor + 0.64f, z, 0f, -6f, 0f);
		Pallet(m, -0.75f, z);
		for (int l = 0; l < 3; l++)
		{
			for (int j = 0; j < 3; j++)
			{
				m.Box(0.36f, 0.1f, 0.16f, Iron, -0.75f, Floor + 0.18f + l * 0.105f, z - 0.2f + j * 0.2f);
			}
		}
		Pallet(m, 0.25f, z);
		for (int l = 0; l < 4; l++)
		{
			for (int j = 0; j < 2; j++)
			{
				m.Box(0.38f, 0.16f, 0.24f, Brick, 0.25f + ((l % 2 == 0) ? -0.2f : 0.2f) * (j * 2 - 1), Floor + 0.2f + l * 0.165f, z + (j * 2 - 1) * 0.13f);
			}
		}
		Pallet(m, 1.25f, z);
		for (int i = 0; i < 4; i++)
		{
			m.Box(0.26f, 0.2f, 0.26f, Stone, 1.05f + (i % 2) * 0.4f, Floor + 0.22f, z - 0.15f + (i / 2) * 0.3f, 7f * i, 23f * i, 0f);
		}
		// Barrels and a crate stack in the left wall's corner by the doors.
		m.Cylinder(0.5f, 0.7f, Wood, -3.9f, Floor + 0.35f, 2.2f);
		m.Cylinder(0.52f, 0.04f, Metal, -3.9f, Floor + 0.15f, 2.2f);
		m.Cylinder(0.52f, 0.04f, Metal, -3.9f, Floor + 0.55f, 2.2f);
		m.Box(0.45f, 0.4f, 0.45f, Wood, -3.92f, Floor + 0.2f, 1.45f, 0f, 8f, 0f);
	}

	private static void Pallet(BoxModel m, float x, float z)
	{
		foreach (float dz in new float[3] { -0.3f, 0f, 0.3f })
		{
			m.Box(0.86f, 0.06f, 0.12f, WoodDark, x, Floor + 0.03f, z + dz);
		}
		m.Box(0.86f, 0.04f, 0.78f, Wood, x, Floor + 0.08f, z);
	}

	private static void BrickWorks(BoxModel m)
	{
		// Posts on the outer side, a beam on them, and a lean-to roof falling from the hall wall.
		foreach (float z in new float[3] { -2.85f, 0f, 2.85f })
		{
			m.Box(0.26f, 2.3f, 0.26f, WoodDark, 4.33f, Floor + 1.15f, z);
		}
		m.Box(0.28f, 0.2f, 6.1f, Wood, 4.33f, Floor + 2.35f, 0f);
		m.SlopeAlongX(4.75f, Floor + 2.47f, 2.45f, Top - 0.1f, 6.5f, 0.12f, BoxModel.Roof);
		m.Box(0.1f, 0.2f, 6.6f, WoodDark, 4.77f, Floor + 2.4f, 0f);
		// Stone heap at the back.
		for (int i = 0; i < 9; i++)
		{
			float s = 0.26f + (i % 3) * 0.06f;
			m.Box(s, s * 0.8f, s, Stone, 3.0f + (i % 3) * 0.33f, Floor + s * 0.4f + ((i > 5) ? 0.24f : 0f), -2.5f + (i / 3) * 0.3f, 11f * i, 37f * i, 5f * i);
		}
		// Brick press: a mould table, an iron frame, the ram on a screw and a cross handle on top.
		m.Box(0.95f, 0.5f, 0.7f, Wood, 3.45f, Floor + 0.25f, -0.5f);
		m.Box(0.1f, 1.55f, 0.12f, Iron, 3.0f, Floor + 1.27f, -0.5f);
		m.Box(0.1f, 1.55f, 0.12f, Iron, 3.9f, Floor + 1.27f, -0.5f);
		m.Box(1.02f, 0.14f, 0.16f, Iron, 3.45f, Floor + 2.0f, -0.5f);
		m.Cylinder(0.1f, 0.7f, Metal, 3.45f, Floor + 1.6f, -0.5f);
		m.Box(0.5f, 0.26f, 0.42f, Iron, 3.45f, Floor + 1.15f, -0.5f);
		m.Box(1.3f, 0.07f, 0.07f, WoodDark, 3.45f, Floor + 2.12f, -0.5f, 0f, 34f, 0f);
		m.Box(0.38f, 0.16f, 0.24f, Brick, 3.45f, Floor + 0.58f, -0.2f);
		// Water barrel and the finished bricks.
		m.Cylinder(0.48f, 0.62f, Wood, 4.05f, Floor + 0.31f, 0.5f);
		m.Cylinder(0.5f, 0.04f, Metal, 4.05f, Floor + 0.12f, 0.5f);
		m.Cylinder(0.5f, 0.04f, Metal, 4.05f, Floor + 0.5f, 0.5f);
		m.Cylinder(0.42f, 0.02f, CrystalBlue, 4.05f, Floor + 0.6f, 0.5f);
		Pallet(m, 3.45f, 1.5f);
		for (int l = 0; l < 4; l++)
		{
			for (int j = 0; j < 3; j++)
			{
				m.Box(0.38f, 0.16f, 0.24f, Brick, 3.45f + ((l % 2 == 0) ? -0.2f : 0.2f), Floor + 0.2f + l * 0.165f, 1.5f - 0.28f + j * 0.28f);
			}
		}
	}
}
