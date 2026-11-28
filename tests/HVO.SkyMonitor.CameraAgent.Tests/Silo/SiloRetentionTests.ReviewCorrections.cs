using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

public sealed partial class SiloRetentionTests
{
    [TestMethod]
    public async Task ReclaimBudgetSurvivesRestartAndTrashDrainWinsOverConcurrentCaptureGrowth()
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        foreach (var number in Enumerable.Range(1, 4))
            await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, number, rawRequired: false)).ConfigureAwait(false);
        silo.Clock.Advance(TimeSpan.FromMinutes(2));
        var total = Healthy.TotalBytes;
        var physical = Healthy with { AvailableBytes = 10 * SiloDiskBudget.GiB + 2 };
        var budget = new SiloDiskBudget(() => physical, 1, 1, 1, 8, measureAllocation: _ => 4096,
            reclaimCaptureHourBytes: 8192);
        var store = new TestRemoval(silo.Local)
        {
            Deleted = () => physical = physical with
            {
                AvailableBytes = physical.AvailableBytes - 8192,
                TrashBytes = physical.TrashBytes + 4096
            }
        };
        var retention = Open(silo, store, budget);
        await retention.SetPolicyAsync(new(false, [new("raw", TimeSpan.Zero, true, true)]), token).ConfigureAwait(false);
        Assert.AreEqual("deleted", await SweepAsync(retention, token).ConfigureAwait(false));
        var admission = budget.AdmissionRelease;
        Assert.IsNotNull(admission);
        Assert.AreEqual("deleted", await SweepAsync(retention, token).ConfigureAwait(false));
        await silo.RestartAsync().ConfigureAwait(false);
        retention = Open(silo, store, budget);
        Assert.HasCount(2, SiloRetentionState.Read(silo.StateRoot.Root).ReclaimLedger!.Entries);
        Assert.AreEqual("awaiting-physical-reclamation", await SweepAsync(retention, token).ConfigureAwait(false));
        Assert.AreEqual(2, store.Deletes);

        // The trash drain is visible while net free bytes still trail the pre-delete measurement.
        physical = new(total, physical.AvailableBytes + 4096, 0);
        Assert.AreEqual("deleted", await SweepAsync(retention, token).ConfigureAwait(false));
        Assert.AreEqual(3, store.Deletes);
        Assert.IsFalse(admission.IsCompleted);
        physical = new(total, 10 * SiloDiskBudget.GiB + 16, 0);
        Assert.IsNotNull(budget.Observe());
        Assert.IsTrue(admission.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task UnprojectedManifestHoldsRecentMembersAndPotentialExternalDependencies()
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var first = SiloDrafts.Capture(silo.Payloads, 1, rawRequired: false);
        await silo.PublishAsync(first).ConfigureAwait(false);
        var existing = first.Members[0];
        var next = SiloDrafts.Capture(silo.Payloads, 2, rawRequired: false) with
        {
            Dependencies = [new(existing.ArtifactId, existing.ArtifactRevision, existing.Sha256, SiloDependencyKind.Reopen, true)]
        };
        await silo.PublishAsync(next).ConfigureAwait(false);
        var manifest = silo.PublishRecord(next.GroupId).Manifest.Locator;
        silo.Clock.Advance(TimeSpan.FromMinutes(1));
        var firstLocator = silo.MemberLocator(first.GroupId, existing.ArtifactId);
        var nextLocator = silo.MemberLocator(next.GroupId, next.Members[0].ArtifactId);
        var store = new TestRemoval(silo.Local);
        var retention = Open(silo, store);
        await retention.SetPolicyAsync(new(false, [new("raw", TimeSpan.Zero, true, true)]), token).ConfigureAwait(false);

        silo.Local.ReadUnavailable = key => key == manifest.Key;
        await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        retention = Open(silo, store);
        Assert.IsFalse((await silo.Index.ReadAuthorityAsync(token).ConfigureAwait(false)).CompleteThroughPrefixEnd);
        Assert.AreEqual("held-reference", (await PlanAsync(retention, firstLocator, silo.Clock.GetUtcNow(), token).ConfigureAwait(false)).Decision);
        var raw = await PlanAsync(retention, nextLocator, silo.Clock.GetUtcNow(), token).ConfigureAwait(false);
        Assert.AreEqual("held-reference", raw.Decision);
        Assert.IsNull(raw.References.Single().PublishedUtc);
        Assert.IsNull(raw.References.Single().Role);
        Assert.AreEqual("catalog-authority-incomplete", await SweepAsync(retention, token).ConfigureAwait(false));
        Assert.AreEqual(0, store.Deletes);

        silo.Local.ReadUnavailable = null;
        await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
        retention = Open(silo, store);
        Assert.AreEqual("held-reference", (await PlanAsync(retention, firstLocator, silo.Clock.GetUtcNow(), token).ConfigureAwait(false)).Decision);
        Assert.AreEqual("expire-role", (await PlanAsync(retention, nextLocator, silo.Clock.GetUtcNow(), token).ConfigureAwait(false)).Decision);
    }

    [TestMethod]
    public async Task StalledDeleteKeepsFenceAndLeavesLifecycleAndPublisherAvailable()
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var first = SiloDrafts.Capture(silo.Payloads, 1, rawRequired: false);
        await silo.PublishAsync(first).ConfigureAwait(false);
        var locator = silo.MemberLocator(first.GroupId, first.Members[0].ArtifactId);
        silo.Clock.Advance(TimeSpan.FromMinutes(1));
        using var lifecycle = new SemaphoreSlim(1);
        var atDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new TestRemoval(silo.Local)
        {
            BeforeDelete = ct =>
            {
                atDelete.SetResult();
                return finishDelete.Task.WaitAsync(ct);
            }
        };
        var retention = Open(silo, store);
        silo.Publisher.RetentionFence = retention.ReadUnsettledTarget;
        await retention.SetPolicyAsync(new(false, [new("raw", TimeSpan.Zero, true, true)]), token).ConfigureAwait(false);
        var sweep = retention.SweepAsync(true, NoHolds, null, token, lifecycle: lifecycle).AsTask();
        try
        {
            await atDelete.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            Assert.AreEqual(locator, retention.ReadUnsettledTarget());
            Assert.IsTrue(await lifecycle.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false));
            lifecycle.Release();
            var next = SiloDrafts.Capture(silo.Payloads, 2, rawRequired: false) with { GroupKind = "raw-capture" };
            Assert.AreEqual(SiloStagedState.Visible,
                (await silo.PublishAsync(next).WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false)).State);
            Assert.IsNull(await retention.TryAcquireAsync([locator], TimeSpan.FromMinutes(1), token).ConfigureAwait(false));
        }
        finally
        {
            finishDelete.TrySetResult();
        }
        Assert.AreEqual("deleted", await sweep.ConfigureAwait(false));
        Assert.IsNull(retention.ReadUnsettledTarget());
    }

    [TestMethod]
    public async Task SweepResolvesOnlyUnreferencedOrphansAndReclaimsSpoolPartials()
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        silo.FileSpoolCapacity = (1024 * 1024, 32);
        var visible = SiloDrafts.Capture(silo.Payloads, 1, rawRequired: false);
        await silo.PublishAsync(visible).ConfigureAwait(false);
        await silo.RestartAsync().ConfigureAwait(false);

        var bytes = new byte[] { 1, 6, 9 };
        var sha = SiloHash.Sha256(bytes);
        var key = SiloObjectNamespace.PayloadKey(SiloRetentionClass.Full, sha);
        var put = await silo.Local.PutCreateOnlyAsync(new(key, bytes.Length, sha,
            ct => SiloArtifactStorage.OpenMemoryAsync(bytes, ct), new Dictionary<string, string>()), token).ConfigureAwait(false);
        var referenced = silo.MemberLocator(visible.GroupId, visible.Members[0].ArtifactId);
        using (var connection = new SqliteConnection($"Data Source={silo.StateRoot.PublicationDatabasePath}"))
        {
            await connection.OpenAsync(token).ConfigureAwait(false);
            foreach (var orphan in new[] { (Key: key, Sha: sha), (Key: referenced.Key, Sha: visible.Members[0].Sha256) })
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO orphan_candidates(store_id, bucket, key, version_id, sha256, length, reason, recorded_unix_ms)
                    VALUES ($store, $bucket, $key, '', $sha, 3, 'uncertain-test', 0);
                    """;
                command.Parameters.AddWithValue("$store", referenced.StoreId);
                command.Parameters.AddWithValue("$bucket", referenced.Bucket);
                command.Parameters.AddWithValue("$key", orphan.Key);
                command.Parameters.AddWithValue("$sha", orphan.Sha);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }
        var partials = new[]
        {
            Path.Combine(silo.RootPath, "payloads-v1", $"{SiloFilePayloadSpool.TokenOf(sha)}.{Guid.NewGuid():N}.tmp"),
            Path.Combine(silo.RootPath, "spool", $"{SiloObjectSpool.NameOf(sha)}.{Guid.NewGuid():N}.tmp")
        };
        foreach (var partial in partials) await File.WriteAllBytesAsync(partial, bytes, token).ConfigureAwait(false);
        var retention = OpenForFixture(silo);
        await retention.SetPolicyAsync(new(false, []), token).ConfigureAwait(false);
        Assert.AreEqual("deleted", await SweepAsync(retention, token).ConfigureAwait(false));
        Assert.IsTrue(partials.All(path => !File.Exists(path)));
        Assert.AreEqual(referenced with { VersionId = "" }, (await silo.State.ListOrphansAsync(token).ConfigureAwait(false)).Single().Locator);
        using var orphanRead = await silo.Local.OpenVersionAsync(key, put.VersionId!, token).ConfigureAwait(false);
        Assert.AreEqual(SiloOpenStatus.NoSuchKey, orphanRead.Status);
        using var retainedRead = await silo.Local.OpenVersionAsync(referenced.Key, referenced.VersionId, token).ConfigureAwait(false);
        Assert.AreEqual(SiloOpenStatus.Found, retainedRead.Status);
    }

    [TestMethod]
    public async Task SavedAndLiveViewsPinTheirExactManifestAndReopenMembers()
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await new SqliteRawCaptureJournal(Path.Combine(silo.RootPath, "journal", "raw-ingress.db"), 1)
            .InitializeAsync(token).ConfigureAwait(false);
        using var processing = new SqliteCaptureProcessingStore(Options.Create(new CameraAgentHostOptions { RawIngressRoot = silo.RootPath }));
        await processing.InitializeAsync(token).ConfigureAwait(false);
        var views = new SqliteCenteredViews(processing, silo.Clock, NullLogger<SqliteCenteredViews>.Instance);
        var evidence = SiloDrafts.Evidence(silo.Payloads, "saved-layer");
        await silo.PublishAsync(evidence).ConfigureAwait(false);
        var saved = SiloDrafts.View(silo.Payloads, "saved", null, 1,
            [SiloDrafts.Layer("stars", "Ready", "saved-layer", 4)], [], publishPreview: true);
        var live = SiloDrafts.View(silo.Payloads, "live", null, 1, [], [], publishPreview: true);
        var savedRaw = SiloDrafts.Capture(silo.Payloads, 90);
        var liveRaw = SiloDrafts.Capture(silo.Payloads, 91);
        await silo.PublishAsync(savedRaw with
        {
            GroupKind = "raw-capture",
            GroupId = Guid.Parse(saved.View!.ReferenceCaptureId).ToString("N"),
            Provenance = new([], CaptureCondition: new(null, "Day"))
        }).ConfigureAwait(false);
        await silo.PublishAsync(liveRaw with
        {
            GroupKind = "raw-capture",
            GroupId = Guid.Parse(live.View!.ReferenceCaptureId).ToString("N"),
            Provenance = new([], CaptureCondition: new(null, "Day"))
        }).ConfigureAwait(false);
        await silo.PublishAsync(saved).ConfigureAwait(false);
        await silo.PublishAsync(live).ConfigureAwait(false);
        var typedBase = SqliteCenteredViews.DeserializeDocument<CenteredViewBase>(saved.View!.Base);
        var typedRevision = SqliteCenteredViews.DeserializeDocument<CenteredViewRevision>(saved.View.Revision);
        var registered = await views.RegisterBaseAsync(typedBase.Definition, typedRevision.Layers, token).ConfigureAwait(false);
        Assert.AreEqual(saved.GroupId, registered.Base.BaseId);
        Assert.AreEqual(saved.RevisionId, registered.Revision.RevisionId);
        Assert.IsNotNull(await views.SaveAsync("operator", Guid.NewGuid(), saved.GroupId, saved.RevisionId, [], token).ConfigureAwait(false));
        await silo.AppendAsync(new SiloLiveIssueRecord("camera-1", "stream", live.GroupId, live.RevisionId, 1,
            silo.Clock.GetUtcNow())).ConfigureAwait(false);

        silo.Clock.Advance(TimeSpan.FromDays(1));
        var retention = OpenForFixture(silo);
        var roles = new[] { "full", "display", "metadata" };
        await retention.SetPolicyAsync(new(false,
            roles.Select(static role => new SiloRoleRetention(role, TimeSpan.Zero, false, true)).ToArray(),
            [new("daytime", [new("centered-view", TimeSpan.FromHours(12))], SkyConditions: ["Day"])]), token).ConfigureAwait(false);
        var savedManifest = silo.PublishRecord(saved.GroupId, saved.RevisionId).Manifest.Locator;
        var liveManifest = silo.PublishRecord(live.GroupId, live.RevisionId).Manifest.Locator;
        var required = silo.MemberLocator(live.GroupId, live.Members.First(static member => member.Required).ArtifactId, live.RevisionId);
        var dependency = silo.MemberLocator(evidence.GroupId, evidence.Members[0].ArtifactId);

        async ValueTask<IReadOnlyList<SiloRetentionCandidate>> CandidatesAsync() =>
            await retention.DryRunAsync(silo.Clock.GetUtcNow(), false,
                await processing.ReadSavedSiloViewHoldsAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);
        var held = await CandidatesAsync().ConfigureAwait(false);
        foreach (var locator in new[] { savedManifest, liveManifest, required, dependency })
            Assert.AreEqual("held-reference", held.Single(candidate => candidate.Locator == locator).Decision);

        await silo.AppendAsync(new SiloLiveIssueRecord("camera-1", "stream", saved.GroupId, saved.RevisionId, 2,
            silo.Clock.GetUtcNow())).ConfigureAwait(false);
        var releasedLive = await CandidatesAsync().ConfigureAwait(false);
        Assert.AreEqual("expire-role", releasedLive.Single(candidate => candidate.Locator == liveManifest).Decision);
        await silo.AppendAsync(new SiloLiveIssueRecord("camera-1", "stream", live.GroupId, live.RevisionId, 3,
            silo.Clock.GetUtcNow())).ConfigureAwait(false);
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(silo.RootPath, "journal", "raw-ingress.db")}"))
        {
            await connection.OpenAsync(token).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM processing_centered_view_selections;";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        Assert.AreEqual("expire-role", (await CandidatesAsync().ConfigureAwait(false)).Single(candidate => candidate.Locator == savedManifest).Decision);
        Assert.AreEqual("deleted", await retention.SweepAsync(true, processing.ReadSavedSiloViewHoldsAsync, null, token,
            select: locator => locator == savedManifest).ConfigureAwait(false));
        Assert.AreEqual("expire-role", (await CandidatesAsync().ConfigureAwait(false)).Single(candidate => candidate.Locator == dependency).Decision);
        var expired = await silo.Catalog.ReopenAsync(saved.GroupId, saved.RevisionId, token).ConfigureAwait(false);
        Assert.IsFalse(expired.Reopenable);
        Assert.AreEqual(SiloDisposition.PolicyExpired,
            expired.Blocking.Single(static item => item.Role.Kind == SiloIdentityKind.Manifest).Disposition);
    }

    [TestMethod]
    public async Task SceneHoldFollowsExactRetainedConsumersAcrossLocalAndArchiveExpiry()
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var capture = SiloDrafts.Capture(silo.Payloads, 1, rawRequired: false);
        await silo.PublishAsync(capture).ConfigureAwait(false);
        var raw = capture.Members[0];
        var scene = SiloDrafts.Evidence(silo.Payloads, "scene-retention");
        var sceneMember = scene.Members[0] with { Role = "projected-scene" };
        await silo.PublishAsync(scene with { Members = [sceneMember] }).ConfigureAwait(false);
        var consumer = SiloDrafts.Evidence(silo.Payloads, "scene-consumer");
        var consumerMember = consumer.Members[0] with { Role = "scene-consumer" };
        consumer = consumer with
        {
            Members = [consumerMember],
            Dependencies = [new(raw.ArtifactId, raw.ArtifactRevision, raw.Sha256, SiloDependencyKind.Compute, true)]
        };
        await silo.PublishAsync(consumer).ConfigureAwait(false);
        var consumerIdentity = SiloHarness.Identity(silo.PublishRecord(consumer.GroupId), consumerMember.ArtifactId);
        var consumerLocator = silo.MemberLocator(consumer.GroupId, consumerMember.ArtifactId);
        var copy = silo.ReplicateToArchive(consumerIdentity, consumerLocator);
        await silo.AppendAsync(copy).ConfigureAwait(false);
        var sceneLocator = silo.MemberLocator(scene.GroupId, sceneMember.ArtifactId);
        var rawLocator = silo.MemberLocator(capture.GroupId, raw.ArtifactId);
        var retention = OpenForFixture(silo);
        var roles = new[] { "raw", "projected-scene", "scene-consumer" };
        await retention.SetPolicyAsync(new(false, roles.Select(static role => new SiloRoleRetention(role, TimeSpan.Zero, false, true)).ToArray()),
            token).ConfigureAwait(false);
        silo.Clock.Advance(TimeSpan.FromDays(1));

        ValueTask<IReadOnlyList<SiloRetentionHold>> Holds(CancellationToken ct) =>
            silo.Index.ReadSceneRetentionHoldsAsync([new(sceneMember.ArtifactId, raw.ArtifactId, true)], false, ct);
        async ValueTask<string> DecisionAsync(IReadOnlyList<SiloRetentionHold> holds) =>
            (await retention.DryRunAsync(silo.Clock.GetUtcNow(), false, holds, token).ConfigureAwait(false))
                .Single(candidate => candidate.Locator == sceneLocator).Decision;

        Assert.AreEqual("held-reference", await DecisionAsync(await Holds(token).ConfigureAwait(false)).ConfigureAwait(false));
        Assert.AreEqual("deleted", await retention.SweepAsync(true, Holds, null, token, select: locator => locator == rawLocator).ConfigureAwait(false));
        Assert.AreEqual("deleted", await retention.SweepAsync(true, Holds, null, token, select: locator => locator == consumerLocator).ConfigureAwait(false));
        Assert.AreEqual("held-reference", await DecisionAsync(await Holds(token).ConfigureAwait(false)).ConfigureAwait(false));

        silo.Archive.Delete(copy.Locator);
        await retention.ReconcileArchiveAsync(new ExpiredArchive(copy.Locator), token).ConfigureAwait(false);
        Assert.AreEqual("expire-role", await DecisionAsync(await Holds(token).ConfigureAwait(false)).ConfigureAwait(false));
        var unknown = await silo.Index.ReadSceneRetentionHoldsAsync([new(sceneMember.ArtifactId, raw.ArtifactId, true)], true, token)
            .ConfigureAwait(false);
        Assert.AreEqual("held-reference", await DecisionAsync(unknown).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task DistinctDataRootMeasuresTrashAndAdmitsOnlyExactAllocationEvidence()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Linux block accounting is required.");
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var data = Directory.CreateDirectory(Path.Combine(silo.RootPath, "data")).FullName;
        var trash = Directory.CreateDirectory(Path.Combine(data, ".minio.sys", "tmp", ".trash")).FullName;
        var file = Path.Combine(trash, "part");
        await File.WriteAllBytesAsync(file, new byte[8192], TestContext.CancellationToken).ConfigureAwait(false);
        var exact = new SiloExactLocator(silo.Binding.StoreId, silo.Binding.Bucket, "exact", "version");
        var budget = SiloFilesystemObservation.CreateBudget(silo.RootPath, data, 1, 8192,
            locator => locator == exact ? 4096 : null, new TestCapacity(data));
        var observed = budget.ObserveReclamation();
        Assert.AreEqual(Healthy.TotalBytes / 2, observed!.Value.TotalBytes);
        Assert.AreEqual(Healthy.AvailableBytes, observed.Value.AvailableBytes);
        Assert.AreEqual(Healthy.TotalBytes, observed.Value.StateCapacity!.Value.TotalBytes);
        Assert.IsGreaterThanOrEqualTo(8192L, observed.Value.TrashBytes!.Value);
        Assert.AreEqual(4096L, budget.ReadExactAllocationBytes(exact));
        Assert.IsNull(budget.ReadExactAllocationBytes(exact with { VersionId = "other" }));
        File.Delete(file);
        Assert.AreEqual(0L, budget.ObserveReclamation()?.TrashBytes);
    }

    private sealed class TestCapacity(string data) : IStorageCapacityProvider
    {
        public StorageCapacity GetCapacity(string storageRoot) =>
            new(storageRoot == data ? Healthy.TotalBytes / 2 : Healthy.TotalBytes, Healthy.AvailableBytes);
    }
}
