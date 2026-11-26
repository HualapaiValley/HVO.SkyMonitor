namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProcessingNodeOptionsTests
{
    [TestMethod]
    public void SceneStyleDescriptorPinsExactProducerAndClosedDefaultsAndPreservesUnchangedBytes()
    {
        var descriptor = SceneStylePresentation.Descriptor;
        Assert.AreEqual("scene-style-presentation-options-v1", descriptor.DescriptorId);
        Assert.AreEqual("SceneStylePresentationLayerV1", descriptor.StepAlias);
        Assert.AreEqual("projected-scene-presentation-v15-explicit-style", descriptor.StepVersion);
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

    [TestMethod]
    public void RetainedShippedLegacyDescriptorsSupportInspectionAndWholeDraftUpgradeFromBaseV13Draft()
    {
        var catalog = ProcessingNodeOptionsCatalog.BuiltIn;
        const string v13Scene = "projected-scene-presentation-v13-plain-circle-bounded-expected-labels";
        const string v14Style = "projected-scene-presentation-v14-explicit-style";
        const string v4Annotation = "projected-annotation-v4";
        const string v1Measured = "measured-stellar-associations-v1";
        const string v1ExposureAssoc = "associate-exposure-measurement-v1";

        // Every previously shipped descriptor remains Described in BuiltIn for lossless inspection.
        var v13Res = catalog.Resolve("ScenePresentationLayer", v13Scene);
        Assert.AreEqual(ProcessingNodeOptionsResolutionStatus.Described, v13Res.Status);
        Assert.AreSame(ProcessingNodeOptionsDescriptors.ScenePresentationLayerV13, v13Res.Descriptor);
        Assert.IsNotNull(v13Res.Upgrade);
        Assert.AreEqual(SceneStylePresentation.StepAlias, v13Res.Upgrade!.ToStepAlias);
        Assert.AreEqual(SceneStylePresentation.ProducerVersion, v13Res.Upgrade.ToStepVersion);

        var v14Res = catalog.Resolve(SceneStylePresentation.StepAlias, v14Style);
        Assert.AreEqual(ProcessingNodeOptionsResolutionStatus.Described, v14Res.Status);
        Assert.AreSame(SceneStylePresentation.DescriptorV14, v14Res.Descriptor);

        var v4AnnRes = catalog.Resolve("Annotation", v4Annotation);
        Assert.AreEqual(ProcessingNodeOptionsResolutionStatus.Described, v4AnnRes.Status);
        Assert.AreSame(AnnotationOptionDescriptors.DescriptorV4, v4AnnRes.Descriptor);
        // Retained v4 annotation descriptor still enforces corner-line selection validation.
        var tooManyCornerLines = ProcessingNodeOptionsCodec.Decode(v4AnnRes.Descriptor!,
            Json("""{"topLeftTokens":["agent-identity","capture-sequence","utc","environment"],"environmentalKinds":["AirTemperature","RelativeHumidity","AtmosphericPressure","WindSpeed","RainState","CloudCover"]}"""));
        Assert.IsFalse(tooManyCornerLines.IsValid);

        var v1MeasRes = catalog.Resolve("MeasuredStellarAssociations", v1Measured);
        Assert.AreEqual(ProcessingNodeOptionsResolutionStatus.Described, v1MeasRes.Status);
        Assert.AreSame(CaptureAnalysisOptionDescriptors.StellarAssociationsV1, v1MeasRes.Descriptor);

        var v1ExpRes = catalog.Resolve(ExposureAssociationProducts.StepAlias, v1ExposureAssoc);
        Assert.AreEqual(ProcessingNodeOptionsResolutionStatus.Described, v1ExpRes.Status);
        Assert.AreSame(CaptureAnalysisOptionDescriptors.CenteredStellarAssociationsV1, v1ExpRes.Descriptor);

        // Whole-draft scene-style upgrade from a draft saved against base v13 upgrades scene + consumers cleanly.
        var baseDraft = Graph(
            Node("legacy-scene", Scene.StepAlias, v13Scene, """{ "markerValue":120, "expectedPositionDiagnostics":false, "future":[1e+02] }"""),
            Node("legacy-annotation", "Annotation", v4Annotation, """{ "markRadius":8 }"""),
            Node("legacy-measured", "MeasuredStellarAssociations", v1Measured, """{ "enabled":true }"""),
            Node("legacy-exposure-assoc", ExposureAssociationProducts.StepAlias, v1ExposureAssoc, """{ "enabled":true }"""),
            Node("legacy-style", SceneStylePresentation.StepAlias, v14Style, """{ "pointGlyph":"Crosshair" }"""),
            Node("manifest", "OverlayManifest", "overlay-manifest-v1", "{}"),
            Node("materializer", "PresentationMaterializer", "typed-presentation-materialization-v1", "{}"));

        Assert.IsEmpty(catalog.Validate(baseDraft));
        var plan = catalog.PlanUpgrade(baseDraft);
        var step = Assert.ContainsSingle(plan.Steps);
        Assert.AreEqual("legacy-scene", step.NodeId);
        Assert.AreEqual(v13Scene, step.FromStepVersion);
        Assert.AreEqual(SceneStylePresentation.StepAlias, step.ToStepAlias);
        Assert.AreEqual(SceneStylePresentation.ProducerVersion, step.ToStepVersion);

        var upgraded = catalog.ApplyUpgrade(baseDraft, plan, "2");
        Assert.IsTrue(upgraded.IsValid, string.Join(' ', upgraded.Diagnostics));
        var upgradedScene = upgraded.Definition!.Nodes.Single(static n => n.Id == "legacy-scene");
        Assert.AreEqual(SceneStylePresentation.StepAlias, upgradedScene.StepAlias);
        Assert.AreEqual(SceneStylePresentation.ProducerVersion, upgradedScene.StepVersion);
        Assert.AreEqual(120, upgradedScene.EffectiveOptions.GetProperty("markerRed").GetInt32());
        Assert.IsFalse(upgradedScene.EffectiveOptions.GetProperty("expectedPositionDiagnostics").GetBoolean());
        Assert.AreEqual("[1e+02]", upgradedScene.EffectiveOptions.GetProperty("future").GetRawText());
        Assert.IsTrue(upgraded.Definition.Nodes.Single(static n => n.Id == "manifest").EffectiveOptions.GetProperty("useSceneStyle").GetBoolean());
        Assert.IsTrue(upgraded.Definition.Nodes.Single(static n => n.Id == "materializer").EffectiveOptions.GetProperty("useSceneStyle").GetBoolean());
        // Unrelated legacy nodes remain intact and still validate cleanly.
        Assert.AreEqual(v4Annotation, upgraded.Definition.Nodes.Single(static n => n.Id == "legacy-annotation").StepVersion);
        Assert.AreEqual(v1Measured, upgraded.Definition.Nodes.Single(static n => n.Id == "legacy-measured").StepVersion);
        Assert.AreEqual(v1ExposureAssoc, upgraded.Definition.Nodes.Single(static n => n.Id == "legacy-exposure-assoc").StepVersion);
        Assert.AreEqual(v14Style, upgraded.Definition.Nodes.Single(static n => n.Id == "legacy-style").StepVersion);
        Assert.IsEmpty(catalog.Validate(upgraded.Definition));
    }
}
