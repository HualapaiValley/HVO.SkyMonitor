using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

/// <summary>Small synthetic snapshots built only through the published orbital contract API.</summary>
internal static class OrbitalElementsStoreTestData
{
    internal static readonly DateTimeOffset Start = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(2);

    internal static string Sha(char value) => new(value, 64);

    internal static OrbitalSoftwareIdentity Parser => new("synthetic-gp-parser", "1", Sha('A'));

    internal static OrbitalSoftwareIdentity Normalizer => new("synthetic-gp-normalizer", "1", Sha('B'));

    internal static OrbitalElementsStoreSettings Settings(
        string root,
        int maximumRetained = 8,
        TimeSpan? minimumRetention = null,
        int maximumQuarantined = 4)
        => new(root, Interval, maximumRetained, minimumRetention ?? TimeSpan.Zero, maximumQuarantined);

    internal static SqliteOrbitalElementsStore Open(
        string root,
        TimeProvider clock,
        OrbitalElementsStoreSettings? settings = null,
        IOrbitalStorageFaultInjector? faults = null)
        => new(settings ?? Settings(root), clock, NullLogger<SqliteOrbitalElementsStore>.Instance,
            faults ?? NullOrbitalStorageFaultInjector.Instance);

    internal static OrbitalRecordV1 Record(string catalogNumber, double epochHours = 0)
        => OrbitalContract.CreateRecord(new("", new("norad", catalogNumber), "celestrak:gp:active", null,
            "Synthetic store fixture " + catalogNumber, null, null, null, Start.AddHours(epochHours), null, null,
            new("EARTH", "TEME", "UTC", "SGP4", OrbitalContract.GpDerivativeConvention, OrbitalContract.CelesTrakGpProfile,
                Parser, Normalizer),
            new(15.5, .0002, 51.6, 125, 45, 270, .0001, .00002, -.0000003, 0, "U", 999, 50000)));

    internal static byte[] Raw(string marker) => Encoding.UTF8.GetBytes("[{\"SYNTHETIC_GP\":\"" + marker + "\"}]");

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>The raw marker fixes the source (and so the manifest); the catalog numbers fix the payload.</summary>
    internal static Candidate Build(string rawMarker, params string[] catalogNumbers)
        => BuildFromProvider("celestrak", rawMarker, catalogNumbers);

    internal static Candidate BuildFromProvider(string provider, string rawMarker, params string[] catalogNumbers)
    {
        var raw = Raw(rawMarker);
        var source = new OrbitalSnapshotSource("celestrak:gp:active", provider, "USSF public GP",
            new Uri("https://celestrak.org/NORAD/elements/gp.php?GROUP=active&FORMAT=JSON"), Start, null, null, null,
            Hash(raw), raw.Length, Parser, Normalizer, new Uri("https://celestrak.org/NORAD/documentation/gp-data-formats.php"),
            "Synthetic orbital facts authored for store tests; no provider payload is redistributed.");
        var numbers = catalogNumbers.Length == 0 ? ["25544"] : catalogNumbers;
        var snapshot = OrbitalSnapshot.Create(numbers.Select(number => Record(number)), [source],
            new OrbitalPopulationCoverage("active-group", OrbitalPopulationCompleteness.CompleteForRequestedPopulation, []));
        return new Candidate(snapshot, raw);
    }

    internal static async Task<string> AdmitAsync(SqliteOrbitalElementsStore store, string runKey = "orbital-refresh")
    {
        var admission = await store.TryBeginAttemptAsync(runKey, OrbitalAttemptTrigger.Scheduled, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(OrbitalAdmissionStatus.Admitted, admission.Status);
        return admission.AttemptId!;
    }

    /// <summary>Admits an attempt, publishes the candidate, then moves the clock past the floor.</summary>
    internal static async Task<OrbitalPublication> PublishAsync(SqliteOrbitalElementsStore store, Clock clock, Candidate candidate)
    {
        var attemptId = await AdmitAsync(store).ConfigureAwait(false);
        var publication = await store.PublishAndActivateAsync(attemptId, candidate.Snapshot, candidate.Raw, CancellationToken.None)
            .ConfigureAwait(false);
        clock.Advance(Interval);
        return publication;
    }

    internal static SqliteConnection OpenDatabase(string root)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "orbital-elements.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Test callers pass fixed private SQL that never contains external input.")]
    internal static object? Scalar(string root, string sql)
    {
        using var connection = OpenDatabase(root);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Test callers pass fixed private SQL that never contains external input.")]
    internal static void Execute(string root, string sql)
    {
        using var connection = OpenDatabase(root);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Name and STRICT flag of every orbital table.</summary>
    internal static List<(string Name, long Strict)> OrbitalTables(string root)
    {
        using var connection = OpenDatabase(root);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, strict FROM pragma_table_list WHERE schema = 'main' AND name LIKE 'orbital%' ORDER BY name;";
        using var reader = command.ExecuteReader();
        var tables = new List<(string, long)>();
        while (reader.Read())
        {
            tables.Add((reader.GetString(0), reader.GetInt64(1)));
        }
        return tables;
    }

    internal static string[] Files(string root, string directory)
        => Directory.EnumerateFileSystemEntries(Path.Combine(root, directory))
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal)
            .ToArray();

    internal sealed record Candidate(OrbitalSnapshot Snapshot, byte[] Raw)
    {
        public string Manifest => Snapshot.Manifest.ManifestSha256;

        public string Payload => Snapshot.Manifest.NormalizedPayloadSha256;

        public string RawSha256 => Hash(Raw);
    }

    internal sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public void Advance(TimeSpan value) => Now += value;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Throws the configured exception once at the armed point.</summary>
    internal sealed class Faults : IOrbitalStorageFaultInjector
    {
        public OrbitalStorageFaultPoint? ArmedPoint { get; set; }

        public Func<Exception>? Throw { get; set; }

        public Action? OnPoint { get; set; }

        public void Inject(OrbitalStorageFaultPoint point)
        {
            if (ArmedPoint != point)
            {
                return;
            }
            ArmedPoint = null;
            OnPoint?.Invoke();
            if (Throw is not null)
            {
                throw Throw();
            }
        }
    }
}

