using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    private static readonly string[] DeepSkyFontChoices = ["MonoRegular", "MonoBold"];
    [TestMethod]
    public async Task EnvironmentSelectionOutsideEnabledAssociationPolicyIsRejectedBeforeDraftCreation()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-environment-policy");
        try
        {
            var acquisition = PresentationMetadataFactsBuilderTests.EnvironmentSelectionAcquisition(
                new EnvironmentPresentationLayerProcessingStepOptions().EnvironmentalKinds);
            Validator.ValidateObject(acquisition, new ValidationContext(acquisition), validateAllProperties: true);
            using var provider = CreateProvider(root, PresentationMetadataFactsBuilderTests.EnvironmentSelectionSettings(acquisition));
            Assert.HasCount(6, provider.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value.EnvironmentalAcquisition.Sources);
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var config = CreateProjectedSceneConfiguration() with
            {
                Pipeline = new CapturePipelineConfig([
                    new("Calibration", "calibration", 10, DependsOn: ["$raw"]),
                    new("RollingCombination", "rolling", 20, DependsOn: ["calibration"]),
                    new("CombinedPreview", "preview", 30, DependsOn: ["rolling"]),
                    new("ProjectedScene", "scene", 40, DependsOn: ["$raw"]),
                    new("EnvironmentPresentationLayer", "environment", 50,
                        JsonSerializer.Deserialize<JsonElement>("""{"widthPixels":1936,"heightPixels":1216,"future":1e+02}"""), ["scene", "preview"])
                ])
            };
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var retainedSource = (await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false))!.Steps[^1].Options!.Value.GetRawText();
            var model = ProcessingGraphDraftModel.FromPipeline(config.Pipeline, initial.ActiveRevisionId, "environment", "2");
            var form = model.Nodes[^1].OptionsForm!;
            form.ClearCollection("environmentalKinds");
            form.AddCollectionItem("environmentalKinds");
            form.SetCollectionItem("environmentalKinds", 0, "SkyQuality");
            Assert.IsTrue(model.TryBuild(out var edited, out _));
            var error = Assert.ThrowsExactly<ValidationException>(() =>
                provider.GetRequiredService<ICaptureProcessingPipelineFactory>().PreviewPlan(config with { Pipeline = edited }));
            StringAssert.Contains(error.Message, "SkyQuality", StringComparison.Ordinal);
            StringAssert.Contains(error.Message, "configured association policy", StringComparison.Ordinal);
            await Assert.ThrowsExactlyAsync<ValidationException>(async () =>
                await operations.CreateRevisionAsync("environment", "2", edited, "unsupported", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, (await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevisionId);
            Assert.AreEqual(retainedSource,
                (await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false))!.Steps[^1].Options!.Value.GetRawText());
            form.ClearCollection("environmentalKinds");
            Assert.IsTrue(model.TryBuild(out var empty, out _));
            var draft = await operations.CreateRevisionAsync("environment", "empty", empty, "empty", "test", null, CancellationToken.None).ConfigureAwait(false);
            await operations.ValidateRevisionAsync(draft.RevisionId, "validate-empty", "test", null, CancellationToken.None).ConfigureAwait(false);
            var active = await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate-empty", "test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(draft.RevisionId, active.ActiveRevisionId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("CloudPresentationLayer", "lineThickness", "4", "9")]
    [DataRow("EnvironmentPresentationLayer", "inset", "32", "65")]
    [DataRow("EnvironmentPresentationLayer", "value", "0", "256")]
    [DataRow("EnvironmentPresentationLayer", "value", "255", "-1")]
    [DataRow("EnvironmentPresentationLayer", "value", "", "1.5")]
    [DataRow("ScenePresentationLayer", "markerValue", "0", "256")]
    [DataRow("ScenePresentationLayer", "labelValue", "255", "-1")]
    [DataRow("ScenePresentationLayer", "imageCircleValue", "0", "true")]
    [DataRow("ScenePresentationLayer", "cardinalValue", "255", "1e400")]
    public async Task RetainedLayerDraftRequiresValidationActivationAndSupportsRollback(string alias, string key, string value, string invalid)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-retained-layer-options");
        try
        {
            using var provider = CreateProvider(root);
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var config = CreateProjectedSceneConfiguration() with
            {
                Pipeline = new CapturePipelineConfig([
                    new("Calibration", "calibration", 10, DependsOn: ["$raw"]),
                    new("RollingCombination", "rolling", 20, DependsOn: ["calibration"]),
                    new("CombinedPreview", "preview", 30, DependsOn: ["rolling"]),
                    new("ProjectedScene", "scene", 40, DependsOn: ["$raw"]),
                    new("CloudAssessment", "assessment", 50, DependsOn: ["calibration"]),
                    new(alias, "layer", 60, JsonDocument.Parse("""{ "WidthPixels":1936, "HeightPixels":1216, "Value":null, "future":[1e+02] }""").RootElement.Clone(),
                        alias == "CloudPresentationLayer" ? ["assessment"] : alias == "ScenePresentationLayer" ? ["scene"] : ["scene", "preview"])
                ])
            };
            var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
            var plan = factory.CreateGraph(config);
            var descriptor = alias == "CloudPresentationLayer" ? RetainedLayerOptionDescriptors.Cloud : alias == "ScenePresentationLayer" ? ProcessingNodeOptionsDescriptors.ScenePresentationLayer : RetainedLayerOptionDescriptors.Environment;
            try { Assert.AreEqual(descriptor.StepVersion, plan.SharedPlan!.Nodes.Single(static node => node.Definition.Id == "layer").Definition.StepVersion); }
            finally { plan.DisposeSteps(); }
            var runtimeType = alias == "CloudPresentationLayer" ? typeof(CloudPresentationLayerProcessingStepOptions) : alias == "ScenePresentationLayer" ? typeof(ScenePresentationLayerProcessingStepOptions) : typeof(EnvironmentPresentationLayerProcessingStepOptions);
            var defaults = ProcessingNodeOptionsCodec.Decode(descriptor, JsonSerializer.SerializeToElement(Activator.CreateInstance(runtimeType), runtimeType));
            foreach (var option in descriptor.Options)
            {
                Assert.AreEqual(option.DefaultValue, defaults.Get(option.Key).Value);
                if (option.Kind is ProcessingOptionKind.TextCollection or ProcessingOptionKind.EnumCollection)
                {
                    if (option.Kind == ProcessingOptionKind.TextCollection)
                    {
                        Assert.AreEqual(256, option.MaximumItems);
                        Assert.AreEqual(16, option.MaximumLength);
                    }
                    continue;
                }
                if (option.Kind == ProcessingOptionKind.Font)
                {
                    var font = runtimeType.GetProperties().Single(property => string.Equals(property.Name, option.Members.Single(), StringComparison.OrdinalIgnoreCase));
                    Assert.AreEqual(typeof(string), font.PropertyType);
                    CollectionAssert.AreEquivalent(DeepSkyFontChoices, option.AllowedValues.ToArray());
                    Assert.IsTrue(option.AllowedValues.Contains(option.DefaultValue.AsText(), StringComparer.Ordinal));
                    continue;
                }
                if (option.Kind is ProcessingOptionKind.Toggle or ProcessingOptionKind.Color or ProcessingOptionKind.RealNumber) continue;
                var property = runtimeType.GetProperties().Single(property => string.Equals(property.Name, option.Members.Single(), StringComparison.OrdinalIgnoreCase));
                var range = (RangeAttribute)Attribute.GetCustomAttribute(property, typeof(RangeAttribute))!;
                Assert.AreEqual(option.MinimumWholeNumber, Convert.ToInt64(range.Minimum, CultureInfo.InvariantCulture));
                Assert.AreEqual(option.MaximumWholeNumber, Convert.ToInt64(range.Maximum, CultureInfo.InvariantCulture));
            }
            var missingDimensions = config.Pipeline with { Steps = config.Pipeline.Steps.Select(step => step.Id == "layer" ? step with { Options = null } : step).ToArray() };
            if (alias != "ScenePresentationLayer") Assert.ThrowsExactly<ValidationException>(() => factory.PreviewPlan(config with { Pipeline = missingDimensions }));
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var storedSource = (await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false))!.Steps[^1].Options!.Value.GetRawText();
            var model = ProcessingGraphDraftModel.FromPipeline(config.Pipeline, initial.ActiveRevisionId, "layer", "2");
            Assert.IsTrue(model.TryBuild(out var unchanged, out _));
            Assert.AreEqual(factory.PreviewPlan(config).EffectiveSha256, factory.PreviewPlan(config with { Pipeline = unchanged }).EffectiveSha256);
            model.Nodes[^1].OptionsForm!.Set(key, value);
            Assert.IsTrue(model.TryBuild(out var edited, out _));
            var draft = await operations.CreateRevisionAsync("layer", "2", edited, "create", "test", null, CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "early", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, (await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevisionId);
            await operations.ValidateRevisionAsync(draft.RevisionId, "validate", "test", null, CancellationToken.None).ConfigureAwait(false);
            var active = await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate", "test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(draft.RevisionId, active.ActiveRevisionId);
            var retained = await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(storedSource, retained!.Steps[^1].Options!.Value.GetRawText());
            var rollback = await operations.RollbackRevisionAsync(initial.ActiveRevisionId, active.StateVersion, "rollback", "test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, rollback.ActiveRevisionId);
            var bad = config.Pipeline with
            {
                Steps = config.Pipeline.Steps.Select(step => step.Id == "layer" ? step with
                {
                    Options = JsonDocument.Parse("{\"widthPixels\":1936,\"heightPixels\":1216,\"" + key + "\":" + invalid + "}").RootElement.Clone()
                } : step).ToArray()
            };
            if (descriptor.Find(key)!.Kind == ProcessingOptionKind.NullableWholeNumber)
                await Assert.ThrowsExactlyAsync<JsonException>(async () =>
                    await operations.CreateRevisionAsync("invalid", "3", bad, "bad", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            else
                await Assert.ThrowsExactlyAsync<ValidationException>(async () =>
                    await operations.CreateRevisionAsync("invalid", "3", bad, "bad", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, (await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevisionId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(
        "ScenePresentationLayer",
        "4918B421967B8E666DDBCBC2F1B3C37864A98CEB28BD4C825C7A8F248FA4866C",
        "4537CC2075230197C36808F30F934949557C61FD03C4845B3C388D071069E46B",
        "F319F063EB58ECFA9EC26875D34C603D6A0D077C8387149DB2E1FA840AC0FB63")]
    [DataRow(
        "SceneStylePresentationLayerV1",
        "D36D41F076ABD9180B47323FFED223DB32AB9B54D8691B90274286F31004F0E8",
        "F288E95CC6FDACA6654D79A3AA73B9EB71ED7831CC9FFF3458971826868B092E",
        "425A0F7FFB59DC4F5D25F53B3DB1D86739DAF25EDAC8395789CE63338D1ACA37")]
    public void DeepSkyEffectiveOptionsPreserveBasePlanHashesAndRespectOptInControls(
        string alias,
        string expectedDefaultSha256,
        string expectedLayerOnSha256,
        string expectedThickness3Sha256)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-deepsky-hash-pin");
        try
        {
            using var provider = CreateRemainingOptionsProvider(root);
            var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
            var baseConfig = CreateProjectedSceneConfiguration();

            string PlanHash(string? optionsJson, out JsonElement effectiveOptions)
            {
                var pipeline = new CapturePipelineConfig(
                [
                    new("ProjectedScene", "scene", 10, null, ["$raw"], Enabled: true),
                    new(alias, "overlay", 20, optionsJson is null ? null : JsonDocument.Parse(optionsJson).RootElement.Clone(), ["scene"], Enabled: true)
                ], CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent);
                var graph = factory.CreateGraph(baseConfig with { Pipeline = pipeline });
                try
                {
                    var node = graph.Nodes.Single(static item => item.Id == "overlay");
                    effectiveOptions = graph.SharedPlan!.Nodes.Single(static item => item.Definition.Id == "overlay").Definition.EffectiveOptions;
                    return node.PlanSha256;
                }
                finally
                {
                    graph.DisposeSteps();
                }
            }

            var defaultHash = PlanHash(null, out var defaultOptions);
            var layerOnHash = PlanHash("""{"includeDeepSkyLayer":true}""", out var layerOnOptions);
            var thickness3Hash = PlanHash("""{"includeDeepSkyLayer":true,"deepSkyLineThickness":3}""", out var thickness3Options);
            var offLabelsFalseHash = PlanHash("""{"includeDeepSkyLayer":false,"deepSkyLabels":false}""", out var offLabelsFalseOptions);
            var offRed10Hash = PlanHash("""{"includeDeepSkyLayer":false,"deepSkyRed":10}""", out var offRed10Options);
            var offPlexRegularHash = PlanHash("""{"includeDeepSkyLayer":false,"deepSkyFont":"PlexRegular"}""", out var offPlexRegularOptions);

            // (i), (iv), (v), (vi): includeDeepSkyLayer=false with pre-existing deepSky* fields (including legacy PlexRegular) stays byte-identical to default.
            Assert.AreEqual(defaultHash, offLabelsFalseHash);
            Assert.AreEqual(defaultHash, offRed10Hash);
            Assert.AreEqual(defaultHash, offPlexRegularHash);
            Assert.AreEqual(defaultOptions.GetRawText(), offLabelsFalseOptions.GetRawText());
            Assert.AreEqual(defaultOptions.GetRawText(), offRed10Options.GetRawText());
            Assert.AreEqual(defaultOptions.GetRawText(), offPlexRegularOptions.GetRawText());

            // Active proposals with PlexRegular fail closed, while retained compilation stays crash-free.
            foreach (var activePlexJson in new[]
            {
                """{"includeDeepSkyLayer":true,"deepSkyFont":"PlexRegular"}""",
                """{"applyInlineDeepSkyStyle":true,"deepSkyFont":"PlexRegular"}"""
            })
            {
                var activePlexPipeline = new CapturePipelineConfig(
                [
                    new("ProjectedScene", "scene", 10, null, ["$raw"], Enabled: true),
                    new(alias, "overlay", 20, JsonDocument.Parse(activePlexJson).RootElement.Clone(), ["scene"], Enabled: true)
                ], CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent);
                Assert.ThrowsExactly<ValidationException>(() => factory.CreateGraph(baseConfig with { Pipeline = activePlexPipeline }));
                var retainedActive = factory.CreateRetainedGraph(baseConfig with { Pipeline = activePlexPipeline });
                retainedActive.DisposeSteps();
            }
            Assert.IsFalse(defaultOptions.EnumerateObject().Any(static prop =>
                prop.Name.StartsWith("deepSky", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(prop.Name, "includeDeepSkyLayer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(prop.Name, "applyInlineDeepSkyStyle", StringComparison.OrdinalIgnoreCase)));

            // (ii) and (iii): includeDeepSkyLayer=true omits the 4 new default properties so base PlanSha256 is preserved.
            foreach (var opts in new[] { layerOnOptions, thickness3Options })
            {
                Assert.IsFalse(opts.TryGetProperty("applyInlineDeepSkyStyle", out _));
                Assert.IsFalse(opts.TryGetProperty("deepSkyMaximumLabels", out _));
                Assert.IsFalse(opts.TryGetProperty("deepSkyMaximumLabelCharacters", out _));
                Assert.IsFalse(opts.TryGetProperty("deepSkyLabelCatalogObjects", out _));
                Assert.IsFalse(opts.TryGetProperty("deepSkyMinimumGlyphRadius", out _));
                Assert.IsTrue(opts.GetProperty("includeDeepSkyLayer").GetBoolean());
            }
            Assert.AreNotEqual(defaultHash, layerOnHash);
            Assert.AreNotEqual(layerOnHash, thickness3Hash);

            // Pin exact base-compatible 64-hex plan hashes for both scene aliases.
            Assert.AreEqual(expectedDefaultSha256, defaultHash, $"alias={alias} default={defaultHash} on={layerOnHash} t3={thickness3Hash}");
            Assert.AreEqual(expectedLayerOnSha256, layerOnHash, $"alias={alias} default={defaultHash} on={layerOnHash} t3={thickness3Hash}");
            Assert.AreEqual(expectedThickness3Sha256, thickness3Hash, $"alias={alias} default={defaultHash} on={layerOnHash} t3={thickness3Hash}");

            // Non-default values of each of the 4 new properties when includeDeepSkyLayer=true MUST change PlanSha256.
            foreach (var (name, json) in new[]
            {
                ("deepSkyMaximumLabels", """{"includeDeepSkyLayer":true,"deepSkyMaximumLabels":8}"""),
                ("deepSkyMaximumLabelCharacters", """{"includeDeepSkyLayer":true,"deepSkyMaximumLabelCharacters":12}"""),
                ("deepSkyLabelCatalogObjects", """{"includeDeepSkyLayer":true,"deepSkyLabelCatalogObjects":false}"""),
                ("deepSkyMinimumGlyphRadius", """{"includeDeepSkyLayer":true,"deepSkyMinimumGlyphRadius":11}""")
            })
            {
                var customHash = PlanHash(json, out var customOptions);
                Assert.IsTrue(customOptions.TryGetProperty(name, out _), $"Non-default '{name}' must enter effective options.");
                Assert.AreNotEqual(layerOnHash, customHash, $"Non-default '{name}' must change PlanSha256.");
            }

            // Explicit opt-in for inline deep-sky styling when includeDeepSkyLayer=false MUST enter effective options and change PlanSha256.
            var inlineOptInHash = PlanHash("""{"includeDeepSkyLayer":false,"applyInlineDeepSkyStyle":true,"deepSkyRed":10,"deepSkyMaximumLabels":6}""", out var inlineOptInOptions);
            Assert.IsTrue(inlineOptInOptions.GetProperty("applyInlineDeepSkyStyle").GetBoolean());
            Assert.AreEqual(10, inlineOptInOptions.GetProperty("deepSkyRed").GetInt32());
            Assert.AreEqual(6, inlineOptInOptions.GetProperty("deepSkyMaximumLabels").GetInt32());
            Assert.AreNotEqual(defaultHash, inlineOptInHash);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("ScenePresentationLayer", """{"includeDeepSkyLayer":true,"deepSkyLineThickness":3}""")]
    [DataRow("ScenePresentationLayer", """{"includeDeepSkyLayer":false,"deepSkyLabels":false,"deepSkyRed":10}""")]
    [DataRow("ScenePresentationLayer", """{"includeDeepSkyLayer":false,"deepSkyFont":"PlexRegular"}""")]
    [DataRow("SceneStylePresentationLayerV1", """{"includeDeepSkyLayer":true,"deepSkyLineThickness":3}""")]
    [DataRow("SceneStylePresentationLayerV1", """{"includeDeepSkyLayer":false,"deepSkyLabels":false,"deepSkyRed":10}""")]
    [DataRow("SceneStylePresentationLayerV1", """{"includeDeepSkyLayer":false,"deepSkyFont":"PlexRegular"}""")]
    public async Task UpgradedSavedDeepSkyNamedRevisionAvoidsSupersessionForActivationRollbackReplayAndLiveLane(
        string alias,
        string legacyOptionsJson)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-deepsky-upgrade-compat");
        try
        {
            var baseConfig = CreateProjectedSceneConfiguration();
            var pipeline = new CapturePipelineConfig(
            [
                new("ProjectedScene", "scene", 10, null, ["$raw"], Enabled: true),
                new(alias, "overlay", 20, JsonDocument.Parse(legacyOptionsJson).RootElement.Clone(), ["scene"], Enabled: true)
            ], CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent);
            var upgradedConfig = baseConfig with { Pipeline = pipeline };

            string revisionId;
            string basicRevisionId;
            long activeStateVersion;
            RawCaptureReceipt receipt;
            using (var provider = CreateProvider(root))
            {
                var ingress = provider.GetRequiredService<IRawCaptureIngress>();
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var basic = await operations.EnsureConfiguredBasicAsync(upgradedConfig, CancellationToken.None).ConfigureAwait(false);
                basicRevisionId = basic.ConfiguredBasicRevisionId;
                var draft = await operations.CreateRevisionAsync("deep-sky-saved", "1", pipeline, "create-legacy", "upgrade-test", null, CancellationToken.None).ConfigureAwait(false);
                revisionId = draft.RevisionId;
                await operations.ValidateRevisionAsync(revisionId, "validate-legacy", "upgrade-test", null, CancellationToken.None).ConfigureAwait(false);
                var activated = await operations.ActivateRevisionAsync(revisionId, basic.StateVersion, "activate-legacy", "upgrade-test", null, CancellationToken.None).ConfigureAwait(false);
                activeStateVersion = activated.StateVersion;
                var stageKey = new string('e', 64);
                var sceneId = new string('d', 64);
                var scene = await CreateProjectedSceneAsync().ConfigureAwait(false);
                await provider.GetRequiredService<IProjectedSceneStagingStore>().StageAsync(
                    stageKey,
                    sceneId,
                    scene,
                    CancellationToken.None).ConfigureAwait(false);
                var submission = WithSceneProvenance(CreateSubmission(), sceneId, stageKey);
                receipt = await ingress.AcceptAndSettleAsync(upgradedConfig, submission, CancellationToken.None).ConfigureAwait(false)
                    ?? throw new AssertFailedException("Raw capture must be accepted.");
            }

            // Reopen fresh host container over the persisted SQLite revision and verify zero supersession.
            using (var reopened = CreateProvider(root))
            {
                var ingress = reopened.GetRequiredService<IRawCaptureIngress>();
                await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var operations = reopened.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var refreshed = await operations.EnsureConfiguredBasicAsync(upgradedConfig, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(revisionId, refreshed.ActiveRevisionId);

                var superseded = await operations.FindSupersededActiveRevisionAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsNull(superseded, "Saved deep-sky named revision must not be reported as superseded after upgrade.");

                // Live standard lane processes without abandoning for supersession.
                var laneStore = reopened.GetRequiredService<ICaptureLaneStore>();
                await laneStore.InitializeLanesAsync(CancellationToken.None).ConfigureAwait(false);
                var standardLane = reopened.GetRequiredService<CaptureLanePolicy>().Definitions.Single(static item => item.Name == "standard");
                var lease = await laneStore.ClaimAsync(standardLane, "upgrade-worker", upgradedConfig, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(lease);
                var liveResult = await reopened.GetRequiredService<StandardCaptureLaneHandler>().HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, liveResult.Outcome,
                    $"Live standard lane must complete upgraded revision without supersession (got {liveResult.Outcome}: {liveResult.Reason}).");

                // Replay submission succeeds without ProcessingGraphRevisionSupersededException.
                var replay = await operations.SubmitReplayAsync(new ProcessingReplaySubmission(
                    receipt.Manifest.Descriptor.Capture.CaptureId,
                    revisionId,
                    receipt.Manifest.Descriptor.Artifact.ArtifactId,
                    Reason: "upgrade-replay-check"),
                    "replay-upgrade-check", "upgrade-test", CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(revisionId, replay.Execution.GraphRevisionId);

                // Rollback to configured-basic and re-activation of the saved named revision both succeed.
                var rolledBack = await operations.RollbackRevisionAsync(basicRevisionId, activeStateVersion, "rollback-check", "upgrade-test", null, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(basicRevisionId, rolledBack.ActiveRevisionId);
                var reactivated = await operations.ActivateRevisionAsync(revisionId, rolledBack.StateVersion, "reactivate-check", "upgrade-test", null, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(revisionId, reactivated.ActiveRevisionId);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
