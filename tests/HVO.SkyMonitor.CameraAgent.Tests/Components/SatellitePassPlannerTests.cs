using System.Globalization;
using Bunit;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class SatellitePassPlannerTests
{
    [TestMethod]
    public void Render_WithoutAdapterIsHonestAndNeverUsesCommonPlanner()
    {
        using var context = new BunitContext();
        var cut = Render(context);
        Assert.Contains("authenticated read service is not registered", cut.Markup, StringComparison.Ordinal);
        Assert.IsTrue(cut.Find("button[type=submit]").HasAttribute("disabled"));
    }

    [TestMethod]
    public void Render_WithoutSiteOrWhileContextLoadsDisablesSearch()
    {
        using var context = Context(new Adapter());
        var cut = context.Render<SatellitePassPlanner>();
        Assert.Contains("site, rig or UTC instant is unavailable", cut.Markup, StringComparison.Ordinal);
        cut.Render(parameters => parameters.Add(component => component.ContextLoading, true));
        Assert.Contains("Waiting for the selected site", cut.Markup, StringComparison.Ordinal);
        Assert.IsTrue(cut.Find("button[type=submit]").HasAttribute("disabled"));
    }

    [TestMethod]
    public void Search_UsesBoundedDefaultsAndDoesNotFetchArcsAutomatically()
    {
        using var context = Context(new Adapter());
        var service = context.Services.GetRequiredService<Adapter>();
        var cut = Render(context);
        Assert.IsEmpty(service.Requests);
        cut.Find("form").Submit();
        var request = service.Requests.Single();
        Assert.AreEqual("25544", request.ObjectFilter);
        Assert.AreEqual(6, (request.EndUtc - request.StartUtc).TotalHours);
        Assert.AreEqual(10, request.MinimumElevationDegrees);
        Assert.IsTrue(request.ExpectedObserverSha256 == Fixture.Context.Observer.CanonicalSha256);
        Assert.IsTrue(request.ExpectedRigSha256 == Fixture.Context.Rig.RigProfileHashSha256);
        Assert.IsEmpty(service.ArcRequests);
        Assert.Contains("No local orbital snapshot", cut.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("25", "10", "25544")]
    [DataRow("0", "10", "25544")]
    [DataRow("6", "90", "25544")]
    [DataRow("6", "NaN", "25544")]
    [DataRow("6", "10", "")]
    public void Search_RejectsUnsupportedInputBeforeReading(string hours, string elevation, string filter)
    {
        var service = new Adapter();
        using var context = Context(service);
        var cut = Render(context);
        cut.Find("#satellite-hours").Input(hours);
        cut.Find("#satellite-elevation").Input(elevation);
        cut.Find("#satellite-filter").Input(filter);
        cut.Find("form").Submit();
        Assert.IsEmpty(service.Requests);
        Assert.Contains("1–24 whole hours", cut.Find("div[role=status]").TextContent, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(CameraAgentSatellitePlanningOutcome.NoSnapshot, "No local orbital snapshot")]
    [DataRow(CameraAgentSatellitePlanningOutcome.TimeInputsUnavailable, "Pinned Earth orientation")]
    [DataRow(CameraAgentSatellitePlanningOutcome.Busy, "busy")]
    [DataRow(CameraAgentSatellitePlanningOutcome.Expired, "expired")]
    [DataRow(CameraAgentSatellitePlanningOutcome.ContextChanged, "site or rig changed")]
    public void Search_PresentsFacadeAvailability(CameraAgentSatellitePlanningOutcome outcome, string expected)
    {
        using var context = Context(new Adapter { Outcome = outcome });
        var cut = Render(context);
        cut.Find("form").Submit();
        Assert.Contains(expected, cut.Markup, StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll(".satellite-table"));
    }

    [TestMethod]
    public void Search_PresentsUnauthorizedAndUnavailableReadStates()
    {
        var service = new Adapter { Kind = OperatorUiResultKind.Unauthorized };
        using var context = Context(service);
        var cut = Render(context);
        cut.Find("form").Submit();
        Assert.Contains("Authorization required", cut.Markup, StringComparison.Ordinal);
        service.Kind = OperatorUiResultKind.Unavailable;
        cut.Find("form").Submit();
        Assert.Contains("Read unavailable", cut.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Search_ShowsCoverageRetrievalAgeAndOriginalSignedEpochAgesSeparately()
    {
        var view = Fixture.Search with { MatchingObjects = 50, SelectedObjects = 32 };
        using var context = Context(new Adapter { SearchView = view });
        var cut = Render(context);
        SearchFixture(cut);
        var text = cut.Find(".satellite-summary").TextContent;
        Assert.Contains("Selected 32 of 50", text, StringComparison.Ordinal);
        Assert.Contains("Object selection truncated", text, StringComparison.Ordinal);
        Assert.Contains("most recent retrieval age at search +0.50 h", text, StringComparison.Ordinal);
        Assert.Contains("CompleteForRequestedPopulation", text, StringComparison.Ordinal);
        Assert.Contains("inactive objects", text, StringComparison.Ordinal);
        Assert.Contains("Original start +1.00 h", cut.Find("tbody").TextContent, StringComparison.Ordinal);
        Assert.Contains("Original end +4.00 h", cut.Find("tbody").TextContent, StringComparison.Ordinal);
        Assert.Contains("Complete computation does not imply", cut.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Search_PresentsEmptyPartialUnavailableAndStaleWithoutVisibilityClaims()
    {
        var query = Fixture.Query.SearchQuery;
        var partial = new OrbitalPredictionResult<OrbitalPassSummaryV1>(query, OrbitalExecutionStatus.Partial,
            OrbitalPredictionReason.EvaluationBudget, 0, 0, 0, [], []);
        var service = new Adapter { SearchView = Fixture.Search with { Prediction = partial, Passes = [] } };
        using var context = Context(service);
        var cut = Render(context);
        SearchFixture(cut);
        Assert.Contains("Partial / EvaluationBudget", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("No passes were returned", cut.Markup, StringComparison.Ordinal);
        var unavailable = new OrbitalPredictionResult<OrbitalPassSummaryV1>(query, OrbitalExecutionStatus.Unavailable,
            OrbitalPredictionReason.MissingEarthOrientation, 0, 0, 0, [], []);
        service.SearchView = Fixture.Search with { Prediction = unavailable, Passes = [] };
        cut.Find("form").Submit();
        Assert.Contains("Unavailable / MissingEarthOrientation", cut.Markup, StringComparison.Ordinal);
        // State presentation only: the adapter already owns scientific validation and freshness classification.
        var row = Fixture.Search.Passes.Single();
        service.SearchView = Fixture.Search with
        {
            Passes = [row with { Pass = row.Pass with { Lineage = row.Pass.Lineage with { Freshness = OrbitalElementFreshness.Stale } } }]
        };
        cut.Find("form").Submit();
        Assert.Contains("Stale", cut.Find("tbody").TextContent, StringComparison.Ordinal);
        Assert.Contains("brightness", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("unknown here", cut.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Search_DistinguishesClippedBoundariesAndPaginatesBoundedRows()
    {
        var pass = Fixture.Arc.Pass;
        var clipped = OrbitalPredictionJson.CreatePass(pass with
        {
            PassInterval = pass.SearchInterval,
            StartBoundary = OrbitalPassBoundary.SearchWindow,
            EndBoundary = OrbitalPassBoundary.SearchWindow
        });
        var rows = Enumerable.Range(0, 128).Select(index => new CameraAgentSatellitePassRow($"Object {index}",
            OrbitalPredictionJson.CreatePass(clipped with { MaximumElevationDegrees = 60 + index / 1000d }))).ToArray();
        using var context = Context(new Adapter { SearchView = Fixture.Search with { Passes = rows } });
        var cut = Render(context);
        SearchFixture(cut);
        Assert.HasCount(8, cut.FindAll("tbody tr"));
        Assert.Contains("Window start (clipped)", cut.Find("tbody").TextContent, StringComparison.Ordinal);
        Assert.Contains("Window end (clipped)", cut.Find("tbody").TextContent, StringComparison.Ordinal);
        Assert.Contains("Pass row limit reached", cut.Markup, StringComparison.Ordinal);
        cut.FindAll("nav button")[1].Click();
        Assert.Contains("Object 8", cut.Find("tbody").TextContent, StringComparison.Ordinal);
        Assert.Contains("Page 2 of 16", cut.Find("nav").TextContent, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Selection_UsesOriginalSearchAndPassIdentitiesAndIndependentDisplayToggles()
    {
        var service = new Adapter { SearchView = Fixture.Search, ArcView = Fixture.Detail };
        using var context = Context(service);
        var cut = Render(context);
        SearchFixture(cut);
        Assert.IsEmpty(service.ArcRequests);
        cut.Find(".satellite-select").Click();
        Assert.AreEqual((Fixture.Search.SearchIdentitySha256, Fixture.Arc.Pass.ProductSha256), service.ArcRequests.Single());
        Assert.AreEqual("true", cut.Find(".satellite-select").GetAttribute("aria-pressed"));
        Assert.HasCount(1, cut.FindAll(".satellite-arc > svg .predicted-path"));
        cut.Find("#satellite-show-path").Change(false);
        Assert.IsEmpty(cut.FindAll(".satellite-arc .predicted-path"));
        Assert.IsNotEmpty(cut.FindAll(".arc-time-label"));
        cut.Find("#satellite-show-labels").Change(false);
        Assert.IsEmpty(cut.FindAll(".arc-time-label"));
        Assert.Contains("altitude", cut.Find(".anchor-facts").TextContent, StringComparison.Ordinal);
        cut.Find("#satellite-show-rig").Change(true);
        Assert.HasCount(1, cut.FindAll(".rig-preview"));
        Assert.Contains("lower confidence", cut.Markup, StringComparison.Ordinal);
        Assert.HasCount(1, service.ArcRequests);
        Assert.HasCount(1, service.Requests);
    }

    [TestMethod]
    public void Selection_RejectsAnArcFromADifferentOriginalSession()
    {
        using var context = Context(new Adapter { SearchView = Fixture.Search, ArcView = Fixture.Detail with { SearchIdentitySha256 = new('F', 64) } });
        var cut = Render(context);
        SearchFixture(cut);
        cut.Find(".satellite-select").Click();
        Assert.Contains("does not match this original search and pass", cut.Markup, StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll(".satellite-arc"));
    }

    [TestMethod]
    public async Task NewSelectionGeneration_KeepsItsArcWhenTheOldSelectionFinishesLateAsync()
    {
        var pending = new TaskCompletionSource<OperatorUiResult<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassArcView>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Adapter { SearchView = Fixture.Search, ArcPending = pending.Task };
        using var context = Context(service);
        var cut = Render(context);
        SearchFixture(cut);
        var first = cut.Find(".satellite-select").ClickAsync(new());
        var firstToken = service.ArcToken;
        service.ArcPending = null;
        service.ArcView = Fixture.Detail;
        await cut.Find(".satellite-select").ClickAsync(new()).ConfigureAwait(false);
        Assert.IsTrue(firstToken.IsCancellationRequested);
        Assert.HasCount(1, cut.FindAll(".satellite-arc"));
        pending.SetResult(OperatorUiResult<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassArcView>>.Failure(
            OperatorUiResultKind.Unavailable, "Outdated arc failure"));
        await first.ConfigureAwait(false);
        Assert.HasCount(1, cut.FindAll(".satellite-arc"));
        Assert.DoesNotContain("Outdated arc failure", cut.Markup, StringComparison.Ordinal);
        Assert.HasCount(2, service.ArcRequests);
    }

    [TestMethod]
    [DataRow("site")]
    [DataRow("rig")]
    [DataRow("time")]
    [DataRow("filter")]
    public async Task ContextChanges_CancelAndDiscardLateSearchCompletionsAsync(string change)
    {
        var pending = new TaskCompletionSource<OperatorUiResult<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassSearchView>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Adapter { SearchPending = pending.Task };
        using var context = Context(service);
        var cut = Render(context);
        await cut.Find("#satellite-hours").InputAsync(new() { Value = "3" }).ConfigureAwait(false);
        await cut.Find("#satellite-elevation").InputAsync(new() { Value = "0" }).ConfigureAwait(false);
        var submit = cut.Find("form").SubmitAsync();
        cut.WaitForAssertion(() => Assert.Contains("Searching the bounded window", cut.Markup, StringComparison.Ordinal));
        ChangeContext(cut, change);
        Assert.IsTrue(service.SearchToken.IsCancellationRequested);
        pending.SetResult(Success(Fixture.Search));
        await submit.ConfigureAwait(false);
        Assert.IsEmpty(cut.FindAll(".satellite-table"));
        Assert.IsFalse(cut.Find("section").GetAttribute("aria-busy") == "true");
    }

    [TestMethod]
    public async Task NewSearchGeneration_CannotBeOverwrittenByAnOlderCompletionAsync()
    {
        var pending = new TaskCompletionSource<OperatorUiResult<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassSearchView>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Adapter { SearchPending = pending.Task };
        using var context = Context(service);
        var cut = Render(context);
        await cut.Find("#satellite-hours").InputAsync(new() { Value = "3" }).ConfigureAwait(false);
        var first = cut.Find("form").SubmitAsync();
        await cut.Find("#satellite-filter").InputAsync(new() { Value = "new filter" }).ConfigureAwait(false);
        service.SearchPending = null;
        await cut.Find("form").SubmitAsync().ConfigureAwait(false);
        pending.SetResult(Success(Fixture.Search));
        await first.ConfigureAwait(false);
        Assert.Contains("No local orbital snapshot", cut.Markup, StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll(".satellite-table"));
    }

    [TestMethod]
    [DataRow("site")]
    [DataRow("rig")]
    [DataRow("time")]
    [DataRow("filter")]
    public async Task ContextChanges_CancelAndDiscardLateArcCompletionsAsync(string change)
    {
        var pending = new TaskCompletionSource<OperatorUiResult<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassArcView>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Adapter { SearchView = Fixture.Search, ArcPending = pending.Task };
        using var context = Context(service);
        var cut = Render(context);
        SearchFixture(cut);
        var selecting = cut.Find(".satellite-select").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.Contains("Loading the original selected pass", cut.Markup, StringComparison.Ordinal));
        ChangeContext(cut, change);
        Assert.IsTrue(service.ArcToken.IsCancellationRequested);
        pending.SetResult(Success(Fixture.Detail));
        await selecting.ConfigureAwait(false);
        Assert.IsEmpty(cut.FindAll(".satellite-detail"));
    }

    [TestMethod]
    public async Task Disposal_CancelsOutstandingSearchAndArcRequestsAsync()
    {
        using var context = Context(new Adapter());
        var service = context.Services.GetRequiredService<Adapter>();
        var pendingSearch = new TaskCompletionSource<OperatorUiResult<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassSearchView>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SearchPending = pendingSearch.Task;
        var cut = Render(context);
        var searching = cut.Find("form").SubmitAsync();
        await cut.Instance.DisposeAsync().ConfigureAwait(false);
        Assert.IsTrue(service.SearchToken.IsCancellationRequested);
        pendingSearch.SetResult(Success(Fixture.Search));
        await searching.ConfigureAwait(false);

        service.SearchPending = null;
        service.SearchView = Fixture.Search;
        var second = Render(context);
        SearchFixture(second);
        var pendingArc = new TaskCompletionSource<OperatorUiResult<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassArcView>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ArcPending = pendingArc.Task;
        var selecting = second.Find(".satellite-select").ClickAsync(new());
        await second.Instance.DisposeAsync().ConfigureAwait(false);
        Assert.IsTrue(service.ArcToken.IsCancellationRequested);
        pendingArc.SetResult(Success(Fixture.Detail));
        await selecting.ConfigureAwait(false);
    }

    [TestMethod]
    public void TimeDisplay_DistinguishesRepeatedLocalHourWithOffsetTimezoneAndUtc()
    {
        using var context = Context(new Adapter());
        var observer = Fixture.Context.Observer with { TimeZoneId = "America/New_York" };
        var cut = context.Render<SatellitePassPlanner>(parameters => parameters
            .Add(component => component.Observer, observer).Add(component => component.Rig, Fixture.Context.Rig)
            .Add(component => component.AtUtc, new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero)));
        Assert.Contains("01:30:00 -04:00 (America/New_York)", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("05:30:00 UTC", cut.Markup, StringComparison.Ordinal);
        cut.Render(parameters => parameters.Add(component => component.AtUtc, new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero)));
        Assert.Contains("01:30:00 -05:00 (America/New_York)", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("06:30:00 UTC", cut.Markup, StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll("input[type=datetime-local]"));
    }

    [TestMethod]
    public void Arc_UsesSharedProjectorForCanonical529FixtureAndRetainsOffRigSamples()
    {
        using var context = new BunitContext();
        var cut = context.Render<SatellitePassArc>(parameters => parameters.Add(component => component.Arc, Fixture.Arc)
            .Add(component => component.Context, Fixture.Context).Add(component => component.ShowRigPreview, true));
        var projector = ProjectorFactory.Create(new(ProjectionModel.EquidistantFisheye, 256, 256, 480 / Math.PI,
            480 / Math.PI, 512, 512, ProjectionAperture.Circular, 240));
        var expected = string.Join(' ', Fixture.Arc.Samples.Select(sample =>
        {
            var point = projector.Project(sample.Horizontal)!.Value;
            return $"{point.X.ToString("0.###", CultureInfo.InvariantCulture)},{point.Y.ToString("0.###", CultureInfo.InvariantCulture)}";
        }));
        Assert.AreEqual(expected, cut.Find(".satellite-arc > svg polyline").GetAttribute("points"));
        Assert.IsEmpty(cut.FindAll(".rig-preview polyline"));
        Assert.HasCount(3, cut.FindAll(".arc-anchor"));
        Assert.Contains("off-rig portions", cut.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Arc_DoesNotBridgeDeclaredGapsOrGuessMissingAnchorsAndBoundsLabels()
    {
        var original = Fixture.Arc;
        double[] seconds = [0, .25, .5, 1.5, 2];
        var samples = Enumerable.Range(0, 5).Select(index => original.Samples[1] with
        { Utc = original.Pass.PassInterval.StartUtc.AddSeconds(seconds[index]) }).ToArray();
        var arc = OrbitalPassArcJson.Create(original with
        {
            Samples = samples,
            Segments = [new(0, 2), new(3, 2)],
            Anchors = new(0, null, 4),
            Status = OrbitalExecutionStatus.Partial,
            Reason = OrbitalPredictionReason.IncompleteInterval
        });
        using var context = new BunitContext();
        var cut = context.Render<SatellitePassArc>(parameters => parameters.Add(component => component.Arc, arc)
            .Add(component => component.Context, Fixture.Context));
        Assert.HasCount(2, cut.FindAll(".predicted-path"));
        Assert.HasCount(2, cut.FindAll(".arc-anchor"));
        Assert.HasCount(1, cut.FindAll(".arc-time-label"));
        Assert.Contains("Unresolved; no declared sample anchor", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Partial / IncompleteInterval", cut.Markup, StringComparison.Ordinal);
    }

    private static void ChangeContext(IRenderedComponent<SatellitePassPlanner> cut, string change)
    {
        switch (change)
        {
            case "site": cut.Render(parameters => parameters.Add(component => component.Observer, Fixture.Context.Observer with { CanonicalSha256 = new('C', 64) })); break;
            case "rig": cut.Render(parameters => parameters.Add(component => component.Rig, Fixture.Context.Rig with { RigProfileHashSha256 = new('E', 64) })); break;
            case "time": cut.Render(parameters => parameters.Add(component => component.AtUtc, Fixture.Arc.Pass.SearchInterval.StartUtc.AddMinutes(1))); break;
            default: cut.Find("#satellite-filter").Input("Starlink"); break;
        }
    }

    private static IRenderedComponent<SatellitePassPlanner> Render(BunitContext context)
        => context.Render<SatellitePassPlanner>(parameters => parameters.Add(component => component.Observer, Fixture.Context.Observer)
            .Add(component => component.Rig, Fixture.Context.Rig).Add(component => component.AtUtc, Fixture.Arc.Pass.SearchInterval.StartUtc));

    private static void SearchFixture(IRenderedComponent<SatellitePassPlanner> cut)
    {
        cut.Find("#satellite-hours").Input("3");
        cut.Find("#satellite-elevation").Input("0");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("section").GetAttribute("aria-busy") == "true"));
    }

    private static BunitContext Context(Adapter service)
    {
        var context = new BunitContext();
        context.Services.AddSingleton(service);
        context.Services.AddSingleton<ICameraAgentSatellitePlanningUiService>(service);
        return context;
    }

    private static OperatorUiResult<CameraAgentSatellitePlanningResponse<T>> Success<T>(T value) where T : class
        => OperatorUiResult<CameraAgentSatellitePlanningResponse<T>>.Success(new(CameraAgentSatellitePlanningOutcome.Ready, "Planning result available.", value));

    private sealed class Adapter : ICameraAgentSatellitePlanningUiService
    {
        internal List<CameraAgentSatellitePassSearchRequest> Requests { get; } = [];
        internal List<(string, string)> ArcRequests { get; } = [];
        internal CameraAgentSatellitePlanningOutcome Outcome { get; set; } = CameraAgentSatellitePlanningOutcome.NoSnapshot;
        internal OperatorUiResultKind Kind { get; set; } = OperatorUiResultKind.Success;
        internal CameraAgentSatellitePassSearchView? SearchView { get; set; }
        internal CameraAgentSatellitePassArcView? ArcView { get; set; }
        internal Task<OperatorUiResult<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassSearchView>>>? SearchPending { get; set; }
        internal Task<OperatorUiResult<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassArcView>>>? ArcPending { get; set; }
        internal CancellationToken SearchToken { get; private set; }
        internal CancellationToken ArcToken { get; private set; }

        public ValueTask<OperatorUiResult<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassSearchView>>> SearchAsync(
            CameraAgentSatellitePassSearchRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            SearchToken = cancellationToken;
            if (SearchPending is { } pending) return new(pending);
            if (Kind != OperatorUiResultKind.Success) return ValueTask.FromResult(OperatorUiResult<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassSearchView>>.Failure(
                Kind, Kind == OperatorUiResultKind.Unauthorized ? "Authorization required" : "Read unavailable"));
            return ValueTask.FromResult(SearchView is { } view ? Success(view) :
                OperatorUiResult<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassSearchView>>.Success(new(Outcome, CameraAgentSatellitePlanningUiService.Describe(Outcome), null)));
        }

        public ValueTask<OperatorUiResult<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassArcView>>> GetArcAsync(
            string searchIdentitySha256, string parentPassSha256, CancellationToken cancellationToken)
        {
            ArcRequests.Add((searchIdentitySha256, parentPassSha256));
            ArcToken = cancellationToken;
            return ArcPending is { } pending ? new(pending) : ValueTask.FromResult(Success(ArcView!));
        }
    }

    /// <summary>Consumer evidence only: pinned #529 bytes, no network or expected science generation.</summary>
    private static class Fixture
    {
        private sealed record FixtureData(OrbitalPassArcV1 Arc, OrbitalPassArcQuery Query,
            CameraAgentSatellitePlanningContext Context, CameraAgentSatellitePassSearchView Search,
            CameraAgentSatellitePassArcView Detail);

        private static readonly FixtureData Data = Load();
        internal static OrbitalPassArcV1 Arc => Data.Arc;
        internal static OrbitalPassArcQuery Query => Data.Query;
        internal static CameraAgentSatellitePlanningContext Context => Data.Context;
        internal static CameraAgentSatellitePassSearchView Search => Data.Search;
        internal static CameraAgentSatellitePassArcView Detail => Data.Detail;

        private static FixtureData Load()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "HVO.SkyMonitor.v9.slnx"))) root = root.Parent;
            Assert.IsNotNull(root, "Repository fixture root must be available for the consumer test.");
            var pack = Path.Combine(root.FullName, "tests", "fixtures", "orbital");
            var arc = OrbitalPassArcJson.Parse(File.ReadAllBytes(Path.Combine(pack, "pass-arc-v1", "arc.json")));
            var original = OrbitalSnapshot.Parse(File.ReadAllBytes(Path.Combine(pack, "contract-v1", "snapshot-manifest.json")),
                File.ReadAllBytes(Path.Combine(pack, "contract-v1", "records.json"))).Snapshot!;
            var snapshot = OrbitalSnapshot.Create([original.Records.Single(record => record.RecordSha256 == arc.Pass.Lineage.RecordSha256)],
                original.Manifest.Sources, original.Manifest.Coverage);
            var request = new OrbitalPredictionRequest(OrbitalPredictionUse.Diagnostic, snapshot,
                new OrbitalSelectionPolicy([new("celestrak:gp:active", 0)]), arc.Pass.SearchInterval, snapshot.ObjectIdentities,
                arc.Pass.Observer, arc.Pass.Model, new(32, 86400, 100000, 32, 100, 10000, 4000, 1, .25));
            var query = new OrbitalPassArcQuery(new(request, 0), arc.Pass, new(100000, 10000, 600000, 1, .001));
            var sky = SkyMapTestData.Result(arc.Pass.SearchInterval.StartUtc);
            var context = new CameraAgentSatellitePlanningContext(sky.Observer with
            {
                LatitudeDegrees = arc.Pass.Observer.LatitudeDegrees,
                LongitudeDegrees = arc.Pass.Observer.LongitudeDegrees,
                ElevationMeters = arc.Pass.Observer.ElevationMeters
            }, sky.Geometry, new(ProjectionModel.Perspective, 50, 50, 500, 500, 100, 100, ProjectionAperture.Rectangular));
            var prediction = new OrbitalPredictionResult<OrbitalPassSummaryV1>(query.SearchQuery, OrbitalExecutionStatus.Complete,
                OrbitalPredictionReason.None, 1, 3, 1, [arc.Pass], []);
            var search = new CameraAgentSatellitePassSearchView(query.SearchQuerySha256, arc.Pass.SearchInterval.StartUtc.AddMinutes(30), arc.Pass.SearchInterval.StartUtc.AddHours(1),
                context, prediction, [new("Synthetic ISS fixture", arc.Pass)], 1, 1, arc.Pass.SearchInterval.StartUtc);
            var detail = new CameraAgentSatellitePassArcView(search.SearchIdentitySha256, arc.Pass.ProductSha256, context,
                new OrbitalPredictionResult<OrbitalPassArcV1>(query, OrbitalExecutionStatus.Complete, OrbitalPredictionReason.None, 1, 3, 1, [arc], []));
            return new(arc, query, context, search, detail);
        }
    }
}
