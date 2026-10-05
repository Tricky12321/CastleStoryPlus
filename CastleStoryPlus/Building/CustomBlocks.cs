using System;
using System.Collections.Generic;
using Brix.Components;
using Brix.Engine;
using Brix.Engine.Blocks;
using Brix.Engine.Blocks.MeshSelector;
using Brix.Engine.Geometrie;
using Brix.External.Factories;
using Brix.External.Factories.Templates;
using Brix.Game.AI;
using Brix.Game.AI.Damage;
using Brix.Game.Components;
using Brix.Game.Components.DisplayDataGenerator;
using Brix.Game.Components.Volumes;
using Brix.Game.Semantique;
using Brix.Input;
using Brix.Util;
using BepInEx.Configuration;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CastleStoryPlus.Building;

// New building blocks in the build menu's brick wheel: stone bricks of 2 x 2 and 2 x 4 blocks, and wooden slabs of
// 1 x 1, 2 x 1, 2 x 2 and 2 x 4 blocks, half a block high (in the upper half of the block, like the game's plank, so
// they are walked on at the block's top). Each is three templates, as the game's blocks: the free block
// (Blocks/<Name>, also what falls when it loses its support), the blueprint (Blueprints/<Name>) and the placed block,
// which the game makes from the first two when it reads the block infos. The stone bricks are clones of the game's
// brick (Stone_DoubleBrick) with a mesh of its brick mesh laid side by side; the slabs clone the plank's free block
// (so a fallen slab is planks, not bricks) and the brick's blueprint. Bricks rest on stone or the ground like the
// game's brick. A slab also holds on to the side of a stone block or the terrain: its support cells are the blocks
// below it and the blocks round it at its own height, and one of them is enough. The IDs are kept in saves and must
// never change; saves with these blocks need the mod.
[Feature(Features.CustomBlocks, Features.CustomBlocksInfo)]
[HarmonyPatch(typeof(Factory), nameof(Factory.Awake))]
internal static class CustomBlocks
{
	private sealed class Shape
	{
		public string Name;

		public int Id;

		public bool Wood;

		// Size in blocks along x and z.
		public int Width;

		public int Depth;

		public int Cost;

		public float Hp;

		public string Label;

		// The game's icon, used when the mod's (IconFile, Assets/Build) is missing.
		public string Icon;

		public string IconFile;
	}

	private static readonly Shape[] Shapes = new Shape[6]
	{
		new Shape { Name = "Stone_Brick2x2", IconFile = "stone_brick_2x2", Id = 9001, Width = 2, Depth = 2, Cost = 2, Hp = 960f, Label = "Large brick 2x2", Icon = "_Block" },
		new Shape { Name = "Stone_Brick2x4", IconFile = "stone_brick_2x4", Id = 9002, Width = 2, Depth = 4, Cost = 4, Hp = 1920f, Label = "Long brick 2x4", Icon = "_Brick" },
		new Shape { Name = "Wood_Slab1x1", IconFile = "wood_slab_1x1", Id = 9003, Wood = true, Width = 1, Depth = 1, Cost = 1, Hp = 120f, Label = "Wood slab 1x1", Icon = "_Plank" },
		new Shape { Name = "Wood_Slab2x1", IconFile = "wood_slab_2x1", Id = 9004, Wood = true, Width = 1, Depth = 2, Cost = 1, Hp = 240f, Label = "Wood slab 2x1", Icon = "_Plank" },
		new Shape { Name = "Wood_Slab2x2", IconFile = "wood_slab_2x2", Id = 9005, Wood = true, Width = 2, Depth = 2, Cost = 2, Hp = 480f, Label = "Wood slab 2x2", Icon = "_Plank" },
		new Shape { Name = "Wood_Slab2x4", IconFile = "wood_slab_2x4", Id = 9006, Wood = true, Width = 2, Depth = 4, Cost = 4, Hp = 960f, Label = "Wood slab 2x4", Icon = "_Plank" }
	};

	private const string StoneBlockSource = "Stone_DoubleBrick";

	private const string WoodBlockSource = "Wood_SinglePlank";

	private const string BlueprintSource = "Stone_DoubleBrick";

	// Slabs fill the upper half of their block.
	private const float SlabHeight = 0.5f;

