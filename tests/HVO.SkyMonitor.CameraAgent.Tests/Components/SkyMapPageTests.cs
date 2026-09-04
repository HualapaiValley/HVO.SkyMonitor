using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class SkyMapPageTests
{
    private static readonly DateTimeOffset Instant = new(2026, 3, 1, 4, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Render_ShowsCatalogIdentityReadOnlyCoordinatesGeometryAndTheBoundedScene()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(new SkyMapUiService(SkyMapTestData.Result(Instant)));

        var cut = context.Render<SkyMapPage>();

        cut.WaitForElement("#sky-observer");
        Assert.AreEqual("Sky map & catalog", cut.Find("h1").TextContent.Trim());
        Assert.AreEqual("Observer location", cut.Find("#sky-observer").TextContent.Trim());
        Assert.AreEqual("Catalog identity", cut.Find("#sky-catalog").TextContent.Trim());
        Assert.Contains("cannot be edited here", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("America/Phoenix", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("hvo-hyg-v3", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Sirius", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Lyr", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("2 / 200", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("carries scene provenance", cut.Markup, StringComparison.Ordinal);
        // Nothing on this page reaches for a map tile, a script, or any remote asset.
        Assert.IsEmpty(cut.FindAll("img"));
        Assert.IsEmpty(cut.FindAll("iframe"));
        Assert.IsEmpty(cut.FindAll("script"));
        Assert.HasCount(1, cut.FindAll("svg.sky-dial"));
        Assert.HasCount(2, cut.FindAll("svg.sky-dial circle.sky-dial__object"));
    }

    [TestMethod]
    public void Render_WhenUnauthorized_NavigatesToAccessDenied()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(new SkyMapUiService(null));
        var navigation = context.Services.GetRequiredService<NavigationManager>();

        _ = context.Render<SkyMapPage>();

        Assert.IsTrue(navigation.Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Render_WhenTheRequestedInstantIsRejected_ShowsTheBoundedWindowNotice()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(new SkyMapUiService(
            null,
            OperatorUiResult<CameraAgentSkyMapProjectionResult>.Failure(
                OperatorUiResultKind.Invalid, CameraAgentSkyMapInstantBounds.RejectionMessage)));

        var cut = context.Render<SkyMapPage>();

        Assert.Contains("Instant outside the accepted window", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(CameraAgentSkyMapInstantBounds.RejectionMessage, cut.Markup, StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll("#sky-observer"));
    }

    [TestMethod]
    public void Render_WhenTheProjectionIsUnavailable_ShowsTheErrorStateWithRetry()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(new SkyMapUiService(
            null,
            OperatorUiResult<CameraAgentSkyMapProjectionResult>.Failure(
                OperatorUiResultKind.Unavailable, "The sky map projection is unavailable.")));

        var cut = context.Render<SkyMapPage>();

        Assert.Contains("Sky map unavailable", cut.Markup, StringComparison.Ordinal);
        Assert.IsNotNull(cut.FindAll("button").SingleOrDefault(static button =>
            button.TextContent.Contains("Try again", StringComparison.Ordinal)));
    }

    internal sealed class SkyMapUiService(
        CameraAgentSkyMapProjectionResult? state,
        OperatorUiResult<CameraAgentSkyMapProjectionResult>? failure = null) : ICameraAgentSkyMapUiService
    {
        public ValueTask<OperatorUiResult<CameraAgentSkyMapProjectionResult>> GetSkyMapAsync(
            DateTimeOffset? atUtc,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(state is null
                ? failure ?? OperatorUiResult<CameraAgentSkyMapProjectionResult>.Failure(
                    OperatorUiResultKind.Unauthorized, "Authorization is required.")
                : OperatorUiResult<CameraAgentSkyMapProjectionResult>.Success(state));
    }
}
