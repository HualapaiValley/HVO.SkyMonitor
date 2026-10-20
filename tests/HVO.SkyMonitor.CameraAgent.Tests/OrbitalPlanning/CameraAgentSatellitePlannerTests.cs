using System.Globalization;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;

namespace HVO.SkyMonitor.CameraAgent.Tests.OrbitalPlanning;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentSatellitePlannerTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task MissingReaderSnapshotDisabledOrActualTimeInputsAreExplicitAsync()
    {
        var missingReader = new PlanningTestInputs(reader: false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.NoSnapshot,
            (await missingReader.Planner.SearchAsync(await missingReader.RequestAsync().ConfigureAwait(false), TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
        var input = new PlanningTestInputs();
        var request = await input.RequestAsync().ConfigureAwait(false);
        input.Snapshots.Current = null;
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.NoSnapshot, (await input.Planner.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
        input.Snapshots.Current = PlanningTestInputs.Snapshot();
        var readsBeforeUnavailableTime = input.Snapshots.Reads;
        var sky = new RecordingSkyMap(input.Sky);
        var planner = CreatePlanner(input, sky);
        input.Options.CurrentValue.Enabled = false;
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.TimeInputsUnavailable, (await planner.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
        Assert.AreEqual(readsBeforeUnavailableTime, input.Snapshots.Reads,
            "A disabled planner must not materialize the local GP population.");
        input.Options.CurrentValue.Enabled = true;
        input.Times.Current = null;
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.TimeInputsUnavailable, (await planner.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
        input.Times.Current = OrbitalTimeData.CreateNominalZeroDiagnostic(PlanningTestInputs.Bytes("time", "leap-seconds.list"));
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.TimeInputsUnavailable, (await planner.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
        Assert.AreEqual(readsBeforeUnavailableTime, input.Snapshots.Reads,
            "Unavailable actual time inputs must be rejected before materializing orbital data.");
        Assert.AreEqual(0, sky.Calls, "Unavailable actual time inputs must not invoke the sky projector.");
        Assert.AreEqual(0, input.Engine.Searches);
    }

    [TestMethod]
    public async Task ActualFrozenTimeAndSourceModelFactsStayPinnedWithoutFabricatedRetrievalAsync()
    {
        var input = new PlanningTestInputs();
        var request = await input.RequestAsync().ConfigureAwait(false);
        var response = await input.Planner.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, response.Outcome, response.Message);
        var view = response.Value!;
        var pass = view.Passes.Single().Pass;
        Assert.AreSame(input.Times.Current, input.Engine.BoundTime);
        Assert.AreEqual(input.Times.Current!.EarthOrientationSha256, pass.Model.EarthOrientationSha256);
        Assert.AreEqual(input.Times.Current.LeapSecondsSha256, pass.Model.LeapSecondsSha256);
        Assert.AreEqual(OrbitalPredictionModel.Propagator, pass.Model.Propagator);
        Assert.AreEqual("wgs84-ellipsoidal-assumed", pass.Model.ObserverHeightDatum);
        Assert.AreEqual(input.Snapshots.Current!.Manifest.Sources.Max(source => source.RetrievedUtc), view.MostRecentSourceRetrievedUtc);
        Assert.AreNotEqual(view.CreatedUtc, view.MostRecentSourceRetrievedUtc);
        Assert.AreEqual((request.StartUtc - pass.Lineage.EpochUtc).TotalHours, pass.Lineage.StartEpochAgeHours);
        Assert.AreNotEqual((request.StartUtc - view.MostRecentSourceRetrievedUtc!.Value).TotalHours, pass.Lineage.StartEpochAgeHours);
        Assert.AreEqual(RigProjectionContextFactory.Create(input.Configuration.Value.Rig), view.Context.RigProjection);
        Assert.AreEqual("America/Los_Angeles", view.Context.Observer.TimeZoneId);
        Assert.IsTrue(view.Context.Observer.EffectiveAtInstant);
        Assert.AreEqual(1, view.MatchingObjects);
        Assert.AreEqual(1, view.SelectedObjects);
    }

    [TestMethod]
    public async Task InvalidWindowElevationFilterAndExpectedContextNeverReachTheEngineAsync()
    {
        var input = new PlanningTestInputs();
        var valid = await input.RequestAsync().ConfigureAwait(false);
        foreach (var invalid in new[]
        {
            valid with { EndUtc = valid.StartUtc }, valid with { EndUtc = valid.StartUtc.AddHours(24).AddTicks(1) },
            valid with { StartUtc = valid.StartUtc.ToOffset(TimeSpan.FromHours(1)) },
            valid with { MinimumElevationDegrees = double.NaN }, valid with { MinimumElevationDegrees = 90 },
            valid with { ExpectedRigSha256 = "bad" }, valid with { ObjectFilter = "" },
            valid with { ObjectFilter = new string('x', 257) }, valid with { ObjectFilter = "025544" },
            valid with { ObjectFilter = "0" }, valid with { ObjectFilter = "25544\n" },
            valid with { ObjectFilter = string.Join(',', Enumerable.Range(1, 33)) }
        })
        {
            var response = await input.Planner.SearchAsync(invalid, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Invalid, response.Outcome, response.Message);
        }
        Assert.AreEqual(0, input.Snapshots.Reads, "Invalid bounded inputs must not materialize orbital data.");
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.ContextChanged,
            (await input.Planner.SearchAsync(valid with { ExpectedObserverSha256 = new string('A', 64) }, TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.ContextChanged,
            (await input.Planner.SearchAsync(valid with { ExpectedRigSha256 = new string('A', 64) }, TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
        Assert.AreEqual(0, input.Engine.Searches);
    }

    [TestMethod]
    public async Task GappedExpiredOutsideCoverageAndLeapCrossingInputsAreWithheldAsync()
    {
        var input = new PlanningTestInputs();
        var request = await input.RequestAsync().ConfigureAwait(false);
        var sky = new RecordingSkyMap(input.Sky);
        var planner = CreatePlanner(input, sky);
        var lines = Encoding.ASCII.GetString(PlanningTestInputs.Bytes("time", "predicted-2026.finals2000A"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        input.Times.Current = OrbitalTimeData.FromFinals2000A(Encoding.ASCII.GetBytes(string.Join('\n', lines.Where((_, index) => index != 2))),
            PlanningTestInputs.Bytes("time", "leap-seconds.list"));
        // October 19 noon needs the October 19/20 daily bracket; remove October 20.
        var result = await planner.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.TimeInputsUnavailable, result.Outcome, result.Message);
        input.Times.Current = PlanningTestInputs.TimeData();
        // Use the actual retained table with only its expiration changed, preserving valid consecutive offsets.
        var leapLines = Encoding.ASCII.GetString(PlanningTestInputs.Bytes("time", "leap-seconds.list")).Split('\n');
        var expiration = (long)(PlanningTestInputs.Start - new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds;
        var expiredLeaps = string.Join('\n', leapLines.Select(line => line.StartsWith("#@", StringComparison.Ordinal) ?
            "#@ " + expiration.ToString(CultureInfo.InvariantCulture) : line));
        input.Times.Current = OrbitalTimeData.FromFinals2000A(PlanningTestInputs.Bytes("time", "predicted-2026.finals2000A"), Encoding.ASCII.GetBytes(expiredLeaps));
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.TimeInputsUnavailable,
            (await planner.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
        input.Times.Current = PlanningTestInputs.TimeData();
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.TimeInputsUnavailable,
            (await planner.SearchAsync(request with { StartUtc = request.StartUtc.AddDays(10), EndUtc = request.EndUtc.AddDays(10) }, TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
        input.Times.Current = OrbitalTimeData.FromFinals2000A(PlanningTestInputs.Bytes("time", "leap-2016.finals2000A"), PlanningTestInputs.Bytes("time", "leap-seconds.list"));
        var start = new DateTimeOffset(2016, 12, 31, 23, 59, 30, TimeSpan.Zero);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.TimeInputsUnavailable,
            (await planner.SearchAsync(request with { StartUtc = start, EndUtc = start.AddMinutes(1) }, TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
        Assert.AreEqual(0, input.Snapshots.Reads, "Unsupported original query times must be rejected before reading the snapshot.");
        Assert.AreEqual(0, sky.Calls, "Unsupported original query times must be rejected before sky projection.");
        Assert.AreEqual(0, input.Engine.Searches);
    }

    [TestMethod]
    public async Task SupportedQueryStillValidatesTheSelectedEpochAfterSnapshotSelectionAsync()
    {
        var input = new PlanningTestInputs();
        var request = await input.RequestAsync().ConfigureAwait(false);
        input.Times.Current = OrbitalTimeData.FromFinals2000A(PlanningTestInputs.Bytes("time", "leap-2016.finals2000A"),
            PlanningTestInputs.Bytes("time", "leap-seconds.list"));
        var start = new DateTimeOffset(2017, 1, 1, 1, 0, 0, TimeSpan.Zero);
        request = request with { StartUtc = start, EndUtc = start.AddHours(1) };
        var snapshot = input.Snapshots.Current!;
        var record = snapshot.Records.Single(item => item.ObjectIdentity.CatalogNumber == "25544");
        input.Snapshots.Current = OrbitalSnapshot.Create([OrbitalContract.CreateRecord(record with
        {
            RecordSha256 = "",
            EpochUtc = start.AddHours(-2)
        })], snapshot.Manifest.Sources, snapshot.Manifest.Coverage);
        var sky = new RecordingSkyMap(input.Sky);
        var response = await CreatePlanner(input, sky).SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.TimeInputsUnavailable, response.Outcome, response.Message);
        Assert.IsNull(response.Value);
        StringAssert.Contains(response.Message, "selected epochs", StringComparison.Ordinal);
        Assert.AreEqual(1, input.Snapshots.Reads, "The query is supported; the selected epoch is only known after loading the snapshot.");
        Assert.AreEqual(1, sky.Calls);
        Assert.AreEqual(0, input.Engine.Searches);
        Assert.IsNull(input.Engine.BoundTime, "Unsupported selected epochs must not create the numerical engines.");
    }

    [TestMethod]
    public async Task MatchingOrderIsDeterministicBoundedAndNameMatchingDoesNotInferObjectStatusAsync()
    {
        var input = new PlanningTestInputs();
        var snapshot = input.Snapshots.Current!;
        var original = snapshot.Records[0];
        var records = Enumerable.Range(1, 40).Reverse().Select(index => OrbitalContract.CreateRecord(original with
        {
            RecordSha256 = "",
            ObjectIdentity = new("norad", (100000 + index).ToString(CultureInfo.InvariantCulture)),
            ObjectName = "MixedCase marker",
            SourceAssertedStatus = "unknown"
        }));
        input.Snapshots.Current = OrbitalSnapshot.Create(records, snapshot.Manifest.Sources, snapshot.Manifest.Coverage);
        var request = await input.RequestAsync("mixedcase").ConfigureAwait(false);
        var result = await input.Planner.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, result.Outcome, result.Message);
        Assert.AreEqual(40, result.Value!.MatchingObjects); Assert.AreEqual(32, result.Value.SelectedObjects);
        CollectionAssert.AreEqual(Enumerable.Range(1, 32).Select(index => (100000 + index).ToString(CultureInfo.InvariantCulture)).ToArray(),
            input.Engine.LastSearch!.Request.Objects.Select(identity => identity.CatalogNumber).ToArray());
        StringAssert.Contains(result.Message, "truncated", StringComparison.Ordinal);
        var selected = input.Engine.LastSearch.Request.Objects.ToArray();
        var again = await input.Planner.SearchAsync(request with { ObjectFilter = "MIXEDCASE" }, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, again.Outcome, again.Message);
        CollectionAssert.AreEqual(selected, input.Engine.LastSearch.Request.Objects.ToArray());
        Assert.AreNotEqual(result.Value.SearchIdentitySha256, again.Value!.SearchIdentitySha256);
        var searchesBeforeAbsentFilter = input.Engine.Searches;
        var absent = await input.Planner.SearchAsync(request with { ObjectFilter = "missing-name" }, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.NoMatches, absent.Outcome);
        Assert.IsNull(absent.Value);
        StringAssert.Contains(absent.Message, "matching 0; selected 0", StringComparison.Ordinal);
        StringAssert.Contains(absent.Message, "in this snapshot", StringComparison.Ordinal);
        StringAssert.Contains(absent.Message, "does not establish global absence", StringComparison.Ordinal);
        Assert.AreEqual(searchesBeforeAbsentFilter, input.Engine.Searches);
    }

    [TestMethod]
    public async Task PartialAndUnavailableNumericalResultsRemainIndependentFromFacadeAvailabilityAsync()
    {
        var input = new PlanningTestInputs();
        input.Engine.Search = (query, _) => ValueTask.FromResult(PlanningTestInputs.Engines.Result(query, partial: true));
        var result = await input.Planner.SearchAsync(await input.RequestAsync().ConfigureAwait(false), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, result.Outcome, result.Message);
        Assert.AreEqual(OrbitalExecutionStatus.Partial, result.Value!.Prediction.Status);
        Assert.AreEqual(OrbitalPredictionReason.EvaluationBudget, result.Value.Prediction.Reason);
        Assert.HasCount(1, result.Value.Passes);
        var detail = await input.Planner.GetArcAsync(result.Value.SearchIdentitySha256, result.Value.Passes[0].Pass.ProductSha256, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, detail.Outcome, detail.Message);
        Assert.AreEqual(OrbitalExecutionStatus.Unavailable, detail.Value!.Prediction.Status);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready,
            (await input.Planner.GetArcAsync(result.Value.SearchIdentitySha256, result.Value.Passes[0].Pass.ProductSha256,
                TestContext.CancellationToken).ConfigureAwait(false)).Outcome,
            "Numerically unavailable products must not invalidate the retained scientific context.");
    }

    [TestMethod]
    public async Task StaleSelectionRequiresExplicitDiagnosticOptionAndExpiredElementsAreNotFreshenedByRetrievalAsync()
    {
        var input = new PlanningTestInputs();
        var request = await input.RequestAsync().ConfigureAwait(false);
        request = request with { StartUtc = request.StartUtc.AddDays(3), EndUtc = request.EndUtc.AddDays(3) };
        var withheld = await input.Planner.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Unavailable, withheld.Value!.Prediction.Status, withheld.Message);
        input.Options.CurrentValue.AllowStaleDiagnostics = true;
        var diagnostic = await input.Planner.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalElementFreshness.Stale, diagnostic.Value!.Passes.Single().Pass.Lineage.Freshness, diagnostic.Message);
        Assert.IsTrue(input.Engine.LastSearch!.Request.SelectionPolicy.AllowStaleDiagnostics);
        Assert.AreEqual(OrbitalPredictionUse.Diagnostic, input.Engine.LastSearch.Request.Use);
        var snapshot = input.Snapshots.Current!;
        input.Snapshots.Current = OrbitalSnapshot.Create(snapshot.Records.Select(record => OrbitalContract.CreateRecord(record with
        {
            RecordSha256 = "",
            EpochUtc = record.EpochUtc.AddDays(-8)
        })), snapshot.Manifest.Sources, snapshot.Manifest.Coverage);
        var expired = await input.Planner.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, expired.Outcome, expired.Message);
        Assert.AreEqual(OrbitalPredictionReason.SelectionWithheld, expired.Value!.Prediction.Reason);
        Assert.HasCount(0, expired.Value.Passes);
    }

    [TestMethod]
    public async Task CallerCircuitIsNotUsedAndBusyCancellationDoNotAbandonOrQueueWorkAsync()
    {
        var input = new PlanningTestInputs();
        var request = await input.RequestAsync().ConfigureAwait(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = false;
        var customContext = new SynchronizationContext();
        input.Engine.Search = async (query, token) =>
        {
            Assert.IsNull(SynchronizationContext.Current);
            entered.SetResult();
            try { await release.Task.WaitAsync(token).ConfigureAwait(false); return PlanningTestInputs.Engines.Result(query); }
            finally { exited = true; }
        };
        using var cancellation = new CancellationTokenSource();
        var previous = SynchronizationContext.Current;
        Task<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassSearchView>> running;
        try
        {
            SynchronizationContext.SetSynchronizationContext(customContext);
            running = input.Planner.SearchAsync(request, cancellation.Token).AsTask();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await entered.Task.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Busy,
            (await input.Planner.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Busy,
            (await input.Planner.GetArcAsync(new string('A', 64), new string('B', 64), TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await running.ConfigureAwait(false)).ConfigureAwait(false);
        Assert.IsTrue(exited); Assert.AreEqual(1, input.Engine.Searches);
        input.Engine.Search = null;
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready,
            (await input.Planner.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
        var searches = input.Engine.Searches;
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await input.Planner.SearchAsync(request, cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(searches, input.Engine.Searches);
    }

    [TestMethod]
    public async Task ExactParentAndOriginalSearchIntervalAreRetainedUntilTtlOrDeterministicEvictionAsync()
    {
        var input = new PlanningTestInputs();
        var request = await input.RequestAsync().ConfigureAwait(false);
        var views = new List<CameraAgentSatellitePassSearchView>();
        for (var index = 0; index < 5; index++)
        {
            var result = await input.Planner.SearchAsync(request with { MinimumElevationDegrees = index }, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, result.Outcome, result.Message);
            views.Add(result.Value!);
            input.Time.Now = input.Time.Now.AddSeconds(1);
        }
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Expired, (await input.Planner.GetArcAsync(views[0].SearchIdentitySha256,
            views[0].Passes[0].Pass.ProductSha256, TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
        var view = views[1];
        var detail = await input.Planner.GetArcAsync(view.SearchIdentitySha256, view.Passes[0].Pass.ProductSha256, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, detail.Outcome, detail.Message);
        Assert.AreEqual(view.Passes[0].Pass.ProductSha256, input.Engine.LastArc!.Pass.ProductSha256);
        Assert.AreEqual(new OrbitalUtcInterval(request.StartUtc, request.EndUtc), input.Engine.LastArc.SearchQuery.Request.Interval);
        Assert.AreEqual(1, input.Engine.LastArc.SearchQuery.MinimumElevationDegrees);
        Assert.AreEqual(CameraAgentSatellitePlanner.ArcBounds, input.Engine.LastArc.Bounds);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Invalid, (await input.Planner.GetArcAsync(view.SearchIdentitySha256,
            views[2].Passes[0].Pass.ProductSha256, TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
        input.Time.Now = view.ExpiresUtc;
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Expired, (await input.Planner.GetArcAsync(view.SearchIdentitySha256,
            view.Passes[0].Pass.ProductSha256, TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
        var repeated = await input.Planner.SearchAsync(request with { MinimumElevationDegrees = 1 }, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, repeated.Outcome, repeated.Message);
        Assert.AreNotEqual(view.SearchIdentitySha256, repeated.Value!.SearchIdentitySha256);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Expired, (await input.Planner.GetArcAsync(view.SearchIdentitySha256,
            view.Passes[0].Pass.ProductSha256, TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
    }

    [TestMethod]
    public async Task DetailUsesOriginalSelectionEvenWhenAParentIntervalWouldChooseAnotherRecordAsync()
    {
        var input = new PlanningTestInputs();
        var original = input.Snapshots.Current!;
        var iss = original.Records.Single(record => record.ObjectIdentity.CatalogNumber == "25544");
        input.Snapshots.Current = OrbitalSnapshot.Create(
            [OrbitalContract.CreateRecord(iss with { RecordSha256 = "", EpochUtc = PlanningTestInputs.Start.AddMinutes(1) }),
             OrbitalContract.CreateRecord(iss with { RecordSha256 = "", EpochUtc = PlanningTestInputs.Start.AddMinutes(30) })],
            original.Manifest.Sources, original.Manifest.Coverage);
        input.Engine.Search = (query, _) =>
        {
            var pass = PlanningTestInputs.Engines.Result(query).Products.Single();
            pass = OrbitalPredictionJson.CreatePass(pass with
            {
                PassInterval = new(PlanningTestInputs.Start.AddMinutes(1), PlanningTestInputs.Start.AddMinutes(5)),
                PeakUtc = PlanningTestInputs.Start.AddMinutes(3),
                StartBoundary = OrbitalPassBoundary.ResolvedCrossing,
                EndBoundary = OrbitalPassBoundary.ResolvedCrossing
            });
            return ValueTask.FromResult(new OrbitalPredictionResult<OrbitalPassSummaryV1>(query,
                OrbitalExecutionStatus.Complete, OrbitalPredictionReason.None, 1, 0, 0, [pass], []));
        };
        var result = await input.Planner.SearchAsync(await input.RequestAsync().ConfigureAwait(false), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, result.Outcome, result.Message);
        var parent = result.Value!.Passes.Single().Pass;
        var searchQuery = input.Engine.LastSearch!;
        var narrowed = OrbitalRecordSelector.Select(searchQuery.Request.Snapshot, parent.Lineage.ObjectIdentity,
            parent.PassInterval, searchQuery.Request.SelectionPolicy).SelectedRecord!;
        Assert.AreNotEqual(narrowed.RecordSha256, parent.Lineage.RecordSha256);
        var detail = await input.Planner.GetArcAsync(result.Value.SearchIdentitySha256, parent.ProductSha256, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, detail.Outcome, detail.Message);
        Assert.AreSame(searchQuery, input.Engine.LastArc!.SearchQuery);
        Assert.AreEqual(parent.Lineage.RecordSha256, input.Engine.LastArc.Pass.Lineage.RecordSha256);
    }

    [TestMethod]
    public async Task RetentionByteCapEvictsOlderSessionsAndRejectsAnOversizedCandidateWithoutLosingEarlierSearchAsync()
    {
        var input = new PlanningTestInputs(); var snapshot = input.Snapshots.Current!;
        input.Snapshots.Current = OrbitalSnapshot.Create(snapshot.Records,
            snapshot.Manifest.Sources.Select(source => source with { RawLengthBytes = 32 * 1024 * 1024 }), snapshot.Manifest.Coverage);
        var request = await input.RequestAsync().ConfigureAwait(false);
        var views = new List<CameraAgentSatellitePassSearchView>();
        for (var index = 0; index < 4; index++)
        {
            var result = await input.Planner.SearchAsync(request with { MinimumElevationDegrees = index }, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, result.Outcome, result.Message); views.Add(result.Value!);
        }
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Expired, (await input.Planner.GetArcAsync(views[0].SearchIdentitySha256,
            views[0].Passes[0].Pass.ProductSha256, TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
        var source = snapshot.Manifest.Sources[0];
        input.Snapshots.Current = OrbitalSnapshot.Create(snapshot.Records,
            Enumerable.Range(0, 4).Select(index => source with
            {
                DatasetKey = index == 0 ? source.DatasetKey : "celestrak:gp:test" + index.ToString(CultureInfo.InvariantCulture),
                RawLengthBytes = 32 * 1024 * 1024
            }), snapshot.Manifest.Coverage);
        var oversized = await input.Planner.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Invalid, oversized.Outcome); Assert.IsNull(oversized.Value);
        StringAssert.Contains(oversized.Message, "not an exact managed-memory bound", StringComparison.Ordinal);
        input.Snapshots.Current = OrbitalSnapshot.Create(snapshot.Records,
            snapshot.Manifest.Sources.Select(item => item with { RawLengthBytes = 32 * 1024 * 1024 }), snapshot.Manifest.Coverage);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, (await input.Planner.GetArcAsync(views[2].SearchIdentitySha256,
            views[2].Passes[0].Pass.ProductSha256, TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
    }

    [TestMethod]
    public async Task SiteRigGeometrySnapshotTimeOrOptionsChangesRejectSelectedDetailAndInterleavedSearchAsync()
    {
        foreach (var change in new Action<PlanningTestInputs>[]
        {
            input => input.Configuration.Value = input.Configuration.Value with
                { Rig = input.Configuration.Value.Rig with { ProfileVersion = "changed" } },
            input => input.Sky.Transform = scene => scene with { Observer = scene.Observer with { Version = 2, CanonicalSha256 = new string('C', 64) } },
            input => input.Sky.Transform = scene => scene with { Geometry = scene.Geometry with { FocalLengthXPixels = 999 } },
            input => input.Snapshots.Current = PlanningTestInputs.Snapshot("two-starlinks"),
            input => input.Times.Current = OrbitalTimeData.FromFinals2000A(
                [.. PlanningTestInputs.Bytes("time", "predicted-2026.finals2000A"), (byte)'\n'], PlanningTestInputs.Bytes("time", "leap-seconds.list")),
            input => input.Options.CurrentValue.AllowStaleDiagnostics = true
        })
        {
            var input = new PlanningTestInputs();
            var result = await input.Planner.SearchAsync(await input.RequestAsync().ConfigureAwait(false), TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, result.Outcome, result.Message);
            change(input);
            Assert.AreEqual(CameraAgentSatellitePlanningOutcome.ContextChanged,
                (await input.Planner.GetArcAsync(result.Value!.SearchIdentitySha256, result.Value.Passes[0].Pass.ProductSha256, TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
            Assert.IsNull(input.Engine.LastArc);
        }
        var interleaved = new PlanningTestInputs(); var request = await interleaved.RequestAsync().ConfigureAwait(false);
        interleaved.Sky.Transform = scene =>
        {
            interleaved.Configuration.Value = interleaved.Configuration.Value with
            { Rig = interleaved.Configuration.Value.Rig with { ProfileVersion = "interleaved" } };
            return scene;
        };
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.ContextChanged,
            (await interleaved.Planner.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
        Assert.AreEqual(0, interleaved.Engine.Searches);
    }

    [TestMethod]
    public async Task UntrustedAdapterExceptionsAreSanitizedAsync()
    {
        var input = new PlanningTestInputs();
        input.Engine.Search = (_, _) => throw new IOException("/secret/path?password=do-not-expose");
        var result = await input.Planner.SearchAsync(await input.RequestAsync().ConfigureAwait(false), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Failed, result.Outcome);
        Assert.IsFalse(result.Message.Contains("secret", StringComparison.Ordinal));
        Assert.IsNull(result.Value);
    }

    [TestMethod]
    public async Task DetailRejectsChangesAndExpirationDuringNumericalWorkAsync()
    {
        foreach (var expire in new[] { false, true })
        {
            var input = new PlanningTestInputs();
            var search = await input.Planner.SearchAsync(await input.RequestAsync().ConfigureAwait(false), TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, search.Outcome, search.Message);
            var view = search.Value!;
            input.Engine.Arc = (query, _) =>
            {
                if (expire) input.Time.Now = view.ExpiresUtc;
                else input.Snapshots.Current = PlanningTestInputs.Snapshot("two-starlinks");
                return ValueTask.FromResult(new OrbitalPredictionResult<OrbitalPassArcV1>(query,
                    OrbitalExecutionStatus.Unavailable, OrbitalPredictionReason.IncompleteInterval, 1, 0, 0, [], []));
            };
            var detail = await input.Planner.GetArcAsync(view.SearchIdentitySha256, view.Passes[0].Pass.ProductSha256, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(expire ? CameraAgentSatellitePlanningOutcome.Expired : CameraAgentSatellitePlanningOutcome.ContextChanged, detail.Outcome);
            Assert.IsNull(detail.Value);
        }
    }

    [TestMethod]
    [DataRow("rig", false)]
    [DataRow("observer", false)]
    [DataRow("geometry", false)]
    [DataRow("snapshot", false)]
    [DataRow("model", false)]
    [DataRow("options", false)]
    [DataRow("missing-snapshot", false)]
    [DataRow("missing-time", false)]
    [DataRow("expired", false)]
    [DataRow("rig", true)]
    [DataRow("observer", true)]
    [DataRow("geometry", true)]
    [DataRow("snapshot", true)]
    [DataRow("model", true)]
    [DataRow("options", true)]
    [DataRow("missing-snapshot", true)]
    [DataRow("missing-time", true)]
    [DataRow("expired", true)]
    public async Task ObservedDetailInvalidationCannotResurrectTheOriginalSessionAsync(string change, bool duringArc)
    {
        var input = new PlanningTestInputs();
        var configuration = input.Configuration.Value;
        var snapshot = input.Snapshots.Current;
        var time = input.Times.Current;
        var allowStaleDiagnostics = input.Options.CurrentValue.AllowStaleDiagnostics;
        var now = input.Time.Now;
        var search = await input.Planner.SearchAsync(await input.RequestAsync().ConfigureAwait(false), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, search.Outcome, search.Message);
        var view = search.Value!;
        Action changeInputs = change switch
        {
            "rig" => () => input.Configuration.Value = configuration with { Rig = configuration.Rig with { ProfileVersion = "changed" } },
            "observer" => () => input.Sky.Transform = scene => scene with { Observer = scene.Observer with { CanonicalSha256 = new('C', 64) } },
            "geometry" => () => input.Sky.Transform = scene => scene with { Geometry = scene.Geometry with { FocalLengthXPixels = 999 } },
            "snapshot" => () => input.Snapshots.Current = PlanningTestInputs.Snapshot("two-starlinks"),
            "model" => () => input.Times.Current = OrbitalTimeData.FromFinals2000A(
                [.. PlanningTestInputs.Bytes("time", "predicted-2026.finals2000A"), (byte)'\n'], PlanningTestInputs.Bytes("time", "leap-seconds.list")),
            "options" => () => input.Options.CurrentValue.AllowStaleDiagnostics = true,
            "missing-snapshot" => () => input.Snapshots.Current = null,
            "missing-time" => () => input.Times.Current = null,
            _ => () => input.Time.Now = view.ExpiresUtc
        };
        if (duringArc)
        {
            input.Engine.Arc = (query, _) =>
            {
                changeInputs();
                return ValueTask.FromResult(new OrbitalPredictionResult<OrbitalPassArcV1>(query,
                    OrbitalExecutionStatus.Unavailable, OrbitalPredictionReason.IncompleteInterval, 1, 0, 0, [], []));
            };
        }
        else changeInputs();
        var detail = await input.Planner.GetArcAsync(view.SearchIdentitySha256, view.Passes[0].Pass.ProductSha256,
            TestContext.CancellationToken).ConfigureAwait(false);
        var expected = change switch
        {
            "missing-snapshot" => CameraAgentSatellitePlanningOutcome.NoSnapshot,
            "missing-time" => CameraAgentSatellitePlanningOutcome.TimeInputsUnavailable,
            "expired" => CameraAgentSatellitePlanningOutcome.Expired,
            _ => CameraAgentSatellitePlanningOutcome.ContextChanged
        };
        Assert.AreEqual(expected, detail.Outcome, detail.Message);
        Assert.IsNull(detail.Value);
        if (!duringArc) Assert.IsNull(input.Engine.LastArc);

        input.Configuration.Value = configuration;
        input.Snapshots.Current = snapshot;
        input.Times.Current = time;
        input.Options.CurrentValue.AllowStaleDiagnostics = allowStaleDiagnostics;
        input.Sky.Transform = null;
        input.Time.Now = now;
        input.Engine.Arc = null;
        var previousArc = input.Engine.LastArc;
        var restored = await input.Planner.GetArcAsync(view.SearchIdentitySha256, view.Passes[0].Pass.ProductSha256,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Expired, restored.Outcome, restored.Message);
        Assert.IsNull(restored.Value);
        Assert.AreSame(previousArc, input.Engine.LastArc, "A discarded handle must not reach the engine after A→B→A.");
    }

    [TestMethod]
    public async Task InvalidDetailIdentitiesDoNotDiscardUnrelatedRetainedSessionsAsync()
    {
        var input = new PlanningTestInputs();
        var request = await input.RequestAsync().ConfigureAwait(false);
        var first = await input.Planner.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        var second = await input.Planner.SearchAsync(request with { MinimumElevationDegrees = 1 }, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, first.Outcome, first.Message);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, second.Outcome, second.Message);
        var firstView = first.Value!;
        var secondView = second.Value!;
        var snapshot = input.Snapshots.Current;
        input.Snapshots.Current = PlanningTestInputs.Snapshot("two-starlinks");
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Invalid,
            (await input.Planner.GetArcAsync(firstView.SearchIdentitySha256, secondView.Passes[0].Pass.ProductSha256,
                TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Invalid,
            (await input.Planner.GetArcAsync("invalid", firstView.Passes[0].Pass.ProductSha256,
                TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Expired,
            (await input.Planner.GetArcAsync(new('F', 64), firstView.Passes[0].Pass.ProductSha256,
                TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
        Assert.IsNull(input.Engine.LastArc);
        input.Snapshots.Current = snapshot;
        foreach (var view in new[] { firstView, secondView })
        {
            var detail = await input.Planner.GetArcAsync(view.SearchIdentitySha256, view.Passes[0].Pass.ProductSha256,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, detail.Outcome, detail.Message);
        }
    }

    [TestMethod]
    public async Task ObservedSnapshotChangeDiscardsOnlyTheRequestedSessionAsync()
    {
        var input = new PlanningTestInputs();
        var request = await input.RequestAsync().ConfigureAwait(false);
        var first = await input.Planner.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, first.Outcome, first.Message);
        var firstView = first.Value!;
        var original = input.Snapshots.Current!;
        input.Snapshots.Current = OrbitalSnapshot.Create(original.Records,
            original.Manifest.Sources.Select(source => source with { RetrievedUtc = source.RetrievedUtc.AddMinutes(1) }), original.Manifest.Coverage);
        var second = await input.Planner.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, second.Outcome, second.Message);
        var secondView = second.Value!;
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.ContextChanged,
            (await input.Planner.GetArcAsync(firstView.SearchIdentitySha256, firstView.Passes[0].Pass.ProductSha256,
                TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready,
            (await input.Planner.GetArcAsync(secondView.SearchIdentitySha256, secondView.Passes[0].Pass.ProductSha256,
                TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
        input.Snapshots.Current = original;
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Expired,
            (await input.Planner.GetArcAsync(firstView.SearchIdentitySha256, firstView.Passes[0].Pass.ProductSha256,
                TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
    }

    [TestMethod]
    public async Task CancelledDetailKeepsSessionAndBusyGateUntilWorkExitsAsync()
    {
        var input = new PlanningTestInputs();
        var request = await input.RequestAsync().ConfigureAwait(false);
        var search = await input.Planner.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, search.Outcome, search.Message);
        var view = search.Value!;
        var originalQuery = input.Engine.LastSearch;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        input.Engine.Arc = async (query, _) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            return new(query, OrbitalExecutionStatus.Unavailable, OrbitalPredictionReason.IncompleteInterval, 1, 0, 0, [], []);
        };
        using var cancellation = new CancellationTokenSource();
        var running = input.Planner.GetArcAsync(view.SearchIdentitySha256, view.Passes[0].Pass.ProductSha256, cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        try
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            Assert.IsFalse(running.IsCompleted, "Cancellation must await the admitted work rather than detach it.");
            Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Busy,
                (await input.Planner.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
            Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Busy,
                (await input.Planner.GetArcAsync(view.SearchIdentitySha256, view.Passes[0].Pass.ProductSha256,
                    TestContext.CancellationToken).ConfigureAwait(false)).Outcome);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await running.ConfigureAwait(false)).ConfigureAwait(false);
        input.Engine.Arc = null;
        var retried = await input.Planner.GetArcAsync(view.SearchIdentitySha256, view.Passes[0].Pass.ProductSha256,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, retried.Outcome, retried.Message);
        Assert.AreSame(originalQuery, input.Engine.LastArc!.SearchQuery);
    }

    [TestMethod]
    public async Task UnchangedLastKnownGoodIdentityRemainsUsableAcrossCurrentReadsAsync()
    {
        var input = new PlanningTestInputs();
        var search = await input.Planner.SearchAsync(await input.RequestAsync().ConfigureAwait(false), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, search.Outcome, search.Message);
        var view = search.Value!;
        var originalQuery = input.Engine.LastSearch;
        // The reader contract keeps LKG available when refresh is halted/disabled; it exposes no operational revision.
        // Re-materializing identical immutable bytes must preserve usability and the original retained query.
        input.Snapshots.Current = PlanningTestInputs.Snapshot();
        input.Times.Current = PlanningTestInputs.TimeData();
        Assert.AreEqual(view.Prediction.SnapshotManifestSha256, input.Snapshots.Current.Manifest.ManifestSha256);
        Assert.AreNotSame(originalQuery!.Request.Snapshot, input.Snapshots.Current);
        var detail = await input.Planner.GetArcAsync(view.SearchIdentitySha256, view.Passes[0].Pass.ProductSha256,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, detail.Outcome, detail.Message);
        Assert.AreSame(originalQuery, input.Engine.LastArc!.SearchQuery);
        Assert.AreEqual(view.Passes[0].Pass.ProductSha256, input.Engine.LastArc.Pass.ProductSha256);
        Assert.AreEqual(view.Passes[0].Pass.Lineage.RecordSha256, input.Engine.LastArc.Pass.Lineage.RecordSha256);
    }

    [TestMethod]
    public async Task RealEngineFrozenIssSearchAndArcRetainSourceShapeAndWorkCountsAsync()
    {
        var input = new PlanningTestInputs(realEngine: true);
        var result = await input.Planner.SearchAsync(await input.RequestAsync().ConfigureAwait(false), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, result.Outcome, result.Message);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, result.Value!.Prediction.Status);
        Assert.AreEqual(1, result.Value.Prediction.RequestedObjects);
        Assert.IsNotNull(result.Value.Prediction.WorkDiagnostics);
        Assert.AreEqual(1, result.Value.Prediction.WorkDiagnostics.RequestedObjectSlots);
        Assert.AreEqual(1, result.Value.Prediction.WorkDiagnostics.VisitedObjectSlots);
        Assert.AreEqual(1, result.Value.Prediction.WorkDiagnostics.SelectedObjectSlots);
        Assert.AreEqual(0L, result.Value.Prediction.WorkDiagnostics.ProjectionAttempts);
        Assert.IsTrue(result.Value.Prediction.PropagationEvaluations > 0);
        Assert.IsTrue(result.Value.Passes.Count > 0);
        var parent = result.Value.Passes[0].Pass;
        var detail = await input.Planner.GetArcAsync(result.Value.SearchIdentitySha256, parent.ProductSha256, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, detail.Outcome, detail.Message);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, detail.Value!.Prediction.Status);
        var arc = detail.Value.Prediction.Products.Single();
        Assert.AreEqual(parent.ProductSha256, arc.Pass.ProductSha256);
        Assert.AreEqual(parent.SearchInterval, arc.Pass.SearchInterval);
        Assert.IsTrue(arc.Samples.Count > 2);
        Assert.IsTrue(arc.Samples.All(sample => sample.Illumination == OrbitalIllumination.Unknown && sample.Pixel is null));
        Assert.IsNull(arc.OrbitUncertainty.Value);
    }

    [TestMethod]
    public async Task RealEngineFrozenStarlinksUseTwoExplicitSlotsAndOriginalCoverageAsync()
    {
        var input = new PlanningTestInputs(realEngine: true);
        input.Snapshots.Current = PlanningTestInputs.Snapshot("two-starlinks");
        input.Configuration.Value = input.Configuration.Value with
        {
            DeploymentLocation = DeploymentLocationSnapshot.Create("starlink-site", 1, "startup-seed", null,
                DateTimeOffset.UnixEpoch, null, 35.347, -113.878, 0, "America/Phoenix")
        };
        var request = await input.RequestAsync("starlink").ConfigureAwait(false);
        request = request with
        {
            StartUtc = new(2026, 10, 20, 3, 55, 0, TimeSpan.Zero),
            EndUtc = new(2026, 10, 20, 4, 10, 0, TimeSpan.Zero)
        };
        var response = await input.Planner.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.Ready, response.Outcome, response.Message);
        var view = response.Value!;
        Assert.AreEqual(2, view.MatchingObjects); Assert.AreEqual(2, view.SelectedObjects);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, view.Prediction.Status);
        Assert.AreEqual(2, view.Prediction.RequestedObjects);
        Assert.IsNotNull(view.Prediction.WorkDiagnostics);
        Assert.AreEqual(2, view.Prediction.WorkDiagnostics.RequestedObjectSlots);
        Assert.AreEqual(2, view.Prediction.WorkDiagnostics.SelectedObjectSlots);
        Assert.AreEqual(OrbitalPopulationCompleteness.Partial, view.Prediction.PopulationCoverage.Completeness);
        Assert.IsTrue(view.Passes.Count >= 2);
        foreach (var parent in view.Passes.Select(row => row.Pass))
        {
            Assert.AreEqual(request.StartUtc, parent.SearchInterval.StartUtc);
            Assert.AreEqual(request.EndUtc, parent.SearchInterval.EndUtc);
            Assert.AreEqual(input.Snapshots.Current.Manifest.ManifestSha256, parent.Lineage.SnapshotManifestSha256);
            Assert.IsNotNull(view.Prediction.WorkDiagnostics);
        }
    }

    private static CameraAgentSatellitePlanner CreatePlanner(PlanningTestInputs input, ICameraAgentSkyMapProjection sky)
        => new(sky, input.Configuration, input.Times, input.Engine, input.Options, input.Time, input.Snapshots);

    private sealed class RecordingSkyMap(ICameraAgentSkyMapProjection inner) : ICameraAgentSkyMapProjection
    {
        internal int Calls { get; private set; }

        public ValueTask<CameraAgentSkyMapProjectionResult> ProjectAsync(DateTimeOffset? atUtc, CancellationToken cancellationToken)
        {
            Calls++;
            return inner.ProjectAsync(atUtc, cancellationToken);
        }
    }
}
