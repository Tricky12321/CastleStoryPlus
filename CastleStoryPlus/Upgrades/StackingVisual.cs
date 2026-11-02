using System;
using System.Collections.Generic;
using Brix.Game.AI;
using Brix.Game.Components;
using Brix.Game.Semantique;
using Brix.Game.Storage;
using CastleStoryPlus.Building;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;

namespace CastleStoryPlus.Upgrades;

// What the Stacking research looks like on a 2 x 2 stockpile. The pallet stacks its goods 2 layers high (2 bricks,
// then 2 crossed over them; 8 ingots, then 8 crossed...); with Stacking the goods stacked in layers get whole layers
// more (Room: a third more per tier, up to 4 layers instead of 2), and the layers above the pallet's own two are drawn here,
// as copies of the pallet's own pieces, alternating like its two layers, shown one by one as the stockpile fills
// up. Goods in the crate (stone, raw iron, crystals, plant fibre...) get the plain extra room; the crate is as much
// taller and its heap rises as much higher. Mixed pallets keep their columns (MixedStockpileVisual), large
// stockpiles are drawn by LargeStockpileVisual (which asks Ratio). Looks only: drawn on every machine from the
// team's tier and the stockpile's content, which are synced and saved by the game and TeamUpgrades.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
internal static class StackingVisual
{
	private sealed class Spec
	{
		// Display child below the pallet holding the pieces.
		public readonly string Group;

		// The children of Group in the pallet's first and second layer, bottom first.
		public readonly int[][] Layers;

		// Height of a layer.
		public readonly float Pitch;

		// The pieces are the children of those children (the planks: a level holds two).
		public readonly bool Nested;

		public Spec(string group, int[] first, int[] second, float pitch, bool nested = false)
		{
			Group = group;
			Layers = new int[2][] { first, second };
			Pitch = pitch;
			Nested = nested;
		}
	}

	private sealed class Drawn
	{
		// What the layers show: the resource, how many and the room (none: no layers). Count -1: drawn again on the
		// next check.
		public Type Type;

		public int Count;

		public int Capacity;

		public GameObject Holder;

		public Transform Crate;

		public Vector3 CrateScale;

		// The combined meshes under Holder, destroyed with it (a mesh made in code is not freed with its object).
		public readonly List<Mesh> Meshes = new List<Mesh>();
	}

	private const string HolderName = "CSP_StackingLayers";

	private const string CratePath = "Visual/Crate";

	private const string HeapPath = "Visual/Content";

	// The heap's height in the crate: the game's animator blends it from empty to full (Stockpile_FillMin/Max).
	private const float HeapEmpty = 0.2f;

	private const float HeapFull = 1.65f;

	private static readonly int[] FirstTwo = { 0, 1 };

	private static readonly int[] SecondTwo = { 2, 3 };

	private static readonly int[] FirstFour = { 0, 1, 2, 3 };

	private static readonly int[] SecondFour = { 4, 5, 6, 7 };

	private static readonly int[] FirstEight = { 0, 1, 2, 3, 4, 5, 6, 7 };

	private static readonly int[] SecondEight = { 8, 9, 10, 11, 12, 13, 14, 15 };

	// The goods a pallet stacks in layers, keyed by the resource's type name.
	private static readonly Dictionary<string, Spec> Specs = new Dictionary<string, Spec>
	{
		{ "StoneBlock", new Spec("Blocks", FirstTwo, SecondTwo, 0.9f) },
		{ "WoodBlock", new Spec("Blocks", FirstTwo, SecondTwo, 0.9f) },
		{ "PlankBlock", new Spec("Planks", new int[3] { 0, 1, 2 }, new int[3] { 3, 4, 5 }, 0.9f, nested: true) },
		{ "ExplosiveBarrel", new Spec("Barrels", FirstFour, SecondFour, 1f) },
		{ "BlueCrystalChunck", new Spec("BlueCrystalChunck", FirstFour, SecondFour, 1f) },
		{ "Rope", new Spec("Ropes", FirstFour, SecondFour, 0.45f) },
		{ "Fabric", new Spec("Fabric", FirstFour, SecondFour, 0.73f) },
		{ "Iron", new Spec("Ingots", FirstEight, SecondEight, 0.3f) },
		{ "Glass", new Spec("Glass", FirstEight, SecondEight, 0.3f) },
		{ "Terracotta", new Spec("TerraCotta", FirstEight, SecondEight, 0.3f) },
		{ "PurifiedBrimstone", new Spec("PurifiedBrimstone", FirstEight, SecondEight, 0.3f) }
	};

