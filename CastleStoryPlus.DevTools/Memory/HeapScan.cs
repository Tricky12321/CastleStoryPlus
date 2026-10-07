using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Memory;

// What fills the managed heap: walks every object reachable from the static fields of all loaded types and from
// every MonoBehaviour/ScriptableObject, estimates each object's size (64-bit Mono layout) and adds it up per type and
// per root (the root that reached it first). Runs a few milliseconds per frame (time-sliced) so the game keeps
// running; the numbers are a snapshot of a moving heap, so they are close, not exact.
internal class HeapScan : MonoBehaviour
{
	// One object waiting to be visited: the root that reached it and the field (or array) that held it.
	private struct Entry
	{
		public object Obj;

		public int Root;

		public string Via;
	}

	// What the scan knows about a type: its size, the fields worth following and the totals found so far.
	private sealed class TypeData
	{
		public string Name;

		public bool IsString;

		public bool IsArray;

		public bool Leaf;

		// Value types: the size of the value itself (inline in a field or array). Classes: 8 (a reference).
		public int InlineSize;

		// Size as an object of its own on the heap (classes and boxed values); arrays and strings are computed.
		public int HeapSize;

		public FieldInfo[] RefFields;

		public string[] RefVia;

		// Struct fields that hold references somewhere inside.
		public FieldInfo[] StructFields;

		public bool HasRefs;

		public int ElementSize;

		// 0: nothing to follow, 1: reference elements, 2: struct elements holding references.
		public int ElementKind;

		public TypeData ElementData;

		public string ArrayVia;

		public long Count;

		public long Bytes;
	}

	internal sealed class TypeRow
	{
		public string Name;

		public long Count;

		public long Bytes;
	}

	internal sealed class RootRow
	{
		public string Label;

		public long Count;

		public long Bytes;
	}

	internal sealed class BigObject
	{
		public string Type;

		public long Bytes;

		public int Length;

		public string Via;

		public string Root;
	}

	internal sealed class Result
	{
		public DateTime Finished;

		public double Seconds;

		public long Objects;

		public long Bytes;

		public int Roots;

		public int TypesSeen;

		public int Failures;

		public long GcStart;

		public long GcEnd;

		public long MonoUsedStart;

		public long MonoUsedEnd;

		public long MonoHeapEnd;

		public List<TypeRow> Types;

		public List<RootRow> RootRows;

		public List<BigObject> Biggest;
	}

	private sealed class RefComparer : IEqualityComparer<object>
	{
		public new bool Equals(object a, object b)
		{
			return ReferenceEquals(a, b);
		}

		public int GetHashCode(object obj)
		{
			return RuntimeHelpers.GetHashCode(obj);
		}
	}

	private const int MaxBiggest = 40;

	private const int MaxRows = 1000;

	private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

	private const BindingFlags StaticFields = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

	private static HeapScan _instance;

	internal static bool Running;

	internal static string Phase = "idle";

	internal static long Objects;

	internal static long Bytes;

	internal static int Failures;

	internal static int RootsDone;

	internal static int RootsTotal;

	internal static int MsPerFrame = 40;

	internal static Result Last;

	private static readonly Stopwatch Clock = new Stopwatch();

	private readonly Stopwatch _frame = new Stopwatch();

	private readonly Dictionary<Type, TypeData> _types = new Dictionary<Type, TypeData>();

	private HashSet<object> _visited;

	private Stack<Entry> _stack;

	private List<string> _rootLabels;

	private List<long> _rootBytes;

	private List<long> _rootCounts;

	private List<BigObject> _biggest;

	private long _biggestMin;

	private int _steps;

	internal static double ElapsedSeconds
	{
		get { return Clock.Elapsed.TotalSeconds; }
	}

	internal static int StackDepth
	{
		get { return (_instance != null && _instance._stack != null) ? _instance._stack.Count : 0; }
	}

	private void Awake()
	{
		_instance = this;
	}

	// Starts a scan; false when one is already running.
	internal static bool StartScan(int msPerFrame)
	{
		if (Running || _instance == null)
		{
			return false;
		}
		MsPerFrame = Math.Max(5, msPerFrame);
		Running = true;
		_instance.StartCoroutine(_instance.Run());
		return true;
	}

