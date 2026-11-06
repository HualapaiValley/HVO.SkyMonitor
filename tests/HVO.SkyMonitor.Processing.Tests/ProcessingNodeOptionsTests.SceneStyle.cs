namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProcessingNodeOptionsTests
{
    [TestMethod]
    public void SceneStyleDescriptorPinsExactProducerAndClosedDefaultsAndPreservesUnchangedBytes()
    {
        var descriptor = SceneStylePresentation.Descriptor;
        Assert.AreEqual("scene-style-presentation-options-v1", descriptor.DescriptorId);
        Assert.AreEqual("SceneStylePresentationLayerV1", descriptor.StepAlias);
        Assert.AreEqual("projected-scene-presentation-v14-explicit-style", descriptor.StepVersion);
        Assert.AreSame(descriptor, ProcessingNodeOptionsCatalog.BuiltIn.Resolve(descriptor.StepAlias.ToUpperInvariant(), descriptor.StepVersion).Descriptor);
        Assert.AreEqual(ProcessingNodeOptionsResolutionStatus.UnsupportedVersion,
            ProcessingNodeOptionsCatalog.BuiltIn.Resolve(descriptor.StepAlias, descriptor.StepVersion + "-future").Status);
        var source = Json("""{ "MarkerRed":144, "future":{"number":1e+02,"text":"Café"}, "markerValue":null }""");
        var decoded = ProcessingNodeOptionsCodec.Decode(descriptor, source);
        Assert.IsTrue(decoded.IsValid);
        Assert.AreEqual(new HVO.SkyMonitor.Imaging.PresentationColor(144, 144, 144), decoded.Get("markerColor").Value.AsColor());
        Assert.AreEqual("MonoBold", decoded.Get("labelFont").Value.AsText());
        Assert.AreEqual("MonoBold", decoded.Get("cardinalFont").Value.AsText());
        Assert.AreEqual("Circle", decoded.Get("pointGlyph").Value.AsText());
        Assert.AreEqual(ProcessingOptionKind.Glyph, descriptor.Find("pointGlyph")!.Kind);
        var unchanged = ProcessingNodeOptionsCodec.Encode(descriptor, source, decoded.Options.ToDictionary(item => item.Descriptor.Key, item => item.Value));
        Assert.IsTrue(unchanged.IsValid);
        Assert.AreEqual(source.GetRawText(), unchanged.Options!.Value.GetRawText());
        var edited = ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
        { ["pointGlyph"] = ProcessingOptionValue.FromGlyph("Crosshair"), ["labelFont"] = ProcessingOptionValue.FromFont("PlexRegular") });
        Assert.IsTrue(edited.IsValid);
        Assert.AreEqual(source.GetProperty("future").GetRawText(), edited.Options!.Value.GetProperty("future").GetRawText());
        Assert.IsFalse(edited.Options.Value.TryGetProperty("cardinalFont", out _));
        Assert.IsTrue(edited.Options.Value.TryGetProperty("markerValue", out _));
    }

    [TestMethod]
    [DataRow("labelFont", "\"Arial\"", ProcessingOptionReasonCodes.NotAllowed)]
    [DataRow("labelFont", "\"PlexBold\"", ProcessingOptionReasonCodes.NotAllowed)]
    [DataRow("labelFont", "\"monobold\"", ProcessingOptionReasonCodes.NotAllowed)]
    [DataRow("cardinalFont", "2", ProcessingOptionReasonCodes.WrongType)]
    [DataRow("pointGlyph", "\"★\"", ProcessingOptionReasonCodes.NotAllowed)]
    [DataRow("pointGlyph", "\"crosshair\"", ProcessingOptionReasonCodes.NotAllowed)]
    [DataRow("pointGlyph", "null", ProcessingOptionReasonCodes.WrongType)]
    [DataRow("markerRed", "256", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("markerGreen", "-1", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("markerBlue", "0.1", ProcessingOptionReasonCodes.WrongType)]
    [DataRow("labelRed", "-1", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("cardinalBlue", "256", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("imageCircleGreen", "256", ProcessingOptionReasonCodes.OutOfRange)]
    public void SceneStyleRejectsUnsupportedAndOutOfRangeValues(string key, string token, string code)
    {
        var decoded = ProcessingNodeOptionsCodec.Decode(SceneStylePresentation.Descriptor, Json($$"""{"{{key}}":{{token}}}"""));
        Assert.IsFalse(decoded.IsValid);
        Assert.AreEqual(code, decoded.Diagnostics.Single().Code);
    }

    [TestMethod]
    [DataRow("{\"labelFont\":\"MonoBold\",\"LabelFont\":\"PlexRegular\"}")]
    [DataRow("{\"markerRed\":1,\"MarkerRed\":2}")]
    public void SceneStyleRejectsDuplicateKnownMembers(string json)
    {
        Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(SceneStylePresentation.Descriptor, Json(json)).Diagnostics
            .Any(item => item.Code == ProcessingOptionReasonCodes.DuplicateMember));
    }

    [TestMethod]
    public void SceneStyleUnknownDuplicateMembersRemainOpaqueAndUntouched()
    {
        var source = Json("{ \"future\":1,\"future\":2 }");
        var descriptor = SceneStylePresentation.Descriptor;
        Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(descriptor, source).IsValid);
        var encoded = ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>());
        Assert.IsTrue(encoded.IsValid);
        Assert.AreEqual(source.GetRawText(), encoded.Options!.Value.GetRawText());
    }

    [TestMethod]
    public void BuiltInSceneUpgradeIsWholeDraftExplicitStaleSafeAndPreservesLegacyOptions()
    {
        var draft = Graph(Node("v12", Scene.StepAlias, ProcessingNodeOptionsDescriptors.ScenePresentationLayerV12.StepVersion,
            """{ "markerValue":0, "future":[1e+02] }"""), Node("v13", Scene.StepAlias, SceneVersion, "{}", enabled: false));
        var bytes = ProcessingGraphJson.SerializeCanonical(draft);
        var catalog = ProcessingNodeOptionsCatalog.BuiltIn;
        Assert.IsEmpty(catalog.Validate(draft));
        var plan = catalog.PlanUpgrade(draft);
        Assert.HasCount(2, plan.Steps);
        Assert.AreEqual(ProcessingOptionReasonCodes.UpgradePlanStale,
            catalog.ApplyUpgrade(draft with { Revision = "stale" }, plan, "2").Diagnostics.Single().Code);
        var result = catalog.ApplyUpgrade(draft, plan, "2");
        Assert.IsTrue(result.IsValid, string.Join(' ', result.Diagnostics));
        Assert.IsTrue(result.Definition!.Nodes.All(node => node.StepAlias == SceneStylePresentation.StepAlias && node.StepVersion == SceneStylePresentation.ProducerVersion));
        var first = result.Definition.Nodes[0].EffectiveOptions;
        Assert.AreEqual("[1e+02]", first.GetProperty("future").GetRawText());
        Assert.AreEqual(0, first.GetProperty("markerRed").GetInt32());
        Assert.IsFalse(first.GetProperty("expectedPositionDiagnostics").GetBoolean());
        CollectionAssert.AreEqual(bytes, ProcessingGraphJson.SerializeCanonical(draft));
        var invalid = Graph(Node("invalid", Scene.StepAlias, SceneVersion, "{\"labelFont\":\"PlexBold\"}"));
        Assert.IsFalse(catalog.ApplyUpgrade(invalid, catalog.PlanUpgrade(invalid), "2").IsValid);
    }
}
