using System.Collections.Generic;

namespace CastleStoryPlus.DevTools.Pathing;

// A binary min-heap of items by integer priority (.NET 3.5 has no priority queue). The same item may be pushed
// again with a lower priority; the reader skips the ones it has already handled.
internal class MinHeap<T>
{
	private readonly List<KeyValuePair<int, T>> _items = new List<KeyValuePair<int, T>>();

	public int Count => _items.Count;

	public void Push(T item, int priority)
	{
		_items.Add(new KeyValuePair<int, T>(priority, item));
		int child = _items.Count - 1;
		while (child > 0)
		{
			int parent = (child - 1) / 2;
			if (_items[parent].Key <= _items[child].Key)
			{
				break;
			}
			Swap(parent, child);
			child = parent;
		}
	}

	public T Pop(out int priority)
	{
		KeyValuePair<int, T> top = _items[0];
		int last = _items.Count - 1;
		_items[0] = _items[last];
		_items.RemoveAt(last);
		int parent = 0;
		while (true)
		{
			int left = parent * 2 + 1;
			if (left >= _items.Count)
			{
				break;
			}
			int right = left + 1;
			int smallest = (right < _items.Count && _items[right].Key < _items[left].Key) ? right : left;
			if (_items[parent].Key <= _items[smallest].Key)
			{
				break;
			}
			Swap(parent, smallest);
			parent = smallest;
		}
		priority = top.Key;
		return top.Value;
	}

	private void Swap(int a, int b)
	{
		KeyValuePair<int, T> item = _items[a];
		_items[a] = _items[b];
		_items[b] = item;
	}
}
