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
        Assert.HasCount(version == SceneVersion ? 30 : 17, decoded.Options);
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
            Assert.IsFalse(defaults.Get("applyInlineDeepSkyStyle").Value.AsToggle());
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
                ["applyInlineDeepSkyStyle"] = ProcessingOptionValue.FromToggle(true),
                ["deepSkyMaximumLabels"] = ProcessingOptionValue.FromWholeNumber(6),
                ["deepSkyMaximumLabelCharacters"] = ProcessingOptionValue.FromWholeNumber(12),
                ["deepSkyLabelCatalogObjects"] = ProcessingOptionValue.FromToggle(false),
                ["deepSkyMinimumGlyphRadius"] = ProcessingOptionValue.FromWholeNumber(11),
                ["deepSkyColor"] = ProcessingOptionValue.FromColor(new(100, 180, 240)),
                ["deepSkyFont"] = ProcessingOptionValue.FromFont("MonoBold")
            });
            Assert.IsTrue(edited.IsValid, string.Join(' ', edited.Diagnostics));
            Assert.AreEqual("[1e+02]", edited.Options!.Value.GetProperty("future").GetRawText());
            Assert.IsTrue(edited.Options.Value.GetProperty("applyInlineDeepSkyStyle").GetBoolean());
            Assert.AreEqual(6, edited.Options.Value.GetProperty("deepSkyMaximumLabels").GetInt32());
            Assert.AreEqual(12, edited.Options.Value.GetProperty("deepSkyMaximumLabelCharacters").GetInt32());
            Assert.IsFalse(edited.Options.Value.GetProperty("deepSkyLabelCatalogObjects").GetBoolean());
            Assert.AreEqual(11, edited.Options.Value.GetProperty("deepSkyMinimumGlyphRadius").GetInt32());
            Assert.AreEqual(100, edited.Options.Value.GetProperty("deepSkyRed").GetInt32());
            Assert.AreEqual("MonoBold", edited.Options.Value.GetProperty("deepSkyFont").GetString());
        }
    }

    [TestMethod]
    [DataRow("applyInlineDeepSkyStyle", "null", ProcessingOptionReasonCodes.WrongType)]
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
    public void DeepSkyFontPreservesInactiveLegacyPlexRegularAndRejectsActiveOrEditedPlexRegular()
    {
        foreach (var descriptor in new[] { Scene, SceneStylePresentation.Descriptor })
        {
            // Legacy saved graphs with inactive deep-sky styling preserve PlexRegular without failing decode or unrelated edits.
            var inactive = Json("""{"includeDeepSkyLayer":false,"deepSkyFont":"PlexRegular","future":[1e+02]}""");
            Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(descriptor, inactive).IsValid, descriptor.DescriptorId);
            Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(descriptor, Json("""{"deepSkyFont":"PlexRegular"}""")).IsValid, descriptor.DescriptorId);

            var unrelatedEdit = ProcessingNodeOptionsCodec.Encode(descriptor, inactive, new Dictionary<string, ProcessingOptionValue>
            {
                ["constellationLineThickness"] = ProcessingOptionValue.FromWholeNumber(4)
            });
            Assert.IsTrue(unrelatedEdit.IsValid, string.Join(' ', unrelatedEdit.Diagnostics));
            Assert.AreEqual("PlexRegular", unrelatedEdit.Options!.Value.GetProperty("deepSkyFont").GetString());
            Assert.AreEqual("[1e+02]", unrelatedEdit.Options.Value.GetProperty("future").GetRawText());

            // Active deep-sky proposals and explicit edits selecting PlexRegular are rejected.
            foreach (var activeJson in new[]
            {
                """{"includeDeepSkyLayer":true,"deepSkyFont":"PlexRegular"}""",
                """{"applyInlineDeepSkyStyle":true,"deepSkyFont":"PlexRegular"}"""
            })
            {
                var activeDiag = ProcessingNodeOptionsCodec.Decode(descriptor, Json(activeJson)).Diagnostics.Single();
                Assert.AreEqual(ProcessingOptionReasonCodes.NotAllowed, activeDiag.Code);
                Assert.AreEqual("effectiveOptions.deepSkyFont", activeDiag.Path);

                // Retained-capture compatibility mode still allows compiling a pre-upgrade saved graph.
                Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(descriptor, Json(activeJson),
                    ProcessingNodeOptionsCodec.DefaultPath, allowQuotedNumbers: false, allowRetainedLegacyDeepSkyFont: true).IsValid);
            }

            var editedFont = ProcessingNodeOptionsCodec.Encode(descriptor, Json("{}"), new Dictionary<string, ProcessingOptionValue>
            {
                ["deepSkyFont"] = ProcessingOptionValue.FromFont("PlexRegular")
            });
            Assert.IsFalse(editedFont.IsValid);
            Assert.AreEqual(ProcessingOptionReasonCodes.NotAllowed, editedFont.Diagnostics.Single().Code);
        }

        // Retained quoted numeric tolerance accepts runtime-valid quoted zOrder spellings (including +12, 012, -0)
        // while keeping strict mode unchanged and preserving range and type checks.
        foreach (var (quotedToken, expectedValue) in new[] { ("12", 12L), ("+12", 12L), ("012", 12L), ("-0", 0L) })
        {
            var quotedManifest = Json($$"""{"layers":[{"kind":"scene-constellations","enabled":false,"zOrder":"{{quotedToken}}","future":1e+02}]}""");
            Assert.IsFalse(ProcessingNodeOptionsCodec.Decode(OverlayManifestOptionDescriptors.Descriptor, quotedManifest).IsValid, quotedToken);
            Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(OverlayManifestOptionDescriptors.Descriptor, quotedManifest,
                ProcessingNodeOptionsCodec.DefaultPath, allowQuotedNumbers: true).IsValid, quotedToken);
            var rowDecoded = ProcessingNodeOptionsCodec.Decode(
                OverlayManifestOptionDescriptors.Layer,
                Json($$"""{"kind":"scene-constellations","zOrder":"{{quotedToken}}"}"""),
                ProcessingNodeOptionsCodec.DefaultPath,
                allowQuotedNumbers: true);
            Assert.IsTrue(rowDecoded.IsValid, quotedToken);
            Assert.AreEqual(expectedValue, rowDecoded.Get("zOrder").Value.AsNullableWholeNumber(), quotedToken);
        }
        Assert.AreEqual(ProcessingOptionReasonCodes.OutOfRange, ProcessingNodeOptionsCodec.Decode(OverlayManifestOptionDescriptors.Descriptor,
            Json("""{"layers":[{"kind":"scene-constellations","zOrder":"9999"}]}"""),
            ProcessingNodeOptionsCodec.DefaultPath, allowQuotedNumbers: true).Diagnostics.Single().Code);
        foreach (var badToken in new[] { "abc", "+", "-", "++12", "+ 12" })
        {
            Assert.AreEqual(ProcessingOptionReasonCodes.WrongType, ProcessingNodeOptionsCodec.Decode(OverlayManifestOptionDescriptors.Descriptor,
                Json($$"""{"layers":[{"kind":"scene-constellations","zOrder":"{{badToken}}"}]}"""),
                ProcessingNodeOptionsCodec.DefaultPath, allowQuotedNumbers: true).Diagnostics.Single().Code, badToken);
        }

        // Retained quoted decimal tolerance accepts leading-'+' spellings accepted by .NET AllowReadingFromString
        // while keeping strict mode, whitespace/sign guards, and range validation enforced.
        foreach (var (quotedToken, expectedValue) in new[] { ("+2.5", 2.5), ("+.5", 0.5), ("+1e2", 100.0), ("+6", 6.0), ("+0", 0.0) })
        {
            var projectedJson = Json($$"""{"deepSkyMaximumSizeArcminutes":"{{quotedToken}}"}""");
            Assert.AreEqual(ProcessingOptionReasonCodes.WrongType,
                ProcessingNodeOptionsCodec.Decode(ProjectedSceneOptionDescriptors.Descriptor, projectedJson).Diagnostics.Single().Code,
                quotedToken);
            var retainedProjected = ProcessingNodeOptionsCodec.Decode(
                ProjectedSceneOptionDescriptors.Descriptor,
                projectedJson,
                ProcessingNodeOptionsCodec.DefaultPath,
                allowQuotedNumbers: true);
            Assert.IsTrue(retainedProjected.IsValid, $"{quotedToken}: {string.Join(' ', retainedProjected.Diagnostics)}");
            Assert.AreEqual(expectedValue, retainedProjected.Get("deepSkyMaximumSizeArcminutes").Value.AsRealNumber(), quotedToken);

            var sceneJson = Json($$"""{"maximumLabelMagnitude":"{{quotedToken}}"}""");
            Assert.AreEqual(ProcessingOptionReasonCodes.WrongType,
                ProcessingNodeOptionsCodec.Decode(Scene, sceneJson).Diagnostics.Single().Code,
                quotedToken);
            var retainedScene = ProcessingNodeOptionsCodec.Decode(
                Scene,
                sceneJson,
                ProcessingNodeOptionsCodec.DefaultPath,
                allowQuotedNumbers: true);
            if (expectedValue <= 30.0)
            {
                Assert.IsTrue(retainedScene.IsValid, $"{quotedToken}: {string.Join(' ', retainedScene.Diagnostics)}");
                Assert.AreEqual(expectedValue, retainedScene.Get("maximumLabelMagnitude").Value.AsRealNumber(), quotedToken);
            }
            else
            {
                Assert.AreEqual(ProcessingOptionReasonCodes.OutOfRange, retainedScene.Diagnostics.Single().Code, quotedToken);
            }

            if (expectedValue > 0.0)
            {
                var rollingJson = Json($$"""{"maximumIntegrationMilliseconds":"{{quotedToken}}"}""");
                Assert.AreEqual(ProcessingOptionReasonCodes.WrongType,
                    ProcessingNodeOptionsCodec.Decode(RollingCombinationOptionDescriptors.Descriptor, rollingJson).Diagnostics.Single().Code,
                    quotedToken);
                var retainedRolling = ProcessingNodeOptionsCodec.Decode(
                    RollingCombinationOptionDescriptors.Descriptor,
                    rollingJson,
                    ProcessingNodeOptionsCodec.DefaultPath,
                    allowQuotedNumbers: true);
                Assert.IsTrue(retainedRolling.IsValid, $"{quotedToken}: {string.Join(' ', retainedRolling.Diagnostics)}");
                Assert.AreEqual(expectedValue, retainedRolling.Get("maximumIntegrationMilliseconds").Value.AsNullableRealNumber(), quotedToken);
            }
        }
        foreach (var badRealToken in new[] { "++1", "+ 1", "+", " +1.5", "+1.5 " })
        {
            Assert.AreEqual(ProcessingOptionReasonCodes.WrongType, ProcessingNodeOptionsCodec.Decode(Scene,
                Json($$"""{"maximumLabelMagnitude":"{{badRealToken}}"}"""),
                ProcessingNodeOptionsCodec.DefaultPath, allowQuotedNumbers: true).Diagnostics.Single().Code, badRealToken);
        }
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
