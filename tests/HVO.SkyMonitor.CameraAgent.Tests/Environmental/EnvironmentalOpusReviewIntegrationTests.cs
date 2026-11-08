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
public sealed partial class EnvironmentalOpusReviewIntegrationTests
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
            using var store = new SqliteEnvironmentalObservationOutbox(new Clock());
            var sources = new[] { Configuration("station-temperature", "LocalWeatherStation", EnvironmentalObservationKind.AirTemperature, "station"),
                Configuration("ir-cloud", "InfraredSky", EnvironmentalObservationKind.CloudCover, "ir") };
            var options = Options.Create(new CameraAgentHostOptions
            { RawIngressRoot = root, EnvironmentalAcquisition = new() { Enabled = true, Sources = sources } });
            var publisher = new Publisher(store, root);
            using var provider = new ServiceCollection().AddSingleton<IEnvironmentalDevice>(new Devices(simulated))
                .AddSingleton<IEnvironmentalObservationPublisher>(publisher).BuildServiceProvider();
            var factory = new EnvironmentalSourceFactory(provider, EnvironmentalAdapterPipelineTests.Registrations());
            using var coordinator = new EnvironmentalAcquisitionCoordinator(factory, publisher, new Locations(), options, new Clock(), store);
            foreach (var source in sources)
                Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced,
                    (await coordinator.AcquireSourceAsync(source.Id, EnvironmentalAcquisitionTrigger.Periodic, Epoch).ConfigureAwait(false)).Disposition);
            var service = new EnvironmentalAssociationService(store, store, options, new Clock(), acquisitionStates: store);
            var associations = await service.AssociateAsync(Guid.NewGuid(), 1, Epoch, Epoch.AddSeconds(1), null,
                [EnvironmentalObservationKind.AirTemperature, EnvironmentalObservationKind.CloudCover], CancellationToken.None).ConfigureAwait(false);
            var air = associations.Single(item => item.Kind == EnvironmentalObservationKind.AirTemperature);
            Assert.AreEqual(LocalEnvironmentalAssociationStatus.Fresh, air.Status);
            var selected = await store.ReadLocalDetailAsync(root, air.SelectedRecordId!.Value, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("station-temperature", selected!.Fact.Source.SourceId);
            Assert.AreEqual(20.0, selected.Fact.Value.NumericValue!.Value);
            var history = await store.ReadLocalPageAsync(root, EnvironmentalObservationKind.AirTemperature, 100, null, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(2, history.Items, "Lineage evidence remains retained even though it is not a primary reading.");
            var latest = EnvironmentalReadingSelection.Select(history.Items, options.Value.EnvironmentalAcquisition, [],
                EnvironmentalObservationKind.AirTemperature, Epoch, Epoch.AddSeconds(1));
            Assert.HasCount(1, latest);
            Assert.AreEqual("station-temperature", latest[0].Fact.Source.SourceId);
            var cloud = associations.Single(item => item.Kind == EnvironmentalObservationKind.CloudCover);
            var cloudRecord = await store.ReadLocalDetailAsync(root, cloud.SelectedRecordId!.Value, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(simulated ? EnvironmentalObservationSourceKind.Simulated : EnvironmentalObservationSourceKind.Derived,
                cloudRecord!.Fact.Source.Kind);
        }
        finally { Delete(root); }
    }

    [TestMethod]
    public async Task DerivedDeliveryWaitsUntilBothLineageInputsAreAcknowledgedAfterRetry()
    {
        var root = Root();
        try
        {
            var clock = new Clock();
            using var store = new SqliteEnvironmentalObservationOutbox(clock);
            var (inputs, derived) = await InfraredAsync().ConfigureAwait(false);
            var target = new EnvironmentalObservationResolvedTarget(Guid.NewGuid(), Guid.NewGuid());
            foreach (var input in inputs) await store.CommitLocalAsync(root, input, target, CancellationToken.None).ConfigureAwait(false);
            await store.CommitLocalAsync(root, derived, target, CancellationToken.None).ConfigureAwait(false);
            for (var index = 0; index < 2; index++)
            {
                var input = await ClaimAsync(store, root).ConfigureAwait(false);
                Assert.AreNotEqual(EnvironmentalObservationKind.CloudCover, input.Record.Observation.Value.Kind);
                await store.RetryAsync(root, input, Epoch.AddSeconds(30), "http-503", CancellationToken.None).ConfigureAwait(false);
            }
            var independent = EnvironmentalAdapterReviewUnitTests.Fact("independent", EnvironmentalObservationKind.AirTemperature, 20);
            await store.CommitLocalAsync(root, independent, target, CancellationToken.None).ConfigureAwait(false);
            var ready = await ClaimAsync(store, root).ConfigureAwait(false);
            Assert.AreEqual(independent.ObservationId, ready.Record.Observation.ObservationId);
            await AckAsync(store, root, ready, Epoch).ConfigureAwait(false);
            Assert.IsNull(await store.ClaimAsync(root, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false));
            clock.Now = Epoch.AddSeconds(31);
            for (var index = 0; index < 2; index++)
            {
                var input = await ClaimAsync(store, root).ConfigureAwait(false);
                Assert.AreNotEqual(EnvironmentalObservationKind.CloudCover, input.Record.Observation.Value.Kind);
                await AckAsync(store, root, input, clock.Now).ConfigureAwait(false);
            }
            var cloud = await ClaimAsync(store, root).ConfigureAwait(false);
            Assert.AreEqual(EnvironmentalObservationKind.CloudCover, cloud.Record.Observation.Value.Kind);
            await AckAsync(store, root, cloud, clock.Now).ConfigureAwait(false);
            Assert.AreEqual(0, (await store.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false)).StoredCount);
        }
        finally { Delete(root); }
    }

    [TestMethod]
    public async Task UnresolvedLineageRotatesPastLaterWaitingProjectionAtAConstantClock()
    {
        var root = Root();
        try
        {
            using var store = new SqliteEnvironmentalObservationOutbox(new Clock(), maximumRecords: 1);
            var target = new EnvironmentalObservationResolvedTarget(Guid.NewGuid(), Guid.NewGuid());
            var independent = EnvironmentalAdapterReviewUnitTests.Fact("independent", EnvironmentalObservationKind.AirTemperature, 20);
            await store.CommitLocalAsync(root, independent, target, CancellationToken.None).ConfigureAwait(false);
            var (_, derived) = await InfraredAsync().ConfigureAwait(false);
            await store.CommitLocalAsync(root, derived, target, CancellationToken.None).ConfigureAwait(false);
            var later = independent with { ObservationId = Guid.NewGuid() };
            var result = await store.CommitLocalAsync(root, later, target, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(EnvironmentalObservationProjectionDisposition.Waiting, result.ProjectionDisposition);
            await AckAsync(store, root, await ClaimAsync(store, root).ConfigureAwait(false), Epoch).ConfigureAwait(false);
            var progressed = await store.ProjectWaitingAsync(root, 1, CancellationToken.None).ConfigureAwait(false);
            progressed += await store.ProjectWaitingAsync(root, 1, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, progressed, "Unresolved lineage must allow the independent record to progress within two bounded passes.");
            Assert.AreEqual(later.ObservationId, (await ClaimAsync(store, root).ConfigureAwait(false)).Record.Observation.ObservationId);
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

    private static async Task<EnvironmentalObservationOutboxLease> ClaimAsync(SqliteEnvironmentalObservationOutbox store, string root)
        => await store.ClaimAsync(root, "worker", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false)
            ?? throw new AssertFailedException("Expected an eligible delivery.");

    private static ValueTask AckAsync(SqliteEnvironmentalObservationOutbox store, string root, EnvironmentalObservationOutboxLease lease, DateTimeOffset now)
        => store.AcknowledgeAsync(root, lease, new(EnvironmentalObservationAcknowledgement.CurrentSchemaVersion,
            lease.Record.Observation.ObservationId, lease.Record.SourceIdentitySha256, lease.Record.ContentSha256,
            now, EnvironmentalObservationDeliveryDisposition.Accepted), CancellationToken.None);

    private static string Root() => Path.Combine(Path.GetTempPath(), "hvo-opus-" + Guid.NewGuid().ToString("N"));
    private static void Delete(string root) { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } = Epoch; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Devices(bool simulated) : IEnvironmentalDevice
    {
        public ValueTask<EnvironmentalDeviceSample> ReadAsync(string deviceId, string command, DateTimeOffset receivedAtUtc, CancellationToken cancellationToken)
            => ValueTask.FromResult(new EnvironmentalDeviceSample(Encoding.UTF8.GetBytes(deviceId == "station"
                ? EnvironmentalHardwareAdapterTests.Station : EnvironmentalHardwareAdapterTests.Infrared.Replace("10,", "18.5,", StringComparison.Ordinal)),
                Epoch, simulated, ReceivedAtUtc: Epoch));
    }
    private sealed class Publisher(SqliteEnvironmentalObservationOutbox store, string root) : IEnvironmentalObservationPublisher
    {
        public async ValueTask<EnvironmentalObservationPublishResult> PublishAsync(EnvironmentalObservationFactV1 fact, CancellationToken cancellationToken = default)
        { await store.CommitLocalAsync(root, fact, cancellationToken).ConfigureAwait(false); return new(EnvironmentalObservationPublishDisposition.Enqueued, null); }
    }
    private sealed class Locations : IDeploymentLocationStore
    {
        public DeploymentLocationSnapshot? Active => EnvironmentalHardwareAdapterTests.Context().DeploymentLocation;
        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(DeploymentLocationSeed seed, CancellationToken cancellationToken) => ValueTask.FromResult(Active!);
        public DeploymentLocationSnapshot Resolve(CaptureLocationProvenance provenance, DateTimeOffset? effectiveUtc = null) => Active!;
    }
}
