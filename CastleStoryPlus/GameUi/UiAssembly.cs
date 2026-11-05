using System;
using System.IO;
using System.Reflection;
using Mono.Cecil;

namespace CastleStoryPlus.GameUi;

// The in-game menu's code lives in its own assembly (CastleStoryPlus.UI.dll, next to the plugin) so it can be
// reloaded in a running game: "Reload UI" (the debug sidebar, DevTools POST /api/ui/reload) closes the menu, loads
// the newest build of the file and builds the menu again. Mono cannot unload an assembly, so each load is a new
// copy under a name of its own (renamed with Mono.Cecil, as BepInEx's ScriptEngine does: Unity and Mono tell the
// copies' types apart by their assembly's name); the old copies stay loaded, unused. The data the features add to
// (the model, catalogues, GameMenuItems) lives here in the plugin and survives a reload; only the builders and the
// views are reloaded. The loaded copy calls back through these delegates (UiEntry.Load sets them).
internal static class UiAssembly
{
	internal const string FileName = "CastleStoryPlus.UI.dll";

	private const string EntryType = "CastleStoryPlus.GameUi.UiEntry";

	// reload: built again in a game already going.
	internal static Action<bool> Open;

	internal static Action Close;

	internal static Func<GameMenuModel> Model;

	// The world editor's menu (CsWorldEditor).
	internal static Action OpenWorldEditor;

	internal static Action CloseWorldEditor;

	internal static Func<bool> WorldEditorOpen;

	// Every view reads its item again.
	internal static Action MarkAll;

	private static bool _pending;

	// How many times the assembly was loaded (1 for the first load).
	internal static int Loads { get; private set; }

	internal static DateTime LoadedAt { get; private set; }

	internal static string FilePath
	{
		get { return Path.Combine(Plugin.Directory, FileName); }
	}

	// Loads the newest build; false (and the copy before, if any, kept) when it could not.
	internal static bool Load()
	{
		try
		{
			string path = FilePath;
			if (!File.Exists(path))
			{
				Plugin.Log.LogError("UiAssembly: " + path + " is missing; no in-game menu");
				return false;
			}
			byte[] bytes;
			using (AssemblyDefinition definition = AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(path))))
			{
				definition.Name.Name = definition.Name.Name + "-" + DateTime.Now.Ticks;
				// After a ModReload the menu is bound to the mod's copy now running, which has a name of its own.
				string mod = typeof(Plugin).Assembly.GetName().Name;
				foreach (AssemblyNameReference reference in definition.MainModule.AssemblyReferences)
				{
					if (reference.Name == "CastleStoryPlus")
					{
						reference.Name = mod;
					}
				}
				using (MemoryStream renamed = new MemoryStream())
				{
					definition.Write(renamed);
					bytes = renamed.ToArray();
				}
			}
			Assembly assembly = Assembly.Load(bytes);
			Type entry = assembly.GetType(EntryType, true);
			entry.GetMethod("Load", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
			Loads++;
			LoadedAt = File.GetLastWriteTime(path);
			Plugin.Log.LogInfo("UiAssembly: loaded " + FileName + " (load " + Loads + ", built " + LoadedAt.ToString("yyyy-MM-dd HH:mm:ss") + ")");
			return true;
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("UiAssembly: could not load " + FileName + ": " + ex);
			return false;
		}
	}

	// Asked from a click or another thread: done at the start of the next frame (UiAssembly.Tick), not inside a
	// view that is about to be destroyed.
	internal static void RequestReload()
	{
		_pending = true;
	}

	internal static void Tick()
	{
		if (!_pending)
		{
			return;
		}
		_pending = false;
		Reload();
	}

	// Closes the menu, loads the newest build and opens the menu again if a game was going.
	internal static bool Reload()
	{
		bool open = Model != null && Model() != null;
		bool editor = WorldEditorOpen != null && WorldEditorOpen();
		try
		{
			Close?.Invoke();
			CloseWorldEditor?.Invoke();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError("UiAssembly: closing the menu for a reload: " + ex);
		}
		bool loaded = Load();
		if (open && Open != null)
		{
			Open(true);
		}
		if (editor && OpenWorldEditor != null)
		{
			OpenWorldEditor();
		}
		return loaded;
	}
}
