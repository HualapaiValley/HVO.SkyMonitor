using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
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
        context.Services.AddSingleton<ICameraAgentTransientUiService>(service);

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
        context.Services.AddSingleton<ICameraAgentTransientUiService>(new TestTransientUiService
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
            StringAssert.Contains(cut.Markup, "Meteor", StringComparison.Ordinal);
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
        context.Services.AddSingleton<ICameraAgentTransientUiService>(new TestTransientUiService
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
        internal OperatorUiResult<CameraAgentTransientOperatorPage> Page { get; init; } =
            OperatorUiResult<CameraAgentTransientOperatorPage>.Success(new([], null));
        internal OperatorUiResult<CameraAgentTransientOperatorDetail> Detail { get; init; } =
            OperatorUiResult<CameraAgentTransientOperatorDetail>.Failure(
                OperatorUiResultKind.NotFound, "The transient candidate was not found.");

        public ValueTask<OperatorUiResult<CameraAgentTransientOperatorPage>> GetPageAsync(
            CameraAgentTransientOperatorQuery query,
            CancellationToken cancellationToken) => ValueTask.FromResult(Page);

        public ValueTask<OperatorUiResult<CameraAgentTransientOperatorDetail>> GetCandidateAsync(
            Guid candidateId,
            CancellationToken cancellationToken) => ValueTask.FromResult(Detail);
    }
}
