using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Storage;

[TestClass]
[TestCategory("Unit")]
public sealed class SwpcStoreFixtureTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "swpc-fixture-" + Guid.NewGuid().ToString("N"));
    private static string Pack => Path.Combine(AppContext.BaseDirectory, "Fixtures", "space-weather", "swpc-v1");
    private SqliteSwpcStore Open() => new(SwpcStoreTests.Settings(_root), NullLogger<SqliteSwpcStore>.Instance);
    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    [DataRow("s01-duplicate-refetch.json")]
    [DataRow("s02-same-identity-revised-content.json")]
    [DataRow("s03-correction-reissue.json")]
    [DataRow("s04-continuation-self-reference.json")]
    [DataRow("s05-extension-chain-expiry.json")]
    [DataRow("s06-extension-before-root.json")]
    [DataRow("s07-watch-supersession-and-cancel.json")]
    [DataRow("s09-synthetic-cancel-warning.json")]
    [DataRow("s10-restart-ack-rolling-window.json")]
    [DataRow("s11-stale-cdn-out-of-order.json")]
    [DataRow("s20-record-level-drift.json")]
    [DataRow("s21-pending-short-window-and-cap.json")]
    [DataRow("s26-original-record-verbatim.json")]
    [DataRow("s27-original-record-outer-whitespace.json")]
    public async Task CommittedFixturesPinMergeOriginalRecordsLifecycleAndRetention(string name)
    {
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(Pack, "scenarios", name)).ConfigureAwait(false));
        SqliteSwpcStore store = Open();
        try
        {
            var at = DateTimeOffset.MinValue;
            foreach (var step in document.RootElement.GetProperty("steps").EnumerateArray())
            {
                var kind = step.GetProperty("kind").GetString();
                if (step.TryGetProperty("atUtc", out var time)) at = DateTimeOffset.Parse(time.GetString()!, CultureInfo.InvariantCulture);
                SwpcStoreView view;
                ImmutableArray<string> diagnostics = [];
                if (kind == "restart")
                {
                    store.Dispose(); store = Open();
                    view = await store.ExecuteAsync(null, (db, tx) => store.ReadView(db, tx, at), CancellationToken.None).ConfigureAwait(false);
                }
                else if (kind == "fetch")
                {
                    var response = step.GetProperty("response");
                    var outcome = step.GetProperty("expect").GetProperty("outcome").GetString();
                    if (outcome is not ("updated" or "unchanged" or "stale-snapshot"))
                    {
                        // Fetch-policy assertions belong to S2. Timed rejected/transport steps still purge.
                        view = await store.ReadAsync(at, CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        var body = response.TryGetProperty("body", out var text) ? text.GetString()! : await File.ReadAllTextAsync(Path.Combine(Pack, response.GetProperty("bodyFile").GetString()!)).ConfigureAwait(false);
                        var snapshot = SwpcStoreTests.Alerts(body, at, response.TryGetProperty("etag", out var tag) ? tag.GetString() : null);
                        var selected = outcome == "updated" ? SwpcFetchOutcome.Updated : outcome == "unchanged" ? SwpcFetchOutcome.Unchanged : SwpcFetchOutcome.StaleSnapshot;
                        var result = await store.ExecuteAsync(at, (db, tx) =>
                        {
                            var merged = SqliteSwpcStore.SaveSnapshot(db, tx, snapshot, selected, at);
                            return (View: store.ReadView(db, tx, at), merged.Diagnostics);
                        }, CancellationToken.None).ConfigureAwait(false);
                        view = result.View; diagnostics = result.Diagnostics;
                    }
                }
                else if (kind == "acknowledge")
                {
                    var before = await store.ReadAsync(at, CancellationToken.None).ConfigureAwait(false);
                    var result = await store.AcknowledgeAsync(new(Guid.NewGuid(), before.Revision, at, step.GetProperty("group").GetString()!,
                        new(step.GetProperty("operator").GetString()!, at)), CancellationToken.None).ConfigureAwait(false);
                    Assert.AreEqual(SwpcWriteStatus.Applied, result.Status); view = result.View;
                }
                else view = await store.ReadAsync(at, CancellationToken.None).ConfigureAwait(false);

                if (!step.TryGetProperty("expect", out var expect)) continue;
                if (expect.TryGetProperty("diagnostics", out var expectedDiagnostics))
                    CollectionAssert.AreEqual(expectedDiagnostics.EnumerateArray().Select(x => x.GetString()).ToArray(), diagnostics.ToArray(), name + " diagnostics");
                if (expect.TryGetProperty("notices", out var notices))
                {
                    CollectionAssert.AreEquivalent(notices.EnumerateObject().Select(x => x.Name).ToArray(), view.Notices.Groups.Select(g => g.Identity).ToArray(), name + " groups");
                    foreach (var expected in notices.EnumerateObject())
                    {
                        var group = view.Notices.Groups.Single(g => g.Identity == expected.Name);
                        var window = SwpcNoticeLifecycle.Evaluate(group, view.Notices.Groups, at);
                        foreach (var field in expected.Value.EnumerateObject())
                        {
                            if (field.Name == "versions") Assert.AreEqual(field.Value.GetInt32(), group.Versions.Length);
                            if (field.Name == "orphan") Assert.AreEqual(field.Value.GetBoolean(), group.Orphan);
                            if (field.Name == "latest") Assert.AreEqual(field.Value.GetString(), group.Latest.Identity);
                            if (field.Name == "state") Assert.AreEqual(field.Value.GetString(), window.State.Token());
                            if (field.Name == "fromUtc") Assert.AreEqual(DateTimeOffset.Parse(field.Value.GetString()!, CultureInfo.InvariantCulture), window.FromUtc);
                            if (field.Name == "untilUtc") Assert.AreEqual(DateTimeOffset.Parse(field.Value.GetString()!, CultureInfo.InvariantCulture), window.UntilUtc);
                            if (field.Name == "acknowledged" && field.Value.ValueKind != JsonValueKind.Null)
                                Assert.AreEqual(field.Value.GetProperty("operator").GetString(), SwpcGroupBuilder.Acknowledgement(group, view.Notices.Acknowledgements)?.Operator);
                        }
                    }
                }
                if (expect.TryGetProperty("originalRecords", out var originals))
                    foreach (var identity in originals.EnumerateObject())
                    {
                        var actual = view.Notices.Versions.Where(v => v.Version.Message.Identity == identity.Name).OrderBy(v => v.Version.IngestSequence).Select(v => v.Version.Message.OriginalRecordJson).ToArray();
                        CollectionAssert.AreEqual(identity.Value.EnumerateArray().Select(x => x.GetString()).ToArray(), actual, name + " exact UTF-8 record spans");
                    }
                if (expect.TryGetProperty("product", out var product))
                {
                    var stored = view.Products[SwpcProduct.Alerts];
                    if (product.TryGetProperty("lkgSha256", out var sha)) Assert.AreEqual(sha.GetString(), stored.LastKnownGood?.Sha256);
                    if (product.TryGetProperty("lastSuccessUtc", out var last)) Assert.AreEqual(DateTimeOffset.Parse(last.GetString()!, CultureInfo.InvariantCulture), stored.LastSuccessUtc);
                }
            }
        }
        finally { store.Dispose(); }
    }

    [TestMethod]
    public async Task RawHistoryPrunesOldestUnprotectedContentAndPinsEveryLkg()
    {
        var at = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var raw = await File.ReadAllTextAsync(Path.Combine(Pack, "capture-20261019T1857Z", "noaa-scales.json")).ConfigureAwait(false);
        using var store = Open();
        SwpcSnapshot Snapshot(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text); var parsed = SwpcScalesParser.Parse(bytes, "application/json"); Assert.IsTrue(parsed.Accepted);
            return new(SwpcProduct.Scales, "/products/noaa-scales.json", at, at, 200, "application/json", bytes.Length, parsed.SnapshotSha256!,
                parsed.StampUtc, new(null, null), [.. bytes], new SwpcScalesBody(parsed));
        }
        var first = Snapshot(raw); var second = Snapshot(raw + "\n");
        await store.ExecuteAsync(at, (db, tx) => SqliteSwpcStore.SaveSnapshot(db, tx, first, SwpcFetchOutcome.Updated, at), CancellationToken.None).ConfigureAwait(false);
        await store.ExecuteAsync(at, (db, tx) => SqliteSwpcStore.SaveSnapshot(db, tx, second, SwpcFetchOutcome.Updated, at), CancellationToken.None).ConfigureAwait(false);
        // Returning to historical content creates another publication sharing the first raw blob.
        await store.ExecuteAsync(at, (db, tx) => SqliteSwpcStore.SaveSnapshot(db, tx, first, SwpcFetchOutcome.Updated, at), CancellationToken.None).ConfigureAwait(false);
        var alerts = SwpcStoreTests.Alerts(SwpcStoreTests.Message(), at);
        await store.ExecuteAsync(at, (db, tx) => SqliteSwpcStore.SaveSnapshot(db, tx, alerts, SwpcFetchOutcome.Updated, at), CancellationToken.None).ConfigureAwait(false);
        // Exercise the production algorithm with small real blobs, without allocating 256 MiB.
        var protectedBytes = first.DecodedByteCount + alerts.DecodedByteCount;
        var trimmed = await store.ExecuteAsync(null, (db, tx) => SqliteSwpcStore.PruneRaw(db, tx, at, protectedBytes), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2, trimmed.Snapshots);
        Assert.AreEqual(second.DecodedByteCount, trimmed.Bytes, "Removing the first publication cannot delete its shared LKG blob.");
        var health = await store.ReadHealthAsync(at, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(2L, health.SnapshotCount); Assert.AreEqual((long)protectedBytes, health.RawBodyBytes);
        var retained = await store.ReadAsync(at.AddDays(36), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(first.Sha256, retained.Products[SwpcProduct.Scales].LastKnownGood!.Sha256);
        Assert.AreEqual(alerts.Sha256, retained.Products[SwpcProduct.Alerts].LastKnownGood!.Sha256);
    }
}
