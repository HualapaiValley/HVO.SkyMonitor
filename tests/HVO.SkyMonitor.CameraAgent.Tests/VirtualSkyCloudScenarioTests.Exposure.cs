using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCloudScenarioTests
{
    [TestMethod]
    public async Task ObservationStep_RetainedAcquisitionTimeCanDifferFromModeledRequestTime()
    {
        var config = Config(CameraPixelFormat.Mono16, Definition()) with { AgentId = "cloud-timing-fixture" };
        var clock = new ExposureTestTimeProvider(FixtureUtc.AddSeconds(5), true);
        var module = new VirtualSkyCameraModule(clock, new InMemoryCelestialCatalog([]), new ProjectedSceneStore());
        await using var moduleLifetime = module.ConfigureAwait(false);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var requested = FixtureUtc.AddTicks(1234);
        var exposure = TimeSpan.FromSeconds(4);
        var request = new CaptureRequest(requested, TimeSpan.FromSeconds(5), CaptureMode.Still, new(exposure, 1, null, null));
        var result = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var frame = result.Frame!;
        var submission = new CaptureLoopSubmission(request, result, requested, request.TargetInterval, TimeSpan.Zero);
        var descriptor = RawCaptureDescriptorFactory.Create(config, submission,
            new(config.AgentId!, 1, Guid.NewGuid(), Guid.NewGuid()), ProcessingIdentity.ComputePayloadSha256(frame.PixelData),
            clock.GetUtcNow().AddMilliseconds(20));
        Assert.IsTrue(FrameReconstructor.TryReconstruct(descriptor, frame.PixelData, out var restored).IsValid);
        Assert.IsNotNull(restored);
        restored = restored with { Metadata = restored.Metadata with { Scene = frame.Metadata.Scene } };
        Assert.AreEqual(descriptor.Timing.ExposureStartedUtc, restored.TimestampUtc);
        Assert.AreNotEqual(requested.ToUnixTimeMilliseconds(), restored.TimestampUtc.ToUnixTimeMilliseconds());
        var manifest = new ArtifactManifestV2(ArtifactManifestV2.CurrentSchemaVersion, descriptor, "cloud-raw.bin", frame.Metadata.Scene);
        var receipt = new RawCaptureReceipt(RawIngressOutcome.Committed, manifest,
            new StoredFrameReference("cloud-raw.bin", Path.Combine(Path.GetTempPath(), "cloud-raw.bin"),
                descriptor.Timing.ExposureStartedUtc, FrameArtifactRole.Raw), CaptureContractJson.ComputeManifestSha256(manifest));
        // Startup fallback envelopes recover the millisecond request from the descriptor.
        var retained = submission with
        {
            Request = request with { RequestedStartUtc = descriptor.Timing.RequestedStartUtc },
            Result = result with { Frame = restored }
        };
        var publisher = new RecordingPublisher();
        var step = new VirtualSkyCloudObservationProcessingStep(
            new CaptureProcessingStepMetadata("CloudObservation", typeof(VirtualSkyCloudObservationProcessingStep).FullName!, 10),
            new VirtualSkyCloudObservationProcessingStepOptions(), publisher);
        await step.ProcessAsync(new(config, retained, receipt), CancellationToken.None).ConfigureAwait(false);
        var fact = Assert.ContainsSingle(publisher.Facts);
        Assert.AreEqual(requested, fact.ObservedFromUtc);
        Assert.AreEqual(requested + exposure, fact.ObservedThroughUtc);
        var corrupted = retained with
        {
            Result = retained.Result with
            { Frame = restored with { TimestampUtc = restored.TimestampUtc.AddSeconds(1) } }
        };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            step.ProcessAsync(new(config, corrupted, receipt), CancellationToken.None).AsTask()).ConfigureAwait(false);
        Assert.HasCount(1, publisher.Facts);
    }
}
