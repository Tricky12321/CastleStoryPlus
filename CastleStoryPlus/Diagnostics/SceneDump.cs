using System.Collections;
using System.IO;
using System.Text;
using CastleStoryPlus.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CastleStoryPlus.Diagnostics;

// Modding aid, off by default ([Debug] SceneDump): writes the full object hierarchy to BepInEx/scenedump/
// when a scene loads and again a few seconds later, so UI built in the Unity scenes can be inspected.
[Feature]
internal class SceneDump : MonoBehaviour
{
	private static readonly float[] Delays = { 0f, 1f, 3f, 6f };

	private static void Enable()
	{
		if (Plugin.Cfg.Bind("Debug", "SceneDump", false, "Write the object hierarchy to BepInEx/scenedump/ on every scene load (modding aid).").Value)
		{
			Plugin.Root.AddComponent<SceneDump>();
		}
	}

	private void Awake()
	{
		SceneManager.sceneLoaded += OnSceneLoaded;
	}

	private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
	{
		StartCoroutine(DumpLater(scene.name));
	}

	private IEnumerator DumpLater(string sceneName)
	{
		float waited = 0f;
		foreach (float delay in Delays)
		{
			while (waited < delay)
			{
				yield return null;
				waited += Time.unscaledDeltaTime;
			}
			Dump(sceneName + "_" + delay.ToString("0") + "s");
		}
	}

	private static void Dump(string label)
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendLine("Screen " + Screen.width + "x" + Screen.height + ", time " + Time.realtimeSinceStartup.ToString("0.00"));
		foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
		{
			if (t.parent == null && t.hideFlags == HideFlags.None && t.gameObject.scene.IsValid())
			{
				sb.AppendLine("=== scene " + t.gameObject.scene.name);
				Write(sb, t, 0);
			}
		}
		string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "scenedump");
		Directory.CreateDirectory(dir);
		string safe = string.Join("_", label.Split(Path.GetInvalidFileNameChars()));
		File.WriteAllText(Path.Combine(dir, System.DateTime.Now.ToString("HH-mm-ss") + "_" + safe + ".txt"), sb.ToString());
	}

	private static void Write(StringBuilder sb, Transform t, int depth)
	{
		sb.Append(' ', depth * 2);
		sb.Append(t.gameObject.activeSelf ? "+ " : "- ").Append(t.name).Append(" [");
		foreach (Component c in t.GetComponents<Component>())
		{
			if (c == null || c is Transform)
			{
				continue;
			}
			sb.Append(c.GetType().Name);
			if (c is Behaviour b && !b.enabled)
			{
				sb.Append("(off)");
			}
			if (c is Image image && image.sprite != null)
			{
				sb.Append("(").Append(image.sprite.name).Append(")");
			}
			if (c is RawImage raw && raw.texture != null)
			{
				sb.Append("(").Append(raw.texture.name).Append(")");
			}
			if (c is Text text)
			{
				sb.Append("(\"").Append(text.text.Replace("\n", " ")).Append("\")");
			}
			if (c is Canvas canvas)
			{
				sb.Append("(").Append(canvas.renderMode).Append(" order ").Append(canvas.sortingOrder).Append(")");
			}
			if (c is CanvasGroup group)
			{
				sb.Append("(alpha ").Append(group.alpha.ToString("0.00")).Append(")");
			}
			sb.Append(' ');
		}
		sb.Append(']');
		if (t is RectTransform r)
		{
			sb.Append(" anchors ").Append(r.anchorMin).Append("-").Append(r.anchorMax).Append(" pos ").Append(r.anchoredPosition).Append(" size ").Append(r.rect.size);
		}
		sb.AppendLine();
		for (int i = 0; i < t.childCount; i++)
		{
			Write(sb, t.GetChild(i), depth + 1);
		}
	}
}
