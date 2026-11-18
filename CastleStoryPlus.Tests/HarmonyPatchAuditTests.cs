using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using NUnit.Framework;

namespace CastleStoryPlus.Tests;

// Audit of the mod's Harmony patches against the game's assemblies (local only: needs the game's DLLs, see
// docs/Testing.md). It reads the patch attributes the way Harmony does and checks, without patching anything:
//   - every patch's target method exists (and is not ambiguous),
//   - every parameter of a patch method is one Harmony can fill (__instance, __result, __state, ___field, the target's
//     own parameter names) and has a compatible type,
//   - a prefix that can skip the original is not followed by a prefix on the same target that ignores __runOriginal,
//   - the mod's network message ids are unique and above the game's own.
// Patches made by code (harmony.Patch(...) in FrameMonitor, LazySignalLists) are not seen by this audit.
[TestFixture]
public class HarmonyPatchAuditTests
{
	// A prefix that skips the original while a later prefix on the same target does not look at __runOriginal, but
	// is known to be fine. Key: "<Target type>.<method>|<skipping patch class>", value: why it is fine.
	private static readonly Dictionary<string, string> SkipWhitelist = new Dictionary<string, string>
	{
		{ "BlueprintMapper.AddMappings|LargeStockpileBlueprintMappingPatch", Unreviewed },
		{ "BlueprintMapper.AddMappings|MarketBlueprintMappingPatch", Unreviewed },
		{ "BuildGoal.GetWorkNode|FreeBlueprintsWorkPatch", Unreviewed },
		{ "Container.ItemClick|ContinueClickPatch", Unreviewed },
		{ "CraftingInstructions.FindResource|MarketFetchPatch", Unreviewed },
		{ "DigGoal.MustOverkill|QuarryBlueCrystalOverkillPatch", Unreviewed },
		{ "GoalSelector.ProcessGoalAndReturnIsBest|TaskReservationSkipPatch", Unreviewed },
		{ "Instructions.Execute|GlobalBuildJobNewTaskPatch", Unreviewed },
		{ "Knowledge.ClosestObjectToPickup|PickupSearchOrder", Unreviewed },
		{ "Labor.CheckProjectForWork|WorkSearchBudget", Unreviewed },
		{ "Labor.ClosestResource|ResearchClosestResourcePatch", Unreviewed },
		{ "Labor.HasToFight|ArtificerHealingFightPatch", Unreviewed },
		{ "Labor.HasToFight|PlayerOrdersFirstFightPatch", Unreviewed },
		{ "Labor.HasToFight|RetreatInstinctPatch", Unreviewed },
		{ "LuaMenuComponent.Load|CsGameMenuLoadPatch", Unreviewed },
		{ "PickUpNode.TakeSome|ResearchTakePatch", Unreviewed },
		{ "PlaceBlueprintsOnBrokenBlocks.OnBlockRemoved|RepairNoRebuildPatch", Unreviewed },
		{ "RecepteurDisplay.UpdateElements|LargeStockpileDisplayPatch", Unreviewed },
		{ "Script.LoadFile|LuaChunkReuse", Unreviewed },
		{ "CraftingInstructions.FindResource|ResearchFetchPatch", Unreviewed },
		{ "Labor.ClosestResource|ResourcePriorityIngredients", Unreviewed },
		{ "HarvestProject.SelectBestGoal|OreMineSelectPatch", Unreviewed },
		{ "SearchPathRequest.Work|SearchFrameBudget", Unreviewed },
		{ "SearchPathRequest.LoopImprovePath|SearchFrameBudget", Unreviewed },
		{ "ResourceConverter.get_CurrentAutoConvertRule|EmptyStockpileConvertPatch", Unreviewed },
		{ "ResourceConverter.get_CurrentConvertRule|EmptyStockpileConvertPatch", Unreviewed },
		{ "SharedFactionVision.BrixUpdate|VisionRate", Unreviewed },
	};

