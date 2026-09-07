using System.Diagnostics;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

/// <summary>
/// Issue #698: the processing store must not share a SQLite page cache with its own availability reader.
/// With <c>Cache=Shared</c> a live <c>processing_outputs</c> aggregate reader holds a shared-cache table lock,
/// and the node writer fails immediately with SQLite error 6 ("database table is locked") instead of the
/// WAL reader/writer coexistence that a private cache provides.
/// </summary>
[TestClass]
public sealed class SqliteCaptureProcessingStoreConcurrencyTests
{
    private const int BusyTimeoutSeconds = 5;

    [TestMethod]
    [TestCategory("Unit")]
    public async Task WriteNode_WhileAvailabilityAggregateReaderIsAlive_CommitsWithoutTableLock()
    {
        var root = CreateRoot("hvo-processing-store-lock");
        try
        {
            var options = CreateOptions(root);
            await InitializeRawIngressAsync(root).ConfigureAwait(false);
            var seeded = CreateOutput(1);
            var distinct = CreateOutput(2);
            var store = new SqliteCaptureProcessingStore(options);
            try
            {
                await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                await WriteCompletedNodeAsync(store, seeded).ConfigureAwait(false);

                using (var reader = await LiveAvailabilityReader.OpenAsync(root, BusyTimeoutSeconds).ConfigureAwait(false))
                {
                    Assert.AreEqual(0, reader.MissingCount);

                    await WriteWhileReaderIsAliveAsync(store, distinct).ConfigureAwait(false);
                    // Repeating the same write before the barrier is released must remain idempotent.
                    await WriteWhileReaderIsAliveAsync(store, distinct).ConfigureAwait(false);
                }
            }
            finally
            {
                store.Dispose();
            }

            using var reopened = new SqliteCaptureProcessingStore(options);
            foreach (var output in new[] { seeded, distinct })
            {
                var node = await reopened.ReadNodeAsync(
                    output.Capture.CaptureId, NodeId, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(node, output.OutputIdentitySha256);
                Assert.AreEqual(DurableProcessingNodeStatus.Completed, node.Status, output.OutputIdentitySha256);
                Assert.HasCount(1, node.Outputs, output.OutputIdentitySha256);
                Assert.AreEqual(output.OutputIdentitySha256, node.Outputs[0].OutputIdentitySha256);
                Assert.AreEqual(output.ArtifactId, node.Outputs[0].ArtifactId);
                Assert.AreEqual(output.PayloadRelativePath, node.Outputs[0].PayloadRelativePath);
                Assert.AreEqual(1, await CountOutputRowsAsync(root, output.OutputIdentitySha256).ConfigureAwait(false));
            }
            Assert.AreEqual(2, await CountOutputRowsAsync(root, null).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task ConfirmGrant_WithProcessingWriteUnderLiveAvailabilityReader_AdmitsOnceWithoutTableLock()
    {
        var root = CreateRoot("hvo-processing-schedule-lock");
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2025, 1, 13, 1, 0, 0, TimeSpan.Zero));
        var options = CreateOptions(root);
        var rawIngress = new JournalInitializer(root);
        await rawIngress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        using var telemetry = new CaptureControlTelemetry();
        using var scheduleStore = new SqliteCaptureScheduleStore(rawIngress, options, timeProvider);
        using var admission = new CaptureAdmissionCoordinator(rawIngress, options, timeProvider, telemetry);
        var rawState = new RawIngressState(timeProvider);
        rawState.Set(RawIngressAvailability.Accepting, "accepting");
        var laneState = new CaptureLaneState(timeProvider, options);
        laneState.Update([]);
        using var runtime = new CaptureScheduleRuntimeCoordinator(
            scheduleStore, admission, rawState, laneState, new EmptyPipelineFactory(), timeProvider);
        using var processingStore = new SqliteCaptureProcessingStore(options);
        try
        {
            await admission.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.InitializeAsync(CreateConfiguration(), CancellationToken.None).ConfigureAwait(false);
            await processingStore.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await WriteCompletedNodeAsync(processingStore, CreateOutput(1)).ConfigureAwait(false);

            var grant = await runtime.WaitForGrantAsync(CancellationToken.None).ConfigureAwait(false);
            using var reader = await LiveAvailabilityReader.OpenAsync(root, BusyTimeoutSeconds).ConfigureAwait(false);
            using var lease = await admission.EnterAsync(CancellationToken.None).ConfigureAwait(false);
            var stopwatch = Stopwatch.StartNew();
            await WriteWhileReaderIsAliveAsync(processingStore, CreateOutput(2)).ConfigureAwait(false);
            var confirmed = await runtime.ConfirmGrantAsync(
                grant, "admission-698", CancellationToken.None).ConfigureAwait(false);
            stopwatch.Stop();
            lease.MarkNoPublicationRequired();

            Assert.IsNotNull(confirmed, "the schedule grant was not confirmed while the availability reader was alive");
            Assert.AreEqual(runtime.Snapshot!.Revision.RevisionId, confirmed.Revision.RevisionId);
            Assert.IsTrue(
                stopwatch.Elapsed < TimeSpan.FromSeconds(BusyTimeoutSeconds),
                $"processing write plus ConfirmGrantAsync took {stopwatch.Elapsed.TotalMilliseconds:F1} ms, which reaches the SQLite busy timeout");
            Assert.AreEqual(2, await CountOutputRowsAsync(root, null).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private const string NodeId = "issue-698";

    private static string CreateRoot(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static IOptions<CameraAgentHostOptions> CreateOptions(string root)
        => Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = root,
            RawIngressReserveBytes = 0,
            RawIngressSqliteBusyTimeoutSeconds = BusyTimeoutSeconds
        });

    private static string DatabasePath(string root) => Path.Combine(root, "journal", "raw-ingress.db");

    private static async Task InitializeRawIngressAsync(string root)
    {
        var journal = new SqliteRawCaptureJournal(DatabasePath(root), BusyTimeoutSeconds);
        await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static DurableProcessingOutput CreateOutput(long sequence)
    {
        var manifest = ArtifactManifestFixture.CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, new byte[8]);
        var descriptor = manifest.Descriptor with
        {
            Capture = manifest.Descriptor.Capture with
            {
                CaptureSequence = sequence,
                CaptureId = Guid.Parse($"69800000-0000-0000-0000-{sequence:D12}")
            },
            Artifact = manifest.Descriptor.Artifact with
            {
                ArtifactId = Guid.Parse($"69810000-0000-0000-0000-{sequence:D12}")
            }
        };
        return new DurableProcessingOutput(
            new string((char)('a' + (sequence % 6)), 64),
            descriptor.Artifact.ArtifactId,
            $"frames/issue-698-{sequence}.bin",
            $"frames/issue-698-{sequence}.json",
            CaptureContractJson.Serialize(manifest with { Descriptor = descriptor }),
            descriptor,
            null,
            ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256,
            [new ProcessingAlgorithmIdentity("issue-698", "1")],
            new ProcessingCompatibilityIdentity("rig", "orientation", "calibration", "mask", "sensor", "regime", "profile"),
            TimeSpan.FromSeconds(1),
            sequence);
    }

    private static ValueTask WriteCompletedNodeAsync(SqliteCaptureProcessingStore store, DurableProcessingOutput output)
        => store.WriteNodeAsync(
            output.Capture.CaptureId,
            new CaptureProcessingGraphNode(NodeId, new NoOpStep(), [], true, null, null, null, new string('c', 64)),
            DurableProcessingNodeStatus.Completed,
            null,
            1,
            new string('d', 64),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            TimeSpan.Zero,
            ProcessingOutcomeStatus.Produced,
            [],
            0,
            null,
            [output],
            CancellationToken.None);

    private static async Task WriteWhileReaderIsAliveAsync(SqliteCaptureProcessingStore store, DurableProcessingOutput output)
    {
        try
        {
            await WriteCompletedNodeAsync(store, output)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(BusyTimeoutSeconds * 2))
                .ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            Assert.Fail(
                $"processing node write failed under a live availability reader with SQLite error {exception.SqliteErrorCode}: {exception.Message}");
        }
    }

    private static async Task<long> CountOutputRowsAsync(string root, string? outputIdentity)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath(root),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        if (outputIdentity is null)
        {
            command.CommandText = "SELECT COUNT(*) FROM processing_outputs;";
        }
        else
        {
            command.CommandText = "SELECT COUNT(*) FROM processing_outputs WHERE output_identity_sha256 = $identity;";
            command.Parameters.AddWithValue("$identity", outputIdentity);
        }
        return Convert.ToInt64(
            await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static CameraModuleConfig CreateConfiguration()
    {
        var profileId = "always-open";
        var schedule = new CaptureScheduleDefinition(
            "capture-schedule-v1",
            [new CaptureScheduleSetpointProfile(profileId, TimeSpan.FromSeconds(1), 1, TimeSpan.FromSeconds(2))],
            Enum.GetValues<DayOfWeek>()
                .Select(day => new CaptureWeeklyScheduleWindow(
                    $"always-open-{day.ToString().ToUpperInvariant()}",
                    day,
                    new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, TimeOnly.MinValue),
                    new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, TimeOnly.MinValue, DayOffset: 1),
                    profileId))
                .ToArray());
        return new CameraModuleConfig(
            new ObservatoryLocation(35, -114, 1000, "UTC"),
            new CameraModuleDescriptor("test"),
            new CameraRigConfig(
                new SensorProfile("test", 2, 2, 5, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("Perspective", 50, 10, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1),
                new CameraControlPolicy
                {
                    ExposureControl = AutomaticControlOwnership.Disabled,
                    GainControl = AutomaticControlOwnership.Disabled
                }),
            CapturePipelineConfig.Empty,
            AgentId: "test-agent")
        {
            DeploymentLocation = DeploymentLocationSnapshot.Create(
                "test-location", 2, "test", null, DateTimeOffset.UnixEpoch, null, 35, -114, 1000, "UTC"),
            Schedule = schedule
        };
    }

    /// <summary>
    /// Holds the exact availability aggregate of <c>ReadAvailabilityInventoryAsync</c> open inside a deferred
    /// transaction on a second connection that uses the same shared-cache connection string the processing
    /// store used before #698. In shared-cache mode this owns the <c>processing_outputs</c> table read lock
    /// for the life of the transaction; in WAL mode with private caches it is an ordinary snapshot reader.
    /// </summary>
    private sealed class LiveAvailabilityReader : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;
        private readonly SqliteCommand _command;
        private readonly SqliteDataReader _reader;

        private LiveAvailabilityReader(
            SqliteConnection connection, SqliteTransaction transaction, SqliteCommand command, SqliteDataReader reader, long missing)
        {
            _connection = connection;
            _transaction = transaction;
            _command = command;
            _reader = reader;
            MissingCount = missing;
        }

        internal long MissingCount { get; }

        internal static async Task<LiveAvailabilityReader> OpenAsync(string root, int busyTimeoutSeconds)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath(root),
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Shared,
                Pooling = false,
                DefaultTimeout = busyTimeoutSeconds
            }.ToString());
            await connection.OpenAsync().ConfigureAwait(false);
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes deferred transactions only through the synchronous overload.
            var transaction = connection.BeginTransaction(deferred: true);
#pragma warning restore CA1849
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT SUM(CASE WHEN availability_state = 'Missing' THEN 1 ELSE 0 END),
                       SUM(CASE WHEN availability_state = 'Quarantined' THEN 1 ELSE 0 END)
                FROM processing_outputs;
                """;
            var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
            var missing = await reader.IsDBNullAsync(0).ConfigureAwait(false) ? 0 : reader.GetInt64(0);
            return new LiveAvailabilityReader(connection, transaction, command, reader, missing);
        }

        public void Dispose()
        {
            _reader.Dispose();
            _command.Dispose();
            _transaction.Dispose();
            _connection.Dispose();
        }
    }

    private sealed class NoOpStep : ICaptureProcessingStep
    {
        public string Name => NodeId;

        public int Order => 0;

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class EmptyPipelineFactory : ICaptureProcessingPipelineFactory
    {
        public CaptureProcessingGraph CreateGraph(CameraModuleConfig config) => new([]);

        public CaptureProcessingPlanPreview PreviewPlan(CameraModuleConfig config) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    private sealed class JournalInitializer(string root) : IRawCaptureIngress
    {
        public async ValueTask InitializeAsync(CancellationToken cancellationToken)
        {
            var journal = new SqliteRawCaptureJournal(DatabasePath(root), BusyTimeoutSeconds);
            await journal.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration, CaptureLoopSubmission submission, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
