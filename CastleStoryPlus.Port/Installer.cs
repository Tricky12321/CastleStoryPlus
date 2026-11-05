using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CastleStoryPlus.Port;

// Makes the playable port from the player's own Castle Story install, into the Unity 6 player's data folder:
//  1. checks the install against the hash manifest (nothing happens unless every file matches);
//  2. patches the game's assemblies into Managed and registers them as script assemblies;
//  3. copies the game's project settings (tags, layers, physics, input...) into the player's globalgamemanagers;
//  4. copies the game's native plugins and StreamingAssets (Wwise banks, game data);
//  5. converts the game's data files into bundles (Content, next to the data folder);
//  6. writes the content's own hash list, so a damaged or partial install can be told from a good one.
public sealed class Installer
{
	public const string InstalledName = "installed.sha256";
	public const string GameRootName = "game.txt";

	public string GameDataDir;
	public string PlayerDataDir;
	public string ContentDir;
	public string ClassPackage;
	public string Manifest;
	public Action<string> Log = _ => { };
	public Action<string, float> Progress = (_, _) => { };

	// Game files changed by a mod whose originals were kept (GameFiles.Verify): relative path, original's path.
	private readonly Dictionary<string, string> _originals = new Dictionary<string, string>();

	public void Run()
	{
		Progress("Checking game files", 0f);
		List<string> problems = GameFiles.Verify(GameDataDir, Manifest, (done, total) => Progress("Checking game files", 0.3f * done / Math.Max(1, total)), _originals);
		if (problems.Count > 0)
		{
			throw new GameFilesException(problems);
		}
		foreach (KeyValuePair<string, string> original in _originals)
		{
			Log("using the original " + original.Key + " kept in " + GameFiles.OriginalsFolder + " (the game's copy was changed by a mod)");
		}
		Log("game files match the manifest");
		string gameManaged = Path.Combine(GameDataDir, "Managed");
		string clean = (_originals.Count > 0) ? CleanManaged(gameManaged) : null;
		try
		{
			Install(clean ?? gameManaged);
		}
		finally
		{
			if (clean != null)
			{
				try
				{
					Directory.Delete(clean, true);
				}
				catch (Exception e)
				{
					// Still open (the converter's assembly reader): left for the next install to replace.
					Log("could not remove " + clean + ": " + e.Message);
				}
			}
		}
	}

	private void Install(string gameManaged)
	{
		Progress("Patching game code", 0.3f);
		InstallCode(gameManaged);
		Progress("Copying game settings", 0.38f);
		GameSettings.Copy(ClassPackage, GameDataDir, PlayerDataDir, Log);
		Progress("Copying plugins", 0.4f);
		CopyPlugins();
		Progress("Copying game data", 0.45f);
		CopyTree(Path.Combine(GameDataDir, "StreamingAssets"), Path.Combine(PlayerDataDir, "StreamingAssets"));
		Progress("Converting game content", 0.7f);
		DataConverter.ConvertAll(ClassPackage, GameDataDir, ContentDir, Log, Path.Combine(PlayerDataDir, GameSettings.TypesName), gameManaged);
		// The game reads and writes Info/ (game data, saves, settings) relative to its folder; the port runs there too,
		// so both share one Info.
		File.WriteAllText(Path.Combine(ContentDir, GameRootName), Path.GetFullPath(Path.Combine(GameDataDir, "..")));
		Progress("Checking converted content", 0.95f);
		WriteInstalled();
		Progress("Done", 1f);
	}

	// Whether the content is complete and unchanged since it was made (sizes only: cheap enough for every start).
	public static bool IsInstalled(string contentDir)
	{
		string list = Path.Combine(contentDir, InstalledName);
		if (!File.Exists(list))
		{
			return false;
		}
		foreach (string line in File.ReadAllLines(list))
		{
			string[] parts = line.Split(new[] { ' ' }, 3);
			if (parts.Length < 3 || parts[0] == "manifest")
			{
				continue;
			}
			var file = new FileInfo(Path.Combine(contentDir, parts[2]));
			if (!file.Exists || file.Length.ToString() != parts[1])
			{
				return false;
			}
		}
		return true;
	}

