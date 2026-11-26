using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Services")]
public sealed class ProcessingGraphDraftModelTests
{
    private static readonly string[] PreviewOnly = ["preview"];
    private static readonly string[] AddedIds = ["preview", "preview-2", "telemetry"];
    private static readonly string[] EligibleForThird = ["$raw", "preview", "preview-2"];
    private static readonly string[] MovedIds = ["preview", "telemetry", "preview-2"];

    [TestMethod]
    public void OptionOnlyEditPreservesSourceGraphShapeAndNoOpIdentity()
    {
        var pipeline = new CapturePipelineConfig([
            new CaptureProcessingStepConfig("ProjectedScene", "scene", Order: 77, DependsOn: ["$raw"], Enabled: true),
            new CaptureProcessingStepConfig("sCeNePrEsEnTaTiOnLaYeR", Options: JsonSerializer.SerializeToElement(new { future = 17 }),
                DependsOn: ["scene", "$raw"], Publication: new(CaptureProcessingPersistenceMode.DurableLocal))
        ]);
        var sourceJson = JsonSerializer.Serialize(pipeline);
        var model = ProcessingGraphDraftModel.FromPipeline(pipeline, "retained", "sky", "2");
        Assert.IsTrue(model.TryBuild(out var unchanged, out _));
        Assert.AreEqual(sourceJson, JsonSerializer.Serialize(unchanged));
        Assert.IsFalse(model.HasGraphChanges(unchanged));

        model.Nodes[1].OptionsForm!.Set("constellationLineThickness", "6");
        Assert.IsTrue(model.TryBuild(out var edited, out _));
        Assert.IsTrue(model.HasGraphChanges(edited));
        Assert.AreEqual(sourceJson, JsonSerializer.Serialize(pipeline), "The retained definition must remain immutable.");
        Assert.AreEqual(pipeline.Steps[0], edited.Steps[0]);
        Assert.AreEqual(pipeline.Steps[1], edited.Steps[1] with { Options = pipeline.Steps[1].Options });
        Assert.AreEqual(6, edited.Steps[1].Options!.Value.GetProperty("constellationLineThickness").GetInt32());
    }

    [TestMethod]
    public void FromPipeline_ThenBuild_RoundTripsNodesDependenciesAndOpaqueOptions()
    {
        var options = JsonSerializer.SerializeToElement(new { outputVariant = "display" });
        var source = new CapturePipelineConfig(
        [
            new CaptureProcessingStepConfig("Preview", "preview", 10, options, ["$raw"], Enabled: true),
            new CaptureProcessingStepConfig("Telemetry", "telemetry", 20, null, ["preview"], Required: false, Enabled: false)
        ]);

        var model = ProcessingGraphDraftModel.FromPipeline(source, "rev-1", "night", "2");
        Assert.IsTrue(model.TryBuild(out var built, out var errors), string.Join(" ", errors));

        Assert.AreEqual("rev-1", model.SourceRevisionId);
        Assert.HasCount(2, built.Steps);
        Assert.AreEqual(CapturePipelineSchemaVersions.ExplicitV2, built.SchemaVersion);
        Assert.AreEqual(CapturePipelineDependencyPolicy.RejectEnabledDependent, built.DependencyPolicy);
        Assert.AreEqual(options.GetRawText(), built.Steps[0].Options!.Value.GetRawText());
        Assert.IsTrue(built.Steps[0].Enabled);
        Assert.IsFalse(built.Steps[1].Enabled);
        Assert.IsFalse(built.Steps[1].Required);
        CollectionAssert.AreEqual(PreviewOnly, built.Steps[1].DependsOn!.ToArray());
        Assert.AreEqual(10, built.Steps[0].Order);
        Assert.AreEqual(20, built.Steps[1].Order);
        Assert.IsTrue(model.Nodes[0].HasOptions);
    }

