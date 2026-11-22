using System;
using System.Collections;
using System.Collections.Generic;
using Brix.Engine;
using Brix.Engine.FlatWorld;
using Brix.Engine.FlatWorld.Layers;
using Brix.Engine.FlatWorld.SoftAlgos;
using Brix.External.Signals;
using Brix.Game;
using Brix.Input;
using CastleStoryPlus.Loading;
using CastleStoryPlus.Metallurgy;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CastleStoryPlus.Diagnostics;

// Debug menu "X-ray": hides the game's terrain and draws blocks of our own around the point the camera looks at
// (Radius columns each way), so bricktrons, buildings and what is under the ground can be seen.
// - Semi (caves, like a Minecraft cave x-ray): only the faces of the ground that face an empty space under the ground
//   (mines, tunnels, quarries, natural caves), in the block's colour and facing into it, so from outside the far walls
//   and floors of the shafts are seen from inside, with the ore in them. The ground's outside (its top and cliffs)
//   is drawn see-through on top, so the surface is still there to find one's way.
// - Full: only the ore (orange crystal, blue crystal, iron, coal; resources packed into the ground count as their ore).
// The ground is read in chunks of Chunk x Chunk columns, a little each frame, nearest first, and kept while the camera
// stays near; the game's voxel changes (digging) update the chunk they are in. Only this machine's view changes.
internal class XRayView : MonoBehaviour
{
	internal enum Mode
	{
		Off,
		Semi,
		Full
	}

	internal const int Radius = 48;

	private const int Chunk = 16;

	// Chunks this many chunk widths beyond the radius are forgotten.
	private const int KeepMargin = 2;

	private const long ScanBudgetMs = 3;

	private const int MaxVertices = 60000;

	// Pixels per tile of our terrain atlas.
	private const int Tile = 64;

	// Grid values: empty outside the ground, empty under the ground's top (dug out or a cave), and terrain types (their
	// class through Classes). Block classes group the types for counting, the ore and the plain-colour fallback.
	private const byte Outside = 0;

	private const byte Hollow = 255;

	// Where no piece has been read yet (only from At).
	private const byte Unread = 254;

	// Ground the game has not changed, whose type is looked up only when needed (Semi).
	private const byte Unknown = 253;

	// How much of the surface shows in Semi.
	private const float SurfaceAlpha = 0.15f;

	private const byte Orange = 1;

	private const byte Blue = 2;

	private const byte Iron = 3;

	private const byte Coal = 4;

	private const byte Stone = 5;

	private const byte Earth = 6;

	private const byte Grass = 7;

	private const byte Sand = 8;

	private const byte Snow = 9;

	private const int ClassCount = 10;

	internal static readonly Color OrangeColor = new Color(1f, 0.55f, 0.1f, 1f);

	internal static readonly Color BlueColor = new Color(0.25f, 0.6f, 1f, 1f);

	internal static readonly Color IronColor = new Color(0.8f, 0.45f, 0.35f, 1f);

	internal static readonly Color CoalColor = new Color(0.15f, 0.15f, 0.17f, 1f);

	private static readonly Color[] ClassColors =
	{
		Color.white, OrangeColor, BlueColor, IronColor, CoalColor,
		new Color(0.55f, 0.56f, 0.58f, 1f), new Color(0.5f, 0.38f, 0.26f, 1f), new Color(0.38f, 0.6f, 0.3f, 1f),
		new Color(0.85f, 0.78f, 0.55f, 1f), new Color(0.92f, 0.94f, 0.97f, 1f)
	};

	// The terrain type whose look each class takes.
	private static readonly int[] TypeOfClass =
	{
#if !ESSENTIALS
		(int)VoxelTerrainType.Stone, (int)VoxelTerrainType.OrangeCrystal, (int)VoxelTerrainType.BlueCrystal, (int)VoxelTerrainType.Iron,
		CoalVeins.CoalType, (int)VoxelTerrainType.Stone, (int)VoxelTerrainType.Earth, (int)VoxelTerrainType.Grass,
#else
		(int)VoxelTerrainType.Stone, (int)VoxelTerrainType.OrangeCrystal, (int)VoxelTerrainType.BlueCrystal, (int)VoxelTerrainType.Iron,
		(int)VoxelTerrainType.Stone, (int)VoxelTerrainType.Stone, (int)VoxelTerrainType.Earth, (int)VoxelTerrainType.Grass,
#endif
		(int)VoxelTerrainType.Sand, (int)VoxelTerrainType.Snow
	};

	// Block class by terrain type; types not listed count as stone.
	private static readonly byte[] Classes = new byte[256];

	private static readonly XYZ[] Normals = { new XYZ(1, 0, 0), new XYZ(-1, 0, 0), new XYZ(0, 1, 0), new XYZ(0, -1, 0), new XYZ(0, 0, 1), new XYZ(0, 0, -1) };

	internal static Mode Current = Mode.Off;

	internal static string Summary = string.Empty;

	private static XRayView _instance;

	private static Material[] _materials;

	private static Material[] _surfaceMaterials;

	private sealed class Piece
	{
		public int X;

		public int Z;

		public int Y0;

		public int SY;

		public byte[] Grid;

		public bool Scanned;

		// Every voxel's type read (else the unchanged ones are Unknown until needed).
		public bool Complete;

