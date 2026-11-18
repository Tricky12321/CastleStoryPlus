using CastleStoryPlus.Core;
using CastleStoryPlus.UI;
using UnityEngine;
using UnityEngine.UI;

namespace CastleStoryPlus.Weather;

// What the weather (Rain) looks and sounds like, on every peer: rain (thin streaks) or snow (slow flakes) falling in
// a box that follows the camera's view, a dark blue-grey veil over the picture under the game's own windows, the
// sun's light, the ambient light and the fog turned down, a rain noise made in code (filtered noise with some
// drops, looped without a seam) and a short message when it starts and stops. All of it fades in and out over
// FadeSeconds (real time) with the weather's strength. The game sets its light every frame, so the turned down
// values are put on top each frame from the value the game set (never on top of an earlier one).
[Feature(Features.Rain, Features.RainInfo)]
internal sealed class RainVisuals : MonoBehaviour
{
	private const float FadeSeconds = 4f;

	private const float BannerSeconds = 3.5f;

	// How dark the sun, the ambient light and the fog get at full strength (1: not at all), and the veil's opacity.
	private const float SunLevel = 0.45f;

	private const float AmbientLevel = 0.7f;

	private const float VeilAlpha = 0.22f;

	private const float RainVolume = 0.35f;

	private const int SampleRate = 44100;

	private static readonly Color RainColor = new Color(0.7f, 0.82f, 1f, 0.5f);

	private static readonly Color SnowColor = new Color(1f, 1f, 1f, 0.9f);

	private static readonly Color VeilColor = new Color(0.06f, 0.09f, 0.16f, 1f);

	private static readonly Color BannerColor = new Color(0.65f, 0.82f, 1f, 1f);

	private static RainVisuals _instance;

	private float _strength;

	private WeatherKind _shown;

	private ParticleSystem _rain;

	private ParticleSystem _snow;

	private Image _veil;

	private AudioSource _sound;

	private GameObject _banner;

	private Text _bannerText;

	private float _bannerStart;

	private float _bannerUntil;

	private Light _sun;

	private float _nextSunLook;

	private bool _hasSun;

	private float _sunBase;

	private float _sunWritten;

	private bool _hasAmbient;

	private Color _ambientBase;

	private Color _ambientWritten;

	private bool _hasFog;

	private Color _fogBase;

	private Color _fogWritten;

	internal float Strength => _strength;

	internal bool SoundPlaying => _sound != null && _sound.isPlaying;

	internal static RainVisuals Instance => _instance;

	private static void Enable()
	{
		_instance = Plugin.Root.AddComponent<RainVisuals>();
		Rain.Changed += (WeatherKind from, WeatherKind to, bool announce) =>
		{
			if (_instance == null || !announce || !Rain.Active)
			{
				return;
			}
			_instance.Announce(from, to);
		};
	}

	private void Announce(WeatherKind from, WeatherKind to)
	{
		if (to == WeatherKind.Rain)
		{
			ShowBanner("IT STARTS TO RAIN");
		}
		else if (to == WeatherKind.Snow)
		{
			ShowBanner("IT STARTS TO SNOW");
		}
		else if (from == WeatherKind.Rain)
		{
			ShowBanner("THE RAIN HAS STOPPED");
		}
		else if (from == WeatherKind.Snow)
		{
			ShowBanner("THE SNOW HAS STOPPED");
		}
	}

	private void Update()
	{
		WeatherKind kind = Rain.Active ? Rain.Current : WeatherKind.None;
		if (kind != WeatherKind.None)
		{
			_shown = kind;
		}
		float target = (kind != WeatherKind.None) ? 1f : 0f;
		if (!Rain.Active)
		{
			_strength = 0f;
		}
		else
		{
			_strength = Mathf.MoveTowards(_strength, target, Time.unscaledDeltaTime / FadeSeconds);
		}
		UpdateBanner();
		if (_strength <= 0f && _rain == null && _snow == null && _veil == null && _sound == null)
		{
			return;
		}
		UpdateParticles();
		UpdateVeil();
		UpdateSound();
	}

