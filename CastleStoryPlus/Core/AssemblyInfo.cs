using System.Runtime.CompilerServices;

// The developer tools (in-game API and test runner) and the unit tests reach the mod's internals.
[assembly: InternalsVisibleTo("CastleStoryPlus.DevTools")]
[assembly: InternalsVisibleTo("CastleStoryPlus.Tests")]
