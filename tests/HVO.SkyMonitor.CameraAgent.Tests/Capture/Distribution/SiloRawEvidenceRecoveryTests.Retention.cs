using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Tests.Silo;
using HVO.SkyMonitor.CameraAgent.Tests.TimeLapses;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static HVO.SkyMonitor.CameraAgent.Tests.Capture.Distribution.DurableCaptureDistributionTests;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Distribution;

public sealed partial class SiloRawEvidenceRecoveryTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LiveViewPromotionSharesRetentionFenceAndRejectsExpiredManifest(bool deleteAlreadyFenced)
    {
        using var fixture = CreateFixture(new());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var (settings, original, testStore) = await CreateRecoverySiloWithStoreAsync().ConfigureAwait(false);
        using var originalLifetime = original;
        var removal = new SiloRetentionTests.TestRemoval(testStore);
        using var installed = new SiloInstalledStore(settings, removal, _ => ValueTask.FromResult(true));
        var payloads = new SiloTestPayloads();
        using var storage = new SiloInstalledStorage(settings, fixture.Options, fixture.Time, null, null, installed, payloads);
        await storage.VerifyReadinessAsync(token).ConfigureAwait(false);
        await new SqliteRawCaptureJournal(Path.Combine(fixture.Root, "journal", "raw-ingress.db"), 1)
            .InitializeAsync(token).ConfigureAwait(false);
        using var processing = new SqliteCaptureProcessingStore(fixture.Options);
        var views = new SqliteCenteredViews(processing, fixture.Time, NullLogger<SqliteCenteredViews>.Instance, silo: storage);
        var stack = SiloTypedViews.Stack("live-retention");
        var now = fixture.Time.GetUtcNow();
        await storage.Publisher.StageAsync(new("product", "live-view-evidence", "r1", now, now, now,
            [.. stack.Products.Select(product => product.Member(payloads))], [], new([])), token).ConfigureAwait(false);
        Assert.AreEqual(SiloStagedState.Visible,
            (await storage.Publisher.PublishAsync("live-view-evidence", "r1", token).ConfigureAwait(false)).State);
        var view = await views.RegisterBaseAsync(stack.Definition, [], token).ConfigureAwait(false);
        Assert.IsNull((await views.PublishSiloChangesAsync(16, token).ConfigureAwait(false)).Status);

        var group = view.Base.BaseId;
        var revision = view.Revision.RevisionId;
        var stream = stack.Definition.Stream;
        var publication = await storage.Index.ReadPublicationAsync(group, revision, token).ConfigureAwait(false);
        Assert.IsNotNull(publication);
        Assert.AreEqual("visible", publication.State);
        var manifestRole = new SiloRoleKey(SiloIdentityKind.Manifest, group, revision);
        var manifest = (await storage.Index.ReadRoleAsync(manifestRole, token).ConfigureAwait(false))!.Locators.Single().Locator;
        await storage.Retention.SetPolicyAsync(new(false, [new("manifest", TimeSpan.Zero, false, true)]), token).ConfigureAwait(false);
        Task<string> SweepAsync() => storage.Retention.SweepAsync(true, processing.ReadSavedSiloViewHoldsAsync, null, token,
            select: locator => locator == manifest, lifecycle: storage.RawLifecycle).AsTask();

        if (!deleteAlreadyFenced)
        {
            Assert.IsNotNull(await views.SelectLiveAsync(stream, group, token).ConfigureAwait(false));
            Assert.IsNull(await views.ReadCurrentAsync(stream, token).ConfigureAwait(false));
            Assert.AreEqual("no-eligible-versions", await SweepAsync().ConfigureAwait(false));
            Assert.AreEqual(0, removal.Deletes);
            Assert.IsNull((await views.PublishSiloChangesAsync(16, token).ConfigureAwait(false)).Status);
            Assert.AreEqual(revision, (await views.ReadCurrentAsync(stream, token).ConfigureAwait(false))?.View.Revision.RevisionId);
            return;
        }

        var atDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        removal.BeforeDelete = ct =>
        {
            atDelete.SetResult();
            return release.Task.WaitAsync(ct);
        };
        var sweep = SweepAsync();
        CenteredViewCurrent? localLive;
        try
        {
            await atDelete.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            Assert.AreEqual(manifest, storage.Retention.ReadUnsettledTarget());
            localLive = await views.SelectLiveAsync(stream, group, token).ConfigureAwait(false);
            Assert.IsNotNull(localLive);
            Assert.AreEqual("silo-centered-view-unpublishable",
                (await views.PublishSiloChangesAsync(16, token).ConfigureAwait(false)).Status);
            Assert.IsNull(await views.ReadCurrentAsync(stream, token).ConfigureAwait(false));
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.AreEqual("deleted", await sweep.ConfigureAwait(false));
        Assert.AreEqual("policy-expired", (await storage.Index.ReadRoleAsync(manifestRole, token).ConfigureAwait(false))!.Journaled);
        Assert.AreEqual("visible", (await storage.Index.ReadPublicationAsync(group, revision, token).ConfigureAwait(false))!.State);
        var issue = new SiloLiveIssueRecord(publication.CameraId!, publication.StreamId!, group, revision,
            localLive.Interval.StartedOrder, now);
        Assert.IsFalse(await storage.Publisher.PublishLiveViewAsync(issue, token).ConfigureAwait(false));
        Assert.AreEqual("silo-centered-view-unpublishable",
            (await views.PublishSiloChangesAsync(16, token).ConfigureAwait(false)).Status);
        Assert.IsNull(await views.ReadRegisteredBaseAsync(stream, stack.Definition.Reference.Capture.CaptureId,
            stack.Definition.Kind, token).ConfigureAwait(false));

        // Replay a stale live record like one accepted before retention fencing covered live promotion.
        await storage.ArchiveJournal.AppendAsync([issue], "fixture-stale-live-issue", token).ConfigureAwait(false);
        await storage.Publisher.ResumeAsync(16, token).ConfigureAwait(false);
        Assert.IsNotNull(await storage.Index.ReadCurrentLiveAsync(issue.CameraId, token).ConfigureAwait(false));
        Assert.IsNull(await views.ReadCurrentAsync(stream, token).ConfigureAwait(false));
        Assert.IsNull(await storage.Index.ReadLastVisibleLiveRevisionAsync(group, token).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TimeLapseFreezeExcludesFencedOrLocallyEvictedRaw(bool archiveEviction)
    {
        using var fixture = CreateFixture(new());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var (settings, original, testStore) = await CreateRecoverySiloWithStoreAsync().ConfigureAwait(false);
        using var originalLifetime = original;
        var removal = new SiloRetentionTests.TestRemoval(testStore);
        using var installed = new SiloInstalledStore(settings, removal, _ => ValueTask.FromResult(true));
        using var storage = new SiloInstalledStorage(settings, fixture.Options, fixture.Time, null, null, installed, null);
        var artifacts = new SiloArtifactStorage(storage, fixture.Time);
        var state = new RawIngressState(fixture.Time);
        using var telemetry = new RawIngressTelemetry(state);
        using var ingress = new RawCaptureIngress(fixture.Options, new FixedCapacityProvider(), state, fixture.Time,
            telemetry, NullLogger<RawCaptureIngress>.Instance, new NullRawIngressFaultInjector(), fixture.Policy,
            laneState: fixture.LaneState, silo: storage, siloSettings: settings, siloArtifacts: artifacts);
        await storage.VerifyReadinessAsync(token).ConfigureAwait(false);

        var configuration = fixture.Configuration with { Observatory = NightlyProductFixture.Observatory };
        var options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = fixture.Root,
            TimeLapses = new() { Enabled = true, RigProfileSha256 = RigProjectionContextFactory.CreateProfileHashSha256(configuration.Rig) }
        });
        var occurrence = SqliteTimeLapseStoreTests.Occurrence(options.Value.TimeLapses, LocalAutomationSourceWindowKind.CompletedCivilHour);
        var window = occurrence.SourceWindow!;
        fixture.Time.Advance(window.StartUtc.AddSeconds(70) - fixture.Time.GetUtcNow());
        var first = await ingress.AcceptAsync(configuration, fixture.CreateSubmission(0), token).ConfigureAwait(false);
        var second = await ingress.AcceptAsync(configuration, fixture.CreateSubmission(1), token).ConfigureAwait(false);
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.IsNull((await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false)).Status);
        var descriptor = first.Manifest.Descriptor;
        var id = descriptor.Artifact.ArtifactId;
        var revision = CaptureContractJson.ComputeDescriptorSha256(descriptor);
        var roleKey = new SiloRoleKey(SiloIdentityKind.Member, id.ToString("D"), revision);
        var role = (await storage.Index.ReadRoleAsync(roleKey, token).ConfigureAwait(false))!;
        var local = role.Locators.Single().Locator;
        Assert.IsTrue(await artifacts.HasRawEvidenceAsync(descriptor, token).ConfigureAwait(false));

        fixture.Time.Advance(window.EarliestFinalUtc.AddMinutes(1) - fixture.Time.GetUtcNow());
        var now = fixture.Time.GetUtcNow();
        await storage.Retention.SetPolicyAsync(new(false, [new("raw", TimeSpan.Zero, false, !archiveEviction)]), token).ConfigureAwait(false);
        using var processing = new SqliteCaptureProcessingStore(fixture.Options);
        using var videos = new SqliteTimeLapseStore(options, fixture.Time);
        using var worker = await videos.AcquireWorkerAsync(token).ConfigureAwait(false);
        await videos.EnqueueAsync(occurrence, options.Value.TimeLapses.Freeze(configuration), [], token).ConfigureAwait(false);
        var job = await videos.ClaimAsync(token).ConfigureAwait(false);
        Assert.IsNotNull(job);
        var reader = new TimeLapseSourceReader(options, ingress, processing, videos, static () => null, new AstronomyEnginePlanetEphemeris(), artifacts);

        Task<string> SweepAsync(ISiloRetentionArchive? custody = null) => storage.Retention.SweepAsync(true,
            static _ => ValueTask.FromResult<IReadOnlyList<SiloRetentionHold>>([]), custody, token,
            select: locator => locator == local, lifecycle: storage.RawLifecycle).AsTask();

        async Task AssertFrozenPlanAsync()
        {
            var plan = await reader.FreezeAsync(job, token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            var retainedId = second.Manifest.Descriptor.Artifact.ArtifactId;
            Assert.AreEqual(retainedId, plan.Sources.Single().Descriptor.Artifact.ArtifactId);
            Assert.AreEqual(1, plan.Exclusions["timelapse.source-unavailable"]);
            var frozen = await videos.GetPlanAsync(job.JobId, token).ConfigureAwait(false);
            Assert.IsNotNull(frozen);
            Assert.AreEqual(retainedId, frozen.Sources.Single().Descriptor.Artifact.ArtifactId);
            Assert.AreEqual(1, frozen.Exclusions["timelapse.source-unavailable"]);
            Assert.IsTrue(await storage.Index.HasVisibleMemberAsync(roleKey.Id, roleKey.Revision, token).ConfigureAwait(false));
            Assert.IsFalse(await artifacts.HasMemberAsync(id, revision, token).ConfigureAwait(false));
            Assert.IsFalse(await artifacts.HasRawEvidenceAsync(descriptor, token).ConfigureAwait(false));
        }

        if (archiveEviction)
        {
            var archive = new SiloTestStore("fixture-archive", "archive-bucket", SiloStoreClass.Archive);
            var bytes = (await artifacts.ReadRawAsync(descriptor, token).ConfigureAwait(false))!;
            var put = await archive.PutCreateOnlyAsync(new(local.Key, role.Length, role.Sha256,
                ct => SiloArtifactStorage.OpenMemoryAsync(bytes, ct), new Dictionary<string, string>()), token).ConfigureAwait(false);
            var survivor = new SiloExactLocator(archive.Descriptor.StoreId, archive.Descriptor.Bucket, local.Key, put.VersionId!);
            var identity = new SiloObjectIdentity(roleKey.Kind, roleKey.Id, roleKey.Revision, role.Sha256, role.Length);
            await storage.ArchiveJournal.AppendAsync([new SiloCopyRecord(identity, survivor, SiloStoreClass.Archive,
                SiloRetrieval.Online, "archive-owner", now, SiloFormats.ExactGetSha256)], "fixture-copy", token).ConfigureAwait(false);
            Assert.AreEqual("deleted", await SweepAsync(new StalledArchive(static _ => Task.CompletedTask)).ConfigureAwait(false));
            var retained = (await storage.Index.ReadRoleAsync(roleKey, token).ConfigureAwait(false))!;
            Assert.IsNull(retained.Journaled);
            Assert.AreEqual("evicted", retained.Locators.Single(copy => copy.Locator == local).CopyState);
            Assert.AreEqual("active", retained.Locators.Single(copy => copy.Locator == survivor).CopyState);
            await AssertFrozenPlanAsync().ConfigureAwait(false);
            return;
        }

        var atDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        removal.BeforeDelete = ct =>
        {
            atDelete.SetResult();
            return release.Task.WaitAsync(ct);
        };
        var sweep = SweepAsync();
        try
        {
            await atDelete.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            Assert.AreEqual(local, storage.Retention.ReadUnsettledTarget());
            await AssertFrozenPlanAsync().ConfigureAwait(false);
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.AreEqual("deleted", await sweep.ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SiloRetentionIoDoesNotBlockRawAdmissionOrPause(bool stallArchive)
    {
        using var fixture = CreateFixture(new());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var (settings, original, testStore) = await CreateRecoverySiloWithStoreAsync().ConfigureAwait(false);
        using var originalLifetime = original;
        var removal = new SiloRetentionTests.TestRemoval(testStore);
        using var installed = new SiloInstalledStore(settings, removal, _ => ValueTask.FromResult(true));
        using var storage = new SiloInstalledStorage(settings, fixture.Options, fixture.Time, null, null, installed, null);
        var artifacts = new SiloArtifactStorage(storage, fixture.Time);
        var state = new RawIngressState(fixture.Time);
        using var telemetry = new RawIngressTelemetry(state);
        using var ingress = new RawCaptureIngress(fixture.Options, new FixedCapacityProvider(), state, fixture.Time,
            telemetry, NullLogger<RawCaptureIngress>.Instance, new NullRawIngressFaultInjector(), fixture.Policy,
            laneState: fixture.LaneState, silo: storage, siloSettings: settings, siloArtifacts: artifacts);
        var maintenance = new CaptureMaintenanceGate();
        using var controls = new CaptureControlTelemetry();
        using var coordinator = new CaptureAdmissionCoordinator(ingress, fixture.Options, fixture.Time, controls,
            maintenanceGate: maintenance);
        await storage.VerifyReadinessAsync(token).ConfigureAwait(false);
        await coordinator.InitializeAsync(token).ConfigureAwait(false);
        Assert.IsTrue(ingress.SiloAdmissionsReady);

        var bytes = new byte[] { 12, 32, 64 };
        var now = fixture.Time.GetUtcNow();
        var member = SiloArtifactStorage.Member(Guid.NewGuid(), "r1", "test-raw", SiloRetentionClass.Full,
            "application/octet-stream", bytes.Length, SiloHash.Sha256(bytes));
        await artifacts.PublishAsync(new("retention-test", "old", "r1", now, now, now, [member], [], new([])),
            (_, ct) => SiloArtifactStorage.OpenMemoryAsync(bytes, ct), token).ConfigureAwait(false);
        var plan = new SiloRetentionPolicy(false, [new("test-raw", TimeSpan.Zero, true, true)]);
        await storage.Retention.SetPolicyAsync(plan, token).ConfigureAwait(false);
        var first = (await storage.Retention.DryRunAsync(now, false, [], token).ConfigureAwait(false))
            .Single(item => item.References.Any(reference => reference.Identity.Id == member.ArtifactId));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Stall(CancellationToken ct)
        {
            entered.SetResult();
            return release.Task.WaitAsync(ct);
        }

        ISiloRetentionArchive? archive = null;
        if (stallArchive)
        {
            var identity = first.References.Single().Identity;
            var copy = first.Locator with { StoreId = "fixture-archive", Bucket = "archive-bucket" };
            await storage.ArchiveJournal.AppendAsync([new SiloCopyRecord(identity, copy, SiloStoreClass.Archive,
                SiloRetrieval.Online, "archive-owner", now, SiloFormats.ExactGetSha256)], "fixture-copy", token).ConfigureAwait(false);
            archive = new StalledArchive(Stall);
        }
        else
        {
            removal.BeforeDelete = Stall;
        }

        var sweep = storage.Retention.SweepAsync(true, static _ => ValueTask.FromResult<IReadOnlyList<SiloRetentionHold>>([]),
            archive, token, lifecycle: storage.RawLifecycle, enterMaintenance: maintenance.TryEnterIfInitialized).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            using (var capture = await coordinator.EnterAsync(token).ConfigureAwait(false))
            {
                Assert.IsNotNull(await ingress.AcceptAsync(fixture.Configuration, fixture.CreateSubmission(0), token)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false));
                capture.MarkPublished();
            }
            var paused = await coordinator.PauseAsync("retention-io-pause", null, "test-owner", null, token)
                .WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            Assert.AreEqual(CaptureAdmissionState.Paused, paused.State);
            Assert.IsFalse(sweep.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.AreEqual(stallArchive ? "no-eligible-versions" : "deleted", await sweep.ConfigureAwait(false));
        Assert.AreEqual(stallArchive ? 0 : 1, removal.Deletes);
    }

    [TestMethod]
    public async Task HostedRetentionClearsExpiredCacheBeforeApplyingHeldExpiryPolicy()
    {
        using var fixture = CreateFixture(new());
        var token = CancellationToken.None;
        var data = Directory.CreateDirectory(Path.Combine(fixture.Root, "silo-data")).FullName;
        var trash = Directory.CreateDirectory(Path.Combine(data, ".minio.sys", "tmp", ".trash")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(trash, "silo-trash"), new byte[8192], token).ConfigureAwait(false);
        var (settings, original, testStore) = await CreateRecoverySiloWithStoreAsync(data).ConfigureAwait(false);
        using var originalLifetime = original;
        var exact = new SiloExactLocator(settings.Binding.StoreId, settings.Binding.Bucket, "one-key", "one-version");
        using var installed = new SiloInstalledStore(settings, new SiloRetentionTests.TestRemoval(testStore), _ => ValueTask.FromResult(true),
            locator => locator == exact ? 4096 : null);
        using var storage = new SiloInstalledStorage(settings, fixture.Options, fixture.Time, null, null, installed, null);
        var headroom = storage.DiskBudget.ObserveReclamation();
        Assert.IsNotNull(headroom?.StateCapacity);
        Assert.IsGreaterThanOrEqualTo(8192L, headroom!.Value.TrashBytes!.Value);
        Assert.AreEqual(4096L, storage.DiskBudget.ReadExactAllocationBytes(exact));
        Assert.IsNull(storage.DiskBudget.ReadExactAllocationBytes(exact with { VersionId = "unknown-version" }));
        var artifacts = new SiloArtifactStorage(storage, fixture.Time);
        var state = new RawIngressState(fixture.Time);
        using var telemetry = new RawIngressTelemetry(state);
        using var ingress = new RawCaptureIngress(fixture.Options, new FixedCapacityProvider(), state, fixture.Time,
            telemetry, NullLogger<RawCaptureIngress>.Instance, new NullRawIngressFaultInjector(), fixture.Policy,
            laneState: fixture.LaneState, silo: storage, siloSettings: settings, siloArtifacts: artifacts);
        await storage.VerifyReadinessAsync(token).ConfigureAwait(false);
        await ingress.InitializeAsync(token).ConfigureAwait(false);
        var directory = Directory.CreateDirectory(storage.PhysicalState.Resolve("restore-cache"));
        var expired = Path.Combine(directory.FullName, "expired");
        var fresh = Path.Combine(directory.FullName, "fresh");
        await File.WriteAllTextAsync(expired, "expired", token).ConfigureAwait(false);
        await File.WriteAllTextAsync(fresh, "fresh", token).ConfigureAwait(false);
        File.SetLastWriteTimeUtc(expired, fixture.Time.GetUtcNow().AddHours(-2).UtcDateTime);
        File.SetLastWriteTimeUtc(fresh, fixture.Time.GetUtcNow().UtcDateTime);
        using var services = new ServiceCollection().BuildServiceProvider();
        using var archive = new SiloArchiveWorker(new ConfigurationManager(), services, fixture.Time, NullLogger<SiloArchiveWorker>.Instance);
        using var worker = new SiloRetentionWorker(storage, ingress, fixture.Options, new CaptureMaintenanceGate(),
            services, archive, fixture.Time, NullLogger<SiloRetentionWorker>.Instance);
        Assert.AreEqual("held", await worker.RunAsync(token).ConfigureAwait(false));
        Assert.IsFalse(File.Exists(expired));
        Assert.IsTrue(File.Exists(fresh));
    }

    private sealed class StalledArchive(Func<CancellationToken, Task> stall) : ISiloRetentionArchive
    {
        public async ValueTask<bool> ProtectAsync(SiloObjectIdentity identity, SiloExactLocator survivor, CancellationToken cancellationToken)
        {
            await stall(cancellationToken).ConfigureAwait(false);
            return true;
        }

        public ValueTask<bool> ConfirmProtectedAsync(SiloObjectIdentity identity, SiloExactLocator survivor, CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
    }
}
