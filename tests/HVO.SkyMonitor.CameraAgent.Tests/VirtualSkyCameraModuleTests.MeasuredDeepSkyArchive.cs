using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    // Held-out sky direction from the pinned catalog, frozen independently of the
    // extractor. Inject only sensor bytes before raw admission. The archived
    // assessment receives no fixture data or position from this code.
    private static readonly CelestialCatalogObject HeldOutDirection = new("fixture-patch", "fixture patch",
        20.8416, 42.477694444444445, 0);

    private static async Task CompareAssessmentDuringExposureAsync(ServiceProvider provider, CameraModuleConfig config,
        ICelestialCatalog catalog, CenteredMeasuredDeepSkyScheduler enabled, TestContext evidence)
    {
        var views = provider.GetRequiredService<SqliteCenteredViews>();
        var token = evidence.CancellationToken;
        var before = await views.ReadMeasuredStackWorkAsync(0, long.MaxValue, 128, token).ConfigureAwait(false);
        Assert.ContainsSingle(before.Work);
        await MeasureAsync(null).ConfigureAwait(false);
        var afterDisabled = await views.ReadMeasuredStackWorkAsync(0, long.MaxValue, 128, token).ConfigureAwait(false);
        Assert.AreEqual(before.Work[0], Assert.ContainsSingle(afterDisabled.Work));
        await MeasureAsync(enabled).ConfigureAwait(false);
        Assert.IsEmpty((await views.ReadMeasuredStackWorkAsync(0, long.MaxValue, 128, token).ConfigureAwait(false)).Work);

        async Task MeasureAsync(CenteredMeasuredDeepSkyScheduler? worker)
        {
            // Use the same native sensor, optics, gain and integration for both real shutters.
            // The scheduler consumes the already committed separate fixture stack during this exposure.
            var deviceClock = new MeasuredDeepSkyRealClock(new DateTimeOffset(2026, 11, 19, 21, 13, 49, TimeSpan.Zero));
            var camera = new VirtualSkyCameraModule(deviceClock, catalog, new ProjectedSceneStore(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var lease = camera.ConfigureAwait(false);
            await camera.InitializeAsync(config with { CenteredExposure = null }, token).ConfigureAwait(false);
            var profile = config.Rig.Pipeline;
            var start = deviceClock.GetUtcNow().AddSeconds(1);
            var request = new CaptureRequest(start, profile.CaptureInterval, CaptureMode.Still,
                new(profile.NightExposure, profile.NightGain, null, null));
            var delay = start - deviceClock.GetUtcNow();
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            var began = Stopwatch.GetTimestamp();
            var exposure = camera.CaptureAsync(request, token);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                Assert.IsFalse(exposure.IsCompleted);
                using var process = Process.GetCurrentProcess();
                process.Refresh();
                var rssBefore = process.WorkingSet64;
                var cpuBefore = process.TotalProcessorTime;
                var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                var assessmentBegan = Stopwatch.GetTimestamp();
                if (worker is not null)
                    await worker.RunOnceAsync(token).ConfigureAwait(false);
                var assessmentElapsed = Stopwatch.GetElapsedTime(assessmentBegan);
                process.Refresh();
                var sample = new
                {
                    Enabled = worker is not null,
                    AssessmentElapsedMs = assessmentElapsed.TotalMilliseconds,
                    CpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                    AllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
                    RssBefore = rssBefore,
                    RssAfter = process.WorkingSet64,
                    process.PeakWorkingSet64,
                    ContinuedExposureWhileAssessing = !exposure.IsCompleted
                };
                var frame = await exposure.ConfigureAwait(false);
                var observed = frame.AcquisitionTiming!;
                Assert.AreEqual(profile.NightExposure, observed.ExposureEndedUtc - observed.ExposureStartedUtc);
                evidence.WriteLine(JsonSerializer.Serialize(new
                {
                    sample,
                    RequestedStart = start,
                    observed.ExposureStartedUtc,
                    observed.ExposureEndedUtc,
                    observed.ReadoutCompletedUtc,
                    EffectiveCadence = profile.CaptureInterval,
                    CaptureElapsedMs = Stopwatch.GetElapsedTime(began).TotalMilliseconds
                }));
            }
            finally
            {
                await exposure.ConfigureAwait(false);
            }
        }
    }

    private static CaptureResult InjectHeldOutDiffuse(CaptureResult capture, CameraModuleConfig config)
    {
        var frame = capture.Frame!;
        var timing = capture.AcquisitionTiming!;
        var midpoint = timing.ExposureStartedUtc + (timing.ExposureEndedUtc - timing.ExposureStartedUtc) / 2;
        var center = HeldOutPixel(config.Rig, config.ResolveObservatory(midpoint), midpoint);
        var pixels = frame.PixelData.ToArray();
        var column = (int)center.X;
        var row = (int)center.Y;
        for (var y = row - 12; y <= row + 12; y++)
        {
            for (var x = column - 12; x <= column + 12; x++)
            {
                var u = (x + .5 - center.X) / 3.2;
                var v = (y + .5 - center.Y) / 2.7;
                var added = (int)Math.Round(18 * Math.Exp(-.5 * (u * u + v * v)));
                var sample = pixels.AsSpan(y * frame.Layout!.StrideBytes + x * sizeof(ushort), sizeof(ushort));
                var code = BinaryPrimitives.ReadUInt16LittleEndian(sample);
                BinaryPrimitives.WriteUInt16LittleEndian(sample, checked((ushort)(code + added)));
            }
        }
        return capture with { Frame = frame with { PixelData = pixels } };
    }

    private static PixelPoint HeldOutPixel(CameraRigConfig rig, ObservatoryLocation location, DateTimeOffset midpoint)
    {
        var ray = VirtualAstrometryReference.ToEnu(VirtualAstrometryReference.J2000(HeldOutDirection),
            midpoint, new(location.LatitudeDegrees, location.LongitudeDegrees, location.ElevationMeters));
        var expected = VirtualAstrometryReference.Project(rig, ray);
        Assert.IsNotNull(expected);
        Assert.IsTrue(expected.Value.X is > 16 and < 1920 && expected.Value.Y is > 16 and < 1200);
        return expected.Value;
    }

    private static async Task VerifyQualifiedReplayAsync(ServiceProvider provider, CameraRigConfig rig, ObservatoryLocation location,
        IDeepSkyCatalog catalog, CenteredViewBase view, CenteredViewRevision revision,
        CameraAgentLayeredPresentation presentation, MeasuredDeepSkyStackV1 result, CancellationToken token)
    {
        Assert.AreEqual(DeepSkyStackStatus.Qualified, result.Status, result.Reason.ToString());
        var hit = Assert.ContainsSingle(result.Identifications.Where(item => item.Status == DeepSkyIdentificationStatus.Qualified));
        Assert.AreEqual("IC5068", hit.CanonicalId);
        var detection = result.Extraction!.Detections.Single(item => item.Id == hit.DetectionId);
        var expected = HeldOutPixel(rig, location, result.ReferenceMidpointUtc!.Value);
        Assert.IsLessThanOrEqualTo(2, double.Hypot(detection.Centroid.X - expected.X, detection.Centroid.Y - expected.Y));
        Assert.IsTrue(detection.RadiusRmsPixels is >= 1.8 and <= 5.5);
        Assert.IsNotNull(detection.StellarProfile);
        Assert.IsGreaterThanOrEqualTo(2, detection.StellarProfile.SampleCount);

        var layer = Assert.ContainsSingle(presentation.Layers.Where(item => item.Kind == "measured-deep-sky"));
        Assert.IsFalse(layer.EnabledByDefault);
        var svg = XDocument.Parse(Encoding.UTF8.GetString(presentation.Svg.Span));
        var group = svg.Descendants().Single(element => (string?)element.Attribute("id") == layer.DomGroupId);
        var circle = Assert.ContainsSingle(group.Descendants().Where(element => element.Name.LocalName == "circle"));
        var displayed = view.Definition.Preview.Transform.ToPreview(detection.Centroid);
        Assert.AreEqual(displayed.X, double.Parse(circle.Attribute("cx")!.Value, CultureInfo.InvariantCulture), .01);
        Assert.AreEqual(displayed.Y, double.Parse(circle.Attribute("cy")!.Value, CultureInfo.InvariantCulture), .01);

        var persistence = provider.GetRequiredService<CaptureProcessingPersistence>();
        var registration = provider.GetRequiredService<ICenteredWindowRegistrationSource>();
        var capture = view.Definition.Reference.Capture.CaptureId;
        var frozen = await persistence.ReadCenteredViewProductAsync(capture, view.Definition.Science, token).ConfigureAwait(false);
        var stack = MeasuredDeepSkyStackRecipe.ReadStackEvidence(frozen);
        var input = new List<ProcessingArtifact>();
        foreach (var pin in result.Planes)
        {
            input.Add(await persistence.ReadCenteredViewProductAsync(capture,
                new(pin.ArtifactId, pin.OutputIdentitySha256, pin.PayloadSha256), token).ConfigureAwait(false));
        }
        var center = stack.Plan.Slots[2].Exposure!;
        var mapping = center.Plate!;
        foreach (var pin in new[] { center.Raw, mapping.Associations, mapping.Scene, mapping.Context, mapping.Solution })
        {
            var artifact = await registration.ReadArtifactAsync(center.Key, pin, token).ConfigureAwait(false);
            Assert.IsNotNull(artifact);
            input.Add(artifact);
        }
        var request = MeasuredDeepSkyStackRecipe.CreateRequest(frozen, input, result.Catalog);
        var replay = await new ProcessingRecipeExecutor([new MeasuredDeepSkyStackRecipe(catalog)])
            .ExecuteAsync(request, token).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, replay.Status, replay.ReasonCode);
        var repeated = Assert.ContainsSingle(replay.Products);
        var saved = Assert.ContainsSingle(revision.Layers.Where(item => item.Key.Kind == layer.Kind)).Evidence;
        Assert.IsNotNull(saved);
        Assert.AreEqual(saved.IdentitySha256, repeated.OutputIdentitySha256);
        Assert.AreEqual(saved.PayloadSha256, repeated.ChecksumSha256);
    }

    private static async Task<PriorMeasuredProofRevision> ReadPriorMeasuredProofRevisionAsync(ServiceProvider provider,
        string baseId, string revisionId, CancellationToken token)
    {
        var view = await provider.GetRequiredService<ICenteredViewReadIndex>().ReadRevisionAsync(baseId, revisionId, token)
            .ConfigureAwait(false);
        Assert.IsNotNull(view);
        var presentation = provider.GetRequiredService<ICenteredViewPresentationReader>();
        var exact = await presentation.ReadAsync(baseId, revisionId, token).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, exact.Status);
        Assert.IsNotNull(exact.Presentation);
        Assert.IsFalse(exact.Presentation.Layers.Any(layer => layer.Kind == "measured-deep-sky"));
        Assert.IsNull(await provider.GetRequiredService<ICenteredMeasuredDeepSky>().ReadAsync(baseId, revisionId, token)
            .ConfigureAwait(false));
        var preview = await ReadMeasuredProofPreviewAsync(presentation, baseId, revisionId, token).ConfigureAwait(false);
        var previewSha256 = PayloadChecksum.ComputeSha256(preview);
        Assert.AreEqual(view.Base.Definition.Preview.PayloadSha256, previewSha256);
        return new(baseId, revisionId, view.Base.Definition.Kind, view.Base.Definition.Science, previewSha256,
            exact.Presentation.ManifestIdentitySha256, PayloadChecksum.ComputeSha256(exact.Presentation.Svg.Span));
    }

    private static async Task<byte[]> ReadMeasuredProofPreviewAsync(ICenteredViewPresentationReader presentation,
        string baseId, string revisionId, CancellationToken token)
    {
        var preview = await presentation.OpenPreviewAsync(baseId, revisionId, token).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, preview.Status);
        Assert.IsNotNull(preview.Content);
        await using var content = preview.Content.ConfigureAwait(false);
        var bytes = new byte[checked((int)preview.Content.ByteLength)];
        await preview.Content.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }

    private sealed record PriorMeasuredProofRevision(string BaseId, string RevisionId, CenteredViewKind Kind,
        CenteredViewArtifact Science, string PreviewSha256, string ManifestSha256, string SvgSha256);
}