	// The light is turned down after the game's own scripts set it this frame.
	private void LateUpdate()
	{
		if (_strength <= 0f && !_hasSun && !_hasAmbient && !_hasFog)
		{
			return;
		}
		Dim();
	}

	private void OnDisable()
	{
		_strength = 0f;
		Dim();
		if (_rain != null)
		{
			_rain.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
		}
		if (_snow != null)
		{
			_snow.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
		}
		if (_sound != null)
		{
			_sound.Stop();
		}
		if (_veil != null)
		{
			_veil.gameObject.SetActive(false);
		}
		if (_banner != null)
		{
			_banner.SetActive(false);
		}
	}

	// ---- light ----

	private void Dim()
	{
		float k = _strength;
		// The sun: the directional light with the most light.
		if (_sun == null && Time.unscaledTime >= _nextSunLook)
		{
			_nextSunLook = Time.unscaledTime + 5f;
			_hasSun = false;
			float best = 0f;
			foreach (Light light in FindObjectsOfType<Light>())
			{
				if (light.type == LightType.Directional && light.enabled && light.intensity > best)
				{
					best = light.intensity;
					_sun = light;
				}
			}
		}
		if (_sun != null)
		{
			if (!_hasSun || !Mathf.Approximately(_sun.intensity, _sunWritten))
			{
				_sunBase = _sun.intensity;
				_hasSun = true;
			}
			_sunWritten = _sunBase * Mathf.Lerp(1f, SunLevel, k);
			_sun.intensity = _sunWritten;
		}
		else
		{
			_hasSun = false;
		}
		if (!_hasAmbient || RenderSettings.ambientLight != _ambientWritten)
		{
			_ambientBase = RenderSettings.ambientLight;
			_hasAmbient = true;
		}
		_ambientWritten = Dimmed(_ambientBase, Mathf.Lerp(1f, AmbientLevel, k));
		RenderSettings.ambientLight = _ambientWritten;
		if (!_hasFog || RenderSettings.fogColor != _fogWritten)
		{
			_fogBase = RenderSettings.fogColor;
			_hasFog = true;
		}
		_fogWritten = Color.Lerp(_fogBase, new Color(0.35f, 0.38f, 0.43f, _fogBase.a), 0.6f * k);
		RenderSettings.fogColor = _fogWritten;
		if (k <= 0f)
		{
			// Back to the game's own values: nothing left to keep track of.
			_hasSun = false;
			_hasAmbient = false;
			_hasFog = false;
		}
	}

	private static Color Dimmed(Color color, float level)
	{
		return new Color(color.r * level, color.g * level, color.b * level, color.a);
	}

	// ---- particles ----

	private void UpdateParticles()
	{
		Camera camera = Camera.main;
		bool wantRain = _shown == WeatherKind.Rain && _strength > 0f;
		bool wantSnow = _shown == WeatherKind.Snow && _strength > 0f;
		if (wantRain && _rain == null)
		{
			_rain = MakeParticles("CSP Rain", rain: true);
		}
		if (wantSnow && _snow == null)
		{
			_snow = MakeParticles("CSP Snow", rain: false);
		}
		Follow(_rain, wantRain, camera, 20f);
		Follow(_snow, wantSnow, camera, 14f);
	}

	private void Follow(ParticleSystem system, bool on, Camera camera, float above)
	{
		if (system == null)
		{
			return;
		}
		if (!on || camera == null)
		{
			if (system.isPlaying)
			{
				system.Stop(true, ParticleSystemStopBehavior.StopEmitting);
			}
			return;
		}
		// Over the ground the camera looks at (a point ahead of it), so the view is full of falling drops.
		Vector3 forward = camera.transform.forward;
		Vector3 centre = camera.transform.position + forward * 25f;
		system.transform.position = new Vector3(centre.x, Mathf.Max(centre.y, camera.transform.position.y - 20f) + above, centre.z);
		ParticleSystem.EmissionModule emission = system.emission;
		emission.rateOverTime = ((system == _rain) ? 4000f : 1500f) * _strength;
		if (!system.isPlaying)
		{
			system.Play();
		}
	}

