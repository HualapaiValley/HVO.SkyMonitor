using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using HVO.SkyMonitor.CameraAgent.Tests.Orbital;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.OrbitalPlanning;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentSatellitePlanningRuntimeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RealRegistrationRetainsDisabledDefaultsAndNeverLoadsTheSnapshotAsync()
    {
        var host = await RuntimeHost.CreateAsync(enabled: false).ConfigureAwait(false);
        await using var lifetime = host.ConfigureAwait(false);
        Assert.IsInstanceOfType<OrbitalSnapshotReader>(host.Provider.GetRequiredService<IOrbitalSnapshotReader>());
        Assert.IsInstanceOfType<CameraAgentPinnedOrbitalTimeSource>(host.Provider.GetRequiredService<ICameraAgentOrbitalTimeSource>());
        using var scope = host.Provider.CreateScope();
        Assert.AreSame(host.Planner, scope.ServiceProvider.GetRequiredService<ICameraAgentSatellitePlanner>());
        var options = host.Provider.GetRequiredService<IOptionsMonitor<CameraAgentSatellitePlanningOptions>>().CurrentValue;
        Assert.IsFalse(options.Enabled);
        Assert.IsFalse(options.AllowStaleDiagnostics);
        Assert.IsNull(options.EarthOrientationPath);
        var response = await host.Planner.SearchAsync(await host.RequestAsync().ConfigureAwait(false), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.TimeInputsUnavailable, response.Outcome);
        Assert.IsFalse(Directory.Exists(host.OrbitalRoot), "Disabled planning must not initialize/materialize the orbital store.");
        Assert.AreEqual(0, host.Handler.Count);
    }

    [TestMethod]
    public async Task RealReaderPinnedTimeAndEnginesRetainPublishedLineageThroughDetailAsync()
    {
        var host = await RuntimeHost.CreateAsync().ConfigureAwait(false);
        await using var lifetime = host.ConfigureAwait(false);
        var candidate = await host.PublishAsync("planning-A").ConfigureAwait(false);
        host.Clock.Now = PlanningTestInputs.Start;
        var search = await host.SearchAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var time = await host.Provider.GetRequiredService<ICameraAgentOrbitalTimeSource>().ReadAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsNotNull(time);
        Assert.IsFalse(time.NominalZeroEarthOrientation);
        Assert.AreEqual(PlanningTestInputs.TimeData().EarthOrientationSha256, time.EarthOrientationSha256);
        Assert.AreEqual(PlanningTestInputs.TimeData().LeapSecondsSha256, time.LeapSecondsSha256);
        Assert.IsTrue(search.Passes.Count > 0, "The synthetic store record must produce at least one geometric pass in the bounded window.");
        var parent = search.Passes[0].Pass;
        Assert.AreEqual(candidate.Manifest, parent.Lineage.SnapshotManifestSha256);
        Assert.AreEqual(candidate.Payload, parent.Lineage.NormalizedPayloadSha256);
        Assert.AreEqual(candidate.Snapshot.Records.Single().RecordSha256, parent.Lineage.RecordSha256);
        Assert.AreEqual(time.EarthOrientationSha256, parent.Model.EarthOrientationSha256);
        Assert.AreEqual(time.LeapSecondsSha256, parent.Model.LeapSecondsSha256);
        var detail = await host.Planner.GetArcAsync(search.SearchIdentitySha256, parent.ProductSha256, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, detail.Outcome, detail.Message);
        Assert.IsTrue(detail.Value!.Prediction.Products.Count > 0);
        var arc = detail.Value.Prediction.Products.Single();
        Assert.AreEqual(parent.ProductSha256, arc.Pass.ProductSha256);
        Assert.AreEqual(parent.SearchInterval, arc.Pass.SearchInterval);
        Assert.AreEqual(candidate.Manifest, arc.Pass.Lineage.SnapshotManifestSha256);
        Assert.IsTrue(arc.Samples.All(sample => sample.Pixel is null && sample.Illumination == OrbitalIllumination.Unknown));
        Assert.AreEqual(0, host.Handler.Count, "Local planning cannot invoke the named provider client.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DisabledOrHaltedRefreshKeepsUnchangedLocalPlanningAvailableAsync(bool halt)
    {
        var host = await RuntimeHost.CreateAsync().ConfigureAwait(false);
        await using var lifetime = host.ConfigureAwait(false);
        var candidate = await host.PublishAsync("planning-LKG").ConfigureAwait(false);
        host.Clock.Now = PlanningTestInputs.Start;
        var search = await host.SearchAsync(TestContext.CancellationToken).ConfigureAwait(false);
        if (halt)
        {
            var attempt = await OrbitalElementsStoreTestData.AdmitAsync(host.Store).ConfigureAwait(false);
            await host.Store.RecordHaltAsync(attempt, null, "orbital.provider-outcome-unknown", TestContext.CancellationToken).ConfigureAwait(false);
        }
        var state = await host.Store.GetStateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(candidate.Manifest, state.CurrentManifestSha256);
        Assert.AreEqual(halt, state.Halt is not null);
        var parent = search.Passes[0].Pass;
        var detail = await host.Planner.GetArcAsync(search.SearchIdentitySha256, parent.ProductSha256, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, detail.Outcome, detail.Message);
        Assert.AreEqual(candidate.Manifest, detail.Value!.Prediction.Products.Single().Pass.Lineage.SnapshotManifestSha256);
        Assert.AreEqual(0, host.Handler.Count);
    }

    [TestMethod]
    public async Task RealSnapshotActivationRejectsOldDetailWithoutRebindingItsOriginalParentAsync()
    {
        var host = await RuntimeHost.CreateAsync().ConfigureAwait(false);
        await using var lifetime = host.ConfigureAwait(false);
        var first = await host.PublishAsync("planning-before").ConfigureAwait(false);
        host.Clock.Now = PlanningTestInputs.Start;
        var search = await host.SearchAsync(TestContext.CancellationToken).ConfigureAwait(false);
        var second = await host.PublishAsync("planning-after").ConfigureAwait(false);
        Assert.AreNotEqual(first.Manifest, second.Manifest);
        var parent = search.Passes[0].Pass;
        var detail = await host.Planner.GetArcAsync(search.SearchIdentitySha256, parent.ProductSha256, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.ContextChanged, detail.Outcome, detail.Message);
        Assert.IsNull(detail.Value);
        Assert.AreEqual(first.Manifest, parent.Lineage.SnapshotManifestSha256);
        Assert.AreEqual(second.Manifest, (await host.Provider.GetRequiredService<IOrbitalSnapshotReader>()
            .ReadCurrentAsync(TestContext.CancellationToken).ConfigureAwait(false)).Snapshot!.Manifest.ManifestSha256);
        Assert.AreEqual(0, host.Handler.Count);
    }

    [TestMethod]
    public async Task WrongActualTimePinFailsBeforeRealStoreInitializationAsync()
    {
        var host = await RuntimeHost.CreateAsync(invalidPin: true).ConfigureAwait(false);
        await using var lifetime = host.ConfigureAwait(false);
        var response = await host.Planner.SearchAsync(await host.RequestAsync().ConfigureAwait(false), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.TimeInputsUnavailable, response.Outcome);
        Assert.IsFalse(Directory.Exists(host.OrbitalRoot));
        Assert.AreEqual(0, host.Handler.Count);
    }

    [TestMethod]
    public async Task EmptyInstalledStoreReturnsUnavailableRatherThanNoMatchesAsync()
    {
        var host = await RuntimeHost.CreateAsync().ConfigureAwait(false);
        await using var lifetime = host.ConfigureAwait(false);
        var response = await host.Planner.SearchAsync(await host.RequestAsync().ConfigureAwait(false), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.NoSnapshot, response.Outcome);
        Assert.IsNull(response.Value);
        Assert.AreEqual(0, host.Handler.Count);
    }

    [SuppressMessage("Maintainability", "CA1506:Avoid excessive class coupling", Justification = "Composes the actual host-neutral infrastructure, reader, pinned time loader and planner for adoption tests.")]
    private sealed class RuntimeHost : IAsyncDisposable
    {
        private readonly string root;
        private readonly PlanningTestInputs.Map sky;
        internal readonly OrbitalElementsStoreTestData.Clock Clock = new(PlanningTestInputs.Start.AddHours(-2));
        internal readonly ScriptedCelesTrakHandler Handler = new();
        internal ServiceProvider Provider { get; private set; } = null!;
        internal ICameraAgentSatellitePlanner Planner => Provider.GetRequiredService<ICameraAgentSatellitePlanner>();
        internal SqliteOrbitalElementsStore Store => Provider.GetRequiredService<SqliteOrbitalElementsStore>();
        internal string OrbitalRoot => Path.Combine(root, "ingress", ".orbital-elements");

        private RuntimeHost(string path)
        {
            root = path;
            sky = new(new PlanningTestInputs.Config(), Clock);
        }

        internal static async Task<RuntimeHost> CreateAsync(bool enabled = true, bool invalidPin = false)
        {
            var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-planning-runtime");
            var host = new RuntimeHost(root);
            var values = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["CameraAgent:RawIngressRoot"] = Path.Combine(root, "ingress"),
                ["CameraAgent:RawIngressReserveBytes"] = "0",
                ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false",
                ["CameraAgent:OrbitalElements:Enabled"] = "false"
            };
            if (enabled)
            {
                var eop = PlanningTestInputs.Bytes("time", "predicted-2026.finals2000A");
                var leaps = PlanningTestInputs.Bytes("time", "leap-seconds.list");
                var eopPath = Path.Combine(root, "eop");
                var leapPath = Path.Combine(root, "leaps");
                await File.WriteAllBytesAsync(eopPath, eop).ConfigureAwait(false);
                await File.WriteAllBytesAsync(leapPath, leaps).ConfigureAwait(false);
                values["CameraAgent:SatellitePlanning:Enabled"] = "true";
                values["CameraAgent:SatellitePlanning:EarthOrientationPath"] = eopPath;
                values["CameraAgent:SatellitePlanning:EarthOrientationSha256"] = invalidPin ? new string('0', 64) : PlanningTestInputs.Hash(eop);
                values["CameraAgent:SatellitePlanning:LeapSecondsPath"] = leapPath;
                values["CameraAgent:SatellitePlanning:LeapSecondsSha256"] = PlanningTestInputs.Hash(leaps);
            }
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(host.Clock);
            services.AddCameraAgentInfrastructure(configuration);
            services.AddCameraAgentSatellitePlanning(configuration);
            services.AddSingleton<IDeploymentLocationProtector>(new OrbitalPassThroughLocationProtector());
            services.AddSingleton<ICameraAgentConfigurationAccessor>(new PlanningTestInputs.Config());
            services.AddSingleton<ICameraAgentSkyMapProjection>(host.sky);
            services.AddHttpClient(CelesTrakGpSource.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => host.Handler);
            host.Provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            var handler = host.Provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(CelesTrakGpSource.HttpClientName);
            while (handler is DelegatingHandler outer) handler = outer.InnerHandler!;
            Assert.AreSame(host.Handler, handler, "The provider handler must be intercepted before any test work.");
            return host;
        }

        internal async Task<OrbitalElementsStoreTestData.Candidate> PublishAsync(string marker)
        {
            await Store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var candidate = OrbitalElementsStoreTestData.Build(marker, "25544");
            var attempt = await OrbitalElementsStoreTestData.AdmitAsync(Store).ConfigureAwait(false);
            var publication = await Store.PublishAndActivateAsync(attempt, candidate.Snapshot, candidate.Raw, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(candidate.Manifest, publication.ManifestSha256);
            return candidate;
        }

        internal async Task<CameraAgentSatellitePassSearchRequest> RequestAsync()
        {
            var scene = await sky.ProjectAsync(PlanningTestInputs.Start, CancellationToken.None).ConfigureAwait(false);
            return new(PlanningTestInputs.Start, PlanningTestInputs.Start.AddHours(6), "25544", 0,
                scene.Observer.CanonicalSha256, scene.Geometry.RigProfileHashSha256);
        }

        internal async Task<CameraAgentSatellitePassSearchView> SearchAsync(CancellationToken cancellationToken)
        {
            var response = await Planner.SearchAsync(await RequestAsync().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, response.Outcome, response.Message);
            Assert.IsTrue(response.Value!.Passes.Count > 0);
            return response.Value;
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync().ConfigureAwait(false);
            Handler.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
}
