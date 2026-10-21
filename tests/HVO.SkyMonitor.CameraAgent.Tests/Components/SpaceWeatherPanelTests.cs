using System.Globalization;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using static HVO.SkyMonitor.CameraAgent.Tests.Components.TestSpaceWeatherUiService;
using SwData = HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.SpaceWeatherUiTestData;
using Disposition = HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.SpaceWeatherOperations.Disposition;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

/// <summary>
/// The full space-weather panel over a scripted operator service: what each state says, the Refresh, Resume and
/// Acknowledge commands with their idempotency keys, the credit, and that no raw record text, path or header value
/// reaches the page.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SpaceWeatherPanelTests
{
    private const string RefreshNowText = "Refresh now (all enabled products)";
    private const string CannotResumeText =
        "This halt cannot be resumed here: its product is not enabled or its time is not a UTC time.";
    private const int StateCount = 10;
    private static readonly string[] CurrentOrder = ["WARK09/1", "WARK05/2"];
    private static readonly string[] ForbiddenMarkup =
        ["secret", "/var/", "/home/", "App_Data", ":\\", "ETag", "If-None-Match", "Last-Modified", "GMT", "Exception", "http://", "https://", "{\""];

    [TestMethod]
    public void States_EachSayDistinctTextAndNoneClaimsAnAbsenceOfSpaceWeather()
    {
        var texts = new List<string>();
        for (var state = 0; state < StateCount; state++)
        {
            using var context = new BunitContext();
            var service = Configure(context);
            var selector = ArrangeState(state, service);

            var cut = context.Render<SpaceWeatherPanel>();

            var banner = cut.WaitForElement(selector);
            texts.Add(banner.TextContent);
            AssertCompliantWording(cut.Markup);
        }

        Assert.HasCount(StateCount, texts);
        CollectionAssert.AllItemsAreUnique(texts, string.Join(" | ", texts));
    }

    [TestMethod]
    public void Loading_ShowsOnlyTheReadingBanner()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        ArrangeState(0, service);

        var cut = context.Render<SpaceWeatherPanel>();

        Assert.AreEqual("Reading the space-weather state.", cut.Find("#space-weather-loading").TextContent);
        Assert.IsEmpty(cut.FindAll("#space-weather-state"));
        Assert.IsEmpty(cut.FindAll("#space-weather-refresh"));
        Assert.IsEmpty(cut.FindAll("#space-weather-credit"));
    }

    [TestMethod]
    public void FailedRead_SaysTheDataIsUnavailableOnceAndReloads()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => service.Reads == 1
            ? ValueTask.FromResult(OperatorUiResult<SpaceWeatherUiView>.Failure(OperatorUiResultKind.Unavailable,
                SpaceWeatherSummary.UnavailableText))
            : Success(View());

        var cut = context.Render<SpaceWeatherPanel>();

        cut.WaitForAssertion(() =>
        {
            var error = cut.Find("#space-weather-error");
            Assert.AreEqual("alert", error.GetAttribute("role"));
            Assert.AreEqual(
                SpaceWeatherSummary.UnavailableText + " Nothing is claimed about current notices.", error.TextContent);
            Assert.AreEqual("Unavailable", cut.Find("#space-weather-state").TextContent);
            Assert.IsEmpty(cut.FindAll("#space-weather-refresh"));
            Assert.IsEmpty(cut.FindAll("article.space-weather-notice"));
        });

        cut.Find("#space-weather-reload").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.IsEmpty(cut.FindAll("#space-weather-error"));
            Assert.HasCount(2, cut.FindAll("article.space-weather-notice"));
        });
        Assert.AreEqual(2, service.Reads);
        Assert.IsEmpty(service.RefreshKeys);
    }

    [TestMethod]
    public void Ready_ShowsTheRefreshStateSourcesSiteCloudAndNotices()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var projection = Projection();
        service.GetHandler = _ => Success(View(projection));

        var cut = context.Render<SpaceWeatherPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Alerts fresh", cut.Find("#space-weather-state").TextContent);
            StringAssert.Contains(cut.Find("#space-weather-state").ClassName, "success", StringComparison.Ordinal);
            Assert.AreEqual("On", cut.Find("#space-weather-enabled").TextContent);
            StringAssert.StartsWith(cut.Find("#space-weather-schedule").TextContent, "Enabled", StringComparison.Ordinal);
            StringAssert.StartsWith(cut.Find("#space-weather-observed").TextContent, projection.Observed.UtcText, StringComparison.Ordinal);
            StringAssert.StartsWith(cut.Find("#space-weather-site").TextContent,
                "Geomagnetic latitude 42.2° (HVO SkyMonitor estimate)", StringComparison.Ordinal);
            StringAssert.StartsWith(cut.Find("#space-weather-cloud").TextContent, SpaceWeatherPanel.CloudText, StringComparison.Ordinal);
            Assert.AreEqual(SpaceWeatherPanel.AutomationsDefinitionLink, cut.Find("#space-weather-schedule-link").GetAttribute("href"));
            Assert.AreEqual(SpaceWeatherPanel.AutomationsRunsLink, cut.Find("#space-weather-runs-link").GetAttribute("href"));
            Assert.IsEmpty(cut.FindAll("#space-weather-setup-schedule"));
            Assert.AreEqual("enabled", cut.Find("#space-weather-sources tr[data-product='alerts'] td").TextContent);

            var refresh = cut.Find("#space-weather-refresh");
            Assert.IsFalse(refresh.HasAttribute("disabled"));
            Assert.IsFalse(refresh.HasAttribute("aria-describedby"));
            Assert.AreEqual(RefreshNowText, refresh.TextContent);
            Assert.IsEmpty(cut.FindAll("#space-weather-refresh-unavailable"));

            var current = cut.FindAll("#space-weather-current article.space-weather-notice");
            CollectionAssert.AreEqual(CurrentOrder, current.Select(static n => n.GetAttribute("data-group")).ToArray());
            Assert.AreEqual(projection.Current[0].GlobalLine, current[0].QuerySelector(".space-weather-line.global")!.TextContent[6..]);
            Assert.AreEqual(projection.Current[0].SiteLine, current[0].QuerySelector(".space-weather-line.site")!.TextContent[12..]);
            Assert.AreEqual("Not acknowledged", current[0].QuerySelector(".space-weather-acknowledgement")!.TextContent);
            Assert.AreEqual("No recently ended SWPC notices retained.", cut.Find("#space-weather-recent .ops-panel-empty").TextContent);
            Assert.AreEqual("No earlier SWPC notices retained.", cut.Find("#space-weather-history .ops-panel-empty").TextContent);
            Assert.IsEmpty(cut.FindAll(".space-weather-tag.warning"));

            StringAssert.Contains(cut.Find("#space-weather-switch").TextContent,
                "hvo-sky config set spaceWeather.enabled=true", StringComparison.Ordinal);
            Assert.AreEqual(SpaceWeatherOperations.Credit, cut.Find("#space-weather-credit").TextContent);
        });
        AssertCompliantWording(cut.Markup);
    }

    [TestMethod]
    public void Ready_SaysTheCloudTextExactlyOnce()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var status = SwData.Status(providerHalt: Halt(),
            products: [SwData.Product(rejected: 5, halt: Halt("rejected-repeatedly"))]);
        service.GetHandler = _ => Success(View(SwData.Project(SwData.View(Notices(), recordDrift: Now.AddHours(-2)), status: status)));

        var cut = context.Render<SpaceWeatherPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.HasCount(2, cut.FindAll("article.space-weather-notice"));
            Assert.HasCount(2, cut.FindAll("article.space-weather-halt"));
            Assert.HasCount(2, cut.FindAll("#space-weather-drift li"));
        });
        Assert.AreEqual(1, Occurrences(cut.Markup, SpaceWeatherPanel.CloudText), cut.Markup);
        StringAssert.StartsWith(cut.Find("#space-weather-cloud").TextContent, SpaceWeatherPanel.CloudText, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Off_KeepsRetainedNoticesListedAndPointsToTheSwitch()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(View(Projection(SwData.Status(enabled: false))));

        var cut = context.Render<SpaceWeatherPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Off", cut.Find("#space-weather-state").TextContent);
            Assert.AreEqual("Off", cut.Find("#space-weather-enabled").TextContent);
            Assert.AreEqual("#space-weather-switch", cut.Find("#space-weather-off a").GetAttribute("href"));
            Assert.HasCount(2, cut.FindAll("article.space-weather-notice"));
            StringAssert.Contains(cut.Find("#space-weather-switch").TextContent, "Space weather is off.", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll("#space-weather-setup"));
            Assert.IsEmpty(cut.FindAll("#space-weather-no-first-success"));
            Assert.AreEqual(SpaceWeatherOperations.Credit, cut.Find("#space-weather-credit").TextContent);
        });
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void UnreadableState_ClaimsNothingAboutNotices(bool storeUnavailable)
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(View(Unreadable(
            storeUnavailable ? SpaceWeatherUiState.StoreUnavailable : SpaceWeatherUiState.ReadFailed)));

        var cut = context.Render<SpaceWeatherPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(storeUnavailable ? SpaceWeatherPanel.StoreUnavailableText : SpaceWeatherPanel.ReadFailedText,
                cut.Find(storeUnavailable ? "#space-weather-store-unavailable" : "#space-weather-read-failed").TextContent);
            Assert.AreEqual("Unavailable", cut.Find("#space-weather-state").TextContent);
            Assert.AreEqual("Unavailable", cut.Find("#space-weather-schedule").FirstChild!.TextContent);
            Assert.IsEmpty(cut.FindAll("#space-weather-sources"));
            Assert.IsEmpty(cut.FindAll("#space-weather-no-notices"));
            Assert.IsEmpty(cut.FindAll("#space-weather-current"));
            Assert.IsEmpty(cut.FindAll("#space-weather-no-first-success"));
            Assert.IsEmpty(cut.FindAll("#space-weather-setup"));
            Assert.AreEqual(SpaceWeatherOperations.Credit, cut.Find("#space-weather-credit").TextContent);
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MissingOrDisabledSchedule_LinksToItsAutomation(bool exists)
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(View(Projection(SwData.Status(definitionExists: exists, definitionEnabled: false))));

        var cut = context.Render<SpaceWeatherPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(exists ? "Disabled" : "No schedule", cut.Find("#space-weather-schedule").FirstChild!.TextContent);
            Assert.AreEqual(exists ? 0 : 1, cut.FindAll("#space-weather-setup").Count);
            Assert.AreEqual(exists ? 1 : 0, cut.FindAll("#space-weather-definition-disabled").Count);
            if (exists)
            {
                Assert.AreEqual(SpaceWeatherPanel.AutomationsDefinitionLink,
                    cut.Find("#space-weather-definition-disabled a").GetAttribute("href"));
                Assert.IsEmpty(cut.FindAll("#space-weather-setup-schedule"));
            }
            else
            {
                Assert.AreEqual(SpaceWeatherPanel.AutomationsSetupLink, cut.Find("#space-weather-setup-link").GetAttribute("href"));
                Assert.AreEqual(SpaceWeatherPanel.AutomationsSetupLink, cut.Find("#space-weather-setup-schedule").GetAttribute("href"));
            }
        });
    }

    [TestMethod]
    [DataRow(0, "Operations change rights are required to refresh space weather.")]
    [DataRow(1, "Space weather is off.")]
    [DataRow(2, "The space-weather store is unavailable.")]
    [DataRow(3, "The space-weather-refresh automation does not exist.")]
    [DataRow(4, "The space-weather-refresh automation is disabled.")]
    [DataRow(5, "SWPC fetching is halted for the provider. Resume it first.")]
    public void Refresh_IsDisabledWithAVisibleReason(int blocked, string reason)
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var view = blocked switch
        {
            0 => View(canMutate: false),
            1 => View(Projection(SwData.Status(enabled: false))),
            2 => View(Unreadable(SpaceWeatherUiState.StoreUnavailable)),
            3 => View(Projection(SwData.Status(definitionExists: false))),
            4 => View(Projection(SwData.Status(definitionEnabled: false))),
            _ => View(Projection(SwData.Status(providerHalt: Halt()))),
        };
        service.GetHandler = _ => Success(view);

        var cut = context.Render<SpaceWeatherPanel>();

        cut.WaitForAssertion(() =>
        {
            var refresh = cut.Find("#space-weather-refresh");
            Assert.IsTrue(refresh.HasAttribute("disabled"));
            Assert.AreEqual("space-weather-refresh-unavailable", refresh.GetAttribute("aria-describedby"));
            Assert.AreEqual(reason, cut.Find("#space-weather-refresh-unavailable").TextContent);
        });
        cut.Find("#space-weather-refresh").Click();
        Assert.IsEmpty(service.RefreshKeys);
        Assert.AreEqual(1, service.Reads);
    }

    [TestMethod]
    public void PendingRefresh_IgnoresADuplicateClick()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var completion = new TaskCompletionSource<OperatorUiResult<SpaceWeatherCommandView>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.RefreshHandler = _ => new ValueTask<OperatorUiResult<SpaceWeatherCommandView>>(completion.Task);
        var cut = context.Render<SpaceWeatherPanel>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#space-weather-refresh").HasAttribute("disabled")));

        cut.Find("#space-weather-refresh").Click();
        cut.Find("#space-weather-refresh").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.HasCount(1, service.RefreshKeys);
            Assert.AreEqual("Requesting…", cut.Find("#space-weather-refresh").TextContent);
            Assert.IsTrue(cut.Find("#space-weather-refresh").HasAttribute("disabled"));
            Assert.IsTrue(cut.Find("#space-weather-reload").HasAttribute("disabled"));
            Assert.IsTrue(cut.FindAll("button.space-weather-acknowledge").All(static b => b.HasAttribute("disabled")));
        });
        completion.SetResult(Command(OperatorUiResultKind.Success, Disposition.Accepted, "space-weather.refresh-queued"));
        cut.WaitForAssertion(() =>
        {
            var message = cut.Find("#space-weather-message");
            Assert.AreEqual(SpaceWeatherOperatorText.Describe("space-weather.refresh-queued"), message.TextContent);
            Assert.AreEqual("status", message.GetAttribute("role"));
            Assert.AreEqual(RefreshNowText, cut.Find("#space-weather-refresh").TextContent);
            Assert.AreEqual(2, service.Reads);
        });
        Assert.HasCount(1, service.RefreshKeys);
        StringAssert.StartsWith(service.RefreshKeys[0], "ui-", StringComparison.Ordinal);
    }

    [TestMethod]
    public void RefreshKey_IsKeptOnlyWhileTheOutcomeIsUnknownOrARevisionConflict()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var outcomes = new Queue<OperatorUiResult<SpaceWeatherCommandView>>(
        [
            Unavailable(),
            Command(OperatorUiResultKind.Conflict, Disposition.Conflict, SpaceWeatherPanel.RevisionConflictReasonCode),
            Command(OperatorUiResultKind.Success, Disposition.Accepted, "space-weather.refresh-queued"),
            Command(OperatorUiResultKind.Conflict, Disposition.Conflict, SpaceWeatherPanel.KeyReusedReasonCode),
            Command(OperatorUiResultKind.Success, Disposition.Replayed, "space-weather.refresh-replayed"),
        ]);
        service.RefreshHandler = _ => ValueTask.FromResult(outcomes.Dequeue());
        var cut = context.Render<SpaceWeatherPanel>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#space-weather-refresh").HasAttribute("disabled")));

        cut.Find("#space-weather-refresh").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Retry refresh", cut.Find("#space-weather-refresh").TextContent);
            Assert.AreEqual("alert", cut.Find("#space-weather-message").GetAttribute("role"));
            StringAssert.EndsWith(cut.Find("#space-weather-message").TextContent,
                "retry sends the identical request with the same key, so it is never queued twice.", StringComparison.Ordinal);
        });
        cut.Find("#space-weather-refresh").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(SpaceWeatherOperatorText.Describe(SpaceWeatherPanel.RevisionConflictReasonCode),
                cut.Find("#space-weather-message").TextContent);
            Assert.AreEqual("Retry refresh", cut.Find("#space-weather-refresh").TextContent);
        });
        cut.Find("#space-weather-refresh").Click();
        cut.WaitForAssertion(() => Assert.AreEqual(RefreshNowText, cut.Find("#space-weather-refresh").TextContent));
        cut.Find("#space-weather-refresh").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(SpaceWeatherOperatorText.Describe(SpaceWeatherPanel.KeyReusedReasonCode),
                cut.Find("#space-weather-message").TextContent);
            Assert.AreEqual(RefreshNowText, cut.Find("#space-weather-refresh").TextContent);
        });
        cut.Find("#space-weather-refresh").Click();
        cut.WaitForAssertion(() => Assert.HasCount(5, service.RefreshKeys));

        Assert.AreEqual(service.RefreshKeys[0], service.RefreshKeys[1], "An unknown outcome is retried with the same key.");
        Assert.AreEqual(service.RefreshKeys[1], service.RefreshKeys[2], "A revision conflict wrote nothing and keeps the key.");
        Assert.AreNotEqual(service.RefreshKeys[2], service.RefreshKeys[3], "A definite outcome ends the intent.");
        Assert.AreNotEqual(service.RefreshKeys[3], service.RefreshKeys[4], "A reused key is never sent again.");
        Assert.AreEqual(6, service.Reads);
    }

    [TestMethod]
    public void ProviderHalt_ResumeBindsTheUnformattedStartAndTheScope()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var halted = Projection(SwData.Status(providerHalt: Halt()));
        service.GetHandler = _ => Success(View(service.Reads == 1 ? halted : null));
        var cut = context.Render<SpaceWeatherPanel>();

        cut.WaitForAssertion(() =>
        {
            var halt = cut.Find("#space-weather-halts article.space-weather-halt");
            Assert.AreEqual("provider", halt.GetAttribute("data-scope"));
            Assert.IsFalse(halt.HasAttribute("data-product"));
            StringAssert.Contains(halt.TextContent, "SWPC fetching halted (provider)", StringComparison.Ordinal);
            StringAssert.Contains(halt.TextContent, halted.ProviderHalt!.Since!.UtcText, StringComparison.Ordinal);
            Assert.IsTrue(cut.Find("#space-weather-refresh").HasAttribute("disabled"));
            Assert.IsFalse(cut.Find("#space-weather-resume-0").HasAttribute("disabled"));
        });

        cut.Find("#space-weather-resume-0").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("provider", cut.Find("#space-weather-resume-scope").TextContent);
            StringAssert.StartsWith(cut.Find("#space-weather-resume-since").TextContent, halted.ProviderHalt!.Since!.UtcText,
                StringComparison.Ordinal);
            Assert.AreEqual(halted.ProviderHalt.Description, cut.Find("#space-weather-resume-reason").TextContent);
            Assert.AreEqual("Resume", cut.Find("#space-weather-resume-confirm").TextContent);
        });
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            Assert.IsEmpty(cut.FindAll("dialog"));
            Assert.AreEqual(SpaceWeatherOperatorText.Describe("resumed"), cut.Find("#space-weather-message").TextContent);
            Assert.IsEmpty(cut.FindAll("#space-weather-halts"));
            Assert.IsFalse(cut.Find("#space-weather-refresh").HasAttribute("disabled"));
        });
        var (request, key) = service.Resumes.Single();
        var since = DateTimeOffset.ParseExact(HaltSinceText, "O", CultureInfo.InvariantCulture);
        Assert.AreEqual("provider", request.Scope);
        Assert.IsNull(request.Product);
        var sent = request.ExpectedHaltSinceUtc;
        Assert.AreEqual(since, sent);
        Assert.AreEqual(TimeSpan.Zero, sent.Offset);
        Assert.AreEqual(421234567L, sent.Ticks % TimeSpan.TicksPerMinute, "The sub-minute ticks are kept.");
        StringAssert.StartsWith(key, "ui-", StringComparison.Ordinal);
        Assert.AreEqual(2, service.Reads);
    }

    [TestMethod]
    public void ProductHalt_ResumeBindsTheProductAndLeavesRefreshAvailable()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(View(Projection(SwData.Status(products: [SwData.Product(halt: Halt())]))));
        var cut = context.Render<SpaceWeatherPanel>();

        cut.WaitForAssertion(() =>
        {
            var halt = cut.Find("article.space-weather-halt");
            Assert.AreEqual("product", halt.GetAttribute("data-scope"));
            Assert.AreEqual("alerts", halt.GetAttribute("data-product"));
            StringAssert.Contains(halt.TextContent, "SWPC fetching halted (alerts)", StringComparison.Ordinal);
            StringAssert.StartsWith(cut.FindAll("#space-weather-sources tr[data-product='alerts'] td")[^1].TextContent,
                "Halted: ", StringComparison.Ordinal);
            Assert.IsFalse(cut.Find("#space-weather-refresh").HasAttribute("disabled"));
        });

        cut.Find("#space-weather-resume-0").Click();
        cut.WaitForAssertion(() => Assert.AreEqual("product / alerts", cut.Find("#space-weather-resume-scope").TextContent));
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("dialog")));
        var (request, _) = service.Resumes.Single();
        Assert.AreEqual("product", request.Scope);
        Assert.AreEqual("alerts", request.Product);
        var sent = request.ExpectedHaltSinceUtc;
        Assert.AreEqual(DateTimeOffset.ParseExact(HaltSinceText, "O", CultureInfo.InvariantCulture), sent);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void HaltThatCannotBeResumedHere_SaysWhyAndSendsNothing(bool provider)
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var status = provider
            ? SwData.Status(providerHalt: new SpaceWeatherOperations.HaltStatus("rate-limited", "2026-10-20T13:07:42+02:00"))
            : SwData.Status(products:
                [SwData.Product(halt: new SpaceWeatherOperations.HaltStatus("rejected-repeatedly", "2026-10-20T13:07:42+02:00"))]);
        service.GetHandler = _ => Success(View(Projection(status)));
        var cut = context.Render<SpaceWeatherPanel>();

        cut.WaitForAssertion(() =>
        {
            var resume = cut.Find("#space-weather-resume-0");
            Assert.IsTrue(resume.HasAttribute("disabled"));
            Assert.AreEqual("space-weather-resume-0-unavailable", resume.GetAttribute("aria-describedby"));
            Assert.AreEqual(CannotResumeText, cut.Find("#space-weather-resume-0-unavailable").TextContent);
        });
        cut.Find("#space-weather-resume-0").Click();

        Assert.IsEmpty(cut.FindAll("dialog"));
        Assert.IsEmpty(service.Resumes);
    }

    [TestMethod]
    public void UnconfiguredProductHalt_IsLabelledAndOffersNoResume()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var status = SwData.Status(products: [SwData.Product(configured: false, halt: Halt()), SwData.Product("scales", halt: Halt())]);
        service.GetHandler = _ => Success(View(Projection(status)));
        var cut = context.Render<SpaceWeatherPanel>();

        cut.WaitForAssertion(() =>
        {
            var retained = cut.Find("article.space-weather-halt[data-product='alerts']");
            StringAssert.Contains(retained.TextContent, "SWPC fetching halted (alerts)", StringComparison.Ordinal);
            Assert.AreEqual(SpaceWeatherPanel.UnconfiguredHaltText, retained.QuerySelector(".space-weather-blocked")!.TextContent);
            Assert.IsFalse(retained.TextContent.Contains("Fetching stays halted", StringComparison.Ordinal), retained.TextContent);
            Assert.IsNull(retained.QuerySelector("button"));
            Assert.IsEmpty(cut.FindAll("#space-weather-resume-0"));

            var configured = cut.Find("article.space-weather-halt[data-product='scales']");
            StringAssert.Contains(configured.TextContent, "Fetching stays halted until an operator resumes it.", StringComparison.Ordinal);
            Assert.IsFalse(cut.Find("#space-weather-resume-1").HasAttribute("disabled"));
        });
        AssertCompliantWording(cut.Markup);
    }

    [TestMethod]
    public void ResumeHaltChanged_ClosesTheDialogAndRereads()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(View(Projection(SwData.Status(providerHalt: Halt()))));
        service.ResumeHandler = (_, _) => ValueTask.FromResult(
            Command(OperatorUiResultKind.Conflict, Disposition.Conflict, SpaceWeatherPanel.HaltChangedReasonCode));
        var cut = context.Render<SpaceWeatherPanel>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#space-weather-resume-0").HasAttribute("disabled")));

        cut.Find("#space-weather-resume-0").Click();
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            Assert.IsEmpty(cut.FindAll("dialog"));
            var message = cut.Find("#space-weather-message");
            Assert.AreEqual("alert", message.GetAttribute("role"));
            Assert.AreEqual(SpaceWeatherOperatorText.Describe(SpaceWeatherPanel.HaltChangedReasonCode) +
                " The halt shown below is the current one.", message.TextContent);
            Assert.HasCount(1, cut.FindAll("article.space-weather-halt"));
        });
        Assert.AreEqual(2, service.Reads);
        Assert.HasCount(1, service.Resumes);
    }

    [TestMethod]
    public void ResumeUnavailable_PinsTheRequestAndRetriesWithTheSameKey()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(View(Projection(SwData.Status(providerHalt: Halt()))));
        service.ResumeHandler = (_, _) => ValueTask.FromResult(service.Resumes.Count == 1
            ? Unavailable()
            : Command(OperatorUiResultKind.Success, Disposition.Replayed, "replayed"));
        var cut = context.Render<SpaceWeatherPanel>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#space-weather-resume-0").HasAttribute("disabled")));
        cut.Find("#space-weather-resume-0").Click();

        cut.Find("form").Submit();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Retry resume", cut.Find("#space-weather-resume-confirm").TextContent);
            Assert.AreEqual("alert", cut.Find("#space-weather-resume-message").GetAttribute("role"));
            StringAssert.Contains(cut.Find("#space-weather-resume-message").TextContent, "same key", StringComparison.Ordinal);
            Assert.HasCount(1, cut.FindAll("dialog .dialog-note"));
        });
        Assert.AreEqual(1, service.Reads, "An unknown outcome does not reread behind the open dialog.");

        // Closing and reopening the same halt keeps the pinned request.
        cut.FindAll("dialog footer button").First(static b => b.TextContent == "Cancel").Click();
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("dialog")));
        cut.Find("#space-weather-resume-0").Click();
        cut.WaitForAssertion(() => Assert.AreEqual("Retry resume", cut.Find("#space-weather-resume-confirm").TextContent));
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("dialog")));
        Assert.HasCount(2, service.Resumes);
        Assert.AreEqual(service.Resumes[0], service.Resumes[1]);
        Assert.AreEqual(2, service.Reads);
    }

    [TestMethod]
    public void ResumeRevisionConflict_KeepsTheKeyAndAnInvalidOutcomeDoesNot()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(View(Projection(SwData.Status(providerHalt: Halt()))));
        var outcomes = new Queue<OperatorUiResult<SpaceWeatherCommandView>>(
        [
            Command(OperatorUiResultKind.Conflict, Disposition.Conflict, SpaceWeatherPanel.RevisionConflictReasonCode),
            Command(OperatorUiResultKind.Invalid, Disposition.Invalid, "space-weather.resume-invalid-scope"),
            Command(OperatorUiResultKind.Success, Disposition.Applied, "resumed"),
        ]);
        service.ResumeHandler = (_, _) => ValueTask.FromResult(outcomes.Dequeue());
        var cut = context.Render<SpaceWeatherPanel>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#space-weather-resume-0").HasAttribute("disabled")));
        cut.Find("#space-weather-resume-0").Click();

        cut.Find("form").Submit();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(SpaceWeatherOperatorText.Describe(SpaceWeatherPanel.RevisionConflictReasonCode),
                cut.Find("#space-weather-resume-message").TextContent);
            Assert.AreEqual("Resume", cut.Find("#space-weather-resume-confirm").TextContent);
        });
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.AreEqual(SpaceWeatherOperatorText.Describe("space-weather.resume-invalid-scope"),
            cut.Find("#space-weather-resume-message").TextContent));
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("dialog")));
        Assert.HasCount(3, service.Resumes);
        Assert.AreEqual(service.Resumes[0].Key, service.Resumes[1].Key, "A revision conflict wrote nothing and keeps the key.");
        Assert.AreNotEqual(service.Resumes[1].Key, service.Resumes[2].Key, "An invalid outcome is definite.");
        Assert.AreEqual(2, service.Reads, "Only the outcome that closes the dialog rereads.");
    }

    [TestMethod]
    public void Acknowledge_SendsTheGroupAndRereads()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(View(service.Reads == 1 ? null : Acknowledged()));
        var cut = context.Render<SpaceWeatherPanel>();
        cut.WaitForAssertion(() => Assert.HasCount(2, cut.FindAll("button.space-weather-acknowledge")));

        cut.Find("button.space-weather-acknowledge[data-group='WARK09/1']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(SpaceWeatherOperatorText.Describe("acknowledged"), cut.Find("#space-weather-message").TextContent);
            Assert.IsEmpty(cut.FindAll("button.space-weather-acknowledge[data-group='WARK09/1']"));
            Assert.HasCount(1, cut.FindAll("button.space-weather-acknowledge[data-group='WARK05/2']"));
            StringAssert.StartsWith(cut.Find("article[data-group='WARK09/1'] .space-weather-acknowledgement").TextContent,
                "Acknowledged ", StringComparison.Ordinal);
        });
        var (group, key) = service.Acknowledgements.Single();
        Assert.AreEqual("WARK09/1", group);
        StringAssert.StartsWith(key, "ui-", StringComparison.Ordinal);
        Assert.AreEqual(2, service.Reads);
    }

    [TestMethod]
    public void AcknowledgeUnavailable_RetriesWithTheSameKey()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.AcknowledgeHandler = (_, _) => ValueTask.FromResult(service.Acknowledgements.Count == 1
            ? Unavailable()
            : Command(OperatorUiResultKind.Success, Disposition.Applied, "acknowledged"));
        var cut = context.Render<SpaceWeatherPanel>();
        cut.WaitForAssertion(() => Assert.HasCount(2, cut.FindAll("button.space-weather-acknowledge")));

        cut.Find("button.space-weather-acknowledge[data-group='WARK05/2']").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Retry acknowledge", cut.Find("button.space-weather-acknowledge[data-group='WARK05/2']").TextContent);
            Assert.AreEqual("Acknowledge", cut.Find("button.space-weather-acknowledge[data-group='WARK09/1']").TextContent);
            StringAssert.EndsWith(cut.Find("#space-weather-message").TextContent,
                "retry sends the identical request with the same key.", StringComparison.Ordinal);
        });
        cut.Find("button.space-weather-acknowledge[data-group='WARK05/2']").Click();

        cut.WaitForAssertion(() => Assert.HasCount(2, service.Acknowledgements));
        Assert.AreEqual(service.Acknowledgements[0], service.Acknowledgements[1]);
        cut.WaitForAssertion(() => Assert.AreEqual("Acknowledge",
            cut.Find("button.space-weather-acknowledge[data-group='WARK05/2']").TextContent));
    }

    [TestMethod]
    [DataRow(SpaceWeatherEscalation.Escalated, "; escalated since acknowledged")]
    [DataRow(SpaceWeatherEscalation.NotEscalated, "")]
    [DataRow(SpaceWeatherEscalation.Unknown, "; escalation since then cannot be determined")]
    public void AcknowledgedNotice_SaysWhenAndWhatChangedSince(SpaceWeatherEscalation escalation, string suffix)
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var projection = SwData.Project(EscalationView(escalation));
        var notice = projection.Current.Single();
        Assert.AreEqual(escalation, notice.Escalation);
        service.GetHandler = _ => Success(View(projection));

        var cut = context.Render<SpaceWeatherPanel>();

        var text = cut.WaitForElement("article[data-group='WARK09/1'] .space-weather-acknowledgement").TextContent;
        Assert.AreEqual(string.Concat("Acknowledged ", notice.AcknowledgedAt!.UtcText, " (", notice.AcknowledgedAt.LocalText, ")", suffix),
            text);
        Assert.IsFalse(text.Contains("; acknowledged", StringComparison.Ordinal), text);
        AssertCompliantWording(cut.Markup);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void UnknownEscalation_OffersAcknowledgeOnlyWithChangeRights(bool canMutate)
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(View(SwData.Project(EscalationView(SpaceWeatherEscalation.Unknown)), canMutate));
        var cut = context.Render<SpaceWeatherPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.HasCount(1, cut.FindAll("article.space-weather-notice"));
            Assert.HasCount(canMutate ? 1 : 0, cut.FindAll("button.space-weather-acknowledge[data-group='WARK09/1']"));
        });
        if (!canMutate)
        {
            Assert.IsEmpty(service.Acknowledgements);
            return;
        }

        cut.Find("button.space-weather-acknowledge[data-group='WARK09/1']").Click();

        cut.WaitForAssertion(() => Assert.HasCount(1, service.Acknowledgements));
        Assert.AreEqual("WARK09/1", service.Acknowledgements.Single().Group);
    }

    [TestMethod]
    public void ReadOnlyOperator_SeesTheStateButNoEnabledCommand()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(View(Projection(SwData.Status(providerHalt: Halt())), canMutate: false));
        var cut = context.Render<SpaceWeatherPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.HasCount(2, cut.FindAll("article.space-weather-notice"));
            Assert.IsEmpty(cut.FindAll("button.space-weather-acknowledge"));
            Assert.IsTrue(cut.Find("#space-weather-refresh").HasAttribute("disabled"));
            Assert.IsTrue(cut.Find("#space-weather-resume-0").HasAttribute("disabled"));
            Assert.AreEqual("Operations change rights are required to resume fetching.",
                cut.Find("#space-weather-resume-0-unavailable").TextContent);
            Assert.IsFalse(cut.Find("#space-weather-reload").HasAttribute("disabled"));
        });
        cut.Find("#space-weather-resume-0").Click();
        cut.Find("#space-weather-refresh").Click();

        Assert.IsEmpty(cut.FindAll("dialog"));
        Assert.IsEmpty(service.Resumes);
        Assert.IsEmpty(service.RefreshKeys);
    }

    [TestMethod]
    public void Reload_RereadsWithoutRunningACommand()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(View(Projection(messages: service.Reads == 1 ? Notices() : [])));
        var cut = context.Render<SpaceWeatherPanel>();
        cut.WaitForAssertion(() => Assert.HasCount(2, cut.FindAll("article.space-weather-notice")));

        cut.Find("#space-weather-reload").Click();

        cut.WaitForAssertion(() => Assert.AreEqual(SpaceWeatherPanel.NoNoticesText, cut.Find("#space-weather-no-notices").TextContent));
        Assert.AreEqual(2, service.Reads);
        Assert.IsEmpty(service.RefreshKeys);
        Assert.IsEmpty(service.Resumes);
        Assert.IsEmpty(service.Acknowledgements);
    }

    [TestMethod]
    public void History_PagesTwentyFiveAtATimeAndCountsOlderNotices()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var messages = Enumerable.Range(0, 210)
            .Select(i => SwData.Warning(i + 1, Now.AddHours(-26).AddMinutes(-i), Now.AddHours(-25).AddMinutes(-i))).ToArray();
        service.GetHandler = _ => Success(View(Projection(messages: messages)));
        var cut = context.Render<SpaceWeatherPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Page 1 of 8", cut.Find("#space-weather-history-page").TextContent);
            Assert.IsTrue(cut.Find("#space-weather-history-previous").HasAttribute("disabled"));
            Assert.HasCount(25, cut.FindAll("#space-weather-history article.space-weather-notice"));
            Assert.AreEqual("10 older retained notices are not shown.", cut.Find("#space-weather-history-older").TextContent);
            Assert.AreEqual("No pending or active SWPC notices retained.", cut.Find("#space-weather-current .ops-panel-empty").TextContent);
        });

        cut.Find("#space-weather-history-next").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Page 2 of 8", cut.Find("#space-weather-history-page").TextContent);
            Assert.AreEqual("WARK05/26", cut.Find("#space-weather-history article.space-weather-notice").GetAttribute("data-group"));
            Assert.IsFalse(cut.Find("#space-weather-history-previous").HasAttribute("disabled"));
        });
        Assert.AreEqual(1, service.Reads, "Paging uses the projection already read.");
    }

    [TestMethod]
    public void Markup_HoldsNoRawRecordTextPathOrHeaderValue()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var raw = SwData.Warning(3, Now.AddHours(-1), Now.AddHours(1), code: "WARK07", level: "G3");
        raw = raw with
        {
            OriginalRecordJson = "{\"message\":\"secret-detail\"}",
            NormalizedText = "secret-detail C:\\swpc\\alerts.json",
            ProductId = "secret-product",
            Header = raw.Header with { Headline = "secret-headline" },
        };
        var status = SwData.Status(providerHalt: Halt(),
            products: [SwData.Product(rejected: 5, halt: Halt("rejected-repeatedly"))]);
        var projection = SwData.Project(SwData.View([.. Notices(), raw], recordDrift: Now.AddHours(-2)), status: status);
        service.GetHandler = _ => Success(View(projection));
        var cut = context.Render<SpaceWeatherPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.HasCount(3, cut.FindAll("article.space-weather-notice"));
            Assert.HasCount(2, cut.FindAll("#space-weather-drift li"));
            Assert.HasCount(2, cut.FindAll("article.space-weather-halt"));
        });
        cut.Find("#space-weather-resume-0").Click();
        cut.WaitForElement("dialog");

        foreach (var forbidden in ForbiddenMarkup)
        {
            Assert.IsFalse(cut.Markup.Contains(forbidden, StringComparison.OrdinalIgnoreCase), forbidden);
        }
        AssertCompliantWording(cut.Markup);
    }

    [TestMethod]
    public void DriftList_NamesRecordDriftOnlyForConfiguredProducts()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var status = SwData.Status(products: [SwData.Product(configured: false), SwData.Product("scales", rejected: 5)]);
        service.GetHandler = _ => Success(View(SwData.Project(SwData.View(Notices(), recordDrift: Now.AddHours(-2)), status: status)));
        var cut = context.Render<SpaceWeatherPanel>();

        cut.WaitForAssertion(() =>
        {
            var drift = cut.FindAll("#space-weather-drift li");
            Assert.HasCount(1, drift);
            Assert.AreEqual("scales", drift[0].GetAttribute("data-product"));
            StringAssert.StartsWith(drift[0].TextContent, "Format drift on scales", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void UnauthorizedRead_NavigatesToAccessDenied()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => ValueTask.FromResult(OperatorUiResult<SpaceWeatherUiView>.Failure(
            OperatorUiResultKind.Unauthorized, "Authorization is required."));

        var cut = context.Render<SpaceWeatherPanel>();

        var navigation = context.Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => Assert.IsTrue(navigation.Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal)));
        Assert.AreEqual(1, service.Reads);
        Assert.IsEmpty(cut.FindAll("article.space-weather-notice"));
    }

    [TestMethod]
    public void UnauthorizedRefresh_NavigatesToAccessDenied()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.RefreshHandler = _ => ValueTask.FromResult(OperatorUiResult<SpaceWeatherCommandView>.Failure(
            OperatorUiResultKind.Unauthorized, "Authorization is required."));
        var cut = context.Render<SpaceWeatherPanel>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#space-weather-refresh").HasAttribute("disabled")));

        cut.Find("#space-weather-refresh").Click();

        var navigation = context.Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => Assert.IsTrue(navigation.Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal)));
        Assert.HasCount(1, service.RefreshKeys);
        Assert.AreEqual(1, service.Reads);
    }

    /// <summary>Arranges panel state <paramref name="state"/> and returns the selector of the banner that names it.</summary>
    private static string ArrangeState(int state, TestSpaceWeatherUiService service)
    {
        switch (state)
        {
            case 0:
                service.GetHandler = static cancellationToken =>
                {
                    var pending = new TaskCompletionSource<OperatorUiResult<SpaceWeatherUiView>>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
                    return new ValueTask<OperatorUiResult<SpaceWeatherUiView>>(pending.Task);
                };
                return "#space-weather-loading";
            case 1:
                service.GetHandler = static _ => ValueTask.FromResult(OperatorUiResult<SpaceWeatherUiView>.Failure(
                    OperatorUiResultKind.Unavailable, SpaceWeatherSummary.UnavailableText));
                return "#space-weather-error";
            case 2:
                service.GetHandler = static _ => Success(View(Projection(SwData.Status(enabled: false))));
                return "#space-weather-off";
            case 3:
                service.GetHandler = static _ => Success(View(Projection(
                    SwData.Status(products: [SwData.Product(succeeded: false, freshness: "none")]), [])));
                return "#space-weather-no-first-success";
            case 4:
                service.GetHandler = static _ => Success(View(Projection(messages: [])));
                return "#space-weather-no-notices";
            case 5:
                service.GetHandler = static _ => Success(View(Projection(SwData.Status(definitionExists: false))));
                return "#space-weather-setup";
            case 6:
                service.GetHandler = static _ => Success(View(Projection(SwData.Status(definitionEnabled: false))));
                return "#space-weather-definition-disabled";
            case 7:
                service.GetHandler = static _ => Success(View(Unreadable(SpaceWeatherUiState.StoreUnavailable)));
                return "#space-weather-store-unavailable";
            case 8:
                service.GetHandler = static _ => Success(View(Unreadable(SpaceWeatherUiState.ReadFailed)));
                return "#space-weather-read-failed";
            default:
                service.GetHandler = static _ => Success(View(Projection() with { Inconsistent = true }));
                return "#space-weather-inconsistent";
        }
    }

    /// <summary>The G5 warning WARK09/1, acknowledged so that its escalation since then is <paramref name="escalation"/>.</summary>
    private static SwpcStoreView EscalationView(SpaceWeatherEscalation escalation)
    {
        // A raise after the acknowledgement escalates it; an acknowledgement older than the only version cannot be judged.
        SwpcMessageRecord[] messages = escalation == SpaceWeatherEscalation.Unknown
            ? [SwData.Warning(1, Now.AddHours(-2), Now.AddHours(4), code: "WARK09", level: "G5")]
            :
            [
                SwData.Warning(1, Now.AddHours(-2), Now.AddHours(4), code: "WARK09", level: "G4"),
                SwData.Warning(2, Now.AddHours(-2), Now.AddHours(4), code: "WARK09", level: "G5", issued: Now.AddHours(-1), link: 1),
            ];
        var acknowledgedAt = escalation switch
        {
            SpaceWeatherEscalation.Escalated => Now.AddMinutes(-90),
            SpaceWeatherEscalation.NotEscalated => Now.AddMinutes(-30),
            _ => Now.AddHours(-3),
        };
        return SwData.View(messages, acknowledgements: new Dictionary<SwpcMessageNode, SwpcAcknowledgement>
        {
            [SwData.Node("WARK09", 1)] = new("owner", acknowledgedAt),
        });
    }

    private static int Occurrences(string markup, string value) => markup.Split(value, StringSplitOptions.None).Length - 1;

    private static TestSpaceWeatherUiService Configure(BunitContext context)
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = new TestSpaceWeatherUiService();
        context.Services.AddSingleton<ICameraAgentSpaceWeatherUiService>(service);
        return service;
    }
}
