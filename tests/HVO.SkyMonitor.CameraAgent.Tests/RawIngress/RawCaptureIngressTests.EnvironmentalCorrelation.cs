using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

public sealed partial class RawCaptureIngressTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task CaptureAcquisitionCorrelationSurvivesRawPublicationAndRestart(bool correlated, bool unavailable)
    {
        var root = CreateRoot();
        try
        {
            var id = correlated ? Guid.NewGuid() : (Guid?)null;
            var receipt = await PersistEnvironmentalCaptureAsync(root, id, Timestamp(1), unavailable: unavailable).ConfigureAwait(false);
            var serialized = CaptureContractJson.Serialize(receipt.Manifest);
            var parsed = CaptureContractJson.ParseManifest(serialized);
            Assert.IsTrue(parsed.IsValid);
            Assert.AreEqual(id, parsed.Document!.Manifest!.Descriptor.CycleEvidence!.AcquisitionAttemptId);
            Assert.AreEqual(unavailable, parsed.Document.Manifest.Descriptor.CycleEvidence.EnvironmentalUnavailable);
            Assert.AreEqual(unavailable, System.Text.Encoding.UTF8.GetString(serialized).Contains("environmentalUnavailable", StringComparison.Ordinal));
            var invalid = receipt.Manifest.Descriptor with
            { CycleEvidence = receipt.Manifest.Descriptor.CycleEvidence! with { AcquisitionAttemptId = Guid.Empty } };
            Assert.IsFalse(invalid.Validate().IsValid);
            if (!correlated) Assert.IsFalse(System.Text.Encoding.UTF8.GetString(serialized).Contains("acquisitionAttemptId", StringComparison.Ordinal));
            using var restarted = CreateIngress(root, new RawIngressState(TimeProvider.System));
            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var replay = await new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1)
                .ReadCommittedCaptureStateAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(replay.Exists);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow((int)RawIngressFaultPoint.BeforeJournalTransactionCommit, 0)]
    [DataRow((int)RawIngressFaultPoint.AfterJournalCommit, 1)]
    public async Task EnvironmentalUnavailableMarkerCommitsAtomicallyAndSurvivesAdmissionRecovery(int fault, int expectedRows)
    {
        var root = CreateRoot();
        try
        {
            await Assert.ThrowsExactlyAsync<InjectedRawIngressFaultException>(async () =>
                await PersistEnvironmentalCaptureAsync(root, Guid.NewGuid(), Timestamp(1), unavailable: true,
                    faultInjector: new OneShotFaultInjector((RawIngressFaultPoint)fault)).ConfigureAwait(false)).ConfigureAwait(false);
            using (var connection = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                Assert.AreEqual((long)expectedRows, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
                if (expectedRows == 1) await AssertRawMarkerAsync(connection).ConfigureAwait(false);
            }
            using var restarted = CreateIngress(root, new RawIngressState(TimeProvider.System));
            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using var recovered = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarLongAsync(recovered, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
            await AssertRawMarkerAsync(recovered).ConfigureAwait(false);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task AssertRawMarkerAsync(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT manifest_json FROM raw_captures;";
        var parsed = CaptureContractJson.ParseManifest((byte[])(await command.ExecuteScalarAsync().ConfigureAwait(false))!);
        Assert.IsTrue(parsed.IsValid);
        Assert.IsTrue(parsed.Document!.Manifest!.Descriptor.CycleEvidence!.EnvironmentalUnavailable);
    }

    [TestMethod]
    public async Task EnvironmentalFenceReaderBoundaryUpgradesSchema15WithoutRewritingCapturedEvidence()
    {
        var root = CreateRoot();
        try
        {
            var receipt = await PersistEnvironmentalCaptureAsync(root, Guid.NewGuid(), Timestamp(1)).ConfigureAwait(false);
            var before = CaptureContractJson.Serialize(receipt.Manifest);
            using (var connection = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                using var version = connection.CreateCommand();
                version.CommandText = "PRAGMA user_version = 15;";
                await version.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            using var restarted = CreateIngress(root, new RawIngressState(TimeProvider.System));
            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using var recovered = await OpenJournalAsync(root).ConfigureAwait(false);
            Assert.AreEqual(16L, await ScalarLongAsync(recovered, "PRAGMA user_version;").ConfigureAwait(false));
            using var manifest = recovered.CreateCommand();
            manifest.CommandText = "SELECT manifest_json FROM raw_captures;";
            CollectionAssert.AreEqual(before, (byte[])(await manifest.ExecuteScalarAsync().ConfigureAwait(false))!);
        }
        finally { Directory.Delete(root, true); }
    }

    internal static CameraModuleConfig EnvironmentalConfiguration() => CreateConfiguration();

    internal static async Task<RawCaptureReceipt> PersistEnvironmentalCaptureAsync(string root, Guid? acquisitionId, DateTimeOffset start,
        EnvironmentalAcquisitionOptions? acquisition = null, bool unavailable = false, IRawIngressFaultInjector? faultInjector = null)
    {
        var config = CreateConfiguration();
        var source = CreateEnrichedSubmission(start, [1, 2, 3, 4]);
        var exposure = TimeSpan.FromSeconds(20);
        var end = start.Add(exposure);
        var frame = source.Result.Frame! with { TimestampUtc = end, Metadata = source.Result.Frame!.Metadata with { Exposure = exposure } };
        var setpoint = source.Result.NextSetpoint with { Exposure = exposure };
        var submission = source with
        {
            Request = source.Request with { RequestedSetpoint = setpoint },
            Result = source.Result with { Frame = frame, NextSetpoint = setpoint, AcquisitionTiming = new(start, end, end) },
            CycleEvidence = source.CycleEvidence! with
            {
                AcquisitionAttemptId = acquisitionId,
                EnvironmentalUnavailable = unavailable,
                Decision = new(end, end, exposure, 1, exposure, 1, CaptureControlDecisionReason.Disabled),
                IngressHandoffStartedUtc = end
            }
        };
        var clock = new FixedTimeProvider(end.AddSeconds(1));
        var state = new RawIngressState(clock);
        using var ingress = acquisition is null ? CreateIngress(root, state, faultInjector, timeProvider: clock)
            : new RawCaptureIngress(Options.Create(new CameraAgentHostOptions
            { RawIngressRoot = root, RawIngressReserveBytes = 0, EnvironmentalAcquisition = acquisition }),
                new FixedCapacityProvider(long.MaxValue), state, clock, new RawIngressTelemetry(state),
                NullLogger<RawCaptureIngress>.Instance, faultInjector ?? new NullRawIngressFaultInjector());
        return (await ingress.AcceptAsync(config, submission, CancellationToken.None).ConfigureAwait(false))!;
    }
}
