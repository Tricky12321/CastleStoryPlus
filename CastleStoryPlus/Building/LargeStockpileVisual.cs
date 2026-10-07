using System.Collections.Generic;
using Brix.External.Factories;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Game.Storage;
using CastleStoryPlus.Core;
using CastleStoryPlus.Upgrades;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Building;

// Draws the large stockpile's content with the pallet's own models in their own size, instead of the 2 x 2 display
// stretched 1.5 x. The meshes and materials come from the (hidden) display children the large stockpile cloned from
// the pallet: the crate and its heaps (raw stone, raw iron, crystals, plant fibre) get one half-size crate per block;
// the 1 x 2 blocks (bricks, logs, planks) are laid as a pinwheel around a half block, mirrored on every other level;
// ingots (iron, glass) lie in two crossed layers like on the pallet; ropes, fabric and cogs get one per block; the
// mod's resources (coal, steel, dark crystal), which the pallet has no display for, are their own model stacked.
// With the Stacking research the blocks, ingots, ropes and fabric get a third layer (StackingVisual.Ratio), and the
// crates are as much taller, their heaps as much higher.
// Mixed pallets get a column per block of the same pieces. Resources without a layout here (bombs, wards...) keep
// the game's own display.
internal class LargeStockpileVisual : MonoBehaviour
{
	private enum Kind
	{
		Crate,
		Block,
		Ingot,
		Coil,
		Cog,
		// The resource's own model stacked in each block-wide column (the mod's resources: the pallet has no
		// display for them).
		Item
	}

	private sealed class Layout
	{
		public readonly Kind Kind;

		// Path of the display child holding the mesh, below the stockpile; null for the resource's own prefab.
		public readonly string Source;

		public readonly float LevelHeight;

		public readonly int Levels;

		public readonly float[] LevelY;

		public Layout(Kind kind, string source, float levelHeight = 0f, int levels = 0, float[] levelY = null)
		{
			Kind = kind;
			Source = source;
			LevelHeight = levelHeight;
			Levels = levels;
			LevelY = levelY;
		}
	}

	private struct Source
	{
		public Mesh Mesh;

		public Material[] Materials;

		public Quaternion Rotation;
	}

	private struct Placement
	{
		public Vector3 Position;

		public Quaternion Rotation;

		public Vector3 Scale;

		public Placement(Vector3 position, Quaternion rotation, Vector3 scale)
		{
			Position = position;
			Rotation = rotation;
			Scale = scale;
		}
	}

	// Keyed by the resource's type name.
	private static readonly Dictionary<string, Layout> Layouts = new Dictionary<string, Layout>
	{
		{ "Stones", new Layout(Kind.Crate, "Visual/Content/Stones") },
		{ "RawIron", new Layout(Kind.Crate, "Visual/Content/Iron") },
		{ "OrangeCrystal", new Layout(Kind.Crate, "Visual/Content/CrystalOrange") },
		{ "BlueCrystal", new Layout(Kind.Crate, "Visual/Content/CrystalBlue") },
		{ "Plant", new Layout(Kind.Crate, "Visual/Content/Plant") },
		{ "StoneBlock", new Layout(Kind.Block, "Blocks/Block1/Brick1", 0.9f, 2) },
		{ "WoodBlock", new Layout(Kind.Block, "Blocks/Block1/Log1", 0.85f, 2) },
		{ "PlankBlock", new Layout(Kind.Block, "Planks/Level1/Plank1", 0.3f, 6) },
		{ "Iron", new Layout(Kind.Ingot, "Ingots/Lingot") },
		{ "Glass", new Layout(Kind.Ingot, "Glass/Lingot1") },
		{ "Rope", new Layout(Kind.Coil, "Ropes/Rope1", levelY: new float[2] { 0.3f, 0.75f }) },
		{ "Fabric", new Layout(Kind.Coil, "Fabric/Fabric1", levelY: new float[2] { 0.6f, 1.33f }) },
		{ "Cog", new Layout(Kind.Cog, null) },
		{ "Clay", new Layout(Kind.Item, null) },
		{ "Terracotta", new Layout(Kind.Item, null) },
		{ "PurifiedBlueCrystal", new Layout(Kind.Item, null) }
	};

	private const string CratePath = "Visual/Crate";

	private const string ContentPath = "Visual/Content";

	// Top of the pallet's base.
	private const float BaseTop = 0.2f;

	// A block-sized crate is the pallet's 2 x 2 crate at this scale.
	private const float CrateScale = 0.47f;

	// Where the 2 x 2 crate stands, in its own space.
	private const float CrateY = 0.19f;

