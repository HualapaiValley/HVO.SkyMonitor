using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;
using HVO.SkyMonitor.CameraAgent.Tests.Silo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static HVO.SkyMonitor.CameraAgent.Tests.Capture.Distribution.DurableCaptureDistributionTests;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Distribution;

public sealed partial class SiloRawEvidenceRecoveryTests
{
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