		public bool MeshDirty = true;

		public int[] Counts = new int[ClassCount];

		public int Hollows;

		public readonly List<Mesh>[] Meshes = new List<Mesh>[ClassCount];

		// Semi: the see-through surface.
		public readonly List<Mesh>[] Surface = new List<Mesh>[ClassCount];
	}

	private struct Face
	{
		public Vector3 Center;

		public Vector3 Normal;

		public byte Type;
	}

	private readonly Dictionary<long, Piece> _pieces = new Dictionary<long, Piece>();

	// Terrain renderers hidden by the mode.
	private readonly List<MeshRenderer> _hidden = new List<MeshRenderer>();

	private readonly List<MeshRenderer> _renderers = new List<MeshRenderer>();

	// Reused while building a piece's meshes.
	private readonly List<Vector3> _vertices = new List<Vector3>();

	private readonly List<Vector3> _normals = new List<Vector3>();

	private readonly List<Vector2> _uvs = new List<Vector2>();

	private readonly List<Vector2> _uvs2 = new List<Vector2>();

	private readonly List<Color> _colors = new List<Color>();

	// The game's own terrain look: a hidden terrain renderer's material, the terrain atlas and noise, and the corner
	// offsets of its texture coordinates (see AddGameUvs). Null when not found: our atlas instead.
	private Material _terrainMaterial;

	private MondeAtlas _gameAtlas;

	private Color32[] _noise;

	private int _noiseWidth;

	private int _noiseHeight;

	private float _uvInner;

	private float _uvOuter;

	private readonly List<int> _triangles = new List<int>();

	// Faces found while building a piece, by class.
	private readonly List<Face>[] _faces = new List<Face>[ClassCount];

	private readonly List<Face>[] _surfaceFaces = new List<Face>[ClassCount];

	private TestWorldConfig3 _world;

	private SignalConnection _voxelChanged = SignalConnection.Empty;

	private bool _connected;

	private float _nextHide;

	private float _nextSummary;

	private int _workSteps;

	private XYZ _focus;

	static XRayView()
	{
		for (int i = 0; i < Classes.Length; i++)
		{
			Classes[i] = Stone;
		}
		Classes[(int)VoxelTerrainType.OrangeCrystal] = Orange;
		Classes[(int)VoxelTerrainType.OrangeCrystalCrust] = Orange;
		Classes[(int)VoxelTerrainType.BlueCrystal] = Blue;
		Classes[(int)VoxelTerrainType.BlueCrystalCrust] = Blue;
		Classes[(int)VoxelTerrainType.Iron] = Iron;
		Classes[(int)VoxelTerrainType.IronCrust] = Iron;
#if !ESSENTIALS
		Classes[CoalVeins.CoalType] = Coal;
#endif
		Classes[(int)VoxelTerrainType.Earth] = Earth;
		Classes[(int)VoxelTerrainType.Grass] = Grass;
		Classes[(int)VoxelTerrainType.ThickGrass] = Grass;
		Classes[(int)VoxelTerrainType.Clover] = Grass;
		Classes[(int)VoxelTerrainType.Sand] = Sand;
		Classes[(int)VoxelTerrainType.Snow] = Snow;
	}

	internal static void Set(Mode mode)
	{
		if (_instance == null)
		{
			_instance = Plugin.Root.AddComponent<XRayView>();
		}
		_instance.StopAllCoroutines();
		_instance.RestoreTerrain();
		Current = mode;
		Summary = string.Empty;
		// The ground read so far is kept; only the meshes depend on the mode. Full needs every voxel's type.
		foreach (Piece piece in _instance._pieces.Values)
		{
			ClearMeshes(piece);
			piece.MeshDirty = true;
			if (mode == Mode.Full && !piece.Complete)
			{
				piece.Scanned = false;
			}
		}
		if (mode == Mode.Off)
		{
			_instance.Forget();
			return;
		}
		_instance._nextHide = 0f;
		_instance.StartCoroutine(_instance.Work());
	}

	// Where a ray that hit the ground at hit first enters a space dug out under the ground (within MaxDepth blocks),
	// for pointing into the tunnels the view shows; false when the view is off or the ray meets none.
	internal static bool FindHollow(Vector3 hit, Vector3 direction, out Vector3 inside)
	{
		inside = hit;
		if (_instance == null || Current == Mode.Off)
		{
			return false;
		}
		const float MaxDepth = 64f;
		const float Step = 0.2f;
		direction.Normalize();
		for (float t = 0f; t <= MaxDepth; t += Step)
		{
			Vector3 point = hit + direction * t;
			byte c = _instance.At(null, Mathf.RoundToInt(point.x), Mathf.RoundToInt(point.y), Mathf.RoundToInt(point.z), false);
			if (c == Hollow)
			{
				inside = point;
				return true;
			}
			if (c == Unread)
			{
				return false;
			}
		}
		return false;
	}

