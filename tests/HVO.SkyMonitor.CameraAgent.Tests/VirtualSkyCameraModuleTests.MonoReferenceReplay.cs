using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    private static async Task<byte[]> ReadReferenceReplayPayloadAsync(ServiceProvider provider, string root, Guid artifactId)
    {
        var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
        var row = await store.ReadOutputByArtifactIdAsync(artifactId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(row);
        return await File.ReadAllBytesAsync(Path.Combine(root, row.PayloadRelativePath)).ConfigureAwait(false);
    }

    private sealed record MonoReferenceReplayWitness(ReconstructionDescriptor Raw, SyntheticCalibrationModelV1 Replacement,
        ProcessingGraphExecutionDetail Execution, byte[] CalibratedBytes);

    private static CameraModuleConfig WithReplacementReferences(CameraModuleConfig profile, SyntheticCalibrationModelV1 replacement)
        => profile with
        {
            Pipeline = profile.Pipeline with
            {
                Steps = profile.Pipeline.Steps.Select(step => step.Id == "linear" ? step with
                {
                    Options = JsonSerializer.SerializeToElement(new
                    {
                        strategy = "SyntheticReferences",
                        outputVariant = "qualified-linear",
                        syntheticCalibration = replacement
                    })
                } : step).ToArray()
            }
        };

    private async Task AssertMonoReferenceReplayAsync(ServiceProvider restarted, CameraModuleConfig profile,
        MonoReferenceReplayWitness witness, ExposureMeasurementV1 measurement, string root)
    {
        var (raw, replacement, original, calibratedBytes) = witness;
        var token = CancellationToken.None;
        var replacementLight = raw with
        {
            Profiles = raw.Profiles with
            {
                Calibration = raw.Profiles.Calibration with
                {
                    Sha256 = SyntheticCalibrationReferenceGenerator.ComputeModelIdentitySha256(replacement)
                }
            }
        };
        var references = restarted.GetRequiredService<SyntheticCalibrationReferenceStore>();
        var replacementBundle = await references.GetOrCreateAsync(replacementLight, replacement, token).ConfigureAwait(false);

        var live = original.Execution;
        var originalLinear = Assert.ContainsSingle(original.Nodes.Single(node => node.NodeId == "linear").Outputs);
        Assert.HasCount(5, originalLinear.SourceArtifactIds);
        Assert.AreEqual(live.PrimaryArtifactId, originalLinear.SourceArtifactIds[0]);
        Assert.HasCount(4, replacementBundle.References);
        Assert.IsFalse(replacementBundle.References.Values.Any(reference => originalLinear.SourceArtifactIds.Contains(reference.ArtifactId)));

        var graphs = restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>();
        var registry = await graphs.EnsureConfiguredBasicAsync(profile, token).ConfigureAwait(false);
        Assert.AreNotEqual(live.GraphRevisionId, registry.ConfiguredBasicRevisionId);

        var submitted = await graphs.SubmitReplayAsync(new(live.CaptureId, live.GraphRevisionId, live.PrimaryArtifactId),
            "mono-reference-replay", "measurement-test", token).ConfigureAwait(false);
        var replay = await ProcessingGraphOperationsTests.RunReplayToCompletionAsync(restarted, submitted.Execution.ExecutionId)
            .ConfigureAwait(false);
        Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, replay.Execution.Status, replay.Execution.FailureReason);
        Assert.AreNotEqual(live.ExecutionId, replay.Execution.ExecutionId);
        Assert.AreEqual(live.CaptureId, replay.Execution.CaptureId);
        Assert.AreEqual(live.PrimaryArtifactId, replay.Execution.PrimaryArtifactId);
        Assert.AreEqual(live.GraphRevisionId, replay.Execution.GraphRevisionId);
        Assert.AreEqual(live.GraphDefinitionIdentitySha256, replay.Execution.GraphDefinitionIdentitySha256);
        Assert.AreEqual(live.SharedPlanIdentitySha256, replay.Execution.SharedPlanIdentitySha256);
        Assert.AreEqual(live.LocalPlanIdentitySha256, replay.Execution.LocalPlanIdentitySha256);

        var outputIds = new List<Guid>();
        foreach (var id in new[] { "linear", ExposureMeasurementProducts.StepAlias })
        {
            var liveNode = original.Nodes.Single(node => node.NodeId == id);
            var replayNode = replay.Nodes.Single(node => node.NodeId == id);
            Assert.AreEqual("Completed", replayNode.Status, replayNode.Reason);
            Assert.AreEqual(liveNode.PlanSha256, replayNode.PlanSha256);
            var expected = Assert.ContainsSingle(liveNode.Outputs);
            var actual = Assert.ContainsSingle(replayNode.Outputs);
            Assert.IsFalse(actual.Published);
            Assert.AreEqual(expected.ArtifactId, actual.ArtifactId);
            Assert.AreEqual(expected.OutputIdentitySha256, actual.OutputIdentitySha256);
            Assert.AreEqual(expected.PayloadSha256, actual.PayloadSha256);
            CollectionAssert.AreEqual(expected.SourceArtifactIds.ToArray(), actual.SourceArtifactIds.ToArray());
            outputIds.Add(actual.ArtifactId);
        }

        var replayedLinear = await ReadReferenceReplayPayloadAsync(restarted, root, outputIds[0]).ConfigureAwait(false);
        CollectionAssert.AreEqual(calibratedBytes, replayedLinear);
        var ui = MeasurementOperations(restarted, graphs);
        var result = await ui.GetExecutionDetailAsync(replay.Execution.ExecutionId, token).ConfigureAwait(false);
        Assert.IsTrue(result.IsSuccess, result.Message);
        var archived = result.Value?.ExposureMeasurement;
        Assert.IsNotNull(archived?.Measurement, archived?.UnavailableReason);
        var replayedMeasurement = archived.Measurement;
        Assert.AreEqual(JsonSerializer.Serialize(measurement), JsonSerializer.Serialize(replayedMeasurement));
        TestContext.WriteLine(JsonSerializer.Serialize(new
        {
            live = live.ExecutionId,
            replay = replay.Execution.ExecutionId,
            live.CaptureId,
            live.PrimaryArtifactId,
            live.GraphRevisionId,
            live.SharedPlanIdentitySha256,
            live.LocalPlanIdentitySha256,
            replacementGraph = registry.ConfiguredBasicRevisionId,
            replacementReferences = replacementBundle.References.Values.Select(artifact => artifact.ArtifactId),
            sourceIds = originalLinear.SourceArtifactIds,
            calibratedSha256 = originalLinear.PayloadSha256,
            measuredArtifactId = outputIds[1],
            replayedMeasurement.Sources.Status,
            replayedMeasurement.Sources.Reason,
            replayedMeasurement.Sources.Detections,
            replayedMeasurement.Hfd
        }));
    }
}
