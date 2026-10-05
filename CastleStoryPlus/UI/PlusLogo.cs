using System.Collections;
using System.Collections.Generic;
using System.IO;
using Brix.NewUI.LoadingScreen;
using CastleStoryPlus.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CastleStoryPlus.UI;

// Castle Story Plus branding (artwork rendered by tools/make_logo.py):
// - main menu: a "PLUS" badge next to every copy of the game's logo (RawImage CastleStory_Logo_570);
// - splash screen: the full Castle Story Plus logo under the Unity logo, fading with it;
// - loading screen: the logo on the top curtain above "Loading", so it slides with the curtain.
// The game's logo itself is not redistributed: menu and loading screen reuse its texture at runtime.
[Feature(Features.PlusLogo, Features.PlusLogoInfo)]
internal class PlusLogo : MonoBehaviour
{
	internal const string BadgeName = "CastleStoryPlusBadge";

	internal const string LogoName = "CastleStoryPlusLogo";

	private const string GameLogoPrefix = "CastleStory_Logo";

	// Badge height relative to the game logo's height, and how far it tucks under the logo's right edge.
	private const float BadgeHeight = 0.4f;

	private const float BadgeTuck = 0.04f;

	private static readonly float[] RescanDelays = { 0.5f, 2f };

	private static readonly Dictionary<string, Sprite> Sprites = new Dictionary<string, Sprite>();

	private static void Enable()
	{
		Plugin.Root.AddComponent<PlusLogo>();
	}

	private void Start()
	{
		SceneManager.sceneLoaded += OnSceneLoaded;
		// The splash scene is already loaded when BepInEx starts.
		for (int i = 0; i < SceneManager.sceneCount; i++)
		{
			OnSceneLoaded(SceneManager.GetSceneAt(i), LoadSceneMode.Additive);
		}
	}

	private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
	{
		if (scene.name == "SceneSplashScreen")
		{
			AddToSplash();
		}
		StartCoroutine(BadgeMenuLogos());
	}

	private static IEnumerator BadgeMenuLogos()
	{
		AddBadges();
		float waited = 0f;
		foreach (float delay in RescanDelays)
		{
			while (waited < delay)
			{
				yield return null;
				waited += Time.unscaledDeltaTime;
			}
			AddBadges();
		}
	}

	// Every game logo in the loaded scenes, inactive menus included.
	private static void AddBadges()
	{
		Sprite badge = LoadSprite("plus.png");
		if (badge == null)
		{
			return;
		}
		foreach (RawImage image in Resources.FindObjectsOfTypeAll<RawImage>())
		{
			if (image == null || !image.gameObject.scene.IsValid() || !IsGameLogo(image.texture) || image.transform.Find(BadgeName) != null)
			{
				continue;
			}
			AddBadge(image.rectTransform, badge);
		}
	}

	// The badge sits right of "STORY", bottom-aligned with the logo, reading CASTLE STORY PLUS.
	private static void AddBadge(RectTransform logo, Sprite badge)
	{
		RectTransform rect = NewImage(BadgeName, logo, badge);
		rect.anchorMin = new Vector2(1f, 0f);
		rect.anchorMax = new Vector2(1f, 0f);
		rect.pivot = new Vector2(0f, 0f);
		float height = logo.rect.height * BadgeHeight;
		rect.sizeDelta = new Vector2(height * badge.rect.width / badge.rect.height, height);
		rect.anchoredPosition = new Vector2(-logo.rect.width * BadgeTuck, 0f);
	}

	// Splash: the full logo under the Unity logo, shown and faded together with it.
	private static void AddToSplash()
	{
		Sprite sprite = LoadSprite("logo.png");
		GameObject canvas = GameObject.Find("Canvas");
		Transform unity = canvas != null ? canvas.transform.Find("Unity") : null;
		Image unityImage = unity != null ? unity.GetComponent<Image>() : null;
		if (sprite == null || unityImage == null || canvas.transform.Find(LogoName) != null)
		{
			return;
		}
		RectTransform unityRect = unityImage.rectTransform;
		RectTransform rect = NewImage(LogoName, canvas.transform, sprite);
		rect.SetSiblingIndex(unity.GetSiblingIndex() + 1);
		rect.anchorMin = unityRect.anchorMin;
		rect.anchorMax = unityRect.anchorMax;
		rect.pivot = new Vector2(0.5f, 1f);
		float width = 640f;
		rect.sizeDelta = new Vector2(width, width * sprite.rect.height / sprite.rect.width);
		rect.anchoredPosition = unityRect.anchoredPosition + new Vector2(0f, -unityRect.rect.height * 0.5f - 60f);
		SplashFollower follower = rect.gameObject.AddComponent<SplashFollower>();
		follower.Target = unityImage;
		follower.Self = rect.GetComponent<Image>();
		follower.LateUpdate();
	}