	private ParticleSystem MakeParticles(string name, bool rain)
	{
		GameObject go = new GameObject(name);
		go.transform.SetParent(transform, false);
		// Emits downwards (a box of 70 x 70 blocks, thin) and keeps its own place in the world.
		go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
		ParticleSystem system = go.AddComponent<ParticleSystem>();
		system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
		ParticleSystem.MainModule main = system.main;
		main.loop = true;
		main.playOnAwake = false;
		main.prewarm = false;
		main.simulationSpace = ParticleSystemSimulationSpace.World;
		main.startSpeed = rain ? 34f : 3.5f;
		main.startLifetime = rain ? 1.3f : 8f;
		main.startSize = rain ? 0.035f : 0.14f;
		main.startColor = rain ? RainColor : SnowColor;
		main.maxParticles = rain ? 9000 : 6000;
		ParticleSystem.ShapeModule shape = system.shape;
		shape.enabled = true;
		shape.shapeType = ParticleSystemShapeType.Box;
		shape.scale = new Vector3(70f, 70f, 1f);
		if (!rain)
		{
			ParticleSystem.NoiseModule noise = system.noise;
			noise.enabled = true;
			noise.strength = 1.2f;
			noise.frequency = 0.25f;
			noise.damping = true;
		}
		ParticleSystemRenderer renderer = go.GetComponent<ParticleSystemRenderer>();
		renderer.renderMode = rain ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
		renderer.lengthScale = 1f;
		renderer.velocityScale = 0.03f;
		renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
		renderer.receiveShadows = false;
		Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
		renderer.material = new Material(shader);
		return system;
	}

	// ---- veil ----

