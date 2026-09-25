using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Components.Shared;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class ArchivePagesTests
{
    private static readonly ObservingDayCalendar Phoenix = ObservingDayCalendar.Create("America/Phoenix");

    private sealed class TestObservingDayUiService : ICameraAgentObservingDayUiService
    {
        public Func<DateOnly, CancellationToken, ValueTask<OperatorUiResult<CameraAgentObservingDayView>>> Handler { get; set; } =
            static (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentObservingDayView>.Failure(OperatorUiResultKind.Unavailable, "unset"));

        public ValueTask<OperatorUiResult<CameraAgentObservingDayView>> GetAsync(DateOnly observingDate, CancellationToken cancellationToken)
            => Handler(observingDate, cancellationToken);
    }
    // Affirmative claims that would imply authority CameraAgent does not have over local candidates,
    // matched against visible text so a negated disclaimer is not mistaken for a claim.
    private static readonly string[] ForbiddenCandidateClaims = ["fireball", "ground track", "impact location", "reconstructed event", "validated event", "correlated event", "published event", "multi-site", "entry speed", "peak altitude"];

    [TestMethod]
    public void Calendar_RendersTheMonthAsAGridOfObservingDays()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        CameraAgentGalleryCalendarQuery? observed = null;
        var night = Phoenix.Resolve(new DateOnly(2026, 7, 21));
        var emptyNight = Phoenix.Resolve(new DateOnly(2026, 7, 22));
        var representative = Guid.NewGuid();
        service.CalendarHandler = (query, _) =>
        {
            observed = query;
            return ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryCalendar>.Success(new("America/Phoenix", false,
            [
                new(night, 12, 1, night.StartUtc.AddHours(2), night.EndUtc.AddHours(-3), representative),
                new(emptyNight, 0, 0, null, null)
            ])));
        };

        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/archive/calendar?month=2026-07");
        var cut = context.Render<ArchiveCalendarPage>();

        cut.WaitForAssertion(() =>
        {
            // July 2026 starts on a Wednesday and ends on a Friday: the grid covers whole weeks,
            // 28 June through 1 August.
            Assert.AreEqual(new DateOnly(2026, 6, 28), observed?.FromDate);
            Assert.AreEqual(new DateOnly(2026, 8, 1), observed?.ToDate);
            var cells = cut.FindAll(".calendar-day");
            Assert.HasCount(35, cells);
            Assert.HasCount(7, cut.FindAll(".calendar-weekdays span"));
            Assert.IsTrue(cells[0].ClassList.Contains("calendar-day--outside"));
            StringAssert.Contains(cells[0].TextContent, "Jun 28", StringComparison.Ordinal);
            // 21 July is index 23 (3 outside days + 20).
            var observedNight = cells[23];
            Assert.IsFalse(observedNight.ClassList.Contains("calendar-day--empty"));
            StringAssert.Contains(observedNight.TextContent, "12 captures", StringComparison.Ordinal);
            Assert.AreEqual("/archive/day/2026-07-21", observedNight.QuerySelector("a")!.GetAttribute("href"));
            Assert.AreEqual($"/api/v1/operations/gallery/{representative:D}/thumbnail", observedNight.QuerySelector("img.calendar-thumb")!.GetAttribute("src"));
            Assert.IsNotNull(observedNight.QuerySelector(".calendar-products i.event"));
            Assert.HasCount(3, observedNight.QuerySelectorAll(".calendar-products i.unavailable"));
            Assert.AreEqual("true", observedNight.QuerySelector(".calendar-products")!.GetAttribute("aria-hidden"));
            var empty = cells[24];
            Assert.IsTrue(empty.ClassList.Contains("calendar-day--empty"));
            StringAssert.Contains(empty.TextContent, "No retained captures", StringComparison.Ordinal);
            Assert.IsNull(empty.QuerySelector("img"));
            Assert.AreEqual("/archive/day/2026-07-22", empty.QuerySelector("a")!.GetAttribute("href"));
            // Now is 12:00 UTC on 23 July = 05:00 Phoenix, inside the night that began on the 22nd.
            Assert.IsTrue(empty.ClassList.Contains("calendar-day--today"));
            var summary = cut.Find(".calendar-summary").TextContent;
            StringAssert.Contains(summary, "Nights with captures1", StringComparison.Ordinal);
            StringAssert.Contains(summary, "30 without retained captures", StringComparison.Ordinal);
            StringAssert.Contains(summary, "Capture coverageUnavailable", StringComparison.Ordinal);
            StringAssert.Contains(summary, "Expected schedule not projected", StringComparison.Ordinal);
            StringAssert.Contains(summary, "Detected candidates1", StringComparison.Ordinal);
            StringAssert.Contains(summary, "not yet generated", StringComparison.Ordinal);
            Assert.IsNotNull(cut.Find(".calendar-legend__unavailable"));
            Assert.IsFalse(cut.Find(".calendar-legend").ParentElement!.ClassList.Contains("observing-calendar"));
            Assert.IsFalse(cut.Markup.Contains("UTC days", StringComparison.Ordinal));
        });
        var nav = cut.FindAll(".month-nav a").Select(link => link.GetAttribute("href")!).ToArray();
        StringAssert.Contains(nav[0], "month=2026-06", StringComparison.Ordinal);
        StringAssert.Contains(nav[1], "month=2026-08", StringComparison.Ordinal);
        StringAssert.Contains(nav[2], "month=2026-07", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Calendar_BrokenRepresentativeFallsBackAndEmptyDaysRemainNavigable()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var night = Phoenix.Resolve(new DateOnly(2026, 7, 21));
        var representative = Guid.NewGuid();
        service.CalendarHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryCalendar>.Success(
            new("America/Phoenix", false, [new(night, 1, 0, night.StartUtc, night.EndUtc, representative)])));
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/archive/calendar?month=2026-07");
        var cut = context.Render<ArchiveCalendarPage>();
        var image = cut.WaitForElement($"img[src='/api/v1/operations/gallery/{representative:D}/thumbnail']");
        image.TriggerEvent("onerror", new Microsoft.AspNetCore.Components.Web.ErrorEventArgs());

        cut.WaitForAssertion(() =>
        {
            var day = cut.Find(".calendar-day:not(.calendar-day--empty)");
            Assert.IsNull(day.QuerySelector("img"));
            Assert.IsNotNull(day.QuerySelector(".calendar-thumb--missing"));
            StringAssert.Contains(day.TextContent, "preview unavailable", StringComparison.Ordinal);
            StringAssert.Contains(day.QuerySelector("a")!.GetAttribute("aria-label"), "preview unavailable", StringComparison.Ordinal);
            Assert.AreEqual("/archive/day/2026-07-21", day.QuerySelector("a")!.GetAttribute("href"));
            StringAssert.Contains(cut.Find(".calendar-day--empty").TextContent, "No retained captures", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find(".calendar-summary").TextContent, "Capture coverageUnavailable", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void Calendar_DefaultsToTheCurrentObservingMonthAndClampsTheMonth()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        CameraAgentGalleryCalendarQuery? observed = null;
        service.CalendarHandler = (query, _) =>
        {
            observed = query;
            return ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryCalendar>.Success(new("America/Phoenix", false, [])));
        };

        var cut = context.Render<ArchiveCalendarPage>();
        // July 2026: the current observing night (22 July) selects its month.
        cut.WaitForAssertion(() => Assert.AreEqual(new DateOnly(2026, 6, 28), observed?.FromDate));

        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/archive/calendar?month=0001-01");
        var clamped = context.Render<ArchiveCalendarPage>();
        clamped.WaitForAssertion(() =>
        {
            // Clamped to February of year 1 so the leading-week arithmetic never underflows:
            // the queried range brackets that month.
            Assert.IsTrue(observed!.FromDate <= new DateOnly(1, 2, 1));
            Assert.IsTrue(observed.ToDate >= new DateOnly(1, 2, 28));
            Assert.IsTrue(observed.ToDate.DayNumber - observed.FromDate.DayNumber < 42);
            Assert.IsNotNull(clamped.Find(".month-nav"));
        });
    }

    [TestMethod]
    public void ObservingDay_RendersFactsTimelineAndHonestProductSlots()
    {
        using var context = new BunitContext();
        Configure(context);
        var day = Phoenix.Resolve(new DateOnly(2026, 7, 21));
        var representative = Guid.NewGuid();
        var exposures = Enumerable.Range(0, 3).Select(index => day.StartUtc.AddHours(8).AddMinutes(index * 5)).ToArray();
        var windowStart = day.StartUtc.AddHours(7);
        var windowEnd = day.StartUtc.AddHours(17);
        var candidate = new CameraAgentTransientOperatorCandidate(
            Guid.NewGuid(), Guid.NewGuid(), "Extracted", "None", "CausalRetained", day.StartUtc.AddHours(9), day.StartUtc.AddHours(9), "Retained", "Pending", "Pending", "Pending");
        var dayService = new TestObservingDayUiService
        {
            Handler = (date, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentObservingDayView>.Success(new(
                new CameraAgentGalleryCalendarDay(day, 3, 1, exposures[0], exposures[^1], representative, exposures[^1]),
                exposures,
                TimeSpan.FromSeconds(60),
                new CameraAgentObservingDayScheduleView([(windowStart, windowEnd)], windowEnd - windowStart, TimeSpan.FromMinutes(3), false),
                [candidate],
                false,
                [],
                representative,
                null,
                null)))
        };
        context.Services.AddSingleton<ICameraAgentObservingDayUiService>(dayService);

        var cut = context.Render<ObservingDayPage>(parameters => parameters.Add(page => page.DateText, "2026-07-21"));

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Find("h1").TextContent, "21 July 2026", StringComparison.Ordinal);
            Assert.AreEqual($"/api/v1/operations/gallery/{representative:D}/thumbnail", cut.Find(".day-hero__image img").GetAttribute("src"));
            var facts = cut.Find(".day-facts").TextContent;
            StringAssert.Contains(facts, "Partial", StringComparison.Ordinal);
            StringAssert.Contains(facts, "19:00–05:00", StringComparison.Ordinal);
            StringAssert.Contains(facts, "1% of 10h 00m", StringComparison.Ordinal);
            StringAssert.Contains(facts, "under 90 % of the scheduled window", StringComparison.Ordinal);
            StringAssert.Contains(facts, "overrides and manual pause are not reflected", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find(".day-hero__caption").TextContent, "Newest capture with a published preview, 2026-07-22 03:10:00 UTC", StringComparison.Ordinal);
            StringAssert.Contains(facts, "Retained captures3", StringComparison.Ordinal);
            StringAssert.Contains(facts, "Current cloudNot assessed", StringComparison.Ordinal);
            StringAssert.Contains(facts, "Total integration1m 00s", StringComparison.Ordinal);
            Assert.HasCount(1, cut.FindAll(".timeline-bar--schedule"));
            // Three captures five minutes apart with one-minute bins do not merge.
            Assert.HasCount(3, cut.FindAll(".timeline-bar--captures"));
            Assert.HasCount(1, cut.FindAll(".timeline-marker"));
            var slots = cut.FindAll(".product-slot");
            Assert.HasCount(3, slots);
            Assert.IsTrue(slots.All(slot => slot.TextContent.Contains("Not yet produced", StringComparison.Ordinal)));
            StringAssert.Contains(cut.Find(".day-events").TextContent, "Extracted", StringComparison.Ordinal);
            Assert.AreEqual("/archive/day/2026-07-20", cut.FindAll(".day-title__step")[0].GetAttribute("href"));
            Assert.AreEqual("/archive/day/2026-07-22", cut.FindAll(".day-title__step")[1].GetAttribute("href"));
            Assert.AreEqual("/gallery?from=2026-07-21T19:00:00.000&to=2026-07-22T18:59:59.999", cut.Find(".day-actions a").GetAttribute("href"));
        });
    }

    [TestMethod]
    public void ObservingDay_ReportsNoSessionAndRejectsMalformedDates()
    {
        using var context = new BunitContext();
        Configure(context);
        var day = Phoenix.Resolve(new DateOnly(2026, 7, 22));
        context.Services.AddSingleton<ICameraAgentObservingDayUiService>(new TestObservingDayUiService
        {
            Handler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentObservingDayView>.Success(new(
                new CameraAgentGalleryCalendarDay(day, 0, 0, null, null), [], TimeSpan.Zero, null, [], false, [], null, null, null)))
        });

        var cut = context.Render<ObservingDayPage>(parameters => parameters.Add(page => page.DateText, "2026-07-22"));
        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Find(".day-hero__empty").TextContent, "No captures were retained", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find(".day-facts").TextContent, "No archived session", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find(".day-facts").TextContent, "Scheduled windowSchedule unavailable", StringComparison.Ordinal);
            Assert.IsNotNull(cut.Find(".timeline-track__empty"));
        });

        var malformed = context.Render<ObservingDayPage>(parameters => parameters.Add(page => page.DateText, "yesterday"));
        malformed.WaitForAssertion(() => StringAssert.Contains(malformed.Markup, "Observing day not recognised", StringComparison.Ordinal));
        // Dates the calendar cannot step from are refused rather than throwing on the step links.
        var edge = context.Render<ObservingDayPage>(parameters => parameters.Add(page => page.DateText, "9999-12-31"));
        edge.WaitForAssertion(() => StringAssert.Contains(edge.Markup, "Observing day not recognised", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ObservingDay_ReportsCapturesOutsideTheScheduledWindow()
    {
        using var context = new BunitContext();
        Configure(context);
        var day = Phoenix.Resolve(new DateOnly(2026, 7, 21));
        var exposures = new[] { day.StartUtc.AddHours(2) };
        context.Services.AddSingleton<ICameraAgentObservingDayUiService>(new TestObservingDayUiService
        {
            Handler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentObservingDayView>.Success(new(
                new CameraAgentGalleryCalendarDay(day, 1, 0, exposures[0], exposures[0]), exposures, TimeSpan.FromSeconds(20),
                new CameraAgentObservingDayScheduleView([(day.StartUtc.AddHours(7), day.StartUtc.AddHours(17))], TimeSpan.FromHours(10), TimeSpan.Zero, true),
                [], false, [], null, null, null)))
        });

        var cut = context.Render<ObservingDayPage>(parameters => parameters.Add(page => page.DateText, "2026-07-21"));
        cut.WaitForAssertion(() =>
        {
            var facts = cut.Find(".day-facts").TextContent;
            StringAssert.Contains(facts, "Outside window", StringComparison.Ordinal);
            StringAssert.Contains(facts, "none inside the scheduled window", StringComparison.Ordinal);
            StringAssert.Contains(facts, "predates the active revision", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void NewPages_RedirectToAccessDeniedWhenUnauthorized()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.CalendarHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryCalendar>.Failure(OperatorUiResultKind.Unauthorized, "denied"));
        service.ProductPageHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentProductPage>.Failure(OperatorUiResultKind.Unauthorized, "denied"));
        service.ProductDetailHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentProductDetail>.Failure(OperatorUiResultKind.Unauthorized, "denied"));
        var navigation = context.Services.GetRequiredService<NavigationManager>();

        navigation.NavigateTo("/archive/calendar");
        var calendar = context.Render<ArchiveCalendarPage>();
        calendar.WaitForAssertion(() => StringAssert.EndsWith(navigation.Uri, "/Account/AccessDenied", StringComparison.Ordinal));
        navigation.NavigateTo("/archive/products");
        var products = context.Render<ProductsPage>();
        products.WaitForAssertion(() => StringAssert.EndsWith(navigation.Uri, "/Account/AccessDenied", StringComparison.Ordinal));
        navigation.NavigateTo("/archive/products/detail");
        var detail = context.Render<ProductDetail>(parameters => parameters.Add(page => page.ArtifactId, Guid.NewGuid()));

        detail.WaitForAssertion(() =>
        {
            StringAssert.EndsWith(navigation.Uri, "/Account/AccessDenied", StringComparison.Ordinal);
            Assert.IsEmpty(detail.FindAll("[role='alert']"));
        });
    }

    [TestMethod]
    public void Calendar_ReportsUtcFallbackEmptyRangeAndErrors()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var utcNight = ObservingDayCalendar.Utc.Resolve(new DateOnly(2026, 7, 23));
        service.CalendarHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryCalendar>.Success(
            new(TimeZoneInfo.Utc.Id, true, [new(utcNight, 0, 0, null, null)])));

        var cut = context.Render<ArchiveCalendarPage>();
        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "Observing nights use UTC days", StringComparison.Ordinal);
            // An empty month reads as zero observed nights, and every cell says so.
            StringAssert.Contains(cut.Find(".calendar-summary").TextContent, "Nights with captures0", StringComparison.Ordinal);
            Assert.IsTrue(cut.FindAll(".calendar-day").All(cell => cell.ClassList.Contains("calendar-day--empty")));
            StringAssert.Contains(cut.Find(".calendar-legend").TextContent, "12:00 to 12:00 UTC", StringComparison.Ordinal);
        });

        service.CalendarHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentGalleryCalendar>.Failure(OperatorUiResultKind.Unavailable, "calendar read failed"));
        var failed = context.Render<ArchiveCalendarPage>();
        failed.WaitForAssertion(() =>
        {
            StringAssert.Contains(failed.Find("[role='alert']").TextContent, "calendar read failed", StringComparison.Ordinal);
            Assert.AreEqual("assertive", failed.Find("[role='alert']").GetAttribute("aria-live"));
            Assert.IsNotNull(failed.Find("[role='alert'] button"));
        });
    }

    [TestMethod]
    public void PageStateNotice_AnnouncesAlertsAssertivelyAndStatusPolitely()
    {
        using var context = new BunitContext();
        var unauthorized = context.Render<PageStateNotice>(parameters => parameters
            .Add(static notice => notice.Kind, PageStateNotice.PageStateKind.Unauthorized)
            .Add(static notice => notice.Title, "Owner sign-in required"));
        var stale = context.Render<PageStateNotice>(parameters => parameters
            .Add(static notice => notice.Kind, PageStateNotice.PageStateKind.Stale)
            .Add(static notice => notice.Title, "Stale"));

        var alert = unauthorized.Find("section");
        Assert.AreEqual("alert", alert.GetAttribute("role"));
        Assert.AreEqual("assertive", alert.GetAttribute("aria-live"));
        var status = stale.Find("section");
        Assert.AreEqual("status", status.GetAttribute("role"));
        Assert.AreEqual("polite", status.GetAttribute("aria-live"));
    }

    [TestMethod]
    public void Products_ListsRetainedOutputsWithFiltersAndBoundedPaging()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        CameraAgentProductQuery? observed = null;
        var product = Product(Guid.Parse("00000000-0000-0000-0000-000000000201"));
        service.ProductPageHandler = (query, _) =>
        {
            observed = query;
            return ValueTask.FromResult(OperatorUiResult<CameraAgentProductPage>.Success(
                new([product, product with { ArtifactId = Guid.NewGuid(), NodeId = "gallery-materialization-abc", IsMaterialization = true, Role = FrameArtifactRole.AnnotatedPreview }], "older")));
        };
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/archive/products?role=Combined&availability=Available");

        var cut = context.Render<ProductsPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(FrameArtifactRole.Combined, observed?.Role);
            Assert.AreEqual("Available", observed?.Availability);
            // An explicit Available in the URL maps onto the default option instead of a blank select.
            Assert.AreEqual(string.Empty, cut.Find("#product-availability").GetAttribute("value") ?? string.Empty);
            var rows = cut.FindAll(".product-table tbody tr");
            Assert.HasCount(2, rows);
            StringAssert.Contains(rows[0].TextContent, "Combined", StringComparison.Ordinal);
            StringAssert.Contains(rows[0].TextContent, "#42", StringComparison.Ordinal);
            StringAssert.Contains(rows[0].TextContent, "rolling-mean", StringComparison.Ordinal);
            StringAssert.Contains(rows[1].TextContent, "Saved layer stack", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "This bounded page omits older products", StringComparison.Ordinal);
            var detailLink = rows[0].QuerySelector("a")!.GetAttribute("href")!;
            StringAssert.StartsWith(detailLink, "/archive/products/00000000-0000-0000-0000-000000000201?returnUrl=", StringComparison.Ordinal);
            Assert.AreEqual("/gallery/00000000-0000-0000-0000-000000000001", rows[0].QuerySelectorAll("a")[1].GetAttribute("href"));
        });
        cut.Find(".cursor-nav .btn-primary").Click();
        cut.WaitForAssertion(() => StringAssert.Contains(navigation.Uri, "cursor=older", StringComparison.Ordinal));

        service.ProductPageHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentProductPage>.Success(new([], null)));
        navigation.NavigateTo("/archive/products?role=Metadata");
        var empty = context.Render<ProductsPage>();
        empty.WaitForAssertion(() => StringAssert.Contains(empty.Markup, "No retained products match", StringComparison.Ordinal));

        service.ProductPageHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentProductPage>.Failure(OperatorUiResultKind.Invalid, "The product kind filter is invalid."));
        navigation.NavigateTo("/archive/products?kind=Timelapse");
        var invalid = context.Render<ProductsPage>();
        invalid.WaitForAssertion(() => StringAssert.Contains(invalid.Find(".page-state--info").TextContent, "product kind filter is invalid", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ProductDetail_ShowsIdentitySourcesGenerationAndNotFound()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var product = Product(Guid.Parse("00000000-0000-0000-0000-000000000201"));
        var sourceCapture = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var night = Phoenix.Resolve(OperatorUiTestData.Now.AddSeconds(-5));
        service.ProductDetailHandler = (artifactId, _) => ValueTask.FromResult(artifactId == product.ArtifactId
            ? OperatorUiResult<CameraAgentProductDetail>.Success(new(
                product,
                OperatorUiTestData.Now.AddSeconds(-5),
                night,
                "rig-test",
                [new(0, Guid.NewGuid(), FrameArtifactRole.Raw, sourceCapture, 41), new(1, Guid.NewGuid(), null, null, null)],
                false,
                new("combine", "Completed", 1, OperatorUiTestData.Now.AddSeconds(-4), OperatorUiTestData.Now.AddSeconds(-3), 850, "Produced"),
                [new(Guid.NewGuid(), new string('P', 64), OperatorUiTestData.Now.AddMinutes(-10), "Available")]))
            : OperatorUiResult<CameraAgentProductDetail>.Failure(OperatorUiResultKind.NotFound, "The requested product was not found."));

        var cut = context.Render<ProductDetail>(parameters => parameters.Add(page => page.ArtifactId, product.ArtifactId));

        cut.WaitForAssertion(() =>
        {
            var text = cut.Markup;
            StringAssert.Contains(text, "Combined · window", StringComparison.Ordinal);
            StringAssert.Contains(text, "2026-07-22 (America/Phoenix)", StringComparison.Ordinal);
            StringAssert.Contains(text, new string('K', 64), StringComparison.Ordinal);
            StringAssert.Contains(text, "Capture #41", StringComparison.Ordinal);
            StringAssert.Contains(text, "Unretained artifact", StringComparison.Ordinal);
            StringAssert.Contains(text, "Completed (Produced)", StringComparison.Ordinal);
            StringAssert.Contains(text, "850 ms", StringComparison.Ordinal);
            StringAssert.Contains(text, "Retained predecessors", StringComparison.Ordinal);
            Assert.AreEqual("/api/v1/operations/artifacts/00000000-0000-0000-0000-000000000201/content",
                cut.FindAll("a").Single(link => link.TextContent.Contains("Download content", StringComparison.Ordinal)).GetAttribute("href"));
        });

        var missing = context.Render<ProductDetail>(parameters => parameters.Add(page => page.ArtifactId, Guid.NewGuid()));
        missing.WaitForAssertion(() => StringAssert.Contains(missing.Find("[role='alert']").TextContent, "was not found", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Transients_PassCalendarRangeAndLinkSourceCaptures()
    {
        using var context = new BunitContext();
        var transient = new RecordingTransientUiService();
        context.Services.AddSingleton<ICameraAgentTransientUiService>(transient);
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(new TestOperatorUiService());
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/transients?from=2026-07-21T19:00:00&to=2026-07-22T18:59:59");

        var list = context.Render<TransientPage>();

        list.WaitForAssertion(() =>
        {
            Assert.AreEqual(new DateTimeOffset(2026, 7, 21, 19, 0, 0, TimeSpan.Zero), transient.LastQuery?.FromUtc);
            Assert.AreEqual(new DateTimeOffset(2026, 7, 22, 18, 59, 59, TimeSpan.Zero), transient.LastQuery?.ToUtc);
            StringAssert.Contains(list.Find(".range-state").TextContent, "Showing candidates created between", StringComparison.Ordinal);
            var visible = list.Find(".transient-page").TextContent;
            foreach (var forbidden in ForbiddenCandidateClaims)
            {
                Assert.IsFalse(visible.Contains(forbidden, StringComparison.OrdinalIgnoreCase), forbidden);
            }
        });

        navigation.NavigateTo("/transients?from=not-a-date");
        var invalid = context.Render<TransientPage>();
        invalid.WaitForAssertion(() => StringAssert.Contains(invalid.Find("[role='alert']").TextContent, "not a valid UTC date", StringComparison.Ordinal));

        var candidateId = Guid.Parse("00000000-0000-0000-0000-000000000301");
        var sourceCapture = Guid.Parse("00000000-0000-0000-0000-000000000002");
        transient.Detail = OperatorUiResult<CameraAgentTransientOperatorDetail>.Success(new(
            new(candidateId, Guid.NewGuid(), "Provisional", "pending", "candidate_persisted", OperatorUiTestData.Now, OperatorUiTestData.Now, "Available", "Absent", "Absent", "Absent"),
            new("Available", OperatorUiTestData.Now, null, 1, null, null, []),
            new("Absent"), new("Absent"), new("Absent", ReasonCodes: []), new("Absent"),
            [new(0, Guid.NewGuid(), Guid.NewGuid(), FrameArtifactRole.Raw, sourceCapture, 41, OperatorUiTestData.Now, OperatorUiTestData.Now, OperatorUiTestData.Now.AddSeconds(2))]));
        var detail = context.Render<TransientDetail>(parameters => parameters.Add(page => page.CandidateId, candidateId));
        detail.WaitForAssertion(() =>
        {
            var link = detail.Find(".source-list a");
            Assert.AreEqual("/gallery/00000000-0000-0000-0000-000000000002", link.GetAttribute("href"));
            StringAssert.Contains(link.TextContent, "Capture #41", StringComparison.Ordinal);
            var text = detail.Find(".transient-detail").TextContent;
            foreach (var forbidden in ForbiddenCandidateClaims)
            {
                Assert.IsFalse(text.Contains(forbidden, StringComparison.OrdinalIgnoreCase), forbidden);
            }
        });
    }

    private static TestOperatorUiService Configure(BunitContext context)
    {
        var service = new TestOperatorUiService();
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);
        context.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(OperatorUiTestData.Now));
        context.Services.AddSingleton<IObservingDayCalendarProvider>(new FixedObservingDayCalendarProvider(Phoenix));
        return service;
    }

    private static CameraAgentProduct Product(Guid artifactId) => new(
        artifactId,
        new string('K', 64),
        Guid.Parse("00000000-0000-0000-0000-000000000001"),
        42,
        "agent-test",
        "combine",
        FrameArtifactRole.Combined,
        "window",
        OperatorUiTestData.Now.AddSeconds(-3),
        OperatorUiTestData.Now.AddSeconds(-3),
        "application/x-hvo-packed-image",
        new string('B', 64),
        4096,
        new CameraAgentGalleryRecipe("rolling-mean", "1.0.0", "build-7", new string('C', 64), new string('D', 64)),
        [new("mean", "v1")],
        null,
        null,
        null,
        "Available",
        null,
        TimeSpan.FromSeconds(3),
        3,
        null,
        null,
        "Live",
        false);

    private sealed class RecordingTransientUiService : ICameraAgentTransientUiService
    {
        public ValueTask<OperatorUiResult<TransientCaptureStageView>> GetCaptureStagesAsync(Guid captureId, CancellationToken cancellationToken)
            => ValueTask.FromResult(OperatorUiResult<TransientCaptureStageView>.Success(new(captureId, [])));

        internal CameraAgentTransientOperatorQuery? LastQuery { get; private set; }
        internal OperatorUiResult<CameraAgentTransientOperatorDetail> Detail { get; set; } =
            OperatorUiResult<CameraAgentTransientOperatorDetail>.Failure(OperatorUiResultKind.NotFound, "The transient candidate was not found.");

        public ValueTask<OperatorUiResult<CameraAgentTransientOperatorPage>> GetPageAsync(CameraAgentTransientOperatorQuery query, CancellationToken cancellationToken)
        {
            LastQuery = query;
            return ValueTask.FromResult(OperatorUiResult<CameraAgentTransientOperatorPage>.Success(new([], null)));
        }

        public ValueTask<OperatorUiResult<CameraAgentTransientOperatorDetail>> GetCandidateAsync(Guid candidateId, CancellationToken cancellationToken)
            => ValueTask.FromResult(Detail);
    }
}