	// Column centres, in the order MixedStockpiles hands out columns; the pivot is the centre block.
	private static readonly Vector2[] Centres = new Vector2[9]
	{
		new Vector2(-1f, -1f),
		new Vector2(0f, -1f),
		new Vector2(1f, -1f),
		new Vector2(-1f, 0f),
		new Vector2(0f, 0f),
		new Vector2(1f, 0f),
		new Vector2(-1f, 1f),
		new Vector2(0f, 1f),
		new Vector2(1f, 1f)
	};

	internal static bool Enabled;

	private string _signature = string.Empty;

	// Empty and mixed large stockpiles are always drawn here; one resource only when it has a layout.
	public static bool Handles(Recepteur recepteur)
	{
		if (!Enabled || recepteur == null)
		{
			return false;
		}
		List<Ressource> types = MixedStockpiles.Types(recepteur);
		return types.Count != 1 || Layouts.ContainsKey(types[0].GetType().Name);
	}

	public static void Refresh(Recepteur recepteur, bool handled)
	{
		LargeStockpileVisual visual = recepteur.GetComponentInChildren<LargeStockpileVisual>(true);
		if (!handled)
		{
			if (visual != null)
			{
				visual.Clear();
			}
			return;
		}
		HideGameContent(recepteur.transform);
		if (visual == null)
		{
			GameObject go = new GameObject("CSP_LargeStockpileContent");
			go.transform.SetParent(recepteur.transform, false);
			visual = go.AddComponent<LargeStockpileVisual>();
		}
		visual.Build(recepteur);
	}

	// The crate and heaps are switched on by the pallet's animator (AnimatedProgression), not by its display.
	public static void HideGameContent(Transform root)
	{
		Transform crate = root.Find(CratePath);
		if (crate != null && crate.gameObject.activeSelf)
		{
			crate.gameObject.SetActive(false);
		}
		Transform content = root.Find(ContentPath);
		if (content == null)
		{
			return;
		}
		foreach (Transform heap in content)
		{
			if (heap.gameObject.activeSelf)
			{
				heap.gameObject.SetActive(false);
			}
		}
	}

	private void Clear()
	{
		_signature = string.Empty;
		for (int i = transform.childCount - 1; i >= 0; i--)
		{
			Destroy(transform.GetChild(i).gameObject);
		}
	}

	private void Build(Recepteur recepteur)
	{
		List<Ressource> types = MixedStockpiles.Types(recepteur);
		string signature = "#";
		foreach (Ressource resource in types)
		{
			signature += resource.GetType().Name + ":" + recepteur.ContentDescription.Value(resource) + "/" + recepteur.BaseCapacity.Value(resource) + ";";
		}
		// Every display on the stockpile asks for a refresh; build once per content change.
		if (signature == _signature)
		{
			return;
		}
		Clear();
		_signature = signature;
		if (types.Count == 1)
		{
			BuildSingle(recepteur, types[0]);
		}
		else if (types.Count > 1)
		{
			BuildMixed(recepteur, types);
		}
	}

	private void BuildSingle(Recepteur recepteur, Ressource resource)
	{
		Layout layout = Layouts[resource.GetType().Name];
		int capacity = Mathf.Max(1, recepteur.BaseCapacity.Value(resource));
		float fill = Mathf.Clamp01((float)recepteur.ContentDescription.Value(resource) / capacity);
		float ratio = StackingVisual.Ratio(recepteur, resource);
		if (layout.Kind == Kind.Crate)
		{
			// The heap measured against the crate's room before Stacking: a crate holding more is taller.
			foreach (Vector2 centre in Centres)
			{
				AddCrate(recepteur, layout, centre, fill * ratio, ratio);
			}
			return;
		}
		if (!TryGetSource(recepteur, resource, layout, out Source source))
		{
			return;
		}
		AddPieces(transform, source, LargePieces(layout, source, ratio), Vector2.zero, fill);
	}

	private void BuildMixed(Recepteur recepteur, List<Ressource> types)
	{
		int column = 0;
		foreach (Ressource resource in types)
		{
			Layouts.TryGetValue(resource.GetType().Name, out Layout layout);
			int perColumn = MixedStockpiles.PerColumn(recepteur, resource);
			int left = recepteur.ContentDescription.Value(resource);
			while (left > 0 && column < Centres.Length)
			{
				int inColumn = Mathf.Min(left, perColumn);
				float fill = (float)inColumn / perColumn;
				if (layout != null && layout.Kind == Kind.Crate)
				{
					AddCrate(recepteur, layout, Centres[column], fill, 1f);
				}
				else if (TryGetSource(recepteur, resource, layout, out Source source))
				{
					List<Placement> pieces = (layout != null) ? ColumnPieces(layout, source) : RepresentativeColumn(source);
					AddPieces(transform, source, pieces, Centres[column], fill);
				}
				left -= inColumn;
				column++;
			}
		}
	}

