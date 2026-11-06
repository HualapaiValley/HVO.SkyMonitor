using Bunit;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

/// <summary>
/// The compact orbital data notice: coverage relative to the requested population, element age kept separate from
/// retrieval time, and no state that turns missing or unreadable data into a claim about the sky.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalDataStatusNoticeTests
{
    private static readonly DateTimeOffset Now = TestOrbitalUiService.Now;

    private const string HaltedNoStatusLine =
        "Provider halted: No valid HTTP status recorded. Refresh waits until an operator resumes the provider.";

    private static readonly string[] FreshLines =
    [
        "Requested population: Synthetic active group. Complete for the requested population.",
        "Known exclusions: synthetic-debris.",
        "Newest element epoch is 6h 0m old.",
    ];

    private static readonly string[] NoExclusionLines =
    [
        "Requested population: Synthetic active group. Partial for the requested population.",
        "No known exclusions are listed.",
        "Newest element epoch is 6h 0m old.",
    ];

    [TestMethod]
    public void UnreadableStatus_MakesNoDataClaim()
    {
        var content = OrbitalDataStatusNotice.Compose(null);

        Assert.AreEqual("Orbital data status unavailable.", content.Headline);
        Assert.AreEqual("failure", content.Tone);
        Assert.AreEqual(OrbitalDataStatusNotice.StatusUnavailableText, content.Lines.Single());
        StringAssert.Contains(content.Lines[0], "this does not mean no satellites are overhead", StringComparison.Ordinal);
    }

    [TestMethod]
    public void UnavailableStore_MakesNoDataClaimEvenWithAnInstalledSnapshot()
    {
        var content = OrbitalDataStatusNotice.Compose(TestOrbitalUiService.View(
            health: OrbitalElementsHealthState.Unhealthy, healthReason: OrbitalElementsAvailability.StoreUnavailableReasonCode));

        Assert.AreEqual("Orbital data unknown.", content.Headline);
        Assert.AreEqual("failure", content.Tone);
        Assert.AreEqual(OrbitalDataStatusNotice.StoreUnavailableText, content.Lines.Single());
    }

    [TestMethod]
    public void NoActiveSnapshot_IsMissingDataNotAnEmptySky()
    {
        var status = TestOrbitalUiService.Status() with
        {
            CurrentManifestSha256 = null,
            CurrentPayloadSha256 = null,
            Reason = OrbitalElementsOperations.NoActiveSnapshotReasonCode,
        };

        var content = OrbitalDataStatusNotice.Compose(TestOrbitalUiService.View(status));

        Assert.IsNull(content.Headline);
        Assert.AreEqual("pending", content.Tone);
        Assert.AreEqual(OrbitalOperatorText.NoDataText, content.Lines.Single());
        StringAssert.StartsWith(content.Lines[0], "No orbital data installed.", StringComparison.Ordinal);
        StringAssert.Contains(content.Lines[0], "this does not mean no satellites are overhead", StringComparison.Ordinal);
    }

    [TestMethod]
    public void UnreadableManifest_SaysCoverageCannotBeShownAndElementAgeIsUnknown()
    {
        var view = TestOrbitalUiService.View(withManifest: false,
            manifestUnavailableReason: OrbitalOperatorText.ManifestRetiredReasonCode);

        var content = OrbitalDataStatusNotice.Compose(view);

        Assert.IsTrue(view.ElementAgeUnknown);
        Assert.IsTrue(view.ElementsStale, "With no readable epoch the shared rule treats the elements as stale.");
        Assert.AreEqual(OrbitalElementsHealthEvaluator.IsElementStale(view.ObservedUtc, null), view.ElementsStale);
        Assert.AreEqual(OrbitalDataState.Stale, view.State);
        Assert.AreEqual("Orbital elements are stale.", content.Headline);
        Assert.AreEqual("warning", content.Tone);
        Assert.HasCount(2, content.Lines);
        Assert.AreEqual(
            "Orbital elements are installed, but their coverage cannot be shown. That snapshot was retired by retention. Reload to see the current snapshot.",
            content.Lines[0]);
        Assert.AreEqual(OrbitalDataStatusNotice.ElementAgeUnknownText, content.Lines[1]);
        Assert.DoesNotContain("48 h", content.Lines[1], "An unknown age is never claimed to be past the limit.");
    }

    [TestMethod]
    public void FreshSnapshot_StatesCoverageExclusionsAndElementAge()
    {
        var content = OrbitalDataStatusNotice.Compose(TestOrbitalUiService.View());

        Assert.AreEqual("Orbital elements installed.", content.Headline);
        Assert.AreEqual("success", content.Tone);
        CollectionAssert.AreEqual(FreshLines, content.Lines.ToArray());
    }

    [TestMethod]
    public void NoKnownExclusions_IsStatedRatherThanImplied()
    {
        var manifest = TestOrbitalUiService.Manifest() with
        {
            Coverage = new OrbitalPopulationCoverage("Synthetic active group", OrbitalPopulationCompleteness.Partial, []),
        };

        var content = OrbitalDataStatusNotice.Compose(TestOrbitalUiService.View(manifest: manifest));

        CollectionAssert.AreEqual(NoExclusionLines, content.Lines.ToArray());
    }

    [TestMethod]
    public void OldElementsRetrievedRecently_AreStaleByElementAge()
    {
        var status = TestOrbitalUiService.Status() with
        {
            LastRetrievalSuccessUtc = Now.AddMinutes(-5),
            LastActivationUtc = Now.AddMinutes(-5),
        };
        var view = TestOrbitalUiService.View(status, TestOrbitalUiService.Manifest(TimeSpan.FromHours(50)));

        var content = OrbitalDataStatusNotice.Compose(view);

        Assert.IsTrue(view.ElementsStale);
        Assert.IsFalse(view.RefreshOverdue, "A recent retrieval is not overdue, however old its elements are.");
        Assert.AreEqual("Orbital elements are stale.", content.Headline);
        Assert.AreEqual("warning", content.Tone);
        StringAssert.Contains(content.Lines[^1], "even the newest epoch is older than 48 h", StringComparison.Ordinal);
        StringAssert.Contains(content.Lines[^1], "They stay in use until a refresh replaces them.", StringComparison.Ordinal);
    }

    [TestMethod]
    public void RecentElementsWithAnOldRetrieval_AreOverdueByRetrievalTime()
    {
        var status = TestOrbitalUiService.Status() with
        {
            LastRetrievalSuccessUtc = Now.AddHours(-13),
            LastActivationUtc = Now.AddHours(-13),
        };
        var view = TestOrbitalUiService.View(status, TestOrbitalUiService.Manifest(TimeSpan.FromHours(14)));

        var content = OrbitalDataStatusNotice.Compose(view);

        Assert.IsFalse(view.ElementsStale);
        Assert.IsTrue(view.RefreshOverdue);
        Assert.AreEqual("Orbital refresh is overdue.", content.Headline);
        Assert.AreEqual("warning", content.Tone);
        Assert.AreEqual("Refresh is overdue: no successful retrieval in the last 12 h. The installed elements stay in use.", content.Lines[^1]);
    }

    [TestMethod]
    public void BothStaleAndOverdue_ListsBothAndLeadsWithElementAge()
    {
        var status = TestOrbitalUiService.Status() with
        {
            LastRetrievalSuccessUtc = Now.AddDays(-3),
            LastActivationUtc = Now.AddDays(-3),
        };

        var content = OrbitalDataStatusNotice.Compose(TestOrbitalUiService.View(status,
            TestOrbitalUiService.Manifest(TimeSpan.FromDays(4), TimeSpan.FromDays(5))));

        Assert.AreEqual("Orbital elements are stale.", content.Headline);
        Assert.HasCount(5, content.Lines);
        StringAssert.StartsWith(content.Lines[3], "The elements are stale", StringComparison.Ordinal);
        StringAssert.StartsWith(content.Lines[4], "Refresh is overdue", StringComparison.Ordinal);
    }

    [TestMethod]
    public void SnapshotWithNoRecordedRetrieval_IsOverdueWithoutFallingBackToTheActivationTime()
    {
        var status = TestOrbitalUiService.Status() with
        {
            LastRetrievalSuccessUtc = null,
            LastActivationUtc = Now.AddMinutes(-5),
        };
        var view = TestOrbitalUiService.View(status);

        var content = OrbitalDataStatusNotice.Compose(view);

        Assert.IsNull(view.RetrievalAge);
        Assert.IsTrue(view.RefreshOverdue, "Since round 1 every activation records its retrieval; none recorded is overdue.");
        Assert.AreEqual(OrbitalDataState.RefreshOverdue, view.State);
        Assert.AreEqual("Orbital refresh is overdue.", content.Headline);
        Assert.AreEqual("Refresh is overdue: no successful retrieval in the last 12 h. The installed elements stay in use.", content.Lines[^1]);
    }

    [TestMethod]
    public void NoSnapshot_IsNeverOverdueBecauseHealthJudgesOverdueOnlyWithASnapshot()
    {
        var status = TestOrbitalUiService.Status() with
        {
            CurrentManifestSha256 = null,
            CurrentPayloadSha256 = null,
            LastRetrievalSuccessUtc = null,
            LastActivationUtc = null,
            Reason = OrbitalElementsOperations.NoActiveSnapshotReasonCode,
        };

        var view = TestOrbitalUiService.View(status);

        Assert.IsFalse(view.RefreshOverdue);
        Assert.IsFalse(view.ElementsStale);
        Assert.AreEqual(OrbitalDataState.NoData, view.State);
    }

    [TestMethod]
    public void RefreshOff_StillReportsStaleElementsButNeverOverdue()
    {
        var status = TestOrbitalUiService.Status() with
        {
            Enabled = false,
            LastRetrievalSuccessUtc = Now.AddDays(-5),
            Reason = OrbitalElementsOperations.DisabledReasonCode,
            RecoveryAction = OrbitalRefreshRecoveryAction.Configure,
        };
        var view = TestOrbitalUiService.View(status, TestOrbitalUiService.Manifest(TimeSpan.FromDays(4), TimeSpan.FromDays(5)));

        var content = OrbitalDataStatusNotice.Compose(view);

        Assert.IsTrue(view.ElementsStale, "The installed data's age matters to consumers even with refresh off.");
        Assert.IsFalse(view.RefreshOverdue, "Health returns early with refresh off, so nothing is overdue.");
        Assert.AreEqual(OrbitalDataState.Stale, view.State);
        Assert.AreEqual("Orbital elements are stale.", content.Headline);
        Assert.IsFalse(content.Lines.Any(static line => line.StartsWith("Refresh is overdue", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(0d, 1d)]
    [DataRow(47.99d, 11.99d)]
    [DataRow(48d, 12d)]
    [DataRow(48.01d, 12.01d)]
    [DataRow(500d, 500d)]
    [DataRow(-2d, -1d)]
    public void Freshness_IsExactlyTheHealthRule(double newestEpochHours, double retrievalHours)
    {
        var retrieved = Now - TimeSpan.FromHours(retrievalHours);
        var view = TestOrbitalUiService.View(
            TestOrbitalUiService.Status() with { LastRetrievalSuccessUtc = retrieved },
            TestOrbitalUiService.Manifest(TimeSpan.FromHours(newestEpochHours), TimeSpan.FromHours(Math.Max(newestEpochHours, 0) + 1)));
        var newest = view.Manifest!.MaximumEpochUtc;

        Assert.AreEqual(Now, view.ObservedUtc);
        Assert.AreEqual(OrbitalElementsHealthEvaluator.IsElementStale(view.ObservedUtc, newest), view.ElementsStale);
        Assert.AreEqual(
            OrbitalElementsHealthEvaluator.IsRefreshOverdue(view.ObservedUtc, retrieved, TestOrbitalUiService.RefreshOptions),
            view.RefreshOverdue);
        Assert.AreEqual(newestEpochHours > 48, view.ElementsStale);
        Assert.AreEqual(retrievalHours > 12, view.RefreshOverdue);
    }

    [TestMethod]
    [DataRow(OrbitalOperatorText.ProviderOutcomeUnknownReasonCode)]
    [DataRow(OrbitalOperatorText.InvalidHttpStatusReasonCode)]
    public void HaltWithoutAValidStatus_SaysNoValidHttpStatusWasRecorded(string haltReasonCode)
    {
        var halt = new OrbitalProviderHalt(Now.AddHours(-1), null, haltReasonCode + ": no valid HTTP status recorded");
        var withData = TestOrbitalUiService.View(HaltedStatus(halt));
        var withoutData = TestOrbitalUiService.View(HaltedStatus(halt) with
        {
            CurrentManifestSha256 = null,
            CurrentPayloadSha256 = null,
        });

        foreach (var content in new[] { OrbitalDataStatusNotice.Compose(withData), OrbitalDataStatusNotice.Compose(withoutData) })
        {
            Assert.AreEqual("warning", content.Tone);
            Assert.AreEqual(HaltedNoStatusLine, content.Lines[^1]);
            foreach (var line in content.Lines)
            {
                Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(line, @"HTTP\s*\d"), line);
            }
        }
        Assert.AreEqual(OrbitalOperatorText.NoDataText, OrbitalDataStatusNotice.Compose(withoutData).Lines[0]);
    }

    [TestMethod]
    public void HaltWithARecordedStatus_NamesIt()
    {
        var halt = new OrbitalProviderHalt(Now.AddHours(-1), 403, "orbital.provider-http-403");

        var content = OrbitalDataStatusNotice.Compose(TestOrbitalUiService.View(HaltedStatus(halt)));

        Assert.AreEqual("Provider halted: HTTP 403. Refresh waits until an operator resumes the provider.", content.Lines[^1]);
        Assert.AreEqual("warning", content.Tone);
    }

    [TestMethod]
    public void RenderedNotice_UsesTheSuppliedIdAndStatusRole()
    {
        using var context = new BunitContext();

        var cut = context.Render<OrbitalDataStatusNotice>(parameters => parameters
            .Add(notice => notice.View, TestOrbitalUiService.View())
            .Add(notice => notice.Id, "current-sky-orbital-notice"));

        var notice = cut.Find("#current-sky-orbital-notice");
        Assert.AreEqual("status", notice.GetAttribute("role"));
        Assert.AreEqual("Orbital elements installed.", notice.QuerySelector("strong")!.TextContent);
        Assert.HasCount(3, notice.QuerySelectorAll(".orbital-notice-line"));
        Assert.IsNotNull(notice.QuerySelector(".status-icon.success"));
    }

    private static OrbitalRefreshStatus HaltedStatus(OrbitalProviderHalt halt) => TestOrbitalUiService.Status() with
    {
        ProviderHalt = halt,
        Reason = OrbitalElementsOperations.ProviderHaltedReasonCode,
        RecoveryAction = OrbitalRefreshRecoveryAction.ResumeProvider,
    };
}
