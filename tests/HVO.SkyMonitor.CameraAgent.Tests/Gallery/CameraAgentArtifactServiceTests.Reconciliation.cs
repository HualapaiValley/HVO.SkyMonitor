using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Gallery;

public sealed partial class CameraAgentArtifactServiceTests
{
    [TestMethod]
    [DataRow(16, false)]
    [DataRow(128, false)]
    [DataRow(16, true)]
    public Task Reconciliation_AllowsCaptureStorageAndArtifactReadBeforeNextProduct(int productCount, bool pendingCopies)
        => AssertConcurrentReconciliationAsync(productCount, wholePassControl: false, pendingCopies);

    [TestMethod]
    public Task Reconciliation_WholePassNegativeControlExceedsBudget()
        => AssertConcurrentReconciliationAsync(128, wholePassControl: true);

    private static async Task AssertConcurrentReconciliationAsync(int productCount, bool wholePassControl, bool pendingCopies = false)
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync().ConfigureAwait(false);
        for (var index = 0; index < productCount; index++)
            _ = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Preview, variant: $"reconciliation-{index}").ConfigureAwait(false);
        if (pendingCopies)
        {
            var row = (await fixture.ProcessingStore.ReadProcessingEvidencePageAsync(null, 1, CancellationToken.None)
                .ConfigureAwait(false)).Items.Single();
            var destinationRelative = $"processing-quarantine/recovery-{Guid.NewGuid():N}";
            var destination = Path.Combine(fixture.Root, destinationRelative);
            Directory.CreateDirectory(destination);
            foreach (var relative in new[] { row.PayloadRelativePath, row.SidecarRelativePath })
                File.Copy(Path.Combine(fixture.Root, relative), Path.Combine(destination, Path.GetFileName(relative)));
            await fixture.ProcessingStore.PlanLifecycleOperationAsync(new(
                $"quarantine:{Guid.NewGuid():N}", "quarantine", row.OutputIdentitySha256,
                row.PayloadRelativePath, row.SidecarRelativePath, destinationRelative, "recovery-control", 0),
                CancellationToken.None).ConfigureAwait(false);
        }
        var clock = new ReconciliationClock();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        var reconciler = new DerivedProductReconciler(fixture.Root, fixture.ProcessingStore,
            new DerivedProductLifecycleOptions { ReconciliationBatchSize = productCount }, clock,
            observationCompleted: async token =>
            {
                if (!first) return;
                first = false;
                reached.SetResult();
                await resume.Task.WaitAsync(token).ConfigureAwait(false);
            });
        var lifecycle = StorageLifecycleLock.ForRoot(fixture.Root);
        var pass = reconciler.RunAsync(CancellationToken.None).AsTask();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        // Negative control models the old whole-pass ownership at the exact same observation boundary.
        var expectBudgetViolation = wholePassControl;
        if (wholePassControl) await lifecycle.WaitAsync().ConfigureAwait(false);
        var started = clock.GetTimestamp();
        var capture = StoreDuringReconciliationAsync(fixture, raw);
        var read = fixture.Service.OpenContentAsync(raw.ArtifactId, CancellationToken.None).AsTask();
        try
        {
            if (wholePassControl)
            {
                Assert.IsFalse(capture.IsCompleted);
                Assert.IsFalse(read.IsCompleted);
                clock.Advance(StorageLifecycleTelemetry.WaitBudget + TimeSpan.FromMilliseconds(1));
                Assert.IsGreaterThan(StorageLifecycleTelemetry.WaitBudget, clock.GetElapsedTime(started),
                    "The old whole-pass hold must violate the same bound.");
                lifecycle.Release();
                wholePassControl = false;
            }
            await capture.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var result = await read.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Found, result.Status);
            var content = result.Content!;
            await using (content.ConfigureAwait(false))
            {
                CollectionAssert.AreEqual(raw.Payload, await ReadAllAsync(content).ConfigureAwait(false));
            }
            Assert.AreEqual(expectBudgetViolation, clock.GetElapsedTime(started) > StorageLifecycleTelemetry.WaitBudget);
            Assert.IsFalse(pass.IsCompleted, "Callers must finish before reconciliation proceeds to the next product.");
        }
        finally
        {
            if (wholePassControl) lifecycle.Release();
            resume.TrySetResult();
            _ = await pass.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Reconciliation_DefersChangedOrRemovedProductInsteadOfApplyingStaleValidation(bool remove)
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var raw = await fixture.AddRawAsync().ConfigureAwait(false);
        var output = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Preview).ConfigureAwait(false);
        // Make the observed version invalid, then repair or remove it before quarantine can be planned.
        await File.WriteAllBytesAsync(output.PayloadPath, new byte[] { 0 }).ConfigureAwait(false);
        var first = true;
        var reconciler = new DerivedProductReconciler(fixture.Root, fixture.ProcessingStore,
            observationCompleted: async token =>
            {
                if (!first) return;
                first = false;
                var gate = StorageLifecycleLock.ForRoot(fixture.Root);
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (remove)
                    {
                        File.Delete(output.PayloadPath);
                        File.Delete(output.SidecarPath);
                        await fixture.ExecuteAsync("DELETE FROM processing_outputs;").ConfigureAwait(false);
                    }
                    else
                    {
                        var replacement = output.PayloadPath + ".replacement";
                        await File.WriteAllBytesAsync(replacement, output.Payload, token).ConfigureAwait(false);
                        File.Move(replacement, output.PayloadPath, overwrite: true);
                    }
                }
                finally { gate.Release(); }
            });
        var summary = await reconciler.RunAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, summary.Quarantined);
        Assert.AreEqual(0, summary.Missing);
        Assert.AreEqual(0, summary.QuarantineBytes);
        if (!remove)
        {
            CollectionAssert.AreEqual(output.Payload, await File.ReadAllBytesAsync(output.PayloadPath).ConfigureAwait(false));
            var next = await new DerivedProductReconciler(fixture.Root, fixture.ProcessingStore)
                .RunAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, next.Available);
        }
    }

    private static async Task StoreDuringReconciliationAsync(ArtifactFixture fixture, StoredArtifact raw)
    {
        var descriptor = raw.Manifest.Descriptor;
        var frame = new CameraFrame(descriptor.Timing.ExposureStartedUtc, 2, 2, CameraPixelFormat.Mono16,
            raw.Payload, new FrameMetadata(descriptor.Controls.EffectiveExposure, descriptor.Controls.EffectiveGain,
                descriptor.Controls.EffectiveTemperatureC ?? double.NaN, "lock-bound-test", Offset: descriptor.Controls.EffectiveOffset), 4);
        var config = ProcessingConformanceFixture.CameraConfig with
        {
            AgentId = "lock-bound-test",
            Pipeline = new CapturePipelineConfig([], CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent)
        };
        var receipt = new RawCaptureReceipt(RawIngressOutcome.Committed, raw.Manifest,
            new StoredFrameReference(raw.Manifest.RelativeArtifactPath, raw.PayloadPath, frame.TimestampUtc, FrameArtifactRole.Raw),
            CaptureContractJson.ComputeManifestSha256(raw.Manifest));
        var context = new CaptureProcessingContext(config, new CaptureLoopSubmission(
            new CaptureRequest(frame.TimestampUtc, frame.Metadata.Exposure, CaptureMode.Still),
            new CaptureResult(frame, new CaptureSetpoint(frame.Metadata.Exposure, frame.Metadata.Gain, null, null), TimeSpan.Zero, CaptureMode.Still, false),
            frame.TimestampUtc, frame.Metadata.Exposure, TimeSpan.Zero), receipt);
        context.BeginNode("producer", ["$raw"]);
        var source = context.Artifacts!.Raw;
        var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
            "lock-bound", "1.0.0", "lock-bound-v1", System.Text.Json.JsonSerializer.SerializeToElement(new { })));
        var sources = new[] { descriptor.Artifact.ArtifactId };
        var product = new ProcessingProduct(FrameArtifactRole.Preview, "lock-bound",
            ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Preview, "lock-bound", recipe.IdentitySha256, sources),
            "application/x-hvo-linear-frame", descriptor.Layout, raw.Payload, ProcessingIdentity.ComputePayloadSha256(raw.Payload),
            recipe, [new ProcessingAlgorithmIdentity("lock-bound", "v1")], sources, frame.Metadata.Exposure,
            CameraAgentRecipeExecutionAdapter.CreateArtifact(config, source, "source").Compatibility);
        var artifact = context.AddDerivative(FrameArtifactRole.Preview, frame, "lock-bound", sources,
            CaptureProcessingContext.CreateArtifactId(product.OutputIdentitySha256));
        context.AssociateProcessingProduct(artifact, product);
        context.BeginNode("storage", ["producer"]);
        using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        var step = new FileStorageCaptureProcessingStep(new("storage", "Storage", 100),
            new FileStorageCaptureProcessingStepOptions { StorageRoot = fixture.Root, UpdateLatestFrame = false, QueueForUpload = false },
            Mock.Of<ILatestFrameAccessor>(), storage, Mock.Of<IArtifactOutbox>(),
            Options.Create(new CameraAgentHostOptions()), NullLogger<FileStorageCaptureProcessingStep>.Instance);
        await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
        var sidecar = Directory.EnumerateFiles(Path.Combine(fixture.Root, "frames"), "*.json", SearchOption.AllDirectories)
            .Single(path => !string.Equals(path, raw.SidecarPath, StringComparison.Ordinal));
        var manifest = CaptureContractJson.ParseManifest(await File.ReadAllBytesAsync(sidecar).ConfigureAwait(false)).Document?.Manifest;
        Assert.IsNotNull(manifest, "The real storage step must publish a committed manifest while reconciliation is paused.");
        Assert.AreEqual(FrameArtifactRole.Preview, manifest.Descriptor.Artifact.Role);
        Assert.AreEqual(raw.ChecksumSha256, manifest.Descriptor.Artifact.ChecksumSha256);
        CollectionAssert.AreEqual(raw.Payload, await File.ReadAllBytesAsync(
            Path.Combine(fixture.Root, manifest.RelativeArtifactPath)).ConfigureAwait(false));
    }

    private sealed class ReconciliationClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }
}
