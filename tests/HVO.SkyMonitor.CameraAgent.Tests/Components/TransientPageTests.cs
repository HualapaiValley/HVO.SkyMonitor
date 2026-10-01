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

    private static void Configure(BunitContext context, TestTransientUiService service)
    {
        context.Services.AddSingleton<ICameraAgentTransientUiService>(service);
        context.Services.AddSingleton<IObservingDayCalendarProvider>(
            new FixedObservingDayCalendarProvider(ObservingDayCalendar.Create("America/Phoenix")));
        context.Services.AddSingleton<ICameraAgentEventEvidenceUiService>(new CameraAgentEventEvidenceUiService(
            service, new TestOperatorUiService(), Mock.Of<ICameraAgentProcessingGraphUiService>()));
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

        public ValueTask<OperatorUiResult<CameraAgentTransientOperatorPage>> GetPageAsync(
            CameraAgentTransientOperatorQuery query,
            CancellationToken cancellationToken) => ValueTask.FromResult(Page);

        internal Func<Guid, OperatorUiResult<CameraAgentTransientOperatorDetail>>? DetailHandler { get; init; }

        public ValueTask<OperatorUiResult<CameraAgentTransientOperatorDetail>> GetCandidateAsync(
            Guid candidateId,
            CancellationToken cancellationToken) => ValueTask.FromResult(DetailHandler?.Invoke(candidateId) ?? Detail);
    }
}
