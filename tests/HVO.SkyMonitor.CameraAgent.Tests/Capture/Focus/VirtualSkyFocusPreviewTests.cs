using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Focus;

/// <summary>
/// The VirtualSky simulated defocus model measured end to end: rendered previews, the production sampler and image-circle
/// mask, and the Imaging star measurer. The model's Gaussian width is the ground truth the measured HFD must follow.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class VirtualSkyFocusPreviewTests
{
    // HFD of a pixel-integrated Gaussian: 2 * sqrt(2 ln 2) * sqrt(sigma^2 + 1/12).
    private const double HalfFluxDiameterPerSigma = 2 * 1.1774100225154747;
    private static readonly double[] SweepPositions = [0, 100, 200, 300, 400, 500, 560, 620, 720, 820, 920, 1000];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task DefocusSweep_MeasuredHfdFollowsTheDeclaredModelWithAStableCentroidAndFlux()
    {
        var module = FocusTestModules.Create(magnitude: 0);
        await using var lifetime = module.ConfigureAwait(false);
        // The same sky without the star renders identical noise, so the difference is the rendered star exactly.
        var starless = FocusTestModules.Create(magnitude: 30);
        await using var starlessLifetime = starless.ConfigureAwait(false);
        var config = FocusTestModules.Config();
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        await starless.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var measurements = new List<(double Position, double Truth, double TruthFlux, FocusStarMeasurement Measurement)>();

        foreach (var position in SweepPositions)
        {
            var frame = await PreviewAsync(module, position, 20, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            var sky = await PreviewAsync(starless, position, 20, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            var sigma = double.Parse(frame.Metadata.Extra!["psfSigmaPixels"], CultureInfo.InvariantCulture);
            var measurement = Measure(config, frame);
            var truthFlux = RenderedFlux(frame, sky);
            measurements.Add((position, HalfFluxDiameterPerSigma * Math.Sqrt(sigma * sigma + 1d / 12), truthFlux, measurement));
            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"position {position,5}: sigma {sigma:F3} truth HFD {measurements[^1].Truth:F3} measured {measurement.HalfFluxDiameterPixels:F3} FWHM {measurement.FwhmPixels:F3} flux {measurement.TotalFlux:F0} rendered {truthFlux:F0} centroid {measurement.Centroid?.X:F3},{measurement.Centroid?.Y:F3} SNR {measurement.SignalToNoise:F0}"));
        }

        foreach (var (position, truth, _, measurement) in measurements)
        {
            Assert.AreEqual(FocusStarStatus.Valid, measurement.Status, $"{position}: {measurement.ReasonCode}");
            Assert.AreEqual(truth, measurement.HalfFluxDiameterPixels!.Value, 0.04 * truth, $"HFD at {position}");
        }
        var diameters = measurements.Select(static item => item.Measurement.HalfFluxDiameterPixels!.Value).ToArray();
        var best = Array.IndexOf(SweepPositions, 560d);
        for (var index = 1; index < diameters.Length; index++)
        {
            if (index <= best)
            {
                Assert.IsLessThan(diameters[index - 1], diameters[index], $"HFD must fall toward best focus at {SweepPositions[index]}");
            }
            else
            {
                Assert.IsGreaterThan(diameters[index - 1], diameters[index], $"HFD must rise past best focus at {SweepPositions[index]}");
            }
        }
        var meanX = measurements.Average(static item => item.Measurement.Centroid!.Value.X);
        var meanY = measurements.Average(static item => item.Measurement.Centroid!.Value.Y);
        var meanFlux = measurements.Average(static item => item.Measurement.TotalFlux!.Value);
        foreach (var (position, _, truthFlux, measurement) in measurements)
        {
            Assert.AreEqual(meanX, measurement.Centroid!.Value.X, 0.1, $"Centroid X must not move with focus ({position})");
            Assert.AreEqual(meanY, measurement.Centroid.Value.Y, 0.1, $"Centroid Y must not move with focus ({position})");
            // The aperture follows the star, so the sky-fit residual summed over it is largest for the broadest profile.
            Assert.AreEqual(meanFlux, measurement.TotalFlux!.Value, 0.015 * meanFlux, $"Flux must not change with focus ({position})");
            Assert.AreEqual(truthFlux, measurement.TotalFlux.Value, 0.025 * truthFlux, $"Flux must match the rendered star ({position})");
        }
        Assert.AreEqual(FocusTestModules.ZenithStar.X, meanX, 0.1);
        Assert.AreEqual(FocusTestModules.ZenithStar.Y, meanY, 0.1);
    }

    [TestMethod]
    public async Task ClippedAndEmptyFields_AreReportedExplicitly()
    {
        var bright = FocusTestModules.Create(magnitude: 0);
        await using var brightLifetime = bright.ConfigureAwait(false);
        var empty = FocusTestModules.Create(magnitude: 30);
        await using var emptyLifetime = empty.ConfigureAwait(false);
        var config = FocusTestModules.Config();
        await bright.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        await empty.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);

        var focused = Measure(config, await PreviewAsync(bright, 560, 200, TimeSpan.FromSeconds(4)).ConfigureAwait(false));
        var defocused = Measure(config, await PreviewAsync(bright, 200, 200, TimeSpan.FromSeconds(4)).ConfigureAwait(false));
        var starless = Measure(config, await PreviewAsync(empty, 560, 200, TimeSpan.FromSeconds(4)).ConfigureAwait(false));

        Assert.AreEqual(FocusStarStatus.Saturated, focused.Status, "A star driven into clipping must not report a width.");
        Assert.IsNull(focused.HalfFluxDiameterPixels);
        Assert.AreEqual(FocusStarStatus.Valid, defocused.Status, "Spread over more pixels the same star is measurable.");
        Assert.AreEqual(FocusStarStatus.NoStar, starless.Status);
        Assert.AreEqual(FocusStarReasonCodes.NoCandidate, starless.ReasonCode, "The vignetted sky alone must not become a star.");
    }

    [TestMethod]
    public async Task Previews_NeverAdvanceOrPerturbOrdinaryCaptures()
    {
        var previewedStore = new ProjectedSceneStore();
        var previewed = FocusTestModules.Create(sceneStore: previewedStore);
        await using var previewedLifetime = previewed.ConfigureAwait(false);
        var untouched = FocusTestModules.Create();
        await using var untouchedLifetime = untouched.ConfigureAwait(false);
        var config = FocusTestModules.Config();
        await previewed.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        await untouched.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var request = FocusTestModules.Request(TimeSpan.FromSeconds(1), 20);

        var firstPreviewed = await previewed.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var preview = await PreviewAsync(previewed, 900, 200, TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        await PreviewAsync(previewed, 300, 20, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        var secondPreviewed = await previewed.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var firstUntouched = await untouched.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var secondUntouched = await untouched.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(Sha(firstUntouched.Frame!), Sha(firstPreviewed.Frame!));
        Assert.AreEqual(Sha(secondUntouched.Frame!), Sha(secondPreviewed.Frame!),
            "Previews between captures must not consume noise sequence, timeline, or setpoint state.");
        Assert.AreEqual(secondUntouched.Frame!.Metadata.Extra!["sceneId"], secondPreviewed.Frame!.Metadata.Extra!["sceneId"]);
        Assert.IsFalse(secondPreviewed.Frame.Metadata.Extra.ContainsKey("focusPreview"));
        Assert.IsFalse(secondPreviewed.Frame.Metadata.Extra.ContainsKey("simulatedFocusPosition"),
            "Ordinary captures always render the configured PSF.");
        Assert.IsFalse(previewedStore.TryGet(preview.Metadata.Extra!["sceneId"], out _), "A preview scene is never retained.");
        Assert.IsTrue(previewedStore.TryGet(secondPreviewed.Frame.Metadata.Extra["sceneId"], out _));
    }

    [TestMethod]
    public async Task PreviewProvenance_BindsTheDeclaredModelPositionAndRenderedWidth()
    {
        var module = FocusTestModules.Create();
        await using var lifetime = module.ConfigureAwait(false);
        var config = FocusTestModules.Config();
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var request = FocusTestModules.Request(TimeSpan.FromSeconds(1), 20);

        var capture = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var positioned = await PreviewAsync(module, 300, 20, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        var defaulted = await module.CaptureFocusPreviewAsync(new(request), CancellationToken.None).ConfigureAwait(false);

        var model = module.SimulatedFocus!;
        var options = new VirtualSimulatedFocusOptions();
        var extra = positioned.Metadata.Extra!;
        Assert.AreEqual(VirtualSimulatedFocusOptions.ModelId, model.ModelId);
        Assert.AreEqual(VirtualSimulatedFocusOptions.Units, model.Units);
        Assert.AreEqual(200d, model.DefaultPosition);
        Assert.AreEqual("virtual-simulated-defocus", module.FocusPreviewFidelity.Kind);
        Assert.IsFalse(module.FocusPreviewFidelity.QualifiesPhysicalFocus);
        Assert.AreEqual("true", extra["focusPreview"]);
        Assert.AreEqual("300", extra["simulatedFocusPosition"]);
        Assert.AreEqual("560", extra["simulatedFocusBestPosition"]);
        Assert.AreEqual(model.ParametersSha256, extra["simulatedFocusParametersSha256"]);
        Assert.AreEqual(options.SigmaPixels(1, 300), double.Parse(extra["psfSigmaPixels"], CultureInfo.InvariantCulture), 1e-12);
        Assert.AreNotEqual(capture.Frame!.Metadata.Extra!["sceneId"], extra["sceneId"],
            "A preview never shares an identity with a capture of the same instant.");
        Assert.AreEqual("200", defaulted.Frame!.Metadata.Extra!["simulatedFocusPosition"]);
        Assert.AreEqual(8, options.SigmaPixels(1, -10_000), 1e-12, "The declared ceiling bounds the rendered width.");

        var changedDefault = FocusTestModules.Create();
        await using var changedLifetime = changedDefault.ConfigureAwait(false);
        await changedDefault.InitializeAsync(FocusTestModules.Config(options with { DefaultPosition = 560 }),
            CancellationToken.None).ConfigureAwait(false);
        var changedPreview = await changedDefault.CaptureFocusPreviewAsync(new(request), CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreNotEqual(model.ParametersSha256, changedDefault.SimulatedFocus!.ParametersSha256,
            "Every declared model parameter, including the default position, belongs to its immutable identity.");
        Assert.AreEqual("560", changedPreview.Frame!.Metadata.Extra!["simulatedFocusPosition"]);
        Assert.AreEqual("1", changedPreview.Frame.Metadata.Extra["psfSigmaPixels"]);
        Assert.IsFalse(defaulted.Frame.PixelData.Span.SequenceEqual(changedPreview.Frame.PixelData.Span),
            "A changed declared default must resolve a different actual rendered PSF, not only metadata.");
    }

    [TestMethod]
    public async Task InvalidOrDisabledModels_AreRefusedExplicitly()
    {
        var module = FocusTestModules.Create();
        await using var lifetime = module.ConfigureAwait(false);
        await module.InitializeAsync(FocusTestModules.Config(), CancellationToken.None).ConfigureAwait(false);
        var disabled = FocusTestModules.Create();
        await using var disabledLifetime = disabled.ConfigureAwait(false);
        await disabled.InitializeAsync(FocusTestModules.Config(new VirtualSimulatedFocusOptions { Enabled = false }),
            CancellationToken.None).ConfigureAwait(false);
        var request = FocusTestModules.Request(TimeSpan.FromSeconds(1), 20);

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            module.CaptureFocusPreviewAsync(new(request, 1000.5), CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            module.CaptureFocusPreviewAsync(new(request, double.NaN), CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            disabled.CaptureFocusPreviewAsync(new(request, 560), CancellationToken.None)).ConfigureAwait(false);
        var fixedPsf = await disabled.CaptureFocusPreviewAsync(new(request), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(disabled.SimulatedFocus);
        Assert.AreEqual("virtual-fixed-psf", disabled.FocusPreviewFidelity.Kind);
        Assert.AreEqual("1", fixedPsf.Frame!.Metadata.Extra!["psfSigmaPixels"]);
        Assert.IsFalse(fixedPsf.Frame.Metadata.Extra.ContainsKey("simulatedFocusPosition"));

        foreach (var invalid in new[]
        {
            new VirtualSimulatedFocusOptions { MaximumSigmaPixels = 20 },
            new VirtualSimulatedFocusOptions { MinimumPosition = 10, MaximumPosition = 10 },
            new VirtualSimulatedFocusOptions { BestPosition = 2000 },
            new VirtualSimulatedFocusOptions { DefaultPosition = -1 },
            new VirtualSimulatedFocusOptions { DefocusSigmaPixelsPerStep = 0 }
        })
        {
            var rejected = FocusTestModules.Create();
            await using var rejectedLifetime = rejected.ConfigureAwait(false);
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await rejected.InitializeAsync(FocusTestModules.Config(invalid), CancellationToken.None).ConfigureAwait(false),
                $"{invalid}").ConfigureAwait(false);
        }
    }

    private static async Task<CameraFrame> PreviewAsync(VirtualSkyCameraModule module, double position, double gain, TimeSpan exposure)
    {
        var result = await module.CaptureFocusPreviewAsync(
            new(FocusTestModules.Request(exposure, gain), position), CancellationToken.None).ConfigureAwait(false);
        return result.Frame!;
    }

    private static FocusStarMeasurement Measure(CameraModuleConfig config, CameraFrame frame)
        => ManualFocusPreviewMeasurement.Measure(frame, 1, null, null, CancellationToken.None,
            CameraModuleRunner.ResolveMeteringImageCircle(config, frame)).Measurement;

    private static double RenderedFlux(CameraFrame star, CameraFrame sky)
    {
        var starPixels = star.PixelData.Span;
        var skyPixels = sky.PixelData.Span;
        double flux = 0;
        for (var index = 0; index < starPixels.Length; index += 2)
        {
            flux += BinaryPrimitives.ReadUInt16LittleEndian(starPixels[index..]) - BinaryPrimitives.ReadUInt16LittleEndian(skyPixels[index..]);
        }
        return flux;
    }

    private static string Sha(CameraFrame frame) => Convert.ToHexStringLower(SHA256.HashData(frame.PixelData.Span));
}