	// A block-sized crate on the pallet with its heap, as high as the crate is full; height stretches the crate upwards.
	private void AddCrate(Recepteur recepteur, Layout layout, Vector2 centre, float fill, float height)
	{
		Transform crateSource = recepteur.transform.Find(CratePath);
		Transform heapSource = recepteur.transform.Find(layout.Source);
		if (!TryGetSource(crateSource, out Source crate))
		{
			return;
		}
		GameObject cell = new GameObject("Crate");
		cell.transform.SetParent(transform, false);
		cell.transform.localPosition = new Vector3(centre.x, BaseTop, centre.y);
		cell.transform.localScale = Vector3.one * CrateScale;
		// The crate mesh stands on its z axis (turned upright by its rotation).
		AddPiece(cell.transform, crate, new Placement(Vector3.zero, crate.Rotation, new Vector3(1f, 1f, height)));
		if (fill > 0f && TryGetSource(heapSource, out Source heap))
		{
			// Low in the crate when nearly empty, up to just under the rim when full.
			float y = 0.25f + fill * 1.1f - CrateY;
			AddPiece(cell.transform, heap, new Placement(new Vector3(0f, y, 0f), heap.Rotation, Vector3.one));
		}
	}

	// Shows the first part of the pieces (bottom first), as many as the fill asks for.
	private static void AddPieces(Transform parent, Source source, List<Placement> pieces, Vector2 offset, float fill)
	{
		int count = (fill <= 0f) ? 0 : Mathf.Max(1, Mathf.CeilToInt(fill * pieces.Count));
		for (int i = 0; i < count && i < pieces.Count; i++)
		{
			Placement piece = pieces[i];
			piece.Position += new Vector3(offset.x, 0f, offset.y);
			AddPiece(parent, source, piece);
		}
	}

	private static void AddPiece(Transform parent, Source source, Placement placement)
	{
		GameObject piece = new GameObject(source.Mesh.name);
		piece.transform.SetParent(parent, false);
		piece.transform.localPosition = placement.Position;
		piece.transform.localRotation = placement.Rotation;
		piece.transform.localScale = placement.Scale;
		piece.AddComponent<MeshFilter>().sharedMesh = source.Mesh;
		piece.AddComponent<MeshRenderer>().sharedMaterials = source.Materials;
	}

	private static bool TryGetSource(Recepteur recepteur, Ressource resource, Layout layout, out Source source)
	{
		if (layout != null && layout.Source != null)
		{
			return TryGetSource(recepteur.transform.Find(layout.Source), out source);
		}
		// No display child for it (the pallet's cog slots are empty): the resource's own prefab.
		source = default(Source);
		Factory.AssetKey key = Description.RepresentativeOf(resource);
		GameObject prefab = (key != null) ? Factory.Peek(key) : null;
		MeshFilter filter = (prefab != null) ? prefab.GetComponentInChildren<MeshFilter>(true) : null;
		if (!TryGetSource(filter != null ? filter.transform : null, out source))
		{
			return false;
		}
		source.Rotation = Quaternion.Inverse(prefab.transform.rotation) * filter.transform.rotation;
		return true;
	}

	private static bool TryGetSource(Transform transform, out Source source)
	{
		source = default(Source);
		MeshFilter filter = (transform != null) ? transform.GetComponent<MeshFilter>() : null;
		MeshRenderer renderer = (filter != null) ? filter.GetComponent<MeshRenderer>() : null;
		if (filter == null || filter.sharedMesh == null || renderer == null)
		{
			return false;
		}
		source.Mesh = filter.sharedMesh;
		source.Materials = renderer.sharedMaterials;
		source.Rotation = transform.localRotation;
		return true;
	}