	private static readonly Dictionary<string, Mesh> Meshes = new Dictionary<string, Mesh>();

	private static Material _woodMaterial;

	private static bool _woodMaterialRead;

	private static bool _woodMaterialApplied;

	internal static ConfigEntry<KeyCode> AnchorKey;

	// Which corner of the block goes where the mouse points: 0 to 3, the corners in turn.
	internal static int Anchor;

	private static void Enable()
	{
		AnchorKey = Plugin.Cfg.Bind("Building", "BlockAnchorKey", KeyCode.T, "While placing one of the mod's bricks or slabs: choose the next corner of the block to place it by.");
		UI.BuildIcons.Register();
		string stone = string.Empty, wood = string.Empty;
		foreach (Shape shape in Shapes)
		{
			string line = "_t.Add(AssetKey.New(\"Blueprints\", \"" + shape.Name + "\"),\t\t{ Name = ||\"" + shape.Label + "\",\t\tIcon = " + UI.BuildIcons.Lua(shape.IconFile, shape.Icon) + ",\tHotkey = \"\",\tgroupId = " + (shape.Wood ? 5 : 1) + " })\n";
			if (shape.Wood)
			{
				wood += line;
			}
			else
			{
				stone += line;
			}
		}
		LuaInjection.AddPatch(Features.CustomBlocks, "LUI/Meta/Meta_StoneBlock.lua", "_t.Add(AssetKey.New(\"Blueprints\", \"Stone_Staircase\"),", LuaInjection.Mode.InsertBefore, stone);
		LuaInjection.AddPatch(Features.CustomBlocks, "LUI/Meta/Meta_StoneBlock.lua", "_t.Add(AssetKey.New(\"Blueprints\", \"Wood_Ramp_TopA\"),", LuaInjection.Mode.InsertBefore, wood);
	}

	// Factories with the same name are merged; look at the registered ones after every Awake (see CustomBuilding).
	private static void Postfix()
	{
		foreach (Shape shape in Shapes)
		{
			TryCreate("Blocks", shape, blueprint: false);
			TryCreate("Blueprints", shape, blueprint: true);
			TryCreateDescriptor(shape);
		}
		ApplyWoodMaterial();
	}

	// The plank's material lives in another factory (MeshVariations), which may load after the blocks: the wood
	// blocks get it once it is there.
	internal static void ApplyWoodMaterial()
	{
		if (_woodMaterialApplied || !Factory.Factories.TryGetValue("Blocks", out Factory blocks) || blocks == null)
		{
			return;
		}
		SetUpWoodMaterial();
		if (_woodMaterial == null)
		{
			return;
		}
		foreach (Shape shape in Shapes)
		{
			if (shape.Wood && blocks.cachedTemplates.ContainsKey(shape.Name))
			{
				MeshRenderer renderer = blocks.GetTemplate(shape.Name).gameObject.GetComponent<MeshRenderer>();
				if (renderer != null)
				{
					renderer.sharedMaterials = new Material[1] { _woodMaterial };
				}
			}
		}
		// Block infos made before this keep the materials they read from the template.
		if (BlockInfoGenerator._idToBlockInfos != null)
		{
			foreach (BlockInfo info in BlockInfoGenerator._idToBlockInfos.Values)
			{
				if (info == null || info.Key == null || !IsWoodShape(info.Key.Name))
				{
					continue;
				}
				DefaultVoxelDrawer drawer = info.VoxelDrawer as DefaultVoxelDrawer;
				DefaultVoxelDisplayDataGenerator generator = (drawer != null) ? drawer.VoxelDisplayDataGenerator as DefaultVoxelDisplayDataGenerator : null;
				if (generator != null)
				{
					generator._mats = new Material[1] { _woodMaterial };
				}
			}
		}
		_woodMaterialApplied = true;
	}

	private static bool IsWoodShape(string name)
	{
		foreach (Shape shape in Shapes)
		{
			if (shape.Wood && shape.Name == name)
			{
				return true;
			}
		}
		return false;
	}