    [TestMethod]
    public void EditingOperations_KeepTheListConsistent()
    {
        var model = ProcessingGraphDraftModel.Empty();
        model.Name = "night";
        model.Revision = "1";
        model.AddNode("preview");
        model.AddNode("preview");
        model.AddNode("telemetry");

        CollectionAssert.AreEqual(AddedIds, model.Nodes.Select(static node => node.Id).ToArray());
        CollectionAssert.AreEqual(EligibleForThird, model.EligibleDependencies(model.Nodes[2]).ToArray());
        model.Nodes[1].Id = "   ";
        CollectionAssert.DoesNotContain(model.EligibleDependencies(model.Nodes[2]).ToArray(), "   ");
        Assert.IsFalse(model.EligibleDependencies(model.Nodes[2]).Any(static id => id.Length == 0));
        model.Nodes[1].Id = "preview-2";
        model.Nodes[2].ToggleDependency("preview", selected: true);
        model.Move(model.Nodes[2], -1);
        CollectionAssert.AreEqual(MovedIds, model.Nodes.Select(static node => node.Id).ToArray());
        model.Remove(model.Nodes[0]);
        Assert.IsFalse(model.Nodes[0].DependsOn.Contains("preview"));
        Assert.IsTrue(model.TryBuild(out _, out _));
        model.Nodes[0].ToggleDependency(ProcessingGraphDraftModel.RawInput, selected: false);
        Assert.IsFalse(model.TryBuild(out _, out var errors));
        CollectionAssert.Contains(errors.ToArray(), "Node 'telemetry' must declare at least one dependency (use $raw for the raw capture).");
    }

    [TestMethod]
    public void Build_ReportsIdentityDuplicateAndForwardDependencyProblems()
    {
        var model = ProcessingGraphDraftModel.Empty();
        model.AddNode("preview");
        model.AddNode("telemetry");
        model.Nodes[1].Id = "preview";
        model.Nodes[0].ToggleDependency("telemetry", selected: true);

        Assert.IsFalse(model.TryBuild(out _, out var errors));

        CollectionAssert.Contains(errors.ToArray(), "The graph needs a name.");
        CollectionAssert.Contains(errors.ToArray(), "The graph needs a revision label.");
        CollectionAssert.Contains(errors.ToArray(), "Node identifier 'preview' is used more than once.");
        CollectionAssert.Contains(errors.ToArray(), "Node 'preview' depends on 'telemetry', which is not the raw input or a node above it.");
    }

    [TestMethod]
    public void Move_DropsInputsThatAreNoLongerAboveAndKeepsStaleSelectionsRenderable()
    {
        var model = ProcessingGraphDraftModel.Empty();
        model.Name = "night";
        model.Revision = "1";
        model.AddNode("preview");
        model.AddNode("telemetry");
        model.Nodes[1].ToggleDependency("preview", selected: true);

        model.Move(model.Nodes[1], -1);

        Assert.AreEqual("telemetry", model.Nodes[0].Id);
        Assert.IsFalse(model.Nodes[0].DependsOn.Contains("preview"), "an input that moved below must not remain selected");
        Assert.IsTrue(model.TryBuild(out _, out _));

        // A renamed predecessor leaves a stale selection that stays visible so it can be cleared.
        model.Nodes[1].ToggleDependency("telemetry", selected: true);
        model.Nodes[0].Id = "telemetry-2";
        CollectionAssert.Contains(model.RenderableDependencies(model.Nodes[1]).ToArray(), "telemetry");
        Assert.IsFalse(model.TryBuild(out _, out var errors));
        CollectionAssert.Contains(errors.ToArray(), "Node 'preview' depends on 'telemetry', which is not the raw input or a node above it.");
    }

