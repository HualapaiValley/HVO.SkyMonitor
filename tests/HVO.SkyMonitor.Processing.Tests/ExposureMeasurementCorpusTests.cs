using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
using static HVO.SkyMonitor.Processing.Tests.ExposureMeasurementTests;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Manual")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "MSTest handles its async tests.")]
public sealed class ExposureMeasurementCorpusTests
{
    private const string CorpusVariable = "HVO_EXPOSURE_MEASUREMENT_CORPUS";

    [TestMethod]
    public async Task NovemberMonoCorporaRetainRawOraclesAndUnsolvedDetections()
    {
        var root = CorpusDirectory("mono-november");
        using var oracle = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "expected-mono.json")));
        foreach (var frame in oracle.RootElement.EnumerateArray())
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(root, frame.GetProperty("artifact").GetString()!));
            Assert.AreEqual(frame.GetProperty("rawSha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
            var raw = CreateRaw(Layout(1936, 1216), bytes, frame.GetProperty("captureId").GetGuid());
            var linear = await Normalize(raw);
            var result = await Run(raw, linear, Bind(raw, linear));
            var stats = result.RawBrightness!;
            Assert.AreEqual(frame.GetProperty("pixelCount").GetInt64(), stats.SampleCount);
            Assert.AreEqual(frame.GetProperty("minAdu").GetUInt16(), stats.Minimum);
            Assert.AreEqual(frame.GetProperty("maxAdu").GetUInt16(), stats.Maximum);
            Assert.AreEqual(frame.GetProperty("sumAdu").GetUInt64(), stats.Sum);
            Assert.AreEqual(frame.GetProperty("zeroCount").GetInt64(), stats.ZeroCount);
            Assert.AreEqual(frame.GetProperty("whiteLevelCount").GetInt64(), stats.SensorClipping.Count);
            Assert.AreEqual(ExposureMeasurementStatus.Usable, result.Sources.Status, result.Sources.Reason);
            Assert.IsNotEmpty(result.Sources.Detections);
        }
    }

    [TestMethod]
    public async Task StagedGeometryReplayMatchesChecksummedSources()
    {
        var root = CorpusDirectory("geometry");
        using var oracle = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "geometry-oracle.json")));
        var sources = oracle.RootElement.GetProperty("sourcesAt103").EnumerateArray()
            .Select(source => new PixelPoint(source[0].GetDouble(), source[1].GetDouble())).ToArray();
        foreach (var frame in oracle.RootElement.GetProperty("frames").EnumerateArray())
        {
            var name = frame.GetProperty("file").GetString()!;
            var bayer = name.StartsWith("bayer-", StringComparison.Ordinal);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(root, name));
            Assert.AreEqual(frame.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
            var shift = frame.GetProperty("inputPositionMinusReference103");
            var expected = sources.Select(source => new PixelPoint(source.X + shift[0].GetDouble(), source.Y + shift[1].GetDouble())).ToArray();
            await AssertGeometry(Layout(160, 128, bayer), bytes, expected, bayer);
        }
    }

    private static string CorpusDirectory(string name)
    {
        var root = Environment.GetEnvironmentVariable(CorpusVariable);
        Assert.IsFalse(string.IsNullOrWhiteSpace(root), $"Set {CorpusVariable} to the retained exposure evidence root.");
        return Path.Combine(root!, name);
    }
}
