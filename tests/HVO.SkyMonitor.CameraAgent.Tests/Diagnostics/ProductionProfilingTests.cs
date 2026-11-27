using System.Diagnostics;
using System.Diagnostics.Metrics;
using HVO.SkyMonitor.CameraAgent.Common.Diagnostics;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Configuration;
using Microsoft.Data.Sqlite;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace HVO.SkyMonitor.CameraAgent.Tests.Diagnostics;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Diagnostics")]
[DoNotParallelize]
public sealed class ProductionProfilingTests
{
    [TestMethod]
    public void AcceptsThreeModesAndRefusesInvalidValuesWithoutEchoingThem()
    {
        Assert.AreEqual(ProductionProfilingMode.Baseline, ProductionProfiling.ReadMode(null));
        Assert.AreEqual(ProductionProfilingMode.Baseline, ProductionProfiling.ReadMode("Baseline"));
        Assert.AreEqual(ProductionProfilingMode.Enabled, ProductionProfiling.ReadMode(" enabled "));
        Assert.AreEqual(ProductionProfilingMode.Disabled, ProductionProfiling.ReadMode("Disabled"));
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => ProductionProfiling.ReadMode("invalid-value"));
        Assert.IsFalse(exception.Message.Contains("invalid-value", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TraceSamplerHonorsLocalParentButRemoteParentCannotForceSampling()
    {
        var sampler = ProductionProfiling.CreateSampler();
        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        Assert.AreEqual(SamplingDecision.RecordAndSample, Decide(new ActivityContext(traceId, spanId, ActivityTraceFlags.Recorded)));
        Assert.AreEqual(SamplingDecision.Drop, Decide(new ActivityContext(traceId, spanId, ActivityTraceFlags.None)));
        Assert.AreEqual(SamplingDecision.Drop, Decide(new ActivityContext(traceId, spanId, ActivityTraceFlags.Recorded, isRemote: true)));
        Assert.AreEqual(SamplingDecision.Drop, Decide(new ActivityContext(traceId, spanId, ActivityTraceFlags.None, isRemote: true)));

        SamplingDecision Decide(ActivityContext parent) =>
            sampler.ShouldSample(new SamplingParameters(parent, traceId, "test", ActivityKind.Internal, null, null)).Decision;
    }

    [TestMethod]
    public void SqliteInstrumentationDistinguishesContentionFromCancellationAndOtherErrors()
    {
        Assert.AreEqual(CameraAgentWorkloadTelemetry.Outcome.Busy, SqliteRawCaptureJournal.ClassifySqliteFailure(new SqliteException("synthetic", 5)));
        Assert.AreEqual(CameraAgentWorkloadTelemetry.Outcome.Locked, SqliteRawCaptureJournal.ClassifySqliteFailure(new SqliteException("synthetic", 6)));
        Assert.AreEqual(CameraAgentWorkloadTelemetry.Outcome.Failure, SqliteRawCaptureJournal.ClassifySqliteFailure(new SqliteException("synthetic", 10)));
        Assert.AreEqual(CameraAgentWorkloadTelemetry.Outcome.Cancelled, SqliteRawCaptureJournal.ClassifySqliteFailure(new OperationCanceledException()));
    }

    [TestMethod]
    public async Task PassiveCheckpointReportsPinnedFramesAndCompletesAfterReaderCloses()
    {
        var root = Directory.CreateTempSubdirectory("hvo-checkpoint-");
        try
        {
            using var listener = new MeterListener
            {
                InstrumentPublished = static (instrument, receiver) =>
                {
                    if (instrument.Meter.Name == CameraAgentWorkloadTelemetry.MeterName &&
                        instrument.Name == "camera_agent.workload.duration")
                    {
                        receiver.EnableMeasurementEvents(instrument);
                    }
                }
            };
            var outcomes = new List<string>();
            listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
            {
                if (tags[0].Value is "sqlite_checkpoint" && tags[1].Value is string outcome)
                    outcomes.Add(outcome);
            });
            listener.Start();

            var path = Path.Combine(root.FullName, "journal.db");
            var journal = new SqliteRawCaptureJournal(path, 1);
            await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
            using var writer = new SqliteConnection(connectionString);
            await writer.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            using var write = writer.CreateCommand();
            write.CommandText = "CREATE TABLE test_checkpoint (value INTEGER); INSERT INTO test_checkpoint VALUES (1);";
            await write.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);

            using (var readerConnection = new SqliteConnection(connectionString))
            {
                await readerConnection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
#pragma warning disable CA1849 // The asynchronous API has no deferred overload.
                using var transaction = readerConnection.BeginTransaction(deferred: true);
#pragma warning restore CA1849
                using var read = readerConnection.CreateCommand();
                read.Transaction = transaction;
                read.CommandText = "SELECT value FROM test_checkpoint";
                Assert.AreEqual(1L, await read.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false));
                write.CommandText = "INSERT INTO test_checkpoint VALUES (2)";
                await write.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                await journal.CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await journal.CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(2, outcomes);
            Assert.AreEqual("incomplete", outcomes[0]);
            Assert.AreEqual("success", outcomes[1]);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task LocalFilePublicationContinuesWhileMetricExportIsBlocked()
    {
        using var exportBlocked = new ManualResetEventSlim();
        using var releaseExport = new ManualResetEventSlim();
        using var exporter = new BlockFirstMetricExport(exportBlocked, releaseExport);
        var builder = Sdk.CreateMeterProviderBuilder();
        ProductionProfiling.ConfigureMetrics(builder);
        using var provider = builder.AddReader(new PeriodicExportingMetricReader(exporter, exportIntervalMilliseconds: 60_000)).Build();
        CameraAgentWorkloadTelemetry.RecordSceneBytes(1024);
        var blockedFlush = Task.Run(() => provider.ForceFlush(15_000));
        var directory = Directory.CreateTempSubdirectory("hvo-profiling-");
        try
        {
            Assert.IsTrue(exportBlocked.Wait(TimeSpan.FromSeconds(5)));
            var files = new RawIngressFileStore(directory.FullName, new NullRawIngressFaultInjector());
            var paths = files.GetPaths(DateTimeOffset.UnixEpoch, Guid.NewGuid());
            byte[] payload = [1, 2, 3];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await files.PublishPayloadAsync(paths, payload, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(paths.PayloadAbsolutePath, timeout.Token).ConfigureAwait(false));
            Assert.IsFalse(blockedFlush.IsCompleted);
        }
        finally
        {
            releaseExport.Set();
            await blockedFlush.ConfigureAwait(false);
            directory.Delete(recursive: true);
        }
    }

    private sealed class BlockFirstMetricExport(ManualResetEventSlim entered, ManualResetEventSlim release) : BaseExporter<Metric>
    {
        public override ExportResult Export(in Batch<Metric> batch)
        {
            entered.Set();
            return release.Wait(TimeSpan.FromSeconds(15)) ? ExportResult.Success : ExportResult.Failure;
        }
    }
}
