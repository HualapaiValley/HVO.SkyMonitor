using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    [TestMethod]
    public async Task InstallerControlledNight_BecomesTheActiveRigAndCaptureModeAndSurvivesRestart()
    {
        // Deployment.Cli.Tests binds this shared fixture to the complete generated configuration by canonical hash.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "installer-controlled-night.json");
        using var before = JsonDocument.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(false));
        var root = Path.Combine(Path.GetTempPath(), $"hvo-installer-lighting-{Guid.NewGuid():N}");
        var options = Options.Create(new CameraAgentHostOptions
        {
            ConfigFilePath = path,
            RawIngressRoot = root,
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled },
            Observatory = new ObservatoryLocation(35.347, -113.878, 0, "America/Phoenix")
        });
        try
        {
            var loader = new FileCameraAgentConfigurationLoader(options, NullLogger<FileCameraAgentConfigurationLoader>.Instance);
            var baseline = await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            var ingress = new InstallerLightingJournal(root);
            using var schedule = new SqliteCaptureScheduleStore(ingress, options, TimeProvider.System);
            var initial = await schedule.InitializeAsync(baseline, CancellationToken.None).ConfigureAwait(false);
            var named = new SqliteNamedRigProfileStore(ingress, options, TimeProvider.System, schedule);
            var imported = await named.ImportActiveAsync(initial.ActiveRevision, CancellationToken.None).ConfigureAwait(false);
            var active = imported.Revisions.Single(revision => revision.RevisionId == imported.Selection.ActiveRevisionId);
            Assert.AreEqual("ControlledNight", active.Module.Options!.Value.GetProperty("illuminationMode").GetString());
            Assert.IsNull(imported.Selection.PendingRevisionId);

            var effective = initial.ActiveRevision.Profile.ApplyTo(baseline);
            var noon = new DateTimeOffset(2026, 1, 15, 19, 0, 0, TimeSpan.Zero);
            var module = new VirtualSkyCameraModule(TimeProvider.System, CreateCanonicalStarCatalog(),
                new ProjectedSceneStore(), StandardConstellationTopology.CreateD3Celestial(), new AstronomyEnginePlanetEphemeris());
            await using var moduleLifetime = module.ConfigureAwait(false);
            await module.InitializeAsync(effective, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureSolarRegime.Night, ((ICameraSimulatedSkyRegime)module).SimulatedSolarRegime);
            var capture = await module.CaptureAsync(new CaptureRequest(noon, TimeSpan.FromSeconds(1), CaptureMode.Still),
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(capture.Frame);
            Assert.AreEqual("ControlledNight", capture.Frame.Metadata.Extra!["skyIlluminationMode"]);
            Assert.IsTrue(double.Parse(capture.Frame.Metadata.Extra["stellarSolarAltitudeDegrees"],
                System.Globalization.CultureInfo.InvariantCulture) > 0, "The test captures during real daytime.");

            using var restarted = new SqliteCaptureScheduleStore(ingress, options, TimeProvider.System);
            var restored = await restarted.InitializeAsync(await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false),
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevision.RevisionId, restored.ActiveRevision.RevisionId);
            Assert.IsNull(restored.PendingRevision);
            using var after = JsonDocument.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(false));
            Assert.AreEqual(CaptureContractJson.ComputeCanonicalJsonSha256(before.RootElement),
                CaptureContractJson.ComputeCanonicalJsonSha256(after.RootElement));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class InstallerLightingJournal(string root) : IRawCaptureIngress
    {
        public async ValueTask InitializeAsync(CancellationToken cancellationToken)
            => await new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), busyTimeoutSeconds: 5)
                .InitializeAsync(cancellationToken).ConfigureAwait(false);

        public ValueTask<RawCaptureReceipt?> AcceptAsync(CameraModuleConfig configuration, CaptureLoopSubmission submission,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