    [TestMethod]
    public void RegionalVisibilityEditsPreserveRetainedStepSettingsAndPruneUnusedPrediction()
    {
        var storage = new CaptureProcessingStepConfig("Storage", "archive", Order: 75,
            Options: JsonSerializer.SerializeToElement(new { queueForUpload = false, futureOption = 7 }),
            DependsOn: ["$raw"], Enabled: true, Publication: new(CaptureProcessingPersistenceMode.DurableLocal));
        var measurement = new CaptureProcessingStepConfig(ExposureMeasurementProducts.StepAlias,
            ExposureMeasurementProducts.StepAlias, Order: 4, DependsOn: ["$raw"], Required: false);
        var original = new CapturePipelineConfig([measurement, storage]);
        var draft = ProcessingGraphDraftModel.FromPipeline(original, "retained", "regional", "2");
        draft.AddNode(RegionalStellarVisibilityProducts.StepAlias);
        Assert.IsTrue(draft.TryBuild(out var added, out var errors), string.Join(" ", errors));
        Assert.AreEqual(storage, added.Steps.Single(step => step.Id == storage.Id));
        Assert.AreEqual(measurement, added.Steps.Single(step => step.Id == measurement.Id));
        Assert.HasCount(1, added.Steps.Where(static step => step.Type == ExposureCatalogPredictionProducts.StepAlias).ToArray());

        // Disable the last consumer on a freshly reopened revision.
        draft = ProcessingGraphDraftModel.FromPipeline(added, "retained-regional", "regional", "3");
        var regional = draft.Nodes.Single(static row => row.Type == RegionalStellarVisibilityProducts.StepAlias);
        regional.Enabled = false;
        draft.EnsureRegionalVisibilityWiring(regional);
        Assert.IsTrue(draft.TryBuild(out var disabled, out errors), string.Join(" ", errors));
        Assert.IsFalse(draft.Nodes.Any(static row => row.Type == ExposureCatalogPredictionProducts.StepAlias));
        Assert.IsFalse(regional.DependsOn.Contains(ExposureCatalogPredictionProducts.StepAlias));
        Assert.AreEqual(storage, disabled.Steps.Single(step => step.Id == storage.Id));

        regional.Enabled = true;
        draft.EnsureRegionalVisibilityWiring(regional);
        draft.AddNode(RegionalStellarVisibilityProducts.StepAlias);
        var otherConsumer = draft.Nodes.Single(row => row.Type == RegionalStellarVisibilityProducts.StepAlias && row != regional);
        draft.Remove(regional);
        Assert.IsTrue(draft.Nodes.Any(static row => row.Type == ExposureCatalogPredictionProducts.StepAlias));
        draft.Remove(otherConsumer);
        Assert.IsFalse(draft.Nodes.Any(static row => row.Type == ExposureCatalogPredictionProducts.StepAlias));
        Assert.IsTrue(draft.TryBuild(out var removed, out errors), string.Join(" ", errors));
        Assert.AreEqual(JsonSerializer.Serialize(original), JsonSerializer.Serialize(removed));
    }

    [TestMethod]
    public void EnsureRegionalVisibilityWiringDoesNotAddLaterIneligibleAstrometryDependency()
    {
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        var original = new CapturePipelineConfig(
        [
            new(ExposureMeasurementProducts.StepAlias, ExposureMeasurementProducts.StepAlias, Order: 4, DependsOn: ["$raw"], Required: false, Enabled: true, Publication: durable),
            new(RegionalStellarVisibilityProducts.StepAlias, "regional", Order: 30, DependsOn: ["$raw", ExposureMeasurementProducts.StepAlias], Required: false, Enabled: false, Publication: durable),
            new(CaptureAnalysisOptionDescriptors.CenteredAstrometryAlias, "solve", Order: 40, DependsOn: ["$raw", ExposureMeasurementProducts.StepAlias], Required: false, Enabled: true, Publication: durable)
        ]);

        var draft = ProcessingGraphDraftModel.FromPipeline(original, "retained", "regional", "2");
        var regional = draft.Nodes.Single(static row => row.Id == "regional");
        regional.Enabled = true;
        draft.EnsureRegionalVisibilityWiring(regional);

        Assert.IsFalse(regional.DependsOn.Contains("solve"),
            "EnsureRegionalVisibilityWiring must not wire a later astrometry node that is ineligible as a predecessor.");
        Assert.IsTrue(regional.DependsOn.Contains(ExposureCatalogPredictionProducts.StepAlias));
        Assert.IsTrue(draft.TryBuild(out var rebuilt, out var errors), string.Join(" ", errors));
        var rebuiltRegional = rebuilt.Steps.Single(static step => step.Id == "regional");
        Assert.IsFalse(rebuiltRegional.DependsOn!.Contains("solve", StringComparer.Ordinal));
        Assert.IsTrue(rebuiltRegional.DependsOn!.Contains(ExposureCatalogPredictionProducts.StepAlias, StringComparer.Ordinal));
    }

    [TestMethod]
    public void FromPipeline_CarriesSchemaAndDependencyPolicyIntoTheBuiltDraft()
    {
        var legacy = new CapturePipelineConfig(
            [new CaptureProcessingStepConfig("Preview", "preview", 10, null, ["$raw"])],
            CapturePipelineSchemaVersions.LegacyV1,
            CapturePipelineDependencyPolicy.LegacyInference);

        var model = ProcessingGraphDraftModel.FromPipeline(legacy, "rev-legacy", "legacy", "1");
        Assert.IsTrue(model.TryBuild(out var built, out var errors), string.Join(" ", errors));

        Assert.AreEqual(CapturePipelineSchemaVersions.LegacyV1, model.SchemaVersion);
        Assert.AreEqual(CapturePipelineDependencyPolicy.LegacyInference, built.DependencyPolicy);
        Assert.AreEqual(CapturePipelineSchemaVersions.LegacyV1, built.SchemaVersion);
        Assert.AreEqual(CapturePipelineSchemaVersions.ExplicitV2, ProcessingGraphDraftModel.Empty().SchemaVersion);
    }

