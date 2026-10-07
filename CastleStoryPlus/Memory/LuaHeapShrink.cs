using System;
using System.Collections.Generic;
using CastleStoryPlus.Core;
using HarmonyLib;
using MoonSharp.Interpreter;

namespace CastleStoryPlus.Memory;

// The game's MoonSharp keeps every Lua value that a closure captures in one static array of 10,485,760 DynValues
// (HeapAllocatedDynValue), made full size when Lua first runs: 240 MB of references, plus 40 MB of counts. The game
// uses a small part of it, but every garbage collection reads the whole array for references, which makes each
// collection's pause longer (the heap scan found it to be a quarter of what a collection walks). Here the arrays
// start at 1,048,576 slots and double when the slots run out, as a List does.
[Feature(Features.LuaHeapShrink, Features.LuaHeapShrinkInfo)]
internal static class LuaHeapShrink
{
	private const int StartSlots = 1 << 20;

	private static void Enable()
	{
		Traverse heap = Traverse.Create(typeof(HeapAllocatedDynValue));
		Stack<int> empty = heap.Field("_emptySlots").GetValue<Stack<int>>();
		lock (empty)
		{
			DynValue[] values = heap.Field("_heapAllocatedDynValue").GetValue<DynValue[]>();
			int[] counts = heap.Field("_refCount").GetValue<int[]>();
			int used = heap.Field("_size").GetValue<int>();
			int slots = StartSlots;
			while (slots < used * 2)
			{
				slots *= 2;
			}
			if (slots >= values.Length)
			{
				return;
			}
			heap.Field("_heapAllocatedDynValue").SetValue(Copy(values, slots, used));
			heap.Field("_refCount").SetValue(Copy(counts, slots, used));
			Plugin.Log.LogInfo("LuaHeapShrink: Lua value heap " + values.Length + " slots -> " + slots + " (" + used + " used)");
		}
	}

	private static T[] Copy<T>(T[] from, int slots, int used)
	{
		T[] to = new T[slots];
		Array.Copy(from, to, used);
		return to;
	}

	// Room for count more slots past the end (Allocate takes a freed slot first, else the next one past _size).
	internal static void Ensure(int count, Stack<int> empty)
	{
		lock (empty)
		{
			Traverse heap = Traverse.Create(typeof(HeapAllocatedDynValue));
			DynValue[] values = heap.Field("_heapAllocatedDynValue").GetValue<DynValue[]>();
			int used = heap.Field("_size").GetValue<int>();
			int needed = used + Math.Max(0, count - empty.Count);
			if (needed <= values.Length)
			{
				return;
			}
			int slots = values.Length;
			while (slots < needed)
			{
				slots *= 2;
			}
			heap.Field("_heapAllocatedDynValue").SetValue(Copy(values, slots, used));
			heap.Field("_refCount").SetValue(Copy(heap.Field("_refCount").GetValue<int[]>(), slots, used));
			Plugin.Log.LogInfo("LuaHeapShrink: Lua value heap grown to " + slots + " slots");
		}
	}
}

// Only when the free slots are used up and _size is at the arrays' end does a slot need room: checked without
// reflection, so the common call costs two field reads.
[Feature(Features.LuaHeapShrink, Features.LuaHeapShrinkInfo)]
[HarmonyPatch(typeof(HeapAllocatedDynValue), nameof(HeapAllocatedDynValue.Allocate), new Type[0])]
internal static class LuaHeapShrinkAllocatePatch
{
	private static void Prefix(int ____size, DynValue[] ____heapAllocatedDynValue, Stack<int> ____emptySlots)
	{
		if (____emptySlots.Count == 0 && ____size >= ____heapAllocatedDynValue.Length)
		{
			LuaHeapShrink.Ensure(1, ____emptySlots);
		}
	}
}

[Feature(Features.LuaHeapShrink, Features.LuaHeapShrinkInfo)]
[HarmonyPatch(typeof(HeapAllocatedDynValue), nameof(HeapAllocatedDynValue.Allocate), new Type[] { typeof(int[]) })]
internal static class LuaHeapShrinkAllocateManyPatch
{
	private static void Prefix(int[] array, int ____size, DynValue[] ____heapAllocatedDynValue, Stack<int> ____emptySlots)
	{
		if (array != null && ____size + Math.Max(0, array.Length - ____emptySlots.Count) > ____heapAllocatedDynValue.Length)
		{
			LuaHeapShrink.Ensure(array.Length, ____emptySlots);
		}
	}
}