	private static void TryCreate(string factoryName, Shape shape, bool blueprint)
	{
		if (!Factory.Factories.TryGetValue(factoryName, out Factory factory) || factory == null)
		{
			return;
		}
		string source = blueprint ? BlueprintSource : (shape.Wood ? WoodBlockSource : StoneBlockSource);
		try
		{
			if (factory.cachedTemplates.Count == 0)
			{
				factory.ResetTemplateCache();
			}
			if (factory.cachedTemplates.ContainsKey(shape.Name) || !factory.cachedTemplates.ContainsKey(source))
			{
				return;
			}
			if (shape.Wood)
			{
				SetUpWoodMaterial();
			}
			GameObject go = Object.Instantiate(factory.GetTemplate(source).gameObject, factory.transform);
			go.name = shape.Name;
			if (blueprint)
			{
				SetUpBlueprint(go, shape);
			}
			else
			{
				SetUpBlock(go, shape);
			}
			FactoryImprint imprint = go.GetComponent<FactoryImprint>();
			if (imprint != null)
			{
				imprint.AssetKey = new Factory.AssetKey(factory.name, shape.Name);
			}
			Factory.AssetKey key = factory.AddAsset<CloneTemplate>(go);
			Brix.Network.FactoryRegistrator.RegisterHandlerForTemplate(key);
			Plugin.Log.LogInfo("CustomBlocks: added " + key);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("CustomBlocks: could not add " + shape.Name + " to factory " + factoryName + ": " + ex);
		}
	}

	// The mesh descriptor (Blocks_MeshDescriptors/<Name>): the block's mesh per block it fills. The game draws placed
	// blocks and blueprint ghosts from it, one piece per block, with the block's position in the mesh's second UV
	// (the ghost shader needs it; a block without a descriptor is drawn as one mesh and its ghost stayed invisible).
	// Bricks reuse the game's brick's two pieces (with their hidden-face variants), slabs have one piece.
	private static void TryCreateDescriptor(Shape shape)
	{
		const string factoryName = "Blocks_MeshDescriptors";
		if (!Factory.Factories.TryGetValue(factoryName, out Factory factory) || factory == null)
		{
			return;
		}
		try
		{
			if (factory.cachedTemplates.Count == 0)
			{
				factory.ResetTemplateCache();
			}
			if (factory.cachedTemplates.ContainsKey(shape.Name) || !factory.cachedTemplates.ContainsKey(StoneBlockSource))
			{
				return;
			}
			if (shape.Wood)
			{
				SetUpWoodMaterial();
			}
			GameObject go = Object.Instantiate(factory.GetTemplate(StoneBlockSource).gameObject, factory.transform);
			go.name = shape.Name;
			VoxelizedMeshDescriptor descriptor = go.GetComponent<VoxelizedMeshDescriptor>();
			VoxelizedMesh source = descriptor.voxelMesh;
			descriptor.voxelMesh = VoxelMeshFor(shape, source);
			FactoryImprint imprint = go.GetComponent<FactoryImprint>();
			if (imprint != null)
			{
				imprint.AssetKey = new Factory.AssetKey(factory.name, shape.Name);
			}
			factory.AddAsset<CloneTemplate>(go);
			Plugin.Log.LogInfo("CustomBlocks: added descriptor " + shape.Name + " (brick pieces at " + Positions(source) + ", pivot " + source.pivot + ")");
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("CustomBlocks: could not add the mesh descriptor of " + shape.Name + ": " + ex);
		}
	}

	private static VoxelizedMesh VoxelMeshFor(Shape shape, VoxelizedMesh brick)
	{
		// The pivot is the model's middle seen from the root block: the brick's is half a block along z (its centre
		// offset is -0.5), plus whatever else its pivot holds; ours is the middle of our blocks.
		VoxelizedMesh mesh = new VoxelizedMesh
		{
			originalMesh = Meshes.TryGetValue(shape.Name, out Mesh whole) ? whole : null,
			pivot = -CenterOffset(shape) + (brick.pivot - new Vector3(0f, 0f, 0.5f))
		};
		List<VoxelizedMesh.VoxelData> voxels = new List<VoxelizedMesh.VoxelData>();
		Mesh slabPiece = shape.Wood ? SlabPiece() : null;
		for (int x = 0; x < shape.Width; x++)
		{
			for (int z = 0; z < shape.Depth; z++)
			{
				VoxelizedMesh.VoxelData data = new VoxelizedMesh.VoxelData { position = new XYZ(x, 0, z) };
				if (shape.Wood)
				{
					data.faces = new VoxelizedMesh.FaceData[0];
					for (int i = 0; i < data.partialMeshes.Length; i++)
					{
						data.partialMeshes[i] = slabPiece;
					}
				}
				else
				{
					// The brick's piece at its own back (z 0) or front (z 1) end.
					VoxelizedMesh.VoxelData piece = PieceAt(brick, z % 2);
					data.faces = piece.faces;
					data.partialMeshes = piece.partialMeshes;
				}
				voxels.Add(data);
			}
		}
		mesh.voxels = voxels.ToArray();
		return mesh;
	}