    [TestMethod]
    [DataRow("12")]
    [DataRow("+12")]
    [DataRow("012")]
    [DataRow("-0")]
    public void UntouchedOverlayManifestWithQuotedZOrderSurvivesUnrelatedNodeEditByteForByte(string quotedZOrder)
    {
        var manifestJson = $$"""{ "layers": [ { "kind": "scene-constellations", "enabled": false, "zOrder": "{{quotedZOrder}}", "future": 1e+02 } ] }""";
        var source = new CapturePipelineConfig(
        [
            new CaptureProcessingStepConfig("JpegEncoding", "jpeg", 10,
                JsonDocument.Parse("""{ "jpegQuality": 85 }""").RootElement.Clone(), ["$raw"], Enabled: true),
            new CaptureProcessingStepConfig("OverlayManifest", "manifest", 20,
                JsonDocument.Parse(manifestJson).RootElement.Clone(), ["jpeg"], Enabled: true)
        ]);

        var model = ProcessingGraphDraftModel.FromPipeline(source, "rev-quoted", "night", "2");
        Assert.IsTrue(model.TryBuild(out var unchanged, out var initialErrors), string.Join(' ', initialErrors));
        Assert.IsFalse(model.HasGraphChanges(unchanged));
        Assert.AreEqual(manifestJson, unchanged.Steps[1].Options!.Value.GetRawText());

        model.Nodes[0].OptionsForm!.Set("jpegQuality", "92");
        Assert.IsTrue(model.TryBuild(out var edited, out var errors), string.Join(' ', errors));
        Assert.IsTrue(model.HasGraphChanges(edited));
        Assert.AreEqual(92, edited.Steps[0].Options!.Value.GetProperty("jpegQuality").GetInt32());
        Assert.AreEqual(manifestJson, edited.Steps[1].Options!.Value.GetRawText(),
            "An untouched OverlayManifest with retained quoted zOrder and unknown fields must survive byte-for-byte.");
    }

    [TestMethod]
    public void ExistsInSourceRevision_DistinguishesSavedNodesFromUnsavedAndRenamedNodes()
    {
        var empty = ProcessingGraphDraftModel.Empty();
        empty.AddNode("Preview");
        Assert.IsFalse(empty.ExistsInSourceRevision(empty.Nodes[0]));

        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        var source = new CapturePipelineConfig(
        [
            new(ExposureMeasurementProducts.StepAlias, ExposureMeasurementProducts.StepAlias, 4, null, ["$raw"], Required: false, Enabled: true, Publication: durable),
            new("Preview", "preview", 10, null, ["$raw"], Enabled: true)
        ]);
        var draft = ProcessingGraphDraftModel.FromPipeline(source, "rev-active", "night", "2");
        Assert.IsTrue(draft.ExistsInSourceRevision(draft.Nodes[0]));
        Assert.IsTrue(draft.ExistsInSourceRevision(draft.Nodes[1]));

        draft.AddNode(RegionalStellarVisibilityProducts.StepAlias);
        var regional = draft.Nodes.Single(static row => row.Type == RegionalStellarVisibilityProducts.StepAlias);
        var prediction = draft.Nodes.Single(static row => row.Type == ExposureCatalogPredictionProducts.StepAlias);
        Assert.IsFalse(draft.ExistsInSourceRevision(regional), "Newly added editable node does not exist in the source revision.");
        Assert.IsFalse(draft.ExistsInSourceRevision(prediction), "Auto-added ExposureCatalogPrediction does not exist in the source revision.");

        draft.Nodes[1].Id = "preview-renamed";
        Assert.IsFalse(draft.ExistsInSourceRevision(draft.Nodes[1]), "Renamed node ID does not exist in the source revision.");
        draft.Nodes[1].Id = "PREVIEW";
        Assert.IsTrue(draft.ExistsInSourceRevision(draft.Nodes[1]), "Source revision lookup matches case-insensitively like the config page.");

        var converted = draft.WithConvertedPipeline(new CapturePipelineConfig(
        [
            new(ExposureMeasurementProducts.StepAlias, ExposureMeasurementProducts.StepAlias, 4, null, ["$raw"], Required: false, Enabled: true, Publication: durable),
            new(ExposureCatalogPredictionProducts.StepAlias, ExposureCatalogPredictionProducts.StepAlias, 5, null, ["$raw"], Required: false, Enabled: true, Publication: durable)
        ]));
        Assert.IsTrue(converted.ExistsInSourceRevision(converted.Nodes[0]));
        Assert.IsFalse(converted.ExistsInSourceRevision(converted.Nodes[1]),
            "WithConvertedPipeline must retain the original source revision's node set.");
    }

