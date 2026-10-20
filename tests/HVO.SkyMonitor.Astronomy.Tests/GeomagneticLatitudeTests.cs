using System.Globalization;
using System.Text.Json.Nodes;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class GeomagneticLatitudeTests
{
    public static IEnumerable<object[]> Vectors()
    {
        var vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/space-weather/swpc-v1/rounding-vectors.json")))!;
        foreach (var kind in new[] { "geomagneticLatitude", "relevanceDifference" })
            foreach (var vector in vectors[kind]!.AsArray()) yield return [kind, vector!.ToJsonString()];
    }

    [TestMethod]
    [DataRow("geomagneticLatitude", 0)]
    [DataRow("geomagneticLatitude", 1)]
    [DataRow("geomagneticLatitude", 2)]
    [DataRow("geomagneticLatitude", 3)]
    [DataRow("geomagneticLatitude", 4)]
    [DataRow("geomagneticLatitude", 5)]
    [DataRow("geomagneticLatitude", 6)]
    [DataRow("geomagneticLatitude", 7)]
    [DataRow("geomagneticLatitude", 8)]
    [DataRow("geomagneticLatitude", 9)]
    [DataRow("geomagneticLatitude", 10)]
    [DataRow("geomagneticLatitude", 11)]
    [DataRow("relevanceDifference", 0)]
    [DataRow("relevanceDifference", 1)]
    [DataRow("relevanceDifference", 2)]
    [DataRow("relevanceDifference", 3)]
    [DataRow("relevanceDifference", 4)]
    [DataRow("relevanceDifference", 5)]
    [DataRow("relevanceDifference", 6)]
    [DataRow("relevanceDifference", 7)]
    [DataRow("relevanceDifference", 8)]
    [DataRow("relevanceDifference", 9)]
    [DataRow("relevanceDifference", 10)]
    [DataRow("relevanceDifference", 11)]
    [DataRow("relevanceDifference", 12)]
    [DataRow("relevanceDifference", 13)]
    [DataRow("relevanceDifference", 14)]
    [DataRow("relevanceDifference", 15)]
    [DataRow("relevanceDifference", 16)]
    [DataRow("relevanceDifference", 17)]
    public void ExactBinaryRoundingMatchesGoldenVectors(string kind, int index)
    {
        var vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/space-weather/swpc-v1/rounding-vectors.json")))!;
        var vector = vectors[kind]![index]!;
        var raw = double.Parse(vector["unroundedDouble"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var expected = vector["expectedRounded"]!.GetValue<double>();
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(GeomagneticLatitude.RoundToTenth(raw)));
        if (kind == "geomagneticLatitude")
        {
            var input = vector["input"]!;
            Assert.AreEqual(expected, GeomagneticLatitude.Compute(input["latitudeDeg"]!.GetValue<double>(), input["longitudeDeg"]!.GetValue<double>()));
        }
    }

    [TestMethod]
    public void DefaultDotnetRoundingDisagreesWithAtLeastOneGoldenVector()
    {
        Assert.IsTrue(Vectors().Any(row =>
        {
            var vector = JsonNode.Parse((string)row[1])!;
            return Math.Round(double.Parse(vector["unroundedDouble"]!.GetValue<string>(), CultureInfo.InvariantCulture), 1) != vector["expectedRounded"]!.GetValue<double>();
        }));
    }

    [TestMethod]
    public void SiteLatitudeMatchesContractExample()
    {
        var contract = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/space-weather/space-weather-facts-v1.md"));
        var m = System.Text.RegularExpressions.Regex.Match(contract, @"At HVO \(([-0-9.]+), ([-0-9.]+), geomagnetic latitude ([0-9.]+)°\)");
        Assert.IsTrue(m.Success);
        Assert.AreEqual(double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), GeomagneticLatitude.Compute(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));
    }

    [TestMethod]
    [DataRow(double.NaN, 0)]
    [DataRow(double.PositiveInfinity, 0)]
    [DataRow(90.1, 0)]
    [DataRow(0, -180.1)]
    public void RejectsInvalidLocations(double latitude, double longitude)
        => Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => GeomagneticLatitude.Compute(latitude, longitude));
}