	private static VoxelizedMesh.VoxelData PieceAt(VoxelizedMesh brick, int z)
	{
		foreach (VoxelizedMesh.VoxelData data in brick.voxels)
		{
			if (data.position.z == z)
			{
				return data;
			}
		}
		return brick.voxels[Mathf.Min(z, brick.voxels.Length - 1)];
	}

	private static string Positions(VoxelizedMesh mesh)
	{
		List<string> parts = new List<string>();
		foreach (VoxelizedMesh.VoxelData data in mesh.voxels)
		{
			parts.Add(data.position.x + "," + data.position.y + "," + data.position.z);
		}
		return string.Join(" ", parts.ToArray());
	}

	// One block of a slab, round the block's middle, in the upper half.
	private static Mesh SlabPiece()
	{
		if (Meshes.TryGetValue("Wood_SlabPiece", out Mesh mesh))
		{
			return mesh;
		}
		mesh = SlabMesh(new Shape { Name = "Wood_SlabPiece", Wood = true, Width = 1, Depth = 1 }, 0f);
		mesh.name = "Wood_SlabPiece";
		Meshes["Wood_SlabPiece"] = mesh;
		return mesh;
	}

	// The free block: what the block is, how it looks and its health. The game's block info is made from it.
	private static void SetUpBlock(GameObject go, Shape shape)
	{
		BlockInfoMeta meta = go.GetComponent<BlockInfoMeta>();
		meta.ID = shape.Id;
		meta.BlueprintKey = new Factory.AssetKey("Blueprints", shape.Name);
		meta.IsPartialBloc = false;
		meta.MergeableBloc = false;
		if (shape.Wood)
		{
			meta.Groups = BlockGroups.Block | BlockGroups.Wood;
			meta.Ressource = ResourceType.WoodenBlock;
			meta.Surface = VoxelSurface.Wood;
			// The plank picks its mesh by its neighbours; the slab has one mesh.
			PlankVoxelDisplayDataGenerator plank = go.GetComponent<PlankVoxelDisplayDataGenerator>();
			if (plank != null)
			{
				Object.DestroyImmediate(plank);
			}
		}
		Mesh mesh = MeshFor(shape, go.GetComponent<MeshFilter>());
		go.GetComponent<MeshFilter>().sharedMesh = mesh;
		// The plank's prefab has no material (the game gives it one while playing); the slab then keeps the same.
		if (shape.Wood && _woodMaterial != null)
		{
			go.GetComponent<MeshRenderer>().sharedMaterials = new Material[1] { _woodMaterial };
		}
		BoxCollider collider = go.GetComponent<BoxCollider>();
		if (collider != null)
		{
			collider.center = mesh.bounds.center;
			collider.size = mesh.bounds.size;
		}
		BaseVoxelTransform voxelTransform = go.GetComponent<BaseVoxelTransform>();
		if (voxelTransform != null)
		{
			voxelTransform.InitialCenterOffset = CenterOffset(shape);
		}
		IDamageReceiver damage = go.GetComponent<IDamageReceiver>();
		if (damage != null)
		{
			damage.MaxHP = shape.Hp;
		}
		VolumeDataComponent volume = go.GetComponent<VolumeDataComponent>();
		if (volume != null)
		{
			Plugin.Log.LogInfo("CustomBlocks: " + shape.Name + " block source volume " + Cells(volume._indexedVolumetricProperties));
			long body = 0;
			foreach (KeyValuePair<XYZ, long> cell in volume._indexedVolumetricProperties)
			{
				if (cell.Key.Equals(XYZ.zero))
				{
					body = cell.Value;
				}
			}
			IndexedVolumetricProperties cells = new IndexedVolumetricProperties();
			for (int x = 0; x < shape.Width; x++)
			{
				for (int z = 0; z < shape.Depth; z++)
				{
					cells[new XYZ(x, 0, z)] = body;
				}
			}
			volume._indexedVolumetricProperties = cells;
		}
	}