	// Puts a newly written file in place by renaming, not by writing over it: the installer runs inside the player,
	// which has its globalgamemanagers (and on a second install the game's assemblies) open. A file that is already
	// the same is left alone. A different one is renamed out of the way first (a unique .old name, gone at the next
	// install): Windows lets a file in use be renamed where it was opened for that, but deleting it only marks it,
	// and the name stays taken until the player quits.
	internal static void MoveInto(string temp, string target)
	{
		if (File.Exists(target))
		{
			if (SameContent(temp, target))
			{
				File.Delete(temp);
				return;
			}
			string old = target + "." + DateTime.UtcNow.Ticks + ".old";
			try
			{
				File.Move(target, old);
			}
			catch (IOException e)
			{
				File.Delete(temp);
				throw new IOException(Path.GetFileName(target) + " is in use and can not be replaced. Start the game without mods and install again.", e);
			}
		}
		File.Move(temp, target);
	}

	// What MoveInto had to leave behind in a folder.
	private static void DeleteOld(string dir)
	{
		foreach (string old in Directory.GetFiles(dir, "*.old"))
		{
			try
			{
				File.Delete(old);
			}
			catch (IOException)
			{
				// Still in use: the next install tries again.
			}
			catch (UnauthorizedAccessException)
			{
				// Still in use: the next install tries again.
			}
		}
	}

