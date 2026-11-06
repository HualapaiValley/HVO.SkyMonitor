namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProcessingNodeOptionsTests
{
    [TestMethod]
    [DataRow("ImageQuality", "integer-image-statistics-v1", 2)]
    [DataRow("Telemetry", "cameraagent-step-v2:Telemetry", 1)]
    [DataRow("VirtualSkyCloudObservation", "cameraagent-step-v2:VirtualSkyCloudObservation", 1)]
    public void CaptureScalarDescriptorsPreserveDefaultsUnknownMembersAndExactVersions(string alias, string version, int count)
    {
        ArgumentNullException.ThrowIfNull(alias);
        var catalog = ProcessingNodeOptionsCatalog.BuiltIn;
        var descriptor = catalog.Resolve(alias.ToUpperInvariant(), version).Descriptor!;
        var source = Json("""{ "future" : [1e+02], "Enabled": true }""");
        var decoded = ProcessingNodeOptionsCodec.Decode(descriptor, source);
        Assert.IsTrue(decoded.IsValid);
        Assert.HasCount(count, decoded.Options);
        Assert.IsTrue(decoded.Get("enabled").Value.AsToggle());
        var defaults = ProcessingNodeOptionsCodec.Decode(descriptor, Json("{}"));
        Assert.IsTrue(defaults.Get("enabled").Value.AsToggle());
        var unchanged = ProcessingNodeOptionsCodec.Encode(descriptor, source,
            decoded.Options.ToDictionary(static item => item.Descriptor.Key, static item => item.Value));
        Assert.AreEqual(source.GetRawText(), unchanged.Options!.Value.GetRawText());
        var edited = ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
        {
            ["enabled"] = ProcessingOptionValue.FromToggle(false)
        });
        Assert.IsTrue(edited.IsValid);
        Assert.AreEqual(source.GetRawText().Replace("true", "false", StringComparison.Ordinal), edited.Options!.Value.GetRawText());
        Assert.AreEqual(ProcessingNodeOptionsResolutionStatus.UnsupportedVersion, catalog.Resolve(alias, version + "-future").Status);
        Assert.AreEqual(ProcessingOptionReasonCodes.WrongType,
            ProcessingNodeOptionsCodec.Decode(descriptor, Json("""{"enabled":"false"}""")).Diagnostics.Single().Code);
        Assert.AreEqual(ProcessingOptionReasonCodes.DuplicateMember,
            ProcessingNodeOptionsCodec.Decode(descriptor, Json("""{"enabled":true,"Enabled":false}""")).Diagnostics.Single().Code);
    }

    [TestMethod]
    [DataRow("", ProcessingOptionReasonCodes.NotAllowed)]
    [DataRow(" ", ProcessingOptionReasonCodes.NotAllowed)]
    [DataRow(" quality", ProcessingOptionReasonCodes.NotAllowed)]
    [DataRow("quality ", ProcessingOptionReasonCodes.NotAllowed)]
    [DataRow("quality\nroute", ProcessingOptionReasonCodes.NotAllowed)]
    public void QualityOutputRouteRejectsInvalidGraphIdentifiers(string route, string reason)
    {
        var encoded = ProcessingNodeOptionsCodec.Encode(CaptureScalarOptionDescriptors.ImageQuality, Json("{}"),
            new Dictionary<string, ProcessingOptionValue> { ["outputVariant"] = ProcessingOptionValue.FromText(route) });
        Assert.IsFalse(encoded.IsValid);
        Assert.AreEqual(reason, encoded.Diagnostics.Single().Code);
    }

    [TestMethod]
    public void QualityOutputRouteUsesExistingGraphLengthBoundaryAndStrictJsonTypes()
    {
        var descriptor = CaptureScalarOptionDescriptors.ImageQuality;
        Assert.AreEqual("image-quality-v1", ProcessingNodeOptionsCodec.Decode(descriptor, Json("{}")).Get("outputVariant").Value.AsText());
        Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(descriptor, Json("{\"outputVariant\":\"" + new string('q', 128) + "\"}")).IsValid);
        Assert.AreEqual(ProcessingOptionReasonCodes.TooLong,
            ProcessingNodeOptionsCodec.Decode(descriptor, Json("{\"outputVariant\":\"" + new string('q', 129) + "\"}")).Diagnostics.Single().Code);
        foreach (var token in new[] { "null", "42", "false", "[]" })
            Assert.AreEqual(ProcessingOptionReasonCodes.WrongType,
                ProcessingNodeOptionsCodec.Decode(descriptor, Json("{\"outputVariant\":" + token + "}")).Diagnostics.Single().Code);
    }
}