	// The blueprint: its cells (body, support and where builders stand), ghost mesh, cost and support rule.
	private static void SetUpBlueprint(GameObject go, Shape shape)
	{
		Mesh mesh = MeshFor(shape, go.GetComponent<MeshFilter>());
		MeshFilter filter = go.GetComponent<MeshFilter>();
		if (filter != null)
		{
			filter.sharedMesh = mesh;
		}
		BlueprintRenderer renderer = go.GetComponent<BlueprintRenderer>();
		if (renderer != null)
		{
			renderer.initialMesh = mesh;
		}
		BoxCollider collider = go.GetComponent<BoxCollider>();
		if (collider != null)
		{
			collider.center = mesh.bounds.center;
			collider.size = mesh.bounds.size;
		}
		BaseVoxelTransform voxelTransform = go.GetComponent<BaseVoxelTransform>();
		if (voxelTransform != null)
		{
			voxelTransform.InitialCenterOffset = CenterOffset(shape);
		}
		// The brick's fitter puts a 1 x 2 block on the border between two blocks along its length; wider blocks
		// came out half a block off. Ours places the chosen corner on the block the mouse points at.
		AbstractFitter old = go.GetComponent<AbstractFitter>();
		CustomBlockFitter fitter = go.AddComponent<CustomBlockFitter>();
		if (old != null)
		{
			fitter.CloneInternals(old);
			Object.DestroyImmediate(old);
		}
		fitter._oneRotationPerAxis = false;
		Recepteur recepteur = go.GetComponent<Recepteur>();
		if (recepteur != null)
		{
			CustomBuilding.RemoveResources(recepteur._baseCapacity);
			Type resource = shape.Wood ? Adjectif.plankBlock.GetType() : Adjectif.stoneBlock.GetType();
			recepteur._baseCapacity.Add(Adjectif.New(resource, shape.Cost));
		}
		VolumeDataComponent volume = go.GetComponent<VolumeDataComponent>();
		if (volume != null)
		{
			Plugin.Log.LogInfo("CustomBlocks: " + shape.Name + " blueprint source volume " + Cells(volume._indexedVolumetricProperties));
			int supports;
			volume._indexedVolumetricProperties = BlueprintCells(shape, out supports);
			ValidatorComponent validator = go.GetComponent<ValidatorComponent>();
			if (validator != null)
			{
				// Bricks: like the game's brick, one support cell may be missing per two blocks. Slabs: one is enough.
				validator.MaxMissingSupport = shape.Wood ? supports - 1 : shape.Width * shape.Depth / 2;
			}
		}
	}

	private static IndexedVolumetricProperties BlueprintCells(Shape shape, out int supports)
	{
		const long access = (long)VPropertyMask.access;
		const long swap = (long)VPropertyMask.meshSwapTrigger;
		const long support = (long)VPropertyMask.mainSupportReq;
		IndexedVolumetricProperties cells = new IndexedVolumetricProperties();
		long body = (long)(VPropertyMask.fantome | (shape.Wood ? VPropertyMask.plankSupport : VPropertyMask.stoneSupport));
		supports = 0;
		for (int x = 0; x < shape.Width; x++)
		{
			for (int z = 0; z < shape.Depth; z++)
			{
				cells[new XYZ(x, 0, z)] = body;
				cells[new XYZ(x, -1, z)] = support;
				cells[new XYZ(x, 1, z)] = swap;
				supports++;
			}
		}
		// The cells round the body (not the corners), along the long sides first. At the body's height they swap
		// the neighbours' meshes, and a slab is also held by the blocks there.
		List<XYZ> ring = RingCells(shape);
		foreach (XYZ cell in ring)
		{
			long mask = swap;
			if (shape.Wood)
			{
				mask |= support;
				supports++;
			}
			cells[cell] = (cells.TryGetValue(cell, out long old) ? old : 0L) | mask;
		}
		// Where builders stand, as the game's brick has them: beside the body, one and two lower, and on the
		// blocks round it (one higher: a slab next to a wall is built from the top of the wall). The workers'
		// knowledge keeps at most 32 places per object, so the levels are filled in that order up to 32.
		int places = 0;
		foreach (int y in new int[4] { 0, -1, 1, -2 })
		{
			foreach (XYZ cell in ring)
			{
				if (places >= MaxAccessCells)
				{
					return cells;
				}
				XYZ position = new XYZ(cell.x, y, cell.z);
				cells[position] = (cells.TryGetValue(position, out long old) ? old : 0L) | access;
				places++;
			}
		}
		return cells;
	}