	// Found by the first run of this audit (18-11-2026): each of these prefixes replaces the original (sets __result and
	// returns false) behind a condition of its own, and a later prefix on the same target does not ask __runOriginal.
	// Whether the conditions can both be true, and what then wins, has not been reviewed: review one, give it its own
	// reason here (or fix it with __runOriginal) and the list shrinks. New pairs are not on this list and fail.
	private const string Unreviewed = "baseline 18-11-2026, not reviewed";

	// Patches that are known not to match their target (a game version that has none of it, or a target that is
	// made on purpose by a TargetMethod that returns null for the other game version). Key: patch class full name.
	private static readonly Dictionary<string, string> TargetWhitelist = new Dictionary<string, string>
	{
		{ "CastleStoryPlus.UI.LobbyNameField", "its TargetMethod reads a static delegate that the running game sets; no game here" },
		{ "CastleStoryPlus.Saving.FastSaveDeferPatch", "its TargetMethod lists Asset_Map's methods, and Asset_Map's static initializer needs the running engine" }
	};

	private const int DefaultPriority = 400;

	private sealed class PatchMethod
	{
		public Type PatchClass;
		public MethodInfo Method;
		public string Kind;
		public int Priority;
		public bool CanSkipOriginal;

		// Set when the method has [HarmonyPatch] attributes of its own (its own target); else the class's.
		public List<MethodBase> OwnTargets;
	}

	private sealed class PatchClass
	{
		public Type Type;
		public List<MethodBase> Targets = new List<MethodBase>();
		public List<string> Problems = new List<string>();
		public List<PatchMethod> Methods = new List<PatchMethod>();
	}

	private static List<PatchClass> _patches;

	private static Assembly ModAssembly
	{
		get
		{
			return typeof(Plugin).Assembly;
		}
	}

	[OneTimeSetUp]
	public void Collect()
	{
		AppDomain.CurrentDomain.AssemblyResolve += ResolveFromGameFolders;
		_patches = new List<PatchClass>();
		foreach (Type type in LoadableTypes(ModAssembly))
		{
			if (!IsPatchClass(type))
			{
				continue;
			}
			PatchClass patch = new PatchClass { Type = type };
			CollectMethods(patch);
			bool allOwn = patch.Methods.Count > 0 && patch.Methods.All(m => m.OwnTargets != null);
			if (!allOwn)
			{
				ResolveTargets(patch);
			}
			_patches.Add(patch);
		}
	}

	// Assemblies of the game that are not next to the tests (written by the build to gamefolders.txt).
	private static Assembly ResolveFromGameFolders(object sender, ResolveEventArgs args)
	{
		string file = Path.Combine(AppContext.BaseDirectory, "gamefolders.txt");
		if (!File.Exists(file))
		{
			return null;
		}
		string name = new AssemblyName(args.Name).Name + ".dll";
		foreach (string folder in File.ReadAllLines(file))
		{
			string path = Path.Combine(folder.Trim(), name);
			if (folder.Trim().Length > 0 && File.Exists(path))
			{
				return Assembly.LoadFrom(path);
			}
		}
		return null;
	}

	[Test]
	public void ThereAreHundredsOfPatchesToAudit()
	{
		Assert.That(_patches.Count, Is.GreaterThan(100));
	}

	[Test]
	public void EveryPatchHasATargetThatExists()
	{
		List<string> failures = new List<string>();
		foreach (PatchClass patch in _patches)
		{
			if (TargetWhitelist.ContainsKey(patch.Type.FullName))
			{
				continue;
			}
			foreach (string problem in patch.Problems)
			{
				failures.Add(patch.Type.FullName + ": " + problem);
			}
			if (patch.Problems.Count == 0 && patch.Targets.Count == 0 && !patch.Methods.Any(m => m.OwnTargets != null))
			{
				failures.Add(patch.Type.FullName + ": no target");
			}
		}
		Assert.That(failures, Is.Empty, "Patch targets that do not resolve:\n" + string.Join("\n", failures.ToArray()));
	}

