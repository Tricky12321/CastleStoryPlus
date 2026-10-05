using CastleStoryPlus.Building;
using UnityEngine;
using static CastleStoryPlus.Building.BoxModel;

namespace CastleStoryPlus.Market;

// The market hall, built from boxes: a stone plinth, a timber frame, a shingled gable roof with a pennant, a
// striped awning over a counter with crates of goods at the front, and shelves at the back. 4 x 3 blocks, about
// 4 blocks high, centred on the origin with the counter facing +z. See BoxModel for the meshes and textures.
internal static class MarketModel
{
	private static BoxModel _model;

	// ghost: the blueprint's see-through material for everything; otherwise each surface is drawn with a copy of
	// baseMaterial (the shader of the game's own buildings) and its texture.
	public static GameObject Build(Transform parent, Vector3 centre, Material baseMaterial, Material ghost)
	{
		_model = new BoxModel("Market", 7);
		AddHall();
		GameObject root = _model.Build(parent, centre, baseMaterial, ghost);
		_model = null;
		return root;
	}

	private static void AddHall()
	{
		// Stone plinth.
		for (float x = -2f; x < 2f; x += 0.5f)
		{
			for (float z = -1.5f; z < 1.5f; z += 0.5f)
			{
				Box(0.5f, 0.3f, 0.5f, Stone, x + 0.25f, 0.15f, z + 0.25f);
			}
		}
		Box(0.6f, 0.15f, 0.4f, Stone, 0f, 0.075f, 1.7f);
		// Timber frame.
		const float post = 2.3f;
		foreach (float x in new float[3] { -1.85f, 0f, 1.85f })
		{
			foreach (float z in new float[2] { -1.35f, 1.35f })
			{
				Box(0.2f, post, 0.2f, WoodDark, x, 0.3f + post / 2f, z);
			}
			Box(0.2f, 0.18f, 2.9f, Wood, x, 0.3f + post, 0f);
		}
		Box(4f, 0.18f, 0.2f, Wood, 0f, 0.3f + post, -1.35f);
		Box(4f, 0.18f, 0.2f, Wood, 0f, 0.3f + post, 1.35f);
		// Gable roof of overlapping shingle rows.
		float top = 0.3f + post + 0.1f;
		const float rise = 1.3f;
		const float half = 1.75f;
		float angle = Mathf.Atan2(rise, half) * Mathf.Rad2Deg;
		foreach (float side in new float[2] { -1f, 1f })
		{
			for (int row = 0; row < 6; row++)
			{
				float t = row / 6f;
				float z = side * (half * (1f - t) + 0.12f) - side * 0.12f;
				float y = top + rise * t;
				for (int i = 0; i < 9; i++)
				{
					float x = -2f + i * 0.5f + (row % 2) * 0.12f - 0.06f;
					Box(0.5f, 0.06f, 0.4f, Roof, x, y, z, side * angle);
				}
			}
		}
		Box(4.6f, 0.16f, 0.16f, WoodDark, 0f, top + rise + 0.08f, 0f);
		// Gable ends: stacked planks narrowing to the ridge.
		foreach (float x in new float[2] { -2.08f, 2.08f })
		{
			for (int i = 0; i < 6; i++)
			{
				float t = (i + 0.5f) / 6f;
				Box(0.08f, rise / 6f, 2f * half * (1f - t), WoodLight, x, top + rise * t, 0f);
			}
		}
		// Pennant.
		Box(0.06f, 1.1f, 0.06f, WoodDark, 1.8f, top + rise + 0.6f, 0f);
		Box(0.04f, 0.3f, 0.6f, Awning, 1.8f, top + rise + 0.95f, 0.32f);
		Box(0.04f, 0.16f, 0.3f, Awning, 1.8f, top + rise + 0.95f, 0.75f);
		// Striped awning over the counter, sloping out from the front beam, with a scalloped valance.
		Box(3.9f, 0.04f, 0.72f, Awning, 0f, 2.42f, 1.68f, 25f);
		for (int i = 0; i < 10; i++)
		{
			Box(0.36f, 0.12f, 0.03f, Awning, -1.755f + i * 0.39f, 2.2f, 2.0f);
		}
		// Front counter.
		for (int i = 0; i < 10; i++)
		{
			Box(0.3f, 0.7f, 0.08f, WoodLight, -1.44f + i * 0.32f, 0.65f, 1.05f);
		}
		Box(3.4f, 0.1f, 0.55f, Wood, 0f, 1.05f, 0.9f);
		// Crates of goods on the counter.
		Crate(-1.3f, 1.1f, 0.88f);
		Log(-1.3f, 1.46f, 0.84f);
		Log(-1.3f, 1.46f, 0.98f);
		Crate(-0.6f, 1.1f, 0.88f);
		Box(0.34f, 0.2f, 0.2f, Brick, -0.62f, 1.46f, 0.86f);
		Box(0.34f, 0.2f, 0.2f, Brick, -0.56f, 1.6f, 0.92f);
		Crate(0.1f, 1.1f, 0.88f);
		Box(0.3f, 0.1f, 0.14f, Iron, 0.1f, 1.44f, 0.83f);
		Box(0.3f, 0.1f, 0.14f, Iron, 0.1f, 1.52f, 0.94f);
		Crate(0.8f, 1.1f, 0.88f);
		Crystal(0.8f, 1.6f, 0.88f, CrystalBlue, 0.14f);
		Crate(1.4f, 1.1f, 0.88f);
		Box(0.36f, 0.05f, 0.14f, WoodLight, 1.4f, 1.44f, 0.88f);
		Box(0.36f, 0.05f, 0.14f, WoodLight, 1.42f, 1.5f, 0.93f);
		// Back shelves with stock.
		Box(3.4f, 0.08f, 0.45f, Wood, 0f, 0.9f, -1.05f);
		Box(3.4f, 0.08f, 0.45f, Wood, 0f, 1.5f, -1.05f);
		Box(0.08f, 1.3f, 0.45f, WoodDark, -1.7f, 1f, -1.05f);
		Box(0.08f, 1.3f, 0.45f, WoodDark, 1.7f, 1f, -1.05f);
		for (int i = 0; i < 6; i++)
		{
			Barrel(-1.4f + i * 0.56f, 0.3f, -1.6f + 0.1f * (i % 2));
			Box(0.34f, 0.2f, 0.2f, Brick, -1.3f + i * 0.5f, 1.04f, -1.05f);
		}
		Crystal(1.2f, 1.72f, -1.05f, CrystalOrange, 0.12f);
		Log(-0.9f, 1.66f, -1.05f);
		Log(-0.4f, 1.66f, -1.05f);
		// Hanging sign with a balance, under the awning.
		Box(0.9f, 0.42f, 0.06f, WoodLight, 0f, 1.98f, 1.48f);
		Box(0.05f, 0.36f, 0.05f, Metal, 0f, 1.94f, 1.53f);
		Box(0.5f, 0.04f, 0.04f, Metal, 0f, 2.1f, 1.53f);
		Cylinder(0.2f, 0.04f, CrystalOrange, -0.24f, 1.92f, 1.53f);
		Cylinder(0.2f, 0.04f, CrystalOrange, 0.24f, 1.92f, 1.53f);
	}

