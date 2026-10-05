using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace CastleStoryPlus.Mcp.Protocol;

// One MCP tool: name, description, JSON schema of its arguments, and what it does.
public class McpTool
{
	public string Name;

	public string Description;

	public JsonObject InputSchema;

	public Func<JsonObject, Task<JsonObject>> Handler;

	public static JsonObject Text(string text, bool isError = false)
	{
		return new JsonObject
		{
			["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
			["isError"] = isError
		};
	}

	public static JsonObject Image(string base64Png, string caption)
	{
		return new JsonObject
		{
			["content"] = new JsonArray
			{
				new JsonObject { ["type"] = "image", ["data"] = base64Png, ["mimeType"] = "image/png" },
				new JsonObject { ["type"] = "text", ["text"] = caption }
			},
			["isError"] = false
		};
	}
}