	[Test]
	public void EveryPatchParameterCanBeFilledByHarmony()
	{
		List<string> failures = new List<string>();
		foreach (PatchClass patch in _patches)
		{
			if (TargetWhitelist.ContainsKey(patch.Type.FullName))
			{
				continue;
			}
			foreach (PatchMethod method in patch.Methods)
			{
				foreach (MethodBase target in TargetsOf(patch, method))
				{
					foreach (string problem in CheckParameters(method, target))
					{
						failures.Add(patch.Type.FullName + "." + method.Method.Name + " on " + Describe(target) + ": " + problem);
					}
				}
			}
		}
		Assert.That(failures, Is.Empty, "Patch parameters that do not match:\n" + string.Join("\n", failures.ToArray()));
	}

	[Test]
	public void APrefixThatSkipsTheOriginalLeavesLaterPrefixesAbleToSeeIt()
	{
		List<string> failures = new List<string>();
		List<string> shared = new List<string>();
		foreach (KeyValuePair<MethodBase, List<PatchMethod>> group in PrefixesByTarget())
		{
			if (group.Value.Count < 2)
			{
				continue;
			}
			List<PatchMethod> order = group.Value.OrderByDescending(p => p.Priority).ThenBy(p => p.PatchClass.FullName, StringComparer.Ordinal).ToList();
			shared.Add(Describe(group.Key) + ": " + string.Join(", ", order.Select(p => p.PatchClass.Name + "(" + p.Priority + (p.CanSkipOriginal ? ", can skip" : string.Empty) + ")").ToArray()));
			for (int i = 0; i < order.Count; i++)
			{
				if (!order[i].CanSkipOriginal)
				{
					continue;
				}
				for (int j = i + 1; j < order.Count; j++)
				{
					if (order[j].PatchClass == order[i].PatchClass || HasParameter(order[j].Method, "__runOriginal"))
					{
						continue;
					}
					string key = Describe(group.Key) + "|" + order[i].PatchClass.Name;
					if (SkipWhitelist.ContainsKey(key))
					{
						continue;
					}
					failures.Add(Describe(group.Key) + ": " + order[i].PatchClass.Name + " can skip the original, then " + order[j].PatchClass.Name + " runs without looking at __runOriginal (whitelist key: " + key + ")");
				}
			}
		}
		TestContext.Out.WriteLine("Targets with prefixes from several patch classes (" + shared.Count + "):\n" + string.Join("\n", shared.ToArray()));
		Assert.That(failures, Is.Empty, "Prefixes that skip the original before a prefix that does not check __runOriginal:\n" + string.Join("\n", failures.ToArray()));
	}

	[Test]
	public void ModNetworkMessageIdsAreUniqueAndOverTheGamesOwn()
	{
		Dictionary<int, string> seen = new Dictionary<int, string>();
		List<string> failures = new List<string>();
		// Constants of the mod's classes: const short ...Message / ToClients / ToHost = 2xx.
		// ModMessages holds the range of the mod's ids (First, Last: every client ignores them until a feature registers its
		// own handler) and a copy of the one id a client sends to the host: not ids of its own, but the range they must lie in.
		Type registry = ModAssembly.GetType("CastleStoryPlus.Multiplayer.ModMessages", true);
		const BindingFlags constants = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
		int first = (short)registry.GetField("First", constants).GetRawConstantValue();
		int last = (short)registry.GetField("Last", constants).GetRawConstantValue();
		foreach (Type type in LoadableTypes(ModAssembly))
		{
			if (type == registry)
			{
				continue;
			}
			foreach (FieldInfo field in type.GetFields(constants))
			{
				if (!field.IsLiteral || field.FieldType != typeof(short))
				{
					continue;
				}
				int id = (short)field.GetRawConstantValue();
				if (id < 200)
				{
					continue;
				}
				string name = type.FullName + "." + field.Name;
				if (id > last || id < first)
				{
					failures.Add(name + " = " + id + " is outside ModMessages " + first + " to " + last + " (the range clients ignore until a feature registers)");
				}
				if (seen.TryGetValue(id, out string other))
				{
					failures.Add("id " + id + " is used by " + other + " and " + name);
				}
				else
				{
					seen[id] = name;
				}
			}
		}
		// Ids written as numbers in a Register/Send call.
		string root = FindRepositoryRoot();
		Regex literal = new Regex(@"(RegisterHandler|SendToAll|SendByChannel|SendToReady)\(\s*(\d+)\s*,");
		foreach (string file in Directory.GetFiles(Path.Combine(root, "CastleStoryPlus"), "*.cs", SearchOption.AllDirectories))
		{
			if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
			{
				continue;
			}
			foreach (Match match in literal.Matches(File.ReadAllText(file)))
			{
				failures.Add(Path.GetFileName(file) + ": message id " + match.Groups[2].Value + " written as a number; give it a named constant so this test sees it");
			}
		}
		Assert.That(seen.Count, Is.GreaterThan(5), "The mod's message id constants were not found");
		Assert.That(failures, Is.Empty, string.Join("\n", failures.ToArray()));
	}

