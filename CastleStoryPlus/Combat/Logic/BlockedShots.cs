using System.Collections.Generic;

namespace CastleStoryPlus.Combat;

// Which targets a ranged fighter found it could not shoot from where it stands, and for how long it leaves them be, as
// plain logic with no call into the game or Unity, so it can be tested without the game (CastleStoryPlus.UnitTests).
// ArcherRetarget holds one of these and does the game side (#165).
//
// A target is blocked for one fighter on one block (the cell it stood on when the shot failed): from any other block
// the fighter tries it afresh, and a test from another block (RangedReposition tries places by moving the fighter
// there for a moment) neither sees nor forgets the entry. The first block lasts FirstHold seconds; each further block
// on the same cell before a shot gets off doubles it, up to MaxHold, so a target that stays out of sight is tried
// less and less often while the fighter shoots others. A shot fired at the target forgets it; an entry not renewed
// for ForgetAfter seconds after its hold ran out is dropped.
internal sealed class BlockedShots<TShooter, TTarget> where TShooter : class where TTarget : class
{
	public const float FirstHold = 4f;

	public const float MaxHold = 16f;

	public const float ForgetAfter = 30f;

	private struct Key : System.IEquatable<Key>
	{
		public TShooter Shooter;

		public TTarget Target;

		public bool Equals(Key other)
		{
			return EqualityComparer<TShooter>.Default.Equals(Shooter, other.Shooter) && EqualityComparer<TTarget>.Default.Equals(Target, other.Target);
		}

		public override bool Equals(object obj)
		{
			return obj is Key other && Equals(other);
		}

		public override int GetHashCode()
		{
			int shooter = (Shooter != null) ? Shooter.GetHashCode() : 0;
			int target = (Target != null) ? Target.GetHashCode() : 0;
			return shooter * 397 ^ target;
		}
	}

	private sealed class Entry
	{
		public int X;

		public int Y;

		public int Z;

		public int Strikes;

		public float Until;
	}

	private readonly Dictionary<Key, Entry> _entries = new Dictionary<Key, Entry>();

	private readonly List<Key> _gone = new List<Key>();

	public int Count => _entries.Count;

	// How long the target is left be after its strikes-th block in a row on the same cell.
	public static float Hold(int strikes)
	{
		float hold = FirstHold;
		for (int i = 1; i < strikes && hold < MaxHold; i++)
		{
			hold *= 2f;
		}
		return (hold < MaxHold) ? hold : MaxHold;
	}

	// The shooter, standing on the cell (x, y, z), could not shoot the target: it is left be for a while.
	public void Block(TShooter shooter, TTarget target, int x, int y, int z, float now)
	{
		if (shooter == null || target == null)
		{
			return;
		}
		if (_entries.Count >= 256)
		{
			Prune(now);
		}
		Key key = new Key { Shooter = shooter, Target = target };
		if (!_entries.TryGetValue(key, out Entry entry))
		{
			entry = new Entry();
			_entries[key] = entry;
		}
		if (entry.Strikes > 0 && entry.X == x && entry.Y == y && entry.Z == z)
		{
			entry.Strikes++;
		}
		else
		{
			entry.Strikes = 1;
		}
		entry.X = x;
		entry.Y = y;
		entry.Z = z;
		entry.Until = now + Hold(entry.Strikes);
	}

	// Whether the shooter, standing on the cell (x, y, z), leaves the target be now.
	public bool IsBlocked(TShooter shooter, TTarget target, int x, int y, int z, float now)
	{
		if (_entries.Count == 0 || shooter == null || target == null)
		{
			return false;
		}
		Key key = new Key { Shooter = shooter, Target = target };
		if (!_entries.TryGetValue(key, out Entry entry))
		{
			return false;
		}
		if (now >= entry.Until + ForgetAfter)
		{
			_entries.Remove(key);
			return false;
		}
		return now < entry.Until && entry.X == x && entry.Y == y && entry.Z == z;
	}

	// A shot at the target got off: it is tried as soon as asked again.
	public void Forget(TShooter shooter, TTarget target)
	{
		if (_entries.Count == 0 || shooter == null || target == null)
		{
			return;
		}
		_entries.Remove(new Key { Shooter = shooter, Target = target });
	}

	public void Prune(float now)
	{
		_gone.Clear();
		foreach (KeyValuePair<Key, Entry> pair in _entries)
		{
			if (now >= pair.Value.Until + ForgetAfter)
			{
				_gone.Add(pair.Key);
			}
		}
		foreach (Key key in _gone)
		{
			_entries.Remove(key);
		}
		_gone.Clear();
	}

	public void Clear()
	{
		_entries.Clear();
	}
}
