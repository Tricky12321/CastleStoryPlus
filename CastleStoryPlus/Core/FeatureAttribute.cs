using System;

namespace CastleStoryPlus.Core;

// Groups patch classes into a feature that can be switched off in the config ([Features] section).
// A class with [HarmonyPatch] is patched when its feature is on; a static Enable() method is run as well.
[AttributeUsage(AttributeTargets.Class)]
internal class FeatureAttribute : Attribute
{
	public string Name { get; }

	public string Description { get; }

	// Always-on infrastructure (no config entry).
	public FeatureAttribute()
	{
	}

	public FeatureAttribute(string name, string description)
	{
		Name = name;
		Description = description;
	}
}
