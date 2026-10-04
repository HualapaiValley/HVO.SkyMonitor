using System.Globalization;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

/// <summary>The generated products library that the Products page opens with, and the retained view beside it.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class GeneratedProductsLibraryTests
{
    private static readonly DateOnly Newest = NightlyProductFixture.ObservingDate;
    private static readonly DateOnly Older = Newest.AddDays(-1);

    private static readonly string[] NewestFirstCards =
        ["Star trail, night of 1 Oct 2026", "Keogram, night of 1 Oct 2026", "Star trail, night of 30 Sep 2026", "Keogram, night of 30 Sep 2026"];

    private static readonly string[] NoMatchActions = ["/archive/products", "/archive/day/2026-10-01"];

    [TestMethod]
    public void Library_ShowsEachDailyFinalAsACardWithItsIdentitySourcesSpanAndAutomation()
    {
        using var context = new BunitContext();
        var (_, nightly) = Configure(context);
        var keogram = Entry(Newest, NightlyProductKind.Keogram, NightlyProductWindowDisposition.Produced);
        var starTrail = Entry(Newest, NightlyProductKind.StarTrail, NightlyProductWindowDisposition.Produced, hourlyProduced: 9, hourlyWithout: 2);
        var partial = Entry(Older, NightlyProductKind.StarTrail, NightlyProductWindowDisposition.NoSources, hourlyProduced: 3, hourlyWithout: 8);
        var rejected = Entry(Older, NightlyProductKind.Keogram, NightlyProductWindowDisposition.Rejected, reason: "insufficient-coverage");
        nightly.LibraryHandler = _ => OperatorUiResult<NightlyProductLibraryPage>.Success(
            new([keogram, starTrail, partial, rejected], Older, null));

        var cut = Render(context, "/archive/products");

        cut.WaitForAssertion(() => Assert.HasCount(4, cut.FindAll(".library-card")));
        Assert.AreEqual(new NightlyProductLibraryQuery(GeneratedProductsView.PageDates), nightly.LibraryQueries.Single());
        Assert.AreEqual("Generated products", cut.Find("h1").TextContent.Trim());
        Assert.AreEqual("page", cut.Find(".product-views a[href='/archive/products']").GetAttribute("aria-current"));
        Assert.IsNull(cut.Find($".product-views a[href='{ProductsPage.RetainedPath}']").GetAttribute("aria-current"));
        // Newest report date first, and each date reads star trail then keogram as the observing day page does.
        CollectionAssert.AreEqual(NewestFirstCards, cut.FindAll(".library-card").Select(static card => card.GetAttribute("aria-label")).ToArray());
        Assert.AreEqual("4 product cards from 2 observing days, newest first.", cut.Find(".library-summary").TextContent);

        var produced = cut.FindAll(".library-card")[0];
        var product = starTrail.Product!;
        Assert.AreEqual("available", produced.GetAttribute("data-nightly-state"));
        Assert.AreEqual(NightlyProductLinks.Detail(product.ProductId), produced.QuerySelector("a.library-card__media")!.GetAttribute("href"));
        Assert.AreEqual(NightlyProductLinks.Preview(product.ProductId), produced.QuerySelector("img")!.GetAttribute("src"));
        StringAssert.Contains(produced.TextContent, "Nightly star trail produced, 9 hourly star trails", StringComparison.Ordinal);
        StringAssert.Contains(produced.TextContent, "12 segment products", StringComparison.Ordinal);
        // The span is local to the period's own site, from one morning to the next.
        var mst = TimeSpan.FromHours(-7);
        StringAssert.Contains(produced.TextContent, string.Create(CultureInfo.InvariantCulture,
            $"Frames{product.FirstObservationUtc.ToOffset(mst):HH:mm} 1 Oct–{product.LastObservationUtc.ToOffset(mst):HH:mm} 2 Oct"),
            StringComparison.Ordinal);
        Assert.AreEqual("Produced", produced.QuerySelector(".library-card__facts .hvo-chip")!.TextContent);
        Assert.AreEqual("Hourly: 9 of 11 completed hours produced", produced.QuerySelector(".library-card__hourly")!.TextContent);
        Assert.AreEqual("/archive/day/2026-10-01", produced.QuerySelector(".library-card__meta a")!.GetAttribute("href"));
        StringAssert.Contains(produced.QuerySelector(".library-card__meta")!.TextContent, "Automation: fixture-star-trail · revision 1",
            StringComparison.Ordinal);

        // An evaluated period without a nightly product keeps its recorded reason and has no image or product link.
        var partialCard = cut.FindAll(".library-card")[2];
        Assert.AreEqual("partial", partialCard.GetAttribute("data-nightly-state"));
        Assert.IsNull(partialCard.QuerySelector("img"));
        Assert.IsNull(partialCard.QuerySelector("a.library-card__media"));
        StringAssert.Contains(partialCard.QuerySelector(".library-card__media--empty")!.TextContent, "No nightly star trail", StringComparison.Ordinal);
        StringAssert.Contains(partialCard.TextContent, "Partial: 3 hourly star trails, no nightly star trail", StringComparison.Ordinal);
        StringAssert.Contains(partialCard.QuerySelector(".library-card__facts")!.TextContent, "Period", StringComparison.Ordinal);
        Assert.AreEqual("/archive/day/2026-09-30", partialCard.QuerySelector(".library-card__meta a")!.GetAttribute("href"));
        var rejectedCard = cut.FindAll(".library-card")[3];
        Assert.AreEqual("unavailable not-produced", rejectedCard.GetAttribute("data-nightly-state"));
        Assert.IsNull(rejectedCard.QuerySelector("img"));
        StringAssert.Contains(rejectedCard.QuerySelector(".library-card__media--empty")!.TextContent, "Not produced", StringComparison.Ordinal);
        StringAssert.Contains(rejectedCard.TextContent, "Keogram not produced: rejected (insufficient-coverage)", StringComparison.Ordinal);
        Assert.IsNull(rejectedCard.QuerySelector(".library-card__hourly"));

        // The first page offers only older products, continuing below the oldest date it shows.
        Assert.AreEqual("/archive/products?before=2026-09-30", cut.Find(".library-pages .btn-primary").GetAttribute("href"));
        Assert.HasCount(1, cut.FindAll(".library-pages a"));
    }

    [TestMethod]
    public void Library_FailedPreviewIsAnExplicitPlaceholderThatStillOpensTheProduct()
    {
        using var context = new BunitContext();
        var (_, nightly) = Configure(context, previewFailed: true);
        var entry = Entry(Newest, NightlyProductKind.StarTrail, NightlyProductWindowDisposition.Produced);
        nightly.LibraryHandler = _ => OperatorUiResult<NightlyProductLibraryPage>.Success(new([entry], null, null));

        var cut = Render(context, "/archive/products");

        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".library-card__media").TextContent, "Preview unavailable", StringComparison.Ordinal));
        Assert.IsEmpty(cut.FindAll(".library-card img"));
        Assert.AreEqual(NightlyProductLinks.Detail(entry.Product!.ProductId), cut.Find("a.library-card__media").GetAttribute("href"));
        Assert.IsEmpty(cut.FindAll(".library-pages"));
    }

    [TestMethod]
    public void Library_FiltersAreUrlStateThatMapsOntoTheLibraryQuery()
    {
        using var context = new BunitContext();
        var (_, nightly) = Configure(context);
        const string Filtered = "/archive/products?type=keogram&status=partial&day=2026-10-01&before=2026-09-01";

        var cut = Render(context, Filtered);

        cut.WaitForAssertion(() => Assert.HasCount(1, nightly.LibraryQueries));
        // A single observing day is never paged, so its page position is dropped.
        Assert.AreEqual(new NightlyProductLibraryQuery(GeneratedProductsView.PageDates, NightlyProductKind.Keogram,
            NightlyProductLibraryState.Partial, Newest), nightly.LibraryQueries[0]);
        Assert.AreEqual(NightlyProductContract.KeogramTarget, cut.Find("#library-type").GetAttribute("value"));
        Assert.AreEqual("partial", cut.Find("#library-status").GetAttribute("value"));
        Assert.AreEqual("2026-10-01", cut.Find("#library-day").GetAttribute("value"));
        // Choices CameraAgent cannot answer truthfully stay visible but disabled, with the reason beside them.
        foreach (var unrecorded in new[] { "#library-type option[value='time-lapse']", "#library-type option[value='daily-summary']", "#library-status option[value='running']" })
        {
            Assert.IsTrue(cut.Find(unrecorded).HasAttribute("disabled"), unrecorded);
        }
        Assert.IsFalse(cut.Find("#library-status option[value='partial']").HasAttribute("disabled"));
        Assert.AreEqual("library-filter-note", cut.Find("#library-type").GetAttribute("aria-describedby"));
        StringAssert.Contains(cut.Find("#library-filter-note").TextContent, "not yet generated", StringComparison.Ordinal);

        var navigation = context.Services.GetRequiredService<NavigationManager>();
        cut.Find("#library-type").Change(NightlyProductContract.StarTrailTarget);
        StringAssert.EndsWith(navigation.Uri, "/archive/products?type=star-trail&status=partial&day=2026-10-01", StringComparison.Ordinal);
        Render(context, Filtered).Find("#library-status").Change(string.Empty);
        StringAssert.EndsWith(navigation.Uri, "/archive/products?type=keogram&day=2026-10-01", StringComparison.Ordinal);
        Render(context, Filtered).Find("#library-day").Change("2026-09-30");
        StringAssert.EndsWith(navigation.Uri, "/archive/products?type=keogram&status=partial&day=2026-09-30", StringComparison.Ordinal);
        Render(context, Filtered).Find("#library-day").Change(string.Empty);
        StringAssert.EndsWith(navigation.Uri, "/archive/products?type=keogram&status=partial", StringComparison.Ordinal);
        Render(context, Filtered).FindAll(".library-toolbar button").Single(static button => button.TextContent == "Show all days").Click();
        StringAssert.EndsWith(navigation.Uri, "/archive/products?type=keogram&status=partial", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("type=time-lapse", "Timelapses are not yet generated")]
    [DataRow("type=daily-summary", "Daily summaries are not yet generated")]
    [DataRow("type=fireball", "not a product type CameraAgent generates")]
    [DataRow("status=running", "Generation progress is not recorded")]
    [DataRow("status=available", "not a recorded product state")]
    [DataRow("day=yesterday", "observing day filter is not a date")]
    [DataRow("before=2026-13-40", "page position is not a date")]
    public void Library_UnrecordedOrMalformedFiltersAreExplainedInsteadOfQueried(string query, string expected)
    {
        using var context = new BunitContext();
        var (_, nightly) = Configure(context);

        var cut = Render(context, "/archive/products?" + query);

        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".page-state--info").TextContent, expected, StringComparison.Ordinal));
        Assert.IsEmpty(nightly.LibraryQueries);
        Assert.IsEmpty(cut.FindAll(".library-card"));
        Assert.AreEqual("/archive/products", cut.Find(".page-state--info a").GetAttribute("href"));
    }

    [TestMethod]
    public void Library_EmptyStatesTellNoProductsNoMatchAndNoOlderApart()
    {
        using var context = new BunitContext();
        Configure(context);

        var none = Render(context, "/archive/products");
        none.WaitForAssertion(() => StringAssert.Contains(none.Find(".page-state").TextContent, "No generated products yet", StringComparison.Ordinal));
        Assert.AreEqual("/operations/automations", none.Find(".page-state a").GetAttribute("href"));

        var noMatch = Render(context, "/archive/products?status=produced&day=2026-10-01");
        noMatch.WaitForAssertion(() => StringAssert.Contains(noMatch.Find(".page-state").TextContent, "No generated products match these filters", StringComparison.Ordinal));
        CollectionAssert.AreEqual(NoMatchActions, noMatch.FindAll(".page-state a").Select(static link => link.GetAttribute("href")).ToArray());

        // An empty older page cannot speak for newer pages, so it returns to them with the filters kept.
        var noOlder = Render(context, "/archive/products?type=keogram&before=2026-09-01");
        noOlder.WaitForAssertion(() => StringAssert.Contains(noOlder.Find(".page-state").TextContent, "No older generated products", StringComparison.Ordinal));
        Assert.AreEqual("/archive/products?type=keogram", noOlder.Find(".page-state a").GetAttribute("href"));
        Assert.IsEmpty(noOlder.FindAll(".library-pages"));
    }

    [TestMethod]
    public void Library_BoundedScanIsStatedAndPagingKeepsTheFilters()
    {
        using var context = new BunitContext();
        var (_, nightly) = Configure(context);
        var searched = new DateOnly(2026, 7, 31);
        nightly.LibraryHandler = _ => OperatorUiResult<NightlyProductLibraryPage>.Success(new([], searched, searched));

        var bounded = Render(context, "/archive/products?status=produced");

        bounded.WaitForAssertion(() => StringAssert.Contains(bounded.Find(".library-bounded").TextContent,
            $"examining {NightlyProductProjectionContract.MaximumLibraryScannedDates} evaluated days, back to 31 Jul 2026", StringComparison.Ordinal));
        Assert.IsEmpty(bounded.FindAll(".page-state"));
        Assert.AreEqual("/archive/products?status=produced&before=2026-07-31", bounded.Find(".library-pages .btn-primary").GetAttribute("href"));

        nightly.LibraryHandler = _ => OperatorUiResult<NightlyProductLibraryPage>.Success(
            new([Entry(Newest, NightlyProductKind.Keogram, NightlyProductWindowDisposition.Produced)], null, null));
        var older = Render(context, "/archive/products?status=produced&before=2026-10-02");
        older.WaitForAssertion(() => Assert.HasCount(1, older.FindAll(".library-card")));
        Assert.AreEqual(new DateOnly(2026, 10, 2), nightly.LibraryQueries[^1].Before);
        Assert.AreEqual("/archive/products?status=produced", older.Find(".library-pages a").GetAttribute("href"));
        Assert.IsEmpty(older.FindAll(".library-pages .btn-primary"));
    }

    [TestMethod]
    public void Library_FailureOffersRetryInvalidIsInformationalAndDeniedRedirects()
    {
        using var context = new BunitContext();
        var (_, nightly) = Configure(context);
        nightly.LibraryHandler = _ => OperatorUiResult<NightlyProductLibraryPage>.Failure(OperatorUiResultKind.Unavailable, "The nightly product store is unavailable.");

        var cut = Render(context, "/archive/products");

        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".page-state--error").TextContent, "The nightly product store is unavailable.", StringComparison.Ordinal));
        nightly.LibraryHandler = _ => OperatorUiResult<NightlyProductLibraryPage>.Success(
            new([Entry(Newest, NightlyProductKind.StarTrail, NightlyProductWindowDisposition.Produced)], null, null));
        cut.Find(".page-state button").Click();
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll(".library-card")));
        Assert.HasCount(2, nightly.LibraryQueries);

        nightly.LibraryHandler = _ => OperatorUiResult<NightlyProductLibraryPage>.Failure(OperatorUiResultKind.Invalid,
            "The requested products page is outside the generated products library bound.");
        var invalid = Render(context, "/archive/products");
        invalid.WaitForAssertion(() => StringAssert.Contains(invalid.Find(".page-state--info").TextContent, "outside the generated products library bound", StringComparison.Ordinal));

        nightly.LibraryHandler = _ => OperatorUiResult<NightlyProductLibraryPage>.Failure(OperatorUiResultKind.Unauthorized, "denied");
        var denied = Render(context, "/archive/products");
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        denied.WaitForAssertion(() => StringAssert.EndsWith(navigation.Uri, "/Account/AccessDenied", StringComparison.Ordinal));
        Assert.IsEmpty(denied.FindAll(".library-card"));
    }

    [TestMethod]
    [DataRow("view=retained")]
    [DataRow("role=Combined")]
    [DataRow("kind=PixelData")]
    [DataRow("availability=Missing")]
    [DataRow("recipe=rolling-mean")]
    [DataRow("cursor=older")]
    [DataRow("pageSize=10")]
    public void Products_RetainedLinksOpenTheRetainedViewUnchanged(string query)
    {
        using var context = new BunitContext();
        var (operations, nightly) = Configure(context);
        CameraAgentProductQuery? observed = null;
        operations.ProductPageHandler = (productQuery, _) =>
        {
            observed = productQuery;
            return ValueTask.FromResult(OperatorUiResult<CameraAgentProductPage>.Success(new([], null)));
        };

        var cut = Render(context, "/archive/products?" + query);

        cut.WaitForAssertion(() => Assert.IsNotNull(observed));
        Assert.AreEqual("Retained outputs", cut.Find("h1").TextContent.Trim());
        Assert.AreEqual("page", cut.Find($".product-views a[href='{ProductsPage.RetainedPath}']").GetAttribute("aria-current"));
        Assert.IsNotNull(cut.Find("form.product-filters"));
        Assert.IsEmpty(cut.FindAll(".library-toolbar"));
        Assert.IsEmpty(nightly.LibraryQueries);
    }

    [TestMethod]
    public void Products_LibraryViewDoesNotReadRetainedOutputs()
    {
        using var context = new BunitContext();
        var (operations, nightly) = Configure(context);
        var retainedReads = 0;
        operations.ProductPageHandler = (_, _) =>
        {
            retainedReads++;
            return ValueTask.FromResult(OperatorUiResult<CameraAgentProductPage>.Success(new([], null)));
        };

        var cut = Render(context, "/archive/products?type=star-trail");

        cut.WaitForAssertion(() => Assert.HasCount(1, nightly.LibraryQueries));
        Assert.AreEqual(0, retainedReads);
        Assert.IsEmpty(cut.FindAll("form.product-filters, .product-table"));
        Assert.AreEqual("/operations/automations", cut.Find(".page-header a").GetAttribute("href"));
    }

    private static (TestOperatorUiService Operations, TestNightlyProductUiService Nightly) Configure(BunitContext context, bool previewFailed = false)
    {
        RetainedPreviewImageTestSupport.Configure(context, previewFailed);
        var operations = new TestOperatorUiService();
        var nightly = new TestNightlyProductUiService();
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(operations);
        context.Services.AddSingleton<ICameraAgentNightlyProductUiService>(nightly);
        context.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(OperatorUiTestData.Now));
        return (operations, nightly);
    }

    private static IRenderedComponent<ProductsPage> Render(BunitContext context, string url)
    {
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        return context.Render<ProductsPage>();
    }

    /// <summary>A library entry of one recorded daily final, summarized as the store summarizes it.</summary>
    private static NightlyProductLibraryEntry Entry(DateOnly date, NightlyProductKind kind, NightlyProductWindowDisposition disposition,
        string? reason = null, int hourlyProduced = 0, int hourlyWithout = 0)
    {
        var occurrence = NightlyDayFixture.Daily(kind, observingDate: date);
        var record = disposition == NightlyProductWindowDisposition.Produced
            ? NightlyDayFixture.Produced(occurrence, NightlyDayFixture.Product(occurrence, kind), candidates: 40, admitted: 12)
            : NightlyDayFixture.Without(occurrence, kind, disposition, candidates: 3, reason);
        var summary = new NightlyProductDateSummary(date, kind, record.ReportingPeriod.IdentitySha256, record.FinalProduct?.ProductId,
            disposition, reason, hourlyProduced, hourlyWithout);
        return new(summary, record, OtherPeriodRecorded: false);
    }
}
