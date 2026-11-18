using System.Collections.Generic;
using Brix.Game.AI;
using UnityEngine;

namespace CastleStoryPlus.Treasure;

// A beast's model on its unit: the unit's own meshes are emptied (kept to give back), the model follows the unit and is
// shown whenever the unit's body would be (fog, culling, level of detail), and it moves on its own:
// - a gait per kind from the unit's speed: the Spirestalker paces (the legs of a side together), the Cindermane walks
//   and gallops when running, the Boulderback knuckle-walks swaying, the Ironhorn trots heavily; standing, they
//   breathe and look about;
// - an attack per kind from the unit's combat state (wind-up while it cranks, the blow when it swings): the
//   Spirestalker rears its neck and whips it down, the Cindermane rears and pounces, the Boulderback raises both fists
//   and slams them down, the Ironhorn lowers its head and charges a few steps with its horn;
// - on a corpse it falls as a ragdoll and crumbles away when the corpse goes (BeastRagdoll).
// Every peer builds it from the synced variant and the unit's state; nothing of it is saved.
internal sealed class BeastBody : MonoBehaviour
{
	private sealed class Hidden
	{
		public SkinnedMeshRenderer Skinned;

		public MeshFilter Filter;

		public Mesh Mesh;
	}

	internal BeastInfo Info;

	internal bool Dead;

	private readonly List<Hidden> _hidden = new List<Hidden>();

	private Renderer _visibility;

	private GameObject _root;

	private Dictionary<string, Transform> _parts;

	private readonly Dictionary<Transform, Quaternion> _rest = new Dictionary<Transform, Quaternion>();

	// This frame's swing (about the side axis, radians; positive turns a leg's foot back, a head down) and turn (about
	// the up axis) of each part.
	private readonly Dictionary<string, float> _swing = new Dictionary<string, float>();

	private readonly Dictionary<string, float> _turn = new Dictionary<string, float>();

	private Labor _labor;

	private Vector3 _lastPosition;

	private float _phase;

	private float _speed;

	private float _nextHide;

	private float _wind;

	private float _strike;

	private Locomotion4.State _lastState;

	private BeastRagdoll _ragdoll;

	private BeastVoice _voice;

	// What it sounds like (GuardianSounds); null on a corpse.
	internal BeastVoice Voice => _voice;

	internal static BeastBody Attach(GameObject unit, BeastInfo info, bool dead)
	{
		BeastBody body = unit.GetComponent<BeastBody>();
		if (body != null && body.Info == info && body.Dead == dead)
		{
			return body;
		}
		if (body != null)
		{
			body.Detach();
		}
		body = unit.AddComponent<BeastBody>();
		body.Info = info;
		body.Dead = dead;
		body.Build();
		return body;
	}

	internal static void DetachFrom(GameObject unit)
	{
		BeastBody body = (unit != null) ? unit.GetComponent<BeastBody>() : null;
		if (body != null)
		{
			body.Detach();
		}
	}

	private void Build()
	{
		_labor = GetComponent<Labor>();
		Material template = null;
		foreach (SkinnedMeshRenderer skinned in GetComponentsInChildren<SkinnedMeshRenderer>(true))
		{
			if (_visibility == null || (skinned.sharedMesh != null && skinned.bounds.size.sqrMagnitude > _visibility.bounds.size.sqrMagnitude))
			{
				_visibility = skinned;
				template = skinned.sharedMaterial;
			}
		}
		_root = new GameObject("BeastModel");
		_root.transform.SetParent(transform, false);
		_parts = Info.Model.Build(_root.transform, template);
		if (_parts == null)
		{
			// No model: the unit keeps its own look.
			Destroy(_root);
			_root = null;
			return;
		}
		foreach (Transform part in _parts.Values)
		{
			_rest[part] = part.localRotation;
		}
		_lastPosition = transform.position;
		HideOriginal();
		if (Dead)
		{
			_ragdoll = BeastRagdoll.Create(_root.transform, _parts, gameObject);
			GuardianSounds.Play(Info, GuardianSound.Death, transform.position);
		}
		else
		{
			_voice = new BeastVoice(Info, _labor);
		}
	}

