using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Services")]
public sealed class ProcessingNodeOptionsFormDefaultsTests
{
    [TestMethod]
    public void OverlayCodeDefaultsMatchRestoredScenePaletteAndResetPreservesUnknownFields()
    {
        var source = JsonDocument.Parse(
            """{ "constellationLineRed": 255, "constellationLineGreen": 0, "constellationLineBlue": 0, "constellationLineThickness": 6, "future": [1e+02] }""")
            .RootElement.Clone();
        var form = new ProcessingNodeOptionsForm(ProcessingNodeOptionsDescriptors.ScenePresentationLayer, source);

        Assert.AreEqual("#60a0ff", form.DefaultDisplay("constellationLineColor"));
        Assert.AreEqual("2 px", form.DefaultDisplay("constellationLineThickness"));
        Assert.AreEqual("On", form.DefaultDisplay("drawConstellationLines"));
        Assert.AreEqual("Default palette", form.DefaultDisplay("markerValue"));
        Assert.AreEqual("#ff0000", form.ColorPickerValue("constellationLineColor"));
        Assert.IsFalse(form.IsAtDefault("constellationLineColor"));
        Assert.IsFalse(form.IsAtDefault("constellationLineThickness"));
        Assert.IsTrue(form.HasNonDefaultValues);

        // Invalid hex text falls back safely for the HTML color picker while preserving raw text for correction.
        form.Set("constellationLineColor", "not-a-color");
        Assert.AreEqual("not-a-color", form.Get("constellationLineColor"));
        Assert.AreEqual("#60a0ff", form.ColorPickerValue("constellationLineColor"));
        Assert.IsFalse(form.TryBuild(out _, out var invalidErrors));
        Assert.IsNotEmpty(invalidErrors);

        form.ResetOptionToDefault("constellationLineColor");
        Assert.AreEqual("#60a0ff", form.Get("constellationLineColor"));
        Assert.IsTrue(form.IsAtDefault("constellationLineColor"));
        Assert.IsTrue(form.HasNonDefaultValues);

        form.ResetAllToDefaults();
        Assert.IsFalse(form.HasNonDefaultValues);
        Assert.IsTrue(form.TryBuild(out var reset, out var errors), string.Join(' ', errors));
        Assert.AreEqual(96, reset!.Value.GetProperty("constellationLineRed").GetInt32());
        Assert.AreEqual(160, reset.Value.GetProperty("constellationLineGreen").GetInt32());
        Assert.AreEqual(255, reset.Value.GetProperty("constellationLineBlue").GetInt32());
        Assert.AreEqual(2, reset.Value.GetProperty("constellationLineThickness").GetInt32());
        Assert.AreEqual("[1e+02]", reset.Value.GetProperty("future").GetRawText());
    }

    [TestMethod]
    public void ResetAllToDefaultsOnEmptySourceDoesNotMaterializeAbsentMembers()
    {
        const string sourceText = """{ "future": [1e+02] }""";
        var source = JsonDocument.Parse(sourceText).RootElement.Clone();
        var form = new ProcessingNodeOptionsForm(SceneStylePresentation.Descriptor, source);

        Assert.IsFalse(form.HasNonDefaultValues);
        form.Set("pointGlyph", "Crosshair");
        form.Set("labelFont", "PlexRegular");
        form.Set("markerColor", "#112233");
        Assert.IsTrue(form.HasNonDefaultValues);

        form.ResetAllToDefaults();
        Assert.IsFalse(form.HasNonDefaultValues);
        Assert.IsTrue(form.TryBuild(out var rebuilt, out var errors), string.Join(' ', errors));
        Assert.AreEqual(sourceText, rebuilt!.Value.GetRawText());
    }