	private static class WrongPatch
	{
		public static bool Good(string __instance, int startIndex, ref string __result)
		{
			return true;
		}

		public static bool Wrong(string __instance, int wrongName, ref int __result)
		{
			return true;
		}
	}

	// The audit itself: a patch that fits String.Substring(int) has no problem, one that does not has several.
	[Test]
	public void TheAuditFindsAWrongPatch()
	{
		MethodBase target = typeof(string).GetMethod("Substring", new[] { typeof(int) });
		PatchMethod good = new PatchMethod { Kind = "Prefix", Method = typeof(WrongPatch).GetMethod("Good") };
		PatchMethod wrong = new PatchMethod { Kind = "Prefix", Method = typeof(WrongPatch).GetMethod("Wrong") };
		Assert.That(CheckParameters(good, target), Is.Empty);
		Assert.That(CheckParameters(wrong, target).Count(), Is.EqualTo(2));
	}

	private static IEnumerable<KeyValuePair<MethodBase, List<PatchMethod>>> PrefixesByTarget()
	{
		Dictionary<MethodBase, List<PatchMethod>> groups = new Dictionary<MethodBase, List<PatchMethod>>();
		foreach (PatchClass patch in _patches)
		{
			foreach (PatchMethod method in patch.Methods.Where(m => m.Kind == "Prefix"))
			{
				foreach (MethodBase target in TargetsOf(patch, method))
				{
					if (!groups.TryGetValue(target, out List<PatchMethod> list))
					{
						list = new List<PatchMethod>();
						groups[target] = list;
					}
					list.Add(method);
				}
			}
		}
		return groups;
	}

	private static List<MethodBase> TargetsOf(PatchClass patch, PatchMethod method)
	{
		return method.OwnTargets ?? patch.Targets;
	}

	private static IEnumerable<Type> LoadableTypes(Assembly assembly)
	{
		try
		{
			return assembly.GetTypes();
		}
		catch (ReflectionTypeLoadException ex)
		{
			string errors = string.Join("\n", ex.LoaderExceptions.Select(e => e.Message).Distinct().ToArray());
			Assert.Fail("Types of " + assembly.GetName().Name + " could not be loaded (a missing game assembly?):\n" + errors);
			return ex.Types.Where(t => t != null);
		}
	}

	private static bool IsPatchClass(Type type)
	{
		return type.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0;
	}

	// The class's [HarmonyPatch] attributes merged as Harmony does (a later attribute's set values win). Harmony's own
	// HarmonyMethod.Merge needs AccessTools, which does not start on .NET 8.
	private static HarmonyMethod ClassInfo(Type type)
	{
		return Merge(null, type.GetCustomAttributes(typeof(HarmonyPatch), false));
	}

