using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.TimeLapses;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Focused MSTest methods.")]
public sealed class SqliteTimeLapseStoreTests
{
    [TestMethod]
    public async Task Restart_RetainsFrozenOccurrenceAndPlanAndRecoversOnlyWorkingJobs()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-store");
        try
        {
            var options = Options(root);
            var clock = new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10));
            var request = Occurrence(options.Value.TimeLapses, LocalAutomationSourceWindowKind.CompletedCivilHour);
            Guid jobId;
            using (var store = new SqliteTimeLapseStore(options, clock))
            {
                using var lease = await store.AcquireWorkerAsync(CancellationToken.None);
                await store.EnqueueAsync(request, options.Value.TimeLapses.Freeze(NightlyProductFixture.Configuration()), [], CancellationToken.None);
                await store.EnqueueAsync(request, options.Value.TimeLapses.Freeze(NightlyProductFixture.Configuration()), [], CancellationToken.None);
                var job = await store.ClaimAsync(CancellationToken.None);
                Assert.IsNotNull(job);
                jobId = job.JobId;
                await store.FreezePlanAsync(jobId, new([], new Dictionary<string, int> { ["timelapse.solar-excluded"] = 2 }), CancellationToken.None);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                    await store.FreezePlanAsync(jobId, new([], new Dictionary<string, int>()), CancellationToken.None));
                Assert.HasCount(1, (await store.GetDayAsync(NightlyProductFixture.ObservingDate, CancellationToken.None)).Jobs);
            }
            using var reopened = new SqliteTimeLapseStore(options, clock);
            using var recoveredLease = await reopened.AcquireWorkerAsync(CancellationToken.None);
            var recovered = await reopened.ClaimAsync(CancellationToken.None);
            Assert.IsNotNull(recovered);
            Assert.AreEqual(jobId, recovered.JobId);
            Assert.AreEqual(request, recovered.Occurrence);
            Assert.AreEqual(2, (await reopened.GetPlanAsync(jobId, CancellationToken.None))!.Exclusions["timelapse.solar-excluded"]);
            await reopened.CompleteAsync(jobId, CameraAgentTimeLapseState.NoSources, "timelapse.no-admitted-sources", null, CancellationToken.None);
            Assert.IsNull(await reopened.ClaimAsync(CancellationToken.None));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await reopened.CompleteAsync(jobId, CameraAgentTimeLapseState.Failed, "changed", null, CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Daily_RequiresExactPartitionAndWaitsForEveryChildBeforeAssembly()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-day");
        try
        {
            var options = Options(root);
            var clock = new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10));
            using var store = new SqliteTimeLapseStore(options, clock);
            using var lease = await store.AcquireWorkerAsync(CancellationToken.None);
            var request = Occurrence(options.Value.TimeLapses, LocalAutomationSourceWindowKind.SunriseDay);
            var planner = new LocalAutomationWindowPlanner(new FixedObservingDayCalendarProvider(NightlyProductFixture.Calendar));
            var hours = planner.ResolveWindows(request.SourceWindow!.ReportingPeriod,
                request.SourceWindow.Policy with { Kind = LocalAutomationSourceWindowKind.CompletedCivilHour });
            var preset = options.Value.TimeLapses.Freeze(NightlyProductFixture.Configuration());
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await store.EnqueueAsync(request, preset, hours.Skip(1).ToArray(), CancellationToken.None));
            await store.EnqueueAsync(request, preset, hours, CancellationToken.None);
            Guid? parent = null;
            for (var i = 0; i < hours.Count; i++)
            {
                var child = await store.ClaimAsync(CancellationToken.None);
                Assert.IsNotNull(child);
                Assert.IsNotNull(child.ParentJobId);
                parent ??= child.ParentJobId;
                Assert.AreEqual(parent, child.ParentJobId);
                Assert.AreEqual(request, child.Occurrence);
                Assert.AreEqual(LocalAutomationSourceWindowKind.CompletedCivilHour, child.Window.Policy.Kind);
                await store.CompleteAsync(child.JobId, CameraAgentTimeLapseState.NoSources, "empty", null, CancellationToken.None);
            }
            var daily = await store.ClaimAsync(CancellationToken.None);
            Assert.IsNotNull(daily);
            Assert.AreEqual(parent, daily.JobId);
            Assert.IsNull(daily.ParentJobId);
            Assert.AreEqual(request.SourceWindow, daily.Window);
            var hourly = Occurrence(options.Value.TimeLapses, LocalAutomationSourceWindowKind.CompletedCivilHour);
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
                await store.EnqueueAsync(hourly, preset, [hourly.SourceWindow!], CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Retry_IsAuditedIdempotentAndRevisionBoundWithoutChangingFrozenInputs()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-retry");
        try
        {
            var options = Options(root);
            using var store = new SqliteTimeLapseStore(options, new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10)));
            using var lease = await store.AcquireWorkerAsync(CancellationToken.None);
            var occurrence = Occurrence(options.Value.TimeLapses, LocalAutomationSourceWindowKind.CompletedCivilHour);
            await store.EnqueueAsync(occurrence, options.Value.TimeLapses.Freeze(NightlyProductFixture.Configuration()), [], CancellationToken.None);
            var job = await store.ClaimAsync(CancellationToken.None);
            Assert.IsNotNull(job);
            await store.FreezePlanAsync(job.JobId, new([], new Dictionary<string, int> { ["excluded"] = 3 }), CancellationToken.None);
            await store.CompleteAsync(job.JobId, CameraAgentTimeLapseState.Unavailable, "encoder-missing", null, CancellationToken.None);
            var failed = (await store.GetDayAsync(NightlyProductFixture.ObservingDate, CancellationToken.None)).Jobs.Single();
            Assert.IsGreaterThan(job.Revision, failed.Revision);
            var requestId = Guid.NewGuid();
            Assert.IsFalse(await store.RetryAsync(job.JobId, job.Revision, requestId, "owner", "Retry", CancellationToken.None));
            Assert.IsTrue(await store.RetryAsync(job.JobId, failed.Revision, requestId, "owner", "Retry", CancellationToken.None));
            Assert.IsTrue(await store.RetryAsync(job.JobId, failed.Revision, requestId, "owner", "Retry", CancellationToken.None));
            Assert.IsFalse(await store.RetryAsync(job.JobId, failed.Revision, requestId, "different-owner", "Retry", CancellationToken.None));
            Assert.IsFalse(await store.RetryAsync(job.JobId, failed.Revision, Guid.NewGuid(), "owner", "Retry", CancellationToken.None));
            var retry = await store.ClaimAsync(CancellationToken.None);
            Assert.IsNotNull(retry);
            Assert.AreEqual(occurrence, retry.Occurrence);
            Assert.AreEqual(3, (await store.GetPlanAsync(job.JobId, CancellationToken.None))!.Exclusions["excluded"]);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task RestartRemovesOnlyRecordedUnpublishedFiles()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-publication-recovery");
        try
        {
            var options = Options(root);
            using var store = new SqliteTimeLapseStore(options, new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10)));
            await store.InitializeAsync(CancellationToken.None);
            var id = Guid.NewGuid().ToString("N");
            var directory = Path.Combine(root, ".time-lapses", "products");
            Directory.CreateDirectory(directory);
            var interrupted = Path.Combine(directory, id + ".mp4");
            var temporary = Path.Combine(directory, id + ".json." + Guid.NewGuid().ToString("N") + ".tmp");
            var unrelated = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".mp4");
            await File.WriteAllTextAsync(interrupted, "interrupted");
            await File.WriteAllTextAsync(temporary, "interrupted");
            await File.WriteAllTextAsync(unrelated, "retained");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(root, ".time-lapses", "time-lapses.db"), Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO publications(product_id) VALUES($id);";
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync();
            }
            using var worker = await store.AcquireWorkerAsync(CancellationToken.None);
            Assert.IsFalse(File.Exists(interrupted));
            Assert.IsFalse(File.Exists(temporary));
            Assert.AreEqual("retained", await File.ReadAllTextAsync(unrelated));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task TamperedFrozenPlan_FailsClosedInsteadOfChangingRetryInputs()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-tamper");
        try
        {
            var options = Options(root);
            using var store = new SqliteTimeLapseStore(options, new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10)));
            using var lease = await store.AcquireWorkerAsync(CancellationToken.None);
            var request = Occurrence(options.Value.TimeLapses, LocalAutomationSourceWindowKind.CompletedCivilHour);
            await store.EnqueueAsync(request, options.Value.TimeLapses.Freeze(NightlyProductFixture.Configuration()), [], CancellationToken.None);
            var job = await store.ClaimAsync(CancellationToken.None);
            Assert.IsNotNull(job);
            await store.FreezePlanAsync(job.JobId, new([], new Dictionary<string, int>()), CancellationToken.None);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(root, ".time-lapses", "time-lapses.db"), Pooling = false }.ToString());
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER immutable_job_inputs; UPDATE jobs SET plan_sha256='bad';";
            await command.ExecuteNonQueryAsync();
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await store.GetPlanAsync(job.JobId, CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    internal static IOptions<CameraAgentHostOptions> Options(string root) => Microsoft.Extensions.Options.Options.Create(new CameraAgentHostOptions
    {
        RawIngressRoot = root,
        TimeLapses = new() { Enabled = true, RigProfileSha256 = NightlyProductFixture.RigProfileSha256 }
    });

    internal static LocalAutomationOccurrence Occurrence(TimeLapseOptions options, LocalAutomationSourceWindowKind kind)
    {
        var window = NightlyProductFixture.Occurrence(Common.NightlyProducts.NightlyProductKind.Keogram, windowKind: kind).SourceWindow!;
        var definition = new LocalAutomationDefinition(kind == LocalAutomationSourceWindowKind.SunriseDay ? "video-day" : "video-hour", "Video", true, LocalAutomationTaskKind.TimeLapseGeneration,
            options.Target, LocalAutomationTriggerKind.SourceWindowClosed, 1, NightlyProductFixture.DayStartUtc, window.Policy);
        return LocalAutomationWindowPlanner.CreateOccurrence(new(definition, 1,
            LocalAutomationContract.ComputeRevisionSha256(definition), null, null), window);
    }
}