	// The unit's meshes (body and what it holds) are emptied; equipping something new brings a mesh back, so this is
	// repeated now and then.
	private void HideOriginal()
	{
		foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
		{
			if (renderer.transform.IsChildOf(_root.transform))
			{
				continue;
			}
			if (renderer is SkinnedMeshRenderer skinned)
			{
				if (skinned.sharedMesh != null)
				{
					_hidden.Add(new Hidden { Skinned = skinned, Mesh = skinned.sharedMesh });
					skinned.sharedMesh = null;
				}
			}
			else if (renderer is MeshRenderer)
			{
				MeshFilter filter = renderer.GetComponent<MeshFilter>();
				if (filter != null && filter.sharedMesh != null)
				{
					_hidden.Add(new Hidden { Filter = filter, Mesh = filter.sharedMesh });
					filter.sharedMesh = null;
				}
			}
		}
	}

	internal void Detach()
	{
		foreach (Hidden hidden in _hidden)
		{
			if (hidden.Skinned != null && hidden.Skinned.sharedMesh == null)
			{
				hidden.Skinned.sharedMesh = hidden.Mesh;
			}
			if (hidden.Filter != null && hidden.Filter.sharedMesh == null)
			{
				hidden.Filter.sharedMesh = hidden.Mesh;
			}
		}
		_hidden.Clear();
		if (_ragdoll != null)
		{
			// The corpse goes: the ragdoll lying in the world crumbles away.
			_ragdoll.Crumble();
		}
		else if (_root != null)
		{
			Destroy(_root);
		}
		_ragdoll = null;
		_root = null;
		Destroy(this);
	}

	// A pooled unit or corpse put away gets its own meshes back, so whatever reuses it looks right.
	private void OnDisable()
	{
		Detach();
	}

	private void LateUpdate()
	{
		if (_root == null)
		{
			return;
		}
		if (Time.time >= _nextHide)
		{
			_nextHide = Time.time + 0.5f;
			HideOriginal();
		}
		bool visible = _visibility == null || (_visibility.enabled && _visibility.gameObject.activeInHierarchy);
		if (_root.activeSelf != visible)
		{
			_root.SetActive(visible);
		}
		if (Dead)
		{
			return;
		}
		if (visible)
		{
			Animate();
		}
	}