	// The whole 3 x 3 for one resource, bottom first; ratio is the room over the room before Stacking (more layers).
	private static List<Placement> LargePieces(Layout layout, Source source, float ratio)
	{
		List<Placement> pieces = new List<Placement>();
		switch (layout.Kind)
		{
			case Kind.Block:
				for (int level = 0; level < Mathf.Max(layout.Levels, Mathf.RoundToInt(layout.Levels * ratio)); level++)
				{
					float y = BaseTop + layout.LevelHeight * (level + 0.5f);
					foreach (Vector4 block in Pinwheel(level))
					{
						// The block meshes are 1 x 2 along z; a quarter turn lays them along x. The middle one is a half block.
						float length = (block.w > 0f) ? 0.45f : 0.9f;
						pieces.Add(new Placement(new Vector3(block.x, y, block.y), Quaternion.Euler(0f, (block.z > 0f) ? 0f : 90f, 0f), new Vector3(0.9f, 0.9f, length)));
					}
				}
				break;
			case Kind.Ingot:
				// As on the pallet: a layer lengthwise along x, then one crossed along z, and so on up.
				for (int layer = 0; layer < Mathf.Max(2, Mathf.RoundToInt(2 * ratio)); layer++)
				{
					float y = 0.35f + 0.3f * layer;
					if (layer % 2 == 0)
					{
						foreach (float z in new float[6] { -1.25f, -0.75f, -0.25f, 0.25f, 0.75f, 1.25f })
						{
							for (int x = -1; x <= 1; x++)
							{
								pieces.Add(new Placement(new Vector3(x, y, z), Quaternion.Euler(0f, 90f, 0f), Vector3.one));
							}
						}
					}
					else
					{
						foreach (float x in new float[6] { -1.125f, -0.675f, -0.225f, 0.225f, 0.675f, 1.125f })
						{
							for (int z = -1; z <= 1; z++)
							{
								pieces.Add(new Placement(new Vector3(x, y, z), Quaternion.Euler(0f, 180f, 0f), Vector3.one));
							}
						}
					}
				}
				break;
			case Kind.Coil:
			{
				float step = layout.LevelY[1] - layout.LevelY[0];
				for (int level = 0; level < Mathf.Max(layout.LevelY.Length, Mathf.RoundToInt(layout.LevelY.Length * ratio)); level++)
				{
					float y = layout.LevelY[0] + step * level;
					foreach (Vector2 centre in Centres)
					{
						pieces.Add(new Placement(new Vector3(centre.x, y, centre.y), source.Rotation, Vector3.one));
					}
				}
				break;
			}
			case Kind.Cog:
				foreach (Vector2 centre in Centres)
				{
					pieces.Add(new Placement(new Vector3(centre.x, 0.35f, centre.y), Quaternion.Euler(0f, (centre.x + centre.y) * 35f, 0f) * source.Rotation, Vector3.one));
				}
				foreach (Vector2 centre in new Vector2[4] { new Vector2(-0.5f, -0.5f), new Vector2(0.5f, -0.5f), new Vector2(-0.5f, 0.5f), new Vector2(0.5f, 0.5f) })
				{
					pieces.Add(new Placement(new Vector3(centre.x, 0.5f, centre.y), Quaternion.Euler(0f, 45f, 0f) * source.Rotation, Vector3.one));
				}
				break;
			case Kind.Item:
				// Layer by layer over the nine columns, so a part-full stockpile is evenly low.
				foreach (Placement layer in RepresentativeColumn(source))
				{
					foreach (Vector2 centre in Centres)
					{
						Placement piece = layer;
						piece.Position += new Vector3(centre.x, 0f, centre.y);
						pieces.Add(piece);
					}
				}
				break;
		}
		return pieces;
	}

	// Four 1 x 2 blocks around a half block fill 3 x 3 without stretching; every other level is mirrored.
	// Each entry: x, z, 1 when the block runs along z, 1 for the half block.
	private static Vector4[] Pinwheel(int level)
	{
		if (level % 2 == 0)
		{
			return new Vector4[5]
			{
				new Vector4(-0.5f, -1f, 0f, 0f),
				new Vector4(1f, -0.5f, 1f, 0f),
				new Vector4(0.5f, 1f, 0f, 0f),
				new Vector4(-1f, 0.5f, 1f, 0f),
				new Vector4(0f, 0f, 0f, 1f)
			};
		}
		return new Vector4[5]
		{
			new Vector4(0.5f, -1f, 0f, 0f),
			new Vector4(1f, 0.5f, 1f, 0f),
			new Vector4(-0.5f, 1f, 0f, 0f),
			new Vector4(-1f, -0.5f, 1f, 0f),
			new Vector4(0f, 0f, 1f, 1f)
		};
	}

