using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.OrbitalPlanning;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentPinnedOrbitalTimeSourceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RegistrationIsDisabledByDefaultAndDoesNotRegisterAcquisitionOrGpReaderAsync()
    {
        var services = new ServiceCollection();
        services.AddCameraAgentSatellitePlanning(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<CameraAgentSatellitePlanningOptions>>().CurrentValue;
        Assert.IsFalse(options.Enabled);
        Assert.IsNull(options.EarthOrientationPath); Assert.IsNull(options.LeapSecondsPath);
        Assert.IsNull(provider.GetService<HVO.SkyMonitor.Astronomy.IOrbitalSnapshotReader>());
        Assert.IsNull(provider.GetService<HttpClient>());
        Assert.IsNull(await provider.GetRequiredService<ICameraAgentOrbitalTimeSource>().ReadAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.IsNotNull(provider.GetService<ICameraAgentOrbitalPlanningEngineFactory>());
    }

    [TestMethod]
    public async Task ExactPinnedActualInputsLoadOnceAndExplicitPinChangeRevalidatesAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hvo-orbital-time-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new PlanningTestInputs.Settings();
            var eop = PlanningTestInputs.Bytes("time", "predicted-2026.finals2000A");
            var leaps = PlanningTestInputs.Bytes("time", "leap-seconds.list");
            var settings = options.CurrentValue;
            settings.EarthOrientationPath = Path.Combine(directory, "eop"); settings.EarthOrientationSha256 = PlanningTestInputs.Hash(eop);
            settings.LeapSecondsPath = Path.Combine(directory, "leaps"); settings.LeapSecondsSha256 = PlanningTestInputs.Hash(leaps);
            await File.WriteAllBytesAsync(settings.EarthOrientationPath, eop, TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(settings.LeapSecondsPath, leaps, TestContext.CancellationToken).ConfigureAwait(false);
            using var source = new CameraAgentPinnedOrbitalTimeSource(options);
            var resolved = await source.ReadAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(resolved);
            Assert.AreEqual(settings.EarthOrientationSha256, resolved.EarthOrientationSha256);
            Assert.AreEqual(settings.LeapSecondsSha256, resolved.LeapSecondsSha256);
            Assert.IsFalse(resolved.NominalZeroEarthOrientation);
            File.Delete(settings.EarthOrientationPath);
            Assert.AreSame(resolved, await source.ReadAsync(TestContext.CancellationToken).ConfigureAwait(false), "unchanged pins retain immutable parsed bytes; no automatic adoption");
            settings.EarthOrientationSha256 = new string('A', 64);
            Assert.IsNull(await source.ReadAsync(TestContext.CancellationToken).ConfigureAwait(false), "failed adoption must not fall back to old bytes");
            settings.EarthOrientationPath = "relative/eop";
            Assert.IsNull(await source.ReadAsync(TestContext.CancellationToken).ConfigureAwait(false));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task MissingBadHashMalformedAndOversizedFilesAreUnavailableAndCancellationPropagatesAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hvo-orbital-time-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new PlanningTestInputs.Settings();
            var settings = options.CurrentValue;
            settings.EarthOrientationPath = Path.Combine(directory, "eop"); settings.EarthOrientationSha256 = new string('A', 64);
            settings.LeapSecondsPath = Path.Combine(directory, "leaps");
            var leaps = PlanningTestInputs.Bytes("time", "leap-seconds.list");
            settings.LeapSecondsSha256 = PlanningTestInputs.Hash(leaps);
            await File.WriteAllBytesAsync(settings.LeapSecondsPath, leaps, TestContext.CancellationToken).ConfigureAwait(false);
            using var source = new CameraAgentPinnedOrbitalTimeSource(options);
            Assert.IsNull(await source.ReadAsync(TestContext.CancellationToken).ConfigureAwait(false));
            await File.WriteAllBytesAsync(settings.EarthOrientationPath, PlanningTestInputs.Bytes("time", "predicted-2026.finals2000A"), TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNull(await source.ReadAsync(TestContext.CancellationToken).ConfigureAwait(false), "wrong pin");
            var malformed = new byte[] { 1, 2, 3 };
            await File.WriteAllBytesAsync(settings.EarthOrientationPath, malformed, TestContext.CancellationToken).ConfigureAwait(false);
            settings.EarthOrientationSha256 = PlanningTestInputs.Hash(malformed);
            Assert.IsNull(await source.ReadAsync(TestContext.CancellationToken).ConfigureAwait(false), "actual malformed bytes");
            using (var file = File.OpenWrite(settings.EarthOrientationPath)) file.SetLength(CameraAgentPinnedOrbitalTimeSource.MaximumEarthOrientationBytes + 1L);
            Assert.IsNull(await source.ReadAsync(TestContext.CancellationToken).ConfigureAwait(false), "EOP byte bound");
            var eop = PlanningTestInputs.Bytes("time", "predicted-2026.finals2000A");
            await File.WriteAllBytesAsync(settings.EarthOrientationPath, eop, TestContext.CancellationToken).ConfigureAwait(false);
            settings.EarthOrientationSha256 = PlanningTestInputs.Hash(eop);
            using (var file = File.OpenWrite(settings.LeapSecondsPath)) file.SetLength(CameraAgentPinnedOrbitalTimeSource.MaximumLeapSecondsBytes + 1L);
            Assert.IsNull(await source.ReadAsync(TestContext.CancellationToken).ConfigureAwait(false), "leap byte bound");
            using var canceled = new CancellationTokenSource(); await canceled.CancelAsync().ConfigureAwait(false);
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await source.ReadAsync(canceled.Token).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally { Directory.Delete(directory, true); }
    }
}
