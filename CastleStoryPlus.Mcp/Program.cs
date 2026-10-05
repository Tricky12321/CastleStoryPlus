using System;
using System.Threading.Tasks;
using CastleStoryPlus.Mcp.Protocol;
using CastleStoryPlus.Mcp.Services;
using CastleStoryPlus.Mcp.Tools;

namespace CastleStoryPlus.Mcp;

// MCP server over stdio: Claude's tools for the running game, through the DevTools API on localhost.
// The API's port: environment variable CSP_API_PORT (default 27860, as [Api] Port in the DevTools config).
public static class Program
{
	public static async Task<int> Main()
	{
		int port = 27860;
		string portText = Environment.GetEnvironmentVariable("CSP_API_PORT");
		if (!string.IsNullOrEmpty(portText) && int.TryParse(portText, out int parsed))
		{
			port = parsed;
		}
		GameApi api = new GameApi(port);
		McpServer server = new McpServer("castlestory", "0.1.0", GameTools.Build(api));
		await server.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput());
		return 0;
	}
}
