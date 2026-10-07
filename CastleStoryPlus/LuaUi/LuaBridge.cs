using System;
using System.Collections.Generic;
using Brix.UI.Icons;
using Brix.Utils;
using MoonSharp.Interpreter;
using UnityEngine;

namespace CastleStoryPlus.LuaUi;

// Reading and calling the game's Lua menu tables from C#. The game's menu handles keep their values as closures
// (h.Label = ||GetLocalized(...)) or plain values, and their methods (h:Toggle()) in a metatable; these helpers
// treat both alike, and a Lua error in one closure is logged once and answers a default, so one broken handle
// (the game's or another mod's) does not stop a whole C# menu from drawing.
internal static class LuaBridge
{
	// Errors already logged, by where they happened, so a closure failing every frame logs once.
	private static readonly HashSet<string> Logged = new HashSet<string>();

	// A table's field, following __index tables like Lua does (methods of MenuHandle, Event...). Nil when missing.
	public static DynValue Get(Table table, string key)
	{
		// A table of a killed script throws when read: such a table answers nil, like a missing field.
		for (int depth = 0; table != null && table.isAlive && depth < 8; depth++)
		{
			DynValue value = table.RawGet(key);
			if (!value.IsNil())
			{
				return value;
			}
			Table meta = table.MetaTable;
			DynValue index = (meta != null && meta.isAlive) ? meta.RawGet("__index") : DynValue.Nil;
			if (index.Type != DataType.Table)
			{
				break;
			}
			table = index.Table;
		}
		return DynValue.Nil;
	}

	public static Table GetTable(Table table, string key)
	{
		DynValue value = Get(table, key);
		return (value.Type == DataType.Table) ? value.Table : null;
	}

	// A field's value: a function is called (with args) and its first result answered, any other value as it is.
	// Nil when the field is missing or its function fails.
	public static DynValue Value(Table table, string key, params DynValue[] args)
	{
		DynValue value = Get(table, key);
		if (value.Type == DataType.Function || value.Type == DataType.ClrFunction)
		{
			return Call(value, key, args);
		}
		return value;
	}

	// Calls a Lua or C# function; nil and one log line when it fails.
	public static DynValue Call(DynValue function, string where, params DynValue[] args)
	{
		try
		{
			DynValue result;
			if (function.Type == DataType.Function)
			{
				result = function.Function.Call(args);
			}
			else if (function.Type == DataType.ClrFunction)
			{
				Script script = Owner(args);
				if (script == null)
				{
					return DynValue.Nil;
				}
				result = script.Call(function, args);
			}
			else
			{
				return DynValue.Nil;
			}
			return (result.Type == DataType.Tuple) ? ((result.Tuple.Length > 0) ? result.Tuple[0] : DynValue.Nil) : result;
		}
		catch (InterpreterException ex)
		{
			LogOnce(where, ex.DecoratedMessage ?? ex.Message);
		}
		catch (Exception ex)
		{
			LogOnce(where, ex.Message);
		}
		return DynValue.Nil;
	}

	// Calls a method the Lua way, table:Name(args).
	public static DynValue CallMethod(Table table, string name, params DynValue[] args)
	{
		DynValue function = Get(table, name);
		if (function.IsNil())
		{
			return DynValue.Nil;
		}
		DynValue[] withSelf = new DynValue[args.Length + 1];
		withSelf[0] = DynValue.NewTable(table);
		Array.Copy(args, 0, withSelf, 1, args.Length);
		return Call(function, name, withSelf);
	}

	private static Script Owner(DynValue[] args)
	{
		foreach (DynValue arg in args)
		{
			if (arg.Type == DataType.Table && arg.Table.OwnerScript != null)
			{
				return arg.Table.OwnerScript;
			}
		}
		return GameMenuLink.Script;
	}

	public static void LogOnce(string where, string message)
	{
		string key = where + ": " + message;
		if (Logged.Count < 500 && Logged.Add(key))
		{
			Plugin.Log.LogWarning("CsMenus: Lua " + key);
		}
	}

	public static string String(DynValue value, string fallback)
	{
		if (value.IsNil())
		{
			return fallback;
		}
		if (value.Type == DataType.String)
		{
			return value.String;
		}
		if (value.Type == DataType.Number)
		{
			return value.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}
		string text = value.CastToString();
		return text ?? fallback;
	}

	public static bool Bool(DynValue value, bool fallback)
	{
		if (value.IsNil())
		{
			return fallback;
		}
		return value.CastToBool();
	}

	public static float Number(DynValue value, float fallback)
	{
		if (value.Type != DataType.Number)
		{
			return fallback;
		}
		return (float)value.Number;
	}

