using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CastleStoryPlus.Building;

// Builds a mod building's model from boxes and cylinders. Parts are merged into one mesh per surface, textured
// with the mod's own textures (Assets/Market, made by tools/make_market_textures.py): one texture repeat per
// block, so a part's texture keeps its scale whatever its size, and wood grain runs along each part's long side.
// Used by the market, the smithy, the armoury and the research station.
internal sealed class BoxModel
{
	internal sealed class Surface
	{
		public string Texture;

		public Color Fallback;

		// Turn the texture so its width (the wood grain) runs along the longer side of each face.
		public bool AlongLongSide;

		// The texture starts at the part's lower corner instead of at a random offset (a picture that must line up
		// with the part, like the book shelves).
		public bool Aligned;

		public Surface(string texture, int fallback, bool alongLongSide)
		{
			Texture = texture;
			Fallback = Hex(fallback);
			AlongLongSide = alongLongSide;
		}
	}

	public static readonly Surface WoodLight = new Surface("wood_light", 0xc89a5e, alongLongSide: true);

	public static readonly Surface Wood = new Surface("wood", 0x9a6a3c, alongLongSide: true);

	public static readonly Surface WoodDark = new Surface("wood_dark", 0x6e4826, alongLongSide: true);

	public static readonly Surface Stone = new Surface("stone", 0x9b9a92, alongLongSide: false);

	public static readonly Surface Brick = new Surface("brick", 0xb9563a, alongLongSide: false);

	public static readonly Surface Roof = new Surface("roof", 0x9c4a2e, alongLongSide: false);

	public static readonly Surface Awning = new Surface("awning", 0xc23b2c, alongLongSide: false);

	public static readonly Surface Metal = new Surface("metal", 0x3d3f43, alongLongSide: false);

	public static readonly Surface Iron = new Surface("iron", 0x8a9096, alongLongSide: false);

	public static readonly Surface CrystalBlue = new Surface("crystal_blue", 0x58b8e8, alongLongSide: false);

	public static readonly Surface CrystalOrange = new Surface("crystal_orange", 0xf0a23a, alongLongSide: false);

	public static readonly Surface CrystalDark = new Surface("crystal_dark", 0x8a3ad0, alongLongSide: false);

	// A shelf row of book spines, standing on the bottom of each half block.
	public static readonly Surface Books = new Surface("books", 0x7a4a3a, alongLongSide: false) { Aligned = true };

	public static readonly Surface LeatherRed = new Surface("leather_red", 0x8e2f28, alongLongSide: false);

	public static readonly Surface LeatherGreen = new Surface("leather_green", 0x35603a, alongLongSide: false);

	public static readonly Surface LeatherBlue = new Surface("leather_blue", 0x2f4a7a, alongLongSide: false);

	public static readonly Surface Paper = new Surface("paper", 0xece2c6, alongLongSide: false);

	private sealed class MeshData
	{
		public readonly List<Vector3> Vertices = new List<Vector3>();

		public readonly List<Vector3> Normals = new List<Vector3>();

		public readonly List<Vector2> Uvs = new List<Vector2>();

		public readonly List<int> Triangles = new List<int>();
	}

	// The six faces of a unit box: normal, then the two face axes.
	private static readonly Vector3[][] Faces = new Vector3[6][]
	{
		new Vector3[3] { Vector3.right, Vector3.forward, Vector3.up },
		new Vector3[3] { Vector3.left, Vector3.forward, Vector3.up },
		new Vector3[3] { Vector3.up, Vector3.right, Vector3.forward },
		new Vector3[3] { Vector3.down, Vector3.right, Vector3.forward },
		new Vector3[3] { Vector3.forward, Vector3.right, Vector3.up },
		new Vector3[3] { Vector3.back, Vector3.right, Vector3.up }
	};

	private static Mesh _cylinder;

	private static readonly Dictionary<string, Material> Materials = new Dictionary<string, Material>();

	private static Texture2D _flatNormals;

	private readonly string _name;

	private readonly Dictionary<Surface, MeshData> _meshes = new Dictionary<Surface, MeshData>();