    [TestMethod]
    public void DefaultDisplayAndResetCoverCollectionsNullableNumbersAndRowCollections()
    {
        var rolling = new ProcessingNodeOptionsForm(RollingCombinationOptionDescriptors.Descriptor, null);
        Assert.AreEqual("Unlimited", rolling.DefaultDisplay("maximumAgeMilliseconds"));
        Assert.AreEqual("5 frames", rolling.DefaultDisplay("windowSize"));
        Assert.AreEqual("Trailing", rolling.DefaultDisplay("windowKind"));
        rolling.Set("maximumAgeMilliseconds", "500");
        rolling.Set("windowKind", "Centered");
        Assert.IsTrue(rolling.HasNonDefaultValues);
        rolling.ResetAllToDefaults();
        Assert.IsFalse(rolling.HasNonDefaultValues);
        Assert.AreEqual(string.Empty, rolling.Get("maximumAgeMilliseconds"));
        Assert.AreEqual("Trailing", rolling.Get("windowKind"));

        var manifest = new ProcessingNodeOptionsForm(OverlayManifestOptionDescriptors.Descriptor, null);
        Assert.AreEqual("Using producer defaults.", manifest.DefaultDisplay("layers"));
        Assert.IsFalse(manifest.HasNonDefaultValues);
        manifest.AddRow("layers");
        manifest.Rows("layers")[0].Set("enabled", "False");
        manifest.RowsChanged("layers");
        Assert.IsTrue(manifest.HasNonDefaultValues);
        manifest.ResetAllToDefaults();
        Assert.IsFalse(manifest.HasNonDefaultValues);
        Assert.HasCount(1, manifest.Rows("layers"));
        Assert.AreEqual("True", manifest.Rows("layers")[0].Get("enabled"));
        manifest.ClearRows("layers");
        Assert.IsEmpty(manifest.Rows("layers"));

        var materializer = new ProcessingNodeOptionsForm(PresentationMaterializerOptionDescriptors.Descriptor, null);
        Assert.AreEqual("Using manifest defaults.", materializer.DefaultDisplay("enabledLayerKinds"));
        materializer.AddCollectionItem("enabledLayerKinds");
        Assert.IsTrue(materializer.HasNonDefaultValues);
        materializer.ResetAllToDefaults();
        Assert.IsFalse(materializer.HasNonDefaultValues);
        Assert.IsEmpty(materializer.Collection("enabledLayerKinds").Items);
    }

    [TestMethod]
    public void ResettingOverlayManifestPreservesUnknownFieldsInsideLayerOverrideRows()
    {
        const string sourceJson = """
            {
              "constellationOpacityMillionths": 500000,
              "topFuture": [1e+02],
              "layers": [
                { "kind": "scene-constellations", "enabled": false, "zOrder": 42, "future": [1e+02] },
                { "kind": "environment", "enabled": false, "zOrder": 15, "extra": { "keep": true } }
              ]
            }
            """;
        var source = JsonDocument.Parse(sourceJson).RootElement.Clone();
        var form = new ProcessingNodeOptionsForm(OverlayManifestOptionDescriptors.Descriptor, source);

        Assert.IsTrue(form.HasNonDefaultValues);
        Assert.IsFalse(form.IsAtDefault("layers"));

        form.ResetAllToDefaults();

        Assert.IsFalse(form.HasNonDefaultValues);
        Assert.IsTrue(form.IsAtDefault("layers"));
        Assert.IsTrue(form.TryBuild(out var rebuilt, out var errors), string.Join(' ', errors));

        var root = rebuilt!.Value;
        Assert.AreEqual(800_000, root.GetProperty("constellationOpacityMillionths").GetInt32());
        Assert.AreEqual("[1e+02]", root.GetProperty("topFuture").GetRawText());
        var layers = root.GetProperty("layers");
        Assert.AreEqual(2, layers.GetArrayLength());

        Assert.AreEqual("scene-constellations", layers[0].GetProperty("kind").GetString());
        Assert.IsTrue(layers[0].GetProperty("enabled").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, layers[0].GetProperty("zOrder").ValueKind);
        Assert.AreEqual("[1e+02]", layers[0].GetProperty("future").GetRawText());

        Assert.AreEqual("environment", layers[1].GetProperty("kind").GetString());
        Assert.IsTrue(layers[1].GetProperty("enabled").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, layers[1].GetProperty("zOrder").ValueKind);
        Assert.AreEqual("{ \"keep\": true }", layers[1].GetProperty("extra").GetRawText());

        // Row deletion remains an explicit action via RemoveRow / ClearRows.
        form.RemoveRow("layers", 1);
        Assert.IsTrue(form.TryBuild(out var afterRemove, out _));
        Assert.AreEqual(1, afterRemove!.Value.GetProperty("layers").GetArrayLength());
        Assert.AreEqual("[1e+02]", afterRemove.Value.GetProperty("layers")[0].GetProperty("future").GetRawText());

        form.ClearRows("layers");
        Assert.IsTrue(form.TryBuild(out var afterClear, out _));
        Assert.AreEqual(0, afterClear!.Value.GetProperty("layers").GetArrayLength());
    }