	private IEnumerator Run()
	{
		Phase = "types";
		Objects = 0;
		Bytes = 0;
		Failures = 0;
		RootsDone = 0;
		RootsTotal = 0;
		Clock.Reset();
		Clock.Start();
		RestartFrame();
		_steps = 0;
		_visited = new HashSet<object>(new RefComparer());
		_stack = new Stack<Entry>();
		_rootLabels = new List<string>();
		_rootBytes = new List<long>();
		_rootCounts = new List<long>();
		_biggest = new List<BigObject>();
		_biggestMin = 0;
		foreach (TypeData data in _types.Values)
		{
			data.Count = 0;
			data.Bytes = 0;
		}
		long gcStart = GC.GetTotalMemory(false);
		long monoUsedStart = SafeMonoUsed();

		// Every static field of every loaded type (open generic types have no statics of their own).
		List<FieldInfo> statics = new List<FieldInfo>();
		foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			if (!Scanned(assembly))
			{
				continue;
			}
			foreach (Type type in LoadableTypes(assembly))
			{
				if (type == null || type.ContainsGenericParameters || type == typeof(HeapScan))
				{
					continue;
				}
				FieldInfo[] fields;
				try
				{
					fields = type.GetFields(StaticFields);
				}
				catch (Exception)
				{
					Failures++;
					continue;
				}
				foreach (FieldInfo field in fields)
				{
					// Thread and context statics crash Unity's Mono when read by reflection.
					if (!field.IsLiteral && !field.IsDefined(typeof(ThreadStaticAttribute), false) && !field.IsDefined(typeof(ContextStaticAttribute), false))
					{
						statics.Add(field);
					}
				}
			}
			if (_frame.ElapsedMilliseconds > MsPerFrame)
			{
				yield return null;
				RestartFrame();
			}
		}

		UnityEngine.Object[] behaviours = Resources.FindObjectsOfTypeAll(typeof(MonoBehaviour));
		UnityEngine.Object[] scriptables = Resources.FindObjectsOfTypeAll(typeof(ScriptableObject));
		RootsTotal = statics.Count + behaviours.Length + scriptables.Length;

		// Each root is followed to the end before the next, so an object counts for the first root that reaches it.
		Phase = "statics";
		foreach (FieldInfo field in statics)
		{
			RootsDone++;
			object value;
			try
			{
				value = field.GetValue(null);
			}
			catch (Exception)
			{
				Failures++;
				continue;
			}
			if (value == null)
			{
				continue;
			}
			string label = TypeName(field.DeclaringType, true) + "." + field.Name;
			int root = AddRoot(label);
			if (field.FieldType.IsValueType)
			{
				// A static struct lives outside the heap; only what it points to counts.
				try
				{
					WalkFields(value, Data(value.GetType()), root);
				}
				catch (Exception)
				{
					Failures++;
				}
			}
			else
			{
				Push(value, root, label);
			}
			while (!Drain())
			{
				yield return null;
				RestartFrame();
			}
		}

		// The game's components and assets, one root per type.
		Phase = "unity";
		Dictionary<string, int> unityRoots = new Dictionary<string, int>();
		List<UnityEngine.Object> unityObjects = new List<UnityEngine.Object>(behaviours.Length + scriptables.Length);
		unityObjects.AddRange(behaviours);
		unityObjects.AddRange(scriptables);
		behaviours = null;
		scriptables = null;
		for (int i = 0; i < unityObjects.Count; i++)
		{
			RootsDone++;
			UnityEngine.Object unityObject = unityObjects[i];
			unityObjects[i] = null;
			if (ReferenceEquals(unityObject, null) || unityObject is HeapScan)
			{
				continue;
			}
			string label = "unity: " + TypeName(unityObject.GetType(), true);
			if (!unityRoots.TryGetValue(label, out int root))
			{
				root = AddRoot(label);
				unityRoots[label] = root;
			}
			Push(unityObject, root, "unity");
			while (!Drain())
			{
				yield return null;
				RestartFrame();
			}
		}