	private static readonly Dictionary<Recepteur, Drawn> All = new Dictionary<Recepteur, Drawn>();

	private static bool _warnedGone;

	private static void Enable()
	{
		GameSession.OnLeave(Clear);
	}

	private static void Clear()
	{
		foreach (Drawn drawn in All.Values)
		{
			DestroyHolder(drawn);
		}
		All.Clear();
	}

	private static void DestroyHolder(Drawn drawn)
	{
		foreach (Mesh mesh in drawn.Meshes)
		{
			UnityEngine.Object.Destroy(mesh);
		}
		drawn.Meshes.Clear();
		if (drawn.Holder != null)
		{
			UnityEngine.Object.Destroy(drawn.Holder);
			drawn.Holder = null;
		}
	}

	// Stacking's room for a resource: the plain share (a third more per tier, twice the room at the top tier, so goods
	// stacked 2 layers high go up to 4). A layer that is not full is drawn piece by piece, as the pallet's own are.
	internal static int Room(Recepteur recepteur, Type type, int original, float factor)
	{
		return Mathf.RoundToInt(original * factor);
	}

	// The stockpile's room for the resource over its room before Stacking (1.5 for 3 layers instead of 2).
	internal static float Ratio(Recepteur recepteur, Ressource resource)
	{
		int original = Stacking.OriginalRoom(recepteur, resource.GetType());
		int room = recepteur.BaseCapacity.Value(resource);
		return (original > 0 && room > original) ? (float)room / original : 1f;
	}

	// A 2 x 2 pallet holding one resource: its extra layers, or its taller crate and higher heap; anything else is
	// put back as the game draws it.
	internal static void Refresh(Recepteur recepteur)
	{
		if (recepteur == null || !MixedStockpiles.IsPallet(recepteur) || LargeStockpile.IsLarge(recepteur))
		{
			return;
		}
		All.TryGetValue(recepteur, out Drawn drawn);
		Ressource resource = MixedStockpiles.SingleType(recepteur);
		float ratio = (resource != null) ? Ratio(recepteur, resource) : 1f;
		if (drawn == null && ratio <= 1f)
		{
			return;
		}
		if (drawn == null)
		{
			drawn = new Drawn();
			All[recepteur] = drawn;
		}
		AnimatedProgression progression = recepteur.GetComponent<AnimatedProgression>();
		bool crate = resource != null && ratio > 1f && recepteur.ContentString() == "Fill";
		Stretch(recepteur, drawn, crate ? ratio : 1f);
		if (crate)
		{
			int original = Mathf.Max(1, Stacking.OriginalRoom(recepteur, resource.GetType()));
			RaiseHeap(recepteur, Mathf.Clamp((float)recepteur.ContentDescription.Value(resource) / original, 0f, ratio));
		}
		bool layers = resource != null && ratio > 1f && !crate;
		Type type = layers ? resource.GetType() : null;
		int count = layers ? recepteur.ContentDescription.Value(resource) : 0;
		int capacity = layers ? recepteur.BaseCapacity.Value(resource) : 0;
		if (type == drawn.Type && count == drawn.Count && capacity == drawn.Capacity)
		{
			return;
		}
		// The pieces are copied from where they lie, so not while the pallet's animator drops them into place.
		if (progression != null && progression.enabled)
		{
			return;
		}
		DestroyHolder(drawn);
		drawn.Type = type;
		drawn.Count = count;
		drawn.Capacity = capacity;
		if (layers && !BuildLayers(recepteur, resource, drawn))
		{
			// Its own pieces not shown yet: try again on the next check.
			drawn.Count = -1;
		}
	}