	// For the DevTools API: what the view is doing.
	internal static string State()
	{
		if (_instance == null)
		{
			return "not started";
		}
		int scanned = 0;
		int meshes = 0;
		int filled = 0;
		foreach (Piece piece in _instance._pieces.Values)
		{
			if (piece.Scanned)
			{
				scanned++;
			}
			if (piece.SY > 0)
			{
				filled++;
			}
			for (int c = 1; c < ClassCount; c++)
			{
				meshes += (piece.Meshes[c] != null) ? piece.Meshes[c].Count : 0;
			}
		}
		LayerVoxelEngine engine = Voxel.Engine as LayerVoxelEngine;
		Transform root = (engine != null && engine.test != null) ? engine.test.renderTarget : null;
		return "focus " + _instance._focus + ", work steps " + _instance._workSteps + ", pieces " + _instance._pieces.Count + " (" + scanned + " read, " + filled + " with ground), meshes " + meshes
			+ ", hidden renderers " + _instance._hidden.Count + ", render target " + ((root != null) ? root.name + " (" + root.childCount + " children)" : "none") + "";
	}

	private void Awake()
	{
		for (int c = 0; c < ClassCount; c++)
		{
			_faces[c] = new List<Face>();
			_surfaceFaces[c] = new List<Face>();
		}
	}

	private void Update()
	{
		if (Current == Mode.Off)
		{
			return;
		}
		LayerVoxelEngine engine = Voxel.Engine as LayerVoxelEngine;
		if (engine == null || engine.test == null || engine.test.renderTarget == null)
		{
			return;
		}
		// New terrain pieces appear as the camera moves and the terrain changes.
		if (Time.unscaledTime >= _nextHide)
		{
			_nextHide = Time.unscaledTime + 1f;
			HideTerrain(engine.test.renderTarget);
		}
		EnsureMaterials(engine);
		foreach (Piece piece in _pieces.Values)
		{
			Draw(piece.Meshes, _materials, _terrainMaterial);
			Draw(piece.Surface, _surfaceMaterials);
		}
	}

	private static void Draw(List<Mesh>[] meshes, Material[] materials, Material all = null)
	{
		for (int c = 1; c < ClassCount; c++)
		{
			List<Mesh> list = meshes[c];
			if (list == null)
			{
				continue;
			}
			for (int i = 0; i < list.Count; i++)
			{
				Graphics.DrawMesh(list[i], Matrix4x4.identity, (all != null) ? all : materials[c], 0);
			}
		}
	}

	private void HideTerrain(Transform root)
	{
		root.GetComponentsInChildren(true, _renderers);
		for (int i = 0; i < _renderers.Count; i++)
		{
			MeshRenderer renderer = _renderers[i];
			if (_terrainMaterial == null && renderer.sharedMaterial != null && renderer.transform.parent != null && renderer.transform.parent.name == "Lod 0")
			{
				UseGameLook(renderer.sharedMaterial);
			}
			if (renderer.enabled)
			{
				renderer.enabled = false;
				_hidden.Add(renderer);
			}
		}
		_renderers.Clear();
	}

