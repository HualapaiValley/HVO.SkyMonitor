using System.Diagnostics;
using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests.Silo;

public sealed partial class SiloStockStoreTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task StockSiloDaytimeProfileKeepsJpegAndSettlesFullSizeExpiryAfterHolds()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        var keys = ArchiveKeys();
        var container = BuildCappedRetentionStore(keys.Access, keys.Secret);
        await using var lifetime = container.ConfigureAwait(false);
        await container.StartAsync(token).ConfigureAwait(false);
        using var client = SiloS3ObjectStore.CreateClient(Endpoint(container), keys.Access, keys.Secret);
        await CreateVersionedBucketAsync(client, Bucket, token).ConfigureAwait(false);
        var binding = new SiloStorageBinding("profile-fixture", "profile-local", Bucket);
        var local = new SiloS3ObjectStore(client, new(binding.StoreId, binding.Bucket, SiloStoreClass.Local,
            SiloRetrieval.Online, PersistenceQualified: true), TimeSpan.FromSeconds(10));
        var clock = new ArchiveTestClock();
        using var directory = new ArchiveTestDirectory();
        using var root = SiloStateRoot.Acquire(directory.Path);
        var state = new SiloPublicationState(root.PublicationDatabasePath, binding, clock);
        await state.InitializeAsync(token).ConfigureAwait(false);
        using var index = new SiloCatalogIndex(root.IndexDatabasePath, binding, clock);
        await index.InitializeAsync(token).ConfigureAwait(false);
        using var journal = new SiloJournalWriter(state, root.Spool, local, binding, clock);
        await journal.OpenFirstGenerationAsync(new Dictionary<string, long>(), token).ConfigureAwait(false);
        var catalog = new SiloCatalog(index, new SiloStoreSet(local), SiloNoAuthorityEvidence.Instance);
        var spool = new SiloFilePayloadSpool(root, 32 * 1024 * 1024, 32);
        using var publisher = new SiloPublisher(state, root.Spool, journal, catalog, spool, binding, clock);
        var physical = await ReadPhysicalHeadroomAsync(container, token).ConfigureAwait(false);
        var budget = new SiloDiskBudget(() => physical, 1, 1, 1, 1);
        var retention = new SiloRetention(state, index, publisher, catalog, journal, root.Root, local, budget, clock);
        await retention.SetPolicyAsync(new(false, [],
            [new("daytime",
                [new("Raw", TimeSpan.Zero), new("Calibrated", TimeSpan.Zero), new("Combined", TimeSpan.Zero),
                 new("Preview", TimeSpan.Zero), new("AnnotatedPreview", TimeSpan.Zero),
                 new("Preview", TimeSpan.FromDays(2), "image/jpeg"),
                 new("AnnotatedPreview", TimeSpan.FromDays(2), "image/jpeg")], SkyConditions: ["Day"])]), token)
            .ConfigureAwait(false);

        const int width = 1936;
        const int height = 1216;
        const int quality = 85;
        const string packed = "application/x-hvo-packed-image";
        var raw = new byte[width * height * 2];
        var preview = new byte[width * height];
        var annotated = new byte[preview.Length * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = y * width + x;
                var level = (byte)(55 + y * 96 / height + x * 32 / width + (x ^ y) % 13);
                raw[2 * pixel] = (byte)(level << 4);
                raw[2 * pixel + 1] = (byte)(level >> 4);
                preview[pixel] = level;
                annotated[3 * pixel] = y < 16 && x % 16 < 3 ? (byte)240 : level;
                annotated[3 * pixel + 1] = level;
                annotated[3 * pixel + 2] = level;
            }
        }
        var calibrated = raw.ToArray();
        calibrated[0] ^= 1;
        var combined = raw.ToArray();
        combined[0] ^= 3;
        var encodeStarted = Stopwatch.GetTimestamp();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var jpeg = JpegImageCodec.EncodeMono8ToJpeg(width, height, preview, quality: quality, cancellationToken: token);
        var annotatedJpeg = JpegImageCodec.EncodeRgb24ToJpeg(width, height, annotated, quality: quality, cancellationToken: token);
        var encodeAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var encodeElapsed = Stopwatch.GetElapsedTime(encodeStarted);

        var now = clock.GetUtcNow();
        var captureId = Guid.NewGuid().ToString("N");
        var input = await PublishAsync("raw-capture", captureId, new([], CaptureCondition: new(null, "Day")),
            [("raw", packed, raw)]).ConfigureAwait(false);
        var initial = (await catalog.ReopenAsync(captureId, "1", token).ConfigureAwait(false)).Items
            .Single(static item => item.Role.Kind == SiloIdentityKind.Member);
        Assert.AreEqual(SiloDisposition.Available, initial.Disposition);
        Assert.IsNotNull(initial.ServedFrom);
        var ingressHold = new SiloRetentionHold(SiloHoldKind.ProcessingInput, initial.Role.Id, initial.Role.Revision);
        Assert.AreEqual("held-reference", (await retention.DryRunAsync(clock.GetUtcNow(), false, [ingressHold], token).ConfigureAwait(false))
            .Single(candidate => candidate.Locator == initial.ServedFrom).Decision);

        var outputs = await PublishAsync("processing-products", Guid.NewGuid().ToString("N"), new([], RetentionCaptureId: captureId),
            [("Calibrated", packed, calibrated), ("Combined", packed, combined),
             ("Preview", packed, preview), ("AnnotatedPreview", packed, annotated),
             ("Preview", "image/jpeg", jpeg), ("AnnotatedPreview", "image/jpeg", annotatedJpeg)]).ConfigureAwait(false);
        var members = input.Concat(outputs).ToArray();
        var allIds = members.Select(static member => Guid.ParseExact(member.ArtifactId, "D")).ToArray();
        var payloads = new[] { raw, calibrated, combined, preview, annotated, jpeg, annotatedJpeg };
        var holds = new[]
        {
            new SiloRetentionHold(SiloHoldKind.StackContributor, members[0].ArtifactId),
            new SiloRetentionHold(SiloHoldKind.ProcessingInput, members[1].ArtifactId),
            new SiloRetentionHold(SiloHoldKind.RestoreOrReplay, members[2].ArtifactId)
        };
        var planned = await retention.DryRunAsync(clock.GetUtcNow(), false, holds, token).ConfigureAwait(false);
        foreach (var hold in holds)
            Assert.AreEqual("held-reference", planned.Single(item => item.References.Any(r => r.Identity.Id == hold.ArtifactId)).Decision);
        var replay = await retention.TryAcquireAsync([initial.ServedFrom], TimeSpan.FromMinutes(5), token).ConfigureAwait(false);
        Assert.IsNotNull(replay);
        Assert.AreEqual("no-eligible-versions", await retention.SweepAsync(true, _ => ValueTask.FromResult<IReadOnlyList<SiloRetentionHold>>([]),
            null, token, locator => locator == initial.ServedFrom).ConfigureAwait(false));
        await retention.ReleaseAsync(replay, token).ConfigureAwait(false);
        var stored = new long[members.Length];
        for (var i = 0; i < members.Length; i++)
        {
            var member = members[i];
            var role = await index.ReadRoleAsync(new(SiloIdentityKind.Member, member.ArtifactId, member.ArtifactRevision), token)
                .ConfigureAwait(false);
            Assert.IsNotNull(role);
            var locator = role.Locators.Single().Locator;
            var proof = await SiloVerifier.ProbeAsync(local, locator, member.Length, member.Sha256, clock, true, token)
                .ConfigureAwait(false);
            Assert.AreEqual(SiloProbeStatus.Verified, proof.Status);
            Assert.IsNotNull(proof.Bytes);
            CollectionAssert.AreEqual(payloads[i], proof.Bytes);
            stored[i] = proof.Bytes.LongLength;
        }

        for (var i = 0; i < 5; i++)
        {
            Assert.AreEqual("deleted", await retention.SweepAsync(true, NoHolds, null, token).ConfigureAwait(false));
            physical = await ReadPhysicalHeadroomAsync(container, token).ConfigureAwait(false);
        }
        Assert.AreEqual("no-eligible-versions", await retention.SweepAsync(true, NoHolds, null, token).ConfigureAwait(false));
        var expired = await index.ReadPolicyExpiredArtifactsAsync(allIds, token).ConfigureAwait(false);
        CollectionAssert.AreEquivalent(allIds[..5], expired.ToArray());
        Assert.IsFalse((await catalog.ReopenAsync(captureId, "1", token).ConfigureAwait(false)).Reopenable);
        foreach (var member in outputs.TakeLast(2))
        {
            var role = await index.ReadRoleAsync(new(SiloIdentityKind.Member, member.ArtifactId, member.ArtifactRevision), token)
                .ConfigureAwait(false);
            Assert.IsNotNull(role);
            var proof = await SiloVerifier.ProbeAsync(local, role.Locators.Single().Locator, member.Length, member.Sha256, clock, true, token)
                .ConfigureAwait(false);
            Assert.AreEqual(SiloProbeStatus.Verified, proof.Status);
            Assert.IsNotNull(proof.Bytes);
            var decoded = JpegImageCodec.DecodeJpeg(proof.Bytes, cancellationToken: token);
            Assert.AreEqual(width, decoded.Width);
            Assert.AreEqual(height, decoded.Height);
        }

        var baselineBytes = stored[..5].Sum();
        var compressedScienceBytes = stored[..3].Sum() + stored[5..].Sum();
        var daytimeBytes = stored[5..].Sum();
        Assert.IsLessThan(baselineBytes, compressedScienceBytes);
        Assert.IsLessThan(compressedScienceBytes, daytimeBytes);
        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Silo payload bytes/capture {width}x{height}, JPEG {quality}: uncompressed={baselineBytes}; science+JPEG={compressedScienceBytes}; daytime JPEGs={daytimeBytes}; preview JPEG={stored[5]}; annotated JPEG={stored[6]}; JPEG encode ms={encodeElapsed.TotalMilliseconds:F2}; managed encode bytes={encodeAllocatedBytes}"));

        ValueTask<IReadOnlyList<SiloRetentionHold>> NoHolds(CancellationToken _) =>
            ValueTask.FromResult<IReadOnlyList<SiloRetentionHold>>([]);

        async Task<SiloDraftMember[]> PublishAsync(string kind, string id, SiloManifestProvenance provenance,
            (string Role, string MediaType, byte[] Bytes)[] payloads)
        {
            var bytesByToken = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var members = payloads.Select(payload =>
            {
                var sha = SiloHash.Sha256(payload.Bytes);
                var source = SiloFilePayloadSpool.TokenOf(sha);
                bytesByToken.Add(source, payload.Bytes);
                return new SiloDraftMember(Guid.NewGuid().ToString("D"), "1", payload.Role,
                    payload.MediaType == "image/jpeg" || payload.Role is "Preview" or "AnnotatedPreview"
                        ? SiloRetentionClass.Display : SiloRetentionClass.Full,
                    payload.MediaType, payload.Bytes.Length, sha, true, source);
            }).ToArray();
            var draft = new SiloGroupDraft(kind, id, "1", now, now.AddSeconds(-1), now, members, [], provenance);
            await publisher.StageAsync(draft, (member, cancellation) => SiloArtifactStorage.OpenMemoryAsync(
                bytesByToken[member.SourceToken], cancellation), token).ConfigureAwait(false);
            Assert.AreEqual(SiloStagedState.Visible, (await publisher.PublishAsync(id, "1", token).ConfigureAwait(false)).State);
            return members;
        }
    }
}
