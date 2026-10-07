using System.Collections.Generic;
using CastleStoryPlus.Core;
using MoonSharp.Interpreter;

namespace CastleStoryPlus.LuaUi;

// A stand-in for one of the game's Lua menus that writes down what is added to it. GameMenu fills its views by
// calling methods on them: the bars get m.AddMenuHandleToggle(h) in GameMenu's build coroutine (mods add theirs
// there too), the sidebars get m.AddSection(sh) and m.AddMenuHandleToggle(h, sh) from their handle's onLoad event.
// A recorder answers every method, keeps the calls in order and answers itself, so those chains run unchanged,
// and a C# view draws the menu from the calls. A recorder made from Lua (CastleStoryPlus.MenuRecorder(name), in
// place of a menu block cut out of GameMenu) is found again by its name.
internal class LuaMenuRecorder
{
	public struct Call
	{
		public string Method;

		// Without the recorder itself when the method was called with a colon.
		public DynValue[] Args;
	}

	private static readonly Dictionary<string, LuaMenuRecorder> Named = new Dictionary<string, LuaMenuRecorder>();

	public readonly List<Call> Calls = new List<Call>();

	// Goes up with every call, for views that rebuild when more was added.
	public int Version { get; private set; }

	public Table Table { get; private set; }

	public string Name { get; private set; }

	// Made by the C# menus' features while the plugin starts; GameMenu calls it when its script runs.
	public static void Register()
	{
		LuaInjection.AddFunction("MenuRecorder", (ScriptExecutionContext context, CallbackArguments args) =>
		{
			string name = (args.Count > 0 && args[0].Type == DataType.String) ? args[0].String : string.Empty;
			LuaMenuRecorder recorder = Create(context.OwnerScript, name);
			Named[name] = recorder;
			return DynValue.NewTable(recorder.Table);
		});
	}

	// The recorder GameMenu made under that name, if it belongs to the in-game menu now running.
	public static LuaMenuRecorder Find(string name)
	{
		if (!Named.TryGetValue(name, out LuaMenuRecorder recorder) || recorder.Table == null || recorder.Table.OwnerScript != GameMenuLink.Script)
		{
			return null;
		}
		return recorder;
	}

	public static LuaMenuRecorder Create(Script script, string name)
	{
		LuaMenuRecorder recorder = new LuaMenuRecorder { Name = name };
		Table table = new Table(script);
		Table meta = new Table(script);
		DynValue self = DynValue.NewTable(table);
		Dictionary<string, DynValue> methods = new Dictionary<string, DynValue>();
		meta.Set("__index", DynValue.NewCallback((ScriptExecutionContext context, CallbackArguments args) =>
		{
			if (args.Count < 2 || args[1].Type != DataType.String)
			{
				return DynValue.Nil;
			}
			string key = args[1].String;
			if (!methods.TryGetValue(key, out DynValue method))
			{
				method = DynValue.NewCallback((ScriptExecutionContext callContext, CallbackArguments callArgs) =>
				{
					recorder.Record(key, callArgs, table);
					return self;
				});
				methods[key] = method;
			}
			return method;
		}));
		table.MetaTable = meta;
		recorder.Table = table;
		return recorder;
	}

	private void Record(string method, CallbackArguments args, Table table)
	{
		int first = (args.Count > 0 && args[0].Type == DataType.Table && args[0].Table == table) ? 1 : 0;
		DynValue[] values = new DynValue[System.Math.Max(0, args.Count - first)];
		for (int i = first; i < args.Count; i++)
		{
			values[i - first] = args[i];
		}
		Calls.Add(new Call { Method = method, Args = values });
		Version++;
	}

	// An argument of a call as a table (a handle or section), or null.
	public static Table TableArg(Call call, int index)
	{
		return (index < call.Args.Length && call.Args[index].Type == DataType.Table) ? call.Args[index].Table : null;
	}
}
