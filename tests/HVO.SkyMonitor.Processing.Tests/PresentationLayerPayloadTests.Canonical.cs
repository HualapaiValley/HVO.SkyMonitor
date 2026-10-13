using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class PresentationLayerPayloadTests
{
    private static readonly JsonSerializerOptions OriginalPayloadOptions = CreateOriginalPayloadOptions();

    [TestMethod]
    [DataRow(PresentationLayerPayloadV1.PreviousSchemaVersion)]
    [DataRow(PresentationLayerPayloadV1.SemanticSchemaVersion)]
    [DataRow(PresentationLayerPayloadV1.CurrentSchemaVersion)]
    public void PayloadSchemasKeepOriginalPrimitiveCanonicalBytesAndIdentity(string schema)
    {
        var payload = CanonicalPayloadFixture(schema);
        AssertOriginalPayloadParity(payload);
        var json = Encoding.UTF8.GetString(PresentationLayerPayloadJson.Serialize(payload));
        Assert.AreEqual(schema != PresentationLayerPayloadV1.PreviousSchemaVersion,
            json.Contains("\"stroke\"", StringComparison.Ordinal));
        Assert.AreEqual(schema == PresentationLayerPayloadV1.CurrentSchemaVersion,
            json.Contains("\"appearance\"", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(248)]
    [DataRow(251)]
    [DataRow(255)]
    public void MaskBase64PlusSlashAndPaddingKeepOriginalScalarEncoding(int maskByte)
    {
        var payload = PresentationLayerPayloadJson.Create(new string('A', 64), 64, 64,
            tileMask: new(8, 1, PresentationTileMaskV1.RowMajorLsbFirst, new byte[] { (byte)maskByte }, 1, new()));
        AssertOriginalPayloadParity(payload);
        var json = Encoding.UTF8.GetString(PresentationLayerPayloadJson.Serialize(payload));
        var base64 = Convert.ToBase64String(new byte[] { (byte)maskByte });
        StringAssert.Contains(json, base64.Replace("+", "\\u002B", StringComparison.Ordinal), StringComparison.Ordinal);
        if (base64.Contains('+'))
            Assert.IsFalse(PresentationLayerPayloadJson.Parse(Encoding.UTF8.GetBytes(
                json.Replace("\\u002B", "+", StringComparison.Ordinal))).IsValid, "Unescaped base64 is not the retained canonical spelling.");
    }

    [TestMethod]
    public void CanonicalNumericAndTextEscapingKeepOriginalBytes()
    {
        var payload = PresentationLayerPayloadJson.Create(new string('A', 64), 640, 480,
            markers: [new(new(-0d, double.Epsilon), 0, new())],
            segments: [new(new(1.23456789012345, -0d), new(1e-100, -1e-100), 1, new(0, 127, 255),
                new(0, 0, 1_000_000, 0))],
            ellipses: [new(new(1, 2), 595.8400000000001, 0.125, new(255, 128, 1), ThicknessMilliPixels: 1)],
            textBlocks: [new(PresentationTextAnchor.Point, new(1, 2), ["M42 <&+> \"Étoile\" 😀"], 1, 0, 0, new())]);
        AssertOriginalPayloadParity(payload);
        var json = Encoding.UTF8.GetString(PresentationLayerPayloadJson.Serialize(payload));
        StringAssert.Contains(json, "\\u002B", StringComparison.Ordinal);
        StringAssert.Contains(json, "595.8400000000001", StringComparison.Ordinal);
        Assert.IsFalse(json.Contains("dashOffsetPixels", StringComparison.Ordinal), "Ignored defaults keep their original omission.");
    }

    [TestMethod]
    public void LargeSegmentPayloadKeepsOriginalBytesAndLiveIdentity()
    {
        var segments = Enumerable.Range(0, 4_096).Select(index => new PresentationSegmentV1(
            new(index % 3552 + .125, index / 3552d), new(index % 3552 + .75, index / 3552d + .25),
            2, new(116, 209, 255), new(7, 3, 850_000, index % 8))).ToArray();
        var payload = PresentationLayerPayloadJson.Create(new string('A', 64), 3552, 3552, segments: segments);
        Assert.HasCount(4_096, payload.Segments);
        AssertOriginalPayloadParity(payload);
        var changed = payload with { Segments = segments };
        segments[2] = segments[2] with { Thickness = 3 };
        Assert.AreNotEqual(payload.ContentIdentitySha256, PresentationLayerPayloadJson.ComputeIdentity(changed));
        Assert.ThrowsExactly<ArgumentException>(() => PresentationLayerPayloadJson.Serialize(changed));
        AssertOriginalPayloadParity(payload);
    }

    [TestMethod]
    public void CanonicalPayloadSnapshotsCollectionsAndMaskAndRejectsLaterMutation()
    {
        var markers = new List<PresentationMarkerV1> { new(new(3, 4), 2, new()) };
        var lines = new List<string> { "M42" };
        var bits = new byte[] { 248 };
        var payload = PresentationLayerPayloadJson.Create(new string('A', 64), 64, 64, markers,
            textBlocks: [new(PresentationTextAnchor.Point, new(5, 6), lines, 1, 0, 0, new())],
            tileMask: new(8, 1, PresentationTileMaskV1.RowMajorLsbFirst, bits, 1, new()));
        var original = PresentationLayerPayloadJson.Serialize(payload);
        markers.Clear(); lines[0] = "CHANGED"; bits[0] = 0;
        CollectionAssert.AreEqual(original, PresentationLayerPayloadJson.Serialize(payload));
        AssertOriginalPayloadParity(payload);
        Assert.IsNotNull(payload.TileMask);
        var callerOwned = payload with { TileMask = payload.TileMask with { Bits = bits } };
        callerOwned = callerOwned with { ContentIdentitySha256 = PresentationLayerPayloadJson.ComputeIdentity(callerOwned) };
        bits[0] = 251;
        Assert.ThrowsExactly<ArgumentException>(() => PresentationLayerPayloadJson.Validate(callerOwned));
        Assert.ThrowsExactly<ArgumentException>(() => PresentationLayerPayloadJson.Serialize(callerOwned));
        CollectionAssert.AreEqual(original, PresentationLayerPayloadJson.Serialize(payload));
    }

    [TestMethod]
    public void CanonicalPayloadArrayOrderRemainsIdentitySignificant()
    {
        var original = CanonicalPayloadFixture(PresentationLayerPayloadV1.CurrentSchemaVersion);
        var changed = original with
        {
            Markers = original.Markers.Reverse().ToArray(),
            Segments = original.Segments.Reverse().ToArray(),
            TextBlocks = [original.TextBlocks[0] with { Lines = original.TextBlocks[0].Lines.Reverse().ToArray() }]
        };
        changed = changed with { ContentIdentitySha256 = PresentationLayerPayloadJson.ComputeIdentity(changed) };
        Assert.AreNotEqual(original.ContentIdentitySha256, changed.ContentIdentitySha256);
        AssertOriginalPayloadParity(original);
        AssertOriginalPayloadParity(changed);
    }

    [TestMethod]
    public void TypedPayloadSerializationKeepsStrictCanonicalParsing()
    {
        var payload = CanonicalPayloadFixture(PresentationLayerPayloadV1.CurrentSchemaVersion);
        var json = Encoding.UTF8.GetString(OriginalPayloadBytes(payload));
        var malformed = new[]
        {
            " " + json,
            json.Replace("\"markers\":", "\"MARKERS\":[],\"markers\":", StringComparison.Ordinal),
            json.Replace(payload.ContentIdentitySha256, new string('F', 64), StringComparison.Ordinal),
            json.Replace("\"anchor\":\"Point\"", "\"anchor\":0", StringComparison.Ordinal),
            "{" + json[1..^1] + ",\"unknown\":true}"
        };
        foreach (var candidate in malformed)
            Assert.IsFalse(PresentationLayerPayloadJson.Parse(Encoding.UTF8.GetBytes(candidate)).IsValid, candidate);
        AssertOriginalPayloadParity(payload);
    }

    [TestMethod]
    public void CanonicalPayloadGraphHasOnlyClosedRecordsListsAndMaskScalar()
    {
        var pending = new Stack<Type>();
        var visited = new HashSet<Type>();
        var resolver = new DefaultJsonTypeInfoResolver();
        pending.Push(typeof(PresentationLayerPayloadV1));
        while (pending.TryPop(out var type))
        {
            if (!visited.Add(type) || type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(ReadOnlyMemory<byte>)) continue;
            if (Nullable.GetUnderlyingType(type) is { } inner) { pending.Push(inner); continue; }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            {
                pending.Push(type.GetGenericArguments()[0]);
                continue;
            }
            Assert.IsTrue(type.IsValueType || type.IsSealed, type.FullName);
            Assert.AreNotEqual(typeof(object), type);
            Assert.AreNotEqual(typeof(JsonElement), type);
            Assert.IsNull(type.GetCustomAttribute<JsonConverterAttribute>(), type.FullName);
            var metadata = resolver.GetTypeInfo(type, OriginalPayloadOptions);
            Assert.AreEqual(JsonTypeInfoKind.Object, metadata.Kind, type.FullName);
            foreach (var property in metadata.Properties) pending.Push(property.PropertyType);
        }
        CollectionAssert.IsSubsetOf(new[] { typeof(PresentationTextAppearanceV3),
            typeof(ReadOnlyMemory<byte>), typeof(PixelPoint) }, visited.ToArray());
    }

    private static PresentationLayerPayloadV1 CanonicalPayloadFixture(string schema)
    {
        var semantic = schema != PresentationLayerPayloadV1.PreviousSchemaVersion;
        var appearance = schema == PresentationLayerPayloadV1.CurrentSchemaVersion;
        var body = new PresentationTextStyleV3(PresentationFontFaceV3.MonoRegular, 12_000, 200,
            new(195, 236, 255), new(3, 8, 14), 950_000, 4_000);
        var payload = PresentationLayerPayloadJson.Create(new string('A', 64), 640, 480,
            markers: [new(new(20.5, 30.25), 6, new(144, 144, 144), semantic), new(new(40, 50), 0, new())],
            segments: [new(new(1.25, 2.5), new(100.75, 120.5), 2, new(116, 209, 255),
                semantic ? new(7, 3, 850_000, .125) : null), new(new(3, 4), new(5, 6), 1, new())],
            ellipses: [new(new(320, 240), 95.25, 20.5, new(188, 140, 255),
                semantic ? new(0, 0, 450_000) : null, appearance ? 2_000 : null)],
            textBlocks: [new(PresentationTextAnchor.Point, new(20, 20), ["M42 <&+> \"Étoile\"", "EXPECTED 😀"], 2, 0, 4,
                new(195, 236, 255), semantic ? new(new(2, 8, 14), 840_000, new(116, 209, 255), 6, 0,
                    appearance ? new(1_700, 380_000, true, MinimumWidthMilliPixels: 13_000, CornerRadiusMilliPixels: 0) : null) : null,
                appearance ? new(body, body with { Face = PresentationFontFaceV3.MonoBold }) : null)],
            tileMask: new(8, 1, PresentationTileMaskV1.RowMajorLsbFirst, new byte[] { 248 }, 1, new(255, 64, 32)));
        payload = payload with { SchemaVersion = schema };
        return payload with { ContentIdentitySha256 = PresentationLayerPayloadJson.ComputeIdentity(payload) };
    }

    private static void AssertOriginalPayloadParity(PresentationLayerPayloadV1 payload)
    {
        var blankBytes = OriginalPayloadBytes(payload with { ContentIdentitySha256 = string.Empty });
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(blankBytes)), payload.ContentIdentitySha256);
        Assert.AreEqual(payload.ContentIdentitySha256, PresentationLayerPayloadJson.ComputeIdentity(payload));
        var expected = OriginalPayloadBytes(payload);
        CollectionAssert.AreEqual(expected, PresentationLayerPayloadJson.Serialize(payload));
        var parsed = PresentationLayerPayloadJson.Parse(expected);
        Assert.IsTrue(parsed.IsValid, parsed.ErrorPath);
        Assert.IsNotNull(parsed.Payload);
        CollectionAssert.AreEqual(expected, PresentationLayerPayloadJson.Serialize(parsed.Payload));
    }

    private static byte[] OriginalPayloadBytes(PresentationLayerPayloadV1 payload) =>
        JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(payload, OriginalPayloadOptions)));

    private static JsonSerializerOptions CreateOriginalPayloadOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
