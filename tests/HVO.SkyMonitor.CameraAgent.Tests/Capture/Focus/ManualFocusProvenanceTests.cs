using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Focus;

[TestClass]
[TestCategory("Unit")]
public sealed class ManualFocusProvenanceTests
{
    [TestMethod]
    [DataRow(VirtualSkyIlluminationMode.SolarDriven, false)]
    [DataRow(VirtualSkyIlluminationMode.ControlledNight, false)]
    [DataRow(VirtualSkyIlluminationMode.SolarDriven, true)]
    [DataRow(VirtualSkyIlluminationMode.ControlledNight, true)]
    public async Task RealOwnerPreview_SaveReadbackPreservesIndependentClocksAndIllumination(
        VirtualSkyIlluminationMode mode, bool explicitBackground)
    {
        var (content, frame) = await SavePreviewAsync(mode, explicitBackground).ConfigureAwait(false);
        var sample = content.Record.Session.History.Single();
        var provenance = sample.Provenance;
        var scene = provenance.Scene;
        Assert.IsTrue(content.Summary.Verified);
        Assert.IsNotNull(scene);
        Assert.AreEqual(frame.Metadata.Scene!.VirtualExposure, scene.VirtualExposure);
        Assert.AreEqual(frame.Metadata.Scene.VirtualExposure!.RequestedStartUtc, provenance.CaptureRequestedUtc,
            "The recorded request clock must be the actual request after capture admission drains.");
        Assert.IsNotNull(provenance.CaptureCompletedUtc);
        Assert.IsTrue(provenance.CaptureCompletedUtc >= provenance.CaptureRequestedUtc);
        Assert.IsTrue(sample.MeasuredUtc >= provenance.CaptureCompletedUtc);
        Assert.AreEqual(FocusTestModules.SceneUtc.AddSeconds(0.5), scene.CelestialUtc);
        Assert.AreNotEqual(provenance.CaptureRequestedUtc, scene.CelestialUtc,
            "The fixed celestial fixture must not be relabeled as real acquisition time.");
        Assert.AreEqual(mode.ToString(), scene.IlluminationMode);
        Assert.AreEqual(explicitBackground ? "ExplicitUniformElectronRate" : mode.ToString(), scene.EffectiveIllumination);
        Assert.AreEqual(explicitBackground, scene.BackgroundExplicit);
        Assert.AreEqual(explicitBackground ? 40d : (double?)null, scene.ExplicitBackgroundElectronsPerSecond);
        Assert.IsNotNull(scene.EffectiveBackgroundElectronsPerSecond);
        Assert.IsNotNull(scene.IlluminationAlgorithm);
        Assert.IsNotNull(scene.SolarEphemerisModel);
        var altitude = double.Parse(frame.Metadata.Extra!["stellarSolarAltitudeDegrees"], CultureInfo.InvariantCulture);
        Assert.AreEqual(altitude, scene.SunAltitudeDegrees);
        Assert.IsTrue(altitude is > -90 and < -18,
            "Controlled illumination preserves the actual Sun altitude, never a manufactured -90-degree value.");
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(frame.PixelData.Span)), provenance.FrameSha256);
        Assert.IsFalse(provenance.QualifiesPhysicalFocus);
        Assert.AreEqual(ManualFocusRetentionState.Saved, content.Record.Session.Retention);
        Assert.AreEqual(frame.Metadata.Scene.CatalogName, scene.CatalogName);
        Assert.AreEqual(frame.Metadata.Scene.CatalogVersion, scene.CatalogVersion);
        Assert.AreEqual(frame.Metadata.Scene.CatalogChecksumSha256, scene.CatalogChecksumSha256);
        Assert.AreEqual(frame.Metadata.Scene.RigProfileVersion, scene.RigProfileVersion);
        Assert.AreEqual(frame.Metadata.Scene.RigProfileHashSha256, scene.RigProfileHashSha256);
        Assert.AreEqual(frame.Metadata.Scene.ProjectionModel, scene.ProjectionModel);
        Assert.AreEqual(frame.Metadata.Scene.ProjectionAlgorithmVersion, scene.ProjectionAlgorithmVersion);
        Assert.AreEqual(frame.Metadata.Scene.ProjectionCalibrationVersion, scene.ProjectionCalibrationVersion);
        Assert.AreEqual(frame.Metadata.Scene.AstronomyAlgorithmVersion, scene.AstronomyAlgorithmVersion);
        Assert.AreEqual(frame.Metadata.Scene.SensorRecipeVersion, scene.SensorRecipeVersion);
        Assert.AreEqual(frame.Layout, provenance.SensorLayout);
        var psf = provenance.PointSpreadFunction;
        Assert.IsNotNull(psf);
        Assert.AreEqual(1d, psf.BaseSigmaPixels);
        Assert.AreEqual(new VirtualSkyCameraModuleOptions().PsfRadiusPixels, psf.BaseRadiusPixels);
        Assert.AreEqual(560d, psf.BestPosition);
        Assert.AreEqual(0.014, psf.SigmaPixelsPerStep);
        Assert.AreEqual(8d, psf.MaximumSigmaPixels);
        Assert.AreEqual(0d, psf.MinimumPosition);
        Assert.AreEqual(1000d, psf.MaximumPosition);
        Assert.AreEqual(200d, psf.DefaultPosition);
        Assert.IsTrue(psf.SimulatedControlEnabled);
        Assert.AreEqual(1d, psf.EffectiveSigmaPixels);
        Assert.AreEqual(double.Parse(frame.Metadata.Extra["psfRadiusPixels"], CultureInfo.InvariantCulture),
            psf.EffectiveRadiusPixels);
        Assert.IsNotNull(sample.ComparisonGroupId);
    }

    [TestMethod]
    public async Task MissingSceneMetadata_RemainsUnknownWhileAcquisitionClocksAreRetained()
    {
        var (content, _) = await SavePreviewAsync(VirtualSkyIlluminationMode.ControlledNight, false,
            omitSceneMetadata: true).ConfigureAwait(false);
        var provenance = content.Record.Session.History.Single().Provenance;

        Assert.IsTrue(content.Summary.Verified);
        Assert.IsNull(provenance.Scene);
        Assert.IsNull(provenance.SceneId);
        Assert.IsNotNull(provenance.CaptureRequestedUtc);
        Assert.IsNotNull(provenance.CaptureCompletedUtc);
    }

    [TestMethod]
    public async Task EarlierSavedJson_ReadsWithoutInventingMissingSceneOrAcquisitionFacts()
    {
        var (current, _) = await SavePreviewAsync(VirtualSkyIlluminationMode.ControlledNight, false).ConfigureAwait(false);
        var document = JsonNode.Parse(current.Utf8Json.Span)!;
        var session = document["session"]!;
        foreach (var sample in session["history"]!.AsArray())
        {
            RemoveNewFacts(sample!["provenance"]!.AsObject());
            sample.AsObject().Remove("comparisonGroupId");
        }
        if (session["best"] is { } best)
        {
            RemoveNewFacts(best["provenance"]!.AsObject());
            best.AsObject().Remove("comparisonGroupId");
        }
        var bytes = Encoding.UTF8.GetBytes(document.ToJsonString());
        var checksum = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var root = Path.Combine(Path.GetTempPath(), "hvo-focus-legacy-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, ManualFocusSessionStore.DirectoryName);
        Directory.CreateDirectory(directory);
        try
        {
            var id = current.Record.RecordId;
            await File.WriteAllBytesAsync(Path.Combine(directory, id + ".json"), bytes).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(directory, id + ".sha256"), $"{checksum}  {id}.json\n")
                .ConfigureAwait(false);
            using var store = new ManualFocusSessionStore(root);
            var earlier = await store.ReadAsync(id, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(earlier);
            Assert.IsTrue(earlier.Summary.Verified);
            CollectionAssert.AreEqual(bytes, earlier.Utf8Json.ToArray());
            var provenance = earlier.Record.Session.History.Single().Provenance;
            Assert.IsNull(provenance.CaptureRequestedUtc);
            Assert.IsNull(provenance.CaptureCompletedUtc);
            Assert.IsNull(provenance.Scene);
            Assert.IsNull(provenance.SensorLayout);
            Assert.IsNull(provenance.PointSpreadFunction);
            Assert.IsNull(earlier.Record.Session.History.Single().ComparisonGroupId);
            Assert.AreEqual(current.Record.Session.History.Single().Provenance.FrameSha256, provenance.FrameSha256);
            Assert.AreEqual(current.Record.Session.History.Single().MeasuredUtc,
                earlier.Record.Session.History.Single().MeasuredUtc);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void RemoveNewFacts(JsonObject provenance)
    {
        provenance.Remove("captureRequestedUtc");
        provenance.Remove("captureCompletedUtc");
        provenance.Remove("scene");
        provenance.Remove("sensorLayout");
        provenance.Remove("pointSpreadFunction");
    }

    [TestMethod]
    public async Task ExplicitNativeReadout_SavePreservesRoiBinningAndStoredSampleMeaning()
    {
        var (content, frame) = await SavePreviewAsync(VirtualSkyIlluminationMode.ControlledNight, false,
            nativeReadout: true).ConfigureAwait(false);
        var layout = content.Record.Session.History.Single().Provenance.SensorLayout;
        Assert.IsTrue(content.Summary.Verified);
        Assert.AreEqual(frame.Layout, layout);
        Assert.IsNotNull(layout?.Readout);
        Assert.AreEqual(484, layout.Readout.NativeWidth);
        Assert.AreEqual(42, layout.Readout.RoiX);
        Assert.AreEqual(12, layout.Readout.RoiY);
        Assert.AreEqual(400, layout.Readout.RoiWidth);
        Assert.AreEqual(280, layout.Readout.RoiHeight);
        Assert.AreEqual(2, layout.Readout.BinX);
        Assert.AreEqual(2, layout.Readout.BinY);
        Assert.AreEqual(FrameBinningAlgorithm.DigitalAverageV1, layout.Readout.BinningAlgorithm);
        Assert.AreEqual(200, layout.Width);
        Assert.AreEqual(140, layout.Height);
        Assert.AreEqual(CameraPixelFormat.Mono8, layout.PixelFormat);
        Assert.AreEqual(8, layout.SampleDepthBits);
        Assert.AreEqual(FrameStoredCodeTransform.IdentityV1, layout.StoredCodeTransform);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task QuantizedZeroNoiseAndCrowdedPreviews_MeasureAndSaveWithoutInventedWidths(bool crowded)
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-focus-quantized-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new ManualFocusSessionStore(root);
            var source = new QuantizedPreviewSource(crowded);
            using var coordinator = new ManualFocusSessionCoordinator(source, TimeProvider.System,
                ManualFocusSessionLimits.Default with { MinimumSamplePeriod = TimeSpan.FromMinutes(1) }, store);
            var session = await coordinator.StartAsync(new(new(TimeSpan.FromSeconds(1), 0)), "alice", CancellationToken.None)
                .ConfigureAwait(false);
            await FocusWait.UntilAsync(coordinator, static snapshot => snapshot.TotalSamples == 1, "quantized sample")
                .ConfigureAwait(false);
            await coordinator.StopAsync(session.SessionId, "alice", CancellationToken.None).ConfigureAwait(false);
            var saved = await coordinator.SaveAsync(session.SessionId, "alice", CancellationToken.None).ConfigureAwait(false);
            var content = await store.ReadAsync(saved.SavedRecordId!, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(content);
            Assert.IsTrue(content.Summary.Verified);
            var measurement = content.Record.Session.History.Single().Measurement;
            if (crowded)
            {
                Assert.AreEqual(FocusStarReasonCodes.CrowdedAutomaticTarget, measurement.ReasonCode);
                Assert.AreEqual(FocusStarStatus.NoStar, measurement.Status);
                Assert.IsNull(measurement.HalfFluxDiameterPixels);
                Assert.IsNull(content.Record.Session.Best);
            }
            else
            {
                Assert.AreEqual(FocusStarStatus.Valid, measurement.Status, measurement.ReasonCode);
                Assert.AreEqual(0d, measurement.NoiseSigma);
                Assert.IsTrue(measurement.SignalToNoiseUnbounded);
                Assert.IsNull(measurement.SignalToNoise, "An unbounded SNR is an explicit fact, not a JSON infinity.");
                Assert.IsNotNull(content.Record.Session.Best);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(ManualFocusSessionRecordContent Content, CameraFrame Frame)> SavePreviewAsync(
        VirtualSkyIlluminationMode mode, bool explicitBackground, bool omitSceneMetadata = false, bool nativeReadout = false)
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        var config = FocusTestModules.Config();
        var options = new VirtualSkyCameraModuleOptions
        {
            Seed = 1017,
            FixedSceneUtc = FocusTestModules.SceneUtc,
            IlluminationMode = mode,
            BackgroundElectronsPerSecond = explicitBackground ? 40 : null
        };
        config = config with { Module = config.Module with { Options = JsonSerializer.SerializeToElement(options) } };
        if (nativeReadout)
        {
            config = config with
            {
                Rig = config.Rig with
                {
                    Readout = new SensorReadoutProfile(new SensorCrop(42, 12, 400, 280), 2, 2,
                        FrameBinningAlgorithm.DigitalAverageV1, CameraPixelFormat.Mono8, 8, 8,
                        FrameSamplePacking.ByteAligned, FrameStoredCodeTransform.IdentityV1,
                        FrameLevelCodeSpace.StoredContainer, 4, 255)
                }
            };
        }
        var module = FocusTestModules.Create(magnitude: 0);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        fixture.Publish(module, config);
        var source = new RecordedPreviewSource(fixture.Source, omitSceneMetadata);
        using var store = new ManualFocusSessionStore(fixture.Root);
        using var coordinator = new ManualFocusSessionCoordinator(source, TimeProvider.System,
            ManualFocusSessionLimits.Default with { MinimumSamplePeriod = TimeSpan.FromMinutes(1) }, store);
        var started = await coordinator.StartAsync(new(new(TimeSpan.FromSeconds(1), 20), 560), "alice",
            CancellationToken.None).ConfigureAwait(false);
        await FocusWait.UntilAsync(coordinator, static snapshot => snapshot.TotalSamples >= 1, "one provenance sample")
            .ConfigureAwait(false);
        await coordinator.StopAsync(started.SessionId, "alice", CancellationToken.None).ConfigureAwait(false);
        var saved = await coordinator.SaveAsync(started.SessionId, "alice", CancellationToken.None).ConfigureAwait(false);
        var content = await store.ReadAsync(saved.SavedRecordId!, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(content);
        Assert.IsNotNull(source.Frame);
        return (content, source.Frame);
    }

    private sealed class RecordedPreviewSource(IManualFocusPreviewSource inner, bool omitSceneMetadata)
        : IManualFocusPreviewSource
    {
        public CameraFrame? Frame { get; private set; }

        public ManualFocusSessionAvailability GetAvailability() => inner.GetAvailability();

        public async Task<ManualFocusPreview> AcquireAsync(ManualFocusPreviewSettings settings,
            double? simulatedFocusPosition, CancellationToken cancellationToken)
        {
            var preview = await inner.AcquireAsync(settings, simulatedFocusPosition, cancellationToken).ConfigureAwait(false);
            if (omitSceneMetadata)
            {
                preview = preview with
                {
                    Frame = preview.Frame with { Metadata = preview.Frame.Metadata with { Extra = null, Scene = null } }
                };
            }
            Frame = preview.Frame;
            return preview;
        }
    }

    private sealed class QuantizedPreviewSource(bool crowded) : IManualFocusPreviewSource
    {
        public ManualFocusSessionAvailability GetAvailability()
            => new(true, "Synthetic pixels for measurement qualification.", "QuantizedFixture",
                new("synthetic-measurement-fixture", false, "This fixture does not qualify physical focus."), ModuleGeneration: 1);

        public Task<ManualFocusPreview> AcquireAsync(ManualFocusPreviewSettings settings,
            double? simulatedFocusPosition, CancellationToken cancellationToken)
        {
            const int width = 300, height = 300;
            var bytes = new byte[width * height * 2];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var value = Gaussian(x + 0.5, y + 0.5, crowded ? 147.5 : 150.5);
                    if (crowded) value += Gaussian(x + 0.5, y + 0.5, 153.5);
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan((y * width + x) * 2, 2), (ushort)Math.Round(value));
                }
            }
            var now = DateTimeOffset.UtcNow;
            var frame = new CameraFrame(now, width, height, CameraPixelFormat.Mono16, bytes,
                new FrameMetadata(settings.Exposure, settings.Gain, 0));
            var availability = GetAvailability();
            return Task.FromResult(new ManualFocusPreview(frame, availability.ModuleType!, 1, availability.Fidelity!, null,
                now, now));

            static double Gaussian(double x, double y, double centreX)
                => 5000 * Math.Exp(-((x - centreX) * (x - centreX) + (y - 150.5) * (y - 150.5)) / (2 * 1.5 * 1.5));
        }
    }
}
