using Bunit;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
public sealed class ObservingDayTimeLapseStateTests
{
    [TestMethod]
    [TestCategory("Unit")]
    [DataRow(true, true, true)]
    [DataRow(true, false, true)]
    [DataRow(false, true, true)]
    [DataRow(false, false, true)]
    [DataRow(true, true, false)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, false, false)]
    public void EmptyDay_StatesGenerationStatusAndKeepsSampleSeparate(bool storeAvailable, bool enabled, bool hasSample)
    {
        using var context = CreateContext();
        context.Services.AddSingleton<ICameraAgentNightlyProductUiService>(new TestNightlyProductUiService
        {
            DayHandler = date => OperatorUiResult<CameraAgentNightlyDayView>.Success(new(new NightlyProductDay(date, [], []),
                hasSample ? new("/api/v1/operations/time-lapse-sample", "video/mp4", 1280, 720) : null)
            {
                TimeLapses = storeAvailable ? new(date, [], []) : null,
                TimeLapseGenerationEnabled = enabled
            })
        });

        AssertState(context, storeAvailable, enabled, hasSample);
    }

    [TestMethod]
    [TestCategory("Integration")]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public async Task EmptySqliteCatalog_ProjectsConfiguredStateIntoTileAndSampleViewer(bool storeAvailable, bool enabled)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-observing-day-video");
        try
        {
            using var context = CreateContext();
            var authorization = context.AddAuthorization();
            authorization.SetAuthorized("owner");
            authorization.SetPolicies(CameraAgentAuthorizationPolicyNames.OperationsReadV1);
            var samplePath = Path.Combine(root, "sample.mp4");
            await File.WriteAllBytesAsync(samplePath, [0]).ConfigureAwait(false);
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                TimeLapses = new() { Enabled = enabled },
                TimeLapseSample = new() { Enabled = true, FilePath = samplePath, MediaType = "video/mp4", Width = 1280, Height = 720 }
            });
            var clock = new FixedTimeProvider(NightlyProductFixture.DayEndUtc.AddDays(1));
            using var nightly = new SqliteNightlyProductStore(options, clock);
            using var videos = new SqliteTimeLapseStore(options, clock);
            context.Services.AddSingleton<ICameraAgentNightlyProductUiService>(services => new CameraAgentNightlyProductUiService(
                services.GetRequiredService<AuthenticationStateProvider>(),
                services.GetRequiredService<IAuthorizationService>(), nightly, options,
                NullLogger<CameraAgentNightlyProductUiService>.Instance, storeAvailable ? videos : null));
            var service = context.Services.GetRequiredService<ICameraAgentNightlyProductUiService>();

            var result = await service.GetDayAsync(NightlyDayFixture.Date, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(result.Value, $"{result.Kind}: {result.Message}");
            Assert.AreEqual(enabled, result.Value.TimeLapseGenerationEnabled);
            Assert.IsNotNull(result.Value.TimeLapseSample);
            Assert.AreEqual(storeAvailable, result.Value.TimeLapses is not null);
            if (result.Value.TimeLapses is { } day) Assert.IsEmpty(day.Jobs);

            AssertState(context, storeAvailable, enabled, hasSample: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        RetainedPreviewImageTestSupport.Configure(context);
        LargeImageViewerTestSupport.Configure(context);
        ProductDetailTestSupport.Configure(context);
        TimeLapsePlayerTestSupport.Configure(context);
        context.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(NightlyProductFixture.DayEndUtc.AddDays(1)));
        context.Services.AddSingleton<IObservingDayCalendarProvider>(new FixedObservingDayCalendarProvider(NightlyProductFixture.Calendar));
        context.Services.AddSingleton<ICameraAgentObservingDayUiService>(new FixtureDayService());
        return context;
    }

    private static void AssertState(BunitContext context, bool storeAvailable, bool enabled, bool hasSample)
    {
        var message = !storeAvailable ? "Time-lapse state could not be read."
            : enabled ? "No time-lapse has been requested for this period."
            : "Time-lapse generation is disabled on this CameraAgent.";
        var label = !storeAvailable ? "State unavailable" : enabled ? "Not requested" : "Disabled";
        var cut = context.Render<ObservingDayPage>(parameters => parameters.Add(page => page.DateText, "2026-10-01"));
        cut.WaitForAssertion(() =>
        {
            var tile = cut.Find("[data-product-kind='time-lapse']");
            StringAssert.Contains(tile.TextContent, message, StringComparison.Ordinal);
            Assert.DoesNotContain("not yet available", cut.Markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Not yet generated", tile.TextContent, StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll("video"));
            if (hasSample)
            {
                StringAssert.Contains(tile.TextContent, "it was not generated from this night and is not counted as a product.", StringComparison.Ordinal);
                Assert.AreEqual(label, tile.QuerySelector(".hvo-chip")!.TextContent);
                Assert.AreEqual("Sample", tile.QuerySelector(".hvo-chip--info")!.TextContent);
            }
            else
            {
                Assert.IsNull(tile.QuerySelector("button"));
            }
        });
        if (!hasSample) return;

        cut.Find($"#{ObservingDayPage.TimeLapseTileId}").Click();
        cut.WaitForAssertion(() =>
        {
            var details = cut.Find("[data-viewer-kind='time-lapse']");
            StringAssert.Contains(details.TextContent, message, StringComparison.Ordinal);
            Assert.AreEqual(label, details.QuerySelector(".hvo-chip")!.TextContent);
            StringAssert.Contains(details.TextContent, "This configured sample was not generated from this night and is not counted as a product.", StringComparison.Ordinal);
            Assert.DoesNotContain("not yet available", cut.Markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Not yet generated", details.TextContent, StringComparison.Ordinal);
            StringAssert.Contains(cut.Find(".large-viewer figcaption").TextContent, "Sample, not generated from this night", StringComparison.Ordinal);
            Assert.AreEqual("/api/v1/operations/time-lapse-sample", cut.Find("video source").GetAttribute("src"));
        });
    }

    private sealed class FixtureDayService : ICameraAgentObservingDayUiService
    {
        public ValueTask<OperatorUiResult<CameraAgentObservingDayView>> GetAsync(DateOnly observingDate, CancellationToken cancellationToken)
            => ValueTask.FromResult(OperatorUiResult<CameraAgentObservingDayView>.Success(new(
                new CameraAgentGalleryCalendarDay(NightlyProductFixture.Calendar.Resolve(observingDate), 0, 0, null, null),
                [], TimeSpan.Zero, null, [], false, [], null, null, null)));
    }
}
