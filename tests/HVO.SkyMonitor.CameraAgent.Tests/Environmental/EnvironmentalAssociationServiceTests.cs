using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Integration")]
public sealed class EnvironmentalAssociationServiceTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private string? _root;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), $"environment-association-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (_root is not null && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AssociationsPersistFreshStaleAndMissingWithHalfOpenStaleBoundary()
    {
        using var store = new SqliteEnvironmentalObservationOutbox(new FixedTimeProvider(Epoch.AddMinutes(1)));
        await store.CommitLocalAsync(_root!, Fact(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            EnvironmentalObservationKind.AirTemperature,
            12,
            null,
            staleAfter: Epoch.AddSeconds(5)), CancellationToken.None).ConfigureAwait(false);
        await store.CommitLocalAsync(_root!, Fact(
            Guid.Parse("10000000-0000-0000-0000-000000000002"),
            EnvironmentalObservationKind.RelativeHumidity,
            42,
            null,
            staleAfter: Epoch.AddSeconds(4)), CancellationToken.None).ConfigureAwait(false);
        var service = CreateService(store);

        var associations = await service.AssociateAsync(
            Guid.Parse("20000000-0000-0000-0000-000000000001"),
            1,
            Epoch.AddSeconds(4),
            Epoch.AddSeconds(5),
            "rig-1",
            [
                EnvironmentalObservationKind.AirTemperature,
                EnvironmentalObservationKind.RelativeHumidity,
                EnvironmentalObservationKind.AtmosphericPressure
            ],
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(LocalEnvironmentalAssociationStatus.Fresh, associations.Single(
            association => association.Kind == EnvironmentalObservationKind.AirTemperature).Status);
        Assert.AreEqual(LocalEnvironmentalAssociationStatus.Stale, associations.Single(
            association => association.Kind == EnvironmentalObservationKind.RelativeHumidity).Status);
        Assert.AreEqual(LocalEnvironmentalAssociationStatus.Missing, associations.Single(
            association => association.Kind == EnvironmentalObservationKind.AtmosphericPressure).Status);
        var subset = await service.ReadCompletedAsync(
            associations[0].CaptureId,
            1,
            Epoch.AddSeconds(4),
            Epoch.AddSeconds(5),
            "rig-1",
            [EnvironmentalObservationKind.AirTemperature],
            CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(subset);
        Assert.HasCount(1, subset);
        Assert.AreEqual(LocalEnvironmentalAssociationStatus.Fresh, subset[0].Status);
        var persisted = await store.ReadAssociationsAsync(
            _root!, associations[0].CaptureId, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(3, persisted);
        CollectionAssert.AreEquivalent(
            associations.Select(static association => association.AssociationIdentitySha256).ToArray(),
            persisted.Select(static association => association.AssociationIdentitySha256).ToArray());
    }

    [TestMethod]
    public async Task W6AllKindPolicyServesCloudAndPresentationSubsetsWithoutChangingPolicyIdentity()
    {
        using var store = new SqliteEnvironmentalObservationOutbox();
        var allKinds = new[]
        {
            EnvironmentalObservationKind.AirTemperature,
            EnvironmentalObservationKind.RelativeHumidity,
            EnvironmentalObservationKind.AtmosphericPressure,
            EnvironmentalObservationKind.WindSpeed,
            EnvironmentalObservationKind.RainState,
            EnvironmentalObservationKind.CloudCover
        };
        var service = CreateService(store, allKinds);
        var captureId = Guid.Parse("21000000-0000-0000-0000-000000000001");
        var persisted = await service.AssociateAsync(captureId, 7, Epoch.AddSeconds(1), Epoch.AddSeconds(2),
            "rig-1", allKinds, CancellationToken.None).ConfigureAwait(false);
        var policy = EnvironmentalAssociationService.CreatePolicyIdentity(allKinds);

        var cloud = await service.ReadCompletedAsync(captureId, 7, Epoch.AddSeconds(1), Epoch.AddSeconds(2),
            "rig-1", [EnvironmentalObservationKind.RainState], CancellationToken.None).ConfigureAwait(false);
        var presentationKinds = allKinds.Where(static kind => kind != EnvironmentalObservationKind.RainState).ToArray();
        var presentation = await service.ReadCompletedAsync(captureId, 7, Epoch.AddSeconds(1), Epoch.AddSeconds(2),
            "rig-1", presentationKinds, CancellationToken.None).ConfigureAwait(false);

        Assert.HasCount(6, persisted);
        Assert.IsNotNull(cloud);
        Assert.IsNotNull(presentation);
        Assert.HasCount(1, cloud);
        Assert.HasCount(5, presentation);
        Assert.IsTrue(cloud.All(item => item.PolicyIdentitySha256 == policy));
        Assert.IsTrue(presentation.All(item => item.PolicyIdentitySha256 == policy));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await service.ReadCompletedAsync(
            captureId, 7, Epoch.AddSeconds(1), Epoch.AddSeconds(2), "rig-1",
            [EnvironmentalObservationKind.RainState, EnvironmentalObservationKind.RainState],
            CancellationToken.None).AsTask().ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ChangedConfiguredKindPolicyRejectsOldPersistedSet()
    {
        using var store = new SqliteEnvironmentalObservationOutbox();
        var oldKinds = new[]
        {
            EnvironmentalObservationKind.RainState,
            EnvironmentalObservationKind.CloudCover
        };
        var captureId = Guid.Parse("22000000-0000-0000-0000-000000000001");
        _ = await CreateService(store, oldKinds).AssociateAsync(captureId, 8,
            Epoch.AddSeconds(1), Epoch.AddSeconds(2), "rig-1", oldKinds, CancellationToken.None).ConfigureAwait(false);
        var changed = CreateService(store,
            [.. oldKinds, EnvironmentalObservationKind.RelativeHumidity]);

        var result = await changed.ReadCompletedAsync(captureId, 8, Epoch.AddSeconds(1), Epoch.AddSeconds(2),
            "rig-1", [EnvironmentalObservationKind.RainState], CancellationToken.None).ConfigureAwait(false);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void AssociationLanePolicyUsesOnlyEnabledConfiguredDistinctSortedKinds()
    {
        var sources = new[]
        {
            Source(EnvironmentalObservationKind.RainState, "rain"),
            Source(EnvironmentalObservationKind.AirTemperature, "temperature"),
            Source(EnvironmentalObservationKind.RainState, "rain-copy")
        };

        CollectionAssert.AreEqual(
            new[] { EnvironmentalObservationKind.AirTemperature, EnvironmentalObservationKind.RainState },
            EnvironmentalAssociationCaptureLaneHandler.ResolvePolicyKinds(
                new EnvironmentalAcquisitionOptions { Enabled = true, Sources = sources }));
        Assert.IsEmpty(EnvironmentalAssociationCaptureLaneHandler.ResolvePolicyKinds(
            new EnvironmentalAcquisitionOptions { Enabled = false, Sources = sources }));
        Assert.IsEmpty(EnvironmentalAssociationCaptureLaneHandler.ResolvePolicyKinds(
            new EnvironmentalAcquisitionOptions { Enabled = true, Sources = [] }));

        static EnvironmentalSourceConfiguration Source(EnvironmentalObservationKind kind, string id) => new()
        {
            Id = id,
            Type = "Test",
            Kind = kind,
            Triggers = [EnvironmentalAcquisitionTrigger.Periodic]
        };
    }

    [TestMethod]
    public async Task EqualTierContradictionsRemainContradictoryAndDeterministicAfterRestart()
    {
        using (var store = new SqliteEnvironmentalObservationOutbox())
        {
            await store.CommitLocalAsync(_root!, Fact(
                Guid.Parse("30000000-0000-0000-0000-000000000001"),
                EnvironmentalObservationKind.CloudCover,
                0.1,
                null,
                uncertainty: 0.01,
                sourceId: "cloud-a"), CancellationToken.None).ConfigureAwait(false);
            await store.CommitLocalAsync(_root!, Fact(
                Guid.Parse("30000000-0000-0000-0000-000000000002"),
                EnvironmentalObservationKind.CloudCover,
                0.9,
                null,
                uncertainty: 0.01,
                sourceId: "cloud-b"), CancellationToken.None).ConfigureAwait(false);
            var association = AssertSingle(await CreateService(store).AssociateAsync(
                Guid.Parse("40000000-0000-0000-0000-000000000001"),
                2,
                Epoch.AddSeconds(1),
                Epoch.AddSeconds(2),
                "rig-1",
                [EnvironmentalObservationKind.CloudCover],
                CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(LocalEnvironmentalAssociationStatus.Contradictory, association.Status);
            Assert.IsNull(association.SelectedRecordId);
            Assert.HasCount(2, association.ConflictingRecordIds);
            Assert.IsTrue(association.ConflictingRecordIds.SequenceEqual(association.ConflictingRecordIds.Order()));
        }

        using var restarted = new SqliteEnvironmentalObservationOutbox();
        var persisted = AssertSingle(await restarted.ReadAssociationsAsync(
            _root!, Guid.Parse("40000000-0000-0000-0000-000000000001"), CancellationToken.None)
            .ConfigureAwait(false));
        Assert.AreEqual(LocalEnvironmentalAssociationStatus.Contradictory, persisted.Status);
        Assert.HasCount(2, persisted.ConflictingRecordIds);
    }

    [TestMethod]
    public async Task OverlappingReadingsFromOneSourceSelectNewestWithoutSelfContradiction()
    {
        using var store = new SqliteEnvironmentalObservationOutbox();
        await store.CommitLocalAsync(_root!, Fact(
            Guid.Parse("41000000-0000-0000-0000-000000000001"),
            EnvironmentalObservationKind.CloudCover,
            0.1,
            null,
            uncertainty: 0.01,
            sourceId: "cloud-a"), CancellationToken.None).ConfigureAwait(false);
        var newest = await store.CommitLocalAsync(_root!, Fact(
            Guid.Parse("41000000-0000-0000-0000-000000000002"),
            EnvironmentalObservationKind.CloudCover,
            0.9,
            null,
            uncertainty: 0.01,
            sourceId: "cloud-a",
            observedAtUtc: Epoch.AddSeconds(1)), CancellationToken.None).ConfigureAwait(false);

        var association = AssertSingle(await CreateService(store).AssociateAsync(
            Guid.Parse("42000000-0000-0000-0000-000000000001"),
            3,
            Epoch.AddSeconds(1),
            Epoch.AddSeconds(2),
            "rig-1",
            [EnvironmentalObservationKind.CloudCover],
            CancellationToken.None).ConfigureAwait(false));

        Assert.AreEqual(LocalEnvironmentalAssociationStatus.Fresh, association.Status);
        Assert.AreEqual(newest.Record.RecordId, association.SelectedRecordId);
        Assert.IsEmpty(association.ConflictingRecordIds);
    }

    [TestMethod]
    public async Task CameraTemperatureAssociationCannotCrossRigScope()
    {
        using var store = new SqliteEnvironmentalObservationOutbox();
        await store.CommitLocalAsync(_root!, Fact(
            Guid.Parse("50000000-0000-0000-0000-000000000001"),
            EnvironmentalObservationKind.CameraSensorTemperature,
            -5,
            null,
            rigId: "rig-1",
            schemaVersion: EnvironmentalObservationSchemaVersions.V2), CancellationToken.None).ConfigureAwait(false);
        var rigTwo = await store.CommitLocalAsync(_root!, Fact(
            Guid.Parse("50000000-0000-0000-0000-000000000002"),
            EnvironmentalObservationKind.CameraSensorTemperature,
            -10,
            null,
            rigId: "rig-2",
            schemaVersion: EnvironmentalObservationSchemaVersions.V2), CancellationToken.None).ConfigureAwait(false);

        var association = AssertSingle(await CreateService(store).AssociateAsync(
            Guid.Parse("60000000-0000-0000-0000-000000000001"),
            3,
            Epoch.AddSeconds(1),
            Epoch.AddSeconds(2),
            "rig-2",
            [EnvironmentalObservationKind.CameraSensorTemperature],
            CancellationToken.None).ConfigureAwait(false));

        Assert.AreEqual(LocalEnvironmentalAssociationStatus.Fresh, association.Status);
        Assert.AreEqual(rigTwo.Record.RecordId, association.SelectedRecordId);
        Assert.AreEqual("rig-2", association.RigId);
    }

    [TestMethod]
    public async Task SubMillisecondExposureNormalizesToDurableMillisecondInterval()
    {
        var captureId = Guid.Parse("61000000-0000-0000-0000-000000000001");
        using (var store = new SqliteEnvironmentalObservationOutbox())
        {
            await store.CommitLocalAsync(_root!, Fact(
                Guid.Parse("61000000-0000-0000-0000-000000000002"),
                EnvironmentalObservationKind.AirTemperature,
                12,
                null), CancellationToken.None).ConfigureAwait(false);
            var association = AssertSingle(await CreateService(store).AssociateAsync(
                captureId,
                4,
                Epoch.AddTicks(1),
                Epoch.AddTicks(2),
                "rig-1",
                [EnvironmentalObservationKind.AirTemperature],
                CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(Epoch, association.ExposureFromUtc);
            Assert.AreEqual(Epoch.AddMilliseconds(1), association.ExposureThroughUtc);
        }

        using var restarted = new SqliteEnvironmentalObservationOutbox();
        var replay = AssertSingle(await CreateService(restarted).AssociateAsync(
            captureId,
            4,
            Epoch.AddTicks(1),
            Epoch.AddTicks(2),
            "rig-1",
            [EnvironmentalObservationKind.AirTemperature],
            CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(Epoch, replay.ExposureFromUtc);
        Assert.AreEqual(Epoch.AddMilliseconds(1), replay.ExposureThroughUtc);
    }

    [TestMethod]
    public async Task RetentionPreservesEveryContradictoryAssociationReference()
    {
        using (var store = new SqliteEnvironmentalObservationOutbox(new FixedTimeProvider(Epoch)))
        {
            await store.CommitLocalAsync(_root!, Fact(
                Guid.Parse("70000000-0000-0000-0000-000000000001"),
                EnvironmentalObservationKind.CloudCover,
                0.1,
                null,
                uncertainty: 0.01,
                sourceId: "cloud-a"), CancellationToken.None).ConfigureAwait(false);
            await store.CommitLocalAsync(_root!, Fact(
                Guid.Parse("70000000-0000-0000-0000-000000000002"),
                EnvironmentalObservationKind.CloudCover,
                0.9,
                null,
                uncertainty: 0.01,
                sourceId: "cloud-b"), CancellationToken.None).ConfigureAwait(false);
            await store.CommitLocalAsync(_root!, Fact(
                Guid.Parse("70000000-0000-0000-0000-000000000003"),
                EnvironmentalObservationKind.RelativeHumidity,
                45,
                null), CancellationToken.None).ConfigureAwait(false);
        }
        using (var store = new SqliteEnvironmentalObservationOutbox(new FixedTimeProvider(Epoch.AddDays(20))))
        {
            var service = new EnvironmentalAssociationService(
                store,
                store,
                Options.Create(new CameraAgentHostOptions { RawIngressRoot = _root! }),
                new FixedTimeProvider(Epoch.AddDays(20)));
            _ = await service.AssociateAsync(
                Guid.Parse("80000000-0000-0000-0000-000000000001"),
                4,
                Epoch.AddSeconds(1),
                Epoch.AddSeconds(2),
                "rig-1",
                [EnvironmentalObservationKind.CloudCover],
                CancellationToken.None).ConfigureAwait(false);
        }

        var rawJournalDirectory = Path.Combine(_root!, "journal");
        Directory.CreateDirectory(rawJournalDirectory);
        var rawJournalPath = Path.Combine(rawJournalDirectory, "raw-ingress.db");
        using (var rawJournal = new SqliteConnection($"Data Source={rawJournalPath}"))
        {
            await rawJournal.OpenAsync().ConfigureAwait(false);
            using var command = rawJournal.CreateCommand();
            command.CommandText = """
                CREATE TABLE raw_captures(capture_id TEXT NOT NULL PRIMARY KEY);
                INSERT INTO raw_captures(capture_id) VALUES('80000000000000000000000000000001');
                """;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        using var restarted = new SqliteEnvironmentalObservationOutbox(new FixedTimeProvider(Epoch.AddDays(40)));
        var retained = await restarted.RetainLocalAsync(
            _root!, Epoch.AddDays(9), 100, CancellationToken.None).ConfigureAwait(false);
        var snapshot = await restarted.GetLocalSnapshotAsync(_root!, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(1, retained.RemovedCount);
        Assert.AreEqual(2, snapshot.StoredCount);
        Assert.HasCount(2, AssertSingle(await restarted.ReadAssociationsAsync(
            _root!, Guid.Parse("80000000-0000-0000-0000-000000000001"), CancellationToken.None)
            .ConfigureAwait(false)).ConflictingRecordIds);

        using (var rawJournal = new SqliteConnection($"Data Source={rawJournalPath}"))
        {
            await rawJournal.OpenAsync().ConfigureAwait(false);
            using var command = rawJournal.CreateCommand();
            command.CommandText = "DELETE FROM raw_captures;";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        var released = await restarted.RetainLocalAsync(
            _root!, Epoch.AddDays(30), 100, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, released.RemovedCount);
        Assert.IsEmpty(await restarted.ReadAssociationsAsync(
            _root!, Guid.Parse("80000000-0000-0000-0000-000000000001"), CancellationToken.None)
            .ConfigureAwait(false));
    }

    private EnvironmentalAssociationService CreateService(SqliteEnvironmentalObservationOutbox store)
        => new(store, store, Options.Create(new CameraAgentHostOptions { RawIngressRoot = _root! }), new FixedTimeProvider(Epoch));

    private EnvironmentalAssociationService CreateService(
        SqliteEnvironmentalObservationOutbox store,
        IReadOnlyList<EnvironmentalObservationKind> configuredKinds)
        => new(store, store, Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = _root!,
            EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions
            {
                Enabled = true,
                Sources = configuredKinds.Select((kind, index) => new EnvironmentalSourceConfiguration
                {
                    Id = $"source-{index}",
                    Type = "Test",
                    Kind = kind,
                    Triggers = [EnvironmentalAcquisitionTrigger.Periodic]
                }).ToArray()
            }
        }), new FixedTimeProvider(Epoch));

    private static LocalEnvironmentalCaptureAssociation AssertSingle(
        IReadOnlyList<LocalEnvironmentalCaptureAssociation> associations)
    {
        Assert.HasCount(1, associations);
        return associations[0];
    }

    private static EnvironmentalObservationFactV1 Fact(
        Guid observationId,
        EnvironmentalObservationKind kind,
        double? numeric,
        bool? boolean,
        DateTimeOffset? staleAfter = null,
        double? uncertainty = null,
        string sourceId = "weather",
        string? rigId = null,
        string schemaVersion = EnvironmentalObservationSchemaVersions.V1,
        DateTimeOffset? observedAtUtc = null)
    {
        using var document = JsonDocument.Parse("{}");
        var parameters = document.RootElement.Clone();
        return new EnvironmentalObservationFactV1(
            schemaVersion,
            observationId,
            new EnvironmentalObservationSource(
                "test-provider",
                sourceId,
                "1.0.0",
                EnvironmentalObservationSourceKind.Measured,
                new EnvironmentalObservationProvenance(
                    new ProcessingAlgorithmIdentity("normalizer", "1.0.0"),
                    parameters,
                    CaptureContractJson.ComputeCanonicalJsonSha256(parameters))),
            observedAtUtc ?? Epoch,
            null,
            null,
            Epoch,
            Epoch.AddMinutes(2),
            staleAfter ?? Epoch.AddMinutes(1),
            new EnvironmentalObservationValue(
                kind,
                Unit(kind),
                numeric,
                boolean,
                EnvironmentalObservationQuality.Good,
                uncertainty),
            [],
            rigId);
    }

    private static EnvironmentalObservationUnit Unit(EnvironmentalObservationKind kind)
        => kind switch
        {
            EnvironmentalObservationKind.AirTemperature or EnvironmentalObservationKind.CameraSensorTemperature =>
                EnvironmentalObservationUnit.DegreesCelsius,
            EnvironmentalObservationKind.RelativeHumidity => EnvironmentalObservationUnit.Percent,
            EnvironmentalObservationKind.AtmosphericPressure => EnvironmentalObservationUnit.Pascals,
            EnvironmentalObservationKind.CloudCover => EnvironmentalObservationUnit.Fraction,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
