using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Brix.External.Signals;
using Brix.Utils.Collections;
using CastleStoryPlus.Core;
using HarmonyLib;

namespace CastleStoryPlus.Performance;

// Every signal of the game (SignalBase._actions) keeps its connections in a StructLinkedList, which made two
// List<int> of its own (_dataIndices, _freeIndices) when it was created. Most signals are never connected to: in a
// running base some 700 000 of these lists stood empty, a ninth of all managed objects, and Mono's garbage collector
// looks at every one of them on each collection. A new linked list now starts with one shared empty list in both
// fields and gets its own two when its first node is added (Alloc, the only place that writes to them first). Lists
// made before the patch keep their own.
// Nothing writes to the shared list: before the first Alloc the lists are only read (Count, the indexer of an empty
// list throws before Release writes), and Clear() on it only clears an empty list. A list still sharing is told by
// both fields holding the same object, which two lists of its own never do.
// The patches are made under an id of their own that ModReload does not remove: lists made by the patched
// constructor would otherwise get the shared one written to by an unpatched Alloc. A reloaded copy finds them in place.
[Feature(Features.FrameSavers, Features.FrameSaversInfo)]
internal static class LazySignalLists
{
	private const string HarmonyId = Plugin.Guid + ".lazylists";

	// The game assemblies whose fields and types name the linked lists to patch.
	private static readonly string[] Assemblies = { "Brix.Core", "Assembly-CSharp-firstpass", "Assembly-CSharp" };

	// Both lists of every linked list not yet added to. Never written.
	private static readonly List<int> Shared = new List<int>();

	private static readonly FieldInfo SharedField = AccessTools.Field(typeof(LazySignalLists), nameof(Shared));

	private static readonly ConstructorInfo NewList = typeof(List<int>).GetConstructor(Type.EmptyTypes);

	private static void Enable()
	{
		if (Harmony.HasAnyPatches(HarmonyId))
		{
			Plugin.Log.LogInfo("LazySignalLists: already patched by an earlier copy of the mod");
			return;
		}
		Harmony harmony = new Harmony(HarmonyId);
		HarmonyMethod prefix = new HarmonyMethod(typeof(LazySignalLists), nameof(AllocPrefix));
		HarmonyMethod transpiler = new HarmonyMethod(typeof(LazySignalLists), nameof(ConstructorTranspiler));
		int patched = 0;
		int failed = 0;
		foreach (Type list in ListTypes())
		{
			try
			{
				MethodInfo alloc = AccessTools.Method(list, "Alloc");
				ConstructorInfo constructor = list.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
				if (alloc == null || constructor == null)
				{
					throw new MissingMethodException(list.FullName, alloc == null ? "Alloc" : ".ctor");
				}
				// Alloc first: a constructor sharing the list without the Alloc that swaps it out would write to it.
				harmony.Patch(alloc, prefix: prefix);
				harmony.Patch(constructor, transpiler: transpiler);
				patched++;
			}
			catch (Exception ex)
			{
				failed++;
				Plugin.Log.LogWarning("LazySignalLists: " + list.FullName + " not patched: " + ex.Message);
			}
		}
		Plugin.Log.LogInfo("LazySignalLists: " + patched + " linked list types start with shared empty lists" + (failed > 0 ? ", " + failed + " failed" : string.Empty));
	}

	// The constructor's two 'new List<int>()' load the shared list instead.
	private static IEnumerable<CodeInstruction> ConstructorTranspiler(IEnumerable<CodeInstruction> instructions)
	{
		List<CodeInstruction> code = new List<CodeInstruction>(instructions);
		int replaced = 0;
		for (int i = 0; i < code.Count; i++)
		{
			CodeInstruction instruction = code[i];
			if (instruction.opcode != OpCodes.Newobj || !NewList.Equals(instruction.operand))
			{
				continue;
			}
			CodeInstruction load = new CodeInstruction(OpCodes.Ldsfld, SharedField);
			load.labels.AddRange(instruction.labels);
			load.blocks.AddRange(instruction.blocks);
			code[i] = load;
			replaced++;
		}
		if (replaced != 2)
		{
			throw new InvalidOperationException("expected 2 new List<int>() in the constructor, found " + replaced);
		}
		return code;
	}

	// Before the first node: the list gets two lists of its own.
	private static void AllocPrefix(ref List<int> ____dataIndices, ref List<int> ____freeIndices)
	{
		if (ReferenceEquals(____dataIndices, ____freeIndices))
		{
			____dataIndices = new List<int>();
			____freeIndices = new List<int>();
		}
	}

	// Each closed StructLinkedList the game's types name: a signal's _actions (through the signal's own type, a
	// field's type, a base type or a type argument of one) and fields of a linked list type.
	private static List<Type> ListTypes()
	{
		List<Type> lists = new List<Type>();
		HashSet<Type> seen = new HashSet<Type>();
		foreach (string name in Assemblies)
		{
			Assembly assembly = Find(name);
			if (assembly == null)
			{
				Plugin.Log.LogWarning("LazySignalLists: assembly " + name + " not found");
				continue;
			}
			Type[] types;
			try
			{
				types = assembly.GetTypes();
			}
			catch (ReflectionTypeLoadException ex)
			{
				types = ex.Types;
			}
			foreach (Type type in types)
			{
				if (type == null)
				{
					continue;
				}
				try
				{
					Collect(type, lists, seen);
					foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
					{
						Collect(field.FieldType, lists, seen);
					}
				}
				catch (Exception)
				{
					// A type naming one that does not load; its other fields are not needed.
				}
			}
		}
		return lists;
	}

	private static void Collect(Type type, List<Type> lists, HashSet<Type> seen)
	{
		if (type == null || type.IsGenericParameter || !seen.Add(type))
		{
			return;
		}
		if (type.IsArray || type.IsByRef || type.IsPointer)
		{
			Collect(type.GetElementType(), lists, seen);
			return;
		}
		if (type.IsGenericType && !type.IsGenericTypeDefinition)
		{
			Type definition = type.GetGenericTypeDefinition();
			if (!type.ContainsGenericParameters)
			{
				if (definition == typeof(StructLinkedList<>))
				{
					lists.Add(type);
				}
				else if (definition == typeof(SignalBase<>))
				{
					Collect(AccessTools.Field(type, "_actions")?.FieldType, lists, seen);
				}
			}
			foreach (Type argument in type.GetGenericArguments())
			{
				Collect(argument, lists, seen);
			}
		}
		Collect(type.BaseType, lists, seen);
	}

	private static Assembly Find(string name)
	{
		foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			if (assembly.GetName().Name == name)
			{
				return assembly;
			}
		}
		try
		{
			return Assembly.Load(name);
		}
		catch (Exception)
		{
			return null;
		}
	}
}
