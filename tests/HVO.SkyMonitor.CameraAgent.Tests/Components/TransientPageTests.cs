using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using Moq;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class TransientPageTests
{
    [TestMethod]
    public void ListRendersStageStatesDetailLinksAndNavigatesKeysetPages()
    {
        using var context = new BunitContext();
        var candidate = Candidate();
        var service = new TestTransientUiService
        {
            Page = OperatorUiResult<CameraAgentTransientOperatorPage>.Success(new([candidate], "older-cursor"))
        };
        Configure(context, service);

        var cut = context.Render<TransientPage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, candidate.CandidateId.ToString(), StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Causal", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Centered", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Assessment", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Final", StringComparison.Ordinal);
            Assert.IsTrue(cut.FindAll($"a[href^='/transients/{candidate.CandidateId:D}']").Count >= 1);
        });
        cut.FindAll("button").Single(button => button.TextContent.Contains("Older", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => StringAssert.Contains(
            context.Services.GetRequiredService<NavigationManager>().Uri,
            "cursor=older-cursor",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void DetailRendersSanitizedSummariesAndExplicitAbsentOrPendingStages()
    {
        using var context = new BunitContext();
        var detail = Detail();
        Configure(context, new TestTransientUiService
        {
            Detail = OperatorUiResult<CameraAgentTransientOperatorDetail>.Success(detail)
        });

        var cut = context.Render<TransientDetail>(parameters =>
            parameters.Add(page => page.CandidateId, detail.Candidate.CandidateId));

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "Candidate evidence", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Causal window", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Centered window", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Evidence state: Pending", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Evidence state: Absent", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Fireball", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Receipt identity SHA-256", StringComparison.Ordinal);
            Assert.IsFalse(cut.Markup.Contains("/private/", StringComparison.Ordinal));
            Assert.IsFalse(cut.Markup.Contains("secret", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(cut.Markup.Contains("rawJson", StringComparison.OrdinalIgnoreCase));
        });
    }

    [TestMethod]
    public void UnauthorizedListNavigatesToAccessDenied()
    {
        using var context = new BunitContext();
        Configure(context, new TestTransientUiService
        {
            Page = OperatorUiResult<CameraAgentTransientOperatorPage>.Failure(
                OperatorUiResultKind.Unauthorized, "Authorization is required.")
        });

        var cut = context.Render<TransientPage>();

        cut.WaitForAssertion(() => StringAssert.EndsWith(
            context.Services.GetRequiredService<NavigationManager>().Uri,
            "/Account/AccessDenied",
            StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("Validated")]
    [DataRow("Rejected")]
    public void LocalWorkflowNeverBecomesOwnerReview(string workflow)
    {
        using var context = new BunitContext();
        var candidate = Candidate() with { EventState = workflow };
        Configure(context, new TestTransientUiService
        {
            Page = OperatorUiResult<CameraAgentTransientOperatorPage>.Success(new([candidate], null)),
            Detail = OperatorUiResult<CameraAgentTransientOperatorDetail>.Success(Detail() with { Candidate = candidate })
        });
        var cut = context.Render<TransientPage>();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Fireball candidate", cut.Find(".event-card h2").TextContent);
            Assert.AreEqual("Unavailable", cut.Find(".event-card-facts dd").TextContent);
            Assert.IsTrue(cut.Find("select[aria-describedby='review-unavailable']").HasAttribute("disabled"));
            Assert.IsFalse(cut.Markup.Contains("Owner confirmed", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void CalendarKeepsAllMatchesOnTheLocalNoonObservingDate()
    {
        using var context = new BunitContext();
        var first = Candidate();
        var second = first with { CandidateId = Guid.NewGuid(), EventId = Guid.NewGuid(), CreatedUtc = first.CreatedUtc.AddMinutes(-1) };
        Configure(context, new TestTransientUiService
        {
            Page = OperatorUiResult<CameraAgentTransientOperatorPage>.Success(new([first, second], "older")),
            DetailHandler = id => OperatorUiResult<CameraAgentTransientOperatorDetail>.Success(Detail() with { Candidate = id == first.CandidateId ? first : second })
        });
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/transients?view=calendar&month=2026-07&classification=fireball");
        var cut = context.Render<TransientPage>();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("July 2026", cut.Find(".event-month-heading h2").TextContent);
            Assert.AreEqual(35, cut.FindAll(".event-month-day").Count);
            Assert.AreEqual(2, cut.FindAll(".event-month-day.has-event a").Count);
            Assert.AreEqual("22", cut.Find(".event-month-day.has-event > span").TextContent);
            Assert.AreEqual("true", cut.Find("button[aria-label='Event calendar view']").GetAttribute("aria-pressed"));
            StringAssert.Contains(cut.Markup, "Older candidates are omitted", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void UnsupportedMeasurementsAndAuthorityActionsStayUnavailable()
    {
        using var context = new BunitContext();
        Configure(context, new TestTransientUiService { Detail = OperatorUiResult<CameraAgentTransientOperatorDetail>.Success(Detail()) });
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/transients/" + Candidate().CandidateId + "?returnUrl=https%3A%2F%2Foutside.example%2F");
        var cut = context.Render<TransientDetail>(parameters => parameters.Add(page => page.CandidateId, Candidate().CandidateId));
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(3, cut.FindAll("[role='tab'][disabled]").Count);
            Assert.AreEqual(5, cut.FindAll(".context-frame.unavailable").Count);
            Assert.AreEqual(3, cut.FindAll("button.btn[disabled][aria-describedby]").Count);
            StringAssert.Contains(cut.Markup, "Not measured", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Requires genuine correlation", StringComparison.Ordinal);
            Assert.AreEqual("/transients", cut.Find(".breadcrumb a").GetAttribute("href"));
            Assert.IsEmpty(cut.FindAll(".event-media-view img"));
        });
    }

    [TestMethod]
    public void UnknownCandidateDoesNotBorrowAnotherDetail()
    {
        using var context = new BunitContext();
        Configure(context, new TestTransientUiService { Detail = OperatorUiResult<CameraAgentTransientOperatorDetail>.Success(Detail()) });
        var cut = context.Render<TransientDetail>(parameters => parameters.Add(page => page.CandidateId, Guid.NewGuid()));
        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "Candidate unavailable", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll(".event-media-layout"));
            Assert.IsFalse(cut.Markup.Contains(Candidate().CandidateId.ToString(), StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void NoMatchingFilterKeepsTheBoundedScopeAndUnavailableSummary()
    {
        using var context = new BunitContext();
        Configure(context, new TestTransientUiService
        {
            Page = OperatorUiResult<CameraAgentTransientOperatorPage>.Success(new([Candidate()], "older")),
            Detail = OperatorUiResult<CameraAgentTransientOperatorDetail>.Success(Detail())
        });
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/transients?classification=aircraft");
        var cut = context.Render<TransientPage>();
        cut.WaitForAssertion(() =>
        {
            Assert.IsEmpty(cut.FindAll(".event-card"));
            StringAssert.Contains(cut.Markup, "0 matching local candidates on this bounded page", StringComparison.Ordinal);
            Assert.AreEqual("Unavailable", cut.Find(".event-summary article strong").TextContent);
            Assert.IsTrue(cut.FindAll("button").Any(button => button.TextContent == "Older candidates"));
        });
    }

    [TestMethod]
    [DataRow("Absent")]
    [DataRow("Pending")]
    [DataRow("Unavailable")]
    public void MissingAssessmentIsNotARetainedUnknownClassification(string state)
    {
        var detail = Detail() with { AssessmentEvidence = new(state) };
        using var listContext = new BunitContext();
        Configure(listContext, new TestTransientUiService
        {
            Page = OperatorUiResult<CameraAgentTransientOperatorPage>.Success(new([Candidate()], null)),
            Detail = OperatorUiResult<CameraAgentTransientOperatorDetail>.Success(detail)
        });
        listContext.Services.GetRequiredService<NavigationManager>().NavigateTo("/transients?classification=unresolved");
        var list = listContext.Render<TransientPage>();
        list.WaitForAssertion(() =>
        {
            Assert.IsEmpty(list.FindAll(".event-card"));
            StringAssert.Contains(list.Markup, "0 matching local candidates", StringComparison.Ordinal);
        });
        using var detailContext = new BunitContext();
        Configure(detailContext, new TestTransientUiService { Detail = OperatorUiResult<CameraAgentTransientOperatorDetail>.Success(detail) });
        var page = detailContext.Render<TransientDetail>(parameters => parameters.Add(component => component.CandidateId, detail.Candidate.CandidateId));
        page.WaitForAssertion(() =>
        {
            StringAssert.Contains(page.Find("h1").TextContent, "not assessed", StringComparison.OrdinalIgnoreCase);
            var panel = page.Find(".science-grid > .science-panel:nth-child(2)");
            Assert.IsFalse(panel.TextContent.Contains("Retained assessment", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(panel.TextContent.Contains("Inferred", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void RetainedUnknownClassificationPreservesItsLocalAuthorityScope()
    {
        using var context = new BunitContext();
        var detail = Detail() with { AssessmentEvidence = Detail().AssessmentEvidence with { Classification = "Unknown" } };
        Configure(context, new TestTransientUiService { Detail = OperatorUiResult<CameraAgentTransientOperatorDetail>.Success(detail) });
        var page = context.Render<TransientDetail>(parameters => parameters.Add(component => component.CandidateId, detail.Candidate.CandidateId));
        page.WaitForAssertion(() =>
        {
            Assert.AreEqual("Unresolved candidate", page.Find("h1").TextContent);
            StringAssert.Contains(page.Find(".science-grid > .science-panel:nth-child(2)").TextContent,
                "Local detector authority: Authoritative", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    [DataRow("all")]
    [DataRow("unresolved")]
    public void FailedCandidateEvidenceReadsAreDisclosedWithoutInventingClassification(string classification)
    {
        using var context = new BunitContext();
        Configure(context, new TestTransientUiService
        {
            Page = OperatorUiResult<CameraAgentTransientOperatorPage>.Success(new([Candidate()], null)),
            Detail = OperatorUiResult<CameraAgentTransientOperatorDetail>.Failure(OperatorUiResultKind.Unavailable, "Unavailable")
        });
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/transients?classification=" + classification);
        var page = context.Render<TransientPage>();
        page.WaitForAssertion(() =>
        {
            StringAssert.Contains(page.Find(".event-evidence-unavailable").TextContent,
                "Details unavailable for 1 local candidate", StringComparison.Ordinal);
            Assert.AreEqual(classification == "all" ? 1 : 0, page.FindAll(".event-card").Count);
        });
    }

    [TestMethod]
    public void CalendarDefaultsToTheFilteredCandidatesObservingMonth()
    {
        using var context = new BunitContext();
        var older = Candidate();
        var newer = older with { CandidateId = Guid.NewGuid(), CreatedUtc = older.CreatedUtc.AddMonths(1) };
        Configure(context, new TestTransientUiService
        {
            Page = OperatorUiResult<CameraAgentTransientOperatorPage>.Success(new([newer, older], null)),
            DetailHandler = id => OperatorUiResult<CameraAgentTransientOperatorDetail>.Success(Detail() with
            {
                Candidate = id == newer.CandidateId ? newer : older,
                AssessmentEvidence = Detail().AssessmentEvidence with { Classification = id == newer.CandidateId ? "Satellite" : "Meteor" }
            })
        });
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/transients?view=calendar&classification=fireball");
        var page = context.Render<TransientPage>();
        page.WaitForAssertion(() =>
        {
            Assert.AreEqual("July 2026", page.Find(".event-month-heading h2").TextContent);
            Assert.HasCount(1, page.FindAll(".event-month-day.has-event a"));
        });
    }

    [TestMethod]
    public void CalendarDisclosesMatchingCandidatesInOtherMonths()
    {
        using var context = new BunitContext();
        var older = Candidate() with { CreatedUtc = new DateTimeOffset(2026, 7, 31, 20, 0, 0, TimeSpan.Zero) };
        var newer = older with { CandidateId = Guid.NewGuid(), CreatedUtc = older.CreatedUtc.AddDays(1) };
        Configure(context, new TestTransientUiService
        {
            Page = OperatorUiResult<CameraAgentTransientOperatorPage>.Success(new([newer, older], null)),
            DetailHandler = id => OperatorUiResult<CameraAgentTransientOperatorDetail>.Success(Detail() with
            {
                Candidate = id == newer.CandidateId ? newer : older
            })
        });
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/transients?view=calendar");
        var page = context.Render<TransientPage>();
        page.WaitForAssertion(() =>
        {
            Assert.AreEqual("August 2026", page.Find(".event-month-heading h2").TextContent);
            StringAssert.Contains(page.Find(".event-other-months").TextContent,
                "1 matching local candidate in other months", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void LatestSummaryDoesNotAssertEmptyDuringLoadingOrFailure()
    {
        using var context = new BunitContext();
        var pending = new TaskCompletionSource<OperatorUiResult<CameraAgentTransientOperatorPage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Configure(context, new TestTransientUiService { PageHandler = (_, _) => new(pending.Task) });
        var page = context.Render<TransientPage>();
        Assert.AreEqual("Loading", page.Find(".event-summary article:last-child strong").TextContent);
        pending.SetResult(OperatorUiResult<CameraAgentTransientOperatorPage>.Failure(OperatorUiResultKind.Unavailable, "Unavailable"));
        page.WaitForAssertion(() =>
        {
            Assert.AreEqual("Unavailable", page.Find(".event-summary article:last-child strong").TextContent);
            StringAssert.Contains(page.Find(".event-summary article:last-child small").TextContent, "could not be read", StringComparison.Ordinal);
        });
    }

    private static void Configure(BunitContext context, TestTransientUiService service)
    {
        RetainedPreviewImageTestSupport.Configure(context);
        context.Services.AddSingleton<ICameraAgentTransientUiService>(service);
        context.Services.AddSingleton<IObservingDayCalendarProvider>(
            new FixedObservingDayCalendarProvider(ObservingDayCalendar.Create("America/Phoenix")));
        context.Services.AddSingleton<ICameraAgentEventEvidenceUiService>(new CameraAgentEventEvidenceUiService(
            service, new TestOperatorUiService(), Mock.Of<ICameraAgentProcessingGraphUiService>(), new TestOperatorUiService()));
    }

    private static CameraAgentTransientOperatorCandidate Candidate() => new(
        Guid.Parse("10000000-0000-0000-0000-000000000001"),
        Guid.Parse("20000000-0000-0000-0000-000000000001"),
        "Complete",
        "Validated",
        "finalized",
        OperatorUiTestData.Now.AddMinutes(-1),
        OperatorUiTestData.Now,
        "Available",
        "Available",
        "Available",
        "Available");

    private static CameraAgentTransientOperatorDetail Detail()
    {
        var candidate = Candidate();
        return new(
            candidate,
            new CameraAgentTransientCandidateEvidence(
                "Available",
                candidate.CreatedUtc,
                Guid.Parse("30000000-0000-0000-0000-000000000001"),
                5,
                new(3552, 3552, 10, 20, 30, 40, 3),
                new(200, 3, 5, 9000, 4095, 2, 1),
                ["transient.elongated-track"]),
            new CameraAgentTransientExtractionEvidence("Pending"),
            new CameraAgentTransientExtractionEvidence("Absent"),
            new CameraAgentTransientAssessmentEvidence(
                "Available",
                Guid.Parse("40000000-0000-0000-0000-000000000001"),
                "Authoritative",
                "Meteor",
                "Fireball",
                950000,
                1,
                ["transient.saturated-brightness"],
                new string('A', 64)),
            new CameraAgentTransientFinalEvidence(
                "Available",
                "Validated",
                1,
                candidate.CreatedUtc.AddSeconds(-5),
                candidate.CreatedUtc,
                1,
                1,
                new string('B', 64)));
    }

    private sealed class TestTransientUiService : ICameraAgentTransientUiService
    {
        public ValueTask<OperatorUiResult<TransientCaptureStageView>> GetCaptureStagesAsync(Guid captureId, CancellationToken cancellationToken)
            => ValueTask.FromResult(OperatorUiResult<TransientCaptureStageView>.Success(new(captureId, [])));

        internal OperatorUiResult<CameraAgentTransientOperatorPage> Page { get; init; } =
            OperatorUiResult<CameraAgentTransientOperatorPage>.Success(new([], null));
        internal OperatorUiResult<CameraAgentTransientOperatorDetail> Detail { get; init; } =
            OperatorUiResult<CameraAgentTransientOperatorDetail>.Failure(
                OperatorUiResultKind.NotFound, "The transient candidate was not found.");

        internal Func<CameraAgentTransientOperatorQuery, CancellationToken, ValueTask<OperatorUiResult<CameraAgentTransientOperatorPage>>>? PageHandler { get; init; }

        public ValueTask<OperatorUiResult<CameraAgentTransientOperatorPage>> GetPageAsync(
            CameraAgentTransientOperatorQuery query,
            CancellationToken cancellationToken) => PageHandler?.Invoke(query, cancellationToken) ?? ValueTask.FromResult(Page);

        internal Func<Guid, OperatorUiResult<CameraAgentTransientOperatorDetail>>? DetailHandler { get; init; }

        public ValueTask<OperatorUiResult<CameraAgentTransientOperatorDetail>> GetCandidateAsync(
            Guid candidateId,
            CancellationToken cancellationToken) => ValueTask.FromResult(DetailHandler?.Invoke(candidateId) ?? Detail);
    }
}
