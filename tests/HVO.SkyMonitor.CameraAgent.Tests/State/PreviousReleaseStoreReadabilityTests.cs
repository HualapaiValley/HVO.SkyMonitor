using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Tests.State;

/// <summary>
/// The CameraAgent migration rule (#1314, install contract section 6), enforced for every SQLite store: what a release
/// leaves in each store stays readable by the previous release for one release, so a rollback is still a swap. Add
/// first, remove in a later release.
/// </summary>
/// <remarks>
/// <para>
/// This is a test rather than an architecture rule because the rule is about persisted state, not code shape. The
/// previous release's stores are frozen as SQL images in <c>tests/fixtures/state-compatibility/previous-release</c>:
/// every table, its fresh metadata rows, every index, trigger and view, and the user version, at the location an
/// instance keeps the store, for the storage roots the manifest names. Every CameraAgent store validates its own
/// schema exactly, so the expectation is the complete schema, compared object by object, together with the rows that
/// record a store's version.
/// </para>
/// <para>
/// Two directions are checked. The stores this release creates must be exactly the previous release's. And the
/// previous release's stores, once this release has opened them (and migrated them, if it does), must still be exactly
/// the previous release's. A change the previous release cannot read fails here. It ships only as a declared
/// <c>snapshotRollbackExceptions</c> entry naming the store, its objects, and the image state-boundary label whose
/// change makes the installer treat the upgrade as a migration that snapshots the stores and rolls back by restoring
/// them. The fixture changes only in the release pull request that cuts a version.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class PreviousReleaseStoreReadabilityTests
{
    /// <summary>The image labels the installer compares to decide that an upgrade declares a state migration.</summary>
    private static readonly HashSet<string> InstallerStateBoundaryLabels = new(StringComparer.Ordinal)
    {
        "io.hvo.skymonitor.state-compatibility", "io.hvo.skymonitor.minimum-compatible-revision",
        "io.hvo.skymonitor.identity-migration", "io.hvo.skymonitor.raw-ingress-schema",
        "io.hvo.skymonitor.raw-ingress-migration", "io.hvo.skymonitor.catalog-manifest-version"
    };

    [TestMethod]
    public async Task CurrentStores_AreExactlyThePreviousReleaseStores()
    {
        var previous = PreviousRelease.Load();
        using var harness = CameraAgentStateStoreHarness.Create(previous.StorageRoots);
        await harness.InitializeStoresAsync().ConfigureAwait(false);
        if (Environment.GetEnvironmentVariable("HVO_WRITE_PREVIOUS_RELEASE_FIXTURES") is { Length: > 0 } output)
        {
            PreviousRelease.Write(output, harness);
            Assert.Inconclusive($"Wrote the previous-release store images to {output}.");
        }

        CollectionAssert.AreEqual(previous.Locations, harness.StoreLocations().ToArray(),
            "This release keeps a different set of SQLite stores than the previous release.");
        AssertReadableByThePreviousRelease(previous, harness);
    }

    [TestMethod]
    public async Task PreviousReleaseStores_OpenedByThisRelease_StayReadableByThePreviousRelease()
    {
        var previous = PreviousRelease.Load();
        using var harness = CameraAgentStateStoreHarness.Create(previous.StorageRoots);
        foreach (var location in previous.Locations)
        {
            CameraAgentStateStoreHarness.Rebuild(harness.StorePath(location), previous.Dump(location));
        }

        // The upgraded instance's startup: every store is opened, and migrated if this release migrates it.
        await harness.InitializeStoresAsync().ConfigureAwait(false);

        AssertReadableByThePreviousRelease(previous, harness);
    }

    [TestMethod]
    [DataRow("raw/journal/raw-ingress.db", "ALTER TABLE processing_nodes ADD COLUMN added_by_next_release TEXT;")]
    [DataRow("raw/journal/raw-ingress.db", "CREATE TABLE added_by_next_release(id INTEGER PRIMARY KEY);")]
    [DataRow("raw/journal/raw-ingress.db", "PRAGMA user_version = 99;")]
    [DataRow("raw/.fleet/fleet-status.db", "ALTER TABLE fleet_status_records ADD COLUMN added_by_next_release TEXT;")]
    [DataRow("archive/outbox/artifact-outbox.db", "CREATE INDEX ix_added_by_next_release ON artifact_outbox_records(record_id);")]
    [DataRow("identity/cameraagent_identity.db", "ALTER TABLE AspNetUsers ADD COLUMN added_by_next_release TEXT;")]
    [DataRow("raw/.fleet/fleet-status.db", "UPDATE fleet_status_metadata SET schema_version = schema_version + 1;")]
    [DataRow("identity/cameraagent_identity.db", "INSERT INTO \"__EFMigrationsHistory\" VALUES('29990101000000_AddedByNextRelease', '10.0.13');")]
    public async Task TheRuleRefusesAStoreThePreviousReleaseWouldReject(string location, string change)
    {
        // The check bites, including a processing table gaining a column while the processing schema stays at 8, and a
        // version kept in a row changing while no DDL does.
        ArgumentNullException.ThrowIfNull(location);
        var previous = PreviousRelease.Load();
        using var harness = CameraAgentStateStoreHarness.Create(previous.StorageRoots);
        await harness.InitializeStoresAsync().ConfigureAwait(false);
        Assert.IsEmpty(UnreadableObjects(previous, harness));
        Execute(harness.StorePath(location), change);

        var unreadable = UnreadableObjects(previous, harness);

        Assert.IsNotEmpty(unreadable, change);
        Assert.IsTrue(unreadable.All(entry => entry.StartsWith(location + " ", StringComparison.Ordinal)), string.Join("; ", unreadable));
    }

    private static void AssertReadableByThePreviousRelease(PreviousRelease previous, CameraAgentStateStoreHarness harness)
    {
        var unreadable = UnreadableObjects(previous, harness);
        Assert.IsEmpty(unreadable,
            "The previous release cannot read these objects: " + string.Join("; ", unreadable) +
            ". Make the previous release accept the change first, or declare it in snapshotRollbackExceptions.");
    }

    /// <summary>Every object the previous release would reject, less the declared exceptions, as "store object".</summary>
    private static List<string> UnreadableObjects(PreviousRelease previous, CameraAgentStateStoreHarness harness)
    {
        var unreadable = new List<string>();
        foreach (var location in previous.Locations)
        {
            var path = harness.StorePath(location);
            if (!File.Exists(path))
            {
                unreadable.Add($"{location} (missing)");
                continue;
            }
            using var expected = new TemporaryStore(previous.Dump(location));
            foreach (var difference in CameraAgentStateStoreHarness.Differences(
                         CameraAgentStateStoreHarness.ReadSchema(expected.Path), CameraAgentStateStoreHarness.ReadSchema(path)))
            {
                if (!previous.IsDeclaredException(location, difference)) unreadable.Add($"{location} {difference}");
            }
        }
        return unreadable;
    }

    private static void Execute(string path, string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Test-owned statements, not input.
        command.CommandText = sql;
#pragma warning restore CA2100
        command.ExecuteNonQuery();
    }

    /// <summary>The frozen previous release: its storage roots, its stores, and its declared exceptions.</summary>
    private sealed class PreviousRelease
    {
        private const string ManifestName = "manifest.json";
        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
        private readonly string directory;
        private readonly Dictionary<string, string> dumps;
        private readonly List<(string Location, HashSet<string> Objects, string Label)> exceptions;

        private PreviousRelease(
            string directory, IReadOnlyList<string> storageRoots, Dictionary<string, string> dumps,
            List<(string, HashSet<string>, string)> exceptions)
        {
            this.directory = directory;
            StorageRoots = storageRoots;
            this.dumps = dumps;
            this.exceptions = exceptions;
        }

        public IReadOnlyList<string> StorageRoots { get; }

        public string[] Locations => [.. dumps.Keys.Order(StringComparer.Ordinal)];

        public string Dump(string location) => File.ReadAllText(Path.Combine(directory, dumps[location]));

        public bool IsDeclaredException(string location, string objectKey)
            => exceptions.Any(exception => exception.Location == location && exception.Objects.Contains(objectKey));

        public static PreviousRelease Load()
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "previous-release");
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, ManifestName)));
            var root = document.RootElement;
            Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
            var dumps = root.GetProperty("stores").EnumerateArray().ToDictionary(
                static store => store.GetProperty("location").GetString()!,
                static store => store.GetProperty("dump").GetString()!,
                StringComparer.Ordinal);
            var exceptions = new List<(string, HashSet<string>, string)>();
            foreach (var exception in root.GetProperty("snapshotRollbackExceptions").EnumerateArray())
            {
                var label = exception.GetProperty("stateBoundaryLabel").GetString()!;
                // A declared exception is detected by the installer only through a state-boundary label it compares.
                Assert.IsTrue(InstallerStateBoundaryLabels.Contains(label),
                    $"Exception label '{label}' is not a state boundary the installer compares, so it would not snapshot the stores.");
                exceptions.Add((exception.GetProperty("location").GetString()!,
                    exception.GetProperty("objects").EnumerateArray().Select(static item => item.GetString()!).ToHashSet(StringComparer.Ordinal),
                    label));
            }
            return new PreviousRelease(
                directory,
                [.. root.GetProperty("storageRoots").EnumerateArray().Select(static item => item.GetString()!)],
                dumps,
                exceptions);
        }

        public static void Write(string output, CameraAgentStateStoreHarness harness)
        {
            Directory.CreateDirectory(output);
            var stores = new List<object>();
            foreach (var location in harness.StoreLocations())
            {
                var file = location.Replace("/", "__", StringComparison.Ordinal) + ".sql";
                File.WriteAllText(Path.Combine(output, file), CameraAgentStateStoreHarness.Dump(harness.StorePath(location)));
                stores.Add(new { location, dump = file });
            }
            File.WriteAllText(Path.Combine(output, ManifestName), JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                release = "development/v1 baseline",
                note = "The previous CameraAgent release's SQLite stores, as images that rebuild each one. No release has shipped, so the current development/v1 stores stand in. Regenerate them only in the release pull request that cuts a version, with HVO_WRITE_PREVIOUS_RELEASE_FIXTURES set when running PreviousReleaseStoreReadabilityTests; an ordinary pull request that needs a different image is shipping a store the previous release cannot read.",
                storageRoots = harness.StorageRoots,
                stores,
                snapshotRollbackExceptions = Array.Empty<object>()
            }, WriteOptions) + "\n");
        }
    }

    private sealed class TemporaryStore : IDisposable
    {
        public TemporaryStore(string dump)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hvo-previous-store-{Guid.NewGuid():N}.db");
            CameraAgentStateStoreHarness.Rebuild(Path, dump);
        }

        public string Path { get; }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(Path);
        }
    }
}
