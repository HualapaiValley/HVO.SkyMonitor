using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Storage;

[TestClass]
[TestCategory("Unit")]
public sealed class SwpcStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "swpc-" + Guid.NewGuid().ToString("N"));
    internal static readonly SwpcFetchCheckpoint Empty = new(null, null, false, 0, 0, 0, null);
    private SqliteSwpcStore Open() => new(Settings(_root), NullLogger<SqliteSwpcStore>.Instance);
    internal static SwpcStoreSettings Settings(string root) => new(root, new Dictionary<SwpcProduct, SwpcFreshnessThresholds>
    {
        [SwpcProduct.Alerts] = new(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(3600)),
        [SwpcProduct.Scales] = new(TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(10800)),
        [SwpcProduct.KpForecast] = new(TimeSpan.FromSeconds(900), TimeSpan.FromSeconds(21600)),
        [SwpcProduct.ThreeDayForecast] = new(TimeSpan.FromSeconds(1800), TimeSpan.FromSeconds(93600))
    }.ToImmutableDictionary());
    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private SqliteConnection Database()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "space-weather.db"), Pooling = false }.ToString());
        db.Open(); return db;
    }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only private constant test mutations and literal DataRow statements are accepted.")]
    private void Sql(string sql)
    {
        using var db = Database(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    internal static SwpcSnapshot Alerts(string body, DateTimeOffset? time = null, string? etag = "W/\"a\"")
    {
        var at = time ?? Now; var bytes = Encoding.UTF8.GetBytes(body); var parsed = SwpcAlertsParser.Parse(bytes, "application/json");
        Assert.IsTrue(parsed.Accepted);
        return new(SwpcProduct.Alerts, "/products/alerts.json", at, at, 200, "application/json", bytes.Length,
            parsed.SnapshotSha256!, parsed.StampUtc, new(etag, "Tue, 20 Oct 2026 12:00:00 GMT"), [.. bytes], new SwpcAlertsBody(parsed));
    }
    internal static string Message(int serial = 1, string detail = "one", string? link = null, string code = "ALTZZZ") => JsonSerializer.Serialize(new[] { new
    {
        product_id = "SYNTH", issue_datetime = "2026-10-20 12:00:00.000",
        message = $"Space Weather Message Code: {code}\r\nSerial Number: {serial.ToString(CultureInfo.InvariantCulture)}\r\nIssue Time: 2026 Oct 20 1200 UTC\r\n\r\nALERT: Synthetic {detail}\n" + (link ?? "")
    }});
    private static ValueTask<(SwpcStoreView View, ImmutableArray<string> Diagnostics)> Save(SqliteSwpcStore store, SwpcSnapshot snapshot,
        SwpcFetchOutcome outcome = SwpcFetchOutcome.Updated, DateTimeOffset? at = null) => store.ExecuteAsync(at ?? Now, (db, tx) =>
        {
            var merged = SqliteSwpcStore.SaveSnapshot(db, tx, snapshot, outcome, at ?? Now);
            return (store.ReadView(db, tx, at ?? Now), merged.Diagnostics);
        }, CancellationToken.None);

    [TestMethod]
    public async Task CreatesStrictWalSchemaAndReopensWithoutChangingState()
    {
        using (var store = Open()) { await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false); Assert.HasCount(4, (await store.ReadAsync(Now, CancellationToken.None).ConfigureAwait(false)).Products); }
        using var db = Database(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM pragma_table_list WHERE schema = 'main' AND name LIKE 'swpc_%' AND strict <> 1;";
        Assert.AreEqual(0L, await cmd.ExecuteScalarAsync().ConfigureAwait(false)); cmd.CommandText = "PRAGMA journal_mode;"; Assert.AreEqual("wal", await cmd.ExecuteScalarAsync().ConfigureAwait(false));
        using var reopened = Open(); Assert.AreEqual(0L, (await reopened.ReadAsync(Now, CancellationToken.None).ConfigureAwait(false)).Revision);
    }
    [TestMethod]
    [DataRow("PRAGMA user_version=2;")]
    [DataRow("PRAGMA user_version=-1;")]
    [DataRow("CREATE TABLE foreign_table(x TEXT);")]
    [DataRow("ALTER TABLE swpc_products ADD COLUMN drift TEXT;")]
    public async Task UnknownUnversionedAndDriftedSchemasFailClosed(string mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        using (var store = Open()) await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        if (mutation.StartsWith("CREATE", StringComparison.Ordinal)) Sql("PRAGMA user_version=0;");
        Sql(mutation); using var reopened = Open();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.InitializeAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
        using var db = Database(); using var command = db.CreateCommand(); command.CommandText = "SELECT count(*) FROM swpc_products;"; Assert.AreEqual(4L, await command.ExecuteScalarAsync().ConfigureAwait(false));
    }
    [TestMethod]
    public async Task ASecondOwnerIsRefusedUntilDisposed()
    {
        using var first = Open(); await first.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        using var second = Open();
        var failure = await Assert.ThrowsExactlyAsync<SwpcStorageException>(() => second.InitializeAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
        Assert.AreEqual("space-weather.storage-locked", failure.ReasonCode);
        first.Dispose(); await second.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
    }
    [TestMethod]
    public async Task DuplicateRevisionStaleRevisionAndExactOriginalSurviveReopen()
    {
        var one = Alerts(Message()); var two = Alerts(Message(detail: "two"));
        using (var store = Open())
        {
            var first = await Save(store, one).ConfigureAwait(false); var duplicate = await Save(store, one).ConfigureAwait(false);
            Assert.IsEmpty(duplicate.Diagnostics); Assert.HasCount(1, duplicate.View.Notices.Versions);
            var revised = await Save(store, two).ConfigureAwait(false); Assert.HasCount(2, revised.View.Notices.Versions);
            Assert.HasCount(1, revised.Diagnostics);
            Assert.AreEqual("source-revision:ALTZZZ/1/2026-10-20T12:00:00Z", revised.Diagnostics[0]);
            var stale = await Save(store, Alerts(Message(detail: "ignored")), SwpcFetchOutcome.StaleSnapshot).ConfigureAwait(false);
            Assert.HasCount(2, stale.View.Notices.Versions); Assert.IsTrue(stale.Diagnostics.Any(x => x.StartsWith("stale-revision-ignored:", StringComparison.Ordinal)));
            var repeated = one with { Parsed = new SwpcAlertsBody(((SwpcAlertsBody)one.Parsed).Value with { Records = [((SwpcAlertsBody)one.Parsed).Value.Records[0], ((SwpcAlertsBody)one.Parsed).Value.Records[0]] }) };
            Assert.IsTrue((await Save(store, repeated).ConfigureAwait(false)).Diagnostics.Any(x => x.StartsWith("duplicate-record-in-body:", StringComparison.Ordinal)));
        }
        using var reopened = Open(); var view = await reopened.ReadAsync(Now, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(2, view.Notices.Versions); Assert.AreEqual(1L, view.Notices.Versions[0].Version.IngestSequence);
        Assert.AreEqual(((SwpcAlertsBody)one.Parsed).Value.Records[0].OriginalRecordJson, view.Notices.Versions[0].Version.Message.OriginalRecordJson);
    }
    [TestMethod]
    public async Task OrphansResolveAfterWholeBodyAndSelfLinksAreNotOrphans()
    {
        using var store = Open();
        var orphan = await Save(store, Alerts(Message(2, link: "Continuation of Serial Number: 1\n"))).ConfigureAwait(false);
        Assert.IsTrue(orphan.View.Notices.Groups[0].Orphan);
        Assert.AreEqual("orphan-link:ALTZZZ/2/2026-10-20T12:00:00Z", orphan.Diagnostics.Single());
        var resolved = await Save(store, Alerts(Message())).ConfigureAwait(false);
        Assert.IsFalse(resolved.View.Notices.Groups[0].Orphan); Assert.AreEqual("orphan-resolved:ALTZZZ/1", resolved.Diagnostics.Single());
        var self = await Save(store, Alerts(Message(3, link: "Continuation of Serial Number: 3\n"))).ConfigureAwait(false);
        Assert.IsFalse(self.View.Notices.Groups.Single(g => g.Identity == "ALTZZZ/3").Orphan);
    }
    [TestMethod]
    public async Task AcknowledgementHaltAndFreshnessAreDurableAndScoped()
    {
        using var store = Open(); var initial = (await Save(store, Alerts(Message())).ConfigureAwait(false)).View;
        var ack = await store.AcknowledgeAsync(new(Guid.NewGuid(), initial.Revision, Now, "ALTZZZ/1", new("operator", Now)), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SwpcWriteStatus.Applied, ack.Status);
        var product = await store.ChangeHaltAsync(new(Guid.NewGuid(), ack.View.Revision, Now, new(SwpcProduct.Alerts, new("gone", Now)), ImmutableDictionary<SwpcProduct, SwpcFetchCheckpoint>.Empty), CancellationToken.None).ConfigureAwait(false);
        var provider = await store.ChangeHaltAsync(new(Guid.NewGuid(), product.View.Revision, Now, new(null, new("forbidden", Now)), ImmutableDictionary<SwpcProduct, SwpcFetchCheckpoint>.Empty), CancellationToken.None).ConfigureAwait(false);
        var clear = await store.ChangeHaltAsync(new(Guid.NewGuid(), provider.View.Revision, Now, new(null, null), ImmutableDictionary<SwpcProduct, SwpcFetchCheckpoint>.Empty), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(clear.View.ProviderHalt); Assert.IsNotNull(clear.View.Products[SwpcProduct.Alerts].ProductHalt);
        Assert.AreEqual(SwpcFreshness.Fresh, clear.View.Products[SwpcProduct.Alerts].Freshness);
        Assert.AreEqual(SwpcFreshness.Stale, (await store.ReadAsync(Now.AddSeconds(360), CancellationToken.None).ConfigureAwait(false)).Products[SwpcProduct.Alerts].Freshness);
        Assert.AreEqual(SwpcFreshness.Outdated, (await store.ReadAsync(Now.AddSeconds(3600), CancellationToken.None).ConfigureAwait(false)).Products[SwpcProduct.Alerts].Freshness);
        Assert.AreEqual("operator", clear.View.Notices.Acknowledgements.Single().Value.Operator);
        var health = await store.ReadHealthAsync(Now, CancellationToken.None).ConfigureAwait(false); Assert.AreEqual(1L, health.MessageVersionCount);
    }
    [TestMethod]
    public async Task RetentionDeletesTheWholeGroupAtTheExactBoundaryButKeepsLkg()
    {
        using var store = Open(); await Save(store, Alerts(Message())).ConfigureAwait(false);
        var until = Now.AddHours(6); var boundary = until.AddDays(35);
        Assert.HasCount(1, (await store.ReadAsync(boundary.AddTicks(-1), CancellationToken.None).ConfigureAwait(false)).Notices.Versions);
        var purged = await store.ApplyRetentionAsync(boundary, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, purged.GroupsDeleted); Assert.AreEqual(1, purged.VersionsDeleted);
        var view = await store.ReadAsync(boundary, CancellationToken.None).ConfigureAwait(false); Assert.IsEmpty(view.Notices.Versions); Assert.IsNotNull(view.Products[SwpcProduct.Alerts].LastKnownGood);
    }

    [TestMethod]
    public async Task CorruptedRawBodyFailsClosedWithoutRewritingLastKnownGood()
    {
        using var store = Open(); await Save(store, Alerts(Message())).ConfigureAwait(false);
        Sql("UPDATE swpc_raw SET body=x'00';");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.ReadAsync(Now, CancellationToken.None).AsTask()).ConfigureAwait(false);
        using var db = Database(); using var command = db.CreateCommand();
        command.CommandText = "SELECT hex(body) FROM swpc_raw;";
        Assert.AreEqual("00", await command.ExecuteScalarAsync().ConfigureAwait(false), "A corrupt accepted body is preserved for explicit recovery.");
    }
}
