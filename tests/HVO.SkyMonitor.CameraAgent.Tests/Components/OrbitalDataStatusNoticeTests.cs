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
    public void UnreadableManifest_SaysCoverageCannotBeShown()
    {
        var content = OrbitalDataStatusNotice.Compose(TestOrbitalUiService.View(withManifest: false,
            manifestUnavailableReason: OrbitalOperatorText.ManifestRetiredReasonCode));

        Assert.AreEqual("warning", content.Tone);
        Assert.AreEqual(
            "Orbital elements are installed, but their coverage cannot be shown. That snapshot was retired by retention. Reload to see the current snapshot.",
            content.Lines.Single());
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
}