	// The workers' knowledge keeps at most this many places to stand per object.
	private const int MaxAccessCells = 32;

	// The cells next to the body at its height, without the corners: the long sides first, then the ends.
	private static List<XYZ> RingCells(Shape shape)
	{
		List<XYZ> sideX = new List<XYZ>();
		List<XYZ> sideZ = new List<XYZ>();
		for (int z = 0; z < shape.Depth; z++)
		{
			sideX.Add(new XYZ(-1, 0, z));
			sideX.Add(new XYZ(shape.Width, 0, z));
		}
		for (int x = 0; x < shape.Width; x++)
		{
			sideZ.Add(new XYZ(x, 0, -1));
			sideZ.Add(new XYZ(x, 0, shape.Depth));
		}
		List<XYZ> ring = new List<XYZ>();
		ring.AddRange((shape.Depth >= shape.Width) ? sideX : sideZ);
		ring.AddRange((shape.Depth >= shape.Width) ? sideZ : sideX);
		return ring;
	}

	// The cell of the block (from its root cell) at the chosen corner, for a block of the given name.
	internal static Vector3 AnchorCell(string name)
	{
		foreach (Shape shape in Shapes)
		{
			if (name.StartsWith(shape.Name))
			{
				int x = (Anchor == 1 || Anchor == 2) ? shape.Width - 1 : 0;
				int z = (Anchor == 2 || Anchor == 3) ? shape.Depth - 1 : 0;
				return new Vector3(x, 0f, z);
			}
		}
		return Vector3.zero;
	}

	// The model's middle is the middle of the block's cells; the root cell is (0, 0, 0).
	private static Vector3 CenterOffset(Shape shape)
	{
		return new Vector3(-(shape.Width - 1) / 2f, 0f, -(shape.Depth - 1) / 2f);
	}

	private static Mesh MeshFor(Shape shape, MeshFilter source)
	{
		if (Meshes.TryGetValue(shape.Name, out Mesh mesh))
		{
			return mesh;
		}
		mesh = shape.Wood ? SlabMesh(shape) : BrickMesh(shape, source != null ? source.sharedMesh : null);
		mesh.name = shape.Name;
		Meshes[shape.Name] = mesh;
		return mesh;
	}

	// The game's brick mesh (1 x 2 blocks, readable) laid side by side and end to end.
	private static Mesh BrickMesh(Shape shape, Mesh brick)
	{
		List<CombineInstance> parts = new List<CombineInstance>();
		for (int x = 0; x < shape.Width; x++)
		{
			for (int z = 0; z < shape.Depth; z += 2)
			{
				Vector3 position = new Vector3(x - (shape.Width - 1) / 2f, 0f, z + 0.5f - (shape.Depth - 1) / 2f);
				parts.Add(new CombineInstance { mesh = brick, transform = Matrix4x4.Translate(position) });
			}
		}
		Mesh mesh = new Mesh();
		mesh.CombineMeshes(parts.ToArray(), mergeSubMeshes: true, useMatrices: true);
		mesh.RecalculateBounds();
		return mesh;
	}

