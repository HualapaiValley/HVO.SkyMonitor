using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    public async Task SavedV12Draft_RejectsVersionSubstitutionAndExecutesExplicitlyRecompiledV13Revision()
    {
        const string previous = "projected-scene-presentation-v12-plain-circle-markers";
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-saved-v12-execution");
        try
        {
            using var provider = CreateProvider(root);
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var config = CreateProjectedSceneConfiguration();
            config = config with
            {
                Rig = config.Rig with { Sensor = config.Rig.Sensor with { WidthPixels = 64, HeightPixels = 64 } },
                Pipeline = new CapturePipelineConfig([
                    new CaptureProcessingStepConfig("ProjectedScene", "projected-scene", 10, DependsOn: ["$raw"]),
                    new CaptureProcessingStepConfig("ScenePresentationLayer", "overlay", 20,
                        JsonSerializer.SerializeToElement(new { constellationLineThickness = 5 }), ["projected-scene"])
                ])
            };
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var graph = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateGraph(config);
            byte[] savedBytes;
            try
            {
                savedBytes = Encoding.UTF8.GetBytes(graph.SharedPlan!.CanonicalDefinition.GetRawText()
                    .Replace(PresentationLayerProducers.SceneProducerVersion, previous, StringComparison.Ordinal));
            }
            finally
            {
                graph.DisposeSteps();
            }

            // This is a saved portable draft, not a pipeline whose alias silently selects the installed implementation.
            var saved = ProcessingGraphJson.Parse(savedBytes).Definition!;
            Assert.IsEmpty(ProcessingNodeOptionsCatalog.BuiltIn.Validate(saved));
            var declared = ProcessingGraphCompiler.Compile(saved).Plan!;
            Assert.AreEqual(previous, declared.Nodes.Single(static n => n.Definition.Id == "overlay").Definition.StepVersion);
            var now = DateTimeOffset.UtcNow;
            var proposal = new ProcessingGraphDeliveryProposalV1(
                ProcessingGraphDeliverySchemaVersions.Current, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), initial.ActiveRevisionId,
                operations.Capabilities.IdentitySha256, declared.DefinitionIdentitySha256, declared.PlanIdentitySha256,
                saved, now.AddMinutes(-1), now.AddHours(1));
            await operations.StageAsync(proposal, CancellationToken.None).ConfigureAwait(false);
            var rejected = await operations.ReadPendingFactAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingGraphDeliveryFactKind.Rejected, rejected!.Kind,
                "The installed v13 producer must not execute a delivered v12 plan under its old identity.");
            Assert.AreEqual(initial.ActiveRevisionId, (await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevisionId);
            await operations.SupersedeProposalAsync(proposal.ProposalId, CancellationToken.None).ConfigureAwait(false);

            // Explicitly rebuilding the saved options as a NEW local draft binds the currently installed version.
            // Validation/activation is required; decoding the old descriptor alone never migrates or executes it.
            var migratedPipeline = new CapturePipelineConfig(saved.Nodes.Select(static node => new CaptureProcessingStepConfig(
                node.StepAlias, node.Id, node.Order, node.EffectiveOptions,
                node.Dependencies.Select(static dependency => dependency.ProducerId).ToArray(),
                node.FailurePolicy == ProcessingGraphNodeFailurePolicy.Required, node.Enabled)).ToArray());
            var draft = await operations.CreateRevisionAsync("saved-scene", "explicit-v13", migratedPipeline,
                "saved-v12-create", "owner-test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingGraphRevisionLifecycle.Draft, draft.Lifecycle);
            var snapshot = await operations.ReadRevisionSnapshotAsync(draft.RevisionId, CancellationToken.None).ConfigureAwait(false);
            var migrated = ProcessingGraphJson.Parse(snapshot!.DefinitionJson).Definition!;
            Assert.AreEqual(PresentationLayerProducers.SceneProducerVersion,
                migrated.Nodes.Single(static node => node.Id == "overlay").StepVersion);
            Assert.AreNotEqual(declared.PlanIdentitySha256, snapshot.State.SharedPlanIdentitySha256);
            await operations.ValidateRevisionAsync(draft.RevisionId, "saved-v12-validate", "owner-test", null,
                CancellationToken.None).ConfigureAwait(false);
            await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "saved-v12-activate", "owner-test", null,
                CancellationToken.None).ConfigureAwait(false);

            var executed = await CaptureConstellationsAsync(provider, config, 0).ConfigureAwait(false);
            Assert.AreEqual(draft.RevisionId, executed.RevisionId);
            Assert.IsNotEmpty(executed.Payload.Segments);
            Assert.IsTrue(executed.Payload.Segments.All(static segment => segment.Thickness == 5));
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            foreach (var artifactId in new[] { executed.ArtifactId, executed.AnnotationArtifactId })
            {
                var retained = await store.ReadOutputByArtifactIdAsync(artifactId, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(PresentationLayerProducers.SceneProducerVersion, retained!.Artifact.Recipe.ImplementationVersion);
            }
            Assert.IsNotEmpty(executed.Annotations.Markers, "The executed scene must contain stellar predictions.");
            Assert.IsEmpty(executed.Annotations.TextBlocks, "Unmeasured saved-draft stars must not regain historical v12 labels.");
            CollectionAssert.AreEqual(savedBytes, ProcessingGraphJson.SerializeCanonical(saved));
            Assert.AreEqual(declared.PlanIdentitySha256, ProcessingGraphCompiler.Compile(saved).Plan!.PlanIdentitySha256);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
