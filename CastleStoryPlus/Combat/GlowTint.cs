using System.Collections.Generic;
using CastleStoryPlus.UI;
using UnityEngine;

namespace CastleStoryPlus.Combat;

// Tints an object one colour (materials and their pictures, particles, trails and lights) and puts its own colours
// back: a healing bolt green, a critical hit's arrow or bolt red (a critical sword is coloured by UpgradeVisuals).
// Projectiles are pooled, so the colours are kept per object and restored when it is reused for an ordinary shot.
internal class GlowTint : MonoBehaviour
{
	internal static readonly Color Green = new Color(0.25f, 1f, 0.35f, 1f);

	internal static readonly Color Red = new Color(1f, 0.15f, 0.1f, 1f);

	private static readonly string[] ColorProperties = new string[3] { "_Color", "_TintColor", "_EmissionColor" };

	private readonly List<KeyValuePair<Material, KeyValuePair<string, Color>>> _materials = new List<KeyValuePair<Material, KeyValuePair<string, Color>>>();

	// The pictures of the object's materials: the game's own shaders mostly have no colour property, so the picture
	// itself is swapped for a recoloured copy (TextureRecolour, shared) while tinted.
	private readonly List<KeyValuePair<Material, KeyValuePair<string, Texture>>> _textures = new List<KeyValuePair<Material, KeyValuePair<string, Texture>>>();

	// How much brighter than the picture the recoloured copy is, so the colour shows on dark wood and feathers.
	private const float PictureBoost = 1.6f;

	private readonly List<KeyValuePair<ParticleSystem, ParticleSystem.MinMaxGradient>> _particles = new List<KeyValuePair<ParticleSystem, ParticleSystem.MinMaxGradient>>();

	private readonly List<KeyValuePair<ParticleSystem, bool>> _particleFades = new List<KeyValuePair<ParticleSystem, bool>>();

	private readonly List<KeyValuePair<TrailRenderer, KeyValuePair<Color, Color>>> _trails = new List<KeyValuePair<TrailRenderer, KeyValuePair<Color, Color>>>();

	private readonly List<KeyValuePair<Light, Color>> _lights = new List<KeyValuePair<Light, Color>>();

	private bool _captured;

	// The colour shown, null for the object's own.
	private Color? _tint;

	// The bolt and its trail (a pooled object of its own, ArrowTrailer).
	internal static void ApplyBolt(GameObject bolt, Color? tint)
	{
		Apply(bolt, tint);
		ArrowTrailer trailer = (bolt != null) ? bolt.GetComponent<ArrowTrailer>() : null;
		if (trailer != null && trailer.trail != null)
		{
			Apply(trailer.trail.gameObject, tint);
		}
	}

	internal static void Apply(GameObject go, Color? tint)
	{
		if (go == null)
		{
			return;
		}
		GlowTint glow = go.GetComponent<GlowTint>();
		if (glow == null)
		{
			if (tint == null)
			{
				return;
			}
			glow = go.AddComponent<GlowTint>();
		}
		glow.Set(tint);
	}

	private void Set(Color? tint)
	{
		if (tint == _tint)
		{
			return;
		}
		if (!_captured)
		{
			Capture();
		}
		_tint = tint;
		bool tinted = tint.HasValue;
		Color color = tint.GetValueOrDefault();
		foreach (KeyValuePair<Material, KeyValuePair<string, Color>> entry in _materials)
		{
			if (entry.Key != null)
			{
				Color original = entry.Value.Value;
				entry.Key.SetColor(entry.Value.Key, tinted ? Recolor(original, color) : original);
			}
		}
		foreach (KeyValuePair<Material, KeyValuePair<string, Texture>> entry in _textures)
		{
			if (entry.Key != null)
			{
				Texture original = entry.Value.Value;
				Texture2D recoloured = tinted ? TextureRecolour.Recoloured(original, color * PictureBoost, false, "glow") : null;
				entry.Key.SetTexture(entry.Value.Key, (recoloured != null) ? recoloured : original);
			}
		}
		foreach (KeyValuePair<ParticleSystem, ParticleSystem.MinMaxGradient> entry in _particles)
		{
			if (entry.Key != null)
			{
				ParticleSystem.MainModule main = entry.Key.main;
				main.startColor = tinted ? new ParticleSystem.MinMaxGradient(color) : entry.Value;
				if (tinted)
				{
					RecolorAlive(entry.Key, color);
				}
			}
		}
		// A colour over lifetime gradient would tint the particles back; it is turned off while tinted.
		foreach (KeyValuePair<ParticleSystem, bool> entry in _particleFades)
		{
			if (entry.Key != null)
			{
				ParticleSystem.ColorOverLifetimeModule fade = entry.Key.colorOverLifetime;
				fade.enabled = !tinted && entry.Value;
			}
		}
		foreach (KeyValuePair<TrailRenderer, KeyValuePair<Color, Color>> entry in _trails)
		{
			if (entry.Key != null)
			{
				entry.Key.startColor = tinted ? Recolor(entry.Value.Key, color) : entry.Value.Key;
				entry.Key.endColor = tinted ? Recolor(entry.Value.Value, color) : entry.Value.Value;
			}
		}
		foreach (KeyValuePair<Light, Color> entry in _lights)
		{
			if (entry.Key != null)
			{
				entry.Key.color = tinted ? color : entry.Value;
			}
		}
	}

