using System;
using System.Reflection;
using System.Text.RegularExpressions;
using CastleStoryPlus.Core;
using HarmonyLib;
using Newtonsoft.Json.Serialization;

namespace CastleStoryPlus.Loading;

// Saves name the type of each object they hold ("UnityEngine.Color, UnityEngine"). Unity 5.6 keeps the engine's
// types in UnityEngine; Unity 6 splits them into modules (UnityEngine.CoreModule, UnityEngine.PhysicsModule...) and
// its UnityEngine only forwards to them. A save written by the Unity 6 port named the modules, which the 5.6 game
// cannot load: its objects (bricktrons, the home crystal, buildings) were lost and only the terrain loaded. The port
// now writes the 5.6 names, which it reads through UnityEngine's forwards, and the 5.6 game reads the module names
// of saves the port wrote before.
internal static class PortableSaveTypes
{
	private static readonly Regex EngineModule = new Regex(@", UnityEngine\.[A-Za-z0-9]+Module\b");

	internal static string ToEngine(string typeName)
	{
		if (typeName == null || typeName.IndexOf("Module", StringComparison.Ordinal) < 0)
		{
			return typeName;
		}
		return EngineModule.Replace(typeName, ", UnityEngine");
	}

	internal static bool IsEngineModule(string assemblyName)
	{
		return assemblyName != null && assemblyName.StartsWith("UnityEngine.", StringComparison.Ordinal) && assemblyName.EndsWith("Module", StringComparison.Ordinal);
	}
}

#if UNITY6
// Newtonsoft's and the game's own type names (Simple assembly format) both go through RemoveAssemblyDetails.
[Feature]
[HarmonyPatch]
internal static class PortableSaveTypesWritePatch
{
	private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
	{
		MethodBase newtonsoft = AccessTools.Method("Newtonsoft.Json.Utilities.ReflectionUtils:RemoveAssemblyDetails", new[] { typeof(string) });
		if (newtonsoft != null)
		{
			yield return newtonsoft;
		}
		yield return AccessTools.Method(typeof(Brix.IO.Serialization.Json.SerializationUtils), nameof(Brix.IO.Serialization.Json.SerializationUtils.RemoveAssemblyDetails));
	}

	private static void Postfix(ref string __result)
	{
		__result = PortableSaveTypes.ToEngine(__result);
	}
}
#else
[Feature]
[HarmonyPatch(typeof(DefaultSerializationBinder), nameof(DefaultSerializationBinder.BindToType))]
internal static class PortableSaveTypesReadPatch
{
	private static void Prefix(ref string assemblyName, ref string typeName)
	{
		if (PortableSaveTypes.IsEngineModule(assemblyName))
		{
			assemblyName = "UnityEngine";
		}
		typeName = PortableSaveTypes.ToEngine(typeName);
	}
}
#endif
