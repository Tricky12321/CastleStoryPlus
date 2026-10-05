using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace CastleStoryPlus.DevTools.Api;

// A small HTTP/1.1 server on 127.0.0.1 only (no other computer can reach it), with JSON answers. Plain sockets:
// the game's Mono 2.6 has no dependable HttpListener. Each call gets its own thread; the work itself is done on
// the game's main thread (MainThread).
internal class ApiServer
{
	private const int MaxHeaderBytes = 64 * 1024;

	private const int MaxBodyBytes = 4 * 1024 * 1024;

	private readonly int _port;

	private readonly List<Route> _routes;

	private TcpListener _listener;

	private Thread _thread;

	private volatile bool _running;

	public ApiServer(int port, List<Route> routes)
	{
		_port = port;
		_routes = routes;
	}

	public void Start()
	{
		try
		{
			_listener = new TcpListener(IPAddress.Loopback, _port);
			_listener.Start();
		}
		catch (Exception ex)
		{
			DevToolsPlugin.Log.LogError("API: could not listen on 127.0.0.1:" + _port + ": " + ex.Message);
			return;
		}
		_running = true;
		_thread = new Thread(AcceptLoop);
		_thread.IsBackground = true;
		_thread.Name = "CastleStoryPlus.DevTools API";
		_thread.Start();
		DevToolsPlugin.Log.LogInfo("API: listening on http://127.0.0.1:" + _port + "/api");
	}

	public void Stop()
	{
		_running = false;
		try
		{
			if (_listener != null)
			{
				_listener.Stop();
			}
		}
		catch (Exception)
		{
			// Already closed.
		}
	}

	private void AcceptLoop()
	{
		while (_running)
		{
			TcpClient client;
			try
			{
				client = _listener.AcceptTcpClient();
			}
			catch (Exception)
			{
				if (!_running)
				{
					return;
				}
				continue;
			}
			Thread worker = new Thread(() => Serve(client));
			worker.IsBackground = true;
			worker.Start();
		}
	}

	private void Serve(TcpClient client)
	{
		try
		{
			using (client)
			{
				NetworkStream stream = client.GetStream();
				stream.ReadTimeout = 10000;
				ApiRequest request;
				try
				{
					request = Read(stream);
				}
				catch (ApiException ex)
				{
					Write(stream, ex.Status, Error(ex.Message));
					return;
				}
				if (request == null)
				{
					return;
				}
				int status = 200;
				object result;
				try
				{
					result = Dispatch(request);
				}
				catch (ApiException ex)
				{
					status = ex.Status;
					result = Error(ex.Message);
				}
				catch (TimeoutException ex)
				{
					status = 504;
					result = Error(ex.Message);
				}
				catch (Exception ex)
				{
					status = 500;
					result = Error(ex.GetType().Name + ": " + ex.Message, ex.StackTrace);
				}
				Write(stream, status, result);
			}
		}
		catch (Exception ex)
		{
			DevToolsPlugin.Log.LogWarning("API: " + ex.Message);
		}
	}

