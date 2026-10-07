using System;
using System.Collections.Generic;
using UnityEngine;

namespace CastleStoryPlus.Building;

// A long stone brick's own texture: the game's 1 x 2 brick is a plain box whose every face shows one picture from the
// bricks' shared texture (the top one stone with a joint at both ends). A longer or wider brick stretched over that
// picture looked stretched, and its pieces joined; here every face gets its own picture instead, made from the game's
// at the same size per block: the picture's edges (joints and bevels) as they are, its middle continued (mirrored
// back and forth, so it has no seams) to the face's size. Built while the game runs from the game's own diffuse and
// normal textures (read back through the graphics card, as they cannot be read directly), so none of the game's
// pictures are shipped; one texture pair and material per brick shape.
internal static class LongStoneTexture
{
	private class Face
	{
		public List<int> Vertices = new List<int>();

		public Rect Source;

		// Size of the new picture in pixels, and where it goes in the new texture.
		public int Width;

		public int Height;

		public int TargetX;

		public int TargetY;
	}

	// Share of a picture kept as it is at each side (the joints and bevels sit well inside it).
	private const float Border = 0.3f;

	// Pixels around each picture, filled with its edge, so mipmaps do not bleed between pictures.
	private const int Padding = 4;

	private const int AtlasWidth = 1024;

	private static readonly Dictionary<string, Material> Materials = new Dictionary<string, Material>();

	private static Texture2D _diffuse;

	private static Texture2D _normals;

	// Gives the brick mesh its own pictures: moves its texture coordinates onto a new texture and answers the material
	// that shows it. sourceMesh is the game's 1 x 2 brick, lengthened the brick mesh made from it (same vertices).
	// Null (and the mesh unchanged) when the game's textures cannot be read.
	internal static Material Make(string name, Mesh sourceMesh, Mesh lengthened, Material source)
	{
		if (Materials.TryGetValue(name, out Material made))
		{
			return made;
		}
		if (source == null || !source.HasProperty("_Diffuse") || !source.HasProperty("_Normals"))
		{
			return null;
		}
		try
		{
			_diffuse = _diffuse ?? ReadBack(source.GetTexture("_Diffuse"), linear: false);
			_normals = _normals ?? ReadBack(source.GetTexture("_Normals"), linear: true);
			if (_diffuse == null || _normals == null)
			{
				return null;
			}
			List<Face> faces = Faces(sourceMesh, lengthened, _diffuse.width, _diffuse.height);
			int height = Pack(faces);
			Texture2D diffuse = Paint(faces, _diffuse, height, linear: false);
			Texture2D normals = Paint(faces, _normals, height, linear: true);
			diffuse.name = name + "_Diffuse";
			normals.name = name + "_Normals";
			Remap(faces, lengthened, diffuse.width, height);
			Material material = new Material(source) { name = name };
			material.SetTexture("_Diffuse", diffuse);
			material.SetTexture("_Normals", normals);
			Materials[name] = material;
			Plugin.Log.LogInfo("CustomBlocks: " + name + " has its own texture (" + diffuse.width + " x " + height + ", " + faces.Count + " faces)");
			return material;
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning("CustomBlocks: no own texture for " + name + ": " + ex.Message);
			return null;
		}
	}

