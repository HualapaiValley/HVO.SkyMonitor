namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProcessingNodeOptionsTests
{
    [TestMethod]
    [DataRow("Preview", "default")]
    [DataRow("CalibratedPreview", "calibrated-preview")]
    [DataRow("CombinedPreview", "combined-preview")]
    public void PreviewDescriptorsMatchExactModesDefaultsAndPreserveUntouchedJson(string alias, string variant)
    {
        ArgumentNullException.ThrowIfNull(alias);
        foreach (var fixedTransfer in new[] { false, true })
        {
            var version = fixedTransfer ? "fixed-preview-v1" : "encoded-preview-v1";
            var descriptor = ProcessingNodeOptionsCatalog.BuiltIn.Resolve(alias.ToUpperInvariant(), version).Descriptor!;
            var defaults = ProcessingNodeOptionsCodec.Decode(descriptor, Json(fixedTransfer ? "{\"FixedTransfer\":{}}" : "{}"));
            Assert.IsTrue(defaults.IsValid);
            Assert.HasCount(6, defaults.Options);
            Assert.IsTrue(defaults.Get("enabled").Value.AsToggle());
            Assert.AreEqual("mono16-asinh-v2", defaults.Get("recipeVersion").Value.AsText());
            Assert.AreEqual(variant, defaults.Get("outputVariant").Value.AsText());
            if (!fixedTransfer)
            {
                Assert.AreEqual(0.5, defaults.Get("blackPercentile").Value.AsRealNumber());
                Assert.AreEqual(0.9999, defaults.Get("whitePercentile").Value.AsRealNumber());
                Assert.AreEqual(4, defaults.Get("asinhStrength").Value.AsRealNumber());
            }
            var source = Json(fixedTransfer
                ? """{ "FixedTransfer": {"BlackLevel":1e+02,"WhiteLevel":4095,"Gamma":1.2,"future":[1e+02]}, "blackPercentile":0.10, "RecipeVersion":"retained-label", "future": [1e+02] }"""
                : """{ "FixedTransfer": null, "blackPercentile":0.10, "RecipeVersion":"retained-label", "future": [1e+02] }""");
            Assert.AreEqual(version, PreviewOptionDescriptors.VersionFor(source));
            var decoded = ProcessingNodeOptionsCodec.Decode(descriptor, source);
            Assert.IsTrue(decoded.IsValid);
            Assert.AreEqual(source.GetRawText(), ProcessingNodeOptionsCodec.Encode(descriptor, source,
                decoded.Options.ToDictionary(static item => item.Descriptor.Key, static item => item.Value)).Options!.Value.GetRawText());
            var changed = ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
            {
                ["recipeVersion"] = ProcessingOptionValue.FromText("edited-label")
            });
            Assert.IsTrue(changed.IsValid);
            Assert.AreEqual(source.GetRawText().Replace("retained-label", "edited-label", StringComparison.Ordinal), changed.Options!.Value.GetRawText());
            var empty = fixedTransfer ? "{\"FixedTransfer\":{}}" : "{}";
            Assert.AreEqual(empty, ProcessingNodeOptionsCodec.Encode(descriptor, Json(empty),
                defaults.Options.ToDictionary(static item => item.Descriptor.Key, static item => item.Value)).Options!.Value.GetRawText());
            Assert.IsNull(descriptor.Find("fixedTransfer"));
            Assert.IsNull(descriptor.Find("jpegQuality"), "Preview always produces packed output; JPEG quality belongs to JpegEncoding.");
            if (fixedTransfer)
            {
                Assert.IsNull(descriptor.Find("blackPercentile"));
                Assert.AreEqual(ProcessingOptionReasonCodes.UnknownOption, ProcessingNodeOptionsCodec.Encode(descriptor, source,
                    new Dictionary<string, ProcessingOptionValue> { ["asinhStrength"] = ProcessingOptionValue.FromRealNumber(8) }).Diagnostics.Single().Code);
            }
        }
        Assert.AreEqual(ProcessingNodeOptionsResolutionStatus.UnsupportedVersion,
            ProcessingNodeOptionsCatalog.BuiltIn.Resolve(alias, "future-preview-v1").Status);
    }

    [TestMethod]
    [DataRow("blackPercentile", "-0.01", false)]
    [DataRow("blackPercentile", "0", true)]
    [DataRow("blackPercentile", "0.999999", false)]
    [DataRow("whitePercentile", "0.000001", false)]
    [DataRow("whitePercentile", "1", true)]
    [DataRow("whitePercentile", "1.000001", false)]
    [DataRow("asinhStrength", "0.01", true)]
    [DataRow("asinhStrength", "1000", true)]
    [DataRow("asinhStrength", "0.009", false)]
    [DataRow("asinhStrength", "1001", false)]
    [DataRow("asinhStrength", "\"4\"", false)]
    [DataRow("asinhStrength", "null", false)]
    [DataRow("asinhStrength", "1e400", false)]
    [DataRow("recipeVersion", "\" \"", false)]
    [DataRow("outputVariant", "\" route\"", false)]
    public void PreviewCodecValidatesBoundsTypesAndPercentileOrdering(string member, string token, bool valid)
    {
        foreach (var descriptor in PreviewOptionDescriptors.All.Where(static item => item.StepVersion == "encoded-preview-v1"))
            Assert.AreEqual(valid, ProcessingNodeOptionsCodec.Decode(descriptor, Json("{\"" + member + "\":" + token + "}")).IsValid);
    }

    [TestMethod]
    public void PreviewOrderingUsesEffectiveDefaultsAndRejectsEqualLevelsAfterEdit()
    {
        var descriptor = PreviewOptionDescriptors.All[0];
        Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(descriptor, Json("""{"blackPercentile":0.999999,"whitePercentile":1}""")).IsValid);
        Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(descriptor, Json("""{"blackPercentile":0,"whitePercentile":0.000001}""")).IsValid);
        var encoded = ProcessingNodeOptionsCodec.Encode(descriptor, Json("""{"blackPercentile":0.25,"whitePercentile":0.75}"""),
            new Dictionary<string, ProcessingOptionValue> { ["blackPercentile"] = ProcessingOptionValue.FromRealNumber(0.75) });
        Assert.IsFalse(encoded.IsValid);
        Assert.AreEqual(ProcessingOptionReasonCodes.NotAllowed, encoded.Diagnostics.Single().Code);
        Assert.AreEqual(ProcessingOptionReasonCodes.DuplicateMember, ProcessingNodeOptionsCodec.Decode(descriptor,
            Json("""{"blackPercentile":0.1,"BlackPercentile":0.2}""")).Diagnostics.Single().Code);
        Assert.AreEqual("encoded-preview-v1", PreviewOptionDescriptors.VersionFor(Json("""{"FixedTransfer":{},"fixedTransfer":null}""")));
        Assert.AreEqual("fixed-preview-v1", PreviewOptionDescriptors.VersionFor(Json("""{"FixedTransfer":null,"fixedTransfer":{}}""")));
    }

    [TestMethod]
    public void JpegQualityEditsPreserveNullableResizeAndUnknownMembersByteForByte()
    {
        var descriptor = ProcessingNodeOptionsCatalog.BuiltIn.Resolve("JpegEncoding", "packed-jpeg-v1").Descriptor!;
        Assert.HasCount(3, descriptor.Options);
        var defaults = ProcessingNodeOptionsCodec.Decode(descriptor, Json("{}"));
        Assert.AreEqual(HVO.SkyMonitor.Imaging.JpegImageCodec.DefaultQuality, defaults.Get("jpegQuality").Value.AsWholeNumber());
        Assert.AreEqual("annotated-final-jpeg", defaults.Get("outputVariant").Value.AsText());
        Assert.AreEqual(ProcessingOptionKind.NullableWholeNumber, descriptor.Find("maximumDimension")!.Kind);
        foreach (var resize in new[] { "", ", \"MaximumDimension\":null", ", \"MaximumDimension\":4096" })
        {
            var source = Json("{ \"JpegQuality\":90, \"future\":[1e+02]" + resize + " }");
            var encoded = ProcessingNodeOptionsCodec.Encode(descriptor, source,
                new Dictionary<string, ProcessingOptionValue> { ["jpegQuality"] = ProcessingOptionValue.FromWholeNumber(75) });
            Assert.IsTrue(encoded.IsValid);
            Assert.AreEqual(source.GetRawText().Replace(":90", ":75", StringComparison.Ordinal), encoded.Options!.Value.GetRawText());
        }
        foreach (var token in new[] { "0", "101", "90.0", "\"90\"", "null" })
            Assert.IsFalse(ProcessingNodeOptionsCodec.Decode(descriptor, Json("{\"jpegQuality\":" + token + "}")).IsValid);
        foreach (var token in new[] { "1", "100" })
            Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(descriptor, Json("{\"jpegQuality\":" + token + "}")).IsValid);
    }
}
