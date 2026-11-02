using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CastleStoryPlus.DevTools.Api;
using static CastleStoryPlus.DevTools.Endpoints.Json;

namespace CastleStoryPlus.DevTools.Endpoints;

// Which managed method Mono compiled to a code address: for a `perf record` of the game (perf sees the compiled code
// only as addresses in anonymous memory, "[JIT] tid N"). The addresses of a recording are asked while the game still
// runs (the code moves between runs), and the answer's `map` is the text of a perf map file
// (/tmp/perf-<pid>.map: "start size name" per method), which perf reads when it reports.
// Mono's runtime library is taken from the process itself (dlopen of the library already loaded), since the player
// keeps it out of the loader's search path. Linux only.
internal static class JitEndpoints
{
	private const int RtldNow = 2;

	private const int RtldNoLoad = 4;

	[DllImport("libdl.so.2")]
	private static extern IntPtr dlopen(string path, int flags);

	[DllImport("libdl.so.2")]
	private static extern IntPtr dlsym(IntPtr handle, string name);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate IntPtr DomainGet();

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate IntPtr TableFind(IntPtr domain, IntPtr address);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate IntPtr InfoPointer(IntPtr info);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int InfoInt(IntPtr info);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate IntPtr MethodFullName(IntPtr method, int signature);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void FreePointer(IntPtr pointer);

	private static DomainGet _domainGet;

	private static TableFind _tableFind;

	private static InfoPointer _getMethod;

	private static InfoPointer _codeStart;

	private static InfoInt _codeSize;

	private static MethodFullName _fullName;

	private static FreePointer _free;

	private static string _path;

	private static IntPtr _handle;

	private static readonly Dictionary<string, long> Addresses = new Dictionary<string, long>();

	internal static void Add(List<Route> routes)
	{
		routes.Add(new Route("GET", "/api/jit/info", "Where Mono's runtime library is loaded and the addresses of the functions used by /api/jit/resolve, with a probe: this very method's compiled code looked up and named", Info));
		routes.Add(new Route("POST", "/api/jit/resolve", "The managed methods Mono compiled at the given code addresses (for a perf recording of this very run): per distinct method its code start, size and name, and `map` as a perf map file's text", Resolve)
		{
			Parameters = "body: the addresses, hexadecimal, one per line or separated by commas or spaces (0x prefix optional); unknown (not managed code) addresses are counted, not listed"
		});
	}

	private static void Bind()
	{
		if (_tableFind != null)
		{
			return;
		}
		// Unity's own runtime (libmonobdwgc, or libmono in Unity 5), not a system Mono that may be loaded as well.
		string path = null;
		string[] names = { "libmonobdwgc-2.0.so", "/libmono.so", "libmonosgen-2.0.so" };
		foreach (string wanted in names)
		{
			foreach (string line in File.ReadAllLines("/proc/self/maps"))
			{
				int at = line.IndexOf('/');
				if (at >= 0 && line.Substring(at).Contains(wanted))
				{
					path = line.Substring(at);
					break;
				}
			}
			if (path != null)
			{
				break;
			}
		}
		if (path == null)
		{
			throw new ApiException(501, "Mono's runtime library is not among the loaded libraries");
		}
		IntPtr handle = dlopen(path, RtldNow | RtldNoLoad);
		if (handle == IntPtr.Zero)
		{
			throw new ApiException(501, "dlopen failed for " + path);
		}
		_path = path;
		_handle = handle;
		_domainGet = Symbol<DomainGet>(handle, "mono_domain_get");
		_tableFind = Symbol<TableFind>(handle, "mono_jit_info_table_find");
		_getMethod = Symbol<InfoPointer>(handle, "mono_jit_info_get_method");
		_codeStart = Symbol<InfoPointer>(handle, "mono_jit_info_get_code_start");
		_codeSize = Symbol<InfoInt>(handle, "mono_jit_info_get_code_size");
		_fullName = Symbol<MethodFullName>(handle, "mono_method_full_name");
		IntPtr free = dlsym(handle, "mono_free");
		_free = (free == IntPtr.Zero) ? null : (FreePointer)Marshal.GetDelegateForFunctionPointer(free, typeof(FreePointer));
	}

	private static T Symbol<T>(IntPtr handle, string name) where T : class
	{
		IntPtr pointer = dlsym(handle, name);
		if (pointer == IntPtr.Zero)
		{
			throw new ApiException(501, "Mono's runtime has no " + name);
		}
		Addresses[name] = pointer.ToInt64();
		return Marshal.GetDelegateForFunctionPointer(pointer, typeof(T)) as T;
	}