    [TestMethod]
    public void ResettingOverlayManifestWithQuotedNumericRowFieldsPreservesRowsAndUnknownMembers()
    {
        const string sourceJson = """
            {
              "layers": [
                { "kind": "scene-constellations", "enabled": false, "zOrder": "12", "future": 1e+02 },
                { "kind": "environment", "enabled": true, "zOrder": "34", "extra": { "keep": true } }
              ]
            }
            """;
        var source = JsonDocument.Parse(sourceJson).RootElement.Clone();
        var form = new ProcessingNodeOptionsForm(OverlayManifestOptionDescriptors.Descriptor, source);

        Assert.HasCount(2, form.Rows("layers"), "Quoted numeric row fields must not discard the retained row collection.");
        Assert.AreEqual("12", form.Rows("layers")[0].Get("zOrder"));
        Assert.AreEqual("34", form.Rows("layers")[1].Get("zOrder"));
        Assert.IsFalse(form.IsAtDefault("layers"));
        Assert.IsTrue(form.HasNonDefaultValues);

        form.ResetAllToDefaults();

        Assert.IsTrue(form.IsAtDefault("layers"));
        Assert.IsFalse(form.HasNonDefaultValues);
        Assert.IsTrue(form.TryBuild(out var rebuilt, out var errors), string.Join(' ', errors));

        var layers = rebuilt!.Value.GetProperty("layers");
        Assert.AreEqual(2, layers.GetArrayLength());
        Assert.AreEqual("scene-constellations", layers[0].GetProperty("kind").GetString());
        Assert.IsTrue(layers[0].GetProperty("enabled").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, layers[0].GetProperty("zOrder").ValueKind);
        Assert.AreEqual("1e+02", layers[0].GetProperty("future").GetRawText());
        Assert.AreEqual("environment", layers[1].GetProperty("kind").GetString());
        Assert.IsTrue(layers[1].GetProperty("enabled").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, layers[1].GetProperty("zOrder").ValueKind);
        Assert.AreEqual("{ \"keep\": true }", layers[1].GetProperty("extra").GetRawText());
    }

