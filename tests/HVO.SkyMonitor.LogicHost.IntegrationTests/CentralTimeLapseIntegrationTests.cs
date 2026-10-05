using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services.TimeLapses;
using HVO.SkyMonitor.TestSupport;
using HVO.SkyMonitor.Video.FFmpeg;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class CentralTimeLapseIntegrationTests
{
    [TestMethod]
    public async Task ConcurrentClaimsAndExpiredLeaseCannotCompleteAnotherAttempt()
    {
        await using var firstScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        await using var secondScope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var clock = new VideoClock(DateTimeOffset.UtcNow);
        var first = new CentralTimeLapseStore(firstScope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), clock);
        var second = new CentralTimeLapseStore(secondScope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), clock);
        var request = CreateRequest();
        var id = await first.EnqueueAsync(_ => Task.FromResult(request), CancellationToken.None);
        Assert.AreEqual(id, await second.EnqueueAsync(_ => Task.FromResult(request), CancellationToken.None));
        var claims = await Task.WhenAll(first.ClaimAsync(CancellationToken.None), second.ClaimAsync(CancellationToken.None));
        Assert.HasCount(1, claims.OfType<CentralTimeLapseLease>().ToArray());
        var stale = claims.OfType<CentralTimeLapseLease>().Single();
        clock.Advance(CentralTimeLapseStore.LeaseDuration + TimeSpan.FromSeconds(1));
        Assert.IsFalse(await first.RenewAsync(stale, CancellationToken.None));
        var current = await second.ClaimAsync(CancellationToken.None);
        Assert.IsNotNull(current);
        Assert.AreEqual(stale.JobId, current.JobId);
        Assert.AreNotEqual(stale.Token, current.Token);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first.CompleteAsync(stale,
            CentralTimeLapseState.NoSources, "test.stale", null, CancellationToken.None));
        await second.CompleteAsync(current, CentralTimeLapseState.NoSources, "timelapse.no-admitted-sources", null, CancellationToken.None);
        Assert.IsNull(await first.ClaimAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task ImmutableRequestIsProtectedAgainstDirectSqlMutation()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var store = new CentralTimeLapseStore(db, TimeProvider.System);
        var request = CreateRequest();
        var id = await store.EnqueueAsync(_ => Task.FromResult(request), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<SqlException>(async () => await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [CentralTimeLapseJobs] SET [RequestJson]=N'{{}}' WHERE [Id]={id}"));
        await Assert.ThrowsExactlyAsync<SqlException>(async () => await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM [CentralTimeLapseJobs] WHERE [Id]={id}"));
        var lease = await store.ClaimAsync(CancellationToken.None);
        Assert.IsNotNull(lease);
        Assert.AreEqual(request.Identity, lease.Request.Identity);
        await store.CompleteAsync(lease, CentralTimeLapseState.NoSources, "timelapse.no-admitted-sources", null, CancellationToken.None);
    }

    [TestMethod]
    public async Task DailyRequestWaitsForEveryFrozenHourlyDependency()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var store = new CentralTimeLapseStore(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), TimeProvider.System);
        var template = CreateRequest();
        var hours = SunriseReportingCalendar.PartitionCivilHours(template.Period);
        var ids = new List<Guid>();
        foreach (var hour in hours)
        {
            var request = template with { StartUtc = hour.StartUtc, EndUtc = hour.EndUtc, EligibleUtc = hour.EndUtc.AddMinutes(10) };
            ids.Add(await store.EnqueueAsync(_ => Task.FromResult(request), CancellationToken.None));
        }
        var daily = template with
        {
            StartUtc = template.Period.StartUtc,
            EndUtc = template.Period.EndUtc,
            EligibleUtc = template.Period.EndUtc.AddMinutes(10),
            IsDaily = true,
            HourlyJobIds = ids
        };
        await store.EnqueueAsync(_ => Task.FromResult(daily), CancellationToken.None);
        foreach (var unused in ids)
        {
            var lease = await store.ClaimAsync(CancellationToken.None);
            Assert.IsNotNull(lease);
            Assert.IsFalse(lease.Request.IsDaily);
            await store.CompleteAsync(lease, CentralTimeLapseState.NoSources, "timelapse.no-admitted-sources", null, CancellationToken.None);
        }
        var final = await store.ClaimAsync(CancellationToken.None);
        Assert.IsNotNull(final);
        Assert.IsTrue(final.Request.IsDaily);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.CompleteAsync(final,
            CentralTimeLapseState.Produced, null, null, CancellationToken.None));
        await store.CompleteAsync(final, CentralTimeLapseState.NoSources, "timelapse.no-admitted-sources", null, CancellationToken.None);
    }

    [TestMethod]
    public async Task ReferenceCompletionCreatesSuccessorButRawRetentionDoesNot()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var store = services.GetRequiredService<CentralTimeLapseStore>();
        var sources = services.GetRequiredService<CentralTimeLapseSources>();
        var template = CreateRequest();
        var owner = await db.Users.SingleAsync(user => user.Email == TestUsers.Operator.Email);
        db.Observatories.Add(new() { Id = template.ObservatoryId, OwnerUserId = owner.Id, Name = "Reference completion", CreatedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var sourceId = await CentralTimeLapseNativeTests.SeedSourceAsync(services, template, 0);
        var selected = db.CentralArtifacts.Where(item => item.Id == sourceId);
        await selected.ExecuteUpdateAsync(update => update.SetProperty(item => item.ReconstructionState, CentralReconstructionState.PendingReference));
        Task<CentralTimeLapseRequest> Freeze(CancellationToken token) => sources.FreezeAsync(template.DevicePublicId, template.ObservatoryId,
            template.Period, template.StartUtc, template.EndUtc, TimeSpan.FromMinutes(10), template.Preset, token);
        Task<string> Discover() => sources.DiscoveryIdentityAsync(template.DevicePublicId, template.ObservatoryId, template.Period,
            template.StartUtc, template.EndUtc, TimeSpan.FromMinutes(10), template.Preset, CancellationToken.None);
        var pendingId = await store.EnqueueAsync(Freeze, CancellationToken.None);
        var pendingIdentity = await Discover();
        Assert.AreEqual(pendingId, await store.FindDiscoveredAsync(template.DevicePublicId, template.Period.ReportDate, pendingIdentity, CancellationToken.None));
        var pending = await store.ClaimAsync(CancellationToken.None);
        Assert.IsNotNull(pending);
        Assert.AreEqual(pendingId, pending.JobId);
        Assert.HasCount(0, pending.Request.Sources);
        await store.CompleteAsync(pending, CentralTimeLapseState.NoSources, "timelapse.no-admitted-sources", null, CancellationToken.None);

        // The raw object and receipt stay unchanged when a missing rig reference arrives later.
        await selected.ExecuteUpdateAsync(update => update.SetProperty(item => item.ReconstructionState, CentralReconstructionState.Complete));
        var completedIdentity = await Discover();
        Assert.AreNotEqual(pendingIdentity, completedIdentity);
        Assert.IsNull(await store.FindDiscoveredAsync(template.DevicePublicId, template.Period.ReportDate, completedIdentity, CancellationToken.None));
        var successorId = await store.EnqueueAsync(Freeze, CancellationToken.None);
        Assert.AreNotEqual(pendingId, successorId);
        var successor = await store.ClaimAsync(CancellationToken.None);
        Assert.IsNotNull(successor);
        Assert.AreEqual(successorId, successor.JobId);
        Assert.HasCount(1, successor.Request.Sources);
        Assert.AreEqual(sourceId, successor.Request.Sources[0].CentralArtifactId);
        await store.CompleteAsync(successor, CentralTimeLapseState.Failed, "test.finished", null, CancellationToken.None);

        // Routine retention changes object availability, not reconstruction completion.
        await selected.ExecuteUpdateAsync(update => update.SetProperty(item => item.ObjectState, CentralArtifactObjectState.Expired));
        Assert.AreEqual(completedIdentity, await Discover());
        Assert.AreEqual(successorId, await store.FindDiscoveredAsync(template.DevicePublicId, template.Period.ReportDate, completedIdentity, CancellationToken.None));
    }

    internal static CentralTimeLapseRequest CreateRequest()
    {
        var site = DeploymentLocationSnapshot.Create("video-test", 1, "test", null, DateTimeOffset.UnixEpoch, null, 35, -114, 900, "America/Phoenix");
        var period = new SunriseReportingCalendar(site).Resolve(new DateOnly(2026, 6, 1)).Period!;
        var rig = new CameraRigConfig(new("Virtual test", 512, 512, 2, SensorColorMode.Mono, CameraPixelFormat.Mono16),
            new("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye, 256, 256, 250, CalibrationVersion: "test"),
            new(90, 0, 0), new(TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(.1), TimeSpan.FromSeconds(20), 0, 0));
        var hour = SunriseReportingCalendar.PartitionCivilHours(period)[1];
        return new(Guid.NewGuid(), Guid.NewGuid(), period, hour.StartUtc, hour.EndUtc, hour.EndUtc.AddMinutes(10), false,
            new(rig, new(), new(), 512, TimeLapseEncoderProfile.Software, new string('A', 64)), [], new Dictionary<string, int>(), []);
    }

    private sealed class VideoClock(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
        internal void Advance(TimeSpan elapsed) => utc += elapsed;
    }
}
