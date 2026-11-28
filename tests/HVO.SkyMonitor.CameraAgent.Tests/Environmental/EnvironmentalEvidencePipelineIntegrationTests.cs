using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Services;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Integration")]
[TestProperty("Area", "Environmental")]
public sealed partial class EnvironmentalEvidencePipelineIntegrationTests
{
    private static readonly DateTimeOffset Epoch = EnvironmentalHardwareAdapterTests.Epoch;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StationAndInfraredLineageInputsDoNotCompeteForAirTemperature(bool simulated)
    {
        var root = Root();
        try
        {
            var clock = new Clock();
            using var store = new SqliteEnvironmentalObservationStore(clock);
            var sources = new[] { Configuration("station-temperature", "LocalWeatherStation", EnvironmentalObservationKind.AirTemperature, "station"),
                Configuration("ir-temperature", "InfraredSky", EnvironmentalObservationKind.SkyTemperature, "ir"),
                Configuration("ir-cloud", "InfraredSky", EnvironmentalObservationKind.CloudCover, "ir") };
            var options = Options.Create(new CameraAgentHostOptions
            { RawIngressRoot = root, EnvironmentalAcquisition = new() { Enabled = true, Sources = sources } });
            var publisher = new Publisher(store, root);
            using var provider = new ServiceCollection().AddSingleton<IEnvironmentalDevice>(new Devices(simulated))
                .AddSingleton<IEnvironmentalObservationPublisher>(publisher).BuildServiceProvider();
            var factory = new EnvironmentalSourceFactory(provider, EnvironmentalAdapterPipelineTests.Registrations());
            using var coordinator = new EnvironmentalAcquisitionCoordinator(factory, publisher, new Locations(), options, clock, store);
            foreach (var source in sources)
            {
                if (source.Id == "ir-cloud") clock.Now = Epoch.AddSeconds(10);
                Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced,
                    (await coordinator.AcquireSourceAsync(source.Id, EnvironmentalAcquisitionTrigger.Periodic, clock.Now).ConfigureAwait(false)).Disposition);
            }
            clock.Now = Epoch.AddSeconds(20);
            var service = new EnvironmentalAssociationService(store, store, options, clock, acquisitionStates: store);
            var associations = await service.AssociateAsync(Guid.NewGuid(), 1, clock.Now, clock.Now.AddSeconds(1), null,
                [EnvironmentalObservationKind.AirTemperature, EnvironmentalObservationKind.CloudCover], CancellationToken.None).ConfigureAwait(false);
            var air = associations.Single(item => item.Kind == EnvironmentalObservationKind.AirTemperature);
            Assert.AreEqual(LocalEnvironmentalAssociationStatus.Fresh, air.Status);
            var selected = await store.ReadLocalDetailAsync(root, air.SelectedRecordId!.Value, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("station-temperature", selected!.Fact.Source.SourceId);
            Assert.AreEqual(20.0, selected.Fact.Value.NumericValue!.Value);
            var history = await store.ReadLocalPageAsync(root, EnvironmentalObservationKind.AirTemperature, 100, null, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(2, history.Items, "Lineage evidence remains retained even though it is not a primary reading.");
            Assert.AreEqual("ir-cloud-ambient", history.Items[0].Fact.Source.SourceId);
            var (principal, authorization) = CameraAgentEnvironmentalUiServiceTests.ReadAuthorized();
            var ui = CameraAgentEnvironmentalUiServiceTests.CreateService(principal, authorization, sources[0],
                stateStore: store, observationStore: store, root: root, timeProvider: clock, hostOptions: options.Value);
            var latest = await ui.GetLatestReadingsAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(latest.IsSuccess);
            Assert.AreEqual("station-temperature", latest.Value!.Single(item => item.Kind == EnvironmentalObservationKind.AirTemperature).SourceId);
            Assert.AreEqual("ir-temperature", latest.Value!.Single(item => item.Kind == EnvironmentalObservationKind.SkyTemperature).SourceId);
            using var context = new BunitContext();
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(ui);
            var page = context.Render<EnvironmentalPage>();
            page.WaitForAssertion(() =>
            {
                var airSlot = page.Find("[data-slot='air-temperature']").TextContent;
                StringAssert.Contains(airSlot, "station-temperature", StringComparison.Ordinal);
                Assert.IsFalse(airSlot.Contains("No observation yet", StringComparison.Ordinal));
            });
            var cloud = associations.Single(item => item.Kind == EnvironmentalObservationKind.CloudCover);
            var cloudRecord = await store.ReadLocalDetailAsync(root, cloud.SelectedRecordId!.Value, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(simulated ? EnvironmentalObservationSourceKind.Simulated : EnvironmentalObservationSourceKind.Derived,
                cloudRecord!.Fact.Source.Kind);
        }
        finally { Delete(root); }
    }

    [TestMethod]
    public async Task LatestPrimaryQueryFindsTheStationBehindMoreThanOneThousandNewerInputs()
    {
        var root = Root();
        try
        {
            var clock = new Clock { Now = Epoch.AddSeconds(20) };
            using var store = new SqliteEnvironmentalObservationStore(clock);
            var station = EnvironmentalAdapterContractsTests.Fact("station", EnvironmentalObservationKind.AirTemperature, 20);
            await store.CommitLocalAsync(root, station, CancellationToken.None).ConfigureAwait(false);
            var (inputs, _) = await InfraredAsync().ConfigureAwait(false);
            var ambient = inputs.Single(item => item.Value.Kind == station.Value.Kind);
            for (var index = 0; index < 1001; index++)
                await store.CommitLocalAsync(root, ambient with { ObservationId = Guid.NewGuid(), ObservedAtUtc = Epoch.AddSeconds(10) },
                    CancellationToken.None).ConfigureAwait(false);
            var (principal, authorization) = CameraAgentEnvironmentalUiServiceTests.ReadAuthorized();
            var ui = CameraAgentEnvironmentalUiServiceTests.CreateService(principal, authorization,
                Configuration("station", "LocalWeatherStation", station.Value.Kind, "station"),
                stateStore: store, observationStore: store, root: root, timeProvider: clock);
            var latest = await ui.GetLatestReadingsAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(latest.IsSuccess);
            Assert.AreEqual("station", latest.Value!.Single(item => item.Kind == station.Value.Kind).SourceId);
        }
        finally { Delete(root); }
    }

    private static async Task<(IReadOnlyList<EnvironmentalObservationFactV1> Inputs, EnvironmentalObservationFactV1 Derived)> InfraredAsync()
    {
        var publisher = new EnvironmentalHardwareAdapterTests.RecordingPublisher();
        var source = new InfraredSkySource(EnvironmentalHardwareAdapterTests.Descriptor("InfraredSky", EnvironmentalObservationKind.CloudCover),
            EnvironmentalHardwareAdapterTests.Options(EnvironmentalHardwareAdapterTests.Infrared), publisher: publisher);
        var result = await source.AcquireAsync(EnvironmentalHardwareAdapterTests.Context(), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(result.Fact);
        return (publisher.Facts, result.Fact);
    }

    private static EnvironmentalSourceConfiguration Configuration(string id, string type, EnvironmentalObservationKind kind, string device)
        => new()
        {
            Id = id,
            Type = type,
            Kind = kind,
            Triggers = [EnvironmentalAcquisitionTrigger.Periodic],
            StaleAfterSeconds = 300,
            ValidForSeconds = 900,
            Options = JsonSerializer.SerializeToElement(new EnvironmentalDeviceOptions(device), JsonOptions)
        };

    private static string Root() => Path.Combine(Path.GetTempPath(), "hvo-environment-evidence-" + Guid.NewGuid().ToString("N"));
    private static void Delete(string root) { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } = Epoch; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Devices(bool simulated) : IEnvironmentalDevice
    {
        public ValueTask<EnvironmentalDeviceSample> ReadAsync(string deviceId, string command, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken)
            => ValueTask.FromResult(new EnvironmentalDeviceSample(Encoding.UTF8.GetBytes(deviceId == "station"
                ? EnvironmentalHardwareAdapterTests.Station : EnvironmentalHardwareAdapterTests.Infrared.Replace("10,", "18.5,", StringComparison.Ordinal)),
                receivedAtUtc, simulated, ReceivedAtUtc: receivedAtUtc));
    }
    private sealed class Publisher(SqliteEnvironmentalObservationStore store, string root)
        : IEnvironmentalObservationPublisher, IEnvironmentalHeldPollPublisher
    {
        public bool SupportsHeldPollSettlement => true;

        public async ValueTask<EnvironmentalAcquisitionReceipt> PublishHeldPollAsync(
            EnvironmentalObservationFactV1 fact, EnvironmentalHeldPoll poll, CancellationToken cancellationToken)
            => (await store.CommitHeldPollAsync(root, fact, poll, cancellationToken).ConfigureAwait(false)).HeldPollReceipt!;

        public async ValueTask<EnvironmentalObservationPublishResult> PublishAsync(EnvironmentalObservationFactV1 fact, CancellationToken cancellationToken = default)
        { await store.CommitLocalAsync(root, fact, cancellationToken).ConfigureAwait(false); return new(EnvironmentalObservationPublishDisposition.Committed); }
    }
    private sealed class Locations : IDeploymentLocationStore
    {
        public DeploymentLocationSnapshot? Active => EnvironmentalHardwareAdapterTests.Context().DeploymentLocation;
        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(DeploymentLocationSeed seed, CancellationToken cancellationToken) => ValueTask.FromResult(Active!);
        public DeploymentLocationSnapshot Resolve(CaptureLocationProvenance provenance, DateTimeOffset? effectiveUtc = null) => Active!;
    }
}
