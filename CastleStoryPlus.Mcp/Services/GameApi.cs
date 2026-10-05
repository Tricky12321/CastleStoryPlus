using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace CastleStoryPlus.Mcp.Services;

// Calls the DevTools API in the running game (http://127.0.0.1:<port>/api/...).
public class GameApi
{
	private readonly HttpClient _http;

	private readonly string _baseUrl;

	public GameApi(int port)
	{
		_baseUrl = "http://127.0.0.1:" + port;
		_http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
	}

	public async Task<string> CallAsync(string method, string path, Dictionary<string, string> query = null, JsonObject body = null)
	{
		StringBuilder url = new StringBuilder(_baseUrl).Append(path);
		if (query != null)
		{
			char separator = '?';
			foreach (KeyValuePair<string, string> pair in query)
			{
				if (string.IsNullOrEmpty(pair.Value))
				{
					continue;
				}
				url.Append(separator).Append(Uri.EscapeDataString(pair.Key)).Append('=').Append(Uri.EscapeDataString(pair.Value));
				separator = '&';
			}
		}
		using HttpRequestMessage request = new HttpRequestMessage(new HttpMethod(method), url.ToString());
		if (body != null)
		{
			request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
		}
		HttpResponseMessage response;
		try
		{
			response = await _http.SendAsync(request);
		}
		catch (HttpRequestException ex)
		{
			throw new InvalidOperationException("The game does not answer at " + _baseUrl + " (not running, still starting, or the DevTools plugin/API is off): " + ex.Message);
		}
		catch (TaskCanceledException)
		{
			throw new InvalidOperationException("The game did not answer within 60 s (" + method + " " + path + ")");
		}
		string text = await response.Content.ReadAsStringAsync();
		if (!response.IsSuccessStatusCode)
		{
			throw new InvalidOperationException("HTTP " + (int)response.StatusCode + " from " + method + " " + path + ": " + text);
		}
		return text;
	}

	public async Task<JsonObject> CallJsonAsync(string method, string path, Dictionary<string, string> query = null, JsonObject body = null)
	{
		return JsonNode.Parse(await CallAsync(method, path, query, body)) as JsonObject;
	}
}