	// The game's Lua colours are UserData around a UnityEngine.Color (Color.New, CastleYellow...).
	public static Color Color(DynValue value, Color fallback)
	{
		if (value.Type == DataType.UserData && value.UserData != null && value.UserData.TryGet<Color>(out Color color))
		{
			return color;
		}
		return fallback;
	}

	// Icons in the game's Lua are Sprites (IconKeys.X:Get64()); a string is taken as an icon key ("Database/Name").
	public static Sprite Sprite(DynValue value)
	{
		if (value.IsNil())
		{
			return null;
		}
		if (value.Type == DataType.UserData && value.UserData != null)
		{
			if (value.UserData.TryGet<Sprite>(out Sprite sprite))
			{
				return sprite;
			}
			if (value.UserData.TryGet<IconKey>(out IconKey key))
			{
				return (key != null) ? key.Get64() : null;
			}
		}
		if (value.Type == DataType.String)
		{
			return Icon(value.String);
		}
		return null;
	}

	public static Sprite Icon(string key)
	{
		if (string.IsNullOrEmpty(key) || !IconKey.KeyExists(key))
		{
			return null;
		}
		return IconKey.FromString(key).Get64();
	}

	// A "##key" is looked up in the game's translations, anything else is shown as it is.
	public static string Localized(string text)
	{
		if (string.IsNullOrEmpty(text) || !text.StartsWith("##"))
		{
			return text;
		}
		return I2Helper.TryGet(text);
	}

	// The game's Event tables (Event.New()): adds a C# listener, answering what removes it again.
	public static Action Listen(Table luaEvent, Script script, Action listener)
	{
		if (luaEvent == null || script == null)
		{
			return () => { };
		}
		DynValue callback = DynValue.NewCallback((ScriptExecutionContext context, CallbackArguments args) =>
		{
			try
			{
				listener();
			}
			catch (Exception ex)
			{
				LogOnce("event listener", ex.ToString());
			}
			return DynValue.Void;
		});
		CallMethod(luaEvent, "AddListener", callback);
		return () => CallMethod(luaEvent, "RemoveListener", callback);
	}

	// A Lua closure that calls a C# function: what the game's Hooks.Connect (and anything else that only takes a
	// Lua function) needs. Made once per script.
	public static DynValue Closure(Script script, Func<CallbackArguments, DynValue> function)
	{
		DynValue wrap = script.Registry.Get("CastleStoryPlus.Wrap");
		if (wrap.IsNil())
		{
			wrap = script.DoString("return function(f) return function(...) return f(...) end end");
			script.Registry.Set("CastleStoryPlus.Wrap", wrap);
		}
		DynValue callback = DynValue.NewCallback((ScriptExecutionContext context, CallbackArguments args) =>
		{
			try
			{
				DynValue result = function(args);
				return result.IsValid ? result : DynValue.Void;
			}
			catch (Exception ex)
			{
				LogOnce("hook listener", ex.ToString());
				return DynValue.Void;
			}
		});
		return Call(wrap, "Closure", callback);
	}

	// Connects a C# action to one of the game's Lua hooks (a global like "Project" with "hk_OnSetStatus"): the
	// same as Hooks.Connect(Project.hk_OnSetStatus, f) in the game's Lua. Disconnected by the game when the script
	// dies, like every hook a Lua menu connects.
	public static bool ConnectHook(Script script, string global, string hook, Action action)
	{
		if (script == null)
		{
			return false;
		}
		DynValue owner = script.Globals.Get(global);
		DynValue hookValue = (owner.Type == DataType.Table || owner.Type == DataType.UserData) ? Index(script, owner, hook) : DynValue.Nil;
		Table hooks = script.Globals.Get("Hooks").Table;
		DynValue connect = (hooks != null) ? hooks.Get("Connect") : DynValue.Nil;
		if (hookValue.IsNil() || connect.IsNil())
		{
			LogOnce("ConnectHook", global + "." + hook + " not found");
			return false;
		}
		DynValue closure = Closure(script, (CallbackArguments args) =>
		{
			action();
			return DynValue.Void;
		});
		script.Call(connect, hookValue, closure);
		return true;
	}

	// A field of a table, or of a C# object or type the game hands to Lua (Project, UIGame...).
	private static DynValue Index(Script script, DynValue owner, string key)
	{
		if (owner.Type == DataType.Table)
		{
			return Get(owner.Table, key);
		}
		try
		{
			IUserData data = owner.UserData;
			return (data != null && data.Descriptor != null) ? data.Descriptor.Index(script, data, DynValue.NewString(key), true) : DynValue.Nil;
		}
		catch (Exception)
		{
			return DynValue.Nil;
		}
	}
}
