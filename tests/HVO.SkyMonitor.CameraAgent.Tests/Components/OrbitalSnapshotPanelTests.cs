using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

/// <summary>
/// Software &amp; Catalog's orbital panel over a scripted operator service: what each refresh state shows, the bounded
/// Refresh and Resume commands with their idempotency keys, and that no path or header value reaches the page.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalSnapshotPanelTests
{
    [TestMethod]
    [DataRow(true, false, true)]
    [DataRow(true, true, false)]
    [DataRow(false, false, false)]
    public void Setup_IsOfferedOnlyForEnabledRefreshWithoutAnExistingDefinition(bool enabled, bool exists, bool offered)
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(TestOrbitalUiService.View(TestOrbitalUiService.Status() with { Enabled = enabled },
            schedule: TestOrbitalUiService.Schedule() with { Exists = exists }));
        var cut = context.Render<OrbitalSnapshotPanel>();
        cut.WaitForElement("#orbital-enabled");
        Assert.AreEqual(enabled ? "On" : "Off", cut.Find("#orbital-enabled").TextContent);
        Assert.IsNotNull(cut.Find("a[href='/operations/site#orbitalElements-switch']"));
        Assert.AreEqual(offered ? 1 : 0, cut.FindAll("#orbital-setup").Count);
        if (offered) Assert.AreEqual("/operations/automations?setup=orbital", cut.Find("#orbital-setup a").GetAttribute("href"));
        Assert.Contains("shared NAT", cut.Markup, StringComparison.Ordinal);
        Assert.IsEmpty(service.RefreshKeys);
    }

    private static readonly DateTimeOffset Now = TestOrbitalUiService.Now;
    private static readonly string ShortManifest = "sha256:" + new string('a', 8) + "…" + new string('a', 8);

    [TestMethod]
    public void SuccessfulActivation_ShowsFreshCountsHashesEpochsAndLinks()
    {
        using var context = new BunitContext();
        Configure(context);

        var cut = context.Render<OrbitalSnapshotPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Fresh", cut.Find("#orbital-state").TextContent);
            StringAssert.Contains(cut.Find("#orbital-state").ClassName, "success", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find("#orbital-data-notice").TextContent, "Orbital elements installed.", StringComparison.Ordinal);
            StringAssert.StartsWith(cut.Find("#orbital-provider").TextContent, "Synthetic Provider / celestrak:gp:active", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find("#orbital-coverage").TextContent, "Synthetic active group", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find("#orbital-coverage").TextContent, "excludes synthetic-debris", StringComparison.Ordinal);
            Assert.AreEqual("12,345 records / 12,340 objects", cut.Find("#orbital-counts").TextContent);
            StringAssert.StartsWith(cut.Find("#orbital-epochs").TextContent, "2026-07-22 06:00:00 UTC to 2026-07-23 06:00:00 UTC", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find("#orbital-epochs").TextContent, "Oldest 1d 6h old / newest 6h 0m old", StringComparison.Ordinal);
            Assert.AreEqual(ShortManifest, cut.Find("#orbital-manifest-sha").TextContent);
            Assert.AreEqual("sha256:" + TestOrbitalUiService.ManifestSha256, cut.Find("#orbital-manifest-sha").GetAttribute("title"));
            Assert.AreEqual("sha256:" + TestOrbitalUiService.PayloadSha256, cut.Find("#orbital-payload-sha").GetAttribute("title"));
            var terms = cut.Find("#orbital-terms a");
            Assert.AreEqual("https://provider.example.invalid/terms", terms.GetAttribute("href"));
            Assert.AreEqual("noopener noreferrer", terms.GetAttribute("rel"));
            Assert.AreEqual(OrbitalOperatorText.ValidityText, cut.Find("#orbital-validity").TextContent);
            Assert.AreEqual("On", cut.Find("#orbital-enabled").TextContent);
            StringAssert.StartsWith(cut.Find("#orbital-schedule").TextContent, "Enabled, every 6 h", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find("#orbital-freshness").TextContent, "6h 0m old", StringComparison.Ordinal);
            Assert.AreEqual("/operations/automations?definition=orbital-elements-refresh", cut.Find("#orbital-schedule-link").GetAttribute("href"));
            Assert.AreEqual("/operations/automations?view=runs", cut.Find("#orbital-runs-link").GetAttribute("href"));
            Assert.IsFalse(cut.Find("#orbital-refresh").HasAttribute("disabled"));
            Assert.IsEmpty(cut.FindAll("#orbital-resume"));
            Assert.AreEqual(OrbitalOperatorText.Recovery(OrbitalRefreshRecoveryAction.None), cut.Find("#orbital-recovery").TextContent);
        });
    }

    [TestMethod]
    public void RecentRetrievalOfOldElements_KeepsTheirAgeAndReadsStale()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var status = TestOrbitalUiService.Status() with
        {
            LastAttemptUtc = Now.AddMinutes(-10),
            LastRetrievalSuccessUtc = Now.AddMinutes(-10),
            LastActivationUtc = Now.AddMinutes(-10),
        };
        service.GetHandler = _ => Success(TestOrbitalUiService.View(status,
            TestOrbitalUiService.Manifest(TimeSpan.FromHours(60), TimeSpan.FromHours(80))));

        var cut = context.Render<OrbitalSnapshotPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Stale", cut.Find("#orbital-state").TextContent);
            StringAssert.Contains(cut.Find("#orbital-freshness").TextContent, "2d 12h old", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find("#orbital-last-retrieval").TextContent, "(10m 0s ago)", StringComparison.Ordinal);
            var notice = cut.Find("#orbital-data-notice").TextContent;
            StringAssert.Contains(notice, "Orbital elements are stale.", StringComparison.Ordinal);
            StringAssert.Contains(notice, "Newest element epoch is 2d 12h old.", StringComparison.Ordinal);
            Assert.AreEqual("12,345 records / 12,340 objects", cut.Find("#orbital-counts").TextContent,
                "Stale last-known-good data stays visible.");
            Assert.AreEqual(ShortManifest, cut.Find("#orbital-manifest-sha").TextContent);
            Assert.IsFalse(cut.Markup.Contains("no satellites", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(cut.Markup.Contains(OrbitalOperatorText.NoDataText, StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void TooEarlyRefresh_ReportsTheNextPermittedAttempt()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var next = Now.AddMinutes(90);
        service.RefreshHandler = (_, _) => ValueTask.FromResult(TestOrbitalUiService.Command(
            OperatorUiResultKind.Conflict, OrbitalElementsCommandStatus.Conflict,
            OrbitalElementsOperations.NotYetPermittedReasonCode, next));
        var cut = context.Render<OrbitalSnapshotPanel>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#orbital-refresh").HasAttribute("disabled")));

        cut.Find("#orbital-refresh").Click();

        cut.WaitForAssertion(() =>
        {
            var message = cut.Find("#orbital-message");
            Assert.AreEqual("alert", message.GetAttribute("role"));
            StringAssert.Contains(message.TextContent, "The next attempt is permitted at 23 Jul 2026 13:30:00", StringComparison.Ordinal);
            Assert.AreEqual(2, service.Reads, "A definite outcome re-reads the status.");
            Assert.AreEqual("Refresh now", cut.Find("#orbital-refresh").TextContent);
        });
    }

    [TestMethod]
    public void ThrottledStatus_DisablesRefreshWithTheNextPermittedTime()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(TestOrbitalUiService.View(TestOrbitalUiService.Status() with
        {
            NextPermittedAttemptUtc = Now.AddMinutes(45),
            RecoveryAction = OrbitalRefreshRecoveryAction.WaitForPermittedAttempt,
        }));

        var cut = context.Render<OrbitalSnapshotPanel>();

        cut.WaitForAssertion(() =>
        {
            var refresh = cut.Find("#orbital-refresh");
            Assert.IsTrue(refresh.HasAttribute("disabled"));
            Assert.AreEqual("orbital-refresh-unavailable", refresh.GetAttribute("aria-describedby"));
            var reason = cut.Find("#orbital-refresh-unavailable").TextContent;
            Assert.AreEqual(reason, refresh.GetAttribute("title"));
            StringAssert.Contains(reason, "The next attempt is permitted at 23 Jul 2026 12:45:00", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find("#orbital-next-attempt").TextContent, "Next permitted 23 Jul 12:45", StringComparison.Ordinal);
        });
        cut.Find("#orbital-refresh").Click();
        Assert.IsEmpty(service.RefreshKeys, "A disabled Refresh never reaches the service.");
    }

    [TestMethod]
    public void UnauthorizedRead_NavigatesToAccessDenied()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => ValueTask.FromResult(OperatorUiResult<OrbitalDataStatusView>.Failure(
            OperatorUiResultKind.Unauthorized, "Authorization is required."));

        var cut = context.Render<OrbitalSnapshotPanel>();

        var navigation = context.Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => Assert.IsTrue(navigation.Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal)));
        Assert.IsEmpty(cut.FindAll(".software-orbital-facts"));
    }

    [TestMethod]
    public void DisabledDefinitionWithAnUnavailableSchedule_DisablesRefreshAndStillShowsTheStatus()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var status = TestOrbitalUiService.Status() with
        {
            Reason = OrbitalElementsOperations.DefinitionDisabledReasonCode,
            RecoveryAction = OrbitalRefreshRecoveryAction.Configure,
        };
        service.GetHandler = _ => Success(TestOrbitalUiService.View(status,
            health: OrbitalElementsHealthState.Degraded) with
        {
            Schedule = null,
        });

        var cut = context.Render<OrbitalSnapshotPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Fresh", cut.Find("#orbital-state").TextContent);
            Assert.AreEqual("12,345 records / 12,340 objects", cut.Find("#orbital-counts").TextContent);
            Assert.IsTrue(cut.Find("#orbital-refresh").HasAttribute("disabled"));
            Assert.AreEqual(OrbitalOperatorText.Describe(OrbitalElementsOperations.DefinitionDisabledReasonCode),
                cut.Find("#orbital-refresh-unavailable").TextContent);
        });
    }

    [TestMethod]
    public void UnauthorizedRefresh_NavigatesToAccessDenied()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.RefreshHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<OrbitalCommandView>.Failure(
            OperatorUiResultKind.Unauthorized, "Authorization is required."));
        var cut = context.Render<OrbitalSnapshotPanel>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#orbital-refresh").HasAttribute("disabled")));

        cut.Find("#orbital-refresh").Click();

        var navigation = context.Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => Assert.IsTrue(navigation.Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal)));
        Assert.HasCount(1, service.RefreshKeys);
    }

    [TestMethod]
    public void ReadOnlyOperator_SeesTheStateButNoEnabledCommand()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(TestOrbitalUiService.View(Halted(403), canMutate: false,
            health: OrbitalElementsHealthState.Degraded));

        var cut = context.Render<OrbitalSnapshotPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Find("#orbital-refresh").HasAttribute("disabled"));
            Assert.AreEqual("Operations change rights are required to refresh orbital data.", cut.Find("#orbital-refresh-unavailable").TextContent);
            Assert.IsTrue(cut.Find("#orbital-resume").HasAttribute("disabled"));
            Assert.AreEqual("Operations change rights are required to resume the provider.", cut.Find("#orbital-resume-unavailable").TextContent);
        });
        cut.Find("#orbital-resume").Click();
        Assert.IsEmpty(cut.FindAll("dialog"));
        Assert.IsEmpty(service.Resumes);
    }

    [TestMethod]
    public void PendingRefresh_IgnoresADuplicateClick()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var completion = new TaskCompletionSource<OperatorUiResult<OrbitalCommandView>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.RefreshHandler = (_, _) => new ValueTask<OperatorUiResult<OrbitalCommandView>>(completion.Task);
        var cut = context.Render<OrbitalSnapshotPanel>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#orbital-refresh").HasAttribute("disabled")));

        cut.Find("#orbital-refresh").Click();
        cut.Find("#orbital-refresh").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.HasCount(1, service.RefreshKeys);
            Assert.AreEqual("Requesting…", cut.Find("#orbital-refresh").TextContent);
            Assert.IsTrue(cut.Find("#orbital-refresh").HasAttribute("disabled"));
            Assert.IsTrue(cut.Find("#orbital-reload").HasAttribute("disabled"));
        });
        completion.SetResult(TestOrbitalUiService.Command(OperatorUiResultKind.Success,
            OrbitalElementsCommandStatus.Accepted, OrbitalElementsOperations.RefreshQueuedReasonCode));
        cut.WaitForAssertion(() =>
        {
            StringAssert.StartsWith(cut.Find("#orbital-message").TextContent, "Refresh queued.", StringComparison.Ordinal);
            Assert.AreEqual("status", cut.Find("#orbital-message").GetAttribute("role"));
            Assert.AreEqual("Refresh now", cut.Find("#orbital-refresh").TextContent);
        });
        Assert.HasCount(1, service.RefreshKeys);
        Assert.AreEqual(OrbitalSnapshotPanel.RefreshReason, service.RefreshReasons.Single());
        StringAssert.StartsWith(service.RefreshKeys[0], "ui-", StringComparison.Ordinal);
    }

    [TestMethod]
    public void RefreshKey_IsReusedOnlyAfterAnUnavailableOutcome()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var outcomes = new Queue<OperatorUiResult<OrbitalCommandView>>(
        [
            OperatorUiResult<OrbitalCommandView>.Failure(OperatorUiResultKind.Unavailable, "The orbital command could not be completed."),
            TestOrbitalUiService.Command(OperatorUiResultKind.Conflict, OrbitalElementsCommandStatus.Conflict,
                OrbitalElementsOperations.NotYetPermittedReasonCode),
            TestOrbitalUiService.Command(OperatorUiResultKind.Success, OrbitalElementsCommandStatus.Accepted,
                OrbitalElementsOperations.RefreshQueuedReasonCode),
        ]);
        service.RefreshHandler = (_, _) => ValueTask.FromResult(outcomes.Dequeue());
        var cut = context.Render<OrbitalSnapshotPanel>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#orbital-refresh").HasAttribute("disabled")));

        cut.Find("#orbital-refresh").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Retry refresh", cut.Find("#orbital-refresh").TextContent);
            StringAssert.Contains(cut.Find("#orbital-message").TextContent, "Retry sends the identical request with the same key", StringComparison.Ordinal);
        });
        cut.Find("#orbital-refresh").Click();
        cut.WaitForAssertion(() => Assert.AreEqual("Refresh now", cut.Find("#orbital-refresh").TextContent));
        cut.Find("#orbital-refresh").Click();
        cut.WaitForAssertion(() => Assert.HasCount(3, service.RefreshKeys));

        Assert.AreEqual(service.RefreshKeys[0], service.RefreshKeys[1], "An unknown outcome is retried with the same key.");
        Assert.AreNotEqual(service.RefreshKeys[1], service.RefreshKeys[2], "A definite outcome ends the intent.");
    }

    [TestMethod]
    public void ProviderHalt_ShowsResumeAndTheDialogBindsTheDisplayedHaltTime()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var halted = Halted(403);
        var reads = 0;
        service.GetHandler = _ => Success(++reads == 1
            ? TestOrbitalUiService.View(halted, health: OrbitalElementsHealthState.Degraded)
            : TestOrbitalUiService.View());
        var cut = context.Render<OrbitalSnapshotPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Halted", cut.Find("#orbital-state").TextContent);
            Assert.AreEqual("HTTP 403", cut.Find("#orbital-halt-status").TextContent);
            Assert.AreEqual(OrbitalOperatorText.Describe("orbital.provider-http-403"), cut.Find("#orbital-halt-reason").TextContent);
            Assert.AreEqual(OrbitalOperatorText.Recovery(OrbitalRefreshRecoveryAction.ResumeProvider), cut.Find("#orbital-recovery").TextContent);
            Assert.IsTrue(cut.Find("#orbital-refresh").HasAttribute("disabled"));
            StringAssert.Contains(cut.Find("#orbital-refresh-unavailable").TextContent, "halted", StringComparison.Ordinal);
            Assert.IsFalse(cut.Find("#orbital-resume").HasAttribute("disabled"));
            Assert.AreEqual("Halted until an operator resumes the provider", cut.Find("#orbital-next-attempt").TextContent);
        });

        cut.Find("#orbital-resume").Click();
        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Find("#orbital-resume-since").TextContent, "2026-07-23 07:00:00 UTC", StringComparison.Ordinal);
            Assert.AreEqual("HTTP 403", cut.Find("#orbital-resume-status").TextContent);
        });
        cut.Find("#orbital-resume-reason").Change("  Provider confirmed the block was lifted.  ");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            Assert.IsEmpty(cut.FindAll("dialog"));
            StringAssert.StartsWith(cut.Find("#orbital-message").TextContent, "Provider resumed.", StringComparison.Ordinal);
            Assert.AreEqual("Fresh", cut.Find("#orbital-state").TextContent);
        });
        var resume = service.Resumes.Single();
        Assert.AreEqual(halted.ProviderHalt!.SinceUtc, resume.HaltSinceUtc);
        Assert.AreEqual("Provider confirmed the block was lifted.", resume.Reason);
        StringAssert.StartsWith(resume.Key, "ui-", StringComparison.Ordinal);
        Assert.AreEqual(2, service.Reads);
    }

    [TestMethod]
    public void ResumeConflict_ClosesTheDialogAndReReads()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(TestOrbitalUiService.View(Halted(429), health: OrbitalElementsHealthState.Degraded));
        service.ResumeHandler = (_, _, _) => ValueTask.FromResult(TestOrbitalUiService.Command(
            OperatorUiResultKind.Conflict, OrbitalElementsCommandStatus.Conflict, "orbital.halt-mismatch"));
        var cut = context.Render<OrbitalSnapshotPanel>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#orbital-resume").HasAttribute("disabled")));

        cut.Find("#orbital-resume").Click();
        cut.Find("#orbital-resume-reason").Change("Checked the provider status page.");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            Assert.IsEmpty(cut.FindAll("dialog"));
            var message = cut.Find("#orbital-message");
            Assert.AreEqual("alert", message.GetAttribute("role"));
            StringAssert.Contains(message.TextContent, "The halt changed after this page read it.", StringComparison.Ordinal);
            Assert.AreEqual(2, service.Reads);
        });
    }

    [TestMethod]
    public void ResumeUnavailable_PinsTheRequestAndRetriesWithTheSameKey()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(TestOrbitalUiService.View(Halted(403), health: OrbitalElementsHealthState.Degraded));
        var calls = 0;
        service.ResumeHandler = (_, _, _) => ValueTask.FromResult(++calls == 1
            ? OperatorUiResult<OrbitalCommandView>.Failure(OperatorUiResultKind.Unavailable, "The orbital command could not be completed.")
            : TestOrbitalUiService.Command(OperatorUiResultKind.Success, OrbitalElementsCommandStatus.Replayed, "orbital.resume-replayed"));
        var cut = context.Render<OrbitalSnapshotPanel>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#orbital-resume").HasAttribute("disabled")));
        cut.Find("#orbital-resume").Click();
        cut.Find("#orbital-resume-reason").Change("Provider block lifted.");

        cut.Find("form").Submit();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Retry resume", cut.Find("#orbital-resume-confirm").TextContent);
            Assert.IsTrue(cut.Find("dialog fieldset").HasAttribute("disabled"), "The pinned reason cannot change.");
            StringAssert.Contains(cut.Find("#orbital-resume-message").TextContent, "same key", StringComparison.Ordinal);
        });
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("dialog")));
        Assert.HasCount(2, service.Resumes);
        Assert.AreEqual(service.Resumes[0], service.Resumes[1]);
    }

    [TestMethod]
    public void ResumeReason_IsRequiredBeforeAnyRequest()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(TestOrbitalUiService.View(Halted(403), health: OrbitalElementsHealthState.Degraded));
        var cut = context.Render<OrbitalSnapshotPanel>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#orbital-resume").HasAttribute("disabled")));
        cut.Find("#orbital-resume").Click();

        cut.Find("#orbital-resume-reason").Change("   ");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.AreEqual(OrbitalSnapshotPanel.ResumeReasonRequiredText, cut.Find("#orbital-resume-message").TextContent));
        Assert.IsEmpty(service.Resumes);
    }

    [TestMethod]
    [DataRow(OrbitalOperatorText.ProviderOutcomeUnknownReasonCode)]
    [DataRow(OrbitalOperatorText.InvalidHttpStatusReasonCode)]
    public void HaltWithoutAValidStatus_SaysSoInsteadOfANumber(string haltReasonCode)
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(TestOrbitalUiService.View(Halted(null, haltReasonCode + ": no valid HTTP status recorded"),
            health: OrbitalElementsHealthState.Degraded));
        var cut = context.Render<OrbitalSnapshotPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Halted", cut.Find("#orbital-state").TextContent);
            Assert.AreEqual(OrbitalOperatorText.NoHttpStatusText, cut.Find("#orbital-halt-status").TextContent);
            Assert.AreEqual(OrbitalOperatorText.Describe(haltReasonCode), cut.Find("#orbital-halt-reason").TextContent);
            StringAssert.Contains(cut.Find("#orbital-data-notice").TextContent,
                "Provider halted: No valid HTTP status recorded.", StringComparison.Ordinal);
            AssertNoInventedHttpStatus(cut.Markup);
        });
        cut.Find("#orbital-resume").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("No valid HTTP status recorded", cut.Find("#orbital-resume-status").TextContent);
            Assert.AreEqual(OrbitalOperatorText.Describe(haltReasonCode), cut.Find("#orbital-resume-halt-reason").TextContent);
            AssertNoInventedHttpStatus(cut.Markup);
        });
    }

    [TestMethod]
    public void InvalidCandidate_ShowsTheQuarantineAndKeepsLastKnownGood()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var status = TestOrbitalUiService.Status() with
        {
            LastAttemptUtc = Now.AddMinutes(-20),
            CandidateQuarantine = new OrbitalCandidateQuarantine(Now.AddMinutes(-20), TestOrbitalUiService.RawSha256,
                "orbital.json-syntax: unexpected token near " + TestOrbitalUiService.SyntheticStoreRoot),
            Reason = OrbitalElementsOperations.CandidateQuarantinedReasonCode,
            RecoveryAction = OrbitalRefreshRecoveryAction.InspectQuarantine,
        };
        service.GetHandler = _ => Success(TestOrbitalUiService.View(status, health: OrbitalElementsHealthState.Degraded));

        var cut = context.Render<OrbitalSnapshotPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Quarantined", cut.Find("#orbital-state").TextContent);
            Assert.AreEqual("The provider response was not valid JSON.", cut.Find("#orbital-quarantine-reason").TextContent);
            StringAssert.Contains(cut.Find("#orbital-quarantine").TextContent, "sha256:cccccccc…cccccccc", StringComparison.Ordinal);
            Assert.AreEqual(OrbitalOperatorText.Describe(OrbitalElementsOperations.CandidateQuarantinedReasonCode), cut.Find("#orbital-reason-text").TextContent);
            Assert.AreEqual(OrbitalOperatorText.Recovery(OrbitalRefreshRecoveryAction.InspectQuarantine), cut.Find("#orbital-recovery").TextContent);
            Assert.AreEqual("12,345 records / 12,340 objects", cut.Find("#orbital-counts").TextContent, "Last-known-good stays visible.");
            Assert.AreEqual(ShortManifest, cut.Find("#orbital-manifest-sha").TextContent);
            Assert.IsFalse(cut.Markup.Contains("unexpected token", StringComparison.Ordinal));
            Assert.IsFalse(cut.Markup.Contains(TestOrbitalUiService.SyntheticStoreRoot, StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void StoreUnavailable_AsksForRepairAndMakesNoDataClaim()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var status = TestOrbitalUiService.Status() with
        {
            Reason = "orbital.storage-io",
            RecoveryAction = OrbitalRefreshRecoveryAction.RepairStorage,
        };
        service.GetHandler = _ => Success(TestOrbitalUiService.View(status, health: OrbitalElementsHealthState.Unhealthy,
            healthReason: "orbital.storage-io"));

        var cut = context.Render<OrbitalSnapshotPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Unavailable", cut.Find("#orbital-state").TextContent);
            Assert.AreEqual(OrbitalOperatorText.Recovery(OrbitalRefreshRecoveryAction.RepairStorage), cut.Find("#orbital-recovery").TextContent);
            Assert.AreEqual("A disk error affected the orbital store.", cut.Find("#orbital-reason-text").TextContent);
            var notice = cut.Find("#orbital-data-notice").TextContent;
            StringAssert.Contains(notice, "Orbital data unknown.", StringComparison.Ordinal);
            StringAssert.Contains(notice, "this does not mean no satellites are overhead", StringComparison.Ordinal);
            string[] dataFacts = ["#orbital-counts", "#orbital-manifest-sha", "#orbital-epochs", "#orbital-freshness",
                "#orbital-activated", "#orbital-provider", "#orbital-schedule", "#orbital-etag"];
            foreach (var selector in dataFacts)
            {
                Assert.IsEmpty(cut.FindAll(selector), selector);
            }
            Assert.IsFalse(cut.Markup.Contains(OrbitalOperatorText.NoDataText, StringComparison.Ordinal));
            Assert.IsTrue(cut.Find("#orbital-refresh").HasAttribute("disabled"));
        });
    }

    [TestMethod]
    public void UnsettledAttemptAfterRestart_SaysSoAndAsksForARestart()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var status = TestOrbitalUiService.Status() with
        {
            LastAttemptUtc = Now.AddMinutes(-30),
            Reason = OrbitalElementsOperations.AttemptUnsettledReasonCode,
            RecoveryAction = OrbitalRefreshRecoveryAction.RepairStorage,
        };
        service.GetHandler = _ => Success(TestOrbitalUiService.View(status, health: OrbitalElementsHealthState.Degraded));

        var cut = context.Render<OrbitalSnapshotPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Needs repair", cut.Find("#orbital-state").TextContent);
            Assert.AreEqual(OrbitalOperatorText.Describe(OrbitalElementsOperations.AttemptUnsettledReasonCode), cut.Find("#orbital-reason-text").TextContent);
            StringAssert.Contains(cut.Find("#orbital-reason-text").TextContent, "usually after a restart", StringComparison.Ordinal);
            Assert.AreEqual("12,345 records / 12,340 objects", cut.Find("#orbital-counts").TextContent);
        });
    }

    [TestMethod]
    public void ReadFailure_ShowsAnAlertAndReloadReadsAgain()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => ValueTask.FromResult(OperatorUiResult<OrbitalDataStatusView>.Failure(
            OperatorUiResultKind.Unavailable, "Orbital data status is unavailable."));
        var cut = context.Render<OrbitalSnapshotPanel>();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Unavailable", cut.Find("#orbital-state").TextContent);
            StringAssert.Contains(cut.Find("#orbital-error").TextContent, "Orbital data status unavailable.", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll("#orbital-refresh"));
        });

        service.GetHandler = _ => Success(TestOrbitalUiService.View());
        cut.Find("#orbital-reload").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Fresh", cut.Find("#orbital-state").TextContent);
            Assert.IsEmpty(cut.FindAll("#orbital-error"));
        });
        Assert.AreEqual(2, service.Reads);
    }

    [TestMethod]
    public void RenderedMarkupAndDiagnostics_HoldNoPathsOrHeaderValues()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var root = TestOrbitalUiService.SyntheticStoreRoot;
        var status = TestOrbitalUiService.Status() with
        {
            ProviderHalt = new OrbitalProviderHalt(Now.AddHours(-1), 403, "provider body mentions " + root),
            CandidateQuarantine = new OrbitalCandidateQuarantine(Now.AddHours(-1), null, root + "/candidate.json"),
            Reason = "orbital.storage-io: " + root + "/orbital.db is locked",
            RecoveryAction = OrbitalRefreshRecoveryAction.ResumeProvider,
        };
        service.GetHandler = _ => Success(TestOrbitalUiService.View(status, health: OrbitalElementsHealthState.Degraded,
            healthReason: "orbital.provider-halted: " + root));

        var cut = context.Render<OrbitalSnapshotPanel>();
        cut.WaitForAssertion(() => Assert.IsNotEmpty(cut.FindAll("#orbital-diagnostics")));
        cut.Find("#orbital-resume").Click();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            Assert.IsFalse(markup.Contains(root, StringComparison.Ordinal), "No store path reaches the page.");
            Assert.IsFalse(markup.Contains("hvo-synthetic", StringComparison.Ordinal));
            Assert.IsFalse(markup.Contains("provider body", StringComparison.Ordinal), "No provider body reaches the page.");
            Assert.IsFalse(markup.Contains(TestOrbitalUiService.SyntheticEntityTag, StringComparison.Ordinal));
            Assert.IsFalse(markup.Contains(TestOrbitalUiService.SyntheticLastModified, StringComparison.Ordinal));
            Assert.IsFalse(markup.Contains("17:00:00 GMT", StringComparison.Ordinal));
            Assert.AreEqual("Recorded", cut.Find("#orbital-etag").TextContent);
            Assert.AreEqual("Recorded", cut.Find("#orbital-last-modified").TextContent);
            Assert.AreEqual(OrbitalOperatorText.UnrecordedReasonText, cut.Find("#orbital-halt-reason").TextContent);
            Assert.AreEqual(OrbitalOperatorText.UnrecordedReasonText, cut.Find("#orbital-resume-halt-reason").TextContent);
            var diagnostics = cut.Find("#orbital-diagnostics").TextContent;
            StringAssert.Contains(diagnostics, "orbital.storage-io", StringComparison.Ordinal);
            StringAssert.Contains(diagnostics, TestOrbitalUiService.ManifestSha256, StringComparison.Ordinal);
            StringAssert.Contains(diagnostics, TestOrbitalUiService.RawSha256, StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void NoInstalledData_SaysSoWithoutClaimingAnEmptySky()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var status = TestOrbitalUiService.Status() with
        {
            CurrentManifestSha256 = null,
            CurrentPayloadSha256 = null,
            LastRetrievalSuccessUtc = null,
            LastActivationUtc = null,
            Reason = OrbitalElementsOperations.NoActiveSnapshotReasonCode,
        };
        service.GetHandler = _ => Success(TestOrbitalUiService.View(status));

        var cut = context.Render<OrbitalSnapshotPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("No data", cut.Find("#orbital-state").TextContent);
            Assert.AreEqual(OrbitalOperatorText.NoDataText, cut.Find("#orbital-data-notice").TextContent.Trim());
            Assert.AreEqual("Never", cut.Find("#orbital-activated").TextContent);
            Assert.IsEmpty(cut.FindAll("#orbital-counts"));
            Assert.IsEmpty(cut.FindAll("#orbital-freshness"));
        });
    }

    [TestMethod]
    [DataRow(0d, "0")]
    [DataRow(48d, "66.7")]
    [DataRow(500d, "100")]
    public void FreshnessMarker_PlacesTheStaleLimitAtTwoThirds(double hours, string expected)
        => Assert.AreEqual(expected, OrbitalSnapshotPanel.MarkerPercent(TimeSpan.FromHours(hours)));

    [TestMethod]
    [DataRow(null, "Not recorded")]
    [DataRow("", "Not recorded")]
    [DataRow("W/\"synthetic\"", "Recorded")]
    public void RecordedText_NeverEchoesTheValue(string? value, string expected)
        => Assert.AreEqual(expected, OrbitalSnapshotPanel.RecordedText(value));

    /// <summary>A halt as the store records it: a recorded status names it, and a null status carries its own reason.</summary>
    private static OrbitalRefreshStatus Halted(int? httpStatus, string? haltReason = null) => TestOrbitalUiService.Status() with
    {
        ProviderHalt = new OrbitalProviderHalt(Now.AddHours(-5), httpStatus,
            haltReason ?? (httpStatus is { } status ? "orbital.provider-http-" + status : OrbitalOperatorText.ProviderOutcomeUnknownReasonCode)),
        Reason = OrbitalElementsOperations.ProviderHaltedReasonCode,
        RecoveryAction = OrbitalRefreshRecoveryAction.ResumeProvider,
    };

    /// <summary>No HTTP number of any kind (0, 200, 403, 429 or otherwise) appears where no valid status was recorded.</summary>
    private static void AssertNoInventedHttpStatus(string markup)
        => Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(markup, @"HTTP\s*\d"), markup);

    private static ValueTask<OperatorUiResult<OrbitalDataStatusView>> Success(OrbitalDataStatusView view)
        => ValueTask.FromResult(OperatorUiResult<OrbitalDataStatusView>.Success(view));

    private static TestOrbitalUiService Configure(BunitContext context)
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = new TestOrbitalUiService();
        context.Services.AddSingleton<ICameraAgentOrbitalUiService>(service);
        return service;
    }
}
