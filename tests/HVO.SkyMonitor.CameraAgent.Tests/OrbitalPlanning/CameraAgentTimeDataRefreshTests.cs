using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.OrbitalPlanning;

/// <summary>
/// The managed time-data refresh (#1474 slice B2): bounded acquisition, provider checksums, atomic pair adoption,
/// single flight and the daily floor, source precedence and status. Fixed clocks and fake HTTP only; the served pair is
/// the committed bundled snapshot, so the provider checksum formats are real.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentTimeDataRefreshTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 6, 0, 0, TimeSpan.Zero);

    public TestContext TestContext { get; set; } = null!;

    private static byte[] BundledEop => File.ReadAllBytes(Path.Combine(CameraAgentPinnedOrbitalTimeSource.BundledDirectory,
        CameraAgentTimeDataSources.EarthOrientationFile));

    private static byte[] BundledLeap => File.ReadAllBytes(Path.Combine(CameraAgentPinnedOrbitalTimeSource.BundledDirectory,
        CameraAgentTimeDataSources.LeapSecondsFile));

    [TestMethod]
    public async Task ClientAcceptsOnlyBounded200ResponsesAsync()
    {
        var uri = CameraAgentTimeDataSources.LeapSecondsUri;
        var modified = new DateTimeOffset(2026, 7, 6, 7, 54, 11, TimeSpan.Zero);
        var ok = await FetchAsync(() => Response(HttpStatusCode.OK, [1, 2, 3], modified), uri, 3).ConfigureAwait(false);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, ok.Bytes);
        Assert.AreEqual(modified, ok.LastModifiedUtc);
        Assert.IsNull(ok.ReasonCode);

        Assert.AreEqual(CameraAgentTimeDataClient.HttpStatusReasonCode,
            (await FetchAsync(() => Response(HttpStatusCode.NotFound, [1]), uri, 3).ConfigureAwait(false)).ReasonCode);
        Assert.AreEqual(CameraAgentTimeDataClient.HttpStatusReasonCode,
            (await FetchAsync(() => Response(HttpStatusCode.MovedPermanently, []), uri, 3).ConfigureAwait(false)).ReasonCode, "no redirect is followed");
        var declared = await FetchAsync(() => Response(HttpStatusCode.OK, [1, 2, 3, 4]), uri, 3).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentTimeDataClient.OversizeReasonCode, declared.ReasonCode);
        Assert.IsNull(declared.Bytes);
        var streamed = await FetchAsync(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new UnsizedStream(4)) },
            uri, 3).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentTimeDataClient.OversizeReasonCode, streamed.ReasonCode, "a body without a length is still bounded");
        Assert.AreEqual(CameraAgentTimeDataClient.EmptyReasonCode,
            (await FetchAsync(() => Response(HttpStatusCode.OK, []), uri, 3).ConfigureAwait(false)).ReasonCode);
        var transport = await FetchAsync(() => throw new HttpRequestException("secret detail"), uri, 3).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentTimeDataClient.TransportReasonCode, transport.ReasonCode);
        Assert.IsFalse(transport.Detail.Contains("secret", StringComparison.Ordinal), "exception text never leaves the client");
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await FetchAsync(() => Response(HttpStatusCode.OK, [1]), new Uri("http://hpiers.obspm.fr/x"), 3).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ClientTimesOutWithinItsDeadlineAndRethrowsCallerCancellationAsync()
    {
        var timedOut = await FetchAsync(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return Response(HttpStatusCode.OK, [1]);
        }, CameraAgentTimeDataSources.EarthOrientationUri, 3, new Clock(Start), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentTimeDataClient.TimedOutReasonCode, timedOut.ReasonCode);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await FetchAsync(_ => Task.FromResult(Response(HttpStatusCode.OK, [1])),
            CameraAgentTimeDataSources.EarthOrientationUri, 3, TimeProvider.System, cancelled.Token).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public void ProviderChecksumsVerifyThePublishedFormats()
    {
        var eop = BundledEop;
        var sha512 = Convert.ToHexStringLower(SHA512.HashData(eop));
        var listed = Encoding.ASCII.GetBytes($"{new string('0', 128)}  finals2000A.daily\n{sha512}  finals2000A.daily.extended\n");
        Assert.AreEqual(sha512, CameraAgentTimeDataSources.PublishedEarthOrientationSha512(listed));
        Assert.IsTrue(CameraAgentTimeDataSources.MatchesSha512(eop, sha512));
        Assert.IsFalse(CameraAgentTimeDataSources.MatchesSha512(eop.AsSpan(1), sha512));
        Assert.IsNull(CameraAgentTimeDataSources.PublishedEarthOrientationSha512(Encoding.ASCII.GetBytes(
            $"{sha512}  finals2000A.daily.extended\n{sha512}  finals2000A.daily.extended\n")), "listed twice");
        Assert.IsNull(CameraAgentTimeDataSources.PublishedEarthOrientationSha512(Encoding.ASCII.GetBytes("abc  finals2000A.daily.extended\n")));
        Assert.IsNull(CameraAgentTimeDataSources.PublishedEarthOrientationSha512(Encoding.ASCII.GetBytes($"{sha512}  other\n")));

        var leap = BundledLeap;
        var published = CameraAgentTimeDataSources.VerifiedLeapSecondsHash(leap);
        Assert.IsNotNull(published, "the published leap file verifies against its own #h");
        Assert.IsTrue(CameraAgentTimeDataSources.CarriesLeapSecondsTerms(leap));
        Assert.IsNull(CameraAgentTimeDataSources.VerifiedLeapSecondsHash(WithLaterExpiry(leap)), "a changed expiry breaks #h");
        Assert.IsNull(CameraAgentTimeDataSources.VerifiedLeapSecondsHash(Encoding.ASCII.GetBytes(
            Encoding.ASCII.GetString(leap) + "#h " + published + "\n")), "two #h lines");
        Assert.IsFalse(CameraAgentTimeDataSources.CarriesLeapSecondsTerms(Encoding.ASCII.GetBytes(
            Encoding.ASCII.GetString(leap).Replace(CameraAgentTimeDataSources.LeapSecondsTerms, "Copyright.", StringComparison.Ordinal))));
    }

    [TestMethod]
    public async Task RefreshAdoptsAVerifiedPairAtomicallyWithItsOriginalBytesAndProvenanceAsync()
    {
        using var host = new Host();
        var execution = await host.Job.ExecuteAsync(manual: false, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, execution.Outcome, execution.Detail);
        StringAssert.StartsWith(execution.Detail, CameraAgentTimeDataRefreshJob.AdoptedReasonCode, StringComparison.Ordinal);
        CollectionAssert.AreEqual(new[] { CameraAgentTimeDataSources.EarthOrientationUri, CameraAgentTimeDataSources.EarthOrientationChecksumsUri,
            CameraAgentTimeDataSources.LeapSecondsUri }, host.Client.Requests);

        var pointer = (await host.Store.ReadPointerAsync(TestContext.CancellationToken).ConfigureAwait(false))!;
        var directory = host.Store.PairDirectory(pointer.Pair);
        CollectionAssert.AreEqual(BundledEop, await File.ReadAllBytesAsync(Path.Combine(directory, CameraAgentTimeDataSources.EarthOrientationFile),
            TestContext.CancellationToken).ConfigureAwait(false));
        CollectionAssert.AreEqual(BundledLeap, await File.ReadAllBytesAsync(Path.Combine(directory, CameraAgentTimeDataSources.LeapSecondsFile),
            TestContext.CancellationToken).ConfigureAwait(false));
        using var provenance = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(directory,
            CameraAgentPinnedOrbitalTimeSource.ProvenanceFileName), TestContext.CancellationToken).ConfigureAwait(false));
        var root = provenance.RootElement;
        Assert.AreEqual(CameraAgentManagedTimeDataStore.ProvenanceSchema, root.GetProperty("schema").GetString());
        var eop = root.GetProperty("earthOrientation");
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(BundledEop)), eop.GetProperty("sha256").GetString());
        Assert.AreEqual(Convert.ToHexStringLower(SHA512.HashData(BundledEop)), eop.GetProperty("providerChecksum").GetProperty("value").GetString());
        Assert.AreEqual(Host.Modified, eop.GetProperty("lastModifiedUtc").GetDateTimeOffset());
        Assert.AreEqual(Start, eop.GetProperty("retrievedUtc").GetDateTimeOffset());
        Assert.AreEqual(BundledEop.Length, eop.GetProperty("lengthBytes").GetInt32());
        Assert.AreEqual(CameraAgentTimeDataSources.VerifiedLeapSecondsHash(BundledLeap),
            root.GetProperty("leapSeconds").GetProperty("providerChecksum").GetProperty("value").GetString());

        var state = (await host.Store.ReadStateAsync(TestContext.CancellationToken).ConfigureAwait(false))!;
        Assert.AreEqual(CameraAgentTimeDataRefreshOutcome.Adopted, state.Outcome);
        Assert.AreEqual(Start, state.LastAttemptUtc);
        Assert.AreEqual(Start, state.LastSuccessUtc);
        Assert.AreEqual(pointer.Pair, pointer.History.Single().Pair);

        // The same coverage as the bundled pair: the tie keeps the bundled pair active, and the managed pair is verified.
        var resolution = await host.Source.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentOrbitalTimeDataSource.Bundled, resolution.Source);
        Assert.AreEqual(CameraAgentManagedTimeDataState.Verified, resolution.Managed);
    }

    [TestMethod]
    [DataRow("checksum-mismatch")]
    [DataRow("checksum-unlisted")]
    [DataRow("leap-hash-mismatch")]
    [DataRow("terms-missing")]
    [DataRow("parse-failed")]
    [DataRow("oversize")]
    [DataRow("timed-out")]
    [DataRow("http-status")]
    public async Task EveryRejectedDownloadKeepsTheAdoptedPairAsync(string fault)
    {
        using var host = new Host();
        var prior = await host.AdoptLongerPairAsync().ConfigureAwait(false);
        host.Clock.Now = Start.AddDays(1);
        var garbage = Encoding.ASCII.GetBytes("not a finals2000A file\n");
        var (expected, configure) = fault switch
        {
            "checksum-mismatch" => (CameraAgentTimeDataRefreshJob.ChecksumMismatchReasonCode,
                (Action<FakeClient>)(client => client.Serve(CameraAgentTimeDataSources.EarthOrientationChecksumsUri, Checksums(garbage)))),
            "checksum-unlisted" => (CameraAgentTimeDataRefreshJob.ChecksumUnavailableReasonCode,
                client => client.Serve(CameraAgentTimeDataSources.EarthOrientationChecksumsUri, Encoding.ASCII.GetBytes("x  finals2000A.daily\n"))),
            "leap-hash-mismatch" => (CameraAgentTimeDataRefreshJob.LeapHashMismatchReasonCode,
                client => client.Serve(CameraAgentTimeDataSources.LeapSecondsUri, WithLaterExpiry(BundledLeap))),
            "terms-missing" => (CameraAgentTimeDataRefreshJob.TermsMissingReasonCode,
                client => client.Serve(CameraAgentTimeDataSources.LeapSecondsUri, WithoutTerms(BundledLeap))),
            "parse-failed" => (CameraAgentTimeDataRefreshJob.ParseFailedReasonCode, client =>
            {
                client.Serve(CameraAgentTimeDataSources.EarthOrientationUri, garbage);
                client.Serve(CameraAgentTimeDataSources.EarthOrientationChecksumsUri, Checksums(garbage));
            }
            ),
            "oversize" => (CameraAgentTimeDataClient.OversizeReasonCode,
                client => client.Fail(CameraAgentTimeDataSources.EarthOrientationUri, CameraAgentTimeDataClient.OversizeReasonCode)),
            "timed-out" => (CameraAgentTimeDataClient.TimedOutReasonCode,
                client => client.Fail(CameraAgentTimeDataSources.LeapSecondsUri, CameraAgentTimeDataClient.TimedOutReasonCode)),
            _ => (CameraAgentTimeDataClient.HttpStatusReasonCode,
                client => client.Fail(CameraAgentTimeDataSources.EarthOrientationChecksumsUri, CameraAgentTimeDataClient.HttpStatusReasonCode))
        };
        configure(host.Client);

        var execution = await host.Job.ExecuteAsync(manual: false, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Failed, execution.Outcome, execution.Detail);
        StringAssert.StartsWith(execution.Detail, expected, StringComparison.Ordinal);
        var state = (await host.Store.ReadStateAsync(TestContext.CancellationToken).ConfigureAwait(false))!;
        Assert.AreEqual(CameraAgentTimeDataRefreshOutcome.Failed, state.Outcome);
        Assert.AreEqual(expected, state.ReasonCode);
        Assert.AreEqual((await host.Store.ReadPointerAsync(TestContext.CancellationToken).ConfigureAwait(false))!.Pair, prior.Pair);
        var resolution = await host.Source.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentOrbitalTimeDataSource.Managed, resolution.Source);
        Assert.AreEqual(prior.History[0].LeapSecondsSha256, resolution.Data!.LeapSecondsSha256);
        Assert.HasCount(1, Directory.GetDirectories(Path.Combine(host.Store.RootDirectory, "pairs")), "a rejected download writes no pair");
    }

    [TestMethod]
    public async Task CrashBetweenWritingAndAdoptingKeepsThePriorPairAndTheOrphanIsRetiredAsync()
    {
        using var host = new Host();
        var prior = await host.AdoptLongerPairAsync().ConfigureAwait(false);
        host.Clock.Now = Start.AddDays(1);
        // A longer download than the adopted pair would be adopted; stop it after its files are durable.
        await host.Store.AdoptAsync(Candidate(BundledEop, WithLaterExpiry(BundledLeap, 800)), Start, TestContext.CancellationToken).ConfigureAwait(false);
        var current = (await host.Store.ReadPointerAsync(TestContext.CancellationToken).ConfigureAwait(false))!;
        host.Store.BeforeCommit = () => throw new IOException("simulated crash before the pointer is replaced");
        var crashed = await host.Store.AdoptAsync(Candidate(BundledEop, WithLaterExpiry(BundledLeap, 900)), Start.AddDays(1),
            TestContext.CancellationToken).AsTask().ContinueWith(static task => task.Exception, TaskScheduler.Default).ConfigureAwait(false);
        Assert.IsInstanceOfType<IOException>(crashed!.InnerException);

        // A restarted process sees the pair adopted before the crash, never the half-adopted one.
        var restarted = new CameraAgentManagedTimeDataStore(host.Store.RootDirectory);
        Assert.AreEqual(current.Pair, (await restarted.ReadPointerAsync(TestContext.CancellationToken).ConfigureAwait(false))!.Pair);
        using var source = new CameraAgentPinnedOrbitalTimeSource(host.Options, CameraAgentPinnedOrbitalTimeSource.BundledDirectory, restarted, host.Clock);
        var resolution = await source.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentOrbitalTimeDataSource.Managed, resolution.Source);
        Assert.AreEqual(current.History[0].LeapSecondsSha256, resolution.Data!.LeapSecondsSha256);
        Assert.HasCount(3, Directory.GetDirectories(Path.Combine(host.Store.RootDirectory, "pairs")), "current, previous and the orphan");

        // The next adoption keeps only the current and previous pairs and retires the orphan and the oldest pair.
        var next = await restarted.AdoptAsync(Candidate(BundledEop, WithLaterExpiry(BundledLeap, 1000)), Start.AddDays(2),
            TestContext.CancellationToken).ConfigureAwait(false);
        CollectionAssert.AreEquivalent(new[] { next.Pair, current.Pair },
            Directory.GetDirectories(Path.Combine(host.Store.RootDirectory, "pairs")).Select(Path.GetFileName).ToArray());
        CollectionAssert.AreEqual(new[] { next.Pair, current.Pair, prior.Pair }, next.History.Select(static item => item.Pair).ToArray(),
            "provenance of retired pairs stays attributable");
    }

    [TestMethod]
    public async Task StorageFailureDuringAdoptionIsRecordedAndKeepsThePriorPairAsync()
    {
        using var host = new Host();
        var prior = await host.AdoptLongerPairAsync(days: 2000).ConfigureAwait(false);
        host.Clock.Now = Start.AddDays(1);
        host.Store.BeforeCommit = () => throw new IOException("disk full");
        // The download is shorter than the adopted pair would make it not-newer, so serve a longer one through the job.
        host.Client.Serve(CameraAgentTimeDataSources.EarthOrientationUri, BundledEop);
        var execution = await host.Job.ExecuteAsync(manual: true, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, execution.Outcome, "a shorter download is not-newer and writes nothing");
        StringAssert.StartsWith(execution.Detail, CameraAgentTimeDataRefreshJob.NotNewerReasonCode, StringComparison.Ordinal);
        Assert.AreEqual(prior.Pair, (await host.Store.ReadPointerAsync(TestContext.CancellationToken).ConfigureAwait(false))!.Pair);

        using var empty = new Host();
        empty.Store.BeforeCommit = () => throw new IOException("disk full");
        var failed = await empty.Job.ExecuteAsync(manual: false, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Failed, failed.Outcome);
        StringAssert.StartsWith(failed.Detail, CameraAgentTimeDataRefreshJob.StorageFailedReasonCode, StringComparison.Ordinal);
        Assert.IsNull(await empty.Store.ReadPointerAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(CameraAgentTimeDataRefreshJob.StorageFailedReasonCode,
            (await empty.Store.ReadStateAsync(TestContext.CancellationToken).ConfigureAwait(false))!.ReasonCode);
    }

    [TestMethod]
    public async Task SingleFlightAndTheDurableDailyFloorBoundEveryAttemptAsync()
    {
        using var host = new Host();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Client.Gate = gate.Task;
        var first = host.Job.ExecuteAsync(manual: false, TestContext.CancellationToken).AsTask();
        await host.Client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsTrue(host.Job.IsRunning);
        var second = await host.Job.ExecuteAsync(manual: true, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Skipped, second.Outcome);
        StringAssert.StartsWith(second.Detail, CameraAgentTimeDataRefreshJob.InFlightReasonCode, StringComparison.Ordinal);
        Assert.AreEqual("in-progress", (await host.Operations.GetStatusAsync(TestContext.CancellationToken).ConfigureAwait(false)).Refresh.Outcome);
        gate.SetResult();
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, (await first.ConfigureAwait(false)).Outcome);
        host.Client.Gate = null;
        var requests = host.Client.Requests.Count;

        foreach (var (offset, manual) in new[] { (TimeSpan.Zero, true), (TimeSpan.FromHours(23), false) })
        {
            host.Clock.Now = Start + offset;
            var skipped = await host.Job.ExecuteAsync(manual, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(LocalAutomationRunOutcome.Skipped, skipped.Outcome);
            StringAssert.Contains(skipped.Detail, "next permitted attempt 2026-10-23T06:00:00.0000000Z", StringComparison.Ordinal);
        }
        // A restart keeps the floor: it is read from the durable record.
        var restarted = new CameraAgentTimeDataRefreshJob(new CameraAgentManagedTimeDataStore(host.Store.RootDirectory), host.Client,
            host.Options, host.Clock, NullLogger<CameraAgentTimeDataRefreshJob>.Instance);
        StringAssert.StartsWith((await restarted.ExecuteAsync(manual: true, TestContext.CancellationToken).ConfigureAwait(false)).Detail,
            CameraAgentTimeDataRefreshJob.FloorReasonCode, StringComparison.Ordinal);
        Assert.AreEqual(requests, host.Client.Requests.Count, "no request is made inside the floor");

        // A daily occurrence that starts up to two minutes early waits out the floor precisely and runs.
        host.Clock.Now = Start.AddDays(1).AddSeconds(-90);
        var daily = await host.Job.ExecuteAsync(manual: false, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, daily.Outcome, daily.Detail);
        StringAssert.StartsWith(daily.Detail, CameraAgentTimeDataRefreshJob.UnchangedReasonCode, StringComparison.Ordinal);
        Assert.AreEqual(1, host.Clock.Delays.Count(static delay => delay == TimeSpan.FromSeconds(90)));
    }

    [TestMethod]
    public async Task OffSwitchSkipsWithoutAnyRequestOrRecordAsync()
    {
        using var host = new Host();
        host.Options.CurrentValue.TimeDataRefreshEnabled = false;
        var execution = await host.Job.ExecuteAsync(manual: true, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Skipped, execution.Outcome);
        StringAssert.StartsWith(execution.Detail, CameraAgentTimeDataRefreshJob.DisabledReasonCode, StringComparison.Ordinal);
        Assert.IsEmpty(host.Client.Requests);
        Assert.IsNull(await host.Store.ReadStateAsync(TestContext.CancellationToken).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CancellationClosesTheRecordAndAStoppedProcessReadsAsInterruptedAsync()
    {
        using var host = new Host();
        using var cancellation = new CancellationTokenSource();
        host.Client.OnFetch = () => cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await host.Job.ExecuteAsync(manual: false, cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
        var state = (await host.Store.ReadStateAsync(TestContext.CancellationToken).ConfigureAwait(false))!;
        Assert.AreEqual(CameraAgentTimeDataRefreshJob.CancelledReasonCode, state.ReasonCode);
        Assert.IsNull(await host.Store.ReadPointerAsync(TestContext.CancellationToken).ConfigureAwait(false));

        await host.Store.WriteStateAsync(state with { Outcome = CameraAgentTimeDataRefreshOutcome.InProgress, CompletedUtc = null },
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("interrupted", (await host.Operations.GetStatusAsync(TestContext.CancellationToken).ConfigureAwait(false)).Refresh.Outcome);
    }

    [TestMethod]
    public async Task PrecedenceRanksTheOverrideThenTheLaterCoverageWithTheBundledPairOnATieAsync()
    {
        using var host = new Host();
        var bundled = await host.Source.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentOrbitalTimeDataSource.Bundled, bundled.Source, "no managed pair yet");
        Assert.AreEqual(CameraAgentManagedTimeDataState.None, bundled.Managed);
        var bundledEnd = CameraAgentPinnedOrbitalTimeSource.PlanningCoverageEnd(bundled.Data!);

        // An equal pair ties and the bundled pair stays active.
        await host.Store.AdoptAsync(Candidate(BundledEop, BundledLeap), Start, TestContext.CancellationToken).ConfigureAwait(false);
        var tie = await host.Source.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentOrbitalTimeDataSource.Bundled, tie.Source);
        Assert.AreEqual(bundledEnd, tie.ManagedCoverageEndUtc);

        // A pair whose coverage ends later wins, and its adoption is picked up by the next resolution.
        await host.AdoptLongerPairAsync(days: 100).ConfigureAwait(false);
        var managed = await host.Source.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentOrbitalTimeDataSource.Managed, managed.Source);
        Assert.IsGreaterThan(bundledEnd, CameraAgentPinnedOrbitalTimeSource.PlanningCoverageEnd(managed.Data!));
        Assert.AreEqual(managed.Data!.LeapSecondsSha256, managed.LeapSecondsProvenance!.Sha256);

        // A newer release (a bundled pair that covers longer) is not shadowed by the older refresh.
        var newerBundle = host.CopyBundle(WithLaterExpiry(BundledLeap, 3000));
        using (var upgraded = new CameraAgentPinnedOrbitalTimeSource(host.Options, newerBundle, host.Store, host.Clock))
            Assert.AreEqual(CameraAgentOrbitalTimeDataSource.Bundled,
                (await upgraded.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false)).Source);

        // A missing bundle leaves the verified managed pair; a corrupted managed pair leaves the bundled one.
        using (var unbundled = new CameraAgentPinnedOrbitalTimeSource(host.Options, Path.Combine(host.Root, "absent"), host.Store, host.Clock))
            Assert.AreEqual(CameraAgentOrbitalTimeDataSource.Managed,
                (await unbundled.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false)).Source);
        var pointer = (await host.Store.ReadPointerAsync(TestContext.CancellationToken).ConfigureAwait(false))!;
        await File.AppendAllTextAsync(Path.Combine(host.Store.PairDirectory(pointer.Pair), CameraAgentTimeDataSources.LeapSecondsFile), "#",
            TestContext.CancellationToken).ConfigureAwait(false);
        using (var fresh = new CameraAgentPinnedOrbitalTimeSource(host.Options, CameraAgentPinnedOrbitalTimeSource.BundledDirectory, host.Store, host.Clock))
        {
            var fallback = await fresh.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentOrbitalTimeDataSource.Bundled, fallback.Source);
            Assert.AreEqual(CameraAgentManagedTimeDataState.Invalid, fallback.Managed);
        }
        // No managed pair remains only after the independently eligible predecessor is also damaged.
        await File.AppendAllTextAsync(Path.Combine(host.Store.PairDirectory(pointer.Previous!), CameraAgentTimeDataSources.LeapSecondsFile), "#",
            TestContext.CancellationToken).ConfigureAwait(false);
        using (var neither = new CameraAgentPinnedOrbitalTimeSource(host.Options, Path.Combine(host.Root, "absent"), host.Store, host.Clock))
        {
            var none = await neither.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentOrbitalTimeDataProblem.BundledInvalid, none.Problem);
            Assert.IsNull(none.Data);
        }

        // An override wins over both, and an invalid override never falls back to either.
        await host.AdoptLongerPairAsync(days: 1200).ConfigureAwait(false);
        var settings = host.Options.CurrentValue;
        var eopPath = Path.Combine(host.Root, "override-eop");
        var leapPath = Path.Combine(host.Root, "override-leap");
        await File.WriteAllBytesAsync(eopPath, PlanningTestInputs.Bytes("time", "predicted-2026.finals2000A"), TestContext.CancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(leapPath, PlanningTestInputs.Bytes("time", "leap-seconds.list"), TestContext.CancellationToken).ConfigureAwait(false);
        settings.EarthOrientationPath = eopPath;
        settings.EarthOrientationSha256 = PlanningTestInputs.Hash(PlanningTestInputs.Bytes("time", "predicted-2026.finals2000A"));
        settings.LeapSecondsPath = leapPath;
        settings.LeapSecondsSha256 = PlanningTestInputs.Hash(PlanningTestInputs.Bytes("time", "leap-seconds.list"));
        var overridden = await host.Source.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentOrbitalTimeDataSource.Override, overridden.Source);
        Assert.AreEqual(CameraAgentOrbitalTimeDataProblem.None, overridden.Problem);
        settings.LeapSecondsSha256 = new string('A', 64);
        var invalid = await host.Source.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentOrbitalTimeDataProblem.OverrideInvalid, invalid.Problem);
        Assert.IsNull(invalid.Data);
    }

    [TestMethod]
    public async Task StatusReportsAnExpiredPairSeparatelyFromAFailedRefreshAsync()
    {
        using var host = new Host();
        var never = await host.Operations.GetStatusAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentTimeDataStatus.CurrentVersion, never.SchemaVersion);
        Assert.AreEqual("bundled", never.ActiveSource);
        Assert.AreEqual("none", never.Problem);
        Assert.AreEqual("never", never.Refresh.Outcome);
        Assert.IsFalse(never.Expired);
        Assert.IsNull(never.ManagedPair);

        host.Client.Fail(CameraAgentTimeDataSources.EarthOrientationUri, CameraAgentTimeDataClient.TransportReasonCode);
        await host.Job.ExecuteAsync(manual: false, TestContext.CancellationToken).ConfigureAwait(false);
        var failed = await host.Operations.GetStatusAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("failed", failed.Refresh.Outcome);
        Assert.AreEqual(CameraAgentTimeDataClient.TransportReasonCode, failed.Refresh.ReasonCode);
        Assert.AreEqual(Start.AddDays(1), failed.Refresh.NextPermittedAttemptUtc);
        Assert.IsFalse(failed.Expired, "a failed refresh does not expire a pair that still covers today");
        Assert.AreEqual(failed.CoverageEndUtc, never.CoverageEndUtc);

        var adopted = await host.AdoptLongerPairAsync().ConfigureAwait(false);
        var end = (await host.Operations.GetStatusAsync(TestContext.CancellationToken).ConfigureAwait(false)).CoverageEndUtc!.Value;
        host.Clock.Now = end.AddDays(1);
        var expired = await host.Operations.GetStatusAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsTrue(expired.Expired);
        Assert.AreEqual("managed", expired.ActiveSource);
        Assert.AreEqual("failed", expired.Refresh.Outcome, "expiry and the last refresh are reported separately");
        Assert.AreEqual(adopted.Pair, expired.ManagedPair!.Pair);
        Assert.AreEqual("verified", expired.ManagedPair.State);
        Assert.AreEqual(end, expired.ManagedPair.CoverageEndUtc);
        Assert.IsTrue(expired.RefreshEnabled);
        Assert.IsFalse(expired.RefreshScheduled, "no automation store is attached");

        // An override does not evaluate the managed pair, but its last adoption is still reported.
        var settings = host.Options.CurrentValue;
        settings.EarthOrientationPath = Path.Combine(host.Root, "absent-eop");
        settings.EarthOrientationSha256 = new string('A', 64);
        settings.LeapSecondsPath = Path.Combine(host.Root, "absent-leap");
        settings.LeapSecondsSha256 = new string('B', 64);
        var overridden = await host.Operations.GetStatusAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("override", overridden.ActiveSource);
        Assert.AreEqual("override-invalid", overridden.Problem);
        Assert.IsNull(overridden.CoverageEndUtc);
        Assert.AreEqual(adopted.Pair, overridden.ManagedPair!.Pair);
        Assert.AreEqual("not-checked", overridden.ManagedPair.State);
    }

    [TestMethod]
    public void AutomationAdapterAcceptsOnlyTheReservedDailyDefinition()
    {
        using var host = new Host();
        var adapter = new CameraAgentTimeDataAutomationAdapter(host.Job);
        Assert.AreEqual(LocalAutomationTaskKind.TimeDataRefresh, adapter.TaskKind);
        Assert.AreEqual(LocalAutomationTaskKind.TimeDataRefresh, adapter.Describe().TaskKind);
        Assert.IsTrue(adapter.SupportsRunNow);
        Assert.IsTrue(adapter.RunsOffSweep);
        Assert.IsNull(adapter.Validate(Definition()));
        Assert.AreEqual(CameraAgentTimeDataAutomationAdapter.ReservedDefinitionIdReasonCode,
            adapter.Validate(Definition() with { DefinitionId = "other" })!.ReasonCode);
        Assert.AreEqual(LocalAutomationContract.UnregisteredTargetReasonCode, adapter.Validate(Definition() with { TaskTarget = "x" })!.ReasonCode);
        Assert.AreEqual(CameraAgentTimeDataAutomationAdapter.IntervalReasonCode,
            adapter.Validate(Definition() with { TriggerInterval = 43_200 })!.ReasonCode, "never more than daily");
        host.Options.CurrentValue.TimeDataRefreshEnabled = false;
        Assert.IsNull(adapter.Validate(Definition()), "the definition can be prepared while the switch is off");
    }

    [TestMethod]
    public async Task RefreshRegistrationAddsTheDailyAdapterAndPlanningStaysAcquisitionFreeAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-time-data-di-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }));
            services.AddCameraAgentSatellitePlanning(new ConfigurationBuilder().Build());
            services.AddCameraAgentTimeDataRefresh();
            var provider = services.BuildServiceProvider();
            await using var disposal = provider.ConfigureAwait(false);
            Assert.AreEqual(Path.Combine(root, CameraAgentManagedTimeDataStore.DirectoryName),
                provider.GetRequiredService<CameraAgentManagedTimeDataStore>().RootDirectory);
            Assert.AreEqual(1, provider.GetServices<ILocalAutomationPeriodicTaskAdapter>()
                .Count(static adapter => adapter.TaskKind == LocalAutomationTaskKind.TimeDataRefresh), "the time-data adapter is registered once");
            Assert.IsNotNull(provider.GetRequiredService<CameraAgentTimeDataOperations>());
            Assert.IsFalse(provider.GetRequiredService<IOptionsMonitor<CameraAgentSatellitePlanningOptions>>().CurrentValue.TimeDataRefreshEnabled,
                "the refresh is off by default");
            Assert.IsFalse(Directory.Exists(root), "registration and resolution write nothing");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task AdoptingAPairRetrievedAfterTheDailyUpdateKeepsTheRestOfTheDayCoveredAsync()
    {
        // USNO's daily update (about 17:11Z) leaves only rapid (I) rows for the current UTC day, which planning treats as a
        // gap. The committed snapshot is such a download: retrieved after the 2026-10-21 update, it covers from 2026-10-22.
        var day = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
        using var host = new Host(day.AddHours(12));
        var earlier = await host.Store.AdoptAsync(Candidate(PredictedOn(BundledEop, day), WithLaterExpiry(BundledLeap, 50)),
            day.AddHours(-12), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, (await PlanAsync(host, host.Clock.Now).ConfigureAwait(false)).Outcome,
            "the pair adopted the day before still covers today");

        // The daily run after the update downloads a pair whose coverage ends later but cannot evaluate the rest of today.
        host.Clock.Now = day.AddHours(17.5);
        var later = Republished(BundledLeap, 400);
        host.Client.Serve(CameraAgentTimeDataSources.LeapSecondsUri, later);
        var execution = await host.Job.ExecuteAsync(manual: false, TestContext.CancellationToken).ConfigureAwait(false);
        StringAssert.StartsWith(execution.Detail, CameraAgentTimeDataRefreshJob.AdoptedReasonCode, StringComparison.Ordinal);
        var adopted = (await host.Store.ReadPointerAsync(TestContext.CancellationToken).ConfigureAwait(false))!;
        Assert.AreEqual(earlier.Pair, adopted.Previous);

        // Until 00:00Z the earlier pair stays active, and the status names the pair planning uses.
        foreach (var at in new[] { day.AddHours(17.75), day.AddHours(23.5) })
        {
            host.Clock.Now = at;
            var plan = await PlanAsync(host, at).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, plan.Outcome, $"planning from {at:O}");
            Assert.AreEqual(earlier.History[0].LeapSecondsSha256, plan.LeapSecondsSha256, "the earlier pair keeps today covered");
            var status = await host.Operations.GetStatusAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("managed", status.ActiveSource);
            Assert.AreEqual("none", status.Problem);
            Assert.IsFalse(status.Expired);
            Assert.IsTrue(status.CoversNow);
            Assert.AreEqual(plan.LeapSecondsSha256, status.LeapSecondsSha256, "the status describes the pair planning uses");
            Assert.AreEqual(earlier.Pair, (await host.Source.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false)).ActiveManagedPair);
            Assert.AreEqual(adopted.Pair, status.ManagedPair!.Pair, "the last adoption is still reported");
        }

        // A lost attempt record lifts the floor; a second same-day download must not retire the only pair covering today.
        host.Clock.Now = day.AddHours(18);
        File.Delete(Path.Combine(host.Store.RootDirectory, CameraAgentManagedTimeDataStore.StateFile));
        host.Client.Serve(CameraAgentTimeDataSources.LeapSecondsUri, Republished(BundledLeap, 800));
        var again = await host.Job.ExecuteAsync(manual: true, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, again.Outcome, again.Detail);
        StringAssert.StartsWith(again.Detail, CameraAgentTimeDataRefreshJob.NotNewerReasonCode, StringComparison.Ordinal);
        var kept = (await host.Store.ReadPointerAsync(TestContext.CancellationToken).ConfigureAwait(false))!;
        Assert.AreEqual(adopted.Pair, kept.Pair);
        Assert.AreEqual(earlier.Pair, kept.Previous);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, (await PlanAsync(host, host.Clock.Now).ConfigureAwait(false)).Outcome);

        // From 00:00Z the new pair covers the day and, ending latest, takes over.
        foreach (var at in new[] { day.AddDays(1), day.AddDays(1).AddHours(6) })
        {
            host.Clock.Now = at;
            var plan = await PlanAsync(host, at).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, plan.Outcome, $"planning from {at:O}");
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(later)), plan.LeapSecondsSha256, "the new pair takes over");
            var status = await host.Operations.GetStatusAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("managed", status.ActiveSource);
            Assert.IsTrue(status.CoversNow);
            Assert.AreEqual(plan.LeapSecondsSha256, status.LeapSecondsSha256);
            Assert.AreEqual(adopted.Pair, (await host.Source.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false)).ActiveManagedPair);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RefreshKeepsTheIntactPairCoveringNowWhenTheCurrentPairIsDamagedAsync(bool damageCurrent)
    {
        var now = new DateTimeOffset(2027, 7, 2, 18, 0, 0, TimeSpan.Zero);
        using var host = new Host(now);
        var earlier = await host.AdoptLongerPairAsync(400).ConfigureAwait(false);
        var current = await host.AdoptLongerPairAsync(500).ConfigureAwait(false);
        Assert.IsTrue(CameraAgentPinnedOrbitalTimeSource.Covers(
            (await CameraAgentPinnedOrbitalTimeSource.ReadProvenancedAsync(host.Store.PairDirectory(earlier.Pair),
                CameraAgentManagedTimeDataStore.ProvenanceSchema, TestContext.CancellationToken).ConfigureAwait(false))!.Data, new(now, now)));
        if (damageCurrent)
            await File.WriteAllTextAsync(Path.Combine(host.Store.PairDirectory(current.Pair), CameraAgentTimeDataSources.EarthOrientationFile),
                "damaged", TestContext.CancellationToken).ConfigureAwait(false);

        var rapid = RapidOn(BundledEop, now.AddHours(-18));
        host.Client.Serve(CameraAgentTimeDataSources.EarthOrientationUri, rapid);
        host.Client.Serve(CameraAgentTimeDataSources.EarthOrientationChecksumsUri, Checksums(rapid));
        host.Client.Serve(CameraAgentTimeDataSources.LeapSecondsUri, Republished(BundledLeap, 800));
        var execution = await host.Job.ExecuteAsync(manual: true, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, execution.Outcome, execution.Detail);
        StringAssert.StartsWith(execution.Detail, damageCurrent ? CameraAgentTimeDataRefreshJob.NotNewerReasonCode :
            CameraAgentTimeDataRefreshJob.AdoptedReasonCode, StringComparison.Ordinal);
        var kept = (await host.Store.ReadPointerAsync(TestContext.CancellationToken).ConfigureAwait(false))!;
        var intact = damageCurrent ? earlier : current;
        Assert.AreEqual(intact.Pair, kept.Previous);
        Assert.IsTrue(Directory.Exists(host.Store.PairDirectory(intact.Pair)), "the intact pair covering now survives retirement");
        var plan = await PlanAsync(host, now).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, plan.Outcome);
        Assert.AreEqual(intact.History[0].LeapSecondsSha256, plan.LeapSecondsSha256);
        var status = await host.Operations.GetStatusAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsTrue(status.CoversNow);
        Assert.IsFalse(status.Expired);
        Assert.AreEqual("managed", status.ActiveSource);
        Assert.AreEqual("none", status.Problem);
        Assert.AreEqual(plan.LeapSecondsSha256, status.LeapSecondsSha256);
        Assert.AreEqual(damageCurrent ? "invalid" : "verified", status.ManagedPair!.State);
        Assert.AreEqual(damageCurrent ? "not-newer" : "adopted", status.Refresh.Outcome);
    }

    [TestMethod]
    [DataRow(false, "not-newer")]
    [DataRow(true, "not-newer")]
    [DataRow(false, "rejected")]
    [DataRow(true, "rejected")]
    [DataRow(false, "adopted")]
    [DataRow(true, "adopted")]
    [DataRow(false, "failed")]
    [DataRow(true, "failed")]
    public async Task RefreshRevalidatesAWarmManagedCacheOnEveryOutcomeAsync(bool damageCurrent, string outcome)
    {
        var now = new DateTimeOffset(2027, 7, 2, 18, 0, 0, TimeSpan.Zero);
        using var host = new Host(now);
        var earlier = await host.AdoptLongerPairAsync(400).ConfigureAwait(false);
        var current = await host.AdoptLongerPairAsync(500).ConfigureAwait(false);
        var warm = await host.Operations.GetStatusAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("verified", warm.ManagedPair!.State);
        Assert.AreEqual(current.History[0].LeapSecondsSha256, warm.LeapSecondsSha256);
        Assert.IsTrue(warm.CoversNow);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, (await PlanAsync(host, now).ConfigureAwait(false)).Outcome);
        if (damageCurrent)
            await File.WriteAllTextAsync(Path.Combine(host.Store.PairDirectory(current.Pair), CameraAgentTimeDataSources.EarthOrientationFile),
                "damaged", TestContext.CancellationToken).ConfigureAwait(false);

        var eop = outcome == "not-newer" ? RapidOn(BundledEop, now.AddHours(-18)) : BundledEop;
        host.Client.Serve(CameraAgentTimeDataSources.EarthOrientationUri, eop);
        host.Client.Serve(CameraAgentTimeDataSources.EarthOrientationChecksumsUri, Checksums(eop));
        host.Client.Serve(CameraAgentTimeDataSources.LeapSecondsUri, Republished(BundledLeap, outcome == "not-newer" ? 10 : 800));
        if (outcome == "rejected")
            host.Client.Serve(CameraAgentTimeDataSources.EarthOrientationChecksumsUri, Checksums(Encoding.ASCII.GetBytes("different")));
        if (outcome == "failed") host.Store.BeforeCommit = () => throw new IOException("disk full");
        var downloads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Client.Gate = downloads.Task;
        var refresh = host.Job.ExecuteAsync(manual: true, TestContext.CancellationToken).AsTask();
        await host.Client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken).ConfigureAwait(false);
        try
        {
            // Concurrent reads during refresh keep using the intact, immutable data already loaded in memory.
            await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
            {
                Assert.IsTrue((await host.Operations.GetStatusAsync(TestContext.CancellationToken).ConfigureAwait(false)).CoversNow);
                Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, (await PlanAsync(host, now).ConfigureAwait(false)).Outcome);
            })).ConfigureAwait(false);
        }
        finally { downloads.SetResult(); }
        var execution = await refresh.ConfigureAwait(false);
        var expectedCode = outcome switch
        {
            "not-newer" => CameraAgentTimeDataRefreshJob.NotNewerReasonCode,
            "rejected" => CameraAgentTimeDataRefreshJob.ChecksumMismatchReasonCode,
            "failed" => CameraAgentTimeDataRefreshJob.StorageFailedReasonCode,
            _ => CameraAgentTimeDataRefreshJob.AdoptedReasonCode
        };
        StringAssert.StartsWith(execution.Detail, expectedCode, StringComparison.Ordinal);
        var pointer = (await host.Store.ReadPointerAsync(TestContext.CancellationToken).ConfigureAwait(false))!;
        if (outcome != "adopted") Assert.AreEqual(current.Pair, pointer.Pair, "non-adoption leaves the pointer identity unchanged");
        var invalidCurrent = damageCurrent && outcome != "adopted";
        var active = invalidCurrent ? earlier.Pair : pointer.Pair;
        var hash = invalidCurrent ? earlier.History[0].LeapSecondsSha256 : pointer.History[0].LeapSecondsSha256;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            var status = await host.Operations.GetStatusAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(invalidCurrent ? "invalid" : "verified", status.ManagedPair!.State);
            if (invalidCurrent) Assert.IsNull(status.ManagedPair.CoverageEndUtc, "damaged current data has no verified coverage");
            else Assert.IsNotNull(status.ManagedPair.CoverageEndUtc);
            Assert.IsTrue(status.CoversNow);
            Assert.IsFalse(status.Expired);
            Assert.AreEqual("managed", status.ActiveSource);
            Assert.AreEqual(hash, status.LeapSecondsSha256);
            var resolution = await host.Source.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(active, resolution.ActiveManagedPair);
            var plan = await PlanAsync(host, now).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, plan.Outcome);
            Assert.AreEqual(hash, plan.LeapSecondsSha256);
        })).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task TimeSourceUsesTheIntactPredecessorWhenTheCurrentPairIsDamagedAsync()
    {
        var now = new DateTimeOffset(2027, 7, 2, 18, 0, 0, TimeSpan.Zero);
        using var host = new Host(now);
        var earlier = await host.AdoptLongerPairAsync(400).ConfigureAwait(false);
        var current = await host.AdoptLongerPairAsync(500).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(host.Store.PairDirectory(current.Pair), CameraAgentTimeDataSources.EarthOrientationFile),
                "damaged", TestContext.CancellationToken).ConfigureAwait(false);

        var resolution = await host.Source.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(earlier.Pair, resolution.ActiveManagedPair);
        Assert.AreEqual(CameraAgentManagedTimeDataState.Invalid, resolution.Managed, "the current adoption is still reported as damaged");
        Assert.AreEqual(CameraAgentOrbitalTimeDataProblem.None, resolution.Problem);
        Assert.AreEqual(CameraAgentOrbitalTimeDataSource.Managed, resolution.Source);
        Assert.IsTrue(CameraAgentPinnedOrbitalTimeSource.Covers(resolution.Data!, new(now, now)));
        var status = await host.Operations.GetStatusAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsTrue(status.CoversNow);
        Assert.IsFalse(status.Expired);
        Assert.AreEqual("invalid", status.ManagedPair!.State);
        Assert.IsNull(status.ManagedPair.CoverageEndUtc, "an invalid current pair has no verified coverage");
        Assert.AreEqual(earlier.History[0].LeapSecondsSha256, status.LeapSecondsSha256);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, (await PlanAsync(host, now).ConfigureAwait(false)).Outcome);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task StoreRetainsAnIntactPairCoveringNowInsteadOfADamagedOrUnusableCurrentPairAsync(bool damageCurrent, bool currentHasGap)
    {
        var now = new DateTimeOffset(2027, 7, 2, 18, 0, 0, TimeSpan.Zero);
        using var host = new Host(now);
        var earlier = await host.AdoptLongerPairAsync(400).ConfigureAwait(false);
        var rapid = RapidOn(BundledEop, now.AddHours(-18));
        var current = await host.Store.AdoptAsync(Candidate(currentHasGap ? rapid : BundledEop, Republished(BundledLeap, 500)),
            now, TestContext.CancellationToken).ConfigureAwait(false);
        if (damageCurrent)
            await File.WriteAllTextAsync(Path.Combine(host.Store.PairDirectory(current.Pair), CameraAgentTimeDataSources.EarthOrientationFile),
                "damaged", TestContext.CancellationToken).ConfigureAwait(false);

        var adopted = await host.Store.AdoptAsync(Candidate(rapid, Republished(BundledLeap, 800)), now,
            TestContext.CancellationToken).ConfigureAwait(false);
        var intact = damageCurrent || currentHasGap ? earlier : current;
        Assert.AreEqual(intact.Pair, adopted.Previous, "the retained reference must keep the intact pair covering now");
        Assert.IsTrue(Directory.Exists(host.Store.PairDirectory(intact.Pair)));
        Assert.AreEqual(3, adopted.History.Count, "retention does not discard adoption history");
        var persisted = (await host.Store.ReadPointerAsync(TestContext.CancellationToken).ConfigureAwait(false))!;
        Assert.AreEqual(adopted.Pair, persisted.Pair);
        Assert.AreEqual(intact.Pair, persisted.Previous);
        CollectionAssert.AreEqual(adopted.History.ToArray(), persisted.History.ToArray());
        var resolution = await host.Source.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(intact.Pair, resolution.ActiveManagedPair);
        Assert.IsTrue((await host.Operations.GetStatusAsync(TestContext.CancellationToken).ConfigureAwait(false)).CoversNow);
    }

    [TestMethod]
    public async Task TheStatusSaysSoWhenNoVerifiedPairCoversTheCurrentTimeAndPlanningFailsAsync()
    {
        // The committed snapshot, retrieved after the 2026-10-21 update, is the only pair: it cannot evaluate the rest of that day.
        var day = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
        using var host = new Host(day.AddHours(18));
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.TimeInputsUnavailable, (await PlanAsync(host, host.Clock.Now).ConfigureAwait(false)).Outcome);
        var status = await host.Operations.GetStatusAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("bundled", status.ActiveSource);
        Assert.AreEqual("none", status.Problem);
        Assert.IsFalse(status.Expired);
        Assert.IsFalse(status.CoversNow, "the status never reads current while planning from now fails");

        // A pair adopted now cannot evaluate the rest of the day either, so nothing changes until 00:00Z.
        var later = Republished(BundledLeap, 400);
        host.Client.Serve(CameraAgentTimeDataSources.LeapSecondsUri, later);
        StringAssert.StartsWith((await host.Job.ExecuteAsync(manual: false, TestContext.CancellationToken).ConfigureAwait(false)).Detail,
            CameraAgentTimeDataRefreshJob.AdoptedReasonCode, StringComparison.Ordinal);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.TimeInputsUnavailable, (await PlanAsync(host, host.Clock.Now).ConfigureAwait(false)).Outcome);
        Assert.IsFalse((await host.Operations.GetStatusAsync(TestContext.CancellationToken).ConfigureAwait(false)).CoversNow);

        // From 00:00Z it covers the day and takes over; the status and planning agree.
        host.Clock.Now = day.AddDays(1);
        var plan = await PlanAsync(host, host.Clock.Now).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, plan.Outcome);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(later)), plan.LeapSecondsSha256);
        status = await host.Operations.GetStatusAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("managed", status.ActiveSource);
        Assert.IsTrue(status.CoversNow);
        Assert.AreEqual(plan.LeapSecondsSha256, status.LeapSecondsSha256);
    }

    [TestMethod]
    [DataRow(CameraAgentManagedTimeDataStore.PointerFile, "[]")]
    [DataRow(CameraAgentManagedTimeDataStore.PointerFile, "\"x\"")]
    [DataRow(CameraAgentManagedTimeDataStore.PointerFile, "history")]
    [DataRow(CameraAgentManagedTimeDataStore.StateFile, "[]")]
    [DataRow(CameraAgentManagedTimeDataStore.StateFile, "\"x\"")]
    public async Task ANonObjectPointerOrRecordIsInvalidDataThatPlanningRefreshAndStatusHandleAsync(string file, string shape)
    {
        using var host = new Host();
        var adopted = await host.AdoptLongerPairAsync().ConfigureAwait(false);
        await host.Store.WriteStateAsync(new(Start.AddDays(-2), false, CameraAgentTimeDataRefreshOutcome.Adopted,
            CameraAgentTimeDataRefreshJob.AdoptedReasonCode, string.Empty, Start.AddDays(-2), Start.AddDays(-2)),
            TestContext.CancellationToken).ConfigureAwait(false);
        var path = Path.Combine(host.Store.RootDirectory, file);
        var damaged = shape;
        if (shape == "history")
        {
            // A well-formed file whose history holds a number instead of an adoption.
            var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.CancellationToken).ConfigureAwait(false))!;
            node["history"] = new System.Text.Json.Nodes.JsonArray(1);
            damaged = node.ToJsonString();
        }
        await File.WriteAllTextAsync(path, damaged, TestContext.CancellationToken).ConfigureAwait(false);

        if (file == CameraAgentManagedTimeDataStore.PointerFile)
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await host.Store.ReadPointerAsync(TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            // Planning falls back to the bundled pair and reports the managed pair as invalid.
            var resolution = await host.Source.ResolveAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentOrbitalTimeDataSource.Bundled, resolution.Source);
            Assert.AreEqual(CameraAgentOrbitalTimeDataProblem.None, resolution.Problem);
            Assert.AreEqual(CameraAgentManagedTimeDataState.Invalid, resolution.Managed);
            Assert.IsNotNull(resolution.Data);
            // The status stays readable (the endpoint would otherwise answer 503).
            var status = await host.Operations.GetStatusAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("bundled", status.ActiveSource);
            Assert.IsNull(status.ManagedPair);
            // The next refresh replaces the damaged pointer.
            var execution = await host.Job.ExecuteAsync(manual: false, TestContext.CancellationToken).ConfigureAwait(false);
            StringAssert.StartsWith(execution.Detail, CameraAgentTimeDataRefreshJob.AdoptedReasonCode, StringComparison.Ordinal);
            Assert.IsNotNull(await host.Store.ReadPointerAsync(TestContext.CancellationToken).ConfigureAwait(false));
        }
        else
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await host.Store.ReadStateAsync(TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            var status = await host.Operations.GetStatusAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("unreadable", status.Refresh.Outcome, "the status stays readable (the endpoint would otherwise answer 503)");
            Assert.AreEqual("managed", status.ActiveSource);
            // The next attempt replaces the damaged record and records its own failure.
            host.Client.Fail(CameraAgentTimeDataSources.EarthOrientationUri, CameraAgentTimeDataClient.TransportReasonCode);
            var execution = await host.Job.ExecuteAsync(manual: false, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(LocalAutomationRunOutcome.Failed, execution.Outcome, execution.Detail);
            var state = (await host.Store.ReadStateAsync(TestContext.CancellationToken).ConfigureAwait(false))!;
            Assert.AreEqual(CameraAgentTimeDataRefreshOutcome.Failed, state.Outcome);
            Assert.AreEqual(CameraAgentTimeDataClient.TransportReasonCode, state.ReasonCode);
            Assert.AreEqual(adopted.Pair, (await host.Store.ReadPointerAsync(TestContext.CancellationToken).ConfigureAwait(false))!.Pair);
        }
    }

    [TestMethod]
    public async Task TheTimeDataHandlerFollowsNoRedirectAndTheClientSendsEachRequestOnceAsync()
    {
        var handler = CameraAgentTimeDataSources.CreatePrimaryHandler();
        using (handler)
        {
            Assert.IsInstanceOfType<SocketsHttpHandler>(handler);
            var sockets = (SocketsHttpHandler)handler;
            Assert.IsFalse(sockets.AllowAutoRedirect, "no redirect is followed");
            Assert.IsFalse(sockets.UseCookies, "no cookies");
            Assert.AreEqual(DecompressionMethods.None, sockets.AutomaticDecompression, "no automatic decompression");
            Assert.IsNull(sockets.Credentials, "no credentials");
            Assert.IsFalse(sockets.PreAuthenticate);
        }

        // The client itself never retries: a redirect or a transport failure is one request.
        foreach (var respond in new Func<HttpResponseMessage>[]
        {
            () => Response(HttpStatusCode.MovedPermanently, []),
            () => throw new HttpRequestException("refused")
        })
        {
            var requests = 0;
            var download = await FetchAsync(() => { requests++; return respond(); }, CameraAgentTimeDataSources.LeapSecondsUri, 3).ConfigureAwait(false);
            Assert.IsNull(download.Bytes);
            Assert.AreEqual(1, requests, "exactly one request");
        }
    }

    [TestMethod]
    public async Task TheRegisteredTimeDataPipelineHasNothingInFrontOfTheNoRedirectHandlerAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-time-data-http-" + Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }));
        services.AddCameraAgentSatellitePlanning(new ConfigurationBuilder().Build());
        services.AddCameraAgentTimeDataRefresh();
        var provider = services.BuildServiceProvider();
        await using var disposal = provider.ConfigureAwait(false);

        var chain = new List<HttpMessageHandler>();
        for (HttpMessageHandler? next = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(CameraAgentTimeDataSources.HttpClientName);
             next is not null; next = (next as DelegatingHandler)?.InnerHandler)
            chain.Add(next);
        // Only the factory's own lifetime wrapper precedes the primary handler: no retry, resilience, redirect or logging handler.
        var names = string.Join(" -> ", chain.Select(static handler => handler.GetType().FullName));
        Assert.HasCount(2, chain, names);
        Assert.AreEqual(typeof(IHttpClientFactory).Assembly, chain[0].GetType().Assembly, names);
        Assert.IsInstanceOfType<SocketsHttpHandler>(chain[1], names);
        Assert.IsFalse(((SocketsHttpHandler)chain[1]).AllowAutoRedirect, "the registered primary handler follows no redirect");
        Assert.IsFalse(Directory.Exists(root));
    }

    /// <summary>Plans one hour from <paramref name="startUtc"/> with the real loader and planner, returning the leap hash it planned with.</summary>
    private static async Task<(CameraAgentSatellitePlanningOutcome Outcome, string? LeapSecondsSha256)> PlanAsync(Host host, DateTimeOffset startUtc)
    {
        var inputs = new PlanningTestInputs();
        var planner = new CameraAgentSatellitePlanner(inputs.Sky, inputs.Configuration, host.Source, inputs.Engine, host.Options, host.Clock,
            inputs.Snapshots);
        var scene = await inputs.Sky.ProjectAsync(startUtc, CancellationToken.None).ConfigureAwait(false);
        var response = await planner.SearchAsync(new(startUtc, startUtc.AddHours(1), "25544", 0, scene.Observer.CanonicalSha256,
            scene.Geometry.RigProfileHashSha256), CancellationToken.None).ConfigureAwait(false);
        return (response.Outcome, response.Outcome == CameraAgentSatellitePlanningOutcome.Ready
            ? inputs.Engine.LastSearch!.Request.Model.LeapSecondsSha256 : null);
    }

    /// <summary>The file as published before <paramref name="day"/>'s update, when that day's rapid row was still predicted.</summary>
    private static byte[] PredictedOn(byte[] eop, DateTimeOffset day)
    {
        var mjd = (day - new DateTimeOffset(1858, 11, 17, 0, 0, 0, TimeSpan.Zero)).Days.ToString(CultureInfo.InvariantCulture) + ".00";
        var lines = Encoding.ASCII.GetString(eop).Split('\n');
        var changed = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length < 68 || lines[i].Substring(7, 8) != mjd) continue;
            var row = lines[i].ToCharArray();
            row[16] = 'P';
            row[57] = 'P';
            lines[i] = new string(row);
            changed++;
        }
        Assert.AreEqual(1, changed, "the day's row");
        return Encoding.ASCII.GetBytes(string.Join('\n', lines));
    }

    private static byte[] RapidOn(byte[] eop, DateTimeOffset day)
    {
        var predicted = PredictedOn(eop, day);
        var mjd = (day - new DateTimeOffset(1858, 11, 17, 0, 0, 0, TimeSpan.Zero)).Days.ToString(CultureInfo.InvariantCulture) + ".00";
        var lines = Encoding.ASCII.GetString(predicted).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length < 68 || lines[i].Substring(7, 8) != mjd) continue;
            var row = lines[i].ToCharArray();
            row[16] = 'I';
            row[57] = 'I';
            lines[i] = new string(row);
        }
        return Encoding.ASCII.GetBytes(string.Join('\n', lines));
    }

    /// <summary>The leap list with its expiry moved <paramref name="days"/> later and its own #h recomputed, as IERS publishes it.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "Reproduces the provider's published SHA-1 #h for a test fixture.")]
    private static byte[] Republished(byte[] leap, int days)
    {
        var lines = Encoding.ASCII.GetString(WithLaterExpiry(leap, days)).Split('\n');
        var data = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.StartsWith("#$", StringComparison.Ordinal) || line.StartsWith("#@", StringComparison.Ordinal))
                data.Append(Squeeze(line[2..].Split('#')[0]));
            else if (!line.StartsWith('#'))
                data.Append(Squeeze(line.Split('#')[0]));
        }
        var hash = Convert.ToHexStringLower(SHA1.HashData(Encoding.ASCII.GetBytes(data.ToString())));
        var republished = Encoding.ASCII.GetBytes(string.Join('\n',
            lines.Select(line => line.StartsWith("#h", StringComparison.Ordinal) ? "#h\t" + hash : line)));
        Assert.AreEqual(hash, CameraAgentTimeDataSources.VerifiedLeapSecondsHash(republished), "the fixture carries a valid #h");
        return republished;

        static string Squeeze(string text) => string.Concat(text.Where(static c => c is not (' ' or '\t' or '\r')));
    }

    private static LocalAutomationDefinition Definition() => new(CameraAgentTimeDataAutomationAdapter.DefinitionId, "Time data", true,
        LocalAutomationTaskKind.TimeDataRefresh, CameraAgentTimeDataAutomationAdapter.Target, LocalAutomationTriggerKind.Periodic,
        CameraAgentTimeDataAutomationAdapter.IntervalSeconds, Start);

    private static byte[] Checksums(byte[] eop)
        => Encoding.ASCII.GetBytes($"{Convert.ToHexStringLower(SHA512.HashData(eop))}  {CameraAgentTimeDataSources.EarthOrientationFile}\n");

    private static CameraAgentTimeDataCandidate Candidate(byte[] eop, byte[] leap)
        => new(eop, null, Convert.ToHexStringLower(SHA512.HashData(eop)), leap, null, new string('0', 40), Start);

    /// <summary>The leap list with its expiry moved <paramref name="days"/> later; its own #h no longer matches.</summary>
    private static byte[] WithLaterExpiry(byte[] leap, int days = 400)
    {
        var lines = Encoding.ASCII.GetString(leap).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("#@", StringComparison.Ordinal)) continue;
            var value = long.Parse(lines[i][2..].Split('#')[0].Trim(), CultureInfo.InvariantCulture);
            lines[i] = "#@\t" + (value + (days * 86_400L)).ToString(CultureInfo.InvariantCulture);
        }
        return Encoding.ASCII.GetBytes(string.Join('\n', lines));
    }

    private static byte[] WithoutTerms(byte[] leap) => Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(leap)
        .Replace(CameraAgentTimeDataSources.LeapSecondsTerms, "Copyright reserved.", StringComparison.Ordinal));

    private static HttpResponseMessage Response(HttpStatusCode status, byte[] body, DateTimeOffset? modified = null)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        response.Content.Headers.LastModified = modified;
        return response;
    }

    private static Task<CameraAgentTimeDataDownload> FetchAsync(Func<HttpResponseMessage> respond, Uri uri, int maximum)
        => FetchAsync(_ => Task.FromResult(respond()), uri, maximum, TimeProvider.System, CancellationToken.None);

    private static async Task<CameraAgentTimeDataDownload> FetchAsync(Func<CancellationToken, Task<HttpResponseMessage>> respond, Uri uri,
        int maximum, TimeProvider clock, CancellationToken cancellationToken)
    {
        using var http = new HttpClient(new Handler(respond)) { Timeout = Timeout.InfiniteTimeSpan };
        return await new CameraAgentTimeDataClient(http, clock, "HVO.SkyMonitor.CameraAgent/1.0")
            .FetchAsync(uri, maximum, cancellationToken).ConfigureAwait(false);
    }

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(cancellationToken);
    }

    /// <summary>A body with no declared length that returns <paramref name="length"/> bytes.</summary>
    private sealed class UnsizedStream(int length) : Stream
    {
        private int _remaining = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(Math.Min(count, _remaining), 1);
            _remaining -= read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A fixed clock whose timers fire at once and record their delays.</summary>
    internal sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now = now;
        internal readonly List<TimeSpan> Delays = [];
        public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (Delays) Delays.Add(dueTime);
            return base.CreateTimer(callback, state, dueTime == Timeout.InfiniteTimeSpan ? dueTime : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    internal sealed class FakeClient : ICameraAgentTimeDataClient
    {
        private readonly Dictionary<Uri, CameraAgentTimeDataDownload> _responses = [];
        internal readonly List<Uri> Requests = [];
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? Gate;
        internal Action? OnFetch;

        internal FakeClient()
        {
            Serve(CameraAgentTimeDataSources.EarthOrientationUri, BundledEop);
            Serve(CameraAgentTimeDataSources.EarthOrientationChecksumsUri, Checksums(BundledEop));
            Serve(CameraAgentTimeDataSources.LeapSecondsUri, BundledLeap);
        }

        internal void Serve(Uri uri, byte[] bytes) => _responses[uri] = new(bytes, Host.Modified, null, "length=" + bytes.Length);
        internal void Fail(Uri uri, string code) => _responses[uri] = new(null, null, code, "status=refused");

        public async ValueTask<CameraAgentTimeDataDownload> FetchAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(uri);
            Entered.TrySetResult();
            OnFetch?.Invoke();
            if (Gate is { } gate) await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return _responses[uri];
        }
    }

    /// <summary>A store in a temporary directory, the real loader over the committed bundle, a fake client and a fixed clock.</summary>
    internal sealed class Host : IDisposable
    {
        internal static readonly DateTimeOffset Modified = new(2026, 10, 21, 17, 11, 48, TimeSpan.Zero);
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "hvo-time-data-" + Guid.NewGuid().ToString("N"));
        internal readonly Clock Clock;
        internal readonly PlanningTestInputs.Settings Options = new();
        internal readonly FakeClient Client = new();
        internal readonly CameraAgentManagedTimeDataStore Store;
        internal readonly CameraAgentPinnedOrbitalTimeSource Source;
        internal readonly CameraAgentTimeDataRefreshJob Job;
        internal readonly CameraAgentTimeDataOperations Operations;

        internal Host(DateTimeOffset? now = null)
        {
            Clock = new(now ?? Start);
            Options.CurrentValue.TimeDataRefreshEnabled = true;
            Store = new(Path.Combine(Root, "raw", CameraAgentManagedTimeDataStore.DirectoryName));
            Source = new(Options, CameraAgentPinnedOrbitalTimeSource.BundledDirectory, Store, Clock);
            Job = new(Store, Client, Options, Clock, NullLogger<CameraAgentTimeDataRefreshJob>.Instance);
            Operations = new(Source, Store, Job, Options, Clock);
        }

        /// <summary>Adopts the bundled EOP with a later leap expiry: a valid pair that covers planning longer than the bundle.</summary>
        internal ValueTask<CameraAgentManagedTimePointer> AdoptLongerPairAsync(int days = 400)
            => Store.AdoptAsync(Candidate(BundledEop, WithLaterExpiry(BundledLeap, days)), Clock.Now, CancellationToken.None);

        /// <summary>A bundle directory with the committed EOP and <paramref name="leap"/>, with matching provenance.</summary>
        internal string CopyBundle(byte[] leap)
        {
            var directory = Path.Combine(Root, "bundle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, CameraAgentTimeDataSources.EarthOrientationFile), BundledEop);
            File.WriteAllBytes(Path.Combine(directory, CameraAgentTimeDataSources.LeapSecondsFile), leap);
            var provenance = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(CameraAgentPinnedOrbitalTimeSource.BundledDirectory,
                CameraAgentPinnedOrbitalTimeSource.ProvenanceFileName)))!;
            provenance["leapSeconds"]!["sha256"] = Convert.ToHexString(SHA256.HashData(leap));
            File.WriteAllText(Path.Combine(directory, CameraAgentPinnedOrbitalTimeSource.ProvenanceFileName), provenance.ToJsonString());
            return directory;
        }

        public void Dispose()
        {
            Source.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
