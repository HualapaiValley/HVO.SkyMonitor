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
        Assert.IsTrue(manifest.HasNonDefaultValues);
        manifest.ResetAllToDefaults();
        Assert.IsFalse(manifest.HasNonDefaultValues);
        Assert.IsEmpty(manifest.Rows("layers"));

        var materializer = new ProcessingNodeOptionsForm(PresentationMaterializerOptionDescriptors.Descriptor, null);
        Assert.AreEqual("Using manifest defaults.", materializer.DefaultDisplay("enabledLayerKinds"));
        materializer.AddCollectionItem("enabledLayerKinds");
        Assert.IsTrue(materializer.HasNonDefaultValues);
        materializer.ResetAllToDefaults();
        Assert.IsFalse(materializer.HasNonDefaultValues);
        Assert.IsEmpty(materializer.Collection("enabledLayerKinds").Items);
    }
}
