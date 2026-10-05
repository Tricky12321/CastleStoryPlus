using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace CastleStoryPlus.Mcp.Protocol;

// The Model Context Protocol over stdio: one JSON-RPC 2.0 message per line. Only what a tool server needs:
// initialize, ping, tools/list and tools/call. Diagnostics go to stderr (stdout is the protocol).
public class McpServer
{
	private const string DefaultProtocolVersion = "2025-06-18";

	private readonly string _name;

	private readonly string _version;

	private readonly Dictionary<string, McpTool> _tools = new Dictionary<string, McpTool>();

	private readonly List<McpTool> _ordered;

	public McpServer(string name, string version, List<McpTool> tools)
	{
		_name = name;
		_version = version;
		_ordered = tools;
		foreach (McpTool tool in tools)
		{
			_tools[tool.Name] = tool;
		}
	}

	public async Task RunAsync(Stream input, Stream output)
	{
		using StreamReader reader = new StreamReader(input, new UTF8Encoding(false));
		using StreamWriter writer = new StreamWriter(output, new UTF8Encoding(false));
		writer.AutoFlush = true;
		while (true)
		{
			string line = await reader.ReadLineAsync();
			if (line == null)
			{
				return;
			}
			if (line.Trim().Length == 0)
			{
				continue;
			}
			JsonObject response = await HandleAsync(line);
			if (response != null)
			{
				await writer.WriteLineAsync(response.ToJsonString());
			}
		}
	}

	private async Task<JsonObject> HandleAsync(string line)
	{
		JsonObject message;
		try
		{
			message = JsonNode.Parse(line) as JsonObject;
		}
		catch (JsonException ex)
		{
			return Error(null, -32700, "Parse error: " + ex.Message);
		}
		if (message == null)
		{
			return Error(null, -32600, "Invalid request");
		}
		JsonNode id = message["id"]?.DeepClone();
		string method = (string)message["method"];
		// Notifications (no id) get no answer.
		if (id == null)
		{
			return null;
		}
		try
		{
			switch (method)
			{
				case "initialize":
					return Result(id, Initialize(message["params"] as JsonObject));
				case "ping":
					return Result(id, new JsonObject());
				case "tools/list":
					return Result(id, ListTools());
				case "tools/call":
					return Result(id, await CallToolAsync(message["params"] as JsonObject));
				default:
					return Error(id, -32601, "Method not found: " + method);
			}
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine(ex);
			return Error(id, -32603, ex.Message);
		}
	}

	private JsonObject Initialize(JsonObject parameters)
	{
		string version = (string)parameters?["protocolVersion"] ?? DefaultProtocolVersion;
		return new JsonObject
		{
			["protocolVersion"] = version,
			["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
			["serverInfo"] = new JsonObject { ["name"] = _name, ["version"] = _version },
			["instructions"] = "Tools for the running Castle Story game with the Castle Story Plus mod and its DevTools plugin. "
				+ "Start with game_status. Run the in-game tests with tests_run (waits for the results by default)."
		};
	}

	private JsonObject ListTools()
	{
		JsonArray tools = new JsonArray();
		foreach (McpTool tool in _ordered)
		{
			tools.Add(new JsonObject
			{
				["name"] = tool.Name,
				["description"] = tool.Description,
				["inputSchema"] = tool.InputSchema.DeepClone()
			});
		}
		return new JsonObject { ["tools"] = tools };
	}

	private async Task<JsonObject> CallToolAsync(JsonObject parameters)
	{
		string name = (string)parameters?["name"];
		if (name == null || !_tools.TryGetValue(name, out McpTool tool))
		{
			return McpTool.Text("Unknown tool: " + name, isError: true);
		}
		JsonObject arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
		try
		{
			return await tool.Handler(arguments);
		}
		catch (Exception ex)
		{
			return McpTool.Text(ex.Message, isError: true);
		}
	}

	private static JsonObject Result(JsonNode id, JsonObject result)
	{
		return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
	}

	private static JsonObject Error(JsonNode id, int code, string message)
	{
		return new JsonObject
		{
			["jsonrpc"] = "2.0",
			["id"] = id,
			["error"] = new JsonObject { ["code"] = code, ["message"] = message }
		};
	}
}
