using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;

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
        }
        if (session["best"] is { } best)
        {
            RemoveNewFacts(best["provenance"]!.AsObject());
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
    }

    private static async Task<(ManualFocusSessionRecordContent Content, CameraFrame Frame)> SavePreviewAsync(
        VirtualSkyIlluminationMode mode, bool explicitBackground, bool omitSceneMetadata = false)
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
}