	private void Capture()
	{
		_captured = true;
		foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
		{
			if (renderer is ParticleSystemRenderer || IsForeignTrail(renderer))
			{
				continue;
			}
			foreach (Material material in renderer.materials)
			{
				CaptureMaterial(material, picture: true);
			}
		}
		foreach (ParticleSystem system in GetComponentsInChildren<ParticleSystem>(true))
		{
			_particles.Add(new KeyValuePair<ParticleSystem, ParticleSystem.MinMaxGradient>(system, system.main.startColor));
			_particleFades.Add(new KeyValuePair<ParticleSystem, bool>(system, system.colorOverLifetime.enabled));
			ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
			if (renderer != null)
			{
				// Particles are tinted by their start colour; their picture stays.
				foreach (Material material in renderer.materials)
				{
					CaptureMaterial(material, picture: false);
				}
			}
		}
		foreach (TrailRenderer trail in GetComponentsInChildren<TrailRenderer>(true))
		{
			if (IsForeignTrail(trail))
			{
				continue;
			}
			_trails.Add(new KeyValuePair<TrailRenderer, KeyValuePair<Color, Color>>(trail, new KeyValuePair<Color, Color>(trail.startColor, trail.endColor)));
		}
		foreach (Light light in GetComponentsInChildren<Light>(true))
		{
			_lights.Add(new KeyValuePair<Light, Color>(light, light.color));
		}
	}

	// A bolt's trail hangs under the bolt only while it flies; it is tinted on its own.
	private bool IsForeignTrail(Renderer renderer)
	{
		ArrowTrailer trailer = GetComponent<ArrowTrailer>();
		return trailer != null && trailer.trail != null && renderer == trailer.trail;
	}

	// Materials of this object only (renderer.materials makes them its own), so the shared ones stay untouched.
	private void CaptureMaterial(Material material, bool picture)
	{
		if (material == null)
		{
			return;
		}
		string textureName = picture ? TextureRecolour.TextureProperty(material) : null;
		if (textureName != null)
		{
			_textures.Add(new KeyValuePair<Material, KeyValuePair<string, Texture>>(material, new KeyValuePair<string, Texture>(textureName, material.GetTexture(textureName))));
		}
		foreach (string property in ColorProperties)
		{
			if (material.HasProperty(property))
			{
				_materials.Add(new KeyValuePair<Material, KeyValuePair<string, Color>>(material, new KeyValuePair<string, Color>(property, material.GetColor(property))));
			}
		}
	}

	// The tint with the original brightness and transparency.
	private static Color Recolor(Color original, Color tint)
	{
		float brightness = Mathf.Max(original.r, Mathf.Max(original.g, original.b));
		return new Color(tint.r * brightness, tint.g * brightness, tint.b * brightness, original.a);
	}

	private static void RecolorAlive(ParticleSystem system, Color tint)
	{
		ParticleSystem.Particle[] particles = new ParticleSystem.Particle[system.particleCount];
		int count = system.GetParticles(particles);
		for (int i = 0; i < count; i++)
		{
			Color32 color = particles[i].startColor;
			particles[i].startColor = new Color32((byte)(tint.r * 255f), (byte)(tint.g * 255f), (byte)(tint.b * 255f), color.a);
		}
		system.SetParticles(particles, count);
	}
}