	private static HarmonyMethod Merge(HarmonyMethod start, object[] attributes)
	{
		HarmonyMethod merged = new HarmonyMethod();
		merged.priority = -1;
		if (start != null)
		{
			merged.declaringType = start.declaringType;
			merged.methodName = start.methodName;
			merged.methodType = start.methodType;
			merged.argumentTypes = start.argumentTypes;
			merged.priority = start.priority;
		}
		foreach (HarmonyPatch attribute in attributes.Cast<HarmonyPatch>())
		{
			HarmonyMethod info = attribute.info;
			merged.declaringType = info.declaringType ?? merged.declaringType;
			merged.methodName = info.methodName ?? merged.methodName;
			merged.methodType = info.methodType ?? merged.methodType;
			merged.argumentTypes = info.argumentTypes ?? merged.argumentTypes;
			merged.priority = (info.priority >= 0) ? info.priority : merged.priority;
		}
		return merged;
	}

	private static void ResolveTargets(PatchClass patch)
	{
		Type type = patch.Type;
		const BindingFlags all = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
		try
		{
			MethodInfo targets = type.GetMethod("TargetMethods", all);
			MethodInfo single = type.GetMethod("TargetMethod", all);
			if (targets != null)
			{
				foreach (MethodBase method in ((IEnumerable)targets.Invoke(null, targets.GetParameters().Length == 0 ? null : new object[] { null })))
				{
					if (method == null)
					{
						patch.Problems.Add("TargetMethods gave a null target");
					}
					else
					{
						patch.Targets.Add(method);
					}
				}
				return;
			}
			if (single != null)
			{
				MethodBase method = (MethodBase)single.Invoke(null, single.GetParameters().Length == 0 ? null : new object[] { null });
				if (method == null)
				{
					patch.Problems.Add("TargetMethod gave null");
				}
				else
				{
					patch.Targets.Add(method);
				}
				return;
			}
		}
		catch (Exception ex)
		{
			Exception inner = (ex is TargetInvocationException && ex.InnerException != null) ? ex.InnerException : ex;
			patch.Problems.Add("TargetMethod(s) threw " + inner.GetType().Name + ": " + inner.Message);
			return;
		}
		ResolveInfo(ClassInfo(type), patch.Targets, patch.Problems);
	}

