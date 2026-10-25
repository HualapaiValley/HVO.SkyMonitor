using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
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
            Assert.AreEqual(config.Pipeline.Steps[^1].Options!.Value.GetRawText(),
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
}
