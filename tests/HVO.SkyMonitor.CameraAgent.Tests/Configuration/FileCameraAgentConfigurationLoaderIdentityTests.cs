using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;

namespace HVO.SkyMonitor.CameraAgent.Tests.Configuration;

[TestClass]
[TestCategory("Unit")]
public sealed class FileCameraAgentConfigurationLoaderIdentityTests
{
    [TestMethod]
    public async Task BackgroundScrubCooldownUsesTheInjectedClockAndExplicitScrubPath()
    {
        var clock = new RefreshClock();
        var store = new PendingLocationStore();
        var loader = new FileCameraAgentConfigurationLoader(Options.Create(new CameraAgentHostOptions
        {
            ConfigFilePath = Path.Combine(AppContext.BaseDirectory, "cameraagent.sample.json"),
            AgentId = "synthetic",
            Observatory = new ObservatoryLocation(35, -114, 1_500, "UTC"),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
        }), NullLogger<FileCameraAgentConfigurationLoader>.Instance, store,
            hostLifetime: new RefreshLifetime(), timeProvider: clock);
        async Task RequestAndDrainAsync()
        {
            await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
                _ = await loader.LoadInstallationSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            var refresh = (Task)typeof(FileCameraAgentConfigurationLoader).GetField("_refresh",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(loader)!;
            await refresh.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        await RequestAndDrainAsync().ConfigureAwait(false);
        Assert.AreEqual(1, store.Scrubs);
        clock.UtcNow = clock.UtcNow.AddSeconds(29);
        await RequestAndDrainAsync().ConfigureAwait(false);
        Assert.AreEqual(1, store.Scrubs);
        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        await RequestAndDrainAsync().ConfigureAwait(false);
        Assert.AreEqual(2, store.Scrubs);
    }

    [TestMethod]
    public async Task OrdinaryUnqualifiedLoadsShareOneHostScrubAndCooldownStartsAtCompletion()
    {
        var clock = new RefreshClock();
        var store = new UnqualifiedLocationStore();
        var loader = new FileCameraAgentConfigurationLoader(Options.Create(new CameraAgentHostOptions
        {
            ConfigFilePath = Path.Combine(AppContext.BaseDirectory, "cameraagent.sample.json"),
            AgentId = "synthetic",
            Observatory = new ObservatoryLocation(35, -114, 1_500, "UTC"),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
        }), NullLogger<FileCameraAgentConfigurationLoader>.Instance, store,
            hostLifetime: new RefreshLifetime(), timeProvider: clock);
        async Task DrainAsync()
        {
            var task = (Task)typeof(FileCameraAgentConfigurationLoader).GetField("_refresh",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(loader)!;
            await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        try
        {
            _ = await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            clock.UtcNow = clock.UtcNow.AddSeconds(60);
            for (var attempt = 0; attempt < 4; attempt++) _ = await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, store.Scrubs);
        }
        finally { store.Release.TrySetResult(); await DrainAsync().ConfigureAwait(false); }
        _ = await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, store.Scrubs, "A long failed scrub must not immediately restart.");
        clock.UtcNow = clock.UtcNow.AddSeconds(29);
        _ = await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, store.Scrubs);
        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        _ = await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        await DrainAsync().ConfigureAwait(false);
        Assert.AreEqual(2, store.Scrubs);
    }

    private sealed class UnqualifiedLocationStore : IDeploymentLocationStore
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Scrubs { get; private set; }
        public bool RequiresRetainedIntegrityScrub => true;
        public DeploymentLocationSnapshot? Active { get; } = DeploymentLocationSnapshot.Create("synthetic", 1, "configuration",
            null, DateTimeOffset.UnixEpoch, null, 35, -114, 1_500, "UTC");
        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(DeploymentLocationSeed seed, CancellationToken cancellationToken)
            => ValueTask.FromResult(Active!);
        public async ValueTask<DeploymentLocationSnapshot> ScrubCurrentAsync(DeploymentLocationSeed seed,
            Func<bool> configurationStillCurrent, CancellationToken cancellationToken)
        {
            Scrubs++;
            Started.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            throw new IOException("Synthetic persistent qualification failure.");
        }
        public DeploymentLocationSnapshot Resolve(CaptureLocationProvenance provenance, DateTimeOffset? effectiveUtc = null)
            => throw new NotSupportedException();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OrdinaryLoadRetriesChangedSnapshotsWithoutAcceptingStaleConfiguration(bool keepsChanging)
    {
        var store = new ChangedSnapshotLocationStore(keepsChanging);
        var loader = new FileCameraAgentConfigurationLoader(Options.Create(new CameraAgentHostOptions
        {
            ConfigFilePath = Path.Combine(AppContext.BaseDirectory, "cameraagent.sample.json"),
            AgentId = "synthetic",
            Observatory = new ObservatoryLocation(35, -114, 1_500, "UTC"),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
        }), NullLogger<FileCameraAgentConfigurationLoader>.Instance, store);
        if (keepsChanging)
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => loader.LoadAsync(CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(3, store.Reads, "Repeated concurrent edits must not create an infinite retry.");
        }
        else
        {
            Assert.AreEqual("synthetic", (await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false)).AgentId);
            Assert.AreEqual(2, store.Reads);
        }
    }

