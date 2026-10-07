using CastleStoryPlus.Building;
using UnityEngine;
using static CastleStoryPlus.Building.BoxModel;

namespace CastleStoryPlus.Upgrades;

// The research station, built from boxes: an open laboratory on a stone footing with a plank floor, a timber frame
// with a plank wall on the left and a gable roof. Full bookshelves line the back, a small one hangs on the left
// wall, an alchemy table with flasks stands in the back left, a reading desk with an open book and a glowing dark
// crystal in the middle, and a stack of books with a scroll in the front right corner. 4 x 3 blocks, centred on the
// origin with the open sides facing +z and +x.
internal static class ResearchModel
{
	private const float Floor = 0.32f;

	public static GameObject Build(Transform parent, Vector3 centre, Material baseMaterial, Material ghost)
	{
		BoxModel m = new BoxModel("ResearchStation", 17);
		for (int x = 0; x < 4; x++)
		{
			for (int z = 0; z < 3; z++)
			{
				m.Box(0.98f, 0.24f, 0.98f, Stone, x - 1.5f, 0.12f, z - 1f);
			}
		}
		m.Box(3.86f, 0.08f, 2.86f, WoodLight, 0f, 0.28f, 0f);
		// Timber frame and the plank wall on the left.
		foreach (float x in new float[2] { -1.9f, 1.9f })
		{
			foreach (float z in new float[2] { -1.4f, 1.4f })
			{
				m.Box(0.22f, 2.6f, 0.22f, WoodDark, x, 1.6f, z);
			}
		}
		m.Box(0.12f, 2.5f, 2.8f, Wood, -1.92f, 1.55f, 0f);
		// Gable roof on two top plates, with a ridge beam and closed gable ends.
		m.Box(4f, 0.18f, 0.22f, WoodDark, 0f, 2.9f, -1.4f);
		m.Box(4f, 0.18f, 0.22f, WoodDark, 0f, 2.9f, 1.4f);
		m.Slope(-1.6f, 3f, 0.04f, 3.87f, 4.5f, 0.12f, Roof);
		m.Slope(-0.04f, 3.87f, 1.6f, 3f, 4.5f, 0.12f, Roof);
		m.Box(4.6f, 0.2f, 0.2f, WoodDark, 0f, 3.97f, 0f);
		foreach (float x in new float[2] { -1.92f, 1.92f })
		{
			for (int i = 0; i < 5; i++)
			{
				float t = (i + 0.5f) / 5f;
				m.Box(0.1f, 0.79f / 5f, 2.8f * (1f - t), WoodLight, x, 2.99f + 0.79f * t, 0f);
			}
		}
		BackShelves(m);
		WallShelf(m);
		AlchemyTable(m, -1.45f, 0f);
		ReadingDesk(m, 0.5f, 0f);
		BookStack(m, 1.45f, 1f);
		return m.Build(parent, centre, baseMaterial, ghost);
	}

	// Two shelf units along the back wall, each two blocks of books high (two rows per block), in a dark frame.
	private static void BackShelves(BoxModel m)
	{
		const float z = -1.25f;
		m.Box(3.7f, 2.1f, 0.06f, WoodDark, 0f, Floor + 1.05f, -1.4f);
		for (int row = 0; row < 2; row++)
		{
			m.Box(3.56f, 1f, 0.3f, Books, 0f, Floor + 0.06f + 0.5f + row, z);
		}
		for (int i = 0; i < 5; i++)
		{
			m.Box(3.7f, 0.06f, 0.4f, WoodDark, 0f, Floor + 0.03f + i * 0.5f, z);
		}
		foreach (float x in new float[3] { -1.81f, 0f, 1.81f })
		{
			m.Box(0.08f, 2.1f, 0.4f, WoodDark, x, Floor + 1.05f, z);
		}
		m.Box(3.84f, 0.1f, 0.46f, WoodDark, 0f, Floor + 2.12f, z);
	}

	// A short shelf on the left wall above the alchemy table, one row of books.
	private static void WallShelf(BoxModel m)
	{
		const float x = -1.7f;
		m.Box(0.3f, 0.5f, 1.2f, Books, x, 1.95f, 0.6f);
		m.Box(0.36f, 0.05f, 1.3f, WoodDark, x, 1.69f, 0.6f);
		m.Box(0.36f, 0.05f, 1.3f, WoodDark, x, 2.22f, 0.6f);
	}