	private void Animate()
	{
		float dt = Mathf.Max(Time.deltaTime, 0.0001f);
		Vector3 moved = transform.position - _lastPosition;
		moved.y = 0f;
		_lastPosition = transform.position;
		float distance = Mathf.Min(moved.magnitude, 2f);
		_speed = Mathf.Lerp(_speed, distance / dt, Mathf.Min(1f, dt * 8f));
		_phase += distance * Info.StepsPerBlock * Mathf.PI * 2f;
		Combat(dt);
		_voice?.Tick(transform.position, _phase, _speed, _lastState);
		_swing.Clear();
		_turn.Clear();
		float walk = Mathf.Clamp01(_speed / 1.2f);
		float t = Time.time;
		float s = Mathf.Sin(_phase);
		float pitch = 0f;
		float roll = 0f;
		float bob = Mathf.Sin(t * 1.6f) * 0.008f * (1f - walk);
		float lunge = 0f;
		// Standing, the head looks about now and then.
		Turn("Head", Mathf.Sin(t * 0.37f) * Mathf.Sin(t * 0.23f) * 0.45f * (1f - walk));
		Swing("Head", Mathf.Sin(t * 1.6f) * 0.03f);
		Turn("Tail", Mathf.Sin(t * 2.3f) * (0.25f + 0.2f * walk));
		switch (Info.Kind)
		{
			case BeastKind.Spirestalker:
				{
					// Pacing: the two legs of a side swing together.
					float a = s * 0.36f * walk;
					Swing("LegFL", a);
					Swing("LegBL", a);
					Swing("LegFR", -a);
					Swing("LegBR", -a);
					roll = s * 2.5f * walk;
					bob += Mathf.Abs(Mathf.Cos(_phase)) * 0.03f * walk;
					Swing("Head", Mathf.Sin(_phase * 2f) * 0.06f * walk);
					Swing("Head", -0.65f * _wind + 0.75f * _strike);
					pitch += 4f * _strike;
					break;
				}
			case BeastKind.Cindermane:
				{
					if (_speed > 2.4f)
					{
						// Gallop: the front legs together, the hind legs together half a stride later; the body rocks.
						float front = s * 0.75f;
						float hind = -Mathf.Sin(_phase + 0.7f) * 0.75f;
						Swing("LegFL", front);
						Swing("LegFR", front * 0.85f);
						Swing("LegBL", hind);
						Swing("LegBR", hind * 0.85f);
						pitch += Mathf.Sin(_phase + 0.3f) * 6f;
						bob += Mathf.Abs(Mathf.Sin(_phase)) * 0.09f;
					}
					else
					{
						float a = s * 0.45f * walk;
						Swing("LegFL", a);
						Swing("LegBR", a);
						Swing("LegFR", -a);
						Swing("LegBL", -a);
						bob += Mathf.Abs(Mathf.Sin(_phase)) * 0.035f * walk;
						Swing("Head", Mathf.Sin(_phase * 2f) * 0.05f * walk);
					}
					// Pounce: rears onto its hind legs, paws up, then leaps forward with a bite.
					pitch += -16f * _wind + 9f * _strike;
					bob += 0.1f * _wind + 0.12f * _strike;
					lunge += 0.5f * _strike;
					Swing("LegFL", -0.9f * _wind - 0.5f * _strike);
					Swing("LegFR", -0.9f * _wind - 0.5f * _strike);
					Swing("Head", 0.35f * _strike - 0.15f * _wind);
					break;
				}
			case BeastKind.Boulderback:
				{
					// Knuckle walk: each arm with the opposite leg, swaying from side to side.
					float a = s * 0.42f * walk;
					Swing("ArmL", a);
					Swing("LegBR", a * 0.8f);
					Swing("ArmR", -a);
					Swing("LegBL", -a * 0.8f);
					roll = s * 5f * walk;
					bob += Mathf.Abs(Mathf.Sin(_phase)) * 0.05f * walk;
					// Slam: both fists up over the head, then down on the ground in front.
					float arms = Mathf.Lerp(0f, -2.7f, _wind);
					arms = Mathf.Lerp(arms, -0.45f, _strike);
					Swing("ArmL", arms);
					Swing("ArmR", arms);
					pitch += -10f * _wind + 14f * _strike;
					bob += 0.06f * _wind - 0.06f * _strike;
					Swing("Head", -0.2f * _wind + 0.2f * _strike);
					break;
				}
			default:
				{
					// Heavy trot: diagonal pairs, a bounce every step.
					float a = s * 0.36f * walk;
					Swing("LegFL", a);
					Swing("LegBR", a);
					Swing("LegFR", -a);
					Swing("LegBL", -a);
					bob += Mathf.Abs(Mathf.Sin(_phase)) * 0.05f * walk;
					Swing("Head", Mathf.Sin(_phase * 2f) * 0.07f * walk);
					// Charge: head lowered, a few steps forward, the horn thrust up at the end.
					Swing("Head", 0.45f * _wind + (0.5f - 1.1f * (1f - _strike)) * _strike);
					lunge += 0.55f * _strike;
					pitch += 5f * _wind;
					break;
				}
		}
		_root.transform.localPosition = new Vector3(0f, bob, lunge);
		_root.transform.localRotation = Quaternion.Euler(pitch, 0f, roll);
		foreach (KeyValuePair<string, Transform> part in _parts)
		{
			_swing.TryGetValue(part.Key, out float swing);
			_turn.TryGetValue(part.Key, out float turn);
			part.Value.localRotation = _rest[part.Value] * Quaternion.Euler(swing * Mathf.Rad2Deg, turn * Mathf.Rad2Deg, 0f);
		}
	}

	// Wind-up while the unit cranks its blow; the blow when it swings, fading in about half a second.
	private void Combat(float dt)
	{
		Locomotion4 locomotion = (_labor != null && _labor.navigation != null) ? _labor.navigation.Locomotion : null;
		Locomotion4.State state = (locomotion != null) ? locomotion.state : Locomotion4.State.InPlace;
		bool cranking = state == Locomotion4.State.CombatCrank;
		bool striking = state == Locomotion4.State.CombatSwing || state == Locomotion4.State.CombatHit || state == Locomotion4.State.CombatMissed || state == Locomotion4.State.CombatBlocked;
		bool wasStriking = _lastState == Locomotion4.State.CombatSwing || _lastState == Locomotion4.State.CombatHit || _lastState == Locomotion4.State.CombatMissed || _lastState == Locomotion4.State.CombatBlocked;
		if (striking && !wasStriking)
		{
			_strike = 1f;
			_wind = Mathf.Max(_wind, 0.6f);
		}
		_lastState = state;
		_wind = Mathf.MoveTowards(_wind, cranking ? 1f : 0f, dt * (cranking ? 3f : 4f));
		_strike = Mathf.MoveTowards(_strike, 0f, dt * 2.2f);
	}

	private void Swing(string name, float radians)
	{
		_swing.TryGetValue(name, out float now);
		_swing[name] = now + radians;
	}

	private void Turn(string name, float radians)
	{
		_turn.TryGetValue(name, out float now);
		_turn[name] = now + radians;
	}
}
