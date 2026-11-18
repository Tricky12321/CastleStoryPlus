using System;
using CastleStoryPlus.Weather;
using Newtonsoft.Json;
using NUnit.Framework;

namespace CastleStoryPlus.UnitTests.Weather;

[TestFixture]
public class RainWireTests
{
	[Test]
	public void SaveTextIsCompactJsonWithTheThreeFields()
	{
		Assert.That(RainWire.SaveText(WeatherKind.Rain, 2.5f, 6f), Is.EqualTo("{\"kind\":1,\"hoursLeft\":2.5,\"rest\":6.0}"));
		Assert.That(RainWire.SaveText(WeatherKind.None, 0f, 0f), Is.EqualTo("{\"kind\":0,\"hoursLeft\":0.0,\"rest\":0.0}"));
	}

	[Test]
	public void SaveTextRoundsToThreeDecimals()
	{
		Assert.That(RainWire.SaveText(WeatherKind.Snow, 1.23456f, 0.0004f), Is.EqualTo("{\"kind\":2,\"hoursLeft\":1.235,\"rest\":0.0}"));
	}

	[TestCase(1, 2.5f, 3f)]
	[TestCase(2, 0.001f, 0f)]
	[TestCase(1, 11.75f, 6f)]
	public void SaveRoundTrips(int kind, float hours, float rest)
	{
		string text = RainWire.SaveText((WeatherKind)kind, hours, rest);
		RainWire.ReadSave(text, out WeatherKind readKind, out float readHours, out float readRest);
		Assert.That((int)readKind, Is.EqualTo(kind));
		Assert.That(readHours, Is.EqualTo(hours).Within(0.001f));
		Assert.That(readRest, Is.EqualTo(rest).Within(0.001f));
	}

	[Test]
	public void ReadSaveEmptyObjectIsNoWeatherAndAFullRest()
	{
		RainWire.ReadSave("{}", out WeatherKind kind, out float hours, out float rest);
		Assert.That(kind, Is.EqualTo(WeatherKind.None));
		Assert.That(hours, Is.EqualTo(0f));
		Assert.That(rest, Is.EqualTo(RainRules.RestHours));
	}

	[Test]
	public void ReadSaveWeatherWithoutHoursIsNoWeather()
	{
		RainWire.ReadSave("{\"kind\":1,\"hoursLeft\":0,\"rest\":2}", out WeatherKind kind, out float hours, out float rest);
		Assert.That(kind, Is.EqualTo(WeatherKind.None));
		Assert.That(hours, Is.EqualTo(0f));
		Assert.That(rest, Is.EqualTo(2f));
	}

	[Test]
	public void ReadSaveNegativeHoursIsNoWeather()
	{
		RainWire.ReadSave("{\"kind\":2,\"hoursLeft\":-4}", out WeatherKind kind, out float hours, out _);
		Assert.That(kind, Is.EqualTo(WeatherKind.None));
		Assert.That(hours, Is.EqualTo(0f));
	}

	[TestCase(3)]
	[TestCase(-1)]
	[TestCase(255)]
	public void ReadSaveUnknownKindIsNoWeather(int raw)
	{
		RainWire.ReadSave("{\"kind\":" + raw + ",\"hoursLeft\":3}", out WeatherKind kind, out float hours, out _);
		Assert.That(kind, Is.EqualTo(WeatherKind.None));
		Assert.That(hours, Is.EqualTo(0f));
	}

	[Test]
	public void ReadSaveIgnoresUnknownFields()
	{
		RainWire.ReadSave("{\"extra\":[1,2],\"kind\":1,\"hoursLeft\":1.5,\"rest\":0}", out WeatherKind kind, out float hours, out float rest);
		Assert.That(kind, Is.EqualTo(WeatherKind.Rain));
		Assert.That(hours, Is.EqualTo(1.5f));
		Assert.That(rest, Is.EqualTo(0f));
	}

	[TestCase("")]
	[TestCase("not json")]
	[TestCase("{\"kind\":")]
	public void ReadSaveThrowsOnBrokenText(string text)
	{
		Assert.Throws(Is.InstanceOf<Exception>(), () => RainWire.ReadSave(text, out _, out _, out _));
	}

	[Test]
	public void ReadSaveThrowsOnAFieldOfTheWrongType()
	{
		Assert.Throws(Is.InstanceOf<Exception>(), () => RainWire.ReadSave("{\"kind\":\"rain\"}", out _, out _, out _));
	}

	[Test]
	public void MessageIsFiveBytesKindThenALittleEndianFloat()
	{
		byte[] bytes = RainWire.EncodeMessage((byte)WeatherKind.Snow, 1.5f);
		// 1.5f = 0x3FC00000
		Assert.That(bytes, Is.EqualTo(new byte[] { 2, 0x00, 0x00, 0xC0, 0x3F }));
	}

	[TestCase(0, 0f)]
	[TestCase(1, 3.25f)]
	[TestCase(2, 0.05f)]
	[TestCase(255, 4.2e3f)]
	public void MessageRoundTrips(int kind, float hours)
	{
		byte[] bytes = RainWire.EncodeMessage((byte)kind, hours);
		Assert.That(RainWire.DecodeMessage(bytes, out byte readKind, out float readHours), Is.True);
		Assert.That((int)readKind, Is.EqualTo(kind));
		Assert.That(readHours, Is.EqualTo(hours));
	}

	[Test]
	public void DecodeRejectsTheWrongLength()
	{
		Assert.That(RainWire.DecodeMessage(null, out _, out _), Is.False);
		Assert.That(RainWire.DecodeMessage(new byte[0], out _, out _), Is.False);
		Assert.That(RainWire.DecodeMessage(new byte[4], out _, out _), Is.False);
		Assert.That(RainWire.DecodeMessage(new byte[6], out _, out _), Is.False);
	}

	[Test]
	public void ADecodedUnknownKindFailsTheValidation()
	{
		RainWire.DecodeMessage(RainWire.EncodeMessage(7, 1f), out byte kind, out float hours);
		Assert.That(RainWire.TryValidate(kind, hours, out _, out _), Is.False);
	}

	[TestCase(float.NaN)]
	[TestCase(float.PositiveInfinity)]
	[TestCase(float.NegativeInfinity)]
	[TestCase(-0.5f)]
	public void ValidateRejectsHoursThatAreNotAUsefulNumber(float hours)
	{
		Assert.That(RainWire.TryValidate(1, hours, out _, out _), Is.False);
	}

	[Test]
	public void ValidateAcceptsTheKnownKindsAndZeroHours()
	{
		Assert.That(RainWire.TryValidate(0, 0f, out WeatherKind none, out _), Is.True);
		Assert.That(none, Is.EqualTo(WeatherKind.None));
		Assert.That(RainWire.TryValidate(2, 5f, out WeatherKind snow, out float hours), Is.True);
		Assert.That(snow, Is.EqualTo(WeatherKind.Snow));
		Assert.That(hours, Is.EqualTo(5f));
		Assert.That(RainWire.TryValidate(3, 5f, out _, out _), Is.False);
		Assert.That(RainWire.TryValidate(255, 5f, out _, out _), Is.False);
	}
}
