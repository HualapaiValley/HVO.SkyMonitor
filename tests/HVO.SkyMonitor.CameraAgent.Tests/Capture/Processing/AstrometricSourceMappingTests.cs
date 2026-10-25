using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
public sealed class AstrometricSourceMappingTests
{
    [TestMethod]
    [DataRow("single", true)]
    [DataRow("alternate-first", false)]
    [DataRow("alternate-last", false)]
    [DataRow("duplicate", false)]
    [DataRow("missing", false)]
    [DataRow("foreign-science", false)]
    [DataRow("preview-integration", false)]
    [DataRow("parent-integration", false)]
    [DataRow("parent-timing", false)]
    [DataRow("parent-exposure", false)]
    [DataRow("unavailable-parent", false)]
    [DataRow("unknown-producer", false)]
    [DataRow("calibration-masters", true)]
    [DataRow("calibration-master-is-source", false)]
    public async Task SingleExposureMappingRequiresCompleteUnambiguousScienceLineage(string scenario, bool expected)
    {
        var (source, rolling, preview) = await CreateMappingInputsAsync().ConfigureAwait(false);
        var foreign = Guid.NewGuid();
        var descriptor = rolling.Descriptor!;
        rolling = scenario switch
        {
            "alternate-first" => Relink(rolling, rolling.ArtifactId, foreign, source.Artifact.ArtifactId),
            "alternate-last" => Relink(rolling, rolling.ArtifactId, source.Artifact.ArtifactId, foreign),
            "duplicate" => Relink(rolling, rolling.ArtifactId, source.Artifact.ArtifactId, source.Artifact.ArtifactId),
            "foreign-science" => Relink(rolling, rolling.ArtifactId, foreign),
            "parent-integration" => rolling with { TotalIntegration = TimeSpan.FromSeconds(2) },
            "parent-timing" => rolling with { Descriptor = descriptor with { Timing = descriptor.Timing with { ExposureEndedUtc = descriptor.Timing.ExposureEndedUtc.AddMilliseconds(1) } } },
            "parent-exposure" => rolling with { Descriptor = descriptor with { Controls = descriptor.Controls with { EffectiveExposure = TimeSpan.FromSeconds(2) } } },
            "unavailable-parent" => rolling with { AvailabilityState = "Unavailable" },
            "unknown-producer" => rolling with { Algorithms = [new("linear-mean", "unknown")] },
            _ => rolling
        };
        if (scenario.StartsWith("calibration-", StringComparison.Ordinal))
        {
            rolling = Relink(rolling, rolling.ArtifactId, scenario == "calibration-masters" ? source.Artifact.ArtifactId : foreign,
                scenario == "calibration-masters" ? foreign : source.Artifact.ArtifactId);
            rolling = rolling with
            {
                Descriptor = rolling.Descriptor! with
                {
                    Artifact = rolling.Artifact with
                    {
                        Role = FrameArtifactRole.Calibrated,
                        Recipe = RecipeIdentityDescriptor.Create(BuiltInProcessingRecipes.ReferenceCalibration, "1.0.0",
                            "reference-calibration-linear16-v2", JsonSerializer.SerializeToElement(new { }))
                    }
                },
                Algorithms = [new("linear16-reference-calibration", Linear16ReferenceCalibration.AlgorithmVersion),
                    new("calibration-normalization", CalibrationMasterBuilder.NormalizationAlgorithmVersion)]
            };
        }
        if (scenario == "preview-integration") preview = preview with { TotalIntegration = TimeSpan.FromSeconds(2) };
        var reads = 0;
        var result = await CaptureProcessingPersistence.HasSingleExposureAstrometricMappingAsync(preview, source, (id, _) =>
        {
            reads++;
            return ValueTask.FromResult<DurableProcessingOutput?>(scenario != "missing" && id == rolling.ArtifactId ? rolling : null);
        }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(expected, result, scenario);
        Assert.IsLessThanOrEqualTo(CaptureProcessingPersistence.MaximumLineageHops, reads);
    }

    [TestMethod]
    [DataRow(7, false, true)]
    [DataRow(8, false, false)]
    [DataRow(2, true, false)]
    public async Task SingleExposureMappingBoundsEveryScienceEdgeAndRejectsCycles(int rollingCount, bool cycle, bool expected)
    {
        var (source, rolling, preview) = await CreateMappingInputsAsync().ConfigureAwait(false);
        var lineage = new Dictionary<Guid, DurableProcessingOutput>();
        var previous = source.Artifact.ArtifactId;
        for (var i = 0; i < rollingCount; i++)
        {
            var next = Relink(rolling, Guid.NewGuid(), previous);
            lineage.Add(next.ArtifactId, next);
            previous = next.ArtifactId;
        }
        if (cycle)
        {
            var first = lineage.Values.First();
            lineage[first.ArtifactId] = Relink(first, first.ArtifactId, previous);
        }
        preview = Relink(preview, preview.ArtifactId, previous);
        var reads = 0;
        var result = await CaptureProcessingPersistence.HasSingleExposureAstrometricMappingAsync(preview, source, (id, _) =>
        {
            reads++;
            return ValueTask.FromResult<DurableProcessingOutput?>(lineage.GetValueOrDefault(id));
        }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(expected, result);
        Assert.IsLessThanOrEqualTo(CaptureProcessingPersistence.MaximumLineageHops, reads);
    }

    private static DurableProcessingOutput Relink(DurableProcessingOutput output, Guid id, params Guid[] sources) => output with
    {
        ArtifactId = id,
        Descriptor = output.Descriptor! with { Artifact = output.Artifact with { ArtifactId = id, SourceArtifactIds = sources } }
    };

    private static async Task<(ReconstructionDescriptor Source, DurableProcessingOutput Rolling, DurableProcessingOutput Preview)> CreateMappingInputsAsync()
    {
        var bytes = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var source = HVO.SkyMonitor.TestSupport.ArtifactManifestFixture.CreateManifest(CameraPixelFormat.Mono16, 4, 4, 8, bytes).Descriptor;
        Assert.IsTrue(source.Validate().IsValid);
        var raw = new ProcessingArtifact(source.Artifact.ArtifactId, FrameArtifactRole.Raw, source.Artifact.Variant,
            ProcessingIdentity.CreateRecipeIdentity(source.Artifact.Recipe).IdentitySha256, source.Artifact.MediaType,
            source.Layout, bytes, source.Artifact.CreatedUtc, source.Controls.EffectiveExposure,
            CameraAgentRecipeExecutionAdapter.CreateCompatibility(source), source.Capture.CaptureSequence, source.Artifact.SourceArtifactIds,
            source.Timing.ExposureStartedUtc, source.Timing.ExposureEndedUtc)
        { CaptureId = source.Capture.CaptureId };
        var executor = new ProcessingRecipeExecutor();
        var rolling = (await executor.ExecuteAsync(new(BuiltInProcessingRecipes.RollingMean,
            JsonSerializer.SerializeToElement(new RollingMeanOptions(2)), ProcessingInputSelector.Raw(), [raw], "rolling-mean")).ConfigureAwait(false)).Products.Single();
        var artifact = new ProcessingArtifact(ProcessingIdentity.CreateArtifactId(rolling.OutputIdentitySha256), rolling.Role, rolling.Variant,
            rolling.Recipe.IdentitySha256, rolling.MediaType, rolling.Layout, rolling.Payload, raw.CreatedUtc, rolling.TotalIntegration,
            rolling.Compatibility, raw.CaptureSequence, rolling.SourceArtifactIds, raw.ObservationStartedUtc, raw.ObservationEndedUtc)
        { CaptureId = raw.CaptureId };
        var preview = (await executor.ExecuteAsync(new(BuiltInProcessingRecipes.EncodedPreview,
            JsonSerializer.SerializeToElement(new EncodedPreviewOptions(OutputEncoding: "Packed")), ProcessingInputSelector.Combined(), [artifact], "combined-preview")).ConfigureAwait(false)).Products.Single();
        return (source, Output(rolling), Output(preview));

        DurableProcessingOutput Output(ProcessingProduct product)
        {
            var id = ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256);
            var descriptor = DerivativeDescriptorFactory.Create(source, id, "mapping-test", product);
            return new(product.OutputIdentitySha256, id, "payload", "sidecar", [], descriptor, null,
                product.Recipe.IdentitySha256, product.Algorithms, product.Compatibility, product.TotalIntegration, source.Capture.CaptureSequence);
        }
    }
}