    [TestMethod]
    public void PredictedSatelliteTracksRowUsesProducerDefaultOffForIsAtDefaultAndReset()
    {
        // (a) Enabled satellite row with no unknown members: Reset turns it OFF (producer default false) and marks IsAtDefault true.
        var enabledSource = JsonDocument.Parse(
            """{ "layers": [ { "kind": "PredictedSatelliteTracks", "enabled": true, "zOrder": 5 } ] }""").RootElement.Clone();
        var enabledForm = new ProcessingNodeOptionsForm(OverlayManifestOptionDescriptors.Descriptor, enabledSource);
        Assert.AreEqual("Off", enabledForm.Rows("layers")[0].DefaultDisplay("enabled"));
        Assert.IsFalse(enabledForm.IsAtDefault("layers"));
        Assert.IsTrue(enabledForm.HasNonDefaultValues);

        enabledForm.ResetAllToDefaults();
        Assert.IsTrue(enabledForm.IsAtDefault("layers"));
        Assert.IsFalse(enabledForm.HasNonDefaultValues);
        Assert.IsTrue(enabledForm.TryBuild(out var enabledReset, out var enabledErrors), string.Join(' ', enabledErrors));
        var enabledRuntime = enabledReset!.Value.Deserialize<HVO.SkyMonitor.CameraAgent.Common.Capture.Processing.OverlayManifestProcessingStepOptions>(JsonSerializerOptions.Web)!;
        var enabledSelection = enabledRuntime.Layers.Single(static row => row.Kind == OrbitalTrackPresentation.LayerKind);
        Assert.IsFalse(enabledSelection.Enabled);
        Assert.IsNull(enabledSelection.ZOrder);

        // (a2) Already-disabled satellite row reports IsAtDefault true and stays OFF after Reset.
        var disabledSource = JsonDocument.Parse(
            """{ "layers": [ { "kind": "PredictedSatelliteTracks", "enabled": false } ] }""").RootElement.Clone();
        var disabledForm = new ProcessingNodeOptionsForm(OverlayManifestOptionDescriptors.Descriptor, disabledSource);
        Assert.IsTrue(disabledForm.IsAtDefault("layers"));
        Assert.IsFalse(disabledForm.HasNonDefaultValues);
        disabledForm.ResetAllToDefaults();
        Assert.IsTrue(disabledForm.IsAtDefault("layers"));
        Assert.IsTrue(disabledForm.TryBuild(out var disabledReset, out _));
        var disabledRuntime = disabledReset!.Value.Deserialize<HVO.SkyMonitor.CameraAgent.Common.Capture.Processing.OverlayManifestProcessingStepOptions>(JsonSerializerOptions.Web)!;
        Assert.IsFalse(disabledRuntime.Layers.Single().Enabled);

        // (a3) Satellite row with omitted enabled member (which binds to true at runtime) reports non-default and resets to enabled:false.
        var omittedSource = JsonDocument.Parse(
            """{ "layers": [ { "kind": "PredictedSatelliteTracks" } ] }""").RootElement.Clone();
        var omittedForm = new ProcessingNodeOptionsForm(OverlayManifestOptionDescriptors.Descriptor, omittedSource);
        Assert.IsFalse(omittedForm.IsAtDefault("layers"));
        omittedForm.ResetAllToDefaults();
        Assert.IsTrue(omittedForm.IsAtDefault("layers"));
        Assert.IsTrue(omittedForm.TryBuild(out var omittedReset, out _));
        var omittedRuntime = omittedReset!.Value.Deserialize<HVO.SkyMonitor.CameraAgent.Common.Capture.Processing.OverlayManifestProcessingStepOptions>(JsonSerializerOptions.Web)!;
        Assert.IsFalse(omittedRuntime.Layers.Single().Enabled);

        // (b) Satellite row with unknown members preserves unknown members while resetting enabled to false and zOrder to null.
        var unknownSource = JsonDocument.Parse(
            """{ "layers": [ { "kind": "PredictedSatelliteTracks", "enabled": true, "zOrder": 5, "future": [1e+02] } ] }""").RootElement.Clone();
        var unknownForm = new ProcessingNodeOptionsForm(OverlayManifestOptionDescriptors.Descriptor, unknownSource);
        Assert.IsFalse(unknownForm.IsAtDefault("layers"));
        Assert.IsTrue(unknownForm.HasNonDefaultValues);

        unknownForm.ResetAllToDefaults();
        Assert.IsTrue(unknownForm.IsAtDefault("layers"));
        Assert.IsFalse(unknownForm.HasNonDefaultValues);
        Assert.IsTrue(unknownForm.TryBuild(out var unknownReset, out var unknownErrors), string.Join(' ', unknownErrors));
        var rowJson = unknownReset!.Value.GetProperty("layers")[0];
        Assert.AreEqual("PredictedSatelliteTracks", rowJson.GetProperty("kind").GetString());
        Assert.IsFalse(rowJson.GetProperty("enabled").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, rowJson.GetProperty("zOrder").ValueKind);
        Assert.AreEqual("[1e+02]", rowJson.GetProperty("future").GetRawText());
        var unknownRuntime = unknownReset.Value.Deserialize<HVO.SkyMonitor.CameraAgent.Common.Capture.Processing.OverlayManifestProcessingStepOptions>(JsonSerializerOptions.Web)!;
        Assert.IsFalse(unknownRuntime.Layers.Single().Enabled);
        Assert.IsNull(unknownRuntime.Layers.Single().ZOrder);

        // (c) Enabled satellite row without zOrder still reports IsAtDefault false so Reset stays enabled.
        var enabledOnlySource = JsonDocument.Parse(
            """{ "layers": [ { "kind": "PredictedSatelliteTracks", "enabled": true } ] }""").RootElement.Clone();
        var enabledOnlyForm = new ProcessingNodeOptionsForm(OverlayManifestOptionDescriptors.Descriptor, enabledOnlySource);
        Assert.IsFalse(enabledOnlyForm.IsAtDefault("layers"));
        Assert.IsTrue(enabledOnlyForm.HasNonDefaultValues);
    }