	private static void Crate(float x, float y, float z)
	{
		const float s = 0.42f;
		Box(s, s * 0.7f, s, WoodLight, x, y + s * 0.35f, z);
		Box(s * 0.9f, 0.05f, s * 0.9f, WoodDark, x, y + s * 0.7f, z);
	}

	private static void Log(float x, float y, float z)
	{
		_model.AddCylinder(WoodDark, new Vector3(x, y, z), Quaternion.Euler(0f, 0f, 90f), new Vector3(0.26f, 0.38f, 0.26f));
	}

	private static void Barrel(float x, float y, float z)
	{
		_model.AddCylinder(Wood, new Vector3(x, y + 0.23f, z), Quaternion.identity, new Vector3(0.34f, 0.23f, 0.34f));
		_model.AddCylinder(Metal, new Vector3(x, y + 0.1f, z), Quaternion.identity, new Vector3(0.36f, 0.02f, 0.36f));
		_model.AddCylinder(Metal, new Vector3(x, y + 0.36f, z), Quaternion.identity, new Vector3(0.36f, 0.02f, 0.36f));
	}

	private static void Crystal(float x, float y, float z, Surface surface, float size)
	{
		_model.AddBox(surface, new Vector3(x, y, z), Quaternion.Euler(45f, 0f, 45f), new Vector3(size, size * 1.6f, size));
	}

	private static void Cylinder(float diameter, float height, Surface surface, float x, float y, float z)
	{
		_model.AddCylinder(surface, new Vector3(x, y, z), Quaternion.identity, new Vector3(diameter, height / 2f, diameter));
	}

	private static void Box(float w, float h, float d, Surface surface, float x, float y, float z, float tiltX = 0f)
	{
		_model.AddBox(surface, new Vector3(x, y, z), Quaternion.Euler(tiltX, 0f, 0f), new Vector3(w, h, d));
	}
}
