namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProcessingNodeOptionsTests
{
    [TestMethod]
    public void ManifestDescriptorPinsExactVersionAndPreservesUntouchedRowsAndRootBytes()
    {
        var descriptor = OverlayManifestOptionDescriptors.Descriptor;
        Assert.AreSame(descriptor, ProcessingNodeOptionsCatalog.BuiltIn.Resolve("oVeRlAyManifest", "overlay-manifest-v1").Descriptor);
        Assert.IsNull(ProcessingNodeOptionsCatalog.BuiltIn.Resolve("OverlayManifest", "overlay-manifest-v2").Descriptor);
        const string row = "{ \"KiNd\":\"cloud-mask\", \"EnAbLed\":false, \"ZOrder\":null, \"future\":1e+02 }";
        const string source = "{ \"Layers\" : [ " + row + " ], \"useRichFrameFacts\":false, \"outputVariant\":\"saved\", \"future\":\"\\u0041\", \"Future\":null }";
        var decoded = ProcessingNodeOptionsCodec.Decode(descriptor, Json(source));
        Assert.IsTrue(decoded.IsValid);
        var edits = decoded.Options.ToDictionary(static field => field.Descriptor.Key, static field => field.Value);
        Assert.AreEqual(source, ProcessingNodeOptionsCodec.Encode(descriptor, Json(source), edits).Options!.Value.GetRawText());
        edits["constellationOpacityMillionths"] = ProcessingOptionValue.FromWholeNumber(0);
        Assert.AreEqual(source[..^2] + ",\"constellationOpacityMillionths\":0 }",
            ProcessingNodeOptionsCodec.Encode(descriptor, Json(source), edits).Options!.Value.GetRawText());
        var rowEdit = ProcessingNodeOptionsCodec.Encode(OverlayManifestOptionDescriptors.Layer, Json(row),
            new Dictionary<string, ProcessingOptionValue> { ["zOrder"] = ProcessingOptionValue.FromNullableWholeNumber(-1024) });
        edits["layers"] = ProcessingOptionValue.FromRows([rowEdit.Options!.Value.GetRawText()]);
        StringAssert.Contains(ProcessingNodeOptionsCodec.Encode(descriptor, Json(source), edits).Options!.Value.GetRawText(),
            row.Replace("null", "-1024", StringComparison.Ordinal), StringComparison.Ordinal);
        foreach (var key in new[] { "useRichFrameFacts", "outputVariant", "basePreviewVariant", "satelliteVariant", "sceneAnnotationVariant", "environmentVariant" })
            Assert.AreEqual(ProcessingOptionReasonCodes.UnknownOption, ProcessingNodeOptionsCodec.Encode(descriptor, Json(source),
                new Dictionary<string, ProcessingOptionValue> { [key] = ProcessingOptionValue.FromText("changed") }).Diagnostics.Single().Code);
        foreach (var host in new[] { ProcessingGraphHosts.CameraAgent, ProcessingGraphHosts.LogicHost })
        {
            var graph = Graph(Node("manifest", descriptor.StepAlias, descriptor.StepVersion, source));
            var before = ProcessingGraphCompiler.Compile(graph, new(host, []));
            var after = ProcessingGraphCompiler.Compile(RoundTrip(ProcessingNodeOptionsCatalog.BuiltIn, graph), new(host, []));
            Assert.IsTrue(before.IsValid);
            Assert.AreEqual(before.Plan!.PlanIdentitySha256, after.Plan!.PlanIdentitySha256);
        }
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"layers\":[]}")]
    [DataRow("{\"layers\":[{\"kind\":\"constellations\",\"enabled\":false,\"zOrder\":-1024},{\"kind\":\"environment\",\"zOrder\":1024}]}")]
    [DataRow("{\"layers\":[{\"kind\":\"scene-annotation\",\"zOrder\":null}],\"constellationOpacityMillionths\":0}")]
    [DataRow("{\"constellationOpacityMillionths\":1000000}")]
    public void ManifestValidOptionsRetainAbsentDefaultAndNullSemantics(string source)
    {
        var descriptor = OverlayManifestOptionDescriptors.Descriptor;
        var decoded = ProcessingNodeOptionsCodec.Decode(descriptor, Json(source));
        Assert.IsTrue(decoded.IsValid);
        Assert.AreEqual(source, ProcessingNodeOptionsCodec.Encode(descriptor, Json(source),
            decoded.Options.ToDictionary(static field => field.Descriptor.Key, static field => field.Value)).Options!.Value.GetRawText());
    }

    [TestMethod]
    [DataRow("{\"layers\":null}")]
    [DataRow("{\"layers\":{}}")]
    [DataRow("{\"layers\":[null]}")]
    [DataRow("{\"layers\":[{}]}")]
    [DataRow("{\"layers\":[{\"kind\":\"Environment\"}]}")]
    [DataRow("{\"layers\":[{\"kind\":\"unknown\"}]}")]
    [DataRow("{\"layers\":[{\"kind\":\"environment\",\"Kind\":\"cloud-mask\"}]}")]
    [DataRow("{\"layers\":[{\"kind\":\"environment\",\"enabled\":null}]}")]
    [DataRow("{\"layers\":[{\"kind\":\"environment\",\"enabled\":1}]}")]
    [DataRow("{\"layers\":[{\"kind\":\"environment\",\"zOrder\":-1025}]}")]
    [DataRow("{\"layers\":[{\"kind\":\"environment\",\"zOrder\":1025}]}")]
    [DataRow("{\"layers\":[{\"kind\":\"environment\",\"zOrder\":1.0}]}")]
    [DataRow("{\"layers\":[{\"kind\":\"environment\",\"zOrder\":\"1\"}]}")]
    [DataRow("{\"layers\":[{\"kind\":\"environment\"},{\"kind\":\"corner-annotations\"}]}")]
    [DataRow("{\"layers\":[{\"kind\":\"scene-constellations\"},{\"kind\":\"constellations\"}]}")]
    [DataRow("{\"layers\":[],\"LAYERS\":[]}")]
    [DataRow("{\"constellationOpacityMillionths\":-1}")]
    [DataRow("{\"constellationOpacityMillionths\":1000001}")]
    [DataRow("{\"constellationOpacityMillionths\":null}")]
    [DataRow("{\"constellationOpacityMillionths\":\"800000\"}")]
    [DataRow("{\"constellationOpacityMillionths\":1e0}")]
    public void ManifestInvalidRowsAndOpacityAreRejectedByBothHostValidation(string source)
    {
        var descriptor = OverlayManifestOptionDescriptors.Descriptor;
        Assert.IsFalse(ProcessingNodeOptionsCodec.Decode(descriptor, Json(source)).IsValid);
        var graph = Graph(Node("manifest", descriptor.StepAlias, descriptor.StepVersion, source));
        Assert.IsNotEmpty(ProcessingNodeOptionsCatalog.BuiltIn.Validate(graph));
        Assert.IsFalse(ProcessingNodeOptionsCodec.Encode(descriptor, Json("{}"),
            new Dictionary<string, ProcessingOptionValue> { ["layers"] = ProcessingOptionValue.FromRows(["null"]) }).IsValid);
    }

    [TestMethod]
    public void ManifestRowLimitIsEnforcedForSourceAndTypedEdits()
    {
        var descriptor = OverlayManifestOptionDescriptors.Descriptor;
        var rows = Enumerable.Repeat("{\"kind\":\"environment\"}", 11).ToArray();
        Assert.AreEqual(ProcessingOptionReasonCodes.OutOfRange, ProcessingNodeOptionsCodec.Decode(descriptor,
            Json("{\"layers\":[" + string.Join(',', rows) + "]}")).Diagnostics.Single().Code);
        Assert.AreEqual(ProcessingOptionReasonCodes.OutOfRange, ProcessingNodeOptionsCodec.Encode(descriptor, Json("{}"),
            new Dictionary<string, ProcessingOptionValue> { ["layers"] = ProcessingOptionValue.FromRows(rows) }).Diagnostics.Single().Code);
    }
}