	// A box over the cells in the upper half of the block, with the planks texture's board strip on every face:
	// one board per block across, running along the long side.
	private static Mesh SlabMesh(Shape shape, float gap = 0.01f)
	{
		Vector3 size = new Vector3(shape.Width - gap, SlabHeight, shape.Depth - gap);
		Vector3 centre = new Vector3(0f, 0.5f - SlabHeight / 2f, 0f);
		List<Vector3> vertices = new List<Vector3>();
		List<Vector3> normals = new List<Vector3>();
		List<Vector2> uvs = new List<Vector2>();
		List<int> triangles = new List<int>();
		Vector3[] axes = new Vector3[3] { Vector3.right, Vector3.up, Vector3.forward };
		for (int a = 0; a < 3; a++)
		{
			for (int sign = -1; sign <= 1; sign += 2)
			{
				Vector3 normal = axes[a] * sign;
				Vector3 u = axes[(a + 1) % 3];
				Vector3 v = axes[(a + 2) % 3];
				float lengthU = Vector3.Scale(u, size).magnitude;
				float lengthV = Vector3.Scale(v, size).magnitude;
				// Boards run along the longer side of the face.
				bool swap = lengthV > lengthU;
				int first = vertices.Count;
				for (int corner = 0; corner < 4; corner++)
				{
					float cu = (corner == 1 || corner == 2) ? 0.5f : -0.5f;
					float cv = (corner >= 2) ? 0.5f : -0.5f;
					vertices.Add(centre + Vector3.Scale(normal * 0.5f + u * cu + v * cv, size));
					normals.Add(normal);
					float along = ((swap ? cv : cu) + 0.5f) * (swap ? lengthV : lengthU);
					float across = ((swap ? cu : cv) + 0.5f) * (swap ? lengthU : lengthV);
					uvs.Add(new Vector2(along * 0.25f, 0.92f + across * 0.04f));
				}
				Vector3 p0 = vertices[first], p1 = vertices[first + 1], p2 = vertices[first + 2];
				bool facing = Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), normal) > 0f;
				triangles.AddRange(facing ? new int[6] { first, first + 1, first + 2, first, first + 2, first + 3 } : new int[6] { first, first + 2, first + 1, first, first + 3, first + 2 });
			}
		}
		Mesh mesh = new Mesh();
		mesh.SetVertices(vertices);
		mesh.SetNormals(normals);
		mesh.SetUVs(0, uvs);
		mesh.SetTriangles(triangles, 0);
		List<Color> colors = new List<Color>();
		for (int i = 0; i < vertices.Count; i++)
		{
			colors.Add(Color.white);
		}
		mesh.SetColors(colors);
		mesh.RecalculateTangents();
		mesh.RecalculateBounds();
		return mesh;
	}

	// The material the game draws placed planks with: the plank's prefab has none of its own, its display data
	// generator takes it from a mesh selector asset. Wood blocks are made either way (without it they show magenta).
	private static void SetUpWoodMaterial()
	{
		if (_woodMaterial != null || _woodMaterialRead)
		{
			return;
		}
		if (!Factory.Factories.TryGetValue("Blocks", out Factory blocks) || blocks == null || !blocks.cachedTemplates.ContainsKey(WoodBlockSource))
		{
			return;
		}
		GameObject plank = blocks.GetTemplate(WoodBlockSource).gameObject;
		PlankVoxelDisplayDataGenerator plankGenerator = plank.GetComponent<PlankVoxelDisplayDataGenerator>();
		if (plankGenerator != null && !Factory.Factories.ContainsKey(plankGenerator._plankCulledAssetKey.Factory))
		{
			// Not loaded yet; asked again after the next factory.
			return;
		}
		try
		{
			PlankVoxelDisplayDataGenerator generator = plank.GetComponent<PlankVoxelDisplayDataGenerator>();
			GameObject selector = (generator != null) ? Factory.Peek(generator._plankCulledAssetKey) : null;
			DefaultBlockMeshSelector meshSelector = (selector != null) ? selector.GetComponent<DefaultBlockMeshSelector>() : null;
			if (meshSelector != null && meshSelector.Material != null && meshSelector.Material.Length > 0)
			{
				_woodMaterial = meshSelector.Material[0];
			}
		}
		catch (Exception)
		{
			// Its factory has not made the template yet; asked again after the next factory.
			return;
		}
		_woodMaterialRead = true;
		if (_woodMaterial == null)
		{
			MeshRenderer renderer = plank.GetComponent<MeshRenderer>();
			_woodMaterial = (renderer != null) ? renderer.sharedMaterial : null;
		}
		Plugin.Log.LogInfo("CustomBlocks: wood material " + ((_woodMaterial != null) ? (_woodMaterial.name + " (" + _woodMaterial.shader.name + ", texture " + ((_woodMaterial.mainTexture != null) ? _woodMaterial.mainTexture.name : "-") + ")") : "none"));
	}

	private static string Cells(Dictionary<XYZ, long> cells)
	{
		List<string> parts = new List<string>();
		foreach (KeyValuePair<XYZ, long> cell in cells)
		{
			parts.Add(cell.Key.x + "," + cell.Key.y + "," + cell.Key.z + "=" + (VPropertyMask)cell.Value);
		}
		return string.Join(" ", parts.ToArray());
	}
}

