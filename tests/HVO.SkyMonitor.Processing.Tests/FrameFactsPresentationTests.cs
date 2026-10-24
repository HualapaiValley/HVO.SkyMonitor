using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Performance", "CA1861:Avoid constant arrays as arguments", Justification = "Single-use fixture expectations are clearer inline.")]
public sealed class FrameFactsPresentationTests
{
    [TestMethod]
    public void FourCornersAreIndependentAndCanonicalAfterRetainedRoundTrip()
    {
        var snapshot = FrameFactsSnapshotV1.Create(FrameFactsRetainedContext.Create(new(FrameFactsRetainedContextTests.Descriptor())));
        var options = new FrameFactsPresentationOptions
        {
            WidthPixels = 1936,
            HeightPixels = 1216,
            TopLeftTemplate = "**{{capture.sequence}}**",
            TopRightTemplate = "*{{acquisition.gain|fixed2}}*",
            BottomLeftTemplate = "__{{environment.airTemperature}}__",
            BottomRightTemplate = "{{time.captureUtc|date}}"
        };
        var payload = FrameFactsPresentation.Create(new string('A', 64), snapshot, options);
        Assert.AreEqual(PresentationLayerPayloadV1.RichTextSchemaVersion, payload.SchemaVersion);
        Assert.HasCount(4, payload.RichText!.Corners);
        CollectionAssert.AreEqual(new[] { "42", "7.25", "UNAVAILABLE", "2026-10-22" },
            payload.RichText.Corners.Select(corner => string.Concat(corner.Lines[0].Runs.Select(run => run.Text))).ToArray());
        var bytes = PresentationLayerPayloadJson.Serialize(payload);
        CollectionAssert.AreEqual(bytes, PresentationLayerPayloadJson.Serialize(PresentationLayerPayloadJson.Parse(bytes).Payload!));
        CollectionAssert.AreEqual(bytes, PresentationLayerPayloadJson.Serialize(FrameFactsPresentation.Create(new string('A', 64), snapshot, options)));
    }

    [TestMethod]
    [DataRow("MonoRegular", 0)]
    [DataRow("MonoRegular", 1)]
    [DataRow("MonoRegular", 2)]
    [DataRow("MonoRegular", 3)]
    [DataRow("MonoRegular", 4)]
    [DataRow("MonoRegular", 5)]
    [DataRow("MonoRegular", 6)]
    [DataRow("MonoRegular", 7)]
    [DataRow("MonoBold", 0)]
    [DataRow("MonoBold", 1)]
    [DataRow("MonoBold", 2)]
    [DataRow("MonoBold", 3)]
    [DataRow("MonoBold", 4)]
    [DataRow("MonoBold", 5)]
    [DataRow("MonoBold", 6)]
    [DataRow("MonoBold", 7)]
    [DataRow("PlexRegular", 0)]
    [DataRow("PlexRegular", 2)]
    [DataRow("PlexRegular", 4)]
    [DataRow("PlexRegular", 6)]
    public void PackagedFacesAndEverySupportedEmphasisAreDeterministic(string face, int style)
    {
        var source = "AV A";
        if ((style & 2) != 0) source = "*" + source + "*";
        if ((style & 1) != 0) source = "**" + source + "**";
        if ((style & 4) != 0) source = "__" + source + "__";
        var payload = FrameFactsPresentation.Create(Hash, Snapshot(), Options(source, face));
        foreach (var corner in payload.RichText!.Corners)
            Assert.AreEqual((PresentationRichEmphasisV1)style, corner.Lines[0].Runs[0].Emphasis);
        var layout = new ImageLayout(1936, 1216, CameraPixelFormat.Mono8, 1936);
        var raw = new byte[layout.RequiredByteLength];
        var before = SHA256.HashData(raw);
        var layers = new[] { new PresentationCompositorLayer(payload, true, PresentationRasterBlendMode.Normal, 1_000_000) };
        var first = PresentationLayerCompositor.CompositeDisplay(layout, raw, layers);
        var second = PresentationLayerCompositor.CompositeDisplay(layout, raw, layers);
        Assert.AreEqual(CameraPixelFormat.Rgb24, first.Layout.PixelFormat);
        CollectionAssert.AreEqual(first.Pixels, second.Pixels);
        CollectionAssert.AreEqual(before, SHA256.HashData(raw));
        foreach (var corner in payload.RichText.Corners)
        {
            using var geometry = PresentationRichText.Layout(corner, layout.Width, layout.Height);
            Assert.IsFalse(geometry.Path.IsEmpty);
            Assert.IsTrue(geometry.Panel.Contains(geometry.Path.Bounds));
            Assert.IsLessThan(layout.Width / 2f, geometry.Panel.Width);
            Assert.IsLessThan(layout.Height / 2f, geometry.Panel.Height);
        }
    }

