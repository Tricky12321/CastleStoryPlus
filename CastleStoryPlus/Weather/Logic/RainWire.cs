using System;
using Newtonsoft.Json.Linq;

namespace CastleStoryPlus.Weather;

// What the weather writes next to the world's save and sends to the other players, and the checks on what comes
// back. Pure (Newtonsoft.Json only, which the game has and the unit tests get from NuGet).
internal static class RainWire
{
	// The weather message is a byte (the kind) and a float (the hours), 5 bytes, as UNET writes them (little endian).
	internal const int MessageBytes = 5;

	// The save: {"kind":1,"hoursLeft":2.5,"rest":6.0}. Rounded to 3 decimals.
	internal static string SaveText(WeatherKind kind, float hoursLeft, float rest)
	{
		return new JObject { ["kind"] = (int)kind, ["hoursLeft"] = Math.Round(hoursLeft, 3), ["rest"] = Math.Round(rest, 3) }.ToString(Newtonsoft.Json.Formatting.None);
	}

	// Reads a save. Throws on text that is not JSON (the caller logs it and starts without weather). A missing rest
	// is a full rest; a missing or unknown kind, or no hours left (or not a real number), is no weather.
	internal static void ReadSave(string text, out WeatherKind kind, out float hoursLeft, out float rest)
	{
		JObject data = JObject.Parse(text);
		rest = (float?)data["rest"] ?? RainRules.RestHours;
		int raw = (int?)data["kind"] ?? 0;
		float hours = (float?)data["hoursLeft"] ?? 0f;
		kind = WeatherKind.None;
		hoursLeft = 0f;
		if (raw != 0 && Enum.IsDefined(typeof(WeatherKind), raw) && hours > 0f && !float.IsInfinity(hours))
		{
			kind = (WeatherKind)raw;
			hoursLeft = hours;
		}
	}

	internal static byte[] EncodeMessage(WeatherKind kind, float hours)
	{
		byte[] bytes = new byte[MessageBytes];
		bytes[0] = (byte)kind;
		byte[] value = BitConverter.GetBytes(hours);
		if (!BitConverter.IsLittleEndian)
		{
			Array.Reverse(value);
		}
		Array.Copy(value, 0, bytes, 1, 4);
		return bytes;
	}

	internal static bool TryDecodeMessage(byte[] bytes, out WeatherKind kind, out float hours)
	{
		kind = WeatherKind.None;
		hours = 0f;
		if (bytes == null || bytes.Length != MessageBytes)
		{
			return false;
		}
		byte[] value = new byte[4];
		Array.Copy(bytes, 1, value, 0, 4);
		if (!BitConverter.IsLittleEndian)
		{
			Array.Reverse(value);
		}
		return TryValidate(bytes[0], BitConverter.ToSingle(value, 0), out kind, out hours);
	}

	// A message from the host is only used when its kind is one we know and its hours are a real, not negative number
	// (a NaN would never run out).
	internal static bool TryValidate(byte rawKind, float rawHours, out WeatherKind kind, out float hours)
	{
		kind = WeatherKind.None;
		hours = 0f;
		if (!Enum.IsDefined(typeof(WeatherKind), (int)rawKind) || float.IsNaN(rawHours) || float.IsInfinity(rawHours) || rawHours < 0f)
		{
			return false;
		}
		kind = (WeatherKind)rawKind;
		hours = rawHours;
		return true;
	}
}