// Places one of the mod's blocks with the chosen corner (CustomBlocks.Anchor, changed with BlockAnchorKey) on the
// block the mouse points at, turned in all four directions.
internal class CustomBlockFitter : DefaultFitter
{
	private void Update()
	{
		if (CustomBlocks.AnchorKey != null && Input.GetKeyDown(CustomBlocks.AnchorKey.Value))
		{
			CustomBlocks.Anchor = (CustomBlocks.Anchor + 1) % 4;
		}
	}

	public override void FitAndOrient(VoxelRaycastHit voxelHit, out Vector3 absolutePosition)
	{
		Vector3 anchor = FindFitPosition(voxelHit);
		transform.rotation = _rotation;
		Vector3 root = anchor - Calcule.RoundVecteur(_rotation * CustomBlocks.AnchorCell(name));
		_voxelTransform.Get.PlacementOffset = Vector3.zero;
		transform.position = _voxelTransform.Get.GetOffsettedPosition(root, _rotation);
		absolutePosition = root;
	}

	public override IFitter CopyTo(GameObject go)
	{
		Object.Destroy((MonoBehaviour)go.GetInterface<IFitter>());
		CustomBlockFitter fitter = go.AddComponent<CustomBlockFitter>();
		fitter.CloneInternals(this);
		fitter._oneRotationPerAxis = false;
		return fitter;
	}
}

// The block infos are made once the factories are loaded; the wood blocks' material must be in place by then.
[Feature(Features.CustomBlocks, Features.CustomBlocksInfo)]
[HarmonyPatch(typeof(BlockInfoGenerator), nameof(BlockInfoGenerator.CreateMetaBlockInfos))]
internal static class CustomBlocksMaterialPatch
{
	private static void Prefix()
	{
		CustomBlocks.ApplyWoodMaterial();
	}

	private static void Postfix()
	{
		CustomBlocks.ApplyWoodMaterial();
	}
}

// Dragging a row of blocks: the game keeps every drag point that is not inside the block's size measured from the
// middle of the previous one, which for a block 4 long gives a step of 3, so the blocks overlapped by one. Ours
// step by the block's own length along the drag (rotation included): every block right after the previous one.
[Feature(Features.CustomBlocks, Features.CustomBlocksInfo)]
[HarmonyPatch(typeof(DefaultDragger), "FilterDragPoints")]
internal static class CustomBlocksDragPatch
{
	private static void Postfix(DefaultDragger __instance, List<Vector3> drags, ref List<Vector3> __result)
	{
		if (drags == null || drags.Count < 2 || __instance.GetComponent<CustomBlockFitter>() == null)
		{
			return;
		}
		Vector3 along = drags[drags.Count - 1] - drags[0];
		HashSet<XYZ> body = new HashSet<XYZ>(__instance.GetComponent<IVolume>().GetCases(VProperty.fantome));
		if (body.Count == 0)
		{
			return;
		}
		// The block's length along the drag: how many distinct cells it covers on that axis.
		HashSet<int> spread = new HashSet<int>();
		foreach (XYZ cell in body)
		{
			spread.Add(Mathf.Abs(along.x) > 0.5f ? cell.x : (Mathf.Abs(along.z) > 0.5f ? cell.z : cell.y));
		}
		int step = Mathf.Max(1, spread.Count);
		List<Vector3> result = new List<Vector3>();
		for (int i = 0; i < drags.Count; i += step)
		{
			// The first point is the held block itself.
			if (!body.Contains(XYZ.FromVector3(drags[i])))
			{
				result.Add(drags[i]);
			}
		}
		__result = result;
	}
}