    [TestMethod]
    public void MissingPoliciesAndBlankLinesRemainIndependent()
    {
        var options = new FrameFactsPresentationOptions
        {
            WidthPixels = 1936,
            HeightPixels = 1216,
            TopLeftTemplate = "X {{time.captureLocal}}",
            TopRightTemplate = "X {{time.captureLocal}}",
            TopRightMissingPolicy = "DropLine",
            BottomLeftTemplate = "\nA\n",
            BottomRightTemplate = string.Empty
        };
        var corners = FrameFactsPresentation.Create(Hash, Snapshot(), options).RichText!.Corners;
        Assert.AreEqual("X UNAVAILABLE", string.Concat(corners[0].Lines[0].Runs.Select(run => run.Text)));
        Assert.IsEmpty(corners[1].Lines);
        Assert.HasCount(3, corners[2].Lines);
        Assert.IsEmpty(corners[3].Lines);
    }

    [TestMethod]
    [DataRow("**bold**", "PlexRegular")]
    [DataRow("{{spaceWeather.kp}}", "MonoRegular")]
    [DataRow("{{unknown.value}}", "MonoRegular")]
    [DataRow("<b>html</b>", "MonoRegular")]
    [DataRow("*unclosed", "MonoRegular")]
    [DataRow("text", "SystemFont")]
    public void InvalidTemplateAndFontCombinationsAreRejectedByProducerAndSharedCodec(string source, string face)
    {
        var options = Options(source, face);
        Assert.Throws<ArgumentException>(() => FrameFactsPresentation.Create(Hash, Snapshot(), options));
        var json = JsonSerializer.SerializeToElement(options);
        Assert.IsFalse(ProcessingNodeOptionsCodec.Decode(FrameFactsPresentationOptionDescriptors.Descriptor, json).IsValid);
    }

    [TestMethod]
    public void SourceOutputLineAndQuadrantLimitsFailWithoutPartialPayloads()
    {
        foreach (var source in new[] { new string('A', 4097), new string('A', 65), string.Join('\n', Enumerable.Repeat("A", 9)) })
            Assert.Throws<ArgumentException>(() => FrameFactsPresentation.Create(Hash, Snapshot(), Options(source)));
        var descriptor = FrameFactsRetainedContextTests.Descriptor();
        var longFact = FrameFactsSnapshotV1.Create(FrameFactsRetainedContext.Create(new(descriptor with
        {
            Capture = descriptor.Capture with { AgentId = new string('A', 65) }
        })));
        Assert.Throws<ArgumentException>(() => FrameFactsPresentation.Create(Hash, longFact, Options("{{capture.agentId}}")));
        Assert.Throws<ArgumentException>(() => FrameFactsPresentation.Create(Hash, Snapshot(), Options(new string('A', 64), size: 64_000)));
        Assert.Throws<ArgumentException>(() => FrameFactsPresentation.Create(Hash, Snapshot(), new() { WidthPixels = 65_536, HeightPixels = 65_536 }));
        var maximum = FrameFactsPresentation.Create(Hash, Snapshot(), Options(string.Join('\n', Enumerable.Repeat(new string('A', 64), 8))));
        Assert.HasCount(8, maximum.RichText!.Corners[0].Lines);
    }

