namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProcessingNodeOptionsTests
{
    [TestMethod]
    [DataRow("projected-scene-presentation-v12-plain-circle-markers")]
    [DataRow(SceneVersion)]
    public void SceneScalarEditsPreserveCollectionsNullableLegacyMembersAndUnchangedBytes(string version)
    {
        var descriptor = ProcessingNodeOptionsCatalog.BuiltIn.Resolve("sCeNePrEsEnTaTiOnLaYeR", version).Descriptor!;
        var source = Json("""{ "DrawMarkers" : true, "constellationIds": [ "Ori", "UMa" ], "markerValue":null, "labelValue":17, "imageCircleValue":null, "cardinalValue":128, "expectedPositionDiagnostics":true, "future":{"x":1e+02} }""");
        var decoded = ProcessingNodeOptionsCodec.Decode(descriptor, source);
        Assert.IsTrue(decoded.IsValid);
        Assert.HasCount(version == SceneVersion ? 29 : 17, decoded.Options);
        Assert.AreEqual(6, decoded.Get("markerRadius").Value.AsWholeNumber());
        Assert.AreEqual(2, decoded.Get("labelScale").Value.AsWholeNumber());
        Assert.AreEqual(24, decoded.Get("maximumLabelCharacters").Value.AsWholeNumber());
        Assert.AreEqual(2.5, decoded.Get("maximumLabelMagnitude").Value.AsRealNumber());
        Assert.AreEqual(2, decoded.Get("cardinalScale").Value.AsWholeNumber());
        foreach (var key in new[] { "drawMarkers", "drawLabels", "drawConstellationLines", "drawImageCircle", "drawCardinalDirections" })
            Assert.IsTrue(decoded.Get(key).Value.AsToggle(), key);
        if (version == SceneVersion)
        {
            Assert.IsTrue(decoded.Get("expectedPositionDiagnostics").Value.AsToggle());
            var strict = ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
            {
                ["expectedPositionDiagnostics"] = ProcessingOptionValue.FromToggle(false)
            });
            Assert.IsTrue(strict.IsValid);
            Assert.IsFalse(strict.Options!.Value.GetProperty("expectedPositionDiagnostics").GetBoolean());
            Assert.AreEqual(source.GetProperty("future").GetRawText(), strict.Options.Value.GetProperty("future").GetRawText());
        }

        var unchanged = ProcessingNodeOptionsCodec.Encode(descriptor, source,
            decoded.Options.ToDictionary(static item => item.Descriptor.Key, static item => item.Value, StringComparer.Ordinal));
        Assert.IsTrue(unchanged.IsValid);
        Assert.AreEqual(source.GetRawText(), unchanged.Options!.Value.GetRawText());

        var edited = ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
        {
            ["drawMarkers"] = ProcessingOptionValue.FromToggle(false),
            ["maximumLabelMagnitude"] = ProcessingOptionValue.FromRealNumber(-1.25)
        });
        Assert.IsTrue(edited.IsValid);
        Assert.AreEqual(source.GetRawText().Replace("true, \"constellationIds\"", "false, \"constellationIds\"", StringComparison.Ordinal)
            .Replace(" }", ",\"maximumLabelMagnitude\":-1.25 }", StringComparison.Ordinal), edited.Options!.Value.GetRawText());
        Assert.IsFalse(edited.Options.Value.TryGetProperty("labelScale", out _), "An edit must not materialize other defaults.");
        Assert.HasCount(2, ProcessingNodeOptionsCatalog.BuiltIn.Upgrades);
    }

    [TestMethod]
    [DataRow("drawMarkers", "null", ProcessingOptionReasonCodes.WrongType)]
    [DataRow("drawLabels", "1", ProcessingOptionReasonCodes.WrongType)]
    [DataRow("drawConstellationLines", "\"true\"", ProcessingOptionReasonCodes.WrongType)]
    [DataRow("drawImageCircle", "[]", ProcessingOptionReasonCodes.WrongType)]
    [DataRow("drawCardinalDirections", "{}", ProcessingOptionReasonCodes.WrongType)]
    [DataRow("markerRadius", "-1", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("markerRadius", "33", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("labelScale", "0", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("labelScale", "9", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("maximumLabelCharacters", "-1", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("maximumLabelCharacters", "65", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("maximumLabelMagnitude", "-30.1", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("maximumLabelMagnitude", "30.1", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("maximumLabelMagnitude", "1e400", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("maximumLabelMagnitude", "\"NaN\"", ProcessingOptionReasonCodes.WrongType)]
    [DataRow("cardinalScale", "0", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("cardinalScale", "9", ProcessingOptionReasonCodes.OutOfRange)]
    public void SceneScalarValidationRejectsInvalidValuesAtBothExactVersions(string member, string token, string reason)
    {
        foreach (var descriptor in new[] { Scene, ProcessingNodeOptionsDescriptors.ScenePresentationLayerV12 })
        {
            var diagnostic = ProcessingNodeOptionsCodec.Decode(descriptor, Json($$"""{"{{member}}":{{token}}}""")).Diagnostics.Single();
            Assert.AreEqual(reason, diagnostic.Code);
            Assert.AreEqual($"effectiveOptions.{member}", diagnostic.Path);
        }
    }

    [TestMethod]
    public void DeepSkyPresentationDescriptorsValidateBoundsDefaultsAndPreserveUnknownFields()
    {
        foreach (var descriptor in new[] { Scene, SceneStylePresentation.Descriptor })
        {
            var source = Json("""{ "future": [1e+02] }""");
            var defaults = ProcessingNodeOptionsCodec.Decode(descriptor, source);
            Assert.IsTrue(defaults.IsValid, descriptor.DescriptorId);
            Assert.IsFalse(defaults.Get("includeDeepSkyLayer").Value.AsToggle());
            Assert.IsTrue(defaults.Get("deepSkyLabels").Value.AsToggle());
            Assert.AreEqual(24, defaults.Get("deepSkyMaximumLabels").Value.AsWholeNumber());
            Assert.AreEqual(24, defaults.Get("deepSkyMaximumLabelCharacters").Value.AsWholeNumber());
            Assert.IsTrue(defaults.Get("deepSkyLabelCatalogObjects").Value.AsToggle());
            Assert.IsTrue(defaults.Get("deepSkyOutlines").Value.AsToggle());
            Assert.AreEqual(1, defaults.Get("deepSkyLabelScale").Value.AsWholeNumber());
            Assert.AreEqual(2, defaults.Get("deepSkyLineThickness").Value.AsWholeNumber());
            Assert.AreEqual(7, defaults.Get("deepSkyMinimumGlyphRadius").Value.AsWholeNumber());
            Assert.AreEqual(new HVO.SkyMonitor.Imaging.PresentationColor(255, 196, 120), defaults.Get("deepSkyColor").Value.AsColor());
            Assert.AreEqual("MonoRegular", defaults.Get("deepSkyFont").Value.AsText());

            var edited = ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
            {
                ["deepSkyMaximumLabels"] = ProcessingOptionValue.FromWholeNumber(6),
                ["deepSkyMaximumLabelCharacters"] = ProcessingOptionValue.FromWholeNumber(12),
                ["deepSkyLabelCatalogObjects"] = ProcessingOptionValue.FromToggle(false),
                ["deepSkyMinimumGlyphRadius"] = ProcessingOptionValue.FromWholeNumber(11),
                ["deepSkyColor"] = ProcessingOptionValue.FromColor(new(100, 180, 240)),
                ["deepSkyFont"] = ProcessingOptionValue.FromFont("PlexRegular")
            });
            Assert.IsTrue(edited.IsValid, string.Join(' ', edited.Diagnostics));
            Assert.AreEqual("[1e+02]", edited.Options!.Value.GetProperty("future").GetRawText());
            Assert.AreEqual(6, edited.Options.Value.GetProperty("deepSkyMaximumLabels").GetInt32());
            Assert.AreEqual(12, edited.Options.Value.GetProperty("deepSkyMaximumLabelCharacters").GetInt32());
            Assert.IsFalse(edited.Options.Value.GetProperty("deepSkyLabelCatalogObjects").GetBoolean());
            Assert.AreEqual(11, edited.Options.Value.GetProperty("deepSkyMinimumGlyphRadius").GetInt32());
            Assert.AreEqual(100, edited.Options.Value.GetProperty("deepSkyRed").GetInt32());
            Assert.AreEqual("PlexRegular", edited.Options.Value.GetProperty("deepSkyFont").GetString());
        }
    }

    [TestMethod]
    [DataRow("deepSkyMaximumLabels", "-1", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("deepSkyMaximumLabels", "25", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("deepSkyMaximumLabelCharacters", "-1", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("deepSkyMaximumLabelCharacters", "65", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("deepSkyLabelCatalogObjects", "null", ProcessingOptionReasonCodes.WrongType)]
    [DataRow("deepSkyMinimumGlyphRadius", "1", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("deepSkyMinimumGlyphRadius", "33", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("deepSkyFont", "\"PlexBold\"", ProcessingOptionReasonCodes.NotAllowed)]
    public void DeepSkyPresentationValidationRejectsInvalidValues(string member, string token, string reason)
    {
        var diagnostic = ProcessingNodeOptionsCodec.Decode(Scene, Json($$"""{"{{member}}":{{token}}}""")).Diagnostics.Single();
        Assert.AreEqual(reason, diagnostic.Code);
        Assert.AreEqual($"effectiveOptions.{member}", diagnostic.Path);
    }

    [TestMethod]
    public void ProjectedSceneRejectsDeepSkyMinimumSizeGreaterThanMaximumSize()
    {
        var invalid = ProcessingNodeOptionsCodec.Decode(ProjectedSceneOptionDescriptors.Descriptor,
            Json("""{"deepSkyMinimumSizeArcminutes": 50, "deepSkyMaximumSizeArcminutes": 10}"""));
        Assert.IsFalse(invalid.IsValid);
        Assert.AreEqual(ProcessingOptionReasonCodes.OutOfRange, invalid.Diagnostics.Single().Code);
        Assert.AreEqual("effectiveOptions.deepSkyMinimumSizeArcminutes", invalid.Diagnostics.Single().Path);

        var equal = ProcessingNodeOptionsCodec.Decode(ProjectedSceneOptionDescriptors.Descriptor,
            Json("""{"deepSkyMinimumSizeArcminutes": 10, "deepSkyMaximumSizeArcminutes": 10}"""));
        Assert.IsTrue(equal.IsValid);
    }
}
