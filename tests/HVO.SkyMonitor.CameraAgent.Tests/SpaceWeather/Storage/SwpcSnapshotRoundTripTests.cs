using System.Globalization;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Storage;

[TestClass]
[TestCategory("Unit")]
public sealed class SwpcSnapshotRoundTripTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "swpc-products-" + Guid.NewGuid().ToString("N"));
    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    [DataRow(SwpcProduct.Scales, "noaa-scales.json", "/products/noaa-scales.json")]
    [DataRow(SwpcProduct.KpForecast, "noaa-planetary-k-index-forecast.json", "/products/noaa-planetary-k-index-forecast.json")]
    [DataRow(SwpcProduct.ThreeDayForecast, "3-day-forecast.original.json", "/text/3-day-forecast.txt")]
    public async Task ApprovedProductsRoundTripExactBytesTypedFactsAndRecordedValidators(SwpcProduct product, string file, string path)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "space-weather", "swpc-v1", "capture-20261019T1857Z", file)).ConfigureAwait(false);
        if (product == SwpcProduct.ThreeDayForecast)
        {
            using var wrapper = JsonDocument.Parse(bytes); bytes = Encoding.UTF8.GetBytes(wrapper.RootElement.GetProperty("text").GetString()!);
        }
        SwpcParsedBody parsed; string hash; DateTimeOffset? stamp;
        if (product == SwpcProduct.Scales) { var b = SwpcScalesParser.Parse(bytes, "application/json"); parsed = new SwpcScalesBody(b); hash = b.SnapshotSha256!; stamp = b.StampUtc; }
        else if (product == SwpcProduct.KpForecast) { var b = SwpcKpForecastParser.Parse(bytes, "application/json"); parsed = new SwpcKpBody(b); hash = b.SnapshotSha256!; stamp = b.StampUtc; }
        else { var b = SwpcThreeDayForecastParser.Parse(bytes, "text/plain"); parsed = new SwpcThreeDayBody(b); hash = b.SnapshotSha256!; stamp = b.StampUtc; }
        var at = DateTimeOffset.Parse("2026-10-20T12:00:00Z", CultureInfo.InvariantCulture);
        var snapshot = new SwpcSnapshot(product, path, at, at, 200, product == SwpcProduct.ThreeDayForecast ? "text/plain" : "application/json",
            bytes.Length, hash, stamp, new("W/\"verbatim\"", "Mon, 19 Oct 2026 18:55:42 GMT"), [.. bytes], parsed);
        using (var store = new SqliteSwpcStore(SwpcStoreTests.Settings(_root), NullLogger<SqliteSwpcStore>.Instance))
        {
            var view = await store.ReadAsync(at, CancellationToken.None).ConfigureAwait(false);
            var start = new SwpcAttemptStart(Guid.NewGuid(), view.Revision, product, "fixture-round-trip",
                SwpcAttemptTrigger.Scheduled, at, new(null, null), SwpcStoreTests.Empty);
            var begun = await store.BeginAttemptAsync(start, CancellationToken.None).ConfigureAwait(false);
            var saved = await store.CompleteAttemptAsync(new(start.AttemptId, begun.View.Revision, at, at, at,
                SwpcFetchOutcome.Updated, 200, "updated", bytes.Length, SwpcValidatorOutcome.Replaced, snapshot),
                _ => new(SwpcStoreTests.Empty, null, null, []), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(SwpcWriteStatus.Applied, saved.Status);
        }
        using var reopened = new SqliteSwpcStore(SwpcStoreTests.Settings(_root), NullLogger<SqliteSwpcStore>.Instance);
        var value = (await reopened.ReadAsync(at, CancellationToken.None).ConfigureAwait(false)).Products[product];
        Assert.AreEqual(hash, value.LastKnownGood!.Sha256); Assert.AreEqual(stamp, value.LastKnownGood.InFileStampUtc);
        Assert.AreEqual(snapshot.ResponseValidators, value.Validators);
        CollectionAssert.AreEqual(bytes, value.LastKnownGood.DecodedBody.ToArray());
        Assert.AreEqual(JsonSerializer.Serialize(parsed), JsonSerializer.Serialize(value.LastKnownGood.Parsed));
        var health = JsonSerializer.Serialize(await reopened.ReadHealthAsync(at, CancellationToken.None).ConfigureAwait(false));
        Assert.IsFalse(health.Contains("verbatim", StringComparison.Ordinal)); Assert.IsFalse(health.Contains("DecodedBody", StringComparison.Ordinal));
    }
}