	// Loading screen: on the top curtain, above the spinner and the "Loading" title.
	internal static void AddToLoadingScreen(LoadingScreen screen)
	{
		Text title = screen.Title;
		Transform top = title != null && title.transform.parent != null ? title.transform.parent.parent : null;
		if (top == null || top.Find(LogoName) != null)
		{
			return;
		}
		Texture gameLogo = FindGameLogo();
		Sprite badge = LoadSprite("plus.png");
		Sprite full = LoadSprite("logo.png");
		RectTransform rect;
		if (gameLogo != null && badge != null)
		{
			rect = new GameObject(LogoName, typeof(RectTransform)).GetComponent<RectTransform>();
			rect.SetParent(top, worldPositionStays: false);
			RawImage raw = rect.gameObject.AddComponent<RawImage>();
			raw.texture = gameLogo;
			raw.raycastTarget = false;
			rect.sizeDelta = new Vector2(gameLogo.width, gameLogo.height);
			AddBadge(rect, badge);
		}
		else if (full != null)
		{
			rect = NewImage(LogoName, top, full);
			rect.sizeDelta = new Vector2(570f, 570f * full.rect.height / full.rect.width);
		}
		else
		{
			return;
		}
		rect.anchorMin = new Vector2(0.5f, 0f);
		rect.anchorMax = new Vector2(0.5f, 0f);
		rect.pivot = new Vector2(0.5f, 0f);
		// Clear of the spinner (centred 64 above the curtain's edge, 64 high) with a gap, nudged left so
		// logo plus badge are centred together.
		float overhang = rect.Find(BadgeName) is RectTransform badgeRect ? badgeRect.sizeDelta.x - BadgeTuck * rect.sizeDelta.x : 0f;
		rect.anchoredPosition = new Vector2(-overhang * 0.5f, 140f);
	}

	private static RectTransform NewImage(string name, Transform parent, Sprite sprite)
	{
		GameObject go = new GameObject(name, typeof(RectTransform));
		RectTransform rect = go.GetComponent<RectTransform>();
		rect.SetParent(parent, worldPositionStays: false);
		go.AddComponent<LayoutElement>().ignoreLayout = true;
		Image image = go.AddComponent<Image>();
		image.sprite = sprite;
		image.preserveAspect = true;
		image.raycastTarget = false;
		return rect;
	}

	private static bool IsGameLogo(Texture texture)
	{
		return texture != null && texture.name.StartsWith(GameLogoPrefix);
	}

	private static Texture FindGameLogo()
	{
		Texture best = null;
		foreach (Texture2D texture in Resources.FindObjectsOfTypeAll<Texture2D>())
		{
			if (IsGameLogo(texture) && (best == null || texture.width > best.width))
			{
				best = texture;
			}
		}
		return best;
	}

	private static Sprite LoadSprite(string file)
	{
		if (Sprites.TryGetValue(file, out Sprite cached) && cached != null)
		{
			return cached;
		}
		string path = Path.Combine(Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "Assets"), file);
		if (!File.Exists(path))
		{
			Plugin.Log.LogWarning("Logo artwork not found: " + path);
			return null;
		}
		// Shown far below its size (the badge is ~1/3 of it in the menu): mipmaps keep the edges smooth,
		// a texture without them aliases into jagged pixels.
		Texture2D texture = new Texture2D(2, 2, TextureFormat.ARGB32, mipmap: true);
		if (!texture.LoadImage(File.ReadAllBytes(path)))
		{
			Destroy(texture);
			return null;
		}
		texture.filterMode = FilterMode.Trilinear;
		texture.anisoLevel = 4;
		texture.wrapMode = TextureWrapMode.Clamp;
		// Survives scene loads like the sprite cache that holds it.
		texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
		Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
		sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
		Sprites[file] = sprite;
		Plugin.Log.LogInfo("Loaded " + file + " (" + texture.width + "x" + texture.height + ", " + texture.mipmapCount + " mipmaps)");
		return sprite;
	}
}

[Feature(Features.PlusLogo, Features.PlusLogoInfo)]
[HarmonyPatch(typeof(LoadingScreen), nameof(LoadingScreen.Start))]
internal static class PlusLogoLoadingScreenPatch
{
	private static void Postfix(LoadingScreen __instance)
	{
		PlusLogo.AddToLoadingScreen(__instance);
	}
}

// Mirrors the splash animation of the Unity logo (shown, alpha, scale) onto the Castle Story Plus logo.
internal class SplashFollower : MonoBehaviour
{
	public Image Target;

	public Image Self;

	public void LateUpdate()
	{
		if (Target == null)
		{
			Destroy(gameObject);
			return;
		}
		bool shown = Target.gameObject.activeSelf && Target.enabled;
		Self.enabled = shown;
		if (!shown)
		{
			return;
		}
		Color color = Color.white;
		color.a = Target.color.a;
		Self.color = color;
		transform.localScale = Target.transform.localScale;
	}
}
