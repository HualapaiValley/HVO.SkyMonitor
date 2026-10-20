using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.TimeLapses;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Focused MSTest methods.")]
public sealed class SqliteTimeLapseStoreTests
{
    [TestMethod]
    public async Task ZeroNineStoredJobReadsRecoversAndCompletesWithoutChangingItsPreset()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-upgrade");
        try
        {
            await RestoreZeroNineAsync(root);
            var options = Options(root);
            var clock = new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10));
            Guid jobId;
            string presetIdentity;
            using (var store = new SqliteTimeLapseStore(options, clock))
            {
                var retained = (await store.GetDayAsync(NightlyProductFixture.ObservingDate, CancellationToken.None)).Jobs.Single();
                jobId = retained.JobId;
                presetIdentity = retained.Preset.IdentitySha256;
                Assert.AreEqual(CameraAgentTimeLapseState.Working, retained.State);
                Assert.IsNull(retained.Preset.RendererVersion);
                await store.EnqueueAsync(retained.Occurrence, retained.Preset, [], CancellationToken.None);
                Assert.AreEqual("5EDB50EBB57E57FC43B575269FD2DF62AF6D30760DFB8807C421B69D026D98A2", presetIdentity);
                using var lease = await store.AcquireWorkerAsync(CancellationToken.None);
                var recovered = await store.ClaimAsync(CancellationToken.None);
                Assert.IsNotNull(recovered);
                Assert.AreEqual(jobId, recovered.JobId);
                Assert.AreEqual(retained.Preset, recovered.Preset);
                await store.FreezePlanAsync(jobId, new([], new Dictionary<string, int>()), CancellationToken.None);
                await store.CompleteAsync(jobId, CameraAgentTimeLapseState.NoSources, "timelapse.no-admitted-sources", null, CancellationToken.None);
            }
            using var reopened = new SqliteTimeLapseStore(options, clock);
            var completed = (await reopened.GetDayAsync(NightlyProductFixture.ObservingDate, CancellationToken.None)).Jobs.Single();
            Assert.AreEqual(jobId, completed.JobId);
            Assert.AreEqual(presetIdentity, completed.Preset.IdentitySha256);
            Assert.AreEqual(CameraAgentTimeLapseState.NoSources, completed.State);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task NewPresetRecordsItsRendererAndCannotMasqueradeAsAReleasedOrUnknownRecipe()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-renderer-preset");
        try
        {
            await RestoreZeroNineAsync(root);
            var options = Options(root);
            using var store = new SqliteTimeLapseStore(options, new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10)));
            var legacy = (await store.GetDayAsync(NightlyProductFixture.ObservingDate, CancellationToken.None)).Jobs.Single().Preset;
            var settings = new TimeLapseOptions
            {
                RigProfileSha256 = HVO.SkyMonitor.Astronomy.RigProjectionContextFactory.CreateProfileHashSha256(legacy.Rig),
                Frames = legacy.Frames, Timing = legacy.Timing, MaximumDimension = legacy.MaximumDimension
            };
            var current = settings.Freeze(NightlyProductFixture.Configuration() with { Rig = legacy.Rig });
            Assert.AreEqual(TimeLapseFrameRenderer.Version, current.RendererVersion);
            Assert.IsTrue(TimeLapseOptions.Matches(current));
            Assert.IsTrue(TimeLapseOptions.Matches(legacy));
            Assert.AreNotEqual(legacy.IdentitySha256, current.IdentitySha256);
            Assert.AreNotEqual("time-lapse:" + legacy.IdentitySha256, settings.Target);
            Assert.IsFalse(TimeLapseOptions.Matches(legacy with { RendererVersion = TimeLapseFrameRenderer.Version }));
            Assert.IsFalse(TimeLapseOptions.Matches(current with { RendererVersion = "unknown" }));
            Assert.IsFalse(TimeLapseOptions.Matches(current with { RendererVersion = null }));
        }
        finally { Directory.Delete(root, true); }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only the checked-in 0.9.0 fixture is executed; no user input.")]
    internal static async Task RestoreZeroNineAsync(string root)
    {
        var directory = Path.Combine(root, ".time-lapses");
        Directory.CreateDirectory(directory);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(directory, "time-lapses.db"), Pooling = false }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "time-lapse-v090", "working-job.sql"));
        await command.ExecuteNonQueryAsync();
    }

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

    [TestMethod]
    public async Task Summaries_UseLatestJobPerWindowForDailyStateProducedHoursAndPendingWork()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-video-summary");
        try
        {
            var options = Options(root);
            var preset = options.Value.TimeLapses.Freeze(NightlyProductFixture.Configuration());
            var hourlyDate = NightlyProductFixture.ObservingDate;
            var dailyDate = hourlyDate.AddDays(1);
            var workingDate = hourlyDate.AddDays(2);
            var clock = new NightlyClock(NightlyProductFixture.Calendar.Resolve(workingDate.AddDays(1)).EndUtc.AddMinutes(10));
            using var store = new SqliteTimeLapseStore(options, clock);
            using var lease = await store.AcquireWorkerAsync(CancellationToken.None);
            var hours = Windows(hourlyDate, LocalAutomationSourceWindowKind.CompletedCivilHour);
            // The rerun definition targets the same hour windows, so its newer jobs replace hours 0 and 1.
            foreach (var (definition, hour, state) in new[]
            {
                ("video-hour", 0, CameraAgentTimeLapseState.Produced), ("video-hour", 1, CameraAgentTimeLapseState.Produced),
                ("video-hour", 2, CameraAgentTimeLapseState.NoSources), ("video-hour", 3, CameraAgentTimeLapseState.Produced),
                ("video-hour-rerun", 1, CameraAgentTimeLapseState.Failed), ("video-hour-rerun", 0, CameraAgentTimeLapseState.Produced)
            })
            {
                await store.EnqueueAsync(Occurrence(options.Value.TimeLapses, hours[hour], definition), preset, [], CancellationToken.None);
                await FinishAsync(store, clock, root, await ClaimAsync(store, clock), state);
            }
            var dayHours = Windows(dailyDate, LocalAutomationSourceWindowKind.CompletedCivilHour);
            await store.EnqueueAsync(Occurrence(options.Value.TimeLapses, Windows(dailyDate, LocalAutomationSourceWindowKind.SunriseDay).Single(), "video-day"),
                preset, dayHours, CancellationToken.None);
            for (var i = 0; i < dayHours.Count; i++)
            {
                var child = await ClaimAsync(store, clock);
                await FinishAsync(store, clock, root, child, dayHours.Take(3).Any(hour => hour.IdentitySha256 == child.Window.IdentitySha256)
                    ? CameraAgentTimeLapseState.Produced : CameraAgentTimeLapseState.NoSources);
            }
            await FinishAsync(store, clock, root, await ClaimAsync(store, clock), CameraAgentTimeLapseState.Produced);
            var workingHour = Windows(workingDate, LocalAutomationSourceWindowKind.CompletedCivilHour)[0];
            await store.EnqueueAsync(Occurrence(options.Value.TimeLapses, workingHour, "video-hour"), preset, [], CancellationToken.None);
            await ClaimAsync(store, clock);
            await store.EnqueueAsync(Occurrence(options.Value.TimeLapses, hours[4], "video-hour"), preset, [], CancellationToken.None);
            foreach (var outside in new[] { hourlyDate.AddDays(-1), workingDate.AddDays(1) })
                await store.EnqueueAsync(Occurrence(options.Value.TimeLapses, Windows(outside, LocalAutomationSourceWindowKind.CompletedCivilHour)[0], "video-hour"),
                    preset, [], CancellationToken.None);

            var summaries = (await store.SummarizeAsync(hourlyDate, workingDate, CancellationToken.None)).ToDictionary(summary => summary.ReportDate);

            Assert.HasCount(3, summaries);
            Assert.AreEqual(new CameraAgentTimeLapseDateSummary(hourlyDate, hours[0].ReportingPeriod.IdentitySha256, null, 2, true), summaries[hourlyDate]);
            Assert.AreEqual(new CameraAgentTimeLapseDateSummary(dailyDate, dayHours[0].ReportingPeriod.IdentitySha256,
                CameraAgentTimeLapseState.Produced, 3, false), summaries[dailyDate]);
            Assert.AreEqual(new CameraAgentTimeLapseDateSummary(workingDate, workingHour.ReportingPeriod.IdentitySha256, null, 0, true), summaries[workingDate]);
        }
        finally { Directory.Delete(root, true); }
    }

    internal static IOptions<CameraAgentHostOptions> Options(string root) => Microsoft.Extensions.Options.Options.Create(new CameraAgentHostOptions
    {
        RawIngressRoot = root,
        TimeLapses = new() { Enabled = true, RigProfileSha256 = NightlyProductFixture.RigProfileSha256 }
    });

    internal static LocalAutomationOccurrence Occurrence(TimeLapseOptions options, LocalAutomationSourceWindowKind kind) =>
        Occurrence(options, NightlyProductFixture.Occurrence(Common.NightlyProducts.NightlyProductKind.Keogram, windowKind: kind).SourceWindow!,
            kind == LocalAutomationSourceWindowKind.SunriseDay ? "video-day" : "video-hour");

    private static LocalAutomationOccurrence Occurrence(TimeLapseOptions options, LocalAutomationSourceWindow window, string definitionId)
    {
        var definition = new LocalAutomationDefinition(definitionId, "Video", true, LocalAutomationTaskKind.TimeLapseGeneration,
            options.Target, LocalAutomationTriggerKind.SourceWindowClosed, 1, NightlyProductFixture.DayStartUtc, window.Policy);
        return LocalAutomationWindowPlanner.CreateOccurrence(new(definition, 1,
            LocalAutomationContract.ComputeRevisionSha256(definition), null, null), window);
    }

    private static IReadOnlyList<LocalAutomationSourceWindow> Windows(DateOnly date, LocalAutomationSourceWindowKind kind) =>
        new LocalAutomationWindowPlanner(new FixedObservingDayCalendarProvider(NightlyProductFixture.Calendar)).ResolveWindows(date,
            NightlyProductFixture.Occurrence(Common.NightlyProducts.NightlyProductKind.Keogram, windowKind: kind).SourceWindow!.Policy);

    private static async Task<CameraAgentTimeLapseJob> ClaimAsync(SqliteTimeLapseStore store, NightlyClock clock)
    {
        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        var job = await store.ClaimAsync(CancellationToken.None);
        Assert.IsNotNull(job);
        return job;
    }

    private static async Task FinishAsync(SqliteTimeLapseStore store, NightlyClock clock, string root, CameraAgentTimeLapseJob job,
        CameraAgentTimeLapseState state)
    {
        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        if (state != CameraAgentTimeLapseState.Produced)
        {
            await store.CompleteAsync(job.JobId, state, "fixture", null, CancellationToken.None);
            return;
        }
        // A real Produced completion requires an encoded publication; summaries read only job rows, so record the terminal row directly.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(root, ".time-lapses", "time-lapses.db"), Pooling = false }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET state='Produced', product_id=$product, updated_ticks=$now, revision=revision+1 WHERE job_id=$id AND state='Working';";
        command.Parameters.AddWithValue("$product", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$now", clock.UtcNow.UtcTicks);
        command.Parameters.AddWithValue("$id", job.JobId.ToString("N"));
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync());
    }
}
