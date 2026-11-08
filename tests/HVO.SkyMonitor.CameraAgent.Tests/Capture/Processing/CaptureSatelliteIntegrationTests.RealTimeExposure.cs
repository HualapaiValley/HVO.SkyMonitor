using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class CaptureSatelliteIntegrationTests
{
    private static readonly JsonSerializerOptions ExposureJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [TestMethod]
    public async Task VirtualSkyTwentySecondExposureIsRetainedAndPassedToSatelliteProcessing()
    {
        var root = CreateRoot();
        try
        {
            var fixture = new CaptureSatelliteFixture();
            var start = fixture.Descriptor.Timing.ExposureStartedUtc;
            var clock = new ExposureTestTimeProvider(start);
            using var provider = CreateProvider(root, fixture, clock);
            var template = Configuration(root, fixture);
            var config = template with
            {
                Pipeline = new([new("ProjectedScene", "scene", DependsOn: ["$raw"]), .. template.Pipeline.Steps]),
                Rig = fixture.Configuration.Rig with { Sensor = fixture.Configuration.Rig.Sensor with { ResponseMode = SensorResponseMode.BayerRaw } },
                Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                {
                    asi178Sensor = new { enabled = true },
                    maximumResults = 10,
                    shotNoiseEnabled = false
                }))
            };
            await provider.GetRequiredService<RawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var module = new VirtualSkyCameraModule(clock, new InMemoryCelestialCatalog([]), new ProjectedSceneStore(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var moduleLifetime = module.ConfigureAwait(false);
            await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            var exposure = TimeSpan.FromSeconds(20);
            var request = new CaptureRequest(start, exposure, CaptureMode.Still, new(exposure, 1, null, null));
            var capture = module.CaptureAsync(request, CancellationToken.None);
            Assert.IsFalse(capture.IsCompleted);
            clock.Advance(exposure);
            var result = await capture.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var submission = new CaptureLoopSubmission(request, result, start, exposure, exposure);
            var receipt = await CaptureAsync(provider, config, fixture, submission: submission).ConfigureAwait(false);
            var timing = receipt.Manifest.Descriptor.Timing;
            Assert.AreEqual(exposure, timing.ExposureEndedUtc - timing.ExposureStartedUtc);
            Assert.IsTrue(timing.ReadoutCompletedUtc >= timing.ExposureEndedUtc);
            Assert.IsTrue(timing.DurableIngressUtc >= timing.ReadoutCompletedUtc);
            var processed = await ProcessAsync(provider, config).ConfigureAwait(false);
            Assert.AreEqual(HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution.CaptureLaneHandlerOutcome.Completed,
                processed.Outcome, processed.Reason);
            var node = await provider.GetRequiredService<HVO.SkyMonitor.CameraAgent.Common.Capture.Processing.SqliteCaptureProcessingStore>()
                .ReadNodeAsync(receipt.Manifest.Descriptor.Capture.CaptureId, "satellite", CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(node);
            var retained = node.Outputs.Single(output => output.ProductSchemaVersion == CaptureSatelliteProducts.InputSchema);
            var input = CaptureSatelliteProducts.ParseInput(await File.ReadAllBytesAsync(Path.Combine(root, retained.PayloadRelativePath))
                .ConfigureAwait(false));
            Assert.AreEqual(timing, input.Descriptor.Timing);
            Assert.AreEqual(exposure, input.Descriptor.Timing.ExposureEndedUtc - input.Descriptor.Timing.ExposureStartedUtc);
            Assert.IsNotNull(input.Plan);
            var plan = JsonSerializer.Deserialize<CaptureOrbitalPredictionPlanV1>(input.Plan.Value.Span,
                ExposureJsonOptions);
            Assert.AreEqual(timing, plan!.Descriptor.Timing);
            var statusOutput = node.Outputs.Single(output => output.ProductSchemaVersion == CaptureSatelliteProducts.StatusSchema);
            var status = CaptureSatelliteProducts.ParseStatus(await File.ReadAllBytesAsync(Path.Combine(root, statusOutput.PayloadRelativePath))
                .ConfigureAwait(false));
            Assert.AreEqual(timing.ExposureStartedUtc, status.Exposure.StartUtc);
            Assert.AreEqual(timing.ExposureEndedUtc, status.Exposure.EndUtc);
            // The interval is the acceptance contract; track count and physical visibility are not asserted.
        }
        finally { Directory.Delete(root, true); }
    }
}