	// A table with flasks of blue and orange crystal, a dark crystal shard and an open book.
	private static void AlchemyTable(BoxModel m, float x, float z)
	{
		const float top = Floor + 0.78f;
		m.Box(0.9f, 0.08f, 0.8f, WoodDark, x, top, z);
		foreach (float dx in new float[2] { -0.38f, 0.38f })
		{
			foreach (float dz in new float[2] { -0.33f, 0.33f })
			{
				m.Box(0.08f, 0.74f, 0.08f, WoodDark, x + dx, Floor + 0.37f, z + dz);
			}
		}
		Flask(m, x - 0.25f, top + 0.04f, z - 0.2f, CrystalBlue, 0.16f, 0.22f);
		Flask(m, x - 0.02f, top + 0.04f, z - 0.25f, CrystalOrange, 0.12f, 0.16f);
		Flask(m, x + 0.22f, top + 0.04f, z - 0.15f, CrystalBlue, 0.1f, 0.28f);
		m.Box(0.08f, 0.22f, 0.08f, CrystalDark, x + 0.25f, top + 0.15f, z + 0.2f, 15f, 45f, 10f);
		OpenBook(m, x - 0.15f, top + 0.05f, z + 0.15f, LeatherGreen, 0f);
	}

	// A glass flask: a round body and a narrow neck with a cork.
	private static void Flask(BoxModel m, float x, float y, float z, Surface glass, float width, float height)
	{
		m.AddCylinder(glass, new Vector3(x, y + height * 0.3f, z), Quaternion.identity, new Vector3(width, height * 0.3f, width));
		m.AddCylinder(glass, new Vector3(x, y + height * 0.75f, z), Quaternion.identity, new Vector3(width * 0.35f, height * 0.2f, width * 0.35f));
		m.AddCylinder(Wood, new Vector3(x, y + height * 0.98f, z), Quaternion.identity, new Vector3(width * 0.38f, height * 0.05f, width * 0.38f));
	}

	// A slanted reading desk facing the scholar's place on its left, with an open book, and a stone pedestal behind it
	// holding a glowing dark crystal.
	private static void ReadingDesk(BoxModel m, float x, float z)
	{
		m.Box(0.6f, 0.08f, 0.5f, WoodDark, x, Floor + 0.04f, z);
		m.Box(0.16f, 0.9f, 0.16f, WoodDark, x, Floor + 0.5f, z);
		// The top tilts down towards -x, where the worker stands.
		m.Box(0.62f, 0.06f, 0.56f, Wood, x, Floor + 1f, z, 0f, 0f, 20f);
		OpenBook(m, x - 0.01f, Floor + 1.05f, z, LeatherRed, 20f);
		m.Box(0.3f, 0.7f, 0.3f, Stone, x + 0.2f, Floor + 0.35f, z - 0.35f);
		m.Box(0.36f, 0.06f, 0.36f, Stone, x + 0.2f, Floor + 0.73f, z - 0.35f);
		m.Box(0.14f, 0.36f, 0.14f, CrystalDark, x + 0.2f, Floor + 0.94f, z - 0.35f, 0f, 45f, 0f);
		m.Box(0.1f, 0.22f, 0.1f, CrystalDark, x + 0.3f, Floor + 0.86f, z - 0.27f, 0f, 30f, -25f);
		m.Box(0.1f, 0.2f, 0.1f, CrystalDark, x + 0.1f, Floor + 0.85f, z - 0.42f, 20f, 60f, 15f);
	}

	// An open book: its cover and two pages, turned about z by the given angle.
	private static void OpenBook(BoxModel m, float x, float y, float z, Surface cover, float tiltZ)
	{
		m.Box(0.4f, 0.025f, 0.5f, cover, x, y, z, 0f, 0f, tiltZ);
		m.Box(0.18f, 0.03f, 0.44f, Paper, x - 0.09f, y + 0.02f, z, 0f, 0f, tiltZ + 4f);
		m.Box(0.18f, 0.03f, 0.44f, Paper, x + 0.09f, y + 0.02f, z, 0f, 0f, tiltZ - 4f);
	}

	// Closed books piled on the floor, each a little turned, with a rolled scroll on top.
	private static void BookStack(BoxModel m, float x, float z)
	{
		Surface[] covers = { LeatherBlue, LeatherRed, LeatherGreen, LeatherBlue, LeatherRed };
		float y = Floor;
		for (int i = 0; i < covers.Length; i++)
		{
			float height = 0.1f + (i % 2) * 0.03f;
			m.Box(0.5f - i * 0.03f, height, 0.38f - i * 0.02f, covers[i], x, y + height / 2f, z, 0f, (i * 23) % 40 - 20f, 0f);
			y += height;
		}
		m.AddCylinder(Paper, new Vector3(x, y + 0.06f, z), Quaternion.Euler(0f, 30f, 90f), new Vector3(0.12f, 0.22f, 0.12f));
		// A second, lower pile beside it.
		m.Box(0.42f, 0.12f, 0.32f, LeatherGreen, x - 0.15f, Floor + 0.06f, z - 0.4f, 0f, 12f, 0f);
		m.Box(0.38f, 0.1f, 0.3f, LeatherRed, x - 0.15f, Floor + 0.17f, z - 0.4f, 0f, -8f, 0f);
	}
}
