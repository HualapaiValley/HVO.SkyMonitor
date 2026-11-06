using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Components;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class FrameFactsDraftConversionTests
{
    [TestMethod]
    public void ConversionIsAtomicAndPreservesGraphShapeUnknownJsonDefaultsAndVariants()
    {
        var source = FrameFactsOptionsEditorTests.LegacyPipeline();
        var json = JsonSerializer.Serialize(source);
        var converted = FrameFactsDraftConversion.Convert(source);
        Assert.AreEqual(json, JsonSerializer.Serialize(source));
        for (var index = 0; index < source.Steps.Count; index++)
        {
            var before = source.Steps[index];
            var after = converted.Steps[index];
            Assert.AreEqual(before, after with { Type = before.Type, Options = before.Options });
        }
        var options = converted.Steps[0].Options!.Value;
        Assert.AreEqual("{\"keep\":[1, 2]}", options.GetProperty("future").GetRawText());
        Assert.AreEqual("presentation-metadata-facts-v2", options.GetProperty("factsOutputVariant").GetString());
        Assert.AreEqual("environment-layer-v1", options.GetProperty("outputVariant").GetString());
        Assert.HasCount(6, options.GetProperty("environmentalKinds").EnumerateArray().ToArray());
        Assert.AreEqual(4, options.GetProperty("inset").GetInt32());
        Assert.IsFalse(options.TryGetProperty("topLeftFont", out _), "Default font is not written.");
        Assert.IsTrue(converted.Steps[1].Options!.Value.GetProperty("useRichFrameFacts").GetBoolean());
        Assert.IsTrue(converted.Steps[2].Options!.Value.GetProperty("useRichFrameFacts").GetBoolean());
        var model = ProcessingGraphDraftModel.FromPipeline(source, "retained", "facts", "2");
        Assert.IsTrue(model.WithConvertedPipeline(converted).HasGraphChanges(converted));
    }

    [TestMethod]
    public void CustomVariantsColoursAndExplicitNullLegacyValueAreSupported()
    {
        var source = FrameFactsOptionsEditorTests.LegacyPipeline();
        foreach (var colour in new[] { "64", "null" })
        {
            var legacy = source.Steps[0] with { Options = JsonDocument.Parse("{\"widthPixels\":1936,\"heightPixels\":1216,\"value\":" + colour + ",\"factsOutputVariant\":\"my-facts\",\"outputVariant\":\"my-layer\",\"environmentalKinds\":[]}").RootElement.Clone() };
            var converted = FrameFactsDraftConversion.Convert(source with { Steps = [legacy] });
            var options = converted.Steps[0].Options!.Value;
            Assert.AreEqual("my-facts", options.GetProperty("factsOutputVariant").GetString());
            Assert.AreEqual("my-layer", options.GetProperty("outputVariant").GetString());
            Assert.AreEqual(colour == "64" ? "#404040" : "#ffffff", new ProcessingNodeOptionsForm(FrameFactsPresentationOptionDescriptors.Descriptor, options).Get("topLeftColor"));
        }
    }

    [TestMethod]
    public void OneUnconvertibleNodeRejectsTheWholeDraftAndLeavesSourceUnchanged()
    {
        var source = FrameFactsOptionsEditorTests.LegacyPipeline();
        var malformed = source.Steps[0] with { Id = "other", Options = JsonDocument.Parse("""{"environmentalKinds":["AirTemperature","AirTemperature"]}""").RootElement.Clone() };
        source = source with { Steps = [source.Steps[0], malformed] };
        var before = JsonSerializer.Serialize(source);
        Assert.ThrowsExactly<ArgumentException>(() => FrameFactsDraftConversion.Convert(source));
        Assert.AreEqual(before, JsonSerializer.Serialize(source));
    }

    [TestMethod]
    public void RichDraftOpeningAndRestoringAllDefaultsCreatesNoRevision()
    {
        var source = new CapturePipelineConfig([new CaptureProcessingStepConfig(FrameFactsPresentation.StepAlias, "facts", 70,
            JsonDocument.Parse("""{ "widthPixels":1936, "heightPixels":1216, "future": [1, 2] }""").RootElement.Clone(), ["$raw"]) ]);
        var model = ProcessingGraphDraftModel.FromPipeline(source, "retained", "facts", "2");
        var form = model.Nodes.Single().OptionsForm!;
        form.Set("topLeftTemplate", "changed");
        form.Set("topLeftTemplate", string.Empty);
        form.Set("topRightFont", "MonoRegular");
        form.Set("bottomLeftColor", "#FFFFFF");
        Assert.IsTrue(model.TryBuild(out var result, out _));
        Assert.IsFalse(model.HasGraphChanges(result));
        Assert.AreEqual(source.Steps[0].Options!.Value.GetRawText(), result.Steps[0].Options!.Value.GetRawText());
    }
}
