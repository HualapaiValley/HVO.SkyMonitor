using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

[TestClass]
[TestCategory("Integration")]
[TestProperty("Area", "Orbital")]
public sealed class OrbitalCsvRefreshIntegrationTests : OrbitalStoreTestBase
{
    [TestMethod]
    public async Task CsvRefreshPreservesLegacyJsonReplayAcrossRestart()
    {
        var root = CreateRoot();
        var clock = new SignalingTimeProvider(OrbitalElementsStoreTestData.Start);
        const string LegacyIdentity = "06F00D385E147B4F494A0D79D0ADB4E0A20A4BDD90799989BBFFBE4EDC502E3D";
        var legacyRaw = CelesTrakGpTestData.ReadFixture(CelesTrakGpTestData.LegacyJsonFixture);
        var legacyManifest = CelesTrakGpTestData.ReadFixture("celestrak-gp-active-legacy-manifest.json");
        var legacyPayload = CelesTrakGpTestData.ReadFixture("celestrak-gp-active-legacy-records.json");
        var legacy = OrbitalSnapshot.Parse(legacyManifest, legacyPayload);
        Assert.IsNotNull(legacy.Snapshot, legacy.Reason);
        Assert.AreEqual(LegacyIdentity, legacy.Snapshot.Manifest.ManifestSha256);
        var legacyRawHash = legacy.Snapshot.Manifest.Sources.Single().RawSha256;
        var csv = CelesTrakGpTestData.ValidCsv();
        var csvHash = CelesTrakGpTestData.Sha256(csv);
        string orbitalRoot;
        DateTimeOffset due;
        var before = await OrbitalCompositionHost.StartAsync(root, clock).ConfigureAwait(false);
        await using (before.ConfigureAwait(false))
        {
            orbitalRoot = before.OrbitalRoot;
            var attempt = await OrbitalElementsStoreTestData.AdmitAsync(before.Store).ConfigureAwait(false);
            await before.Store.PublishAndActivateAsync(attempt, legacy.Snapshot, legacyRaw, CancellationToken.None)
                .ConfigureAwait(false);
            due = (await before.SaveDefinitionAsync().ConfigureAwait(false)).NextRunUtc!.Value;
            Assert.AreEqual(0, before.Handler.Count);
        }

        var after = await OrbitalCompositionHost.StartAsync(root, clock).ConfigureAwait(false);
        await using var lifetime = after.ConfigureAwait(false);

        var beforeRefresh = await after.Reader.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(LegacyIdentity, beforeRefresh.Snapshot?.Manifest.ManifestSha256);

        after.AdvanceTo(due);
        after.Handler.EnqueueBody(csv);
        await after.SweepAsync().ConfigureAwait(false);

        var current = await after.Reader.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(current.Snapshot);
        Assert.AreEqual(OrbitalContract.CelesTrakGpCsvProfile, current.Snapshot.Manifest.ProfileVersion);
        var source = current.Snapshot.Manifest.Sources.Single();
        Assert.AreEqual("?GROUP=active&FORMAT=CSV", after.Handler.Requests.Single().RequestUri!.Query);
        Assert.AreEqual(source.SourceUri, after.Handler.Requests.Single().RequestUri);
        Assert.AreEqual(csvHash, source.RawSha256);
        Assert.Contains("US Space Force / Space-Track", source.TermsReference);
        Assert.AreEqual("US Space Force / Space-Track public GP data via CelesTrak", source.UnderlyingLineage);
        CollectionAssert.AreEqual(csv,
            await File.ReadAllBytesAsync(Path.Combine(orbitalRoot, "raw", csvHash + ".csv")).ConfigureAwait(false));
        CollectionAssert.AreEquivalent(new[] { legacyRawHash + ".json", csvHash + ".csv" },
            OrbitalElementsStoreTestData.Files(orbitalRoot, "raw"));

        var replay = await after.Reader.ReadExactAsync(LegacyIdentity, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(replay.Snapshot);
        Assert.AreEqual(OrbitalContract.CelesTrakGpProfile, replay.Snapshot.Manifest.ProfileVersion);
        var legacySource = replay.Snapshot.Manifest.Sources.Single();
        Assert.AreEqual("?GROUP=active&FORMAT=JSON", legacySource.SourceUri.Query);
        Assert.Contains("Underlying USSF public GP data", legacySource.TermsReference);
        CollectionAssert.AreEqual(legacyManifest, replay.Snapshot.SerializeManifest());
        CollectionAssert.AreEqual(legacyPayload, replay.Snapshot.SerializePayload());
        CollectionAssert.AreEqual(legacyRaw,
            await File.ReadAllBytesAsync(Path.Combine(orbitalRoot, "raw", legacyRawHash + ".json")).ConfigureAwait(false));
        Assert.AreEqual(1, after.Handler.Count);
    }

    [TestMethod]
    [DataRow(503, 15)]
    [DataRow(429, 0)]
    public async Task TheServerErrorCooldownSurvivesResumeAndRestart(int httpStatus, int cooldownMinutes)
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using (var store = OrbitalElementsStoreTestData.Open(root, clock))
        {
            var attempt = await OrbitalElementsStoreTestData.AdmitAsync(store).ConfigureAwait(false);
            // Model a forward UTC clock adjustment while the request is in flight.
            clock.Advance(OrbitalElementsStoreTestData.Interval);
            await store.RecordHaltAsync(attempt, httpStatus, "provider refusal", CancellationToken.None).ConfigureAwait(false);
            var halt = await store.TryBeginAttemptAsync("halted", OrbitalAttemptTrigger.Scheduled, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual(OrbitalAdmissionStatus.Halted, halt.Status);
            Assert.AreEqual(httpStatus, halt.Halt?.HttpStatusCode);
            var resumed = await store.ResumeProviderAsync(
                new OrbitalResumeRequest(clock.GetUtcNow(), "resume", "owner", "provider checked"), CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual(OrbitalCommandStatus.Applied, resumed.Status);
        }

        using var restarted = OrbitalElementsStoreTestData.Open(root, clock);
        var next = clock.GetUtcNow().AddMinutes(cooldownMinutes);
        var state = await restarted.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(next, state.NextPermittedAttemptUtc);
        if (cooldownMinutes > 0)
        {
            clock.Advance(TimeSpan.FromMinutes(cooldownMinutes) - TimeSpan.FromMilliseconds(1));
            var early = await restarted.TryBeginAttemptAsync("early", OrbitalAttemptTrigger.Manual, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual(OrbitalAdmissionStatus.FloorNotElapsed, early.Status);
            Assert.AreEqual(next, early.NextPermittedAttemptUtc);
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }
        await OrbitalElementsStoreTestData.AdmitAsync(restarted, "next").ConfigureAwait(false);
    }
}