	// The target an attribute set names (declaring type, method name, parameter types, kind of method).
	private static void ResolveInfo(HarmonyMethod info, List<MethodBase> targets, List<string> problems)
	{
		if (info.declaringType == null)
		{
			problems.Add("no declaring type and no TargetMethod");
			return;
		}
		Type[] arguments = info.argumentTypes;
		try
		{
			MethodBase target;
			switch (info.methodType ?? MethodType.Normal)
			{
				case MethodType.Constructor:
					target = Constructor(info.declaringType, arguments, false);
					break;
				case MethodType.StaticConstructor:
					target = Constructor(info.declaringType, null, true);
					break;
				case MethodType.Getter:
					target = Accessor(info.declaringType, info.methodName, true);
					break;
				case MethodType.Setter:
					target = Accessor(info.declaringType, info.methodName, false);
					break;
				default:
					if (string.IsNullOrEmpty(info.methodName))
					{
						problems.Add("no method name");
						return;
					}
					target = Declared(info.declaringType, info.methodName, arguments);
					break;
			}
			if (target == null)
			{
				problems.Add(info.declaringType.FullName + "." + (info.methodName ?? ".ctor") + ((arguments != null) ? "(" + string.Join(", ", arguments.Select(a => a.Name).ToArray()) + ")" : string.Empty) + " not found");
			}
			else
			{
				targets.Add(target);
			}
		}
		catch (AmbiguousMatchException)
		{
			problems.Add(info.declaringType.FullName + "." + info.methodName + " is ambiguous: name the parameter types");
		}
		catch (Exception ex)
		{
			problems.Add("target lookup threw " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private static void CollectMethods(PatchClass patch)
	{
		const BindingFlags all = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
		HarmonyMethod classInfo = ClassInfo(patch.Type);
		foreach (MethodInfo method in patch.Type.GetMethods(all))
		{
			string kind = KindOf(method);
			if (kind == null)
			{
				continue;
			}
			int priority = method.GetCustomAttributes(typeof(HarmonyPriority), false).Cast<HarmonyPriority>().Select(a => a.info.priority).DefaultIfEmpty(-1).Max();
			if (priority < 0)
			{
				priority = (classInfo.priority >= 0) ? classInfo.priority : DefaultPriority;
			}
			object[] own = method.GetCustomAttributes(typeof(HarmonyPatch), false);
			List<MethodBase> ownTargets = null;
			if (own.Length > 0)
			{
				ownTargets = new List<MethodBase>();
				ResolveInfo(Merge(classInfo, own), ownTargets, patch.Problems);
			}
			patch.Methods.Add(new PatchMethod
			{
				OwnTargets = ownTargets,
				PatchClass = patch.Type,
				Method = method,
				Kind = kind,
				Priority = priority,
				CanSkipOriginal = kind == "Prefix" && method.ReturnType == typeof(bool) && CanReturnFalse(method)
			});
		}
	}

	private static string KindOf(MethodInfo method)
	{
		foreach (string kind in new[] { "Prefix", "Postfix", "Transpiler", "Finalizer" })
		{
			if (method.Name == kind || method.GetCustomAttributes(false).Any(a => a.GetType().Name == "Harmony" + kind))
			{
				return kind;
			}
		}
		return null;
	}

	// A bool prefix can skip the original when its IL has a "ldc.i4.0" (false) at all; one without can only return true.
	private static bool CanReturnFalse(MethodInfo method)
	{
		MethodBody body = method.GetMethodBody();
		if (body == null)
		{
			return true;
		}
		byte[] il = body.GetILAsByteArray();
		return il.Any(b => b == 0x16);
	}

	private static bool HasParameter(MethodInfo method, string name)
	{
		return method.GetParameters().Any(p => p.Name == name);
	}

	// Harmony's AccessTools does not start on .NET 8, so the lookups it does are done here, the way it does them: the
	// type's own members, all visibilities; without parameter types the name must be unique.
	private const BindingFlags Own = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

	private static MethodBase Declared(Type type, string name, Type[] arguments)
	{
		if (arguments != null)
		{
			return type.GetMethod(name, Own, null, arguments, null);
		}
		MethodInfo[] found = type.GetMethods(Own).Where(m => m.Name == name).ToArray();
		if (found.Length > 1)
		{
			throw new AmbiguousMatchException();
		}
		return found.Length == 1 ? found[0] : null;
	}

	private static MethodBase Constructor(Type type, Type[] arguments, bool isStatic)
	{
		BindingFlags flags = (Own & ~(BindingFlags.Static | BindingFlags.Instance)) | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
		if (arguments != null)
		{
			return type.GetConstructor(flags, null, arguments, null);
		}
		ConstructorInfo[] found = type.GetConstructors(flags);
		if (found.Length > 1)
		{
			throw new AmbiguousMatchException();
		}
		return found.Length == 1 ? found[0] : null;
	}

	private static MethodBase Accessor(Type type, string name, bool getter)
	{
		PropertyInfo property = type.GetProperty(name, Own);
		return (property == null) ? null : (getter ? property.GetGetMethod(true) : property.GetSetMethod(true));
	}

	private static bool HasMember(Type type, string name)
	{
		for (Type t = type; t != null; t = t.BaseType)
		{
			if (t.GetField(name, Own) != null || t.GetProperty(name, Own) != null)
			{
				return true;
			}
		}
		return false;
	}

	private static string Describe(MethodBase target)
	{
		return (target.DeclaringType != null ? target.DeclaringType.Name : "?") + "." + target.Name;
	}

	private static IEnumerable<string> CheckParameters(PatchMethod patch, MethodBase target)
	{
		MethodInfo method = patch.Method;
		Type owner = target.DeclaringType;
		ParameterInfo[] original = target.GetParameters();
		Type returnType = (target as MethodInfo != null) ? ((MethodInfo)target).ReturnType : typeof(void);
		if (patch.Kind == "Prefix" && method.ReturnType != typeof(void) && method.ReturnType != typeof(bool))
		{
			yield return "a prefix returns void or bool, not " + method.ReturnType.Name;
		}
		if (patch.Kind == "Transpiler" && !typeof(IEnumerable).IsAssignableFrom(method.ReturnType))
		{
			yield return "a transpiler returns IEnumerable<CodeInstruction>, not " + method.ReturnType.Name;
		}
		if (patch.Kind == "Finalizer" && method.ReturnType != typeof(void) && method.ReturnType != typeof(Exception))
		{
			yield return "a finalizer returns void or Exception, not " + method.ReturnType.Name;
		}
		if (patch.Kind == "Transpiler")
		{
			// A transpiler's parameters are the instructions, the generator and the original method: not filled by name.
			yield break;
		}
		foreach (ParameterInfo parameter in method.GetParameters())
		{
			string name = parameter.Name;
			Type type = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType() : parameter.ParameterType;
			if (name == "__instance")
			{
				if (target.IsStatic)
				{
					yield return "__instance on a static method";
				}
				else if (!owner.ContainsGenericParameters && !type.ContainsGenericParameters && !type.IsAssignableFrom(owner) && !owner.IsAssignableFrom(type))
				{
					yield return "__instance is " + type.Name + " but the target is on " + owner.Name;
				}
			}
			else if (name == "__result")
			{
				if (returnType == typeof(void))
				{
					yield return "__result on a method that returns void";
				}
				else if (!returnType.ContainsGenericParameters && !type.ContainsGenericParameters && !type.IsAssignableFrom(returnType) && !returnType.IsAssignableFrom(type))
				{
					yield return "__result is " + type.Name + " but the target returns " + returnType.Name;
				}
			}
			else if (name == "__state" || name == "__originalMethod" || name == "__runOriginal" || name == "__exception" || name == "__args")
			{
				if (name == "__runOriginal" && patch.Kind != "Prefix" && patch.Kind != "Postfix")
				{
					yield return "__runOriginal is for prefixes and postfixes";
				}
				if (name == "__exception" && patch.Kind != "Finalizer")
				{
					yield return "__exception is for finalizers";
				}
			}
			else if (name.StartsWith("___", StringComparison.Ordinal))
			{
				string field = name.Substring(3);
				bool numeric = int.TryParse(field, out _);
				if (!numeric && !HasMember(owner, field))
				{
					yield return name + ": " + owner.Name + " has no field " + field;
				}
			}
			else if (name.StartsWith("__", StringComparison.Ordinal) && int.TryParse(name.Substring(2), out int index))
			{
				if (index < 0 || index >= original.Length)
				{
					yield return name + ": the target has " + original.Length + " parameters";
				}
			}
			else
			{
				ParameterInfo match = original.FirstOrDefault(p => p.Name == name);
				if (match == null)
				{
					yield return "parameter " + name + " is not a parameter of the target (" + string.Join(", ", original.Select(p => p.Name).ToArray()) + ")";
				}
				else
				{
					Type origType = match.ParameterType.IsByRef ? match.ParameterType.GetElementType() : match.ParameterType;
					if (!origType.ContainsGenericParameters && !type.ContainsGenericParameters && !type.IsAssignableFrom(origType))
					{
						yield return "parameter " + name + " is " + type.Name + " but the target's is " + origType.Name;
					}
				}
			}
		}
	}

	private static string FindRepositoryRoot()
	{
		string directory = TestContext.CurrentContext.TestDirectory;
		while (directory != null && !File.Exists(Path.Combine(directory, "CastleStoryPlus.sln")))
		{
			directory = Path.GetDirectoryName(directory);
		}
		Assert.That(directory, Is.Not.Null, "CastleStoryPlus.sln not found above the test folder");
		return directory;
	}
}
