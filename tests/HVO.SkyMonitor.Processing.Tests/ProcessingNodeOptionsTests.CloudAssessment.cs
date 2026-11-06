namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProcessingNodeOptionsTests
{
    [TestMethod]
    public void CloudAssessmentDescriptorPreservesOpaqueReferenceAbsentDefaultsAndSourceJson()
    {
        var descriptor = ProcessingNodeOptionsCatalog.BuiltIn.Resolve("cLoUdAsSeSsMeNt", "comparative-tiled-transmission-v1").Descriptor!;
        Assert.AreSame(CloudAssessmentOptionDescriptors.Descriptor, descriptor);
        Assert.AreEqual(ProcessingNodeOptionsResolutionStatus.UnsupportedVersion,
            ProcessingNodeOptionsCatalog.BuiltIn.Resolve("CloudAssessment", "future-v2").Status);
        Assert.IsNull(descriptor.Find("clearReferenceManifestPath"));
        var defaults = ProcessingNodeOptionsCodec.Decode(descriptor, Json("{}"));
        Assert.HasCount(9, defaults.Options);
        Assert.IsTrue(defaults.Get("enabled").Value.AsToggle());
        Assert.IsTrue(defaults.Get("includeMask").Value.AsToggle());
        Assert.AreEqual("cloud-assessment-v1", defaults.Get("outputVariant").Value.AsText());
        Assert.AreEqual(16, defaults.Get("gridColumns").Value.AsWholeNumber());
        Assert.AreEqual(12, defaults.Get("gridRows").Value.AsWholeNumber());
        Assert.AreEqual(850_000, defaults.Get("transmissionThresholdMillionths").Value.AsWholeNumber());
        Assert.AreEqual(64, defaults.Get("minimumReferenceSignal").Value.AsWholeNumber());
        Assert.AreEqual(16, defaults.Get("minimumSamplesPerTile").Value.AsWholeNumber());
        Assert.AreEqual(100_000, defaults.Get("maximumSaturatedFractionMillionths").Value.AsWholeNumber());
        var source = Json("""{ "GridRows": 12, "ClearReferenceManifestPath": "retained/clear.manifest.json", "future": [1e+02], "imageCircle": { "opaque": true } }""");
        var decoded = ProcessingNodeOptionsCodec.Decode(descriptor, source);
        Assert.IsTrue(decoded.IsValid);
        Assert.AreEqual(source.GetRawText(), ProcessingNodeOptionsCodec.Encode(descriptor, source,
            decoded.Options.ToDictionary(static item => item.Descriptor.Key, static item => item.Value)).Options!.Value.GetRawText());
        var changed = ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
        {
            ["gridRows"] = ProcessingOptionValue.FromWholeNumber(64)
        });
        Assert.AreEqual(source.GetRawText().Replace("\"GridRows\": 12", "\"GridRows\": 64", StringComparison.Ordinal),
            changed.Options!.Value.GetRawText());
        Assert.AreEqual("{}", ProcessingNodeOptionsCodec.Encode(descriptor, Json("{}"),
            defaults.Options.ToDictionary(static item => item.Descriptor.Key, static item => item.Value)).Options!.Value.GetRawText());
        Assert.AreEqual(ProcessingOptionReasonCodes.UnknownOption,
            ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
            {
                ["clearReferenceManifestPath"] = ProcessingOptionValue.FromText("other.manifest.json")
            }).Diagnostics.Single().Code);
    }

    [TestMethod]
    [DataRow("gridColumns", 1L, 64L)]
    [DataRow("gridRows", 1L, 64L)]
    [DataRow("transmissionThresholdMillionths", 1L, 999_999L)]
    [DataRow("minimumReferenceSignal", 0L, 65_535L)]
    [DataRow("minimumSamplesPerTile", 1L, 2_147_483_647L)]
    [DataRow("maximumSaturatedFractionMillionths", 0L, 1_000_000L)]
    public void CloudAssessmentCodecUsesExistingIntegerBoundsAndStrictTypes(string member, long minimum, long maximum)
    {
        var descriptor = CloudAssessmentOptionDescriptors.Descriptor;
        foreach (var value in new[] { minimum - 1, minimum, maximum, maximum + 1 })
        {
            var result = ProcessingNodeOptionsCodec.Decode(descriptor,
                Json(System.FormattableString.Invariant($"{{\"{member}\":{value}}}")));
            Assert.AreEqual(value >= minimum && value <= maximum, result.IsValid, $"{member}={value}");
        }
        foreach (var token in new[] { "null", "true", "\"1\"", "1.0", "1e0", "1.5" })
            Assert.IsFalse(ProcessingNodeOptionsCodec.Decode(descriptor, Json("{\"" + member + "\":" + token + "}")).IsValid, token);
    }

    [TestMethod]
    [DataRow("enabled", "false", true)]
    [DataRow("enabled", "0", false)]
    [DataRow("includeMask", "false", true)]
    [DataRow("includeMask", "null", false)]
    [DataRow("includeMask", "\"true\"", false)]
    [DataRow("outputVariant", "\"custom-cloud\"", true)]
    [DataRow("outputVariant", "\"\"", false)]
    [DataRow("outputVariant", "\" route\"", false)]
    [DataRow("outputVariant", "null", false)]
    public void CloudAssessmentCodecValidatesTogglesAndRoute(string member, string token, bool valid)
        => Assert.AreEqual(valid, ProcessingNodeOptionsCodec.Decode(CloudAssessmentOptionDescriptors.Descriptor,
            Json("{\"" + member + "\":" + token + "}")).IsValid);

    [TestMethod]
    public void CloudAssessmentCodecRejectsDuplicateMembersAndLongRoutes()
    {
        var descriptor = CloudAssessmentOptionDescriptors.Descriptor;
        Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(descriptor, Json("{\"outputVariant\":\"" + new string('v', 128) + "\"}")).IsValid);
        Assert.AreEqual(ProcessingOptionReasonCodes.TooLong,
            ProcessingNodeOptionsCodec.Decode(descriptor, Json("{\"outputVariant\":\"" + new string('v', 129) + "\"}")).Diagnostics.Single().Code);
        Assert.AreEqual(ProcessingOptionReasonCodes.DuplicateMember,
            ProcessingNodeOptionsCodec.Decode(descriptor, Json("""{"gridRows":12,"GridRows":12}""")).Diagnostics.Single().Code);
        var graph = Graph(Node("cloud", "CloudAssessment", descriptor.StepVersion, """{"gridRows":65}"""));
        Assert.AreEqual(ProcessingOptionReasonCodes.OutOfRange, ProcessingNodeOptionsCatalog.BuiltIn.Validate(graph).Single().Code);
    }
}