/// <summary>Creates disposable temporary roots and deletes them after each test; stores are disposed by the tests.</summary>
public abstract class OrbitalStoreTestBase
{
    private readonly List<string> _directories = [];

    protected string CreateRoot()
    {
        var directory = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-orbital-store-tests");
        _directories.Add(directory);
        return Path.Combine(directory, "orbital");
    }

    [TestCleanup]
    public void DeleteRoots()
    {
        foreach (var directory in _directories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: a leaked temporary directory never affects another test.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort, as above.
            }
        }
    }
}

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalElementsStoreTests : OrbitalStoreTestBase
{
    private static readonly string[] Tables =
    [
        "orbital_activation_history", "orbital_attempts", "orbital_commands", "orbital_installation",
        "orbital_provider_state", "orbital_snapshot_references", "orbital_snapshots"
    ];

    [TestMethod]
    public async Task InitializeCreatesTheStrictWalSchemaAndTheContentLayout()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using (var store = OrbitalElementsStoreTestData.Open(root, clock))
        {
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (var directory in new[] { "raw", "payloads", "manifests", "quarantine", "staging" })
            {
                Assert.IsTrue(Directory.Exists(Path.Combine(root, directory)), directory);
            }
            var state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(0, state.Revision);
            Assert.IsNull(state.CurrentManifestSha256);
            Assert.IsNull(state.LastAttemptUtc);
            Assert.IsNull(state.NextPermittedAttemptUtc);
        }

        Assert.AreEqual("wal", OrbitalElementsStoreTestData.Scalar(root, "PRAGMA journal_mode;"));
        Assert.AreEqual(1L, OrbitalElementsStoreTestData.Scalar(root, "PRAGMA user_version;"));
        var tables = OrbitalElementsStoreTestData.OrbitalTables(root);
        CollectionAssert.AreEqual(Tables, tables.Select(table => table.Name).ToArray());
        Assert.IsTrue(tables.All(table => table.Strict == 1), "Every orbital table is STRICT.");
    }

    [TestMethod]
    public async Task PhaseOffsetComesFromThePersistedSeedAndSurvivesReopen()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        TimeSpan first;
        using (var store = OrbitalElementsStoreTestData.Open(root, clock))
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => _ = store.PhaseOffset);
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            first = store.PhaseOffset;
        }
        var seed = (byte[])OrbitalElementsStoreTestData.Scalar(root, "SELECT seed FROM orbital_installation;")!;
        Assert.HasCount(32, seed);
        Assert.AreEqual(SqliteOrbitalElementsStore.ComputePhaseOffset(seed), first);
        Assert.IsTrue(first >= TimeSpan.Zero && first < TimeSpan.FromMinutes(30));
        Assert.AreEqual(0, first.Ticks % TimeSpan.TicksPerSecond);

        using var reopened = OrbitalElementsStoreTestData.Open(root, clock);
        await reopened.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(first, reopened.PhaseOffset);
        CollectionAssert.AreEqual(seed, (byte[])OrbitalElementsStoreTestData.Scalar(root, "SELECT seed FROM orbital_installation;")!);
    }

    [TestMethod]
    public void PhaseOffsetIsADeterministicFunctionOfTheSeed()
    {
        var zero = new byte[32];
        var ones = Enumerable.Repeat((byte)0xFF, 32).ToArray();
        Assert.AreEqual(SqliteOrbitalElementsStore.ComputePhaseOffset(zero), SqliteOrbitalElementsStore.ComputePhaseOffset(zero.ToArray()));
        foreach (var seed in new[] { zero, ones })
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes("hvo-orbital-refresh-phase-v1|" + Convert.ToHexString(seed)));
            var expected = TimeSpan.FromSeconds((long)(System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(digest) % 1800));
            Assert.AreEqual(expected, SqliteOrbitalElementsStore.ComputePhaseOffset(seed));
        }
    }

    [TestMethod]
    [DataRow("unversioned")]
    [DataRow("newer")]
    [DataRow("unknown")]
    [DataRow("extra-index")]
    [DataRow("dropped-trigger")]
    [DataRow("altered-table")]
    public async Task UnversionedNewerUnknownAndDriftedSchemasFailClosed(string shape)
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        if (shape is "unversioned" or "newer" or "unknown")
        {
            Directory.CreateDirectory(root);
        }
        else
        {
            using var store = OrbitalElementsStoreTestData.Open(root, clock);
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        var sql = shape switch
        {
            "unversioned" => "CREATE TABLE foreign_table(value TEXT);",
            "newer" => "PRAGMA user_version = 2;",
            "unknown" => "CREATE TABLE foreign_table(value TEXT); PRAGMA user_version = -1;",
            "extra-index" => "CREATE INDEX orbital_attempts_run_key ON orbital_attempts(run_key);",
            "dropped-trigger" => "DROP TRIGGER orbital_snapshots_no_delete;",
            _ => "ALTER TABLE orbital_snapshot_references ADD COLUMN note TEXT;"
        };
        OrbitalElementsStoreTestData.Execute(root, sql);

        using var reopened = OrbitalElementsStoreTestData.Open(root, clock);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => reopened.InitializeAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
        Assert.IsFalse(reopened.IsInitialized);
    }

    [TestMethod]
    public async Task ASecondOwnerOfTheSameRootIsRefusedUntilTheFirstIsDisposed()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        var first = OrbitalElementsStoreTestData.Open(root, clock);
        await first.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        using var second = OrbitalElementsStoreTestData.Open(root, clock);
        var refused = await Assert.ThrowsExactlyAsync<OrbitalStorageException>(
            () => second.InitializeAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
        Assert.AreEqual("orbital.storage-locked", refused.ReasonCode);

        first.Dispose();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => first.GetStateAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
        clock.Advance(TimeSpan.FromSeconds(1));
        await second.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(second.IsInitialized);
    }

    [TestMethod]
    [DataRow("relative")]
    [DataRow("interval")]
    [DataRow("retained")]
    [DataRow("retention")]
    [DataRow("quarantined")]
    public void SettingsOutsideTheProviderAndStorageBoundsAreRejected(string shape)
    {
        var root = CreateRoot();
        var valid = OrbitalElementsStoreTestData.Settings(root);
        var settings = shape switch
        {
            "relative" => valid with { RootDirectory = "relative/orbital" },
            "interval" => valid with { MinimumAttemptInterval = TimeSpan.FromHours(2) - TimeSpan.FromSeconds(1) },
            "retained" => valid with { MaximumRetainedSnapshots = 0 },
            "retention" => valid with { MinimumRetention = TimeSpan.FromSeconds(-1) },
            _ => valid with { MaximumQuarantinedPayloads = 65 }
        };
        var exception = Assert.Throws<ArgumentException>(() => new SqliteOrbitalElementsStore(
            settings, TimeProvider.System, NullLogger<SqliteOrbitalElementsStore>.Instance));
        Assert.AreEqual("settings", exception.ParamName);
        Assert.AreEqual(TimeSpan.FromHours(2), SqliteOrbitalElementsStore.MinimumProviderAttemptInterval);
    }

    [TestMethod]
    public async Task AdmissionCommitsTheAttemptAndTheFloorBeforeReturning()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start.AddTicks(12345));
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        var now = OrbitalElementsStoreTestData.Start.AddTicks(10000);

        var admitted = await store.TryBeginAttemptAsync("run-1", OrbitalAttemptTrigger.Scheduled, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(OrbitalAdmissionStatus.Admitted, admitted.Status);
        Assert.AreEqual(32, admitted.AttemptId!.Length);
        Assert.AreEqual(now + OrbitalElementsStoreTestData.Interval, admitted.NextPermittedAttemptUtc);
        Assert.IsNull(admitted.Halt);
        Assert.AreEqual("InFlight", OrbitalElementsStoreTestData.Scalar(root,
            $"SELECT outcome FROM orbital_attempts WHERE attempt_id = '{admitted.AttemptId}';"));
        var state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(now, state.LastAttemptUtc);
        Assert.AreEqual(OrbitalAttemptOutcome.InFlight, state.LastAttemptOutcome);
        Assert.AreEqual("run-1", state.LastAttemptRunKey);
        Assert.IsNull(state.LastAttemptReasonCode);
        Assert.AreEqual(now + OrbitalElementsStoreTestData.Interval, state.NextPermittedAttemptUtc);
        Assert.AreEqual(1, state.Revision);

        var manual = await store.TryBeginAttemptAsync("run-2", OrbitalAttemptTrigger.Manual, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(OrbitalAdmissionStatus.FloorNotElapsed, manual.Status);
        Assert.IsNull(manual.AttemptId);
        Assert.AreEqual(now + OrbitalElementsStoreTestData.Interval, manual.NextPermittedAttemptUtc);

        clock.Advance(OrbitalElementsStoreTestData.Interval);
        var overlapping = await store.TryBeginAttemptAsync("run-3", OrbitalAttemptTrigger.Scheduled, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(OrbitalAdmissionStatus.AttemptInFlight, overlapping.Status);
        Assert.IsNull(overlapping.AttemptId);

        await store.RecordTransportFailureAsync(admitted.AttemptId, "orbital.transport-timeout", "timed out",
            CancellationToken.None).ConfigureAwait(false);
        var next = await store.TryBeginAttemptAsync("run-4", OrbitalAttemptTrigger.Manual, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(OrbitalAdmissionStatus.Admitted, next.Status);
        Assert.AreNotEqual(admitted.AttemptId, next.AttemptId);
        Assert.AreEqual(2L, OrbitalElementsStoreTestData.Scalar(root, "SELECT attempt_sequence FROM orbital_provider_state;"));
    }

    [TestMethod]
    public async Task AnInFlightAttemptWithARecordedRetrievalBecomesInterruptedOnReopenAndStillHoldsTheFloor()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using (var store = OrbitalElementsStoreTestData.Open(root, clock))
        {
            var attemptId = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);
            await store.RecordRetrievalAsync(attemptId, clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
        clock.Advance(TimeSpan.FromMinutes(30));

        using var reopened = OrbitalElementsStoreTestData.Open(root, clock);
        var state = await reopened.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalAttemptOutcome.Interrupted, state.LastAttemptOutcome);
        Assert.AreEqual("orbital.attempt-interrupted", state.LastAttemptReasonCode);
        Assert.IsNull(state.Halt, "A recorded 200 is a known provider outcome; the interruption does not halt.");
        Assert.AreEqual(OrbitalElementsStoreTestData.Start, state.LastAttemptUtc);
        Assert.AreEqual(OrbitalElementsStoreTestData.Start, state.LastRetrievalSuccessUtc);
        var blocked = await reopened.TryBeginAttemptAsync("run", OrbitalAttemptTrigger.Manual, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(OrbitalAdmissionStatus.FloorNotElapsed, blocked.Status);
        Assert.AreEqual(OrbitalElementsStoreTestData.Start + OrbitalElementsStoreTestData.Interval, blocked.NextPermittedAttemptUtc);

        clock.Advance(TimeSpan.FromMinutes(90));
        Assert.AreEqual(OrbitalAdmissionStatus.Admitted, (await reopened.TryBeginAttemptAsync(
            "run", OrbitalAttemptTrigger.Manual, CancellationToken.None).ConfigureAwait(false)).Status);
    }

    [TestMethod]
    public async Task AnInFlightAttemptWithNoRecordedOutcomeHaltsOnReopenUntilResumedAndStillHoldsTheFloor()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using (var store = OrbitalElementsStoreTestData.Open(root, clock))
        {
            await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);
        }
        clock.Advance(TimeSpan.FromMinutes(30));
        var reopenedAt = clock.GetUtcNow();

        using var reopened = OrbitalElementsStoreTestData.Open(root, clock);
        var state = await reopened.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalAttemptOutcome.Interrupted, state.LastAttemptOutcome);
        Assert.AreEqual("orbital.provider-outcome-unknown", state.LastAttemptReasonCode);
        Assert.IsNotNull(state.Halt);
        Assert.IsNull(state.Halt.HttpStatusCode, "No valid HTTP status was recorded.");
        Assert.AreEqual(reopenedAt, state.Halt.SinceUtc);
        Assert.StartsWith("orbital.provider-outcome-unknown: ", state.Halt.Reason);
        Assert.IsNull(state.LastRetrievalSuccessUtc);
        AssertReadModelAccepts(state);

        clock.Advance(OrbitalElementsStoreTestData.Interval);
        var halted = await reopened.TryBeginAttemptAsync("run", OrbitalAttemptTrigger.Manual, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(OrbitalAdmissionStatus.Halted, halted.Status, "Elapsed time never clears a fail-safe halt.");

        var resumed = await reopened.ResumeProviderAsync(new OrbitalResumeRequest(reopenedAt, "resume-unknown",
            "operator@example.test", "provider access confirmed"), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalCommandStatus.Applied, resumed.Status);
        Assert.IsNull(OrbitalElementsStoreTestData.Scalar(root, "SELECT halt_http_status FROM orbital_commands;") as long?);
        Assert.AreEqual(OrbitalAdmissionStatus.Admitted, (await reopened.TryBeginAttemptAsync(
            "run", OrbitalAttemptTrigger.Manual, CancellationToken.None).ConfigureAwait(false)).Status);
    }

    [TestMethod]
    public async Task AResumeAfterAnUnknownOutcomeHaltStillRespectsTheFloor()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using (var store = OrbitalElementsStoreTestData.Open(root, clock))
        {
            await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);
        }
        using var reopened = OrbitalElementsStoreTestData.Open(root, clock);
        var halt = (await reopened.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Halt!;

        var resumed = await reopened.ResumeProviderAsync(new OrbitalResumeRequest(halt.SinceUtc, "resume-early",
            "operator@example.test", "provider access confirmed"), CancellationToken.None).ConfigureAwait(false);
        var blocked = await reopened.TryBeginAttemptAsync("run", OrbitalAttemptTrigger.Manual, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(OrbitalCommandStatus.Applied, resumed.Status);
        Assert.AreEqual(OrbitalAdmissionStatus.FloorNotElapsed, blocked.Status);
        Assert.AreEqual(OrbitalElementsStoreTestData.Start + OrbitalElementsStoreTestData.Interval, blocked.NextPermittedAttemptUtc);
    }

    [TestMethod]
    public async Task ReconciliationNeverReplacesAnExistingHalt()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using (var store = OrbitalElementsStoreTestData.Open(root, clock))
        {
            await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);
        }
        // Not reachable through the API (a halt refuses admission); written directly to prove the guard.
        OrbitalElementsStoreTestData.Execute(root, """
            UPDATE orbital_provider_state SET halt_since_unix_ms = 1000, halt_http_status = 429, halt_reason = 'synthetic existing halt';
            """);
        clock.Advance(TimeSpan.FromMinutes(30));

        using var reopened = OrbitalElementsStoreTestData.Open(root, clock);
        var state = await reopened.GetStateAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual("orbital.provider-outcome-unknown", state.LastAttemptReasonCode);
        Assert.AreEqual(new OrbitalProviderHalt(DateTimeOffset.FromUnixTimeMilliseconds(1000), 429, "synthetic existing halt"),
            state.Halt);
    }

    [TestMethod]
    public async Task AHaltWithNoValidHttpStatusIsRecordedWithANullStatus()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        var attemptId = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);

        await store.RecordHaltAsync(attemptId, null, " ", CancellationToken.None).ConfigureAwait(false);

        var state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual((OrbitalAttemptOutcome.ProviderHalted, "orbital.invalid-http-status"),
            (state.LastAttemptOutcome!.Value, state.LastAttemptReasonCode));
        Assert.IsNull(state.Halt!.HttpStatusCode);
        Assert.StartsWith("orbital.invalid-http-status: ", state.Halt.Reason);
        Assert.IsNull(OrbitalElementsStoreTestData.Scalar(root, "SELECT http_status FROM orbital_attempts;") as long?);
        AssertReadModelAccepts(state);
        clock.Advance(OrbitalElementsStoreTestData.Interval);
        Assert.AreEqual(OrbitalAdmissionStatus.Halted, (await store.TryBeginAttemptAsync(
            "run", OrbitalAttemptTrigger.Manual, CancellationToken.None).ConfigureAwait(false)).Status);
    }

    [TestMethod]
    public async Task AHaltWithoutARecordedStatusIsLoggedWithoutAnyHttpLabelThroughResume()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        var logger = new OrbitalTestLogger<SqliteOrbitalElementsStore>();
        using var store = new SqliteOrbitalElementsStore(OrbitalElementsStoreTestData.Settings(root), clock, logger,
            NullOrbitalStorageFaultInjector.Instance);
        await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var attemptId = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);

        await store.RecordHaltAsync(attemptId, null, "orbital.invalid-http-status: synthetic", CancellationToken.None)
            .ConfigureAwait(false);
        var halt = (await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Halt!;
        var resumed = await store.ResumeProviderAsync(new OrbitalResumeRequest(halt.SinceUtc, "resume-null-status",
            "operator@example.test", "provider status confirmed"), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OrbitalCommandStatus.Applied, resumed.Status);
        var events = logger.EventIds;
        Assert.Contains(7716, events);
        Assert.Contains(7717, events);
        Assert.DoesNotContain(7703, events);
        Assert.DoesNotContain(7711, events);
        var rendered = logger.Rendered();
        StringAssert.Contains(rendered, "no valid HTTP status recorded", StringComparison.Ordinal);
        Assert.DoesNotContain("HTTP (null)", rendered);
        StringAssert.DoesNotMatch(rendered, new System.Text.RegularExpressions.Regex(@"HTTP \d"),
            "No HTTP status label is invented for a halt that has none.");
    }

    [TestMethod]
    public async Task ARecordedRetrievalAdvancesTheLastRetrievalSuccessAndAQuarantineKeepsIt()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        var attemptId = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);
        var retrievedAt = clock.GetUtcNow().AddSeconds(3);

        await store.RecordRetrievalAsync(attemptId, retrievedAt, CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.RecordRetrievalAsync(
            attemptId, retrievedAt.AddSeconds(1), CancellationToken.None).AsTask()).ConfigureAwait(false);
        await store.RecordQuarantineAsync(attemptId, OrbitalElementsStoreTestData.Raw("quarantined"), null, "orbital.schema",
            "synthetic", CancellationToken.None).ConfigureAwait(false);

        var state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalAttemptOutcome.Quarantined, state.LastAttemptOutcome);
        Assert.AreEqual(retrievedAt, state.LastRetrievalSuccessUtc);
        Assert.IsNull(state.LastActivationUtc);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.RecordRetrievalAsync(
            attemptId, retrievedAt, CancellationToken.None).AsTask()).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AnInterruptedAttemptHaltsOnlyWhenNoProviderOutcomeWasRecorded(bool retrieved)
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        var attemptId = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);
        if (retrieved)
        {
            await store.RecordRetrievalAsync(attemptId, clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(false);

        var unknown = await store.RecordInterruptedAsync(attemptId, cancelled.Token).ConfigureAwait(false);

        var state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(!retrieved, unknown);
        Assert.AreEqual(OrbitalAttemptOutcome.Interrupted, state.LastAttemptOutcome);
        Assert.AreEqual(retrieved ? "orbital.attempt-cancelled" : "orbital.provider-outcome-unknown", state.LastAttemptReasonCode);
        Assert.AreEqual(!retrieved, state.Halt is not null);
        Assert.IsNull(state.Halt?.HttpStatusCode);
        AssertReadModelAccepts(state);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => store.RecordInterruptedAsync(attemptId, CancellationToken.None).AsTask()).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task AHaltBlocksAdmissionUntilAnExactReplaySafeResume()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        var attemptId = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);
        clock.Advance(TimeSpan.FromSeconds(5));
        await store.RecordHaltAsync(attemptId, 403, "forbidden by provider", CancellationToken.None).ConfigureAwait(false);
        var haltedAt = OrbitalElementsStoreTestData.Start.AddSeconds(5);

        var halted = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(new OrbitalProviderHalt(haltedAt, 403, "forbidden by provider"), halted.Halt);
        Assert.AreEqual(OrbitalAttemptOutcome.ProviderHalted, halted.LastAttemptOutcome);
        Assert.AreEqual("orbital.provider-http-403", halted.LastAttemptReasonCode);
        AssertReadModelAccepts(halted);

        clock.Advance(TimeSpan.FromHours(3));
        var admission = await store.TryBeginAttemptAsync("run", OrbitalAttemptTrigger.Manual, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(OrbitalAdmissionStatus.Halted, admission.Status);
        Assert.AreEqual(halted.Halt, admission.Halt);
        Assert.IsNull(admission.AttemptId);

        var request = new OrbitalResumeRequest(haltedAt, "resume-1", "operator@example.test", "provider access restored");
        var mismatch = await store.ResumeProviderAsync(request with { ExpectedHaltSinceUtc = haltedAt.AddSeconds(1) },
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual((OrbitalCommandStatus.Conflict, "orbital.halt-mismatch"), (mismatch.Status, mismatch.ReasonCode));
        Assert.IsNotNull(mismatch.State.Halt);
        var invalid = await store.ResumeProviderAsync(request with { Actor = " " }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual((OrbitalCommandStatus.Invalid, "orbital.resume-invalid-actor"), (invalid.Status, invalid.ReasonCode));
        var offset = await store.ResumeProviderAsync(request with { ExpectedHaltSinceUtc = haltedAt.ToOffset(TimeSpan.FromHours(-7)) },
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalCommandStatus.Invalid, offset.Status);

        var applied = await store.ResumeProviderAsync(request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual((OrbitalCommandStatus.Applied, "orbital.resumed"), (applied.Status, applied.ReasonCode));
        Assert.IsNull(applied.State.Halt);
        Assert.AreEqual(OrbitalElementsStoreTestData.Start + OrbitalElementsStoreTestData.Interval, applied.State.NextPermittedAttemptUtc,
            "Resume never resets the floor.");
        var replay = await store.ResumeProviderAsync(request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual((OrbitalCommandStatus.Unchanged, "orbital.resume-replayed"), (replay.Status, replay.ReasonCode));
        Assert.AreEqual(applied.State.Revision, replay.State.Revision);
        var conflict = await store.ResumeProviderAsync(request with { Reason = "another reason" }, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual((OrbitalCommandStatus.Conflict, "orbital.idempotency-conflict"), (conflict.Status, conflict.ReasonCode));
        var noHalt = await store.ResumeProviderAsync(request with { IdempotencyKey = "resume-2" }, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual((OrbitalCommandStatus.Conflict, "orbital.no-halt"), (noHalt.Status, noHalt.ReasonCode));
        Assert.AreEqual(1L, OrbitalElementsStoreTestData.Scalar(root, "SELECT COUNT(*) FROM orbital_commands;"));

        Assert.AreEqual(OrbitalAdmissionStatus.Admitted, (await store.TryBeginAttemptAsync(
            "run", OrbitalAttemptTrigger.Manual, CancellationToken.None).ConfigureAwait(false)).Status);
    }

    [TestMethod]
    public async Task AHaltIsRecordedEvenWhenCancellationIsRequestedAndABlankReasonGetsADefault()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        var attemptId = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(false);

        await store.RecordHaltAsync(attemptId, 503, "\u0001\u0002\t", cancelled.Token).ConfigureAwait(false);

        var state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(503, state.Halt!.HttpStatusCode);
        Assert.AreEqual("provider returned HTTP 503", state.Halt.Reason);
        AssertReadModelAccepts(state);
    }

    [TestMethod]
    [DataRow(200)]
    [DataRow(99)]
    [DataRow(600)]
    public async Task AHaltRequiresANonSuccessHttpStatus(int status)
    {
        var root = CreateRoot();
        using var store = OrbitalElementsStoreTestData.Open(root, new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start));
        var attemptId = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => store.RecordHaltAsync(attemptId, status, "reason", CancellationToken.None).AsTask()).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task HaltReasonsAreBoundedAndStrippedOfControlCharacters()
    {
        var root = CreateRoot();
        using var store = OrbitalElementsStoreTestData.Open(root, new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start));
        var attemptId = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);
        await store.RecordHaltAsync(attemptId, 429, "line\nbreak" + new string('x', 600), CancellationToken.None).ConfigureAwait(false);

        var reason = (await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Halt!.Reason;
        Assert.AreEqual(512, reason.Length);
        Assert.StartsWith("line break", reason);
        var surrogatePair = char.ConvertFromUtf32(0x1F600);
        Assert.AreEqual(new string('a', 511), SqliteOrbitalElementsStore.BoundText(new string('a', 511) + surrogatePair),
            "A surrogate pair is never split at the bound.");
    }

    [TestMethod]
    public async Task CompletionRequiresTheExactInFlightAttempt()
    {
        var root = CreateRoot();
        using var store = OrbitalElementsStoreTestData.Open(root, new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start));
        var attemptId = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.RecordTransportFailureAsync(
            attemptId, "has space", "detail", CancellationToken.None).AsTask()).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.RecordTransportFailureAsync(
            new string('A', 32), "orbital.transport", "detail", CancellationToken.None).AsTask()).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.RecordTransportFailureAsync(
            new string('0', 32), "orbital.transport", "detail", CancellationToken.None).AsTask()).ConfigureAwait(false);

        await store.RecordTransportFailureAsync(attemptId, "orbital.transport-dns", "no route", CancellationToken.None)
            .ConfigureAwait(false);
        var state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalAttemptOutcome.TransportFailed, state.LastAttemptOutcome);
        Assert.AreEqual("orbital.transport-dns", state.LastAttemptReasonCode);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.RecordTransportFailureAsync(
            attemptId, "orbital.transport", "again", CancellationToken.None).AsTask()).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.RecordHaltAsync(
            attemptId, 403, "late", CancellationToken.None).AsTask()).ConfigureAwait(false);
        Assert.IsNull((await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Halt);
    }

    [TestMethod]
    public async Task AStorageFailureClosesTheInFlightAttemptWithoutAHaltAndStillHoldsTheFloor()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        var attemptId = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);
        var revision = (await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Revision;

        await store.RecordStorageFailureAsync(attemptId, "orbital.storage-no-space", "synthetic", CancellationToken.None)
            .ConfigureAwait(false);

        var state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual((OrbitalAttemptOutcome.StorageFailed, "orbital.storage-no-space"),
            (state.LastAttemptOutcome!.Value, state.LastAttemptReasonCode));
        Assert.IsNull(state.Halt);
        Assert.IsNull(state.LastRetrievalSuccessUtc);
        Assert.AreEqual(revision + 1, state.Revision);
        AssertReadModelAccepts(state);
        var blocked = await store.TryBeginAttemptAsync("run", OrbitalAttemptTrigger.Manual, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(OrbitalAdmissionStatus.FloorNotElapsed, blocked.Status);
        clock.Advance(OrbitalElementsStoreTestData.Interval);
        Assert.AreEqual(OrbitalAdmissionStatus.Admitted, (await store.TryBeginAttemptAsync(
            "run", OrbitalAttemptTrigger.Manual, CancellationToken.None).ConfigureAwait(false)).Status);
    }

    [TestMethod]
    public async Task AStorageFailureRequiresTheExactInFlightAttempt()
    {
        var root = CreateRoot();
        using var store = OrbitalElementsStoreTestData.Open(root, new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start));
        var attemptId = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.RecordStorageFailureAsync(
            new string('0', 32), "orbital.storage-io", "unknown", CancellationToken.None).AsTask()).ConfigureAwait(false);
        await store.RecordTransportFailureAsync(attemptId, "orbital.transport-dns", "no route", CancellationToken.None)
            .ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.RecordStorageFailureAsync(
            attemptId, "orbital.storage-io", "late", CancellationToken.None).AsTask()).ConfigureAwait(false);

        var state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual((OrbitalAttemptOutcome.TransportFailed, "orbital.transport-dns"),
            (state.LastAttemptOutcome!.Value, state.LastAttemptReasonCode), "A terminal outcome is never replaced.");
    }

    [TestMethod]
    public async Task AStorageFailureLeavesAnExistingHaltAndItsStatusUntouched()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        var attemptId = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);
        // Not reachable through the API (a halt refuses admission); written directly to prove the guard.
        OrbitalElementsStoreTestData.Execute(root, """
            UPDATE orbital_provider_state SET halt_since_unix_ms = 1000, halt_http_status = 429, halt_reason = 'synthetic existing halt';
            """);

        await store.RecordStorageFailureAsync(attemptId, "orbital.storage-io", "synthetic", CancellationToken.None)
            .ConfigureAwait(false);

        var state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalAttemptOutcome.StorageFailed, state.LastAttemptOutcome);
        Assert.AreEqual(new OrbitalProviderHalt(DateTimeOffset.FromUnixTimeMilliseconds(1000), 429, "synthetic existing halt"),
            state.Halt);
        Assert.IsNull(OrbitalElementsStoreTestData.Scalar(root, "SELECT http_status FROM orbital_attempts;") as long?);
    }

    [TestMethod]
    public async Task AStorageFailureLeavesTheLastRetrievalSuccessUnchanged()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        var attemptId = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);
        var retrievedAt = clock.GetUtcNow().AddSeconds(3);
        await store.RecordRetrievalAsync(attemptId, retrievedAt, CancellationToken.None).ConfigureAwait(false);
        clock.Advance(TimeSpan.FromMinutes(1));

        await store.RecordStorageFailureAsync(attemptId, "orbital.storage-io", "synthetic", CancellationToken.None)
            .ConfigureAwait(false);

        var state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalAttemptOutcome.StorageFailed, state.LastAttemptOutcome);
        Assert.AreEqual(retrievedAt, state.LastRetrievalSuccessUtc);
        Assert.IsNull(state.LastActivationUtc);
    }

    [TestMethod]
    public async Task QuarantineRetainsOnlyTheNewestBoundedEvidenceAndNeverActivates()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock,
            OrbitalElementsStoreTestData.Settings(root, maximumQuarantined: 2));
        var hashes = new List<string>();
        foreach (var marker in new[] { "q1", "q2", "q3" })
        {
            var raw = OrbitalElementsStoreTestData.Raw(marker);
            hashes.Add(OrbitalElementsStoreTestData.Hash(raw));
            var attemptId = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);
            await store.RecordQuarantineAsync(attemptId, raw, null, "orbital.candidate-invalid", "schema mismatch",
                CancellationToken.None).ConfigureAwait(false);
            clock.Advance(OrbitalElementsStoreTestData.Interval);
        }

        CollectionAssert.AreEquivalent(hashes.Skip(1).Select(hash => hash + ".json").ToArray(),
            OrbitalElementsStoreTestData.Files(root, "quarantine"));
        CollectionAssert.AreEqual(OrbitalElementsStoreTestData.Raw("q3"),
            await File.ReadAllBytesAsync(Path.Combine(root, "quarantine", hashes[2] + ".json")).ConfigureAwait(false));
        var state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(state.CurrentManifestSha256);
        Assert.AreEqual(OrbitalAttemptOutcome.Quarantined, state.LastAttemptOutcome);
        Assert.AreEqual(hashes[2], state.Quarantine!.RawSha256);
        Assert.AreEqual("orbital.candidate-invalid", state.Quarantine.Reason);
        Assert.AreEqual(OrbitalElementsStoreTestData.Start + (2 * OrbitalElementsStoreTestData.Interval), state.Quarantine.ObservedUtc);
        AssertReadModelAccepts(state);
        Assert.AreEqual(2L, OrbitalElementsStoreTestData.Scalar(root, "SELECT COUNT(*) FROM orbital_attempts WHERE quarantine_retained = 1;"));

        var empty = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);
        await store.RecordQuarantineAsync(empty, ReadOnlyMemory<byte>.Empty, null, "orbital.empty-response", "",
            CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull((await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Quarantine!.RawSha256);
        Assert.HasCount(2, OrbitalElementsStoreTestData.Files(root, "quarantine"));

        clock.Advance(OrbitalElementsStoreTestData.Interval);
        var mismatched = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.RecordQuarantineAsync(mismatched,
            OrbitalElementsStoreTestData.Raw("q4"), hashes[0], "orbital.candidate-invalid", "", CancellationToken.None).AsTask())
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task QuarantineWithRetentionDisabledKeepsNoEvidenceFile()
    {
        var root = CreateRoot();
        using var store = OrbitalElementsStoreTestData.Open(root, new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start),
            OrbitalElementsStoreTestData.Settings(root, maximumQuarantined: 0));
        var attemptId = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);
        var raw = OrbitalElementsStoreTestData.Raw("q");
        await store.RecordQuarantineAsync(attemptId, raw, OrbitalElementsStoreTestData.Hash(raw), "orbital.candidate-invalid", "x",
            CancellationToken.None).ConfigureAwait(false);
        Assert.IsEmpty(OrbitalElementsStoreTestData.Files(root, "quarantine"));
        Assert.AreEqual(OrbitalElementsStoreTestData.Hash(raw),
            (await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Quarantine!.RawSha256);
    }

    [TestMethod]
    public async Task PublicationActivatesImmutableFilesAndAnIdenticalPayloadIsUnchanged()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var store = OrbitalElementsStoreTestData.Open(root, clock);
        var first = OrbitalElementsStoreTestData.Build("raw-1", "25544", "100001");
        var second = OrbitalElementsStoreTestData.Build("raw-2", "25544", "100001");
        Assert.AreNotEqual(first.Manifest, second.Manifest);
        Assert.AreEqual(first.Payload, second.Payload);

        var attemptId = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);
        var published = await store.PublishAndActivateAsync(attemptId, first.Snapshot, first.Raw, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(new OrbitalPublication(first.Manifest, first.Payload, false, 2, 2), published);
        CollectionAssert.AreEqual(first.Snapshot.SerializeManifest(),
            await File.ReadAllBytesAsync(Path.Combine(root, "manifests", first.Manifest + ".json")).ConfigureAwait(false));
        CollectionAssert.AreEqual(first.Snapshot.SerializePayload(),
            await File.ReadAllBytesAsync(Path.Combine(root, "payloads", first.Payload + ".json")).ConfigureAwait(false));
        CollectionAssert.AreEqual(first.Raw,
            await File.ReadAllBytesAsync(Path.Combine(root, "raw", first.RawSha256 + ".json")).ConfigureAwait(false));
        var state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(first.Manifest, state.CurrentManifestSha256);
        Assert.AreEqual(first.Payload, state.CurrentPayloadSha256);
        Assert.AreEqual((2, 2), (state.CurrentRecordCount, state.CurrentObjectCount));
        Assert.AreEqual(OrbitalElementsStoreTestData.Start, state.CurrentMinimumEpochUtc);
        Assert.AreEqual(OrbitalElementsStoreTestData.Start, state.CurrentMaximumEpochUtc);
        Assert.AreEqual(first.Snapshot.Manifest.Sources[0].RetrievedUtc, state.LastRetrievalSuccessUtc);
        Assert.AreEqual(OrbitalElementsStoreTestData.Start, state.LastActivationUtc);
        Assert.AreEqual(OrbitalAttemptOutcome.Activated, state.LastAttemptOutcome);
        Assert.AreEqual("orbital.activated", state.LastAttemptReasonCode);
        AssertReadModelAccepts(state);

        clock.Advance(OrbitalElementsStoreTestData.Interval);
        var unchanged = await store.PublishAndActivateAsync(await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false),
            second.Snapshot, second.Raw, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(unchanged.PayloadUnchanged);
        Assert.AreEqual(second.Manifest, unchanged.ManifestSha256);
        state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(second.Manifest, state.CurrentManifestSha256);
        Assert.AreEqual(OrbitalAttemptOutcome.UnchangedPayload, state.LastAttemptOutcome);
        Assert.HasCount(1, OrbitalElementsStoreTestData.Files(root, "payloads"));
        Assert.HasCount(2, OrbitalElementsStoreTestData.Files(root, "manifests"));
        Assert.HasCount(2, OrbitalElementsStoreTestData.Files(root, "raw"));
        Assert.IsEmpty(OrbitalElementsStoreTestData.Files(root, "staging"));
        Assert.AreEqual(first.Manifest, OrbitalElementsStoreTestData.Scalar(root, "SELECT previous_manifest_sha256 FROM orbital_provider_state;"));
        Assert.AreEqual("activated,unchanged-payload", OrbitalElementsStoreTestData.Scalar(root,
            "SELECT group_concat(kind, ',') FROM (SELECT kind FROM orbital_activation_history ORDER BY history_sequence);"));
        Assert.AreEqual(first.Manifest, OrbitalElementsStoreTestData.Scalar(root,
            "SELECT previous_manifest_sha256 FROM orbital_activation_history WHERE history_sequence = 2;"));
    }

    [TestMethod]
    public async Task AnEmptyActiveGroupIsAValidSnapshot()
    {
        var root = CreateRoot();
        using var store = OrbitalElementsStoreTestData.Open(root, new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start));
        var raw = OrbitalElementsStoreTestData.Raw("empty");
        var empty = OrbitalSnapshot.Create([], [OrbitalElementsStoreTestData.Build("empty").Snapshot.Manifest.Sources[0]],
            new OrbitalPopulationCoverage("active-group", OrbitalPopulationCompleteness.Partial, []));
        var publication = await store.PublishAndActivateAsync(await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false),
            empty, raw, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual((0, 0), (publication.RecordCount, publication.ObjectCount));
        var state = await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(state.CurrentMinimumEpochUtc);
        Assert.AreEqual(0, state.CurrentRecordCount);
    }

    [TestMethod]
    [DataRow("raw-bytes")]
    [DataRow("raw-length")]
    [DataRow("provider")]
    [DataRow("empty-raw")]
    public async Task CandidatesThatDoNotBindAreRejectedBeforeStorageIsTouched(string shape)
    {
        var root = CreateRoot();
        using var store = OrbitalElementsStoreTestData.Open(root, new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start));
        var candidate = shape == "provider"
            ? OrbitalElementsStoreTestData.BuildFromProvider("spacetrack", "raw", "25544")
            : OrbitalElementsStoreTestData.Build("raw");
        byte[] raw = shape switch
        {
            "raw-bytes" => OrbitalElementsStoreTestData.Raw("rax"),
            "raw-length" => [.. candidate.Raw, (byte)' '],
            "empty-raw" => [],
            _ => candidate.Raw
        };
        var attemptId = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.PublishAndActivateAsync(
            attemptId, candidate.Snapshot, raw, CancellationToken.None).AsTask()).ConfigureAwait(false);

        foreach (var directory in new[] { "raw", "payloads", "manifests", "staging" })
        {
            Assert.IsEmpty(OrbitalElementsStoreTestData.Files(root, directory), directory);
        }
        Assert.AreEqual(OrbitalAttemptOutcome.InFlight, (await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).LastAttemptOutcome);
        await store.RecordQuarantineAsync(attemptId, raw, null, "orbital.candidate-binding", "did not bind", CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(OrbitalAttemptOutcome.Quarantined, (await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).LastAttemptOutcome);
    }

    [TestMethod]
    [DataRow("UPDATE orbital_activation_history SET kind = 'activated';")]
    [DataRow("DELETE FROM orbital_activation_history;")]
    [DataRow("DELETE FROM orbital_snapshots;")]
    [DataRow("UPDATE orbital_snapshots SET payload_length_bytes = payload_length_bytes + 1;")]
    [DataRow("UPDATE orbital_provider_state SET revision = 0;")]
    [DataRow("UPDATE orbital_provider_state SET snapshot_sequence = 0;")]
    [DataRow("UPDATE orbital_provider_state SET active_manifest_sha256 = NULL, previous_manifest_sha256 = NULL;")]
    [DataRow("DELETE FROM orbital_attempts;")]
    [DataRow("UPDATE orbital_attempts SET outcome = 'Activated' WHERE outcome = 'UnchangedPayload';")]
    [DataRow("UPDATE orbital_installation SET created_unix_ms = 0;")]
    [DataRow("UPDATE orbital_snapshots SET payload_sha256 = lower(payload_sha256);")]
    [DataRow("UPDATE orbital_attempts SET retrieved_utc_ticks = 0;")]
    public async Task DurableHistoryCountersAndIdentitiesCannotBeRewritten(string sql)
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using (var store = OrbitalElementsStoreTestData.Open(root, clock))
        {
            await OrbitalElementsStoreTestData.PublishAsync(store, clock, OrbitalElementsStoreTestData.Build("raw-1", "25544"))
                .ConfigureAwait(false);
            await OrbitalElementsStoreTestData.PublishAsync(store, clock, OrbitalElementsStoreTestData.Build("raw-2", "25544"))
                .ConfigureAwait(false);
        }
        Assert.ThrowsExactly<SqliteException>(() => OrbitalElementsStoreTestData.Execute(root, sql));
    }

    internal static void AssertReadModelAccepts(OrbitalProviderState state)
        => new OrbitalRefreshStatus(OrbitalRefreshStatus.CurrentVersion, OrbitalElementsStoreTestData.Start, state.Revision, true,
            state.CurrentManifestSha256, state.CurrentPayloadSha256, state.LastAttemptUtc, state.LastRetrievalSuccessUtc,
            state.LastActivationUtc, null, state.NextPermittedAttemptUtc, null, state.Halt, state.Quarantine, "synthetic",
            OrbitalRefreshRecoveryAction.None).Validate();
}
