using System.Collections.Generic;

namespace CastleStoryPlus.GameUi;

// Which things are folded away (minimized), remembered per thing: the selected task's popup over the task list keeps
// one state per task (TaskPopupFold), so minimizing the crystal's popup leaves every other task's popup open. Pure
// logic; unit tests: CastleStoryPlus.UnitTests/GameUi.
internal sealed class FoldMemory<TKey> where TKey : class
{
	private readonly HashSet<TKey> _folded = new HashSet<TKey>();

	// Things never folded (and null, nothing selected) are open.
	internal bool IsFolded(TKey key)
	{
		return key != null && _folded.Contains(key);
	}

	internal void Set(TKey key, bool folded)
	{
		if (key == null)
		{
			return;
		}
		if (folded)
		{
			_folded.Add(key);
		}
		else
		{
			_folded.Remove(key);
		}
	}

	// Folds an open thing and opens a folded one; the new state.
	internal bool Toggle(TKey key)
	{
		bool folded = !IsFolded(key);
		Set(key, folded);
		return IsFolded(key);
	}

	internal int Count
	{
		get { return _folded.Count; }
	}

	internal void Clear()
	{
		_folded.Clear();
	}
}
