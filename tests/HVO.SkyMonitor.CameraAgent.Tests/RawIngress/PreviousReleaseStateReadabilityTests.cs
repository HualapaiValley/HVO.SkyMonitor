using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Deployment;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

/// <summary>
/// The CameraAgent migration rule (#1314, install contract section 6): what a release leaves in the shared
/// raw-ingress journal stays readable by the previous release for one release, so a rollback is still a swap. Add
/// first and remove in a later release.
/// </summary>
/// <remarks>
/// <para>
/// This is a test rather than an architecture rule because the rule is about persisted state, not code shape. It
/// takes the previous release's expectations, frozen as data in
/// <c>tests/fixtures/state-compatibility/cameraagent-previous-release.json</c>, and applies them to the journal the
/// current code creates: the user versions that release opens without refusing, and the canonical schema its exact
/// validator compares. The journal validates itself against the same canonical schema after migrating an earlier
/// version, so a migrated journal and a fresh one are the same schema and one check covers both.
/// </para>
/// <para>
/// When this fails, the change is a migration the previous release cannot read. Make the previous release accept it
/// first, or declare it as a breaking state change in a release, whose rollback restores the upgrade's database
/// snapshot instead of swapping. The fixture changes only in the release pull request that cuts a new version.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Unit")]
public sealed class PreviousReleaseStateReadabilityTests
{
    [TestMethod]
    public async Task CurrentRawIngressJournal_IsReadableByThePreviousRelease()
    {
        var previous = await PreviousRelease.LoadAsync().ConfigureAwait(false);
        using var journal = await TemporaryJournal.CreateAsync().ConfigureAwait(false);

        Assert.IsTrue(
            ReadableByPreviousRelease(journal.Connection, previous, out var reason),
            $"The raw-ingress journal this release leaves cannot be read by the previous release ({previous.Release}): {reason}.");
    }

    [TestMethod]
    public async Task CurrentCaptureProcessingSchema_IsReadableByThePreviousRelease()
    {
        var previous = await PreviousRelease.LoadAsync().ConfigureAwait(false);

        CollectionAssert.Contains(
            previous.ReadableProcessingSchemaVersions.ToArray(),
            SqliteCaptureProcessingStore.CurrentSchemaVersion,
            $"Capture processing schema {SqliteCaptureProcessingStore.CurrentSchemaVersion} is not one the previous release ({previous.Release}) opens.");
    }

    [TestMethod]
    [DataRow("added-table")]
    [DataRow("removed-index")]
    [DataRow("version-bump")]
    public async Task TheRuleRefusesAJournalThePreviousReleaseWouldReject(string change)
    {
        // The check bites: each change a next release might make is one the previous release's exact validator refuses.
        var previous = await PreviousRelease.LoadAsync().ConfigureAwait(false);
        using var journal = await TemporaryJournal.CreateAsync().ConfigureAwait(false);
        Assert.IsTrue(ReadableByPreviousRelease(journal.Connection, previous, out _));
        using (var command = journal.Connection.CreateCommand())
        {
#pragma warning disable CA2100 // Test-owned statements, not input.
            command.CommandText = change switch
            {
                "added-table" => "CREATE TABLE added_by_next_release(id INTEGER PRIMARY KEY);",
                "removed-index" => $"DROP INDEX {FirstIndex(journal.Connection)};",
                _ => string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {previous.ReadableUserVersions.Max() + 1};")
            };
#pragma warning restore CA2100
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        Assert.IsFalse(ReadableByPreviousRelease(journal.Connection, previous, out var reason), change);
        Assert.IsFalse(string.IsNullOrEmpty(reason));
    }

    private static bool ReadableByPreviousRelease(SqliteConnection connection, PreviousRelease previous, out string reason)
    {
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        var userVersion = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (!previous.ReadableUserVersions.Contains(userVersion))
        {
            reason = $"user_version {userVersion} is not one of {string.Join(", ", previous.ReadableUserVersions)}";
            return false;
        }
        var fingerprint = RawIngressV13Schema.ComputeFingerprint(connection);
        if (!string.Equals(fingerprint, previous.CanonicalFingerprint, StringComparison.Ordinal))
        {
            reason = $"canonical schema fingerprint {fingerprint} differs from {previous.CanonicalFingerprint}";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static string FirstIndex(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'index' AND sql IS NOT NULL AND name NOT LIKE 'ix_processing%' AND name NOT LIKE 'ix_transient_worker%' ORDER BY name LIMIT 1;";
        return (string)command.ExecuteScalar()!;
    }

    /// <summary>What the previous release accepts, read from the frozen fixture.</summary>
    private sealed record PreviousRelease(
        string Release,
        IReadOnlyList<int> ReadableUserVersions,
        string CanonicalFingerprint,
        IReadOnlyList<int> ReadableProcessingSchemaVersions)
    {
        public static async Task<PreviousRelease> LoadAsync()
        {
            var text = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "cameraagent-previous-release.json")).ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
            var rawIngress = root.GetProperty("rawIngress");
            return new PreviousRelease(
                root.GetProperty("release").GetString()!,
                [.. rawIngress.GetProperty("readableUserVersions").EnumerateArray().Select(static value => value.GetInt32())],
                rawIngress.GetProperty("canonicalFingerprint").GetString()!,
                [.. root.GetProperty("captureProcessing").GetProperty("readableSchemaVersions").EnumerateArray()
                    .Select(static value => value.GetInt32())]);
        }
    }

    /// <summary>A raw-ingress journal created by the current code in a private directory.</summary>
    private sealed class TemporaryJournal : IDisposable
    {
        private readonly string root;

        private TemporaryJournal(string root, SqliteConnection connection)
        {
            this.root = root;
            Connection = connection;
        }

        public SqliteConnection Connection { get; }

        public static async Task<TemporaryJournal> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"hvo-previous-release-{Guid.NewGuid():N}");
            var path = Path.Combine(root, "journal", "raw-ingress.db");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await new SqliteRawCaptureJournal(path, 5).InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            return new TemporaryJournal(root, connection);
        }

        public void Dispose()
        {
            Connection.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