	private static bool SameContent(string a, string b)
	{
		if (new FileInfo(a).Length != new FileInfo(b).Length)
		{
			return false;
		}
		using (var first = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
		using (var second = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
		{
			var left = new byte[81920];
			var right = new byte[81920];
			int read;
			while ((read = first.Read(left, 0, left.Length)) > 0)
			{
				int got = 0;
				while (got < read)
				{
					int n = second.Read(right, got, read - got);
					if (n <= 0)
					{
						return false;
					}
					got += n;
				}
				for (int i = 0; i < read; i++)
				{
					if (left[i] != right[i])
					{
						return false;
					}
				}
			}
			return true;
		}
	}

	// A copy of the game's Managed folder with the kept originals in place of the files a mod changed, so the port
	// is made from (and its references resolve to) the game's own code. Never inside the game's folder.
	private string CleanManaged(string gameManaged)
	{
		string clean = Path.Combine(ContentDir, "managed-original");
		if (Directory.Exists(clean))
		{
			Directory.Delete(clean, true);
		}
		Directory.CreateDirectory(clean);
		foreach (string file in Directory.GetFiles(gameManaged, "*.dll"))
		{
			File.Copy(file, Path.Combine(clean, Path.GetFileName(file)));
		}
		foreach (KeyValuePair<string, string> original in _originals)
		{
			File.Copy(original.Value, Path.Combine(clean, original.Key.Substring("Managed/".Length).Replace('/', Path.DirectorySeparatorChar)), true);
		}
		return clean;
	}

	private void InstallCode(string gameManaged)
	{
		string playerManaged = Path.Combine(PlayerDataDir, "Managed");
		DeleteOld(playerManaged);
		DeleteOld(PlayerDataDir);
		// The player's own copy wins (Unity 6's engine, UI and class libraries); a game file that replaced one earlier
		// is recognised by its registration.
		List<string> sources = Directory.GetFiles(gameManaged, "*.dll")
			.Where(source => !File.Exists(Path.Combine(playerManaged, Path.GetFileName(source))) || IsRegisteredGameAssembly(Path.GetFileName(source)))
			.OrderBy(f => f, StringComparer.Ordinal)
			.ToList();
		var patcher = new CodePatcher(new[] { playerManaged, gameManaged }, gameManaged, sources.Select(Path.GetFileNameWithoutExtension)) { Log = Log };
		var installed = new List<string>();
		foreach (string source in sources)
		{
			string name = Path.GetFileName(source);
			string target = Path.Combine(playerManaged, name);
			patcher.Patch(source, target);
			installed.Add(name);
		}
		RegisterScriptAssemblies(Path.Combine(PlayerDataDir, "ScriptingAssemblies.json"), installed);
		File.WriteAllLines(Path.Combine(playerManaged, "game-assemblies.txt"), installed);
	}

	private bool IsRegisteredGameAssembly(string name)
	{
		string list = Path.Combine(PlayerDataDir, "Managed", "game-assemblies.txt");
		return File.Exists(list) && File.ReadAllLines(list).Contains(name);
	}

	// Adds the game's assemblies to the player's script assemblies (type 16, user assembly), so Unity binds the
	// MonoBehaviours in the game's scenes to them.
	private static void RegisterScriptAssemblies(string path, IEnumerable<string> names)
	{
		string json = File.ReadAllText(path);
		int namesStart = json.IndexOf("\"names\":[", StringComparison.Ordinal) + 9;
		int namesEnd = json.IndexOf(']', namesStart);
		int typesStart = json.IndexOf("\"types\":[", StringComparison.Ordinal) + 9;
		int typesEnd = json.IndexOf(']', typesStart);
		List<string> existing = json.Substring(namesStart, namesEnd - namesStart).Split(',').Select(n => n.Trim().Trim('"')).Where(n => n.Length > 0).ToList();
		List<string> types = json.Substring(typesStart, typesEnd - typesStart).Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
		foreach (string name in names)
		{
			if (!existing.Contains(name))
			{
				existing.Add(name);
				types.Add("16");
			}
		}
		var result = new StringBuilder();
		result.Append("{\"names\":[").Append(string.Join(",", existing.Select(n => "\"" + n + "\""))).Append("],\"types\":[").Append(string.Join(",", types)).Append("]}");
		File.WriteAllText(path, result.ToString());
	}

	// The Windows game keeps its 64-bit plugins straight in Plugins, where Unity 6's Windows player does not look; it
	// loads them from Plugins/x86_64. The Linux game has them in Plugins/x86_64 already.
	private void CopyPlugins()
	{
		string source = Path.Combine(GameDataDir, "Plugins");
		string target = Path.Combine(PlayerDataDir, "Plugins");
		bool windows = Directory.Exists(source) && Directory.GetFiles(source, "*.dll").Length > 0;
		CopyTree(source, windows ? Path.Combine(target, "x86_64") : target);
	}

	private static void CopyTree(string source, string target)
	{
		if (!Directory.Exists(source))
		{
			return;
		}
		foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
		{
			string destination = Path.Combine(target, file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(destination));
			var from = new FileInfo(file);
			var to = new FileInfo(destination);
			if (!to.Exists || to.Length != from.Length || to.LastWriteTimeUtc < from.LastWriteTimeUtc)
			{
				File.Copy(file, destination + ".tmp", true);
				MoveInto(destination + ".tmp", destination);
			}
		}
	}

	// "manifest <hash of the game manifest>" then "<sha256> <size> <file>" per content file.
	private void WriteInstalled()
	{
		var lines = new List<string> { "manifest " + GameFiles.Hash(Manifest) };
		foreach (string file in Directory.GetFiles(ContentDir).OrderBy(f => f, StringComparer.Ordinal))
		{
			string name = Path.GetFileName(file);
			if (name == InstalledName)
			{
				continue;
			}
			lines.Add(GameFiles.Hash(file) + " " + new FileInfo(file).Length + " " + name);
		}
		File.WriteAllLines(Path.Combine(ContentDir, InstalledName), lines);
	}
}

public sealed class GameFilesException : Exception
{
	public GameFilesException(List<string> problems)
		: base(problems.Count + " game files are missing or not the expected version: " + string.Join(", ", problems.Take(10)))
	{
		Problems = problems;
	}

	public List<string> Problems { get; }
}