	private object Dispatch(ApiRequest request)
	{
		if (request.Method == "GET" && (request.Path == "/" || request.Path == "/api" || request.Path == "/api/"))
		{
			return Index();
		}
		bool pathFound = false;
		foreach (Route route in _routes)
		{
			if (!string.Equals(route.Path, request.Path, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			pathFound = true;
			if (route.Method != request.Method)
			{
				continue;
			}
			if (route.OffMainThread)
			{
				return route.Handler(request);
			}
			return MainThread.Run(() => route.Handler(request), route.TimeoutMs);
		}
		if (pathFound)
		{
			throw new ApiException(405, request.Method + " is not allowed on " + request.Path);
		}
		throw new ApiException(404, "No endpoint " + request.Method + " " + request.Path + " (GET /api lists them)");
	}

	private object Index()
	{
		List<object> endpoints = new List<object>();
		foreach (Route route in _routes)
		{
			endpoints.Add(new Dictionary<string, object>
			{
				{ "method", route.Method },
				{ "path", route.Path },
				{ "description", route.Description },
				{ "parameters", route.Parameters }
			});
		}
		return new Dictionary<string, object>
		{
			{ "name", DevToolsPlugin.Name },
			{ "version", DevToolsPlugin.Version },
			{ "endpoints", endpoints }
		};
	}

	private static Dictionary<string, object> Error(string message, string details = null)
	{
		Dictionary<string, object> error = new Dictionary<string, object> { { "error", message } };
		if (details != null)
		{
			error["details"] = details;
		}
		return error;
	}

	private static ApiRequest Read(Stream stream)
	{
		MemoryStream header = new MemoryStream();
		int matched = 0;
		while (matched < 4)
		{
			int b = stream.ReadByte();
			if (b < 0)
			{
				return null;
			}
			header.WriteByte((byte)b);
			matched = (b == ((matched % 2 == 0) ? '\r' : '\n')) ? (matched + 1) : ((b == '\r') ? 1 : 0);
			if (header.Length > MaxHeaderBytes)
			{
				throw new ApiException(431, "Request headers too large");
			}
		}
		string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split(new string[1] { "\r\n" }, StringSplitOptions.None);
		string[] first = lines[0].Split(' ');
		if (first.Length < 2)
		{
			throw new ApiException(400, "Bad request line");
		}
		ApiRequest request = new ApiRequest { Method = first[0].ToUpperInvariant() };
		string target = first[1];
		int question = target.IndexOf('?');
		request.Path = Decode((question >= 0) ? target.Substring(0, question) : target).TrimEnd('/');
		if (request.Path.Length == 0)
		{
			request.Path = "/";
		}
		if (question >= 0)
		{
			foreach (string pair in target.Substring(question + 1).Split('&'))
			{
				if (pair.Length == 0)
				{
					continue;
				}
				int equals = pair.IndexOf('=');
				string key = Decode((equals >= 0) ? pair.Substring(0, equals) : pair);
				request.Query[key] = (equals >= 0) ? Decode(pair.Substring(equals + 1)) : "true";
			}
		}
		int length = 0;
		for (int i = 1; i < lines.Length; i++)
		{
			int colon = lines[i].IndexOf(':');
			if (colon > 0 && lines[i].Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
			{
				int.TryParse(lines[i].Substring(colon + 1).Trim(), out length);
			}
		}
		if (length > MaxBodyBytes)
		{
			throw new ApiException(413, "Request body too large");
		}
		if (length > 0)
		{
			byte[] body = new byte[length];
			int read = 0;
			while (read < length)
			{
				int n = stream.Read(body, read, length - read);
				if (n <= 0)
				{
					break;
				}
				read += n;
			}
			request.Body = Encoding.UTF8.GetString(body, 0, read);
		}
		return request;
	}

	private static string Decode(string text)
	{
		return Uri.UnescapeDataString(text.Replace('+', ' '));
	}

	private static void Write(Stream stream, int status, object result)
	{
		string json = JsonConvert.SerializeObject(result, Formatting.Indented, new JsonSerializerSettings
		{
			ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
			NullValueHandling = NullValueHandling.Include
		});
		byte[] body = Encoding.UTF8.GetBytes(json + "\n");
		string head = "HTTP/1.1 " + status + " " + Reason(status) + "\r\n"
			+ "Content-Type: application/json; charset=utf-8\r\n"
			+ "Content-Length: " + body.Length + "\r\n"
			+ "Connection: close\r\n\r\n";
		byte[] headBytes = Encoding.ASCII.GetBytes(head);
		stream.Write(headBytes, 0, headBytes.Length);
		stream.Write(body, 0, body.Length);
		stream.Flush();
	}

	private static string Reason(int status)
	{
		switch (status)
		{
			case 200:
				return "OK";
			case 400:
				return "Bad Request";
			case 404:
				return "Not Found";
			case 405:
				return "Method Not Allowed";
			case 409:
				return "Conflict";
			case 413:
				return "Payload Too Large";
			case 431:
				return "Request Header Fields Too Large";
			case 504:
				return "Gateway Timeout";
			default:
				return (status >= 500) ? "Internal Server Error" : "Error";
		}
	}
}