    [TestMethod]
    public void IdentitiesBindSourceFactsProvenanceTemplateStyleFontAndLayout()
    {
        var snapshot = Snapshot();
        var original = FrameFactsPresentation.Create(Hash, snapshot, Options("A"));
        var variants = new[]
        {
            FrameFactsPresentation.Create(new string('B', 64), snapshot, Options("A")),
            FrameFactsPresentation.Create(Hash, snapshot, Options("**A**")),
            FrameFactsPresentation.Create(Hash, snapshot, Options("A", "MonoBold")),
            FrameFactsPresentation.Create(Hash, snapshot, Options("A", size: 18_000)),
            FrameFactsPresentation.Create(Hash, snapshot, new() { WidthPixels = 1936, HeightPixels = 1216, TopLeftTemplate = "A", TopLeftRed = 1 })
        };
        foreach (var variant in variants) Assert.AreNotEqual(original.ContentIdentitySha256, variant.ContentIdentitySha256);
        var changed = snapshot with { Facts = snapshot.Facts.Select(fact => fact.Key == "capture.sequence" ? fact with { WholeNumber = 43 } : fact).ToArray() };
        changed = changed with { IdentitySha256 = changed.ComputeIdentity() };
        Assert.AreNotEqual(original.ContentIdentitySha256, FrameFactsPresentation.Create(Hash, changed, Options("A")).ContentIdentitySha256);
        var provenance = snapshot with { Facts = snapshot.Facts.Select(fact => fact with { Provenance = fact.Provenance with { Nature = "Changed retained provenance" } }).ToArray() };
        provenance = provenance with { IdentitySha256 = provenance.ComputeIdentity() };
        Assert.AreNotEqual(original.ContentIdentitySha256, FrameFactsPresentation.Create(Hash, provenance, Options("A")).ContentIdentitySha256);
        var invalidContract = original with { RichText = original.RichText! with { LayoutVersion = "future-layout" } };
        Assert.Throws<ArgumentException>(() => invalidContract.ValidateStructure());
    }

