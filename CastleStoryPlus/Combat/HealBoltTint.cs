using System.Collections.Generic;
using UnityEngine;

namespace CastleStoryPlus.Combat;

// Tints a projectile green (materials, particles, trails and lights) and puts its own colours back. Projectiles are
// pooled, so the colours are kept per object and restored when it is reused for an ordinary shot.
internal class HealBoltTint : MonoBehaviour
{
	internal static readonly Color Green = new Color(0.25f, 1f, 0.35f, 1f);

	private static readonly string[] ColorProperties = new string[3] { "_Color", "_TintColor", "_EmissionColor" };

	private readonly List<KeyValuePair<Material, KeyValuePair<string, Color>>> _materials = new List<KeyValuePair<Material, KeyValuePair<string, Color>>>();

	private readonly List<KeyValuePair<ParticleSystem, ParticleSystem.MinMaxGradient>> _particles = new List<KeyValuePair<ParticleSystem, ParticleSystem.MinMaxGradient>>();

	private readonly List<KeyValuePair<ParticleSystem, bool>> _particleFades = new List<KeyValuePair<ParticleSystem, bool>>();

	private readonly List<KeyValuePair<TrailRenderer, KeyValuePair<Color, Color>>> _trails = new List<KeyValuePair<TrailRenderer, KeyValuePair<Color, Color>>>();

	private readonly List<KeyValuePair<Light, Color>> _lights = new List<KeyValuePair<Light, Color>>();

	private bool _captured;

	private bool _green;

	// The bolt and its trail (a pooled object of its own, ArrowTrailer).
	internal static void ApplyBolt(GameObject bolt, bool green)
	{
		Apply(bolt, green);
		ArrowTrailer trailer = (bolt != null) ? bolt.GetComponent<ArrowTrailer>() : null;
		if (trailer != null && trailer.trail != null)
		{
			Apply(trailer.trail.gameObject, green);
		}
	}

	internal static void Apply(GameObject go, bool green)
	{
		if (go == null)
		{
			return;
		}
		HealBoltTint tint = go.GetComponent<HealBoltTint>();
		if (tint == null)
		{
			if (!green)
			{
				return;
			}
			tint = go.AddComponent<HealBoltTint>();
		}
		tint.Set(green);
	}

	private void Set(bool green)
	{
		if (green == _green)
		{
			return;
		}
		if (!_captured)
		{
			Capture();
		}
		_green = green;
		foreach (KeyValuePair<Material, KeyValuePair<string, Color>> entry in _materials)
		{
			if (entry.Key != null)
			{
				Color original = entry.Value.Value;
				entry.Key.SetColor(entry.Value.Key, green ? Recolor(original) : original);
			}
		}
		foreach (KeyValuePair<ParticleSystem, ParticleSystem.MinMaxGradient> entry in _particles)
		{
			if (entry.Key != null)
			{
				ParticleSystem.MainModule main = entry.Key.main;
				main.startColor = green ? new ParticleSystem.MinMaxGradient(Green) : entry.Value;
				if (green)
				{
					RecolorAlive(entry.Key);
				}
			}
		}
		// A colour over lifetime gradient would tint the green particles back; it is turned off while green.
		foreach (KeyValuePair<ParticleSystem, bool> entry in _particleFades)
		{
			if (entry.Key != null)
			{
				ParticleSystem.ColorOverLifetimeModule fade = entry.Key.colorOverLifetime;
				fade.enabled = !green && entry.Value;
			}
		}
		foreach (KeyValuePair<TrailRenderer, KeyValuePair<Color, Color>> entry in _trails)
		{
			if (entry.Key != null)
			{
				entry.Key.startColor = green ? Recolor(entry.Value.Key) : entry.Value.Key;
				entry.Key.endColor = green ? Recolor(entry.Value.Value) : entry.Value.Value;
			}
		}
		foreach (KeyValuePair<Light, Color> entry in _lights)
		{
			if (entry.Key != null)
			{
				entry.Key.color = green ? Green : entry.Value;
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
				CaptureMaterial(material);
			}
		}
		foreach (ParticleSystem system in GetComponentsInChildren<ParticleSystem>(true))
		{
			_particles.Add(new KeyValuePair<ParticleSystem, ParticleSystem.MinMaxGradient>(system, system.main.startColor));
			_particleFades.Add(new KeyValuePair<ParticleSystem, bool>(system, system.colorOverLifetime.enabled));
			ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
			if (renderer != null)
			{
				foreach (Material material in renderer.materials)
				{
					CaptureMaterial(material);
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
	private void CaptureMaterial(Material material)
	{
		if (material == null)
		{
			return;
		}
		foreach (string property in ColorProperties)
		{
			if (material.HasProperty(property))
			{
				_materials.Add(new KeyValuePair<Material, KeyValuePair<string, Color>>(material, new KeyValuePair<string, Color>(property, material.GetColor(property))));
			}
		}
	}

	// Green with the original brightness and transparency.
	private static Color Recolor(Color original)
	{
		float brightness = Mathf.Max(original.r, Mathf.Max(original.g, original.b));
		return new Color(Green.r * brightness, Green.g * brightness, Green.b * brightness, original.a);
	}

	private static void RecolorAlive(ParticleSystem system)
	{
		ParticleSystem.Particle[] particles = new ParticleSystem.Particle[system.particleCount];
		int count = system.GetParticles(particles);
		for (int i = 0; i < count; i++)
		{
			Color32 color = particles[i].startColor;
			particles[i].startColor = new Color32((byte)(Green.r * 255f), (byte)(Green.g * 255f), (byte)(Green.b * 255f), color.a);
		}
		system.SetParticles(particles, count);
	}
}