	// The crate as much taller as it holds more; it stands on its z axis (turned upright by its rotation).
	private static void Stretch(Recepteur recepteur, Drawn drawn, float height)
	{
		if (drawn.Crate == null)
		{
			drawn.Crate = recepteur.transform.Find(CratePath);
			if (drawn.Crate == null)
			{
				return;
			}
			drawn.CrateScale = drawn.Crate.localScale;
		}
		Vector3 scale = new Vector3(drawn.CrateScale.x, drawn.CrateScale.y, drawn.CrateScale.z * height);
		if (drawn.Crate.localScale != scale)
		{
			drawn.Crate.localScale = scale;
		}
	}

	// The heap as the game's animator places it, measured against the room before Stacking, so a fuller crate
	// heaps higher than the game's full.
	private static void RaiseHeap(Recepteur recepteur, float fill)
	{
		Transform heap = recepteur.transform.Find(HeapPath);
		if (heap == null)
		{
			return;
		}
		Vector3 position = heap.localPosition;
		position.y = HeapEmpty + (HeapFull - HeapEmpty) * fill;
		heap.localPosition = position;
	}

	// The layers above the pallet's two: layer 3 is a copy of the first one two layers up, layer 4 of the second...
	// Shown bottom first, one piece per good above the pallet's own two layers. The pieces of one pallet are
	// combined into one mesh per material (a stockpile's pieces drew a draw call each, and a shadow each: some 500
	// of a large base's 3400 draw calls); a piece whose mesh cannot be read is drawn on its own.
	private static bool BuildLayers(Recepteur recepteur, Ressource resource, Drawn drawn)
	{
		if (!Specs.TryGetValue(resource.GetType().Name, out Spec spec))
		{
			return true;
		}
		int original = Stacking.OriginalRoom(recepteur, resource.GetType());
		int extra = recepteur.ContentDescription.Value(resource) - original;
		if (original <= 0 || extra <= 0)
		{
			return true;
		}
		Transform root = recepteur.transform;
		Transform group = root.Find(spec.Group);
		if (group == null)
		{
			return true;
		}
		int perLayer = original / 2;
		int layers = Mathf.CeilToInt((float)extra / perLayer);
		GameObject holder = new GameObject(HolderName);
		holder.transform.SetParent(root, false);
		drawn.Holder = holder;
		int left = extra;
		var combined = new Dictionary<Material, List<CombineInstance>>();
		var casting = new Dictionary<Material, MeshRenderer>();
		for (int layer = 2; layer < 2 + layers && left > 0; layer++)
		{
			float up = (layer - layer % 2) * spec.Pitch;
			foreach (Transform piece in Pieces(group, spec, layer % 2))
			{
				if (left <= 0)
				{
					break;
				}
				if (!Copy(root, piece, holder.transform, up, combined, casting))
				{
					return false;
				}
				left--;
			}
		}
		foreach (KeyValuePair<Material, List<CombineInstance>> entry in combined)
		{
			// A piece's mesh gone since it was looked at (the game rebuilds a pallet's pieces): left out.
			int gone = entry.Value.RemoveAll(instance => instance.mesh == null);
			if (gone > 0 && !_warnedGone)
			{
				_warnedGone = true;
				Plugin.Log.LogWarning("StackingVisual: " + gone + " piece mesh(es) of " + recepteur.name + " (" + entry.Key.name + ") gone before they were combined");
			}
			if (entry.Value.Count == 0)
			{
				continue;
			}
			var mesh = new Mesh { name = HolderName };
			mesh.CombineMeshes(entry.Value.ToArray(), true, true);
			drawn.Meshes.Add(mesh);
			GameObject part = new GameObject(entry.Key.name);
			part.transform.SetParent(holder.transform, false);
			part.AddComponent<MeshFilter>().sharedMesh = mesh;
			MeshRenderer renderer = part.AddComponent<MeshRenderer>();
			renderer.sharedMaterial = entry.Key;
			renderer.shadowCastingMode = casting[entry.Key].shadowCastingMode;
			renderer.receiveShadows = casting[entry.Key].receiveShadows;
		}
		return true;
	}

