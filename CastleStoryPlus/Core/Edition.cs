namespace CastleStoryPlus.Core;

// Which edition of the mod this is (docs/Editions.md): Full, or Essentials (built with -p:Edition=Essentials: only the
// bug fixes, performance work and AI/pathfinding improvements). Both editions have the same plugin GUID, config file
// and version number; the edition decides the name shown, the logo, the release zips the updater takes and whether
// another player may join (EditionCheck).
internal static class Edition
{
#if ESSENTIALS
	public const string Id = "Essentials";

	public const bool IsEssentials = true;

	// The release zips of this edition: CastleStoryPlus-Essentials-v0.9.4-linux.zip.
	public const string PackagePrefix = "CastleStoryPlus-Essentials-v";

	public const string LogoFile = "logo_essentials.png";

	public const string BadgeFile = "plus_essentials.png";
#else
	public const string Id = "Full";

	public const bool IsEssentials = false;

	// The release zips of this edition: CastleStoryPlus-v0.9.4-linux.zip.
	public const string PackagePrefix = "CastleStoryPlus-v";

	public const string LogoFile = "logo.png";

	public const string BadgeFile = "plus.png";
#endif
}
