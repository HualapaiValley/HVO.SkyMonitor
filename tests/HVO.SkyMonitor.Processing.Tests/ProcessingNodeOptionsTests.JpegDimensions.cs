namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProcessingNodeOptionsTests
{
    private static ProcessingNodeOptionsDescriptor JpegDimensions =>
        ProcessingNodeOptionsCatalog.BuiltIn.Resolve("jPeGeNcOdInG", "packed-jpeg-v1").Descriptor!;

    [TestMethod]
    [DataRow("{}", false, null)]
    [DataRow("{ \"MaximumDimension\":null, \"future\":[1e+02] }", true, null)]
    [DataRow("{ \"maximumDimension\":4096, \"future\":[1e+02] }", true, 4096L)]
    public void NullableJpegDimensionsDistinguishPresenceAndKeepNoOpBytes(string json, bool explicitValue, long? expected)
    {
        var source = Json(json);
        var decoded = ProcessingNodeOptionsCodec.Decode(JpegDimensions, source);
        Assert.IsTrue(decoded.IsValid);
        var dimension = decoded.Get("maximumDimension");
        Assert.AreEqual(explicitValue, dimension.IsExplicit);
        Assert.AreEqual(expected, dimension.Value.AsNullableWholeNumber());
        var encoded = ProcessingNodeOptionsCodec.Encode(JpegDimensions, source,
            decoded.Options.ToDictionary(static item => item.Descriptor.Key, static item => item.Value));
        Assert.IsTrue(encoded.IsValid);
        Assert.AreEqual(json, encoded.Options!.Value.GetRawText());
    }

    [TestMethod]
    [DataRow("null", null)]
    [DataRow("1", null)]
    [DataRow("16384", null)]
    [DataRow("0", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("-1", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("16385", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("9223372036854775808", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("1e400", ProcessingOptionReasonCodes.WrongType)]
    [DataRow("1.0", ProcessingOptionReasonCodes.WrongType)]
    [DataRow("1e3", ProcessingOptionReasonCodes.WrongType)]
    [DataRow("\"100\"", ProcessingOptionReasonCodes.WrongType)]
    [DataRow("\"NaN\"", ProcessingOptionReasonCodes.WrongType)]
    [DataRow("true", ProcessingOptionReasonCodes.WrongType)]
    [DataRow("[]", ProcessingOptionReasonCodes.WrongType)]
    [DataRow("{}", ProcessingOptionReasonCodes.WrongType)]
    public void NullableJpegDimensionsRejectInvalidSourceConsistently(string token, string? reason)
    {
        var source = Json("{\"MaximumDimension\":" + token + "}");
        var decoded = ProcessingNodeOptionsCodec.Decode(JpegDimensions, source);
        var encoded = ProcessingNodeOptionsCodec.Encode(JpegDimensions, source, new Dictionary<string, ProcessingOptionValue>());
        Assert.AreEqual(reason is null, decoded.IsValid);
        Assert.AreEqual(decoded.IsValid, encoded.IsValid);
        if (reason is not null)
        {
            Assert.AreEqual(reason, decoded.Diagnostics.Single().Code);
            Assert.AreEqual(decoded.Diagnostics.Single(), encoded.Diagnostics.Single());
        }
    }

    [TestMethod]
    public void NullableJpegDimensionEditsSetClearAndValidateWithoutTouchingOtherSettings()
    {
        var source = Json("""{ "MaximumDimension":4096, "JpegQuality":90, "OutputVariant":"retained", "FixedTransfer":{"Gamma":1.20}, "future":[1e+02] }""");
        foreach (var value in new long?[] { null, 1, 16384 })
        {
            var edited = ProcessingNodeOptionsCodec.Encode(JpegDimensions, source,
                new Dictionary<string, ProcessingOptionValue> { ["maximumDimension"] = ProcessingOptionValue.FromNullableWholeNumber(value) });
            Assert.IsTrue(edited.IsValid);
            Assert.AreEqual(source.GetRawText().Replace("4096", value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null", StringComparison.Ordinal), edited.Options!.Value.GetRawText());
        }
        foreach (var value in new long[] { 0, -1, 16385, long.MaxValue })
            Assert.AreEqual(ProcessingOptionReasonCodes.OutOfRange, ProcessingNodeOptionsCodec.Encode(JpegDimensions, source,
                new Dictionary<string, ProcessingOptionValue> { ["maximumDimension"] = ProcessingOptionValue.FromNullableWholeNumber(value) }).Diagnostics.Single().Code);
        Assert.AreEqual(ProcessingOptionReasonCodes.WrongType, ProcessingNodeOptionsCodec.Encode(JpegDimensions, source,
            new Dictionary<string, ProcessingOptionValue> { ["maximumDimension"] = ProcessingOptionValue.FromRealNumber(4) }).Diagnostics.Single().Code);
        var duplicate = Json("""{"MaximumDimension":null,"maximumDimension":4}""");
        Assert.AreEqual(ProcessingOptionReasonCodes.DuplicateMember, ProcessingNodeOptionsCodec.Decode(JpegDimensions, duplicate).Diagnostics.Single().Code);
        Assert.AreEqual(ProcessingOptionReasonCodes.DuplicateMember, ProcessingNodeOptionsCodec.Encode(JpegDimensions, duplicate,
            new Dictionary<string, ProcessingOptionValue> { ["maximumDimension"] = ProcessingOptionValue.FromNullableWholeNumber(null) }).Diagnostics.Single().Code);
        var appended = ProcessingNodeOptionsCodec.Encode(JpegDimensions, Json("{ \"future\": [1e+02] }"),
            new Dictionary<string, ProcessingOptionValue> { ["maximumDimension"] = ProcessingOptionValue.FromNullableWholeNumber(4) });
        Assert.AreEqual("{ \"future\": [1e+02],\"maximumDimension\":4 }", appended.Options!.Value.GetRawText());
        Assert.ThrowsExactly<ArgumentException>(() => ProcessingOptionDescriptor.NullableWholeNumber("limit", "limit", "Limit", "Limit.", 0, 1, 10));
        Assert.ThrowsExactly<ArgumentException>(() => ProcessingOptionDescriptor.NullableWholeNumber("limit", "limit", "Limit", "Limit.", null, 10, 1));
    }
}