	private void UpdateVeil()
	{
		if (_veil == null)
		{
			if (_strength <= 0f)
			{
				return;
			}
			// Under the game's windows (a negative sorting order), over the picture.
			GameObject canvas = UiKit.CreateCanvas("RainVeilCanvas", transform, -100);
			canvas.GetComponent<GraphicRaycaster>().enabled = false;
			RectTransform rect = UiKit.CreateRect("Veil", canvas.transform);
			UiKit.SetRect(rect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
			_veil = rect.gameObject.AddComponent<Image>();
			_veil.raycastTarget = false;
		}
		bool show = _strength > 0f;
		if (_veil.gameObject.activeSelf != show)
		{
			_veil.gameObject.SetActive(show);
		}
		Color color = VeilColor;
		color.a = VeilAlpha * _strength;
		_veil.color = color;
	}

	// ---- sound ----

	private void UpdateSound()
	{
		bool wantRain = _shown == WeatherKind.Rain && _strength > 0f;
		if (_sound == null)
		{
			if (!wantRain)
			{
				return;
			}
			_sound = gameObject.AddComponent<AudioSource>();
			_sound.clip = MakeRainClip();
			_sound.loop = true;
			_sound.spatialBlend = 0f;
			_sound.playOnAwake = false;
			_sound.ignoreListenerPause = false;
			if (FindObjectOfType<AudioListener>() == null)
			{
				// The game's sounds go through Wwise, which has its own listener: Unity's audio needs one to be heard.
				gameObject.AddComponent<AudioListener>();
			}
		}
		_sound.volume = wantRain ? RainVolume * _strength : 0f;
		if (wantRain && !_sound.isPlaying)
		{
			_sound.Play();
		}
		else if (!wantRain && _sound.isPlaying)
		{
			_sound.Stop();
		}
	}

	// Three seconds of rain: noise, low-passed to a hiss with the lowest rumble taken off, and now and then a drop;
	// the end is crossfaded into the start so the loop has no seam.
	private static AudioClip MakeRainClip()
	{
		int length = SampleRate * 3;
		int blend = SampleRate / 4;
		float[] raw = new float[length + blend];
		System.Random random = new System.Random(7);
		float fast = 0f;
		float slow = 0f;
		float drop = 0f;
		for (int i = 0; i < raw.Length; i++)
		{
			float noise = (float)(random.NextDouble() * 2.0 - 1.0);
			fast += 0.32f * (noise - fast);
			slow += 0.012f * (fast - slow);
			if (random.NextDouble() < 0.0012)
			{
				drop = (float)(0.4 + random.NextDouble() * 0.6);
			}
			drop *= 0.93f;
			raw[i] = (fast - slow) + drop * (float)(random.NextDouble() * 2.0 - 1.0) * 0.6f;
		}
		float[] data = new float[length];
		for (int i = 0; i < length; i++)
		{
			data[i] = (i < blend) ? Mathf.Lerp(raw[length + i], raw[i], i / (float)blend) : raw[i];
		}
		float peak = 0.0001f;
		foreach (float sample in data)
		{
			peak = Mathf.Max(peak, Mathf.Abs(sample));
		}
		for (int i = 0; i < length; i++)
		{
			data[i] = data[i] / peak * 0.8f;
		}
		AudioClip clip = AudioClip.Create("CSP Rain", length, 1, SampleRate, false);
		clip.SetData(data, 0);
		return clip;
	}

	// ---- message ----

	private void ShowBanner(string message)
	{
		if (_banner == null)
		{
			GameObject canvas = UiKit.CreateCanvas("RainBannerCanvas", transform, 940);
			canvas.GetComponent<GraphicRaycaster>().enabled = false;
			RectTransform rect = UiKit.CreateRect("Banner", canvas.transform);
			UiKit.SetRect(rect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -190f), new Vector2(1200f, 70f));
			_banner = rect.gameObject;
			_bannerText = UiKit.CreateText(rect, string.Empty, 44, BannerColor, TextAnchor.MiddleCenter);
			_bannerText.fontStyle = FontStyle.Bold;
			_bannerText.raycastTarget = false;
			UiKit.SetRect(_bannerText.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
			Outline outline = _bannerText.gameObject.AddComponent<Outline>();
			outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
			outline.effectDistance = new Vector2(3f, -3f);
		}
		_bannerText.text = message;
		_bannerText.color = BannerColor;
		_bannerStart = Time.unscaledTime;
		_bannerUntil = Time.unscaledTime + BannerSeconds;
		_banner.SetActive(true);
	}

	private void UpdateBanner()
	{
		if (_banner == null || !_banner.activeSelf)
		{
			return;
		}
		float left = _bannerUntil - Time.unscaledTime;
		if (left <= 0f)
		{
			_banner.SetActive(false);
			return;
		}
		float age = Time.unscaledTime - _bannerStart;
		_banner.transform.localScale = Vector3.one * (1f + 0.2f * Mathf.Max(0f, 1f - age * 4f));
		Color color = BannerColor;
		color.a = Mathf.Clamp01(left / 0.6f);
		_bannerText.color = color;
	}

	// The loudness of what Unity's audio sends out now (0 when nothing is heard).
	private static float OutputLevel()
	{
		float[] data = new float[512];
		AudioListener.GetOutputData(data, 0);
		float sum = 0f;
		foreach (float sample in data)
		{
			sum += sample * sample;
		}
		return Mathf.Sqrt(sum / data.Length);
	}

	// For the tests and the API: what is shown now.
	internal string Describe()
	{
		return "strength " + _strength.ToString("0.00") + ", rain particles " + ((_rain != null) ? _rain.particleCount : 0) + ", snow particles " + ((_snow != null) ? _snow.particleCount : 0) + ", veil " + ((_veil != null && _veil.gameObject.activeSelf) ? _veil.color.a.ToString("0.00") : "off") + ", sound " + (SoundPlaying ? _sound.volume.ToString("0.00") + " (output level " + OutputLevel().ToString("0.000") + ")" : "off") + ", banner " + (_banner != null && _banner.activeSelf ? _bannerText.text : "none");
	}
}