	private int _seed;

	public BoxModel(string name, int seed)
	{
		_name = name;
		_seed = seed;
		LoadMeshes();
	}

	// ghost: the blueprint's see-through material for everything; otherwise each surface is drawn with a copy of
	// baseMaterial (the shader of the game's own buildings) and its texture.
	public GameObject Build(Transform parent, Vector3 centre, Material baseMaterial, Material ghost)
	{
		GameObject root = new GameObject(_name + "Visuals");
		root.transform.SetParent(parent, worldPositionStays: false);
		root.transform.localPosition = centre;
		foreach (KeyValuePair<Surface, MeshData> group in _meshes)
		{
			Mesh mesh = new Mesh();
			mesh.name = _name + " " + group.Key.Texture;
			mesh.SetVertices(group.Value.Vertices);
			mesh.SetNormals(group.Value.Normals);
			mesh.SetUVs(0, group.Value.Uvs);
			mesh.SetTriangles(group.Value.Triangles, 0);
			// The game's shaders use a normal map (needs tangents, black without) and vertex colours.
			mesh.SetColors(White(group.Value.Vertices.Count));
			mesh.RecalculateTangents();
			mesh.RecalculateBounds();
			GameObject piece = new GameObject("Part " + group.Key.Texture);
			piece.transform.SetParent(root.transform, worldPositionStays: false);
			piece.AddComponent<MeshFilter>().sharedMesh = mesh;
			piece.AddComponent<MeshRenderer>().sharedMaterial = (ghost != null) ? ghost : MaterialFor(group.Key, baseMaterial);
		}
		return root;
	}

	// A box of w x h x d centred on (x, y, z), turned by the given angles in degrees.
	public void Box(float w, float h, float d, Surface surface, float x, float y, float z, float tiltX = 0f, float turnY = 0f, float tiltZ = 0f)
	{
		AddBox(surface, new Vector3(x, y, z), Quaternion.Euler(tiltX, turnY, tiltZ), new Vector3(w, h, d));
	}

	// An upright cylinder of the given diameter and height centred on (x, y, z).
	public void Cylinder(float diameter, float height, Surface surface, float x, float y, float z)
	{
		AddCylinder(surface, new Vector3(x, y, z), Quaternion.identity, new Vector3(diameter, height / 2f, diameter));
	}

