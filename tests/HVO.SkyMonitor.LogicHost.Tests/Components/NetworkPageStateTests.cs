using Bunit;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Components.Pages;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.Tests.LogicHost.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class NetworkPageStateTests
{
    [TestMethod]
    public void PublicDirectory_RendersDelayedLoadingThenEmptyState()
    {
        using var context = CreateContext(out var service);
        var completion = new TaskCompletionSource<PublicObservatoryPage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.ObservatoryPage = _ => completion.Task;

        var cut = context.Render<PublicObservatories>();

        cut.Markup.Should().Contain("Loading the observatory directory");
        completion.SetResult(new([], null));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No observatories are publicly listed"));
    }

    [TestMethod]
    public void PublicDirectory_RendersFailedStateWithoutProtectedDiagnostics()
    {
        using var context = CreateContext(out var service);
        service.ObservatoryPage = _ => Task.FromException<PublicObservatoryPage>(
            new InvalidOperationException("s3://private-object /tmp/private-stack"));

        var cut = context.Render<PublicObservatories>();

        cut.WaitForAssertion(() => cut.Markup.Should()
            .Contain("temporarily unavailable")
            .And.NotContain("s3://")
            .And.NotContain("/tmp/"));
    }

    [TestMethod]
    public void PublicDirectory_RendersMaximumPageAndContinuation()
    {
        using var context = CreateContext(out var service);
        service.ObservatoryPage = _ => Task.FromResult(new PublicObservatoryPage(
            Enumerable.Range(1, 50).Select(index => new PublicObservatorySummary(
                $"station-{index}",
                $"Station {index}",
                "Released profile",
                new PublicLocationProjection(
                    ObservatoryLocationDisclosureLevel.Hidden,
                    null,
                    null,
                    null,
                    null,
                    null),
                0,
                false)).ToArray(),
            "next"));

        var cut = context.Render<PublicObservatories>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".directory-card").Should().HaveCount(50);
            cut.FindAll("button").Should().Contain(button => button.TextContent.Contains("Load more", StringComparison.Ordinal));
        });
    }

    private static BunitContext CreateContext(out StubPublicNetworkReadService service)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        service = new StubPublicNetworkReadService();
        context.Services.AddSingleton<IPublicNetworkReadService>(service);
        return context;
    }

    private sealed class StubPublicNetworkReadService : IPublicNetworkReadService
    {
        internal Func<string?, Task<PublicObservatoryPage>> ObservatoryPage { get; set; } =
            _ => Task.FromResult<PublicObservatoryPage>(new([], null));

        public Task<PublicObservatoryPage> ListObservatoriesAsync(
            int take,
            string? cursor,
            CancellationToken cancellationToken = default) => ObservatoryPage(cursor);

        public Task<HVO.SkyMonitor.LogicHost.Services.PublicObservatoryDetail?> GetObservatoryAsync(
            string slug,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<HVO.SkyMonitor.LogicHost.Services.PublicObservatoryDetail?>(null);

        public Task<PublicLogicalCameraPage> ListObservatoryCamerasAsync(
            string slug,
            int take,
            string? cursor,
            CancellationToken cancellationToken = default) => Task.FromResult(new PublicLogicalCameraPage([], null));

        public Task<PublicImagePage> ListObservatoryProductsAsync(
            string slug,
            int take,
            string? cursor,
            CancellationToken cancellationToken = default) => Task.FromResult(new PublicImagePage([], null));

        public Task<PublicEventPage> ListEventsAsync(
            int take,
            string? cursor,
            CancellationToken cancellationToken = default) => Task.FromResult(new PublicEventPage([], null));

        public Task<PublicHomeSummary> GetHomeAsync(
            int takePerSection,
            CancellationToken cancellationToken = default) => Task.FromResult(new PublicHomeSummary([], [], []));

        public Task<IReadOnlyList<PublicImageSummary>> ListImagesAsync(
            int take,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PublicImageSummary>>([]);
    }
}