	// Draws the ground with the terrain's own material (the nearest level of detail's) from now on.
	private void UseGameLook(Material material)
	{
		try
		{
			LayerVoxelEngine engine = Voxel.Engine as LayerVoxelEngine;
			Common common = (engine != null && engine.test != null) ? engine.test.common : null;
			MondeAtlas atlas = MondeAtlas.GetAtlas("Terrain_LOD0");
			if (common == null || atlas == null)
			{
				return;
			}
			MondeAtlas padding = common.atlas ?? atlas;
			_uvInner = (float)padding.uvInnerDecal / (padding.uvAtlasWidth / 16);
			_uvOuter = (float)padding.uvOuterDecal / (padding.uvAtlasWidth / 4);
			TexColors noise = common.GetProperlySizedNoiseTexColors(1);
			_noise = (noise != null && noise.Width > 0) ? noise.GetPixels32() : null;
			_noiseWidth = (_noise != null) ? noise.Width : 0;
			_noiseHeight = (_noise != null) ? noise.Height : 0;
			_gameAtlas = atlas;
			_terrainMaterial = material;
			Plugin.Log.LogInfo("XRay: drawing with the terrain's material " + material.name + " (" + material.shader.name + "), noise " + _noiseWidth + " x " + _noiseHeight);
			foreach (Piece piece in _pieces.Values)
			{
				piece.MeshDirty = true;
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("XRay: not drawing with the terrain's material: " + ex.Message);
			_terrainMaterial = null;
		}
	}

	private void RestoreTerrain()
	{
		foreach (MeshRenderer renderer in _hidden)
		{
			if (renderer != null)
			{
				renderer.enabled = true;
			}
		}
		_hidden.Clear();
	}

	private static void EnsureMaterials(LayerVoxelEngine engine)
	{
		if (_materials != null)
		{
			return;
		}
		Shader lit = Shader.Find("Diffuse") ?? Shader.Find("Standard");
		// Lit and see-through if the game has it, else unlit (shaded by the vertex colours).
		Shader seeThrough = Shader.Find("Transparent/Diffuse") ?? Shader.Find("Legacy Shaders/Transparent/Diffuse") ?? Shader.Find("Sprites/Default");
		Texture2D atlas = null;
		try
		{
			atlas = BuildAtlas(engine);
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("XRay: no terrain textures, plain colours instead: " + ex.Message);
		}
		_materials = new Material[ClassCount];
		_surfaceMaterials = new Material[ClassCount];
		for (int c = 1; c < ClassCount; c++)
		{
			_materials[c] = (atlas != null)
				? new Material(lit) { mainTexture = atlas, color = Color.white, name = "XRay block " + c }
				: new Material(lit) { color = ClassColors[c], name = "XRay block " + c };
			Color tint = (atlas != null) ? Color.white : ClassColors[c];
			tint.a = SurfaceAlpha;
			_surfaceMaterials[c] = new Material(seeThrough) { mainTexture = atlas, color = tint, name = "XRay surface " + c };
		}
	}

	// The terrain's own look of each class as a tile of our atlas (Tile pixels, 4 x 4 tiles): the block's cell of the
	// terrain texture (_MainTex, 4 x 4 cells), with its special overlay (_TransTex: the specks of ore) on top.
	private static Texture2D BuildAtlas(LayerVoxelEngine engine)
	{
		Material terrain = engine.RenderMaterial;
		MondeAtlas mapping = engine.test.GetAtlas();
		if (terrain == null || mapping == null || !terrain.HasProperty("_MainTex"))
		{
			return null;
		}
		Texture2D baseTex = Readable(terrain.GetTexture("_MainTex"));
		Texture2D overlayTex = terrain.HasProperty("_TransTex") ? Readable(terrain.GetTexture("_TransTex")) : null;
		if (baseTex == null)
		{
			return null;
		}
		Texture2D atlas = new Texture2D(Tile * 4, Tile * 4, TextureFormat.RGBA32, true);
		Color32[] pixels = new Color32[atlas.width * atlas.height];
		for (int c = 1; c < ClassCount; c++)
		{
			int type = TypeOfClass[c];
			Vector2 uv = mapping.GetUv(type);
			bool special = overlayTex != null && mapping.HasUvSpecial(type);
			Vector2 uvSpecial = special ? mapping.GetUvSpecial(type) : Vector2.zero;
			int tx = (c % 4) * Tile;
			int ty = (c / 4) * Tile;
			for (int y = 0; y < Tile; y++)
			{
				for (int x = 0; x < Tile; x++)
				{
					float u = (x + 0.5f) / Tile;
					float v = (y + 0.5f) / Tile;
					Color color = baseTex.GetPixelBilinear((uv.x + u) / 4f, (3f - uv.y + v) / 4f);
					if (special)
					{
						Color speck = overlayTex.GetPixelBilinear((uvSpecial.x + u) / 4f, (3f - uvSpecial.y + v) / 4f);
						color = Color.Lerp(color, speck, speck.a);
					}
					color.a = 1f;
					pixels[(ty + y) * atlas.width + tx + x] = color;
				}
			}
		}
		atlas.SetPixels32(pixels);
		atlas.wrapMode = TextureWrapMode.Clamp;
		atlas.filterMode = FilterMode.Bilinear;
		atlas.Apply(true, true);
		atlas.name = "XRay terrain tiles";
		Object.Destroy(baseTex);
		if (overlayTex != null)
		{
			Object.Destroy(overlayTex);
		}
		return atlas;
	}

	// A readable copy of a (compressed) texture, through a render texture.
	private static Texture2D Readable(Texture original)
	{
		if (original == null)
		{
			return null;
		}
		RenderTexture target = RenderTexture.GetTemporary(original.width, original.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
		RenderTexture previous = RenderTexture.active;
		Texture2D copy = new Texture2D(original.width, original.height, TextureFormat.RGBA32, false);
		try
		{
			Graphics.Blit(original, target);
			RenderTexture.active = target;
			copy.ReadPixels(new Rect(0f, 0f, original.width, original.height), 0, 0, false);
			copy.Apply(false);
		}
		finally
		{
			RenderTexture.active = previous;
			RenderTexture.ReleaseTemporary(target);
		}
		return copy;
	}

	// The corners of a class's tile in our atlas, a little inside so neighbouring tiles do not bleed in.
	private static Vector4 TileRect(int c)
	{
		float size = Tile * 4f;
		float inset = 1.5f / size;
		float u0 = (c % 4) * Tile / size;
		float v0 = (c / 4) * Tile / size;
		return new Vector4(u0 + inset, v0 + inset, u0 + Tile / size - inset, v0 + Tile / size - inset);
	}

	// Where the camera looks: the terrain's colliders under the middle of the screen (they stay when the terrain is
	// hidden), else a point ahead of the camera. CameraScript.Target and Camera.main do not follow the player's camera.
	private static Vector3 Focus()
	{
		CameraScript camera = CameraScript.Instance;
		if (camera == null)
		{
			return Vector3.zero;
		}
		Ray ray = new Ray(camera.transform.position, camera.transform.forward);
		if (Physics.Raycast(ray, out RaycastHit hit, 600f, 1 << (int)UnityLayer.Terrain))
		{
			return hit.point;
		}
		return ray.origin + ray.direction * 60f;
	}

	private static bool IsOre(byte c)
	{
		return c >= Orange && c <= Coal;
	}

	private static bool IsSolid(byte c)
	{
		return c != Outside && c != Hollow && c != Unread && c != Unknown;
	}

	private static long Key(int px, int pz)
	{
		return ((long)px << 32) | (uint)pz;
	}

	private void Forget()
	{
		foreach (Piece piece in _pieces.Values)
		{
			ClearMeshes(piece);
		}
		_pieces.Clear();
		_world = null;
		_terrainMaterial = null;
		_gameAtlas = null;
		_noise = null;
		if (_connected)
		{
			try
			{
				_voxelChanged.Disconnect();
			}
			catch (Exception)
			{
				// Already gone with the scene's signals.
			}
			_voxelChanged = SignalConnection.Empty;
			_connected = false;
		}
	}

	// Reads the nearest piece not yet read, else builds the nearest piece whose meshes are out of date, else waits.
	private IEnumerator Work()
	{
		while (Current != Mode.Off)
		{
			LayerVoxelEngine engine = Voxel.Engine as LayerVoxelEngine;
			HeightmapLayer heightmap = (engine != null && engine.test != null && engine.test.layers != null) ? engine.test.layers.GetLayer<HeightmapLayer>() : null;
			if (heightmap == null || heightmap.fillingTex.Width == 0)
			{
				yield return null;
				continue;
			}
			if (_world != engine.test)
			{
				// Another world: start over, and follow its voxel changes.
				Forget();
				_world = engine.test;
				_voxelChanged = GameSignals.Connect(GameSignals.VoxelChanged, (Action<XYZ>)OnVoxelChanged);
				_connected = true;
			}
			XYZ focus = XYZ.FromVector3(Focus());
			_focus = focus;
			_workSteps++;
			int fx = Mathf.FloorToInt((float)focus.x / Chunk);
			int fz = Mathf.FloorToInt((float)focus.z / Chunk);
			int reach = (Radius + Chunk - 1) / Chunk;
			Evict(fx, fz, reach + KeepMargin);
			Piece next = null;
			int best = int.MaxValue;
			bool build = false;
			for (int dz = -reach; dz <= reach; dz++)
			{
				for (int dx = -reach; dx <= reach; dx++)
				{
					int distance = dx * dx + dz * dz;
					if (distance > reach * reach + 1 || distance >= best)
					{
						continue;
					}
					_pieces.TryGetValue(Key(fx + dx, fz + dz), out Piece piece);
					if (piece == null || !piece.Scanned)
					{
						next = piece ?? new Piece { X = fx + dx, Z = fz + dz };
						best = distance;
						build = false;
					}
					else if (piece.MeshDirty && (next == null || build))
					{
						next = piece;
						best = distance;
						build = true;
					}
				}
			}
			if (Time.unscaledTime >= _nextSummary)
			{
				_nextSummary = Time.unscaledTime + 1f;
				UpdateSummary(fx, fz, reach);
			}
			if (next == null)
			{
				yield return null;
				continue;
			}
			if (build)
			{
				BuildMeshes(next);
				yield return null;
				continue;
			}
			_pieces[Key(next.X, next.Z)] = next;
			IEnumerator scan = Scan(engine, heightmap, next);
			while (scan.MoveNext())
			{
				yield return scan.Current;
				if (Current == Mode.Off || _world != engine.test)
				{
					break;
				}
			}
		}
	}

	// Reads a piece. Within the columns the map made, the ground is solid but where it was dug out (the negative
	// layer's removed set) or is being changed this frame (p2i). Only Full reads every voxel's type through the map's
	// layers, which is slow; Semi leaves the others Unknown and looks up only those it draws.
	private IEnumerator Scan(LayerVoxelEngine engine, HeightmapLayer heightmap, Piece piece)
	{
		Color32[] columns = heightmap.fillingTex.GetPixels32();
		int width = heightmap.fillingTex.Width;
		int height = heightmap.fillingTex.Height;
		int x0 = piece.X * Chunk;
		int z0 = piece.Z * Chunk;
		int y0 = 255;
		int y1 = -1;
		for (int z = z0; z < z0 + Chunk; z++)
		{
			for (int x = x0; x < x0 + Chunk; x++)
			{
				if (x < 0 || z < 0 || x >= width || z >= height)
				{
					continue;
				}
				Color32 column = columns[x + z * width];
				if (column.g > column.r)
				{
					y0 = Mathf.Min(y0, column.r);
					y1 = Mathf.Max(y1, column.g - 1);
				}
			}
		}
		piece.Y0 = (y1 < y0) ? 0 : y0;
		piece.SY = (y1 < y0) ? 0 : y1 - y0 + 1;
		piece.Grid = new byte[Chunk * Chunk * piece.SY];
		piece.Complete = Current == Mode.Full;
		Dictionary<XYZ, int> changed = engine.p2i;
		List<int> types = engine.types;
		NegativeLayer negative = engine.test.layers.GetLayer<NegativeLayer>();
		var handle = engine.GetSpecializedVoxelHandle(new XYZ(x0, y0, z0));
		FrameBudget budget = new FrameBudget(ScanBudgetMs);
		for (int z = z0; z < z0 + Chunk && piece.SY > 0; z++)
		{
			for (int x = x0; x < x0 + Chunk; x++)
			{
				if (x < 0 || z < 0 || x >= width || z >= height)
				{
					continue;
				}
				Color32 column = columns[x + z * width];
				// Within the column the map made, an empty voxel was dug out (or is a cave).
				for (int y = column.r; y < column.g; y++)
				{
					XYZ p = new XYZ(x, y, z);
					byte c;
					if (changed.TryGetValue(p, out int at))
					{
						c = CellOf(types[at]);
					}
					else if (negative != null && negative.removedSet.Contains(p))
					{
						c = Hollow;
					}
					else if (piece.Complete)
					{
						engine.AssignVoxelHandle(handle, p);
						c = CellOf(engine.GetVoxelType(handle));
					}
					else
					{
						c = Unknown;
					}
					piece.Grid[Index(piece, x - x0, y - piece.Y0, z - z0)] = c;
					Count(piece, c, 1);
				}
				if (budget.Exceeded)
				{
					yield return null;
					budget.Reset();
				}
			}
		}
		piece.Scanned = true;
		Dirty(piece.X, piece.Z);
		Dirty(piece.X - 1, piece.Z);
		Dirty(piece.X + 1, piece.Z);
		Dirty(piece.X, piece.Z - 1);
		Dirty(piece.X, piece.Z + 1);
	}

	// A voxel type as a grid value.
	private static byte CellOf(int type)
	{
		if (type <= 0)
		{
			return Hollow;
		}
		return (type >= Unknown) ? (byte)VoxelTerrainType.Stone : (byte)type;
	}

	// The class of a grid value (the empty and unknown values stay as they are).
	private static byte ClassOfCell(byte cell)
	{
		return (cell == Outside || cell >= Unknown) ? cell : Classes[cell];
	}

	private static void Count(Piece piece, byte cell, int amount)
	{
		if (cell == Hollow)
		{
			piece.Hollows += amount;
		}
		else if (cell != Outside && cell < Unknown)
		{
			piece.Counts[Classes[cell]] += amount;
		}
	}

	private static int Index(Piece piece, int lx, int ly, int lz)
	{
		return lx + Chunk * (ly + piece.SY * lz);
	}

	private void Dirty(int px, int pz)
	{
		if (_pieces.TryGetValue(Key(px, pz), out Piece piece))
		{
			piece.MeshDirty = true;
		}
	}

	// A voxel the game changed (dug out, filled): read again, and rebuild its piece (and the piece next to it when
	// it is on the edge).
	private void OnVoxelChanged(XYZ p)
	{
		if (Current == Mode.Off || !(Voxel.Engine is LayerVoxelEngine engine))
		{
			return;
		}
		int px = Mathf.FloorToInt((float)p.x / Chunk);
		int pz = Mathf.FloorToInt((float)p.z / Chunk);
		if (!_pieces.TryGetValue(Key(px, pz), out Piece piece) || !piece.Scanned || p.y < piece.Y0 || p.y >= piece.Y0 + piece.SY)
		{
			return;
		}
		int i = Index(piece, p.x - px * Chunk, p.y - piece.Y0, p.z - pz * Chunk);
		byte old = piece.Grid[i];
		if (old == Outside)
		{
			return;
		}
		byte c = CellOf(engine.GetVoxelType(engine.GetSpecializedVoxelHandle(p)));
		if (c == old)
		{
			return;
		}
		Count(piece, old, -1);
		Count(piece, c, 1);
		piece.Grid[i] = c;
		piece.MeshDirty = true;
		int lx = p.x - px * Chunk;
		int lz = p.z - pz * Chunk;
		if (lx == 0)
		{
			Dirty(px - 1, pz);
		}
		if (lx == Chunk - 1)
		{
			Dirty(px + 1, pz);
		}
		if (lz == 0)
		{
			Dirty(px, pz - 1);
		}
		if (lz == Chunk - 1)
		{
			Dirty(px, pz + 1);
		}
	}

	private void Evict(int fx, int fz, int keep)
	{
		List<long> far = null;
		foreach (KeyValuePair<long, Piece> pair in _pieces)
		{
			if (Mathf.Abs(pair.Value.X - fx) > keep || Mathf.Abs(pair.Value.Z - fz) > keep)
			{
				(far ?? (far = new List<long>())).Add(pair.Key);
			}
		}
		if (far == null)
		{
			return;
		}
		foreach (long key in far)
		{
			ClearMeshes(_pieces[key]);
			_pieces.Remove(key);
		}
	}

	private void UpdateSummary(int fx, int fz, int reach)
	{
		int[] counts = new int[ClassCount];
		int hollows = 0;
		int read = 0;
		foreach (Piece piece in _pieces.Values)
		{
			if (!piece.Scanned || Mathf.Abs(piece.X - fx) > reach || Mathf.Abs(piece.Z - fz) > reach)
			{
				continue;
			}
			read++;
			hollows += piece.Hollows;
			for (int c = 1; c < ClassCount; c++)
			{
				counts[c] += piece.Counts[c];
			}
		}
		Summary = read + " chunks read; " + hollows + " empty under the ground; ore: orange " + counts[Orange] + ", blue " + counts[Blue] + ", iron " + counts[Iron] + ", coal " + counts[Coal];
	}

	// The class at a world position, from the pieces read so far (Unread where nothing was read); home is the piece
	// most lookups fall in. With resolve, an Unknown voxel's type is looked up and kept.
	private byte At(Piece home, int x, int y, int z, bool resolve)
	{
		Piece piece = home;
		int lx = (home != null) ? x - home.X * Chunk : -1;
		int lz = (home != null) ? z - home.Z * Chunk : -1;
		if (lx < 0 || lz < 0 || lx >= Chunk || lz >= Chunk)
		{
			int px = (x >= 0) ? x / Chunk : (x - Chunk + 1) / Chunk;
			int pz = (z >= 0) ? z / Chunk : (z - Chunk + 1) / Chunk;
			if (!_pieces.TryGetValue(Key(px, pz), out piece) || !piece.Scanned)
			{
				return Unread;
			}
			lx = x - px * Chunk;
			lz = z - pz * Chunk;
		}
		int ly = y - piece.Y0;
		if (ly < 0 || ly >= piece.SY)
		{
			return Outside;
		}
		int i = Index(piece, lx, ly, lz);
		byte c = piece.Grid[i];
		if (c == Unknown && resolve && Voxel.Engine is LayerVoxelEngine engine)
		{
			c = CellOf(engine.GetVoxelType(engine.GetSpecializedVoxelHandle(new XYZ(x, y, z))));
			piece.Grid[i] = c;
			Count(piece, c, 1);
		}
		return c;
	}

	private void BuildMeshes(Piece piece)
	{
		ClearMeshes(piece);
		piece.MeshDirty = false;
		if (piece.SY == 0)
		{
			return;
		}
		if (Current == Mode.Semi)
		{
			FindCaveAndSurfaceFaces(piece);
		}
		else
		{
			FindOreFaces(piece);
		}
		Emit(_faces, piece.Meshes, _terrainMaterial != null);
		Emit(_surfaceFaces, piece.Surface, false);
	}

	// Semi, as a Minecraft cave x-ray: the faces of the ground that face an empty space under the ground, facing into
	// it. Seen from outside, the near wall of a tunnel shows its back (not drawn), so the far wall and the floor are
	// seen from inside. A cave face belongs to the piece holding the empty voxel; its colour is the block's.
	// The faces towards the open air (the surface) go to the see-through surface. The edge of the pieces read so far
	// gets no wall.
	private void FindCaveAndSurfaceFaces(Piece piece)
	{
		int x0 = piece.X * Chunk;
		int z0 = piece.Z * Chunk;
		for (int lz = 0; lz < Chunk; lz++)
		{
			for (int ly = 0; ly < piece.SY; ly++)
			{
				for (int lx = 0; lx < Chunk; lx++)
				{
					byte cell = piece.Grid[Index(piece, lx, ly, lz)];
					if (cell == Outside)
					{
						continue;
					}
					int x = x0 + lx;
					int y = piece.Y0 + ly;
					int z = z0 + lz;
					for (int f = 0; f < 6; f++)
					{
						XYZ n = Normals[f];
						if (cell == Hollow)
						{
							byte wall = At(piece, x + n.x, y + n.y, z + n.z, true);
							if (IsSolid(wall))
							{
								_faces[Classes[wall]].Add(new Face { Center = new Vector3(x + n.x, y + n.y, z + n.z), Normal = new Vector3(-n.x, -n.y, -n.z), Type = wall });
							}
						}
						else if (At(piece, x + n.x, y + n.y, z + n.z, false) == Outside)
						{
							byte self = At(piece, x, y, z, true);
							if (IsSolid(self))
							{
								_surfaceFaces[Classes[self]].Add(new Face { Center = new Vector3(x, y, z), Normal = new Vector3(n.x, n.y, n.z), Type = self });
							}
						}
					}
				}
			}
		}
	}

	// Full: the ore blocks, every face that does not touch another ore block. Blocks are centred on their voxel
	// position.
	private void FindOreFaces(Piece piece)
	{
		int x0 = piece.X * Chunk;
		int z0 = piece.Z * Chunk;
		for (int lz = 0; lz < Chunk; lz++)
		{
			for (int ly = 0; ly < piece.SY; ly++)
			{
				for (int lx = 0; lx < Chunk; lx++)
				{
					byte cell = piece.Grid[Index(piece, lx, ly, lz)];
					byte c = ClassOfCell(cell);
					if (!IsOre(c))
					{
						continue;
					}
					int x = x0 + lx;
					int y = piece.Y0 + ly;
					int z = z0 + lz;
					for (int f = 0; f < 6; f++)
					{
						XYZ n = Normals[f];
						if (!IsOre(ClassOfCell(At(piece, x + n.x, y + n.y, z + n.z, false))))
						{
							_faces[c].Add(new Face { Center = new Vector3(x, y, z), Normal = new Vector3(n.x, n.y, n.z), Type = cell });
						}
					}
				}
			}
		}
	}

	// gameLook: the vertices as the game's terrain mesher makes them, for the terrain's own material; else for our atlas.
	private void Emit(List<Face>[] faces, List<Mesh>[] target, bool gameLook)
	{
		for (int c = 1; c < ClassCount; c++)
		{
			List<Face> list = faces[c];
			for (int i = 0; i < list.Count; i++)
			{
				AddFace(list[i].Center, list[i].Normal, c, list[i].Type, gameLook);
				if (_vertices.Count >= MaxVertices)
				{
					Finish(target, c);
				}
			}
			list.Clear();
			if (_vertices.Count > 0)
			{
				Finish(target, c);
			}
		}
	}

	private void AddFace(Vector3 center, Vector3 normal, int c, byte type, bool gameLook)
	{
		// Two axes across the face; the triangles below wind clockwise seen from outside (Unity's front face).
		Vector3 u = (Mathf.Abs(normal.y) > 0.5f) ? Vector3.right : Vector3.up;
		Vector3 v = Vector3.Cross(normal, u);
		Vector3 face = center + normal * 0.5f;
		int start = _vertices.Count;
		_vertices.Add(face + (-u - v) * 0.5f);
		_vertices.Add(face + (-u + v) * 0.5f);
		_vertices.Add(face + (u + v) * 0.5f);
		_vertices.Add(face + (u - v) * 0.5f);
		// Shading for the unlit see-through shader: tops light, sides darker, bottoms darkest.
		float shade = (normal.y > 0.5f) ? 1f : ((normal.y < -0.5f) ? 0.55f : 0.78f);
		for (int i = 0; i < 4; i++)
		{
			_normals.Add(normal);
			_colors.Add(new Color(shade, shade, shade, 1f));
		}
		if (gameLook)
		{
			AddGameUvs(center, type);
		}
		else
		{
			Vector4 tile = TileRect(c);
			_uvs.Add(new Vector2(tile.x, tile.y));
			_uvs.Add(new Vector2(tile.x, tile.w));
			_uvs.Add(new Vector2(tile.z, tile.w));
			_uvs.Add(new Vector2(tile.z, tile.y));
			for (int i = 0; i < 4; i++)
			{
				_uvs2.Add(Vector2.zero);
			}
		}
		_triangles.Add(start);
		_triangles.Add(start + 2);
		_triangles.Add(start + 1);
		_triangles.Add(start);
		_triangles.Add(start + 3);
		_triangles.Add(start + 2);
	}

	// The terrain mesher's texture coordinates (LevelConfigNormalUvs.NormalUvs, Display.CreateLocalizedMesh, CubeDrawer):
	// uv0 counts in sixteenths of the terrain texture, the type's 4 x 4 cell plus one of its 16 variants picked by
	// the map's noise at the voxel; uv2 holds the type's overlay (the specks of ore), else 65535 for none. Both get
	// the corner's offset, a little inside the variant. Transitions to neighbouring types are left out.
	private void AddGameUvs(Vector3 center, byte type)
	{
		XYZ p = new XYZ(Mathf.RoundToInt(center.x), Mathf.RoundToInt(center.y), Mathf.RoundToInt(center.z));
		int variant = 0;
		if (_noise != null)
		{
			int index = Repeat(p.x + 7 * p.y, _noiseWidth) + Repeat(p.z + 17 * p.y, _noiseHeight) * _noiseWidth;
			int noise = _noise[index].r;
			variant = _gameAtlas.UsesWeightedNoise(type) ? LevelConfigNormalUvs.GetWeightedNoiseVariant(noise, _gameAtlas.GetNoiseRowWeights(type)) : noise * 16 / 255 % 16;
		}
		Vector2 cell = _gameAtlas.GetUv(type);
		Vector2 uv = new Vector2(Mathf.Round(cell.x) * 4f + variant % 4, Mathf.Round(cell.y) * 4f + variant / 4);
		Vector2 uv2 = new Vector2(65535f, 65535f);
		if (_gameAtlas.HasUvSpecial(type))
		{
			Vector2 special = _gameAtlas.GetUvSpecial(type);
			uv2 = new Vector2(Mathf.Round(special.x) * 4f + variant % 4 + (Mathf.Round(special.y) * 4f + variant / 4) * 16f, 65535f);
		}
		float d = _uvInner;
		float o = _uvOuter;
		Vector2 outer = new Vector2(o, o);
		// The corners in the order AddFace puts them.
		Vector2 c0 = new Vector2(d, d) + outer;
		Vector2 c1 = new Vector2(d, 1f - d) + outer;
		Vector2 c2 = new Vector2(1f - d, 1f - d) + outer;
		Vector2 c3 = new Vector2(1f - d, d) + outer;
		_uvs.Add(uv + c0);
		_uvs.Add(uv + c1);
		_uvs.Add(uv + c2);
		_uvs.Add(uv + c3);
		_uvs2.Add(uv2 + c0);
		_uvs2.Add(uv2 + c1);
		_uvs2.Add(uv2 + c2);
		_uvs2.Add(uv2 + c3);
	}

	private static int Repeat(int x, int w)
	{
		int r = x % w;
		return (r < 0) ? r + w : r;
	}

	private void Finish(List<Mesh>[] target, int c)
	{
		Mesh mesh = new Mesh { name = "XRay blocks" };
		mesh.SetVertices(_vertices);
		mesh.SetNormals(_normals);
		mesh.SetUVs(0, _uvs);
		mesh.SetUVs(1, _uvs2);
		mesh.SetColors(_colors);
		mesh.SetTriangles(_triangles, 0);
		mesh.RecalculateBounds();
		_vertices.Clear();
		_normals.Clear();
		_uvs.Clear();
		_uvs2.Clear();
		_colors.Clear();
		_triangles.Clear();
		(target[c] ?? (target[c] = new List<Mesh>())).Add(mesh);
	}

	private static void ClearMeshes(Piece piece)
	{
		ClearMeshes(piece.Meshes);
		ClearMeshes(piece.Surface);
	}

	private static void ClearMeshes(List<Mesh>[] lists)
	{
		for (int c = 1; c < ClassCount; c++)
		{
			List<Mesh> meshes = lists[c];
			if (meshes == null)
			{
				continue;
			}
			foreach (Mesh mesh in meshes)
			{
				Object.Destroy(mesh);
			}
			meshes.Clear();
		}
	}
}