    [TestMethod]
    public void ExistsInSourceRevision_RejectsDeletedIdReuseTypeChangesConversionsAndDuplicateIds()
    {
        var source = new CapturePipelineConfig(
        [
            new("ImageQuality", "quality", 10, null, ["$raw"], Enabled: true),
            new("Preview", "preview", 20, null, ["$raw"], Enabled: true),
            new("ScenePresentationLayer", "scene", 30, null, ["$raw"], Enabled: true)
        ]);

        // 1. Deleting a saved row and renaming another saved row (or a brand-new row) to that deleted ID must return false.
        var draft = ProcessingGraphDraftModel.FromPipeline(source, "rev-active", "night", "2");
        var qualityRow = draft.Nodes.Single(static row => row.Id == "quality");
        var previewRow = draft.Nodes.Single(static row => row.Id == "preview");
        draft.Remove(qualityRow);
        previewRow.Id = "quality";
        Assert.IsFalse(draft.ExistsInSourceRevision(previewRow),
            "Renaming an existing row to a deleted saved row's ID must not link to the deleted source stage.");

        draft.AddNode("ImageQuality");
        var addedQuality = draft.Nodes[^1];
        addedQuality.Id = "quality";
        Assert.IsFalse(draft.ExistsInSourceRevision(addedQuality),
            "A newly added row renamed to a deleted saved ID (even with the same type) must not link to the source revision.");

        // 2. Changing a saved row's type directly or via WithConvertedPipeline must return false while unchanged rows stay true.
        var typeChangeDraft = ProcessingGraphDraftModel.FromPipeline(source, "rev-active", "night", "2");
        var changedRow = typeChangeDraft.Nodes.Single(static row => row.Id == "preview");
        changedRow.Type = "CombinedPreview";
        Assert.IsFalse(typeChangeDraft.ExistsInSourceRevision(changedRow),
            "Changing a saved row's type must disable source-revision linking for that row.");
        Assert.IsTrue(typeChangeDraft.ExistsInSourceRevision(typeChangeDraft.Nodes.Single(static row => row.Id == "quality")));

        var converted = typeChangeDraft.WithConvertedPipeline(new CapturePipelineConfig(
        [
            new("ImageQuality", "quality", 10, null, ["$raw"], Enabled: true),
            new("CombinedPreview", "preview", 20, null, ["$raw"], Enabled: true),
            new(SceneStylePresentation.StepAlias, "scene", 30, null, ["$raw"], Enabled: true)
        ]));
        Assert.IsTrue(converted.ExistsInSourceRevision(converted.Nodes.Single(static row => row.Id == "quality")),
            "Untouched saved row must stay linked after WithConvertedPipeline.");
        Assert.IsFalse(converted.ExistsInSourceRevision(converted.Nodes.Single(static row => row.Id == "preview")),
            "Type-changed row must stay unlinked after WithConvertedPipeline.");
        Assert.IsFalse(converted.ExistsInSourceRevision(converted.Nodes.Single(static row => row.Id == "scene")),
            "Row whose type was upgraded by WithConvertedPipeline must not link to the old source-revision stage.");

        // 3. Duplicate trimmed IDs across two rows disable source-revision linking for both conflicting rows until resolved.
        var duplicateDraft = ProcessingGraphDraftModel.FromPipeline(source, "rev-active", "night", "2");
        var originalPreview = duplicateDraft.Nodes.Single(static row => row.Id == "preview");
        duplicateDraft.AddNode("Preview");
        var duplicatePreview = duplicateDraft.Nodes[^1];
        duplicatePreview.Id = " PREVIEW ";
        Assert.IsFalse(duplicateDraft.ExistsInSourceRevision(originalPreview),
            "Duplicating a saved ID in the draft must disable source-revision linking for the conflicting saved row.");
        Assert.IsFalse(duplicateDraft.ExistsInSourceRevision(duplicatePreview),
            "Duplicating a saved ID in the draft must disable source-revision linking for the duplicate row.");

        duplicatePreview.Id = "preview-extra";
        Assert.IsTrue(duplicateDraft.ExistsInSourceRevision(originalPreview),
            "Resolving the duplicate ID restores source-revision linking for the original saved row.");
        Assert.IsFalse(duplicateDraft.ExistsInSourceRevision(duplicatePreview));
    }