	// One block-wide column of a mixed stockpile, bottom first.
	private static List<Placement> ColumnPieces(Layout layout, Source source)
	{
		List<Placement> pieces = new List<Placement>();
		switch (layout.Kind)
		{
			case Kind.Block:
			{
				int levels = Mathf.RoundToInt(1.8f / layout.LevelHeight);
				for (int level = 0; level < levels; level++)
				{
					pieces.Add(new Placement(new Vector3(0f, BaseTop + layout.LevelHeight * (level + 0.5f), 0f), Quaternion.Euler(0f, (level % 2 == 0) ? 90f : 0f, 0f), new Vector3(0.9f, 0.9f, 0.45f)));
				}
				break;
			}
			case Kind.Ingot:
				for (int level = 0; level < 4; level++)
				{
					foreach (float p in new float[2] { -0.25f, 0.25f })
					{
						Vector3 position = (level % 2 == 0) ? new Vector3(0f, 0.35f + 0.3f * level, p) : new Vector3(p, 0.35f + 0.3f * level, 0f);
						pieces.Add(new Placement(position, Quaternion.Euler(0f, (level % 2 == 0) ? 90f : 180f, 0f), Vector3.one));
					}
				}
				break;
			case Kind.Coil:
			{
				float step = layout.LevelY[1] - layout.LevelY[0];
				for (int level = 0; level < 3; level++)
				{
					pieces.Add(new Placement(new Vector3(0f, layout.LevelY[0] + step * level, 0f), source.Rotation, Vector3.one));
				}
				break;
			}
			case Kind.Cog:
				for (int level = 0; level < 6; level++)
				{
					pieces.Add(new Placement(new Vector3(0f, 0.28f + 0.15f * level, 0f), Quaternion.Euler(0f, level * 30f, 0f) * source.Rotation, Vector3.one));
				}
				break;
			case Kind.Item:
				pieces.AddRange(RepresentativeColumn(source));
				break;
		}
		return pieces;
	}

	// A resource without a layout: its own mesh stacked, one per layer, scaled to a 0.85 wide column.
	private static List<Placement> RepresentativeColumn(Source source)
	{
		List<Placement> pieces = new List<Placement>();
		Bounds bounds = source.Mesh.bounds;
		float scale = Mathf.Min(0.85f / Mathf.Max(0.01f, bounds.size.x), 0.85f / Mathf.Max(0.01f, bounds.size.z));
		scale = Mathf.Min(scale, 1.8f / Mathf.Max(0.01f, bounds.size.y));
		float unit = bounds.size.y * scale;
		int units = Mathf.Max(1, Mathf.FloorToInt(1.6f / Mathf.Max(0.01f, unit)));
		for (int i = 0; i < units; i++)
		{
			Quaternion rotation = Quaternion.Euler(0f, (i % 2 == 0) ? 0f : 90f, 0f);
			Vector3 position = new Vector3(0f, BaseTop + unit * (i + 0.5f), 0f) - rotation * (bounds.center * scale);
			pieces.Add(new Placement(position, rotation, Vector3.one * scale));
		}
		return pieces;
	}
}

// Large stockpiles: the resource displays of the cloned pallet stay hidden and LargeStockpileVisual draws the content.
[Feature(Features.LargeStockpile, Features.LargeStockpileInfo)]
[HarmonyPatch(typeof(RecepteurDisplay), "UpdateElements")]
internal static class LargeStockpileDisplayPatch
{
	private static void Enable()
	{
		LargeStockpileVisual.Enabled = true;
	}

	private static bool Prefix(RecepteurDisplay __instance)
	{
		Recepteur recepteur = __instance.targetRecepteur;
		if (!LargeStockpile.IsLarge(recepteur))
		{
			return true;
		}
		bool handled = LargeStockpileVisual.Handles(recepteur);
		LargeStockpileVisual.Refresh(recepteur, handled);
		if (!handled)
		{
			return true;
		}
		foreach (RecepteurDisplay.DisplayElement element in __instance.displayElements)
		{
			if (MixedStockpileDisplayPatch.ShowsResource(element))
			{
				element.ToggleDisplay(false);
			}
			else
			{
				element.UpdateDisplay(recepteur);
			}
		}
		return false;
	}
}

// The pallet's animator shows the stretched crate and heap; on a large stockpile drawn by LargeStockpileVisual it stays off.
[Feature(Features.LargeStockpile, Features.LargeStockpileInfo)]
[HarmonyPatch(typeof(AnimatedProgression), "Update")]
internal static class LargeStockpileAnimatorPatch
{
	private static bool Prefix(AnimatedProgression __instance)
	{
		if (!LargeStockpile.IsLarge(__instance) || !LargeStockpileVisual.Handles(__instance.recepteur))
		{
			return true;
		}
		__instance.enabled = false;
		LargeStockpileVisual.HideGameContent(__instance.transform);
		return false;
	}
}