	// A readable copy of a texture, drawn through a render texture.
	internal static Texture2D ReadBack(Texture texture, bool linear)
	{
		if (texture == null)
		{
			return null;
		}
		RenderTexture target = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.Default);
		RenderTexture previous = RenderTexture.active;
		try
		{
			Graphics.Blit(texture, target);
			RenderTexture.active = target;
			Texture2D copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, linear);
			copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
			copy.Apply(false);
			return copy;
		}
		finally
		{
			RenderTexture.active = previous;
			RenderTexture.ReleaseTemporary(target);
		}
	}

	// The brick's faces (vertices by normal), each with its picture in the game's texture and the size its picture gets:
	// the game's picture scaled by how much longer the face became along each of the picture's directions.
	private static List<Face> Faces(Mesh sourceMesh, Mesh lengthened, int textureWidth, int textureHeight)
	{
		Vector3[] normals = sourceMesh.normals;
		Vector3[] before = sourceMesh.vertices;
		Vector3[] after = lengthened.vertices;
		Vector2[] uv = sourceMesh.uv;
		Dictionary<int, Face> byNormal = new Dictionary<int, Face>();
		for (int i = 0; i < uv.Length; i++)
		{
			int key = (Mathf.RoundToInt(normals[i].x) + 1) * 9 + (Mathf.RoundToInt(normals[i].y) + 1) * 3 + Mathf.RoundToInt(normals[i].z) + 1;
			if (!byNormal.TryGetValue(key, out Face face))
			{
				face = new Face();
				byNormal[key] = face;
			}
			face.Vertices.Add(i);
		}
		List<Face> faces = new List<Face>(byNormal.Values);
		foreach (Face face in faces)
		{
			Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
			Vector2 max = new Vector2(float.MinValue, float.MinValue);
			foreach (int i in face.Vertices)
			{
				min = Vector2.Min(min, uv[i]);
				max = Vector2.Max(max, uv[i]);
			}
			face.Source = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
			face.Width = Mathf.Max(1, Mathf.RoundToInt(face.Source.width * textureWidth * Growth(face, before, after, uv, 0)));
			face.Height = Mathf.Max(1, Mathf.RoundToInt(face.Source.height * textureHeight * Growth(face, before, after, uv, 1)));
		}
		return faces;
	}

	// How much longer the face became along the picture's direction (0: across, 1: up): two of its vertices that differ
	// only in that coordinate, their distance after over before.
	private static float Growth(Face face, Vector3[] before, Vector3[] after, Vector2[] uv, int direction)
	{
		foreach (int a in face.Vertices)
		{
			foreach (int b in face.Vertices)
			{
				if (Mathf.Abs(uv[b][direction] - uv[a][direction]) < 0.001f || Mathf.Abs(uv[b][1 - direction] - uv[a][1 - direction]) > 0.001f)
				{
					continue;
				}
				float span = (before[b] - before[a]).magnitude;
				if (span > 0.001f)
				{
					return (after[b] - after[a]).magnitude / span;
				}
			}
		}
		return 1f;
	}

	// Rows of pictures, left to right; answers the texture's height (a power of two).
	private static int Pack(List<Face> faces)
	{
		faces.Sort((Face a, Face b) => b.Height.CompareTo(a.Height));
		int x = 0;
		int y = 0;
		int rowHeight = 0;
		foreach (Face face in faces)
		{
			int w = face.Width + 2 * Padding;
			int h = face.Height + 2 * Padding;
			if (x + w > AtlasWidth)
			{
				x = 0;
				y += rowHeight;
				rowHeight = 0;
			}
			face.TargetX = x + Padding;
			face.TargetY = y + Padding;
			x += w;
			rowHeight = Mathf.Max(rowHeight, h);
		}
		return Mathf.NextPowerOfTwo(Mathf.Max(1, y + rowHeight));
	}

	private static Texture2D Paint(List<Face> faces, Texture2D source, int height, bool linear)
	{
		Texture2D texture = new Texture2D(AtlasWidth, height, TextureFormat.RGBA32, true, linear)
		{
			wrapMode = TextureWrapMode.Clamp,
			filterMode = FilterMode.Trilinear,
			anisoLevel = 4
		};
		Color32[] pixels = new Color32[AtlasWidth * height];
		Color32[] from = source.GetPixels32();
		int sourceWidth = source.width;
		foreach (Face face in faces)
		{
			int left = Mathf.RoundToInt(face.Source.xMin * source.width);
			int bottom = Mathf.RoundToInt(face.Source.yMin * source.height);
			int width = Mathf.Max(1, Mathf.RoundToInt(face.Source.width * source.width));
			int tall = Mathf.Max(1, Mathf.RoundToInt(face.Source.height * source.height));
			for (int ty = -Padding; ty < face.Height + Padding; ty++)
			{
				int sy = bottom + Continue(Mathf.Clamp(ty, 0, face.Height - 1), tall, face.Height);
				for (int tx = -Padding; tx < face.Width + Padding; tx++)
				{
					int sx = left + Continue(Mathf.Clamp(tx, 0, face.Width - 1), width, face.Width);
					int x = face.TargetX + tx;
					int y = face.TargetY + ty;
					if (x >= 0 && x < AtlasWidth && y >= 0 && y < height)
					{
						pixels[y * AtlasWidth + x] = from[Mathf.Clamp(sy, 0, source.height - 1) * sourceWidth + Mathf.Clamp(sx, 0, sourceWidth - 1)];
					}
				}
			}
		}
		texture.SetPixels32(pixels);
		texture.Apply(true);
		return texture;
	}

	// The source pixel for a pixel of the longer picture: the edges as they are, the middle mirrored back and forth.
	private static int Continue(int at, int sourceSize, int targetSize)
	{
		if (targetSize <= sourceSize)
		{
			return Mathf.Min(at, sourceSize - 1);
		}
		int border = Mathf.Max(1, Mathf.RoundToInt(sourceSize * Border));
		if (at < border)
		{
			return at;
		}
		if (at >= targetSize - border)
		{
			return at - (targetSize - sourceSize);
		}
		int middle = Mathf.Max(1, sourceSize - 2 * border);
		int offset = at - border;
		int round = offset / middle;
		int inside = offset % middle;
		return border + ((round % 2 == 0) ? inside : middle - 1 - inside);
	}

	// Each face's texture coordinates moved from its picture in the game's texture onto its own picture.
	private static void Remap(List<Face> faces, Mesh mesh, int width, int height)
	{
		Vector2[] uv = mesh.uv;
		foreach (Face face in faces)
		{
			Rect target = new Rect((float)face.TargetX / width, (float)face.TargetY / height, (float)face.Width / width, (float)face.Height / height);
			foreach (int i in face.Vertices)
			{
				float u = (face.Source.width > 0f) ? (uv[i].x - face.Source.xMin) / face.Source.width : 0f;
				float v = (face.Source.height > 0f) ? (uv[i].y - face.Source.yMin) / face.Source.height : 0f;
				uv[i] = new Vector2(target.xMin + u * target.width, target.yMin + v * target.height);
			}
		}
		mesh.uv = uv;
	}
}
