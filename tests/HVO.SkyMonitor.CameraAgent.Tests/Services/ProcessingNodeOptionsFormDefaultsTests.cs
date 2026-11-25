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
}
