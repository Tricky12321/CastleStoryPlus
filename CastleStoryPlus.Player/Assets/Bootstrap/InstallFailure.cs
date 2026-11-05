using System.Collections.Generic;
using System.Linq;

namespace CastleStoryPlus.Player
{
	// What the installer screen says when the game's files do not match the version the port is made for: what kind
	// of difference it found and what the player can do about it, instead of a bare count. The problems are
	// GameFiles.Verify's "missing <file>" and "changed <file>" (paths relative to "Castle Story_Data").
	internal static class InstallFailure
	{
		// How many of the files are listed on the screen (all of them are in install.log).
		private const int ListedFiles = 3;

		internal static List<string> Explain(string[] problems, string platform, string logPath)
		{
			var lines = new List<string>();
			string[] missing = problems.Where(p => p.StartsWith("missing ")).ToArray();
			string[] changed = problems.Where(p => p.StartsWith("changed ")).ToArray();
			string[] changedCode = changed.Where(p => p.StartsWith("changed Managed/")).ToArray();
			bool onlyCode = changedCode.Length > 0 && changedCode.Length == changed.Length && missing.Length == 0;
			bool manyData = changed.Length - changedCode.Length > 20;

			if (onlyCode)
			{
				lines.Add("Castle Story's code has been changed (" + Files(changedCode) + "), most likely by another mod or mod loader. The port is made from the unmodified game.");
			}
			else if (manyData)
			{
				lines.Add(changed.Length + " game files are a different version than the port is made for: probably a Steam beta branch, or not the " + platform + " version of Castle Story.");
				lines.Add("In Steam: Castle Story > Properties > Betas: choose \"None\". Then verify the game's files (below).");
			}
			else
			{
				if (missing.Length > 0)
				{
					lines.Add(missing.Length + " game files are missing (" + Files(missing) + "): an interrupted download or update, or files removed by another program.");
				}
				if (changed.Length > 0)
				{
					lines.Add(changed.Length + " game files are changed (" + Files(changed) + "): by another mod, or a damaged download.");
				}
			}
			lines.Add("To fix it: in Steam, right-click Castle Story > Properties > Installed Files > \"Verify integrity of game files\". Steam puts the original files back (other mods' changes to them are undone). Then click Install again.");
			lines.Add("The full list is in " + logPath + ".");
			return lines;
		}

		// The first few files by name (without the "missing "/"changed " prefix), and how many more.
		private static string Files(string[] problems)
		{
			IEnumerable<string> names = problems.Take(ListedFiles).Select(p => p.Substring(p.IndexOf(' ') + 1));
			string list = string.Join(", ", names.ToArray());
			return problems.Length > ListedFiles ? list + " and " + (problems.Length - ListedFiles) + " more" : list;
		}
	}
}