	private static List<Transform> Pieces(Transform group, Spec spec, int layer)
	{
		List<Transform> pieces = new List<Transform>();
		foreach (int index in spec.Layers[layer])
		{
			if (index >= group.childCount)
			{
				continue;
			}
			Transform child = group.GetChild(index);
			if (!spec.Nested)
			{
				pieces.Add(child);
				continue;
			}
			foreach (Transform inner in child)
			{
				pieces.Add(inner);
			}
		}
		return pieces;
	}

	// The meshes shown under a piece (a pallet block holds a brick and a log, one of them shown), up higher: each
	// submesh into the combined list of its material, or, when the mesh cannot be read, as an object of its own.
	private static bool Copy(Transform root, Transform piece, Transform holder, float up, Dictionary<Material, List<CombineInstance>> combined, Dictionary<Material, MeshRenderer> casting)
	{
		bool any = false;
		foreach (MeshFilter filter in piece.GetComponentsInChildren<MeshFilter>(false))
		{
			MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
			if (filter.sharedMesh == null || renderer == null || !renderer.enabled)
			{
				continue;
			}
			Mesh mesh = filter.sharedMesh;
			if (mesh.isReadable)
			{
				// Into the holder's space (the pallet's), then up by the layer's height.
				Matrix4x4 transform = Matrix4x4.TRS(Vector3.up * up, Quaternion.identity, Vector3.one) * holder.worldToLocalMatrix * filter.transform.localToWorldMatrix;
				Material[] materials = renderer.sharedMaterials;
				for (int sub = 0; sub < mesh.subMeshCount && sub < materials.Length; sub++)
				{
					if (materials[sub] == null)
					{
						continue;
					}
					if (!combined.TryGetValue(materials[sub], out List<CombineInstance> instances))
					{
						instances = new List<CombineInstance>();
						combined[materials[sub]] = instances;
						casting[materials[sub]] = renderer;
					}
					instances.Add(new CombineInstance { mesh = mesh, subMeshIndex = sub, transform = transform });
				}
				any = true;
				continue;
			}
			GameObject copy = new GameObject(filter.sharedMesh.name);
			copy.transform.SetParent(holder, false);
			copy.transform.localPosition = root.InverseTransformPoint(filter.transform.position) + Vector3.up * up;
			copy.transform.localRotation = Quaternion.Inverse(root.rotation) * filter.transform.rotation;
			copy.transform.localScale = filter.transform.lossyScale;
			copy.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
			copy.AddComponent<MeshRenderer>().sharedMaterials = renderer.sharedMaterials;
			any = true;
		}
		return any;
	}
}

// Drawn again as soon as the pallet's display changes (after the game has shown its own pieces).
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(RecepteurDisplay), "UpdateElements")]
internal static class StackingVisualDisplayPatch
{
	private static void Postfix(RecepteurDisplay __instance)
	{
		StackingVisual.Refresh(__instance.targetRecepteur);
	}
}

// The game's animator moves the heap while it plays, and drops the pieces into place: drawn again after it.
[Feature(Features.Upgrades, Features.UpgradesInfo)]
[HarmonyPatch(typeof(AnimatedProgression), "Update")]
internal static class StackingVisualAnimatorPatch
{
	private static void Postfix(AnimatedProgression __instance)
	{
		StackingVisual.Refresh(__instance.recepteur);
	}
}
