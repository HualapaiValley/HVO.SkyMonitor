namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProcessingNodeOptionsTests
{
    [TestMethod]
    [DataRow("linear-normalization-none-v1")]
    [DataRow("reference-calibration-linear16-v2")]
    public void CalibrationDescriptorsPreserveOpaqueModelsCompatibilityMembersAndAbsentDefaults(string version)
    {
        var descriptor = ProcessingNodeOptionsCatalog.BuiltIn.Resolve("cALIBRATION", version).Descriptor!;
        Assert.IsNotNull(descriptor);
        var defaults = ProcessingNodeOptionsCodec.Decode(descriptor, Json("{}"));
        Assert.HasCount(3, defaults.Options);
        Assert.IsTrue(defaults.Get("enabled").Value.AsToggle());
        Assert.AreEqual("None", defaults.Get("strategy").Value.AsText());
        Assert.AreEqual("none", defaults.Get("outputVariant").Value.AsText());
        CollectionAssert.AreEqual(new[] { "None", "SyntheticReferences", "CalibrationLibrary" }, descriptor.Find("strategy")!.AllowedValues.ToArray());
        foreach (var opaque in new[] { "syntheticCalibration", "calibrationPasses", "maxCalibrationSeconds", "masterIdentity" })
            Assert.IsNull(descriptor.Find(opaque));
        Assert.AreEqual(ProcessingNodeOptionsResolutionStatus.UnsupportedVersion,
            ProcessingNodeOptionsCatalog.BuiltIn.Resolve("Calibration", "future-v3").Status);
        var source = Json("""{ "Strategy":"SyntheticReferences", "SyntheticCalibration": { "schemaVersion":"synthetic-calibration-model-v1", "future":[1e+02] }, "calibrationPasses":2, "maxCalibrationSeconds":60, "masterIdentity":"retained", "future": [1e+02] }""");
        var decoded = ProcessingNodeOptionsCodec.Decode(descriptor, source);
        Assert.IsTrue(decoded.IsValid);
        Assert.AreEqual(source.GetRawText(), ProcessingNodeOptionsCodec.Encode(descriptor, source,
            decoded.Options.ToDictionary(static item => item.Descriptor.Key, static item => item.Value)).Options!.Value.GetRawText());
        var changed = ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
        {
            ["outputVariant"] = ProcessingOptionValue.FromText("corrected")
        });
        Assert.IsTrue(changed.IsValid);
        Assert.AreEqual(source.GetRawText()[..^2] + ",\"outputVariant\":\"corrected\" }", changed.Options!.Value.GetRawText());
        Assert.AreEqual("{}", ProcessingNodeOptionsCodec.Encode(descriptor, Json("{}"),
            defaults.Options.ToDictionary(static item => item.Descriptor.Key, static item => item.Value)).Options!.Value.GetRawText());
    }

    [TestMethod]
    [DataRow("strategy", "\"None\"", true)]
    [DataRow("strategy", "\"SyntheticReferences\"", true)]
    [DataRow("strategy", "\"CalibrationLibrary\"", true)]
    [DataRow("strategy", "\"none\"", false)]
    [DataRow("strategy", "\"Other\"", false)]
    [DataRow("strategy", "null", false)]
    [DataRow("strategy", "1", false)]
    [DataRow("enabled", "false", true)]
    [DataRow("enabled", "\"false\"", false)]
    [DataRow("outputVariant", "\"corrected\"", true)]
    [DataRow("outputVariant", "\"\"", false)]
    [DataRow("outputVariant", "\" route\"", false)]
    [DataRow("outputVariant", "null", false)]
    public void CalibrationCodecValidatesOnlyEffectiveScalars(string member, string token, bool valid)
    {
        foreach (var descriptor in new[] { CalibrationOptionDescriptors.Normalization, CalibrationOptionDescriptors.References })
            Assert.AreEqual(valid, ProcessingNodeOptionsCodec.Decode(descriptor, Json("{\"" + member + "\":" + token + "}")).IsValid);
    }

    [TestMethod]
    public void CalibrationCodecRejectsDuplicatesOversizedRoutesAndNestedEdits()
    {
        var descriptor = CalibrationOptionDescriptors.Normalization;
        Assert.AreEqual(ProcessingOptionReasonCodes.DuplicateMember, ProcessingNodeOptionsCodec.Decode(descriptor,
            Json("""{"strategy":"None","Strategy":"None"}""")).Diagnostics.Single().Code);
        Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(descriptor, Json("{\"outputVariant\":\"" + new string('v', 128) + "\"}")).IsValid);
        Assert.AreEqual(ProcessingOptionReasonCodes.TooLong, ProcessingNodeOptionsCodec.Decode(descriptor,
            Json("{\"outputVariant\":\"" + new string('v', 129) + "\"}")).Diagnostics.Single().Code);
        Assert.AreEqual(ProcessingOptionReasonCodes.UnknownOption, ProcessingNodeOptionsCodec.Encode(descriptor, Json("{}"),
            new Dictionary<string, ProcessingOptionValue> { ["syntheticCalibration"] = ProcessingOptionValue.FromText("replacement") }).Diagnostics.Single().Code);
    }
}