	// A box with UVs in world units (one texture repeat per block), each part at its own random texture offset so
	// neighbouring parts do not show the same boards or stones.
	public void AddBox(Surface surface, Vector3 position, Quaternion rotation, Vector3 size)
	{
		MeshData data = DataFor(surface);
		Vector2 offset = new Vector2(Random01(), Random01());
		if (surface.Aligned)
		{
			offset = Vector2.zero;
		}
		foreach (Vector3[] face in Faces)
		{
			Vector3 normal = face[0];
			Vector3 axisU = face[1];
			Vector3 axisV = face[2];
			float lengthU = Vector3.Scale(axisU, size).magnitude;
			float lengthV = Vector3.Scale(axisV, size).magnitude;
			bool swap = surface.AlongLongSide && lengthV > lengthU;
			int first = data.Vertices.Count;
			Vector3 worldNormal = rotation * normal;
			for (int corner = 0; corner < 4; corner++)
			{
				float a = (corner == 1 || corner == 2) ? 0.5f : -0.5f;
				float b = (corner >= 2) ? 0.5f : -0.5f;
				Vector3 local = normal * 0.5f + axisU * a + axisV * b;
				data.Vertices.Add(position + rotation * Vector3.Scale(local, size));
				data.Normals.Add(worldNormal);
				Vector2 uv = new Vector2((a + 0.5f) * lengthU, (b + 0.5f) * lengthV);
				data.Uvs.Add((swap ? new Vector2(uv.y, uv.x) : uv) + offset);
			}
			// Corners 0 (-,-), 1 (+,-), 2 (+,+), 3 (-,+); wind both triangles so they face along the normal.
			Vector3 p0 = data.Vertices[first];
			Vector3 p1 = data.Vertices[first + 1];
			Vector3 p2 = data.Vertices[first + 2];
			bool facing = Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), worldNormal) > 0f;
			if (facing)
			{
				data.Triangles.AddRange(new int[6] { first, first + 1, first + 2, first, first + 2, first + 3 });
			}
			else
			{
				data.Triangles.AddRange(new int[6] { first, first + 2, first + 1, first, first + 3, first + 2 });
			}
		}
	}

	// Unity's cylinder, its UVs scaled to world units: around by the circumference, along by the height.
	public void AddCylinder(Surface surface, Vector3 position, Quaternion rotation, Vector3 scale)
	{
		MeshData data = DataFor(surface);
		Vector3[] vertices = _cylinder.vertices;
		Vector3[] normals = _cylinder.normals;
		Vector2[] uvs = _cylinder.uv;
		int[] triangles = _cylinder.triangles;
		int first = data.Vertices.Count;
		float around = Mathf.PI * Mathf.Max(scale.x, scale.z);
		float along = scale.y * 2f;
		Vector2 offset = new Vector2(Random01(), Random01());
		for (int i = 0; i < vertices.Length; i++)
		{
			data.Vertices.Add(position + rotation * Vector3.Scale(vertices[i], scale));
			data.Normals.Add((rotation * new Vector3(normals[i].x / scale.x, normals[i].y / scale.y, normals[i].z / scale.z)).normalized);
			// Grain along the cylinder's axis.
			data.Uvs.Add(new Vector2(uvs[i].y * along, uvs[i].x * around) + offset);
		}
		foreach (int index in triangles)
		{
			data.Triangles.Add(first + index);
		}
	}

	// A flat roof slope between two edges (z0, y0) and (z1, y1), running along x and width wide. The top face
	// lies on the line between the two edges, so slopes that share an edge meet without a gap.
	public void Slope(float z0, float y0, float z1, float y1, float width, float thickness, Surface surface, float x = 0f)
	{
		float dz = z1 - z0;
		float dy = y1 - y0;
		float length = Mathf.Sqrt(dz * dz + dy * dy);
		// A positive turn about x tips +z down; the slope rises from z0 to z1.
		Quaternion rotation = Quaternion.Euler(-Mathf.Atan2(dy, dz) * Mathf.Rad2Deg, 0f, 0f);
		Vector3 middle = new Vector3(x, (y0 + y1) / 2f, (z0 + z1) / 2f);
		AddBox(surface, middle + rotation * new Vector3(0f, -thickness / 2f, 0f), rotation, new Vector3(width, thickness, length));
	}

	// The same, between two edges (x0, y0) and (x1, y1), running along z and depth deep, centred on z.
	public void SlopeAlongX(float x0, float y0, float x1, float y1, float depth, float thickness, Surface surface, float z = 0f)
	{
		float dx = x1 - x0;
		float dy = y1 - y0;
		float length = Mathf.Sqrt(dx * dx + dy * dy);
		// A positive turn about z tips +x up; the slope rises from x0 to x1.
		Quaternion rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(dy, dx) * Mathf.Rad2Deg);
		Vector3 middle = new Vector3((x0 + x1) / 2f, (y0 + y1) / 2f, z);
		AddBox(surface, middle + rotation * new Vector3(0f, -thickness / 2f, 0f), rotation, new Vector3(length, thickness, depth));
	}

	private MeshData DataFor(Surface surface)
	{
		if (!_meshes.TryGetValue(surface, out MeshData data))
		{
			data = new MeshData();
			_meshes[surface] = data;
		}
		return data;
	}

	private float Random01()
	{
		_seed = (int)((_seed * 16807L) % 2147483647);
		return (_seed % 1000) / 1000f;
	}

	private static List<Color> White(int count)
	{
		List<Color> colors = new List<Color>(count);
		for (int i = 0; i < count; i++)
		{
			colors.Add(Color.white);
		}
		return colors;
	}

	private static void LoadMeshes()
	{
		if (_cylinder != null)
		{
			return;
		}
		GameObject cylinder = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
		_cylinder = cylinder.GetComponent<MeshFilter>().sharedMesh;
		Object.Destroy(cylinder);
	}

	private static Material MaterialFor(Surface surface, Material baseMaterial)
	{
		if (Materials.TryGetValue(surface.Texture, out Material material) && material != null)
		{
			return material;
		}
		if (Materials.Count == 0 && baseMaterial != null)
		{
			LogProperties(baseMaterial);
		}
		material = (baseMaterial != null) ? new Material(baseMaterial) : new Material(Shader.Find("Diffuse"));
		material.name = "CastleStoryPlus " + surface.Texture;
		// The game's building shader (ShaderForge) reads its colour from _Diffuse, not _MainTex. Its normal
		// map is laid out for the source building's UVs, so it gets a flat one.
		string diffuse = material.HasProperty("_Diffuse") ? "_Diffuse" : "_MainTex";
		material.SetTexture(diffuse, LoadTexture(surface));
		material.SetTextureScale(diffuse, Vector2.one);
		material.SetTextureOffset(diffuse, Vector2.zero);
		if (material.HasProperty("_Normals"))
		{
			material.SetTexture("_Normals", FlatNormals());
		}
		if (material.HasProperty("_Color"))
		{
			material.color = Color.white;
		}
		Materials[surface.Texture] = material;
		return material;
	}

	// Straight-up normal in both encodings Unity unpacks (RGB, and DXT5nm's alpha and green).
	private static Texture2D FlatNormals()
	{
		if (_flatNormals == null)
		{
			_flatNormals = new Texture2D(2, 2, TextureFormat.RGBA32, mipmap: false, linear: true);
			Color flat = new Color(0.5f, 0.5f, 1f, 0.5f);
			_flatNormals.SetPixels(new Color[4] { flat, flat, flat, flat });
			_flatNormals.Apply();
			_flatNormals.name = "CastleStoryPlus flat normals";
		}
		return _flatNormals;
	}

	// Which common texture and colour slots the game's shader has (Unity 5.6 cannot list them).
	private static void LogProperties(Material material)
	{
		List<string> found = new List<string>();
		foreach (string name in new[] { "_MainTex", "_Diffuse", "_Normals", "_Specular", "_Gloss", "_Emission", "_Color", "_BumpMap", "_NormalMap", "_Normal", "_DetailTex", "_MaskTex", "_Mask", "_OcclusionMap", "_EmissionMap", "_MetallicGlossMap", "_SpecGlossMap", "_Ramp" })
		{
			if (material.HasProperty(name))
			{
				Texture texture = (name != "_Color" && name != "_Gloss") ? material.GetTexture(name) : null;
				found.Add(name + ((texture != null) ? ("=" + texture.name) : string.Empty));
			}
		}
		Plugin.Log.LogInfo("BoxModel: shader " + material.shader.name + " has " + string.Join(", ", found.ToArray()));
	}

	// The surface's PNG next to the plugin, or a plain colour if it is missing.
	private static Texture2D LoadTexture(Surface surface)
	{
		string path = Path.Combine(Path.Combine(Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "Assets"), "Market"), surface.Texture + ".png");
		Texture2D texture = new Texture2D(2, 2, TextureFormat.ARGB32, mipmap: true);
		if (!File.Exists(path) || !texture.LoadImage(File.ReadAllBytes(path)))
		{
			Plugin.Log.LogWarning("BoxModel: texture " + path + " not found, using a plain colour");
			Object.Destroy(texture);
			texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipmap: false);
			texture.SetPixels(new Color[4] { surface.Fallback, surface.Fallback, surface.Fallback, surface.Fallback });
			texture.Apply();
		}
		texture.name = "CastleStoryPlus " + surface.Texture;
		texture.wrapMode = TextureWrapMode.Repeat;
		texture.filterMode = FilterMode.Trilinear;
		texture.anisoLevel = 4;
		return texture;
	}

	private static Color Hex(int rgb)
	{
		return new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
	}
}
