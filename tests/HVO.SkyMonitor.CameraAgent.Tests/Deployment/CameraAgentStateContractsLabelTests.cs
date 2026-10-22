using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Tests.State;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Deployment;

/// <summary>
/// Keeps the CameraAgent image's state contract, its <c>io.hvo.skymonitor.state-contracts</c> label, which release.json's
/// <c>stateContracts.cameraAgent</c> repeats (install contract section 6), equal to the stores this release keeps. Every
/// SQLite store an instance keeps is declared; each store's <c>writes</c> is the schema the store records about itself
/// when this release creates it; and its <c>reads</c> are the schemas this release opens: the one it writes, and for raw
/// ingress also the two it migrates from.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class CameraAgentStateContractsLabelTests
{
    private const string Label = "io.hvo.skymonitor.state-contracts";

    // Each store's database under the instance's state root, and how the store records its schema (section 6). The
    // artifact outbox keeps one database in each storage root.
    private static readonly (string Store, string Location, string Marker)[] Stores =
    [
        ("identity", "identity/cameraagent_identity.db", "SELECT COUNT(*) FROM \"__EFMigrationsHistory\";"),
        ("rawIngress", "raw/journal/raw-ingress.db", "PRAGMA user_version;"),
        ("artifactOutbox", "*/outbox/artifact-outbox.db", "SELECT version FROM artifact_outbox_schema WHERE schema_key = 1;"),
        ("fleetStatus", "raw/.fleet/fleet-status.db", "SELECT schema_version FROM fleet_status_metadata WHERE metadata_key = 1;"),
        ("environmentalOutbox", "raw/.environment/environmental-observation-outbox.db", "PRAGMA user_version;"),
        ("executionEvidenceOutbox", "raw/evidence/execution-evidence-outbox.db", "SELECT version FROM execution_evidence_schema WHERE schema_key = 1;"),
        ("timeLapses", "raw/.time-lapses/time-lapses.db", "PRAGMA user_version;"),
        ("nightlyProducts", "raw/.nightly-products/nightly-products.db", "PRAGMA user_version;"),
        ("localAutomations", "raw/.automation/local-automations.db", "PRAGMA user_version;"),
        ("orbitalElements", "raw/.orbital-elements/orbital-elements.db", "PRAGMA user_version;"),
        ("spaceWeather", "raw/.space-weather/space-weather.db", "PRAGMA user_version;"),
        ("calibrationFamilies", "raw/.calibration-families/calibration-families.db", "PRAGMA user_version;")
    ];

    private static readonly string[] StorageRoots = ["/app/data/raw", "/app/data/archive", "/app/data/archive/products"];

    [TestMethod]
    public async Task Dockerfile_DeclaresWhatEveryStoreThisReleaseCreatesWrites()
    {
        var declared = ReadContract();
        using var harness = CameraAgentStateStoreHarness.Create(StorageRoots);
        await harness.InitializeStoresAsync().ConfigureAwait(false);

        var written = new SortedDictionary<string, SortedSet<long>>(StringComparer.Ordinal);
        foreach (var location in harness.StoreLocations())
        {
            var (store, _, marker) = Stores.SingleOrDefault(candidate => Holds(candidate.Location, location));
            Assert.IsNotNull(store, $"{location} is a store the CameraAgent state contract does not declare.");
            written.TryAdd(store, []);
            written[store].Add(ReadMarker(harness.StorePath(location), marker));
        }

        CollectionAssert.AreEqual(
            Stores.Select(static store => store.Store).Order(StringComparer.Ordinal).ToArray(), written.Keys.ToArray(),
            "every store the contract maps is one this release creates");
        CollectionAssert.AreEqual(
            written.Keys.ToArray(), declared.Keys.Order(StringComparer.Ordinal).ToArray(),
            $"{Label} declares exactly the stores this release keeps");
        foreach (var (store, versions) in written)
        {
            Assert.HasCount(1, versions, $"every {store} database this release creates records one schema");
            Assert.AreEqual(versions.Single(), declared[store].Writes, $"{store} writes the schema it records");
        }
    }

    [TestMethod]
    public void Dockerfile_DeclaresTheSchemasEachStoreThisReleaseOpens()
    {
        var declared = ReadContract();

        foreach (var (store, contract) in declared)
        {
            // No store opens a schema newer than the one it writes.
            Assert.AreEqual(contract.Writes, contract.Max, $"{store} reads no schema newer than it writes");
            if (store != "rawIngress")
            {
                // Every other store refuses any schema but the one it writes, at startup.
                Assert.AreEqual(contract.Writes, contract.Min, $"{store} reads only the schema it writes");
            }
        }
        // Raw ingress migrates 13 and 14 in place to the schema it writes.
        var rawIngress = declared["rawIngress"];
        long[] opened =
        [
            SqliteRawCaptureJournal.PreviousSchemaVersion, SqliteRawCaptureJournal.PreviousRecoverySchemaVersion,
            SqliteRawCaptureJournal.CurrentSchemaVersion
        ];
        Assert.AreEqual(SqliteRawCaptureJournal.CurrentSchemaVersion, rawIngress.Writes);
        CollectionAssert.AreEqual(
            opened,
            Enumerable.Range((int)rawIngress.Min, (int)(rawIngress.Max - rawIngress.Min + 1)).Select(static schema => (long)schema).ToArray());
    }

    private static SortedDictionary<string, (long Writes, long Min, long Max)> ReadContract()
    {
        var labels = CameraAgentStateCompatibilityLabelTests.ReadImageLabels();
        Assert.IsTrue(labels.TryGetValue(Label, out var text), $"the CameraAgent image has no {Label} label");
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowDuplicateProperties = false });
        var contract = new SortedDictionary<string, (long Writes, long Min, long Max)>(StringComparer.Ordinal);
        foreach (var store in document.RootElement.EnumerateObject())
        {
            var reads = store.Value.GetProperty("reads");
            contract.Add(store.Name, (
                store.Value.GetProperty("writes").GetInt64(), reads.GetProperty("min").GetInt64(), reads.GetProperty("max").GetInt64()));
        }
        return contract;
    }

    private static bool Holds(string pattern, string location)
        => pattern.StartsWith("*/", StringComparison.Ordinal)
            ? location.EndsWith(pattern[1..], StringComparison.Ordinal)
            : location == pattern;

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Callers pass only the fixed schema queries in Stores.")]
    private static long ReadMarker(string path, string marker)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = marker;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
