using CastleStoryPlus.Building;
using UnityEngine;
using static CastleStoryPlus.Building.BoxModel;

namespace CastleStoryPlus.Upgrades;

// The armoury, built from boxes: a stone footing with a plank floor, a timber frame with plank walls at the back
// and the left, a gable roof, wooden armour stands with chainmail, a gambeson and warded plate (one per tier, each
// with a helmet), the tier shields on the back wall, a weapon rack with swords and an axe, and a quiver.
// 4 x 3 blocks, centred on the origin with the open sides facing +z and +x.
internal static class ArmouryModel
{
	private const float Floor = 0.32f;

	public static GameObject Build(Transform parent, Vector3 centre, Material baseMaterial, Material ghost)
	{
		BoxModel m = new BoxModel("Armoury", 13);
		for (int x = 0; x < 4; x++)
		{
			for (int z = 0; z < 3; z++)
			{
				m.Box(0.98f, 0.24f, 0.98f, Stone, x - 1.5f, 0.12f, z - 1f);
			}
		}
		m.Box(3.86f, 0.08f, 2.86f, WoodLight, 0f, 0.28f, 0f);
		// Timber frame, plank walls and a brace.
		foreach (float x in new float[2] { -1.9f, 1.9f })
		{
			foreach (float z in new float[2] { -1.4f, 1.4f })
			{
				m.Box(0.22f, 2.6f, 0.22f, WoodDark, x, 1.6f, z);
			}
		}
		m.Box(3.8f, 2.5f, 0.12f, Wood, 0f, 1.55f, -1.42f);
		m.Box(0.12f, 2.5f, 2.8f, Wood, -1.92f, 1.55f, 0f);
		m.Box(0.14f, 2.1f, 0.06f, WoodDark, -0.9f, 1.5f, -1.33f, 0f, 0f, -43f);
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
		// Armour stands, one per tier.
		Stand(m, -0.75f, -0.75f, 1);
		Stand(m, 0.25f, -0.75f, 2);
		Stand(m, 1.25f, -0.75f, 3);
		// Tier shields on the back wall above the stands.
		for (int tier = 1; tier <= 3; tier++)
		{
			Shield(m, -0.75f + (tier - 1), 2.1f, -1.32f, tier);
		}
		// Weapon rack along the left wall with swords of the three tiers and an axe.
		m.Box(0.34f, 0.08f, 1.7f, WoodDark, -1.62f, Floor + 0.04f, 0.55f);
		m.Box(0.12f, 0.08f, 1.7f, Wood, -1.72f, Floor + 1f, 0.55f);
		m.Box(0.08f, 1.05f, 0.08f, WoodDark, -1.72f, Floor + 0.52f, -0.25f);
		m.Box(0.08f, 1.05f, 0.08f, WoodDark, -1.72f, Floor + 0.52f, 1.35f);
		Surface[] blades = { Metal, Iron, CrystalBlue };
		for (int i = 0; i < 3; i++)
		{
			float z = -0.05f + i * 0.38f;
			m.Box(0.03f, 0.8f, 0.09f, blades[i], -1.64f, Floor + 0.5f, z);
			m.Box(0.06f, 0.05f, 0.3f, Metal, -1.64f, Floor + 0.92f, z);
			m.Box(0.06f, 0.22f, 0.06f, WoodDark, -1.64f, Floor + 1.06f, z);
		}
		m.Box(0.06f, 1.1f, 0.06f, WoodDark, -1.64f, Floor + 0.6f, 1.12f);
		m.Box(0.05f, 0.3f, 0.26f, Iron, -1.64f, Floor + 1.02f, 1.22f);
		// Quiver of arrows in the front corner.
		m.AddCylinder(WoodDark, new Vector3(1.5f, Floor + 0.3f, 1f), Quaternion.identity, new Vector3(0.26f, 0.3f, 0.26f));
		m.AddCylinder(Metal, new Vector3(1.5f, Floor + 0.52f, 1f), Quaternion.identity, new Vector3(0.28f, 0.02f, 0.28f));
		for (int i = 0; i < 4; i++)
		{
			float angle = i * Mathf.PI / 2f;
			m.Box(0.03f, 0.35f, 0.03f, Wood, 1.5f + Mathf.Cos(angle) * 0.06f, Floor + 0.72f, 1f + Mathf.Sin(angle) * 0.06f);
			m.Box(0.06f, 0.1f, 0.01f, Awning, 1.5f + Mathf.Cos(angle) * 0.06f, Floor + 0.86f, 1f + Mathf.Sin(angle) * 0.06f);
		}
		return m.Build(parent, centre, baseMaterial, ghost);
	}

