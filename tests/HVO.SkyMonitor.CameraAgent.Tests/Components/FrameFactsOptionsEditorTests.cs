using System.Text.Json;
using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class FrameFactsOptionsEditorTests
{
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);
    [TestMethod]
    public void AliasUsesItsActualDescriptorAndTypeChangesDiscardTheOldForm()
    {
        Assert.AreEqual(FrameFactsPresentation.ProducerVersion, ProcessingNodeOptionsForm.DescriptorFor("framefactspresentationlayerv1")!.StepVersion);
        var node = new ProcessingGraphDraftModel.NodeRow { Type = "ScenePresentationLayer" };
        var previous = node.OptionsForm;
        node.Type = FrameFactsPresentation.StepAlias;
        Assert.AreNotSame(previous, node.OptionsForm);
        Assert.AreEqual(FrameFactsPresentation.ProducerVersion, node.OptionsForm!.Descriptor.StepVersion);
    }

    [TestMethod]
    [DataRow("topLeft")]
    [DataRow("topRight")]
    [DataRow("bottomLeft")]
    [DataRow("bottomRight")]
    public void EachCornerEditsTemplateFontColourSizeAndMissingPolicy(string prefix)
    {
        using var context = Context();
        var form = Form();
        var cut = context.Render<FrameFactsOptionsEditor>(parameters => parameters.Add(value => value.Form, form).Add(value => value.IdPrefix, "facts"));
        cut.Find("#facts-" + prefix + "Template").Change("__*{{capture.sequence}}*__");
        cut.Find("#facts-" + prefix + "Font").Change("MonoBold");
        cut.Find("#facts-" + prefix + "SizeMilliPixels").Change("18000");
        cut.Find("#facts-" + prefix + "Color").Change("#12ab34");
        cut.Find("#facts-" + prefix + "MissingPolicy").Change("DropLine");
        Assert.IsTrue(form.TryBuild(out var options, out _));
        Assert.AreEqual("__*{{capture.sequence}}*__", options!.Value.GetProperty(prefix + "Template").GetString());
        Assert.AreEqual("MonoBold", options.Value.GetProperty(prefix + "Font").GetString());
        Assert.AreEqual(18, options.Value.GetProperty(prefix + "Red").GetInt32());
        Assert.AreEqual(171, options.Value.GetProperty(prefix + "Green").GetInt32());
        Assert.AreEqual(52, options.Value.GetProperty(prefix + "Blue").GetInt32());
        Assert.AreEqual(18000, options.Value.GetProperty(prefix + "SizeMilliPixels").GetInt32());
        Assert.AreEqual("DropLine", options.Value.GetProperty(prefix + "MissingPolicy").GetString());
        Assert.HasCount(4, cut.FindAll("textarea"));
        StringAssert.Contains(cut.Find(".token-catalogue").TextContent, "time.captureLocal", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".token-catalogue").TextContent, "fixed3", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("topLeftTemplate", "{{spaceWeather.kp}}", "frame-facts.reserved-namespace")]
    [DataRow("topRightTemplate", "{{capture.sequence|fixed2}}", "frame-facts.unknown-format")]
    [DataRow("bottomLeftTemplate", "**unclosed", "frame-facts.unclosed-style")]
    [DataRow("bottomRightTemplate", "<script>", "frame-facts.unsupported-syntax")]
    [DataRow("topLeftSizeMilliPixels", "3999", "between")]
    [DataRow("topRightFont", "SystemFont", "allowed")]
    public void ErrorsPreventPreviewAndClearAFormerPreview(string key, string value, string expected)
    {
        using var context = Context();
        var form = Form();
        var cut = context.Render<FrameFactsOptionsEditor>(parameters => parameters.Add(editor => editor.Form, form).Add(editor => editor.IdPrefix, "facts"));
        cut.Find("button").Click();
        Assert.IsNotNull(cut.Find(".preview-image"));
        cut.Find("#facts-" + key).Change(value);
        Assert.IsEmpty(cut.FindAll(".preview-image"));
        Assert.IsFalse(form.TryBuild(out _, out var diagnostics));
        Assert.IsTrue(diagnostics.Any(error => error.Message.Contains(expected, StringComparison.OrdinalIgnoreCase) || error.Code.Contains(expected, StringComparison.OrdinalIgnoreCase)),
            string.Join(' ', diagnostics.Select(error => error.Message)));
    }

    [TestMethod]
    public void GrammarAndExpansionBoundsAndUnsupportedBoldPlexAreVisible()
    {
        var form = Form();
        foreach (var invalid in new[] { new string('x', 65), new string('x', 4097), string.Join('\n', Enumerable.Repeat("line", 9)) })
        {
            form.Set("topLeftTemplate", invalid);
            Assert.IsFalse(form.TryBuild(out _, out _));
        }
        form.Set("topLeftTemplate", "**bold**");
        form.Set("topLeftFont", "PlexRegular");
        Assert.IsFalse(form.TryBuild(out _, out var errors));
        StringAssert.Contains(errors.Single().Message, "bold face", StringComparison.Ordinal);
        form.Set("topLeftFont", "MonoRegular");
        form.Set("topLeftTemplate", string.Join(' ', Enumerable.Repeat("{{capture.sequence.status}}", 10)));
        var exception = Assert.ThrowsExactly<ArgumentException>(() => FrameFactsEditorPreview.Render(form, FrameFactsEditorPreview.MissingSnapshot()));
        StringAssert.Contains(exception.Message, "frame-facts.expansion-too-long", StringComparison.Ordinal);
    }

    [TestMethod]
    public void FrozenPreviewShowsMissingAndStatusDropLineAndNeverFabricatesValues()
    {
        using var context = Context();
        var form = Form();
        form.Set("topLeftTemplate", "{{acquisition.gain}}\n{{acquisition.gain.status}}");
        form.Set("topRightTemplate", "{{acquisition.gain}}");
        form.Set("topRightMissingPolicy", "DropLine");
        form.Set("bottomLeftTemplate", "{{acquisition.gain}} {{acquisition.gain.status}}");
        form.Set("bottomLeftMissingPolicy", "DropLine");
        var cut = context.Render<FrameFactsOptionsEditor>(parameters => parameters.Add(editor => editor.Form, form));
        cut.Find("button").Click();
        StringAssert.Contains(cut.Find(".frozen-preview").TextContent, "every scalar is MISSING", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find(".preview-text").TextContent, "UNAVAILABLE", StringComparison.Ordinal);
        var preview = FrameFactsEditorPreview.Render(form, FrameFactsEditorPreview.MissingSnapshot());
        Assert.IsEmpty(preview.Payload.RichText!.Corners[1].Lines);
        Assert.AreEqual("UNAVAILABLE MISSING", string.Concat(preview.Payload.RichText.Corners[2].Lines[0].Runs.Select(run => run.Text)));
        StringAssert.StartsWith(preview.Image, "data:image/jpeg;base64,", StringComparison.Ordinal);
    }

    [TestMethod]
    public void FrozenInputIsBoundedIdentityCheckedAndIndependentOfGraphEdits()
    {
        var snapshot = Snapshot();
        var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, SnapshotJson);
        var loaded = FrameFactsEditorPreview.ReadSnapshot(json);
        var form = Form();
        form.Set("topLeftTemplate", "{{capture.sequence}}");
        var before = FrameFactsEditorPreview.Render(form, loaded);
        form.Set("topLeftColor", "#ff0000");
        var after = FrameFactsEditorPreview.Render(form, loaded);
        Assert.AreEqual(snapshot.IdentitySha256, after.Payload.RichText!.ContextIdentitySha256);
        Assert.AreNotEqual(before.Image, after.Image);
        CollectionAssert.AreEqual(json, JsonSerializer.SerializeToUtf8Bytes(loaded, SnapshotJson));
        Assert.ThrowsExactly<ArgumentException>(() => FrameFactsEditorPreview.ReadSnapshot(new byte[FrameFactsEditorPreview.MaximumInputBytes + 1]));
        var corrupt = snapshot with { IdentitySha256 = new string('0', 64) };
        Assert.ThrowsExactly<ArgumentException>(() => FrameFactsEditorPreview.ReadSnapshot(JsonSerializer.SerializeToUtf8Bytes(corrupt, SnapshotJson)));
    }

    [TestMethod]
    public void EveryCatalogueTokenAndClosedFormatWorksInEveryCorner()
    {
        var context = Snapshot().ToContext();
        foreach (var entry in FrameFactsRetainedCatalog.Entries)
        {
            foreach (var format in entry.Definition.Formats)
            {
                var source = "{{" + entry.Definition.Key + "|" + format + "}}";
                foreach (var prefix in new[] { "topLeft", "topRight", "bottomLeft", "bottomRight" })
                {
                    var form = Form();
                    form.Set(prefix + "Template", source);
                    Assert.IsTrue(form.TryBuild(out _, out var errors), string.Join(' ', errors.Select(error => error.Message)));
                    Assert.IsTrue(FrameFactsEvaluator.Evaluate(FrameFactsTemplate.Parse(source, FrameFactsRetainedCatalog.Catalog).Template!, context).IsValid, source);
                }
            }
        }
    }

    [TestMethod]
    public void InvalidSourceCanBeRepairedWithoutLosingOtherDisplayedFields()
    {
        var form = new ProcessingNodeOptionsForm(FrameFactsPresentationOptionDescriptors.Descriptor,
            JsonSerializer.SerializeToElement(new { topLeftTemplate = "**bad", topRightTemplate = "preserved", bottomRightRed = 17 }));
        Assert.AreEqual("preserved", form.Get("topRightTemplate"));
        Assert.AreEqual("#11ffff", form.Get("bottomRightColor"));
        form.Set("topLeftTemplate", "fixed");
        Assert.IsTrue(form.TryBuild(out _, out _));
    }

    [TestMethod]
    public void ExplicitConversionReviewCancelApplySaveAndUnchangedDraftUseExistingFlow()
    {
        using var context = Context();
        var service = Configure(context, LegacyPipeline());
        var source = JsonSerializer.Serialize(service.RevisionDetail!.Pipeline);
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find(".editor-actions .btn-primary").HasAttribute("disabled"));
        cut.Find("#frame-facts button").Click();
        Assert.IsNotNull(cut.Find(".conversion-columns"));
        Assert.IsEmpty(service.Created);
        Assert.AreEqual(source, JsonSerializer.Serialize(service.RevisionDetail.Pipeline));
        cut.Find(".conversion-review .btn-outline-light").Click();
        Assert.IsEmpty(cut.FindAll(".conversion-review"));
        cut.Find("#frame-facts button").Click();
        cut.Find(".conversion-review .btn-primary").Click();
        Assert.HasCount(4, cut.FindAll("textarea"));
        Assert.IsTrue(cut.Find(".editor-actions .btn-primary").HasAttribute("disabled"));
        cut.Find("#node-options-0-topLeftTemplate").Change("__{{capture.sequence}}__");
        cut.Find("form").Submit();
        cut.Find(".editor-actions .btn-primary").Click();
        var saved = service.Created.Single().Pipeline;
        Assert.AreEqual(FrameFactsPresentation.StepAlias, saved.Steps[0].Type);
        Assert.AreEqual("{\"keep\":[1, 2]}", saved.Steps[0].Options!.Value.GetProperty("future").GetRawText());
        Assert.AreEqual(source, JsonSerializer.Serialize(service.RevisionDetail.Pipeline));
        Assert.IsEmpty(service.Commands);
    }

    [TestMethod]
    public void RollbackEligibilityMatchesTheDurableStoreForConfiguredAndPreviouslyActivatedRevisions()
    {
        using var context = Context();
        var service = Configure(context, LegacyPipeline());
        var registry = service.Registry!;
        var prior = registry.Revisions.Single(revision => revision.RevisionId == "rev-retired") with
        {
            RevisionId = "rev-prior",
            Lifecycle = HVO.SkyMonitor.CameraAgent.Common.Capture.Processing.ProcessingGraphRevisionLifecycle.Validated,
            RetiredUtc = null
        };
        service.Registry = registry with { ConfiguredBasicRevisionId = "rev-validated", Revisions = [.. registry.Revisions, prior] };
        var cut = context.Render<ProcessingGraphsPage>();
        Assert.IsNotNull(cut.Find("#graph-ROLLBACK-rev-prior"));
        Assert.IsNotNull(cut.Find("#graph-ROLLBACK-rev-validated"));
        Assert.IsEmpty(cut.FindAll("#graph-ROLLBACK-rev-retired"));
        Assert.IsEmpty(cut.FindAll("#graph-RETIRE-rev-validated"));
        cut.Find("#graph-ROLLBACK-rev-prior").Click();
        cut.Find("dialog .btn-primary").Click();
        Assert.AreEqual("rollback", service.Commands.Single().Action);
        Assert.AreEqual(7L, service.Commands.Single().ExpectedVersion);
    }

    [TestMethod]
    public void MixedLegacyAndRichDraftHasOneDeepLinkTargetAndKeepsBothPanels()
    {
        using var context = Context();
        var legacy = LegacyPipeline();
        Configure(context, legacy with
        {
            Steps = [new(FrameFactsPresentation.StepAlias, "rich", 1,
                JsonSerializer.SerializeToElement(new { widthPixels = 1936, heightPixels = 1216 }), ["$raw"]), .. legacy.Steps]
        });
        var cut = context.Render<ProcessingGraphEditorPage>();
        Assert.HasCount(1, cut.FindAll("#frame-facts"));
        Assert.IsNotNull(cut.Find("#frame-facts-conversion button"));
        Assert.HasCount(4, cut.FindAll("#frame-facts textarea"));
    }

    [TestMethod]
    public void DeepLinkLoadsActiveRevision()
    {
        using var context = Context();
        var service = Configure(context, LegacyPipeline());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?section=frame-facts#frame-facts");
        var cut = context.Render<ProcessingGraphEditorPage>();
        Assert.IsNotNull(cut.Find("#frame-facts"));
        Assert.IsNotNull(service.RevisionDetail);
    }

    internal static ProcessingNodeOptionsForm Form() => new(FrameFactsPresentationOptionDescriptors.Descriptor,
        JsonSerializer.SerializeToElement(new { widthPixels = 1936, heightPixels = 1216 }));

    internal static FrameFactsSnapshotV1 Snapshot()
    {
        var empty = FrameFactsEditorPreview.MissingSnapshot();
        var facts = empty.Facts.Select(fact =>
        {
            var kind = FrameFactsRetainedCatalog.Entries.Single(entry => entry.Definition.Key == fact.Key).Definition.Kind;
            return fact with
            {
                Availability = FrameFactsAvailability.Available,
                Kind = kind,
                Text = kind == FrameFactsValueKind.Text ? "fixture" : null,
                WholeNumber = kind == FrameFactsValueKind.WholeNumber ? 42 : null,
                Number = kind == FrameFactsValueKind.Number ? 12.5 : null,
                Timestamp = kind == FrameFactsValueKind.Timestamp ? DateTimeOffset.UnixEpoch : null
            };
        }).ToArray();
        var snapshot = empty with { Facts = facts };
        return snapshot with { IdentitySha256 = snapshot.ComputeIdentity() };
    }

    internal static CapturePipelineConfig LegacyPipeline() => new([
        new CaptureProcessingStepConfig("EnvironmentPresentationLayer", "facts", 70,
            JsonDocument.Parse("""{ "widthPixels":1936, "heightPixels":1216, "future":{"keep":[1, 2]} }""").RootElement.Clone(), ["$raw"]),
        new CaptureProcessingStepConfig("OverlayManifest", "manifest", 80, DependsOn: ["facts"]),
        new CaptureProcessingStepConfig("PresentationMaterializer", "materialized", 90, DependsOn: ["facts"])
    ]);

    private static BunitContext Context()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        return context;
    }

    private static ProcessingExecutionPagesTests.GraphUiService Configure(BunitContext context, CapturePipelineConfig pipeline)
    {
        var service = new ProcessingExecutionPagesTests.GraphUiService
        {
            Registry = ProcessingGraphPagesTests.Registry(),
            RevisionDetail = new(ProcessingGraphPagesTests.Registry().Revisions[0], pipeline, null, null),
            Preview = _ => OperatorUiResult<HVO.SkyMonitor.CameraAgent.Common.Capture.Processing.CaptureProcessingPlanPreview>.Success(
                new(pipeline.SchemaVersion, pipeline.DependencyPolicy, new string('A', 64), new string('B', 64), [], [])),
            Create = command => OperatorUiResult<HVO.SkyMonitor.CameraAgent.Common.Capture.Processing.ProcessingGraphRevisionState>.Success(new("rev-new", command.Name, command.Revision,
                HVO.SkyMonitor.CameraAgent.Common.Capture.Processing.ProcessingGraphRevisionLifecycle.Draft, new string('A', 64), new string('B', 64), new string('C', 64), DateTimeOffset.UnixEpoch, null, null, null))
        };
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?from=rev-active");
        return service;
    }
}