    [TestMethod]
    public void InvalidSourceValueDisplayingAsDefaultKeepsResetEnabledUntilRepaired()
    {
        // Quoted numeric value equal to default (800000) displays as "800000"; _invalidSourceKeys must still keep Reset enabled.
        var quotedSource = JsonDocument.Parse(
            """{ "constellationOpacityMillionths": "800000", "future": [1e+02] }""").RootElement.Clone();
        var manifestForm = new ProcessingNodeOptionsForm(OverlayManifestOptionDescriptors.Descriptor, quotedSource);
        Assert.AreEqual("800000", manifestForm.Get("constellationOpacityMillionths"));
        Assert.IsFalse(manifestForm.IsAtDefault("constellationOpacityMillionths"));
        Assert.IsTrue(manifestForm.HasNonDefaultValues);

        manifestForm.ResetAllToDefaults();
        Assert.IsTrue(manifestForm.IsAtDefault("constellationOpacityMillionths"));
        Assert.IsFalse(manifestForm.HasNonDefaultValues);
        Assert.IsTrue(manifestForm.TryBuild(out var manifestRepaired, out var manifestErrors), string.Join(' ', manifestErrors));
        Assert.AreEqual(JsonValueKind.Number, manifestRepaired!.Value.GetProperty("constellationOpacityMillionths").ValueKind);
        Assert.AreEqual(800_000, manifestRepaired.Value.GetProperty("constellationOpacityMillionths").GetInt32());
        Assert.AreEqual("[1e+02]", manifestRepaired.Value.GetProperty("future").GetRawText());

        // Legacy deepSkyFont ("PlexRegular") displays as default ("MonoRegular"); _invalidSourceKeys keeps Reset enabled and notice visible.
        foreach (var descriptor in new[] { ProcessingNodeOptionsDescriptors.ScenePresentationLayer, SceneStylePresentation.Descriptor })
        {
            var legacySource = JsonDocument.Parse(
                """{ "includeDeepSkyLayer": false, "deepSkyFont": "PlexRegular", "future": [1e+02] }""").RootElement.Clone();
            var sceneForm = new ProcessingNodeOptionsForm(descriptor, legacySource);
            Assert.AreEqual("MonoRegular", sceneForm.Get("deepSkyFont"));
            Assert.IsFalse(sceneForm.IsAtDefault("deepSkyFont"), descriptor.DescriptorId);
            Assert.IsTrue(sceneForm.HasNonDefaultValues, descriptor.DescriptorId);
            StringAssert.Contains(sceneForm.InvalidSourceNotice("deepSkyFont")!, "PlexRegular", StringComparison.Ordinal);

            sceneForm.ResetAllToDefaults();
            Assert.IsTrue(sceneForm.IsAtDefault("deepSkyFont"), descriptor.DescriptorId);
            Assert.IsFalse(sceneForm.HasNonDefaultValues, descriptor.DescriptorId);
            Assert.IsNull(sceneForm.InvalidSourceNotice("deepSkyFont"), descriptor.DescriptorId);
            Assert.IsTrue(sceneForm.TryBuild(out var sceneRepaired, out var sceneErrors), string.Join(' ', sceneErrors));
            Assert.AreEqual("MonoRegular", sceneRepaired!.Value.GetProperty("deepSkyFont").GetString());
            Assert.AreEqual("[1e+02]", sceneRepaired.Value.GetProperty("future").GetRawText());
        }
    }
}