    [TestMethod]
    public void RetainedSnapshotRoundTripsAndRejectsMalformedValuesAndIdentity()
    {
        var snapshot = Snapshot();
        var bytes = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(snapshot)).GetRawText();
        var copy = JsonSerializer.Deserialize<FrameFactsSnapshotV1>(bytes, SnapshotJson)!;
        Assert.AreEqual("42", FrameFactsEvaluator.Evaluate(FrameFactsTemplate.Parse("{{capture.sequence}}", FrameFactsRetainedCatalog.Catalog).Template!, copy.ToContext()).Text);
        Assert.Throws<ArgumentException>(() => (copy with { IdentitySha256 = Hash }).ToContext());
        var malformed = copy with { Facts = copy.Facts.Select(fact => fact.Key == "capture.sequence" ? fact with { Number = 4 } : fact).ToArray() };
        malformed = malformed with { IdentitySha256 = malformed.ComputeIdentity() };
        Assert.Throws<ArgumentException>(() => malformed.ToContext());
        var duplicate = copy with { Facts = Enumerable.Repeat(copy.Facts[0], copy.Facts.Count).ToArray() };
        duplicate = duplicate with { IdentitySha256 = duplicate.ComputeIdentity() };
        Assert.Throws<ArgumentException>(() => duplicate.ToContext());
    }

    [TestMethod]
    [SuppressMessage("Usage", "MSTEST0032:Assertion condition is always true", Justification = "Literal wire versions deliberately pin compatibility across future changes.")]
    public void OldCreatorsAndVersionsStayPinnedAndNewFieldsAreAbsentFromLegacyBytes()
    {
        Assert.AreEqual("metadata-corner-presentation-v6-original-colours", PresentationLayerProducers.MetadataProducerVersion);
        Assert.AreEqual("presentation-layer-payload-v3", PresentationLayerPayloadV1.CurrentSchemaVersion);
        Assert.AreEqual("presentation-metadata-facts-v2", PresentationMetadataFactsProductV1.CurrentSchemaVersion);
        var payload = PresentationLayerProducers.FromMetadataFacts(new(Hash, ["A"], ["B"], ["C"], ["D"]), 1936, 1216);
        Assert.IsNull(payload.RichText);
        var bytes = PresentationLayerPayloadJson.Serialize(payload);
        using var document = JsonDocument.Parse(bytes);
        Assert.IsFalse(document.RootElement.TryGetProperty("richText", out _));
        Assert.AreEqual(payload.ContentIdentitySha256, PresentationLayerPayloadJson.Parse(bytes).Payload!.ContentIdentitySha256);
        Assert.IsEmpty(ProcessingNodeOptionsCatalog.BuiltIn.Upgrades);
        Assert.AreEqual(ProcessingNodeOptionsResolutionStatus.Described,
            ProcessingNodeOptionsCatalog.BuiltIn.Resolve("EnvironmentPresentationLayer", PresentationLayerProducers.MetadataProducerVersion).Status);
    }

    [TestMethod]
    public void PayloadRejectsInvalidSchemasFontsRunsAndMutableInputIsFrozen()
    {
        var payload = FrameFactsPresentation.Create(Hash, Snapshot(), Options("A"));
        Assert.Throws<ArgumentException>(() => (payload with { SchemaVersion = PresentationLayerPayloadV1.CurrentSchemaVersion }).ValidateStructure());
        var corners = payload.RichText!.Corners.ToArray();
        var lines = corners[0].Lines.ToArray();
        var runs = lines[0].Runs.ToArray();
        lines[0] = lines[0] with { Runs = runs };
        corners[0] = corners[0] with { Lines = lines };
        var frozen = PresentationLayerPayloadJson.CreateRichText(Hash, 1936, 1216, payload.RichText with { Corners = corners });
        runs[0] = new("changed", PresentationRichEmphasisV1.None);
        Assert.AreEqual("A", frozen.RichText!.Corners[0].Lines[0].Runs[0].Text);
        corners[0] = corners[0] with { Lines = [new(Enumerable.Repeat(new PresentationRichRunV1("A", PresentationRichEmphasisV1.None), 65).ToArray())] };
        Assert.Throws<ArgumentException>(() => (payload with { RichText = payload.RichText with { Corners = corners } }).ValidateStructure());
        corners[0] = payload.RichText.Corners[0] with { Font = PresentationRichFontFaceV1.PlexRegular, Lines = [new([new("A", PresentationRichEmphasisV1.Bold)])] };
        Assert.Throws<ArgumentException>(() => (payload with { RichText = payload.RichText with { Corners = corners } }).ValidateStructure());
    }

    private static readonly string Hash = new('A', 64);
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
    internal static FrameFactsSnapshotV1 Snapshot() => FrameFactsSnapshotV1.Create(FrameFactsRetainedContext.Create(new(FrameFactsRetainedContextTests.Descriptor())));
    internal static FrameFactsPresentationOptions Options(string source, string face = "MonoRegular", int size = 16_000) => new()
    {
        WidthPixels = 1936,
        HeightPixels = 1216,
        TopLeftTemplate = source,
        TopRightTemplate = source,
        BottomLeftTemplate = source,
        BottomRightTemplate = source,
        TopLeftFont = face,
        TopRightFont = face,
        BottomLeftFont = face,
        BottomRightFont = face,
        TopLeftSizeMilliPixels = size,
        TopRightSizeMilliPixels = size,
        BottomLeftSizeMilliPixels = size,
        BottomRightSizeMilliPixels = size
    };
}