	// A wooden mannequin on a cross foot wearing the tier's armour and helmet: chainmail (1), a padded gambeson
	// (2) or warded plate (3).
	private static void Stand(BoxModel m, float x, float z, int tier)
	{
		m.Box(0.56f, 0.1f, 0.56f, WoodDark, x, Floor + 0.05f, z);
		m.Box(0.4f, 0.06f, 0.08f, Wood, x, Floor + 0.13f, z);
		m.Box(0.08f, 0.06f, 0.4f, Wood, x, Floor + 0.13f, z);
		m.Box(0.09f, 0.95f, 0.09f, WoodDark, x, Floor + 0.55f, z);
		m.Box(0.62f, 0.08f, 0.08f, WoodDark, x, Floor + 1.25f, z);
		switch (tier)
		{
		case 1:
			m.Box(0.5f, 0.6f, 0.3f, Metal, x, Floor + 0.98f, z);
			m.Box(0.54f, 0.07f, 0.34f, WoodDark, x, Floor + 0.78f, z);
			m.Box(0.06f, 0.06f, 0.36f, Iron, x, Floor + 0.78f, z);
			break;
		case 2:
			m.Box(0.54f, 0.64f, 0.34f, WoodLight, x, Floor + 0.98f, z);
			m.Box(0.56f, 0.08f, 0.36f, Awning, x, Floor + 0.74f, z);
			m.Box(0.2f, 0.2f, 0.36f, Awning, x + 0.24f, Floor + 1.24f, z);
			m.Box(0.2f, 0.2f, 0.36f, Awning, x - 0.24f, Floor + 1.24f, z);
			break;
		default:
			m.Box(0.52f, 0.62f, 0.34f, Iron, x, Floor + 0.98f, z);
			m.Box(0.14f, 0.2f, 0.02f, CrystalBlue, x, Floor + 1.05f, z + 0.18f);
			m.Box(0.24f, 0.14f, 0.4f, Iron, x + 0.3f, Floor + 1.26f, z, 0f, 0f, -20f);
			m.Box(0.24f, 0.14f, 0.4f, Iron, x - 0.3f, Floor + 1.26f, z, 0f, 0f, 20f);
			break;
		}
		Surface helmet = (tier == 1) ? Metal : Iron;
		m.Box(0.3f, 0.24f, 0.3f, helmet, x, Floor + 1.43f, z);
		m.Box(0.34f, 0.04f, 0.34f, helmet, x, Floor + 1.32f, z);
		if (tier >= 2)
		{
			m.Box(0.04f, 0.1f, 0.24f, (tier == 3) ? CrystalBlue : Awning, x, Floor + 1.6f, z);
		}
	}

	// A kite shield hung on the wall: wooden (1), painted with an iron rim (2) or steel with a crystal boss (3).
	private static void Shield(BoxModel m, float x, float y, float z, int tier)
	{
		Surface face = (tier == 1) ? Wood : (tier == 2) ? Awning : Iron;
		m.Box(0.46f, 0.36f, 0.05f, face, x, y + 0.08f, z);
		m.Box(0.32f, 0.16f, 0.05f, face, x, y - 0.16f, z);
		m.Box(0.16f, 0.12f, 0.05f, face, x, y - 0.29f, z);
		if (tier >= 2)
		{
			m.Box(0.5f, 0.04f, 0.06f, Metal, x, y + 0.27f, z + 0.01f);
			m.Box(0.04f, 0.5f, 0.06f, Metal, x, y, z + 0.01f);
		}
		m.Box(0.1f, 0.1f, 0.05f, (tier == 3) ? CrystalBlue : Metal, x, y + 0.06f, z + 0.04f);
	}
}