	private static object Info(ApiRequest request)
	{
		Bind();
		Dictionary<string, object> symbols = new Dictionary<string, object>();
		foreach (KeyValuePair<string, long> pair in Addresses)
		{
			symbols[pair.Key] = "0x" + pair.Value.ToString("x", CultureInfo.InvariantCulture);
		}
		List<string> maps = new List<string>();
		foreach (string line in File.ReadAllLines("/proc/self/maps"))
		{
			if (line.Contains(_path))
			{
				maps.Add(line);
			}
		}
		IntPtr domain = _domainGet();
		IntPtr root = Symbol<DomainGet>(_handle, "mono_get_root_domain")();
		object probe = null;
		if (request.Bool("probe", false) && (root != IntPtr.Zero || domain != IntPtr.Zero))
		{
			IntPtr code = typeof(JitEndpoints).GetMethod(nameof(Info), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).MethodHandle.GetFunctionPointer();
			IntPtr info = _tableFind(root != IntPtr.Zero ? root : domain, code);
			probe = Obj("code", "0x" + code.ToInt64().ToString("x", CultureInfo.InvariantCulture), "found", info != IntPtr.Zero,
				"method", info == IntPtr.Zero ? null : FullName(_getMethod(info)), "start", info == IntPtr.Zero ? null : "0x" + _codeStart(info).ToInt64().ToString("x", CultureInfo.InvariantCulture), "size", info == IntPtr.Zero ? 0 : _codeSize(info));
		}
		return Obj("path", _path, "handle", "0x" + _handle.ToInt64().ToString("x", CultureInfo.InvariantCulture), "maps", maps, "symbols", symbols,
			"domain", "0x" + domain.ToInt64().ToString("x", CultureInfo.InvariantCulture), "rootDomain", "0x" + root.ToInt64().ToString("x", CultureInfo.InvariantCulture), "probe", probe);
	}

	private static object Resolve(ApiRequest request)
	{
		Bind();
		string[] parts = request.Body.Split(new[] { '\n', '\r', ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
		Dictionary<long, string> methods = new Dictionary<long, string>();
		Dictionary<long, int> sizes = new Dictionary<long, int>();
		int unknown = 0;
		int bad = 0;
		IntPtr domain = Symbol<DomainGet>(_handle, "mono_get_root_domain")();
		if (domain == IntPtr.Zero)
		{
			domain = _domainGet();
		}
		if (domain == IntPtr.Zero)
		{
			throw new ApiException(501, "Mono has no domain here");
		}
		foreach (string part in parts)
		{
			string text = part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part.Substring(2) : part;
			if (!long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long address))
			{
				bad++;
				continue;
			}
			IntPtr info = _tableFind(domain, new IntPtr(address));
			if (info == IntPtr.Zero)
			{
				unknown++;
				continue;
			}
			long start = _codeStart(info).ToInt64();
			if (methods.ContainsKey(start))
			{
				continue;
			}
			IntPtr method = _getMethod(info);
			methods[start] = (method == IntPtr.Zero) ? "(trampoline)" : FullName(method);
			sizes[start] = _codeSize(info);
		}
		StringBuilder map = new StringBuilder();
		List<object> rows = new List<object>();
		foreach (KeyValuePair<long, string> pair in methods)
		{
			map.Append(pair.Key.ToString("x", CultureInfo.InvariantCulture)).Append(' ').Append(sizes[pair.Key].ToString("x", CultureInfo.InvariantCulture)).Append(' ').Append(pair.Value).Append('\n');
			rows.Add(Obj("start", "0x" + pair.Key.ToString("x", CultureInfo.InvariantCulture), "size", sizes[pair.Key], "method", pair.Value));
		}
		return Obj(
			"asked", parts.Length,
			"methods", rows.Count,
			"unknown", unknown,
			"notHex", bad,
			"resolved", rows,
			"map", map.ToString());
	}

	private static string FullName(IntPtr method)
	{
		IntPtr text = _fullName(method, 1);
		if (text == IntPtr.Zero)
		{
			return "(unnamed)";
		}
		string name = Marshal.PtrToStringAnsi(text) ?? "(unnamed)";
		if (_free != null)
		{
			_free(text);
		}
		// Perf takes the rest of the line as the name; spaces are fine, newlines are not.
		return name.Replace('\n', ' ');
	}
}
