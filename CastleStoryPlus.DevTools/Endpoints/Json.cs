using System;
using System.Collections.Generic;
using UnityEngine;

namespace CastleStoryPlus.DevTools.Endpoints;

// Small helpers for the API's answers: Unity types as plain JSON (Vector3 itself loops when serialized).
internal static class Json
{
	public static float[] Vec(Vector3 v)
	{
		return new float[3] { Round(v.x), Round(v.y), Round(v.z) };
	}

	public static float Round(float value)
	{
		return (float)Math.Round(value, 2);
	}

	public static Dictionary<string, object> Obj(params object[] pairs)
	{
		Dictionary<string, object> result = new Dictionary<string, object>();
		for (int i = 0; i + 1 < pairs.Length; i += 2)
		{
			result[(string)pairs[i]] = pairs[i + 1];
		}
		return result;
	}
}