    [TestMethod]
    public void ExistsInSourceRevision_TreatsRegeneratedPredictionAsUnsavedAndPreservesProvenanceAcrossUnrelatedConversion()
    {
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        const string measurementAlias = ExposureMeasurementProducts.StepAlias;
        const string predictionAlias = ExposureCatalogPredictionProducts.StepAlias;

        // 1. Disabling and re-enabling the sole regional visibility consumer regenerates ExposureCatalogPrediction as an unsaved replacement.
        var centeredSource = new CapturePipelineConfig(
        [
            new(measurementAlias, measurementAlias, 4, null, ["$raw"], Required: false, Enabled: true, Publication: durable),
            new(predictionAlias, predictionAlias, 5, null, ["$raw"], Required: false, Enabled: true, Publication: durable),
            new(RegionalStellarVisibilityProducts.StepAlias, "regional", 30, null,
                ["$raw", measurementAlias, predictionAlias], Required: false, Enabled: true, Publication: durable)
        ]);
        var toggleDraft = ProcessingGraphDraftModel.FromPipeline(centeredSource, "rev-active", "night", "2");
        var initialPrediction = toggleDraft.Nodes.Single(static row => row.Type == predictionAlias);
        var regional = toggleDraft.Nodes.Single(static row => row.Id == "regional");
        Assert.IsTrue(toggleDraft.ExistsInSourceRevision(initialPrediction));

        regional.Enabled = false;
        toggleDraft.EnsureRegionalVisibilityWiring(regional);
        Assert.IsFalse(toggleDraft.Nodes.Any(static row => row.Type == predictionAlias));

        regional.Enabled = true;
        toggleDraft.EnsureRegionalVisibilityWiring(regional);
        var regeneratedPrediction = toggleDraft.Nodes.Single(static row => row.Type == predictionAlias);
        Assert.IsFalse(toggleDraft.ExistsInSourceRevision(regeneratedPrediction),
            "A regenerated ExposureCatalogPrediction row must not inherit source provenance in the same draft.");

        // 2. Temporarily renaming a saved row across an unrelated conversion preserves provenance once the original ID is restored.
        var conversionSource = new CapturePipelineConfig(
        [
            new("Preview", "preview", 10, null, ["$raw"], Enabled: true),
            new("ScenePresentationLayer", "scene", 20, null, ["$raw"], Enabled: true)
        ]);
        var renameDraft = ProcessingGraphDraftModel.FromPipeline(conversionSource, "rev-active", "night", "2");
        var previewRow = renameDraft.Nodes.Single(static row => row.Id == "preview");
        previewRow.Id = "preview-temp";
        Assert.IsFalse(renameDraft.ExistsInSourceRevision(previewRow));

        var converted = renameDraft.WithConvertedPipeline(new CapturePipelineConfig(
        [
            new("Preview", "preview-temp", 10, null, ["$raw"], Enabled: true),
            new(SceneStylePresentation.StepAlias, "scene", 20, null, ["$raw"], Enabled: true)
        ]));
        var convertedPreview = converted.Nodes.Single(static row => row.Id == "preview-temp");
        var convertedScene = converted.Nodes.Single(static row => row.Id == "scene");
        Assert.IsFalse(converted.ExistsInSourceRevision(convertedPreview),
            "While temporarily renamed, the converted draft row must remain unlinked.");
        Assert.IsFalse(converted.ExistsInSourceRevision(convertedScene),
            "A row whose type changed during conversion must remain unlinked.");

        convertedPreview.Id = "preview";
        Assert.IsTrue(converted.ExistsInSourceRevision(convertedPreview),
            "Restoring the original ID on an untouched saved row after an unrelated conversion must restore source-revision linking.");
    }
}