    private sealed class ChangedSnapshotLocationStore(bool keepsChanging) : IDeploymentLocationStore
    {
        internal int Reads { get; private set; }
        public DeploymentLocationSnapshot? Active { get; } = DeploymentLocationSnapshot.Create("synthetic", 1, "configuration",
            null, DateTimeOffset.UnixEpoch, null, 35, -114, 1_500, "UTC");
        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(DeploymentLocationSeed seed, CancellationToken cancellationToken)
        {
            Reads++;
            return keepsChanging || Reads == 1
                ? ValueTask.FromException<DeploymentLocationSnapshot>(new InstallationVerificationPendingException(requiresScrub: true, configurationChanged: true))
                : ValueTask.FromResult(Active!);
        }
        public DeploymentLocationSnapshot Resolve(CaptureLocationProvenance provenance, DateTimeOffset? effectiveUtc = null)
            => throw new NotSupportedException();
    }

    private sealed class RefreshClock : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch.AddYears(56);
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class RefreshLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class PendingLocationStore : IDeploymentLocationStore
    {
        internal int Scrubs { get; private set; }
        public DeploymentLocationSnapshot? Active => null;
        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(DeploymentLocationSeed seed, CancellationToken cancellationToken)
        {
            Scrubs++;
            return ValueTask.FromException<DeploymentLocationSnapshot>(new IOException("Synthetic scrub failure."));
        }
        public ValueTask<DeploymentLocationSnapshot> VerifyInstallationAsync(DeploymentLocationSeed seed, CancellationToken cancellationToken)
            => ValueTask.FromException<DeploymentLocationSnapshot>(new InstallationVerificationPendingException(requiresScrub: true));
        public DeploymentLocationSnapshot Resolve(CaptureLocationProvenance provenance, DateTimeOffset? effectiveUtc = null)
            => throw new NotSupportedException();
    }

    [TestMethod]
    public async Task CentralIntegrationDerivesSampleAgentIdFromPersistentDeviceIdentity()
    {
        var loader = CreateLoader(agentId: null, CentralIntegrationMode.Enabled, "device-identity-1");

        var configuration = await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual("device-identity-1", configuration.AgentId);
    }

    [TestMethod]
    public async Task CentralIntegrationAcceptsExactAgentIdAndRejectsMismatch()
    {
        var matching = CreateLoader("device-identity-1", CentralIntegrationMode.Enabled, "device-identity-1");
        var mismatch = CreateLoader("configured-other-device", CentralIntegrationMode.Enabled, "device-identity-1");

        Assert.AreEqual(
            "device-identity-1",
            (await matching.LoadAsync(CancellationToken.None).ConfigureAwait(false)).AgentId);
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await mismatch.LoadAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(
            "Configured AgentId does not match the persistent CameraAgent device identity.",
            exception.Message);
    }

    [TestMethod]
    public async Task StandaloneModePreservesConfiguredAgentId()
    {
        var provider = new FixedCaptureAgentIdentityProvider("device-identity-1");
        var loader = CreateLoader("standalone-agent", CentralIntegrationMode.Disabled, provider);

        var configuration = await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual("standalone-agent", configuration.AgentId);
        Assert.AreEqual(0, provider.CallCount);
    }

    [TestMethod]
    public async Task CentralIntegrationRejectsBlankPersistentDeviceIdentity()
    {
        var loader = CreateLoader("configured-agent", CentralIntegrationMode.Enabled, " ");

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreEqual(
            "The persistent CameraAgent device identity does not contain a valid device identifier.",
            exception.Message);
    }

    [TestMethod]
    public async Task CentralIntegrationRejectsMissingPersistentIdentityProvider()
    {
        var loader = new FileCameraAgentConfigurationLoader(
            Options.Create(new CameraAgentHostOptions
            {
                ConfigFilePath = Path.Combine(AppContext.BaseDirectory, "cameraagent.sample.json"),
                AgentId = "configured-agent",
                Observatory = new ObservatoryLocation(35, -114, 1_500, "UTC"),
                CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Enabled }
            }),
            NullLogger<FileCameraAgentConfigurationLoader>.Instance);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreEqual(
            "Central integration requires a persistent CameraAgent device identity provider.",
            exception.Message);
    }

    private static FileCameraAgentConfigurationLoader CreateLoader(
        string? agentId,
        CentralIntegrationMode mode,
        string provisionedAgentId)
        => CreateLoader(agentId, mode, new FixedCaptureAgentIdentityProvider(provisionedAgentId));

    private static FileCameraAgentConfigurationLoader CreateLoader(
        string? agentId,
        CentralIntegrationMode mode,
        ICaptureAgentIdentityProvider provider)
        => new(
            Options.Create(new CameraAgentHostOptions
            {
                ConfigFilePath = Path.Combine(AppContext.BaseDirectory, "cameraagent.sample.json"),
                AgentId = agentId,
                Observatory = new ObservatoryLocation(35, -114, 1_500, "UTC"),
                CentralIntegration = new CentralIntegrationOptions { Mode = mode }
            }),
            NullLogger<FileCameraAgentConfigurationLoader>.Instance,
            captureAgentIdentityProvider: provider);

    private sealed class FixedCaptureAgentIdentityProvider(string agentId) : ICaptureAgentIdentityProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<string?> GetAgentIdAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult<string?>(agentId);
        }
    }
}
