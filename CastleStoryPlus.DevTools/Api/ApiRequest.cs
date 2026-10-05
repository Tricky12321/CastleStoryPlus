using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Api;

// One API call: its query string and JSON body, with typed readers that fail with a clear message.
internal class ApiRequest
{
	public string Method;

	public string Path;

	public readonly Dictionary<string, string> Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	public string Body = string.Empty;

	private JObject _json;

	// A value from the query string, else from the JSON body.
	public string Get(string name)
	{
		if (Query.TryGetValue(name, out string value))
		{
			return value;
		}
		JObject json = Json();
		JToken token = (json != null) ? json[name] : null;
		if (token == null || token.Type == JTokenType.Null)
		{
			return null;
		}
		return (token.Type == JTokenType.String) ? (string)token : token.ToString(Newtonsoft.Json.Formatting.None);
	}

	public string String(string name, string fallback)
	{
		string value = Get(name);
		return (value != null) ? value : fallback;
	}

	public int Int(string name, int fallback)
	{
		string value = Get(name);
		if (value == null)
		{
			return fallback;
		}
		if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
		{
			throw new ApiException(400, "'" + name + "' is not a whole number: " + value);
		}
		return result;
	}

	public float Float(string name, float fallback)
	{
		string value = Get(name);
		if (value == null)
		{
			return fallback;
		}
		if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result))
		{
			throw new ApiException(400, "'" + name + "' is not a number: " + value);
		}
		return result;
	}

	public bool Bool(string name, bool fallback)
	{
		string value = Get(name);
		if (value == null)
		{
			return fallback;
		}
		return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
	}

	// "x,y,z" (or a JSON array [x, y, z]).
	public Vector3 Vector(string name)
	{
		string value = Get(name);
		if (value == null)
		{
			throw new ApiException(400, "'" + name + "' is missing (x,y,z)");
		}
		string[] parts = value.Trim('[', ']', ' ').Split(',');
		if (parts.Length != 3)
		{
			throw new ApiException(400, "'" + name + "' must be x,y,z: " + value);
		}
		float[] numbers = new float[3];
		for (int i = 0; i < 3; i++)
		{
			if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
			{
				throw new ApiException(400, "'" + name + "' must be x,y,z: " + value);
			}
		}
		return new Vector3(numbers[0], numbers[1], numbers[2]);
	}

	public string Required(string name)
	{
		string value = Get(name);
		if (string.IsNullOrEmpty(value))
		{
			throw new ApiException(400, "'" + name + "' is missing");
		}
		return value;
	}

	private JObject Json()
	{
		if (_json == null && Body.Length > 0 && Body.TrimStart().StartsWith("{"))
		{
			try
			{
				_json = JObject.Parse(Body);
			}
			catch (Exception ex)
			{
				throw new ApiException(400, "The body is not valid JSON: " + ex.Message);
			}
		}
		return _json;
	}
}

internal class ApiException : Exception
{
	public readonly int Status;

	public ApiException(int status, string message) : base(message)
	{
		Status = status;
	}
}
