using Brix.Game.AI;
using Brix.Game.AI.Damage;
using UnityEngine;

namespace CastleStoryPlus.Treasure;

// What one living guardian sounds like, on every peer, from what its body does (BeastBody calls Tick every frame while
// it is shown, so nothing is heard from a guardian in the fog):
// - footsteps: one per half stride of its gait (the legs of a diagonal pair or a side land together), as loud as it
//   walks fast;
// - an attack sound as the blow starts (the unit swings);
// - a hit sound when its health goes down;
// - a roar when a player's unit comes near (it has spotted it), when it is provoked (on the host, which alone knows the temper; other players hear the roar when it goes into combat) and when
//   it goes into combat, at most once every RoarSeconds;
// - an idle call now and then while it is not fighting.
// The death sound is played by BeastBody when the corpse appears. No allocation per frame: the state is a few fields.
internal sealed class BeastVoice
{
	// Blocks from which it notices a player's unit, seconds between its roars, and between its idle calls.
	private const float SpotRadius = 16f;

	private const float RoarSeconds = 25f;

	private const float HitSeconds = 0.3f;

	private const float MinStepSeconds = 0.12f;

	private readonly BeastInfo _info;

	private readonly Labor _labor;

	private DamageReceiver _health;

	private float _lastHp = -1f;

	private float _lastMax;

	private float _nextLookup;

	private float _nextHit;

	private float _nextIdle;

	private float _nextRoar;

	private float _roarAt = -1f;

	private float _nextWatch;

	private bool _spotted;

	private int _step;

	private bool _stepKnown;

	private float _nextStep;

	private Locomotion4.State _state;

	internal BeastVoice(BeastInfo info, Labor labor)
	{
		_info = info;
		_labor = labor;
		_nextIdle = Time.unscaledTime + Random.Range(4f, 16f);
	}

	// The host provoked this guardian: it roars after a short, random moment (a pack does not roar all at once).
	internal void Provoked()
	{
		_roarAt = Time.unscaledTime + Random.Range(0.15f, 1.1f);
	}

	// For the tests: the next idle call is due now.
	internal void IdleDue()
	{
		_nextIdle = Time.unscaledTime - 1f;
	}

	internal void Tick(Vector3 at, float phase, float speed, Locomotion4.State state)
	{
		if (!GuardianSounds.On)
		{
			return;
		}
		float now = Time.unscaledTime;
		bool fighting = state >= Locomotion4.State.Combat && state <= Locomotion4.State.CombatAbortStrike;
		bool wasFighting = _state >= Locomotion4.State.Combat && _state <= Locomotion4.State.CombatAbortStrike;
		bool striking = Striking(state);
		if (striking && !Striking(_state))
		{
			GuardianSounds.Play(_info, GuardianSound.Attack, at);
		}
		_state = state;
		// Limits are in real time; at a high game speed footsteps and calls would only be a flood.
		bool fast = Time.timeScale > 3f;
		Steps(at, phase, fast ? 0f : speed, now);
		Hurt(at, now);
		if (_roarAt >= 0f && now >= _roarAt)
		{
			_roarAt = -1f;
			Roar(at, now);
		}
		if (fighting && !wasFighting)
		{
			Roar(at, now);
		}
		if (now >= _nextWatch)
		{
			_nextWatch = now + 0.6f;
			bool near = GuardianSounds.PlayerNear(at, SpotRadius);
			if (near && !_spotted)
			{
				Roar(at, now);
			}
			_spotted = near;
		}
		if (now >= _nextIdle)
		{
			_nextIdle = now + Random.Range(14f, 35f);
			if (!fighting && !fast)
			{
				GuardianSounds.Play(_info, GuardianSound.Idle, at);
			}
		}
	}

	private static bool Striking(Locomotion4.State state)
	{
		return state == Locomotion4.State.CombatSwing || state == Locomotion4.State.CombatHit || state == Locomotion4.State.CombatMissed || state == Locomotion4.State.CombatBlocked;
	}

	private void Roar(Vector3 at, float now)
	{
		if (now < _nextRoar)
		{
			return;
		}
		if (GuardianSounds.Play(_info, GuardianSound.Roar, at))
		{
			_nextRoar = now + RoarSeconds;
		}
	}

	// A step for every half stride, when it walks (the unit's body animation counts strides by distance walked).
	private void Steps(Vector3 at, float phase, float speed, float now)
	{
		int step = Mathf.FloorToInt(phase / Mathf.PI);
		if (!_stepKnown)
		{
			_stepKnown = true;
			_step = step;
			return;
		}
		if (step == _step)
		{
			return;
		}
		_step = step;
		if (speed > 0.25f && now >= _nextStep)
		{
			_nextStep = now + MinStepSeconds;
			GuardianSounds.Play(_info, GuardianSound.Step, at, Mathf.Lerp(0.6f, 1f, Mathf.Clamp01(speed / 3f)));
		}
	}

	// Health going down is a hit.
	private void Hurt(Vector3 at, float now)
	{
		if (_health == null && _labor != null && now >= _nextLookup)
		{
			_nextLookup = now + 1f;
			_health = _labor.GetComponent<BricktronDamageReceiver>();
		}
		if (_health == null || _health.MaxHP <= 0f)
		{
			return;
		}
		float hp = _health.CurrentHP;
		if (_lastHp >= 0f && Mathf.Approximately(_lastMax, _health.MaxHP) && hp < _lastHp - 0.001f && hp > 0f && now >= _nextHit)
		{
			_nextHit = now + HitSeconds;
			GuardianSounds.Play(_info, GuardianSound.Hit, at);
		}
		_lastHp = hp;
		_lastMax = _health.MaxHP;
	}
}
