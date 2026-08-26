using System.Text;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class PresentationLayerPayloadTests
{
    [TestMethod]
    public void PayloadRoundTripsCanonicallyWithStableContentIdentity()
    {
        var payload = PresentationLayerPayloadJson.Create(new string('A', 64), 640, 480,
            markers: [new(new(20.5, 30.25), 6, new(144, 144, 144))],
            segments: [new(new(0, 0), new(639, 479), 1, new(96, 160, 255))],
            textBlocks: [new(PresentationTextAnchor.TopRight, default, ["Cloud 12.3%"], 1, 4, 2, new(255, 255, 255))],
            tileMask: new(2, 2, PresentationTileMaskV1.RowMajorLsbFirst, new byte[] { 5 }, 1, new(255, 64, 32)));
        var bytes = PresentationLayerPayloadJson.Serialize(payload);
        var parsed = PresentationLayerPayloadJson.Parse(bytes);

        Assert.IsTrue(parsed.IsValid, parsed.ErrorPath);
        Assert.AreEqual(payload.ContentIdentitySha256, parsed.Payload!.ContentIdentitySha256);
        CollectionAssert.AreEqual(bytes, PresentationLayerPayloadJson.Serialize(parsed.Payload));
        Assert.IsFalse(parsed.Payload.Markers is PresentationMarkerV1[]);
    }

    [TestMethod]
    public void ParserRejectsMalformedNonCanonicalIdentityAndBounds()
    {
        var payload = PresentationLayerPayloadJson.Create(new string('A', 64), 10, 10);
        var json = Encoding.UTF8.GetString(PresentationLayerPayloadJson.Serialize(payload));
        var malformed = new[]
        {
            "null",
            json.Replace("\"widthPixels\":10", "\"widthPixels\":1e1", StringComparison.Ordinal),
            json.Replace("\"markers\":[]", "\"markers\":[],\"MARKERS\":[]", StringComparison.Ordinal),
            json.Replace(payload.ContentIdentitySha256, new string('F', 64), StringComparison.Ordinal),
            "{" + json[1..^1] + ",\"unknown\":true}"
        };
        foreach (var candidate in malformed)
            Assert.IsFalse(PresentationLayerPayloadJson.Parse(Encoding.UTF8.GetBytes(candidate)).IsValid, candidate);
        Assert.IsFalse(PresentationLayerPayloadJson.Parse(new byte[PresentationLayerPayloadJson.MaximumPayloadBytes + 1]).IsValid);
        Assert.ThrowsExactly<ArgumentException>(() => PresentationLayerPayloadJson.Create(new string('A', 64), 10, 10,
            markers: Enumerable.Repeat(new PresentationMarkerV1(default, 1, new()), PresentationLayerPayloadV1.MaximumMarkers + 1)));
        Assert.ThrowsExactly<ArgumentException>(() => PresentationLayerPayloadJson.Create(new string('A', 64), 10, 10,
            tileMask: new(2, 2, PresentationTileMaskV1.RowMajorLsbFirst, new byte[] { 0xF5 }, 1, new())));
    }

    [TestMethod]
    public void MetadataProducerConsumesFactsWithoutAnyBasePixelParameter()
    {
        var facts = new PresentationMetadataFactsV1(new string('B', 64), ["Capture"], [], [], ["Clear"]);
        var payload = PresentationLayerProducers.FromMetadataFacts(facts, 320, 240);

        Assert.AreEqual(new string('B', 64), payload.SourceIdentitySha256);
        Assert.HasCount(4, payload.TextBlocks);
        Assert.IsFalse(typeof(PresentationLayerProducers).GetMethods().Any(method => method.GetParameters().Any(parameter =>
            parameter.ParameterType == typeof(ReadOnlyMemory<byte>) || parameter.ParameterType == typeof(byte[]))));
    }

    [TestMethod]
    public void CreateFreezesCallerCollectionsAndRejectsOversizedCanonicalPayload()
    {
        var markers = new List<PresentationMarkerV1> { new(new(1, 1), 1, new()) };
        var lines = new List<string> { "FIRST" };
        var payload = PresentationLayerPayloadJson.Create(new string('A', 64), 100, 100, markers,
            textBlocks: [new(PresentationTextAnchor.TopLeft, default, lines, 1, 0, 0, new())]);
        markers[0] = new(new(99, 99), 1, new());
        lines[0] = "CHANGED";

        Assert.AreEqual(new HVO.SkyMonitor.Astronomy.PixelPoint(1, 1), payload.Markers[0].Center);
        Assert.AreEqual("FIRST", payload.TextBlocks[0].Lines[0]);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<PresentationMarkerV1>)payload.Markers).Clear());

        var segments = Enumerable.Range(0, PresentationLayerPayloadV1.MaximumSegments)
            .Select(index => new PresentationSegmentV1(new(index, index), new(index + 1, index + 1), 1, new()))
            .ToArray();
        Assert.ThrowsExactly<ArgumentException>(() => PresentationLayerPayloadJson.Create(
            new string('A', 64), 65_536, 4_096, segments: segments));
    }

    [TestMethod]
    public async Task FrozenPayloadSerializationIsStableWhileCallerCollectionsMutateConcurrently()
    {
        var callerMarkers = Enumerable.Range(0, 1_000)
            .Select(index => new PresentationMarkerV1(new(index % 100, index / 100), 1, new())).ToList();
        var payload = PresentationLayerPayloadJson.Create(new string('A', 64), 100, 100, callerMarkers);
        var expected = PresentationLayerPayloadJson.Serialize(payload);

        var mutation = Task.Run(() =>
        {
            for (var index = 0; index < callerMarkers.Count; index++)
                callerMarkers[index] = callerMarkers[index] with { Center = new(99, 99) };
            callerMarkers.Clear();
        });
        var serializations = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            PresentationLayerPayloadJson.Serialize(payload))).ToArray();
        await Task.WhenAll(serializations.Append(mutation)).ConfigureAwait(false);

        foreach (var serialization in serializations)
            CollectionAssert.AreEqual(expected, await serialization.ConfigureAwait(false));
    }
}
