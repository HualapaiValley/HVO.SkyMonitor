using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    [DataRow("upload", true)]
    [DataRow("secondary", true)]
    [DataRow("upload", false)]
    [DataRow("secondary", false)]
    public async Task SecondaryCaptureLanes_ClaimRetryAndCompletionPreserveGraphOwnership(string laneName, bool standardFirst)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-secondary-execution-ownership");
        try
        {
            var clock = new MutableTimeProvider(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero));
            using var provider = CreateSecondaryLaneProvider(root, clock);
            var configuration = SecondaryLaneGraphConfiguration();
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            var receipt = await ingress.AcceptAsync(configuration,
                CreateSubmission(capturedUtc: clock.GetUtcNow().AddMinutes(-1)), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            var executionId = await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId,
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(executionId);
            if (standardFirst) { await CompleteLiveAsync(provider, configuration).ConfigureAwait(false); }
            var expected = await operations.ReadExecutionDetailAsync(executionId.Value, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(expected);
            Assert.AreEqual(standardFirst ? ProcessingGraphExecutionStatus.Completed : ProcessingGraphExecutionStatus.Pending,
                expected.Execution.Status);

            var store = provider.GetRequiredService<ICaptureLaneStore>();
            var lane = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(item => item.Name == laneName);
            Assert.IsTrue(lane.Enabled);
            var lease = await store.ClaimAsync(lane, "secondary-owner", configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            Assert.IsNull(lease.Context.Execution);
            await AssertSecondaryGraphUnchangedAsync(operations, expected).ConfigureAwait(false);
            await store.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
            await AssertSecondaryGraphUnchangedAsync(operations, expected).ConfigureAwait(false);
            lease = await store.ClaimAsync(lane, "secondary-reclaimed", configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            await store.FailAsync(lease, CaptureLaneHandlerResult.Retry("secondary-retry"), CancellationToken.None).ConfigureAwait(false);
            await AssertSecondaryGraphUnchangedAsync(operations, expected).ConfigureAwait(false);
            clock.Advance(TimeSpan.FromSeconds(2));
            lease = await store.ClaimAsync(lane, "secondary-retried", configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            await AssertSecondaryGraphUnchangedAsync(operations, expected).ConfigureAwait(false);
            await store.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            await AssertSecondaryGraphUnchangedAsync(operations, expected).ConfigureAwait(false);

            if (!standardFirst) { await CompleteLiveAsync(provider, configuration).ConfigureAwait(false); }
            var completed = await operations.ReadExecutionDetailAsync(executionId.Value, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(completed);
            Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, completed.Execution.Status);
            Assert.IsNotNull(completed.Execution.CompletedUtc);
            Assert.IsNull(await operations.ReadOldestActiveExecutionKeyAsync(CancellationToken.None).ConfigureAwait(false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow("upload", true)]
    [DataRow("secondary", true)]
    [DataRow("upload", false)]
    [DataRow("secondary", false)]
    public async Task SecondaryCaptureLanes_InvalidEvidenceCannotFailAnotherLanesGraph(string laneName, bool standardFirst)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-secondary-invalid-evidence");
        try
        {
            using var provider = CreateSecondaryLaneProvider(root);
            var configuration = SecondaryLaneGraphConfiguration();
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            var receipt = await ingress.AcceptAsync(configuration,
                CreateSubmission(), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            var executionId = await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId,
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(executionId);
            if (standardFirst) { await CompleteLiveAsync(provider, configuration).ConfigureAwait(false); }
            var expected = await operations.ReadExecutionDetailAsync(executionId.Value, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(expected);
            var store = provider.GetRequiredService<ICaptureLaneStore>();
            var lane = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(item => item.Name == laneName);
            Assert.IsTrue(lane.Enabled);
            var payload = receipt.StoredFrame.AbsolutePath;
            var backup = payload + ".test-backup";
            File.Move(payload, backup);
            try
            {
                Assert.IsNull(await store.ClaimAsync(lane, "secondary-invalid", configuration, CancellationToken.None).ConfigureAwait(false));
                await AssertSecondaryGraphUnchangedAsync(operations, expected).ConfigureAwait(false);
                var backlog = await store.ReadBacklogsAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(lane.Required ? 1L : 0L, backlog.Single(item => item.Lane == laneName).QuarantineCount);
            }
            finally { File.Move(backup, payload); }
            if (!standardFirst)
            {
                if (lane.Required)
                {
                    // Restored bytes still require the integrated authority's explicit audited quarantine release.
                    var evidence = provider.GetRequiredService<IRawEvidenceOperations>();
                    var item = (await evidence.ReadEvidencePageAsync(10, null, CancellationToken.None).ConfigureAwait(false))
                        .Items.Single(item => item.Target.CaptureId == receipt.Manifest.Descriptor.Capture.CaptureId);
                    Assert.AreEqual(receipt.CommittedManifestSha256, item.Target.ManifestSha256);
                    Assert.AreEqual("quarantined", item.Target.State);
                    Assert.AreEqual("lane-evidence-invalid", item.Target.FailureReason);
                    var released = await evidence.ReleaseEvidenceAsync(item.Target, RawEvidenceReleaseAction.Revalidate,
                        "secondary-evidence-revalidation", "secondary-lane-test", "evidence-restored", CancellationToken.None).ConfigureAwait(false);
                    Assert.IsFalse(released.Duplicate);
                    Assert.AreEqual(RawEvidenceReleaseAction.Revalidate, released.Audit.Action);
                    Assert.AreEqual("secondary-lane-test", released.Audit.Actor);
                    Assert.AreEqual("evidence-restored", released.Audit.Reason);
                    await AssertSecondaryGraphUnchangedAsync(operations, expected).ConfigureAwait(false);
                }
                await CompleteLiveAsync(provider, configuration).ConfigureAwait(false);
                Assert.IsNull(await operations.ReadOldestActiveExecutionKeyAsync(CancellationToken.None).ConfigureAwait(false));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ServiceProvider CreateSecondaryLaneProvider(string root, TimeProvider? clock = null)
        => CreateProvider(root, new Dictionary<string, string?>
        {
            ["CameraAgent:CaptureDistribution:UploadEnabled"] = "true",
            ["CameraAgent:CaptureDistribution:SecondaryLanes:0:Name"] = "secondary",
            ["CameraAgent:CaptureDistribution:SecondaryLanes:0:Enabled"] = "true"
        }, clock);

    private static CameraModuleConfig SecondaryLaneGraphConfiguration()
        => CreateConfiguration() with
        {
            Pipeline = new CapturePipelineConfig(
                [new CaptureProcessingStepConfig("Preview", "preview", DependsOn: ["$raw"])],
                CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent)
        };

    private static async Task AssertSecondaryGraphUnchangedAsync(ProcessingGraphOperationsCoordinator operations,
        ProcessingGraphExecutionDetail expected)
    {
        var actual = await operations.ReadExecutionDetailAsync(expected.Execution.ExecutionId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(actual);
        Assert.AreEqual(expected.Execution, actual.Execution, "A nonstandard lane cannot change graph status, timing, attempts or failure state.");
        Assert.AreEqual(JsonSerializer.Serialize(expected.Nodes), JsonSerializer.Serialize(actual.Nodes),
            "A nonstandard lane cannot change node attempts, lineage or publication.");
        var active = await operations.ReadOldestActiveExecutionKeyAsync(CancellationToken.None).ConfigureAwait(false);
        if (expected.Execution.Status == ProcessingGraphExecutionStatus.Completed) { Assert.IsNull(active); }
        else { Assert.IsNotNull(active, "Actual unfinished standard work must remain an export barrier."); }
    }
}
