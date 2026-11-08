namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProcessingNodeOptionsTests
{
    [TestMethod]
    [DataRow("MeasuredStellarAssociations", "measured-stellar-associations-v1", "outputVariant", "null")]
    [DataRow("MeasuredStellarAssociations", "measured-stellar-associations-v1", "enabled", "1")]
    [DataRow("MeasuredAstrometry", "measured-astrometry-v1", "captureStride", "59")]
    [DataRow("MeasuredAstrometry", "measured-astrometry-v1", "captureStride", "1000001")]
    [DataRow("MeasuredAstrometry", "measured-astrometry-v1", "captureStride", "60.5")]
    [DataRow("MeasuredAstrometry", "measured-astrometry-v1", "captureStride", "null")]
    [DataRow("CaptureSatelliteTracks", "capture-satellite-products-v1", "allowStaleDiagnostics", "\"true\"")]
    [DataRow("CaptureSatelliteTracks", "capture-satellite-products-v1", "outputVariant", "\" \"")]
    [DataRow("CaptureSatelliteLayer", "predicted-satellite-tracks-v1", "inputVariant", "\"\"")]
    [DataRow("CaptureSatelliteLayer", "predicted-satellite-tracks-v1", "inputVariant", "42")]
    [DataRow("Storage", "cameraagent-step-v2:Storage", "retentionDays", "0")]
    [DataRow("Storage", "cameraagent-step-v2:Storage", "retentionDays", "3651")]
    [DataRow("Storage", "cameraagent-step-v2:Storage", "storageRoot", "null")]
    [DataRow("Storage", "cameraagent-step-v2:Storage", "queueForUpload", "1")]
    public void RemainingNodeDescriptorsRejectInvalidSourceAndDoNotRewriteIt(string alias, string version, string key, string token)
    {
        var descriptor = ProcessingNodeOptionsCatalog.BuiltIn.Resolve(alias, version).Descriptor;
        Assert.IsNotNull(descriptor, alias);
        var source = Json("{\"" + key + "\":" + token + ",\"future\":[1e+02]}");
        Assert.IsFalse(ProcessingNodeOptionsCodec.Decode(descriptor, source).IsValid);
        Assert.IsFalse(ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>()).IsValid);
        Assert.AreEqual("[1e+02]", source.GetProperty("future").GetRawText());
    }

    [TestMethod]
    [DataRow("MeasuredStellarAssociations", "measured-stellar-associations-v1", "outputVariant", "measured-stellar-associations-v1")]
    [DataRow("MeasuredAstrometry", "measured-astrometry-v1", "outputVariant", "measured-astrometry-v1")]
    [DataRow("CaptureSatelliteTracks", "capture-satellite-products-v1", "outputVariant", "capture-satellites-v1")]
    [DataRow("CaptureSatelliteLayer", "predicted-satellite-tracks-v1", "inputVariant", "capture-satellites-v1")]
    [DataRow("Storage", "cameraagent-step-v2:Storage", "storageRoot", "/tmp/camera")]
    public void RemainingNodeDescriptorsBindDefaultsAndPreserveSource(string alias, string version, string key, string expected)
    {
        var descriptor = ProcessingNodeOptionsCatalog.BuiltIn.Resolve(alias, version).Descriptor;
        Assert.IsNotNull(descriptor, alias);
        const string source = """{ "future": [1e+02] }""";
        var decoded = ProcessingNodeOptionsCodec.Decode(descriptor, Json(source));
        Assert.IsTrue(decoded.IsValid);
        Assert.AreEqual(expected, decoded.Get(key).Value.AsText());
        Assert.IsFalse(decoded.Get(key).IsExplicit);
        var unchanged = ProcessingNodeOptionsCodec.Encode(descriptor, Json(source), decoded.Options.ToDictionary(
            static item => item.Descriptor.Key, static item => item.Value));
        Assert.IsTrue(unchanged.IsValid);
        Assert.AreEqual(source, unchanged.Options!.Value.GetRawText());
        var changed = ProcessingNodeOptionsCodec.Encode(descriptor, Json(source), new Dictionary<string, ProcessingOptionValue>
        { [key] = ProcessingOptionValue.FromText("custom-route") });
        Assert.IsTrue(changed.IsValid);
        Assert.AreEqual("custom-route", ProcessingNodeOptionsCodec.Decode(descriptor, changed.Options!.Value).Get(key).Value.AsText());
        Assert.AreEqual("[1e+02]", changed.Options.Value.GetProperty("future").GetRawText());
    }
}