		Finish(gcStart, monoUsedStart);
	}

	private void Finish(long gcStart, long monoUsedStart)
	{
		Clock.Stop();
		Result result = new Result
		{
			Finished = DateTime.Now,
			Seconds = Clock.Elapsed.TotalSeconds,
			Objects = Objects,
			Bytes = Bytes,
			Roots = _rootLabels.Count,
			TypesSeen = 0,
			Failures = Failures,
			GcStart = gcStart,
			GcEnd = GC.GetTotalMemory(false),
			MonoUsedStart = monoUsedStart,
			MonoUsedEnd = SafeMonoUsed(),
			MonoHeapEnd = SafeMonoHeap(),
			Types = new List<TypeRow>(),
			RootRows = new List<RootRow>(),
			Biggest = _biggest
		};
		foreach (TypeData data in _types.Values)
		{
			if (data.Count > 0)
			{
				result.TypesSeen++;
				result.Types.Add(new TypeRow { Name = data.Name, Count = data.Count, Bytes = data.Bytes });
			}
		}
		result.Types.Sort((TypeRow a, TypeRow b) => b.Bytes.CompareTo(a.Bytes));
		if (result.Types.Count > MaxRows)
		{
			result.Types.RemoveRange(MaxRows, result.Types.Count - MaxRows);
		}
		for (int i = 0; i < _rootLabels.Count; i++)
		{
			if (_rootCounts[i] > 0)
			{
				result.RootRows.Add(new RootRow { Label = _rootLabels[i], Count = _rootCounts[i], Bytes = _rootBytes[i] });
			}
		}
		result.RootRows.Sort((RootRow a, RootRow b) => b.Bytes.CompareTo(a.Bytes));
		if (result.RootRows.Count > MaxRows)
		{
			result.RootRows.RemoveRange(MaxRows, result.RootRows.Count - MaxRows);
		}
		result.Biggest.Sort((BigObject a, BigObject b) => b.Bytes.CompareTo(a.Bytes));

		// Let go of everything the scan held, so it can be collected again.
		_visited = null;
		_stack = null;
		_rootLabels = null;
		_rootBytes = null;
		_rootCounts = null;
		_biggest = null;
		Last = result;
		Phase = "done";
		Running = false;
		Log(result);
	}

	private static void Log(Result result)
	{
		StringBuilder text = new StringBuilder();
		text.Append("Heap scan: ").Append(result.Objects).Append(" objects, ").Append(Mb(result.Bytes)).Append(" MB reachable in ")
			.Append(result.Seconds.ToString("0.0")).Append(" s (GC.GetTotalMemory ").Append(Mb(result.GcEnd)).Append(" MB, Mono used ")
			.Append(Mb(result.MonoUsedEnd)).Append(" MB, ").Append(result.Failures).Append(" failures)");
		text.Append("\n  Top types:");
		for (int i = 0; i < result.Types.Count && i < 15; i++)
		{
			TypeRow row = result.Types[i];
			text.Append("\n    ").Append(Mb(row.Bytes)).Append(" MB  ").Append(row.Count).Append("x  ").Append(row.Name);
		}
		text.Append("\n  Top roots:");
		for (int i = 0; i < result.RootRows.Count && i < 15; i++)
		{
			RootRow row = result.RootRows[i];
			text.Append("\n    ").Append(Mb(row.Bytes)).Append(" MB  ").Append(row.Count).Append("x  ").Append(row.Label);
		}
		DevToolsPlugin.Log.LogInfo(text.ToString());
	}

	internal static string Mb(long bytes)
	{
		return (bytes / 1048576.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
	}

	private void RestartFrame()
	{
		_frame.Reset();
		_frame.Start();
	}

	private static IEnumerable<Type> LoadableTypes(Assembly assembly)
	{
		try
		{
			return assembly.GetTypes();
		}
		catch (ReflectionTypeLoadException ex)
		{
			return ex.Types;
		}
		catch (Exception)
		{
			Failures++;
			return new Type[0];
		}
	}

	private int AddRoot(string label)
	{
		_rootLabels.Add(label);
		_rootBytes.Add(0);
		_rootCounts.Add(0);
		return _rootLabels.Count - 1;
	}

	private void Push(object value, int root, string via)
	{
		if (value == null || !_visited.Add(value))
		{
			return;
		}
		_stack.Push(new Entry { Obj = value, Root = root, Via = via });
	}

	// Visits objects until the stack is empty (true) or this frame's time is used up (false).
	private bool Drain()
	{
		while (_stack.Count > 0)
		{
			if ((++_steps & 1023) == 0 && _frame.ElapsedMilliseconds > MsPerFrame)
			{
				return false;
			}
			Visit(_stack.Pop());
		}
		return true;
	}

	private void Visit(Entry entry)
	{
		object obj = entry.Obj;
		TypeData data = Data(obj.GetType());
		long size;
		int length = -1;
		if (data.IsString)
		{
			size = Round8(20L + 2L * ((string)obj).Length);
		}
		else if (data.IsArray)
		{
			length = ((Array)obj).Length;
			size = Round8(32L + (long)length * data.ElementSize);
		}
		else
		{
			size = data.HeapSize;
		}
		data.Count++;
		data.Bytes += size;
		Objects++;
		Bytes += size;
		_rootBytes[entry.Root] += size;
		_rootCounts[entry.Root]++;
		if (size > _biggestMin || _biggest.Count < MaxBiggest)
		{
			AddBiggest(data, size, length, entry);
		}
		if (data.Leaf || data.IsString)
		{
			return;
		}
		try
		{
			if (data.IsArray)
			{
				WalkArray((Array)obj, data, entry.Root);
			}
			else if (data.HasRefs)
			{
				WalkFields(obj, data, entry.Root);
			}
		}
		catch (Exception)
		{
			Failures++;
		}
	}

	private void WalkArray(Array array, TypeData data, int root)
	{
		if (data.ElementKind == 1)
		{
			object[] refs = array as object[];
			if (refs != null)
			{
				for (int i = 0; i < refs.Length; i++)
				{
					Push(refs[i], root, data.ArrayVia);
				}
			}
			else
			{
				// Multi-dimensional arrays.
				foreach (object element in array)
				{
					Push(element, root, data.ArrayVia);
				}
			}
		}
		else if (data.ElementKind == 2)
		{
			foreach (object element in array)
			{
				WalkFields(element, data.ElementData, root);
			}
		}
	}

	// Follows the references of an object or a boxed struct; structs inside are opened in place.
	private void WalkFields(object holder, TypeData data, int root)
	{
		for (int i = 0; i < data.RefFields.Length; i++)
		{
			Push(data.RefFields[i].GetValue(holder), root, data.RefVia[i]);
		}
		for (int i = 0; i < data.StructFields.Length; i++)
		{
			object value = data.StructFields[i].GetValue(holder);
			if (value != null)
			{
				WalkFields(value, Data(data.StructFields[i].FieldType), root);
			}
		}
	}

	private void AddBiggest(TypeData data, long size, int length, Entry entry)
	{
		BigObject big = new BigObject { Type = data.Name, Bytes = size, Length = length, Via = entry.Via, Root = _rootLabels[entry.Root] };
		if (_biggest.Count < MaxBiggest)
		{
			_biggest.Add(big);
		}
		else
		{
			int smallest = 0;
			for (int i = 1; i < _biggest.Count; i++)
			{
				if (_biggest[i].Bytes < _biggest[smallest].Bytes)
				{
					smallest = i;
				}
			}
			_biggest[smallest] = big;
		}
		if (_biggest.Count >= MaxBiggest)
		{
			long min = long.MaxValue;
			foreach (BigObject b in _biggest)
			{
				min = Math.Min(min, b.Bytes);
			}
			_biggestMin = min;
		}
	}

	private static long Round8(long size)
	{
		return (size + 7) & ~7L;
	}

	// The cached layout of a type (built on first sight).
	private TypeData Data(Type type)
	{
		if (_types.TryGetValue(type, out TypeData data))
		{
			return data;
		}
		data = new TypeData { Name = TypeName(type, true), RefFields = new FieldInfo[0], RefVia = new string[0], StructFields = new FieldInfo[0] };
		_types[type] = data;
		if (type == typeof(string))
		{
			data.IsString = true;
			data.InlineSize = 8;
			return data;
		}
		if (type.IsArray)
		{
			data.IsArray = true;
			data.InlineSize = 8;
			Type elementType = type.GetElementType();
			if (elementType.IsPointer)
			{
				data.ElementSize = 8;
			}
			else if (!elementType.IsValueType)
			{
				data.ElementSize = 8;
				data.ElementKind = 1;
			}
			else
			{
				TypeData element = Data(elementType);
				data.ElementSize = element.InlineSize;
				if (element.HasRefs)
				{
					data.ElementKind = 2;
					data.ElementData = element;
				}
			}
			data.ArrayVia = "[]" + TypeName(type, false);
			return data;
		}
		if (type.IsValueType)
		{
			int primitive = PrimitiveSize(type);
			if (primitive > 0)
			{
				data.InlineSize = primitive;
				data.HeapSize = (int)Round8(16 + primitive);
				return data;
			}
		}
		else
		{
			data.InlineSize = 8;
		}
		data.Leaf = typeof(MemberInfo).IsAssignableFrom(type) || typeof(Assembly).IsAssignableFrom(type) || typeof(Module).IsAssignableFrom(type);

		// Instance fields of the type and all its bases.
		int fieldBytes = 0;
		List<FieldInfo> refs = new List<FieldInfo>();
		List<string> via = new List<string>();
		List<FieldInfo> structs = new List<FieldInfo>();
		for (Type level = type; level != null && level != typeof(object) && level != typeof(ValueType); level = level.BaseType)
		{
			foreach (FieldInfo field in level.GetFields(InstanceFields))
			{
				Type fieldType = field.FieldType;
				if (fieldType.IsPointer)
				{
					fieldBytes += 8;
				}
				else if (!fieldType.IsValueType)
				{
					fieldBytes += 8;
					refs.Add(field);
					via.Add(TypeName(level, false) + "." + field.Name);
				}
				else
				{
					TypeData inner = Data(fieldType);
					fieldBytes += inner.InlineSize;
					if (inner.HasRefs)
					{
						structs.Add(field);
					}
				}
			}
		}
		data.RefFields = refs.ToArray();
		data.RefVia = via.ToArray();
		data.StructFields = structs.ToArray();
		data.HasRefs = data.RefFields.Length > 0 || data.StructFields.Length > 0;
		if (type.IsValueType)
		{
			data.InlineSize = Math.Max(1, fieldBytes);
			data.HeapSize = (int)Round8(16 + data.InlineSize);
		}
		else
		{
			data.HeapSize = (int)Round8(16 + fieldBytes);
		}
		return data;
	}

	// Size of the primitives, enums and the few special value types; 0 for ordinary structs.
	private static int PrimitiveSize(Type type)
	{
		if (type.IsEnum)
		{
			return PrimitiveSize(Enum.GetUnderlyingType(type));
		}
		if (type == typeof(decimal))
		{
			return 16;
		}
		if (!type.IsPrimitive)
		{
			return 0;
		}
		if (type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte))
		{
			return 1;
		}
		if (type == typeof(char) || type == typeof(short) || type == typeof(ushort))
		{
			return 2;
		}
		if (type == typeof(int) || type == typeof(uint) || type == typeof(float))
		{
			return 4;
		}
		// long, ulong, double, IntPtr, UIntPtr.
		return 8;
	}

	// Readable type names: Dictionary<Int32,List<String>> instead of the assembly-qualified generic name.
	internal static string TypeName(Type type, bool withNamespace)
	{
		if (type.IsArray)
		{
			return TypeName(type.GetElementType(), withNamespace) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
		}
		string name = type.Name;
		if (type.IsNested && type.DeclaringType != null)
		{
			name = TypeName(type.DeclaringType, withNamespace) + "+" + name;
		}
		else if (withNamespace && !string.IsNullOrEmpty(type.Namespace))
		{
			name = type.Namespace + "." + name;
		}
		if (!type.IsGenericType)
		{
			return name;
		}
		int tick = name.LastIndexOf('`');
		if (tick >= 0)
		{
			name = name.Substring(0, tick);
		}
		Type[] arguments = type.GetGenericArguments();
		StringBuilder text = new StringBuilder(name).Append('<');
		for (int i = 0; i < arguments.Length; i++)
		{
			if (i > 0)
			{
				text.Append(',');
			}
			text.Append(TypeName(arguments[i], false));
		}
		return text.Append('>').ToString();
	}

	// Statics of the game, its libraries and the mods only. Reading a static field runs its type's static
	// constructor, and the runtime's and Unity's own (mscorlib, System, Mono, UnityEngine) can crash the game
	// that way (a native crash in mono_field_get_value_object, the first scan).
	private static bool Scanned(Assembly assembly)
	{
		string name = assembly.GetName().Name;
		foreach (string prefix in SkippedAssemblies)
		{
			if (name == prefix || name.StartsWith(prefix + ".", StringComparison.Ordinal))
			{
				return false;
			}
		}
		return true;
	}

	private static readonly string[] SkippedAssemblies = new string[] { "mscorlib", "System", "Mono", "UnityEngine", "UnityEditor", "Unity", "Boo", "UnityScript", "nunit", "0Harmony", "HarmonyX", "MonoMod", "BepInEx", "Microsoft" };

	private static long SafeMonoUsed()
	{
		try
		{
			return MonoUsed();
		}
		catch (Exception)
		{
			return 0;
		}
	}

	private static long SafeMonoHeap()
	{
		try
		{
			return MonoHeap();
		}
		catch (Exception)
		{
			return 0;
		}
	}

	// Kept in methods of their own, so a Unity without them fails only here (when the call is compiled).
	private static long MonoUsed()
	{
		return UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
	}

	private static long MonoHeap()
	{
		return UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong();
	}
}
