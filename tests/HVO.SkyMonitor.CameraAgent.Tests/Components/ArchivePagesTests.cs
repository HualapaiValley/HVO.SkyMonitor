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
    // Affirmative claims that would imply authority CameraAgent does not have over local candidates,
    // matched against visible text so a negated disclaimer is not mistaken for a claim.
    private static readonly string[] ForbiddenCandidateClaims = ["fireball", "ground track", "impact location", "reconstructed event", "validated event", "correlated event", "published event", "multi-site", "entry speed", "peak altitude"];

    [TestMethod]
    public void Calendar_ListsObservingNightsWithCountsAndDayLinks()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        CameraAgentGalleryCalendarQuery? observed = null;
        var night = Phoenix.Resolve(new DateOnly(2026, 7, 21));
        var emptyNight = Phoenix.Resolve(new DateOnly(2026, 7, 22));
        service.CalendarHandler = (query, _) =>
        {
            observed = query;
            return ValueTask.FromResult(OperatorUiResult<CameraAgentGalleryCalendar>.Success(new("America/Phoenix", false,
            [
                new(night, 12, 1, night.StartUtc.AddHours(2), night.EndUtc.AddHours(-3)),
                new(emptyNight, 0, 0, null, null)
            ])));
        };

        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/archive/calendar?to=2026-07-23");
        var cut = context.Render<ArchiveCalendarPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(new DateOnly(2026, 7, 23), observed?.ToDate);
            Assert.AreEqual(new DateOnly(2026, 7, 23).AddDays(-(ArchiveCalendarPage.RangeDays - 1)), observed?.FromDate);
            var nights = cut.FindAll(".night");
            Assert.HasCount(2, nights);
            // Newest night first.
            StringAssert.Contains(nights[0].TextContent, "Jul 22 2026", StringComparison.Ordinal);
            Assert.IsTrue(nights[0].ClassList.Contains("night--empty"));
            StringAssert.Contains(nights[1].TextContent, "Jul 21 2026", StringComparison.Ordinal);
            StringAssert.Contains(nights[1].TextContent, "Captures12", StringComparison.Ordinal);
            StringAssert.Contains(nights[1].TextContent, "Candidates1", StringComparison.Ordinal);
            var links = nights[1].QuerySelectorAll(".night__links a").Select(link => link.GetAttribute("href")).ToArray();
            Assert.HasCount(2, links);
            // Millisecond precision keeps the linked day identical to the counted day.
            Assert.AreEqual("/gallery?from=2026-07-21T19:00:00.000&to=2026-07-22T18:59:59.999", links[0]);
            Assert.AreEqual("/transients?from=2026-07-21T19:00:00.000&to=2026-07-22T18:59:59.999", links[1]);
            StringAssert.Contains(nights[1].QuerySelector(".night__zone")!.TextContent, "12:00:00 → 12:00:00 America/Phoenix", StringComparison.Ordinal);
            Assert.IsEmpty(nights[0].QuerySelectorAll(".night__links a"));
            Assert.IsFalse(cut.Markup.Contains("UTC days", StringComparison.Ordinal));
        });
        StringAssert.Contains(cut.FindAll(".range-nav a")[0].GetAttribute("href"), "to=2026-06-22", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Calendar_DefaultsToTheCurrentObservingNightAndClampsTheRange()
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
        // Now is 12:00 UTC on 23 July, 05:00 in Phoenix, inside the night that began at noon on the 22nd.
        cut.WaitForAssertion(() => Assert.AreEqual(new DateOnly(2026, 7, 22), observed?.ToDate));

        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/archive/calendar?to=0001-01-05");
        var clamped = context.Render<ArchiveCalendarPage>();
        clamped.WaitForAssertion(() =>
        {
            Assert.AreEqual(DateOnly.MinValue.AddDays(ArchiveCalendarPage.RangeDays * 2), observed?.ToDate);
            Assert.AreEqual(DateOnly.MinValue.AddDays(ArchiveCalendarPage.RangeDays + 1), observed?.FromDate);
            Assert.IsNotNull(clamped.Find(".range-nav"));
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
            StringAssert.Contains(cut.Markup, "No retained evidence in this range", StringComparison.Ordinal);
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
