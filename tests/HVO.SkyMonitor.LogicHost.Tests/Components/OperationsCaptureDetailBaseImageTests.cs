using System.Reflection;
using System.Security.Claims;
using System.Text;
using Bunit;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using BrowserErrorEventArgs = Microsoft.AspNetCore.Components.Web.ErrorEventArgs;
using CaptureDetailPage = HVO.SkyMonitor.LogicHost.Components.Pages.Operations.OperationsCaptureDetail;

namespace HVO.SkyMonitor.Tests.LogicHost.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class OperationsCaptureDetailBaseImageTests
{
    private const string ModulePath = "./Components/Pages/Operations/OperationsCaptureDetail.razor.js";
    private static readonly Guid CaptureA = Guid.Parse("0f8e5b52-1a54-4f43-9a65-6f3d1f0a0001");
    private static readonly Guid CaptureB = Guid.Parse("0f8e5b52-1a54-4f43-9a65-6f3d1f0a0002");

    [TestMethod]
    public void LoadedBase_RendersTheExactBaseOverlayAndEnabledControls()
    {
        using var context = CreateContext(out _);
        var cut = RenderCapture(context, CaptureA);

        cut.Find(".layered-canvas img").GetAttribute("src").Should().Be(BasePath(CaptureA));
        cut.Find(".layered-overlay svg").Should().NotBeNull();
        cut.FindAll(".layer-controls input[type=checkbox]").Should().OnlyContain(input => !input.HasAttribute("disabled"));
        SaveButton(cut).HasAttribute("disabled").Should().BeFalse();
        cut.FindAll(".layered-unavailable").Should().BeEmpty();
        cut.Find("#layered-presentation-summary").TextContent.Should().Contain("2 selectable layers");
    }

    [TestMethod]
    public async Task BaseLoadError_ClearsBaseAndOverlayAndKeepsTheExactCaptureMetadata()
    {
        using var context = CreateContext(out _);
        var cut = RenderCapture(context, CaptureA);

        await cut.Find(".layered-canvas img").TriggerEventAsync("onerror", new BrowserErrorEventArgs());

        cut.FindAll("img").Should().BeEmpty();
        cut.FindAll(".layered-overlay").Should().BeEmpty();
        cut.FindAll("svg").Should().BeEmpty();
        var status = cut.Find(".layered-unavailable[role=status]");
        status.TextContent.Should().Contain("could not be displayed")
            .And.NotContainAny("503", "409", "404", "missing", "integrity", "storage");
        cut.Find("h1").TextContent.Should().Be($"Capture {CapturedAt(CaptureA):u}");
        cut.Markup.Should().Contain(PresentationIdentity(CaptureA)).And.Contain(ManifestIdentity(CaptureA))
            .And.Contain("Capture provenance").And.NotContain(PresentationIdentity(CaptureB));
        cut.FindAll(".layer-controls input[type=checkbox]").Should().HaveCount(2)
            .And.OnlyContain(input => input.HasAttribute("disabled"));
        SaveButton(cut).HasAttribute("disabled").Should().BeTrue();
        cut.Find("#layered-presentation-summary").TextContent.Should().Contain("base image is unavailable");
    }

    [TestMethod]
    public async Task UnavailableBase_DoesNotMaterializeTheHiddenStack()
    {
        using var context = CreateContext(out var materializer);
        var cut = RenderCapture(context, CaptureA);
        await cut.Find(".layered-canvas img").TriggerEventAsync("onerror", new BrowserErrorEventArgs());

        await cut.InvokeAsync(cut.Instance.SaveSelectedStackAsync);

        materializer.Calls.Should().Be(0);
        cut.Instance.MaterializationMessage.Should().BeNull();
    }

    [TestMethod]
    public async Task BaseFailureDuringSelectionRead_DoesNotMaterializeTheHiddenStack()
    {
        using var context = CreateContext(out var materializer);
        var selection = context.JSInterop.SetupModule(ModulePath).Setup<string[]>("selectedLayerIdentities", _ => true);
        var cut = RenderCapture(context, CaptureA);

        await Task.WhenAll(cut.InvokeAsync(cut.Instance.SaveSelectedStackAsync), FailBaseThenReturnSelectionAsync());

        async Task FailBaseThenReturnSelectionAsync()
        {
            try
            {
                await cut.Find(".layered-canvas img").TriggerEventAsync("onerror", new BrowserErrorEventArgs());
            }
            finally
            {
                selection.SetResult([new string('3', 64)]);
            }
        }

        materializer.Calls.Should().Be(0);
        cut.Instance.MaterializationMessage.Should().BeNull();
        cut.Instance.IsSavingPresentation.Should().BeFalse();
    }

    [TestMethod]
    public async Task ExplicitRetry_RendersANewExactBaseElementAndRebindsLayers()
    {
        using var context = CreateContext(out _);
        var cut = RenderCapture(context, CaptureA);
        var failedKey = cut.Instance.BaseImageKey;
        await cut.Find(".layered-canvas img").TriggerEventAsync("onerror", new BrowserErrorEventArgs());

        await cut.Find(".layered-unavailable button").ClickAsync(new());

        cut.Instance.BaseImageKey.Should().NotBe(failedKey);
        cut.Find(".layered-canvas img").GetAttribute("src").Should().Be(BasePath(CaptureA));
        cut.Find(".layered-overlay svg").Should().NotBeNull();
        cut.FindAll(".layered-unavailable").Should().BeEmpty();
        cut.FindAll(".layer-controls input[type=checkbox]").Should().OnlyContain(input => !input.HasAttribute("disabled"));
        SaveButton(cut).HasAttribute("disabled").Should().BeFalse();
        context.JSInterop.VerifyInvoke("bindLayerToggles", 2);

        await cut.InvokeAsync(() => cut.Instance.OnBaseImageError(failedKey));
        cut.Instance.IsBaseImageUnavailable.Should().BeFalse("an error from the superseded attempt is stale");

        await cut.Find(".layered-canvas img").TriggerEventAsync("onerror", new BrowserErrorEventArgs());
        cut.FindAll(".layered-unavailable").Should().ContainSingle("the current attempt can still fail");
    }

    [TestMethod]
    public async Task NewCaptureIdentity_ResetsUnavailableStateAndIgnoresThePreviousCapturesError()
    {
        using var context = CreateContext(out _);
        var cut = RenderCapture(context, CaptureA);
        var captureAKey = cut.Instance.BaseImageKey;
        await cut.Find(".layered-canvas img").TriggerEventAsync("onerror", new BrowserErrorEventArgs());

        cut.Render(parameters => parameters.Add(page => page.CaptureId, CaptureB));
        cut.WaitForAssertion(() => cut.Find(".layered-canvas img").GetAttribute("src").Should().Be(BasePath(CaptureB)));

        await cut.InvokeAsync(() => cut.Instance.OnBaseImageError(captureAKey));

        cut.Instance.IsBaseImageUnavailable.Should().BeFalse("an error from the previous capture is stale");
        cut.FindAll(".layered-unavailable").Should().BeEmpty();
        cut.Find(".layered-overlay svg").Should().NotBeNull();
        cut.Find("h1").TextContent.Should().Be($"Capture {CapturedAt(CaptureB):u}");
        cut.Markup.Should().Contain(PresentationIdentity(CaptureB)).And.NotContain(PresentationIdentity(CaptureA))
            .And.NotContain(BasePath(CaptureA));
        cut.FindAll(".layer-controls input[type=checkbox]").Should().OnlyContain(input => !input.HasAttribute("disabled"));
    }

    private static IRenderedComponent<CaptureDetailPage> RenderCapture(BunitContext context, Guid captureId)
    {
        var cut = context.Render<CaptureDetailPage>(parameters => parameters.Add(page => page.CaptureId, captureId));
        cut.WaitForAssertion(() => cut.Find(".layered-canvas img"));
        return cut;
    }

    private static AngleSharp.Dom.IElement SaveButton(IRenderedComponent<CaptureDetailPage> cut)
        => cut.FindAll("button").Single(button => button.TextContent.Contains("Save selected stack", StringComparison.Ordinal));

    private static BunitContext CreateContext(out CountingMaterializer materializer)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule(ModulePath);
        context.Services.AddSingleton<AuthenticationStateProvider>(new FixedAuthentication());
        context.Services.AddCascadingAuthenticationState();
        context.Services.AddSingleton(CaptureReadService.Create());
        context.Services.AddSingleton<ICentralLayeredPresentationService>(new FixedPresentations());
        materializer = new CountingMaterializer();
        context.Services.AddSingleton<ICentralPresentationMaterializer>(materializer);
        context.Services.AddSingleton<IPublicRecordPublicationService>(new UnusedPublication());
        return context;
    }

    private static string BasePath(Guid captureId) => $"/api/v1.0/captures/{captureId:D}/presentation/base";

    private static DateTimeOffset CapturedAt(Guid captureId)
        => captureId == CaptureA
            ? new DateTimeOffset(2026, 10, 15, 3, 0, 0, TimeSpan.Zero)
            : new DateTimeOffset(2026, 10, 15, 3, 0, 30, TimeSpan.Zero);

    private static string PresentationIdentity(Guid captureId) => (captureId == CaptureA ? "a" : "b") + new string('1', 63);

    private static string ManifestIdentity(Guid captureId) => (captureId == CaptureA ? "a" : "b") + new string('2', 63);

    private static OperationsCaptureDetail Detail(Guid captureId) => new(
        new OperationsCaptureSummary(captureId, Guid.Empty, null, null, null, "Station", "Camera",
            CapturedAt(captureId), CapturedAt(captureId), "rig", 1, null, 7, 0, false, true, false),
        [],
        null,
        ObservatoryMembershipRole.Owner,
        new OperationsCaptureProvenanceSummary("agent", captureId, default, null, null, null, null, null, null, null,
            null, null, null, null, []));

    private static CentralLayeredPresentation Presentation(Guid captureId)
    {
        var prefix = captureId == CaptureA ? "a" : "b";
        GroupedSvgPresentationLayer[] layers =
        [
            new(prefix + new string('3', 63), "Constellations", $"layer-{prefix}-1", 10, true, 1_000_000, "r1", "s1"),
            new(prefix + new string('4', 63), "Grid", $"layer-{prefix}-2", 20, false, 1_000_000, "r1", "s1")
        ];
        var svg = Encoding.UTF8.GetBytes(
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 4 3\"><g id=\"layer-{prefix}-1\"></g><g id=\"layer-{prefix}-2\"></g></svg>");
        return new CentralLayeredPresentation(captureId, Guid.Empty, Guid.Empty, "image/jpeg", ManifestIdentity(captureId),
            PresentationIdentity(captureId), new string('5', 64), 4, 3, layers, svg);
    }

    private sealed class FixedAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity(
                [new(ClaimTypes.NameIdentifier, "owner"), new("account_type", "User")], "Identity.Application"))));
    }

    private sealed class FixedPresentations : ICentralLayeredPresentationService
    {
        public Task<CentralLayeredPresentationResult> GetAsync(
            Guid captureId, ClaimsPrincipal principal, CancellationToken cancellationToken = default)
            => Task.FromResult(new CentralLayeredPresentationResult(CentralLayeredPresentationStatus.Found, Presentation(captureId)));
    }

    private sealed class CountingMaterializer : ICentralPresentationMaterializer
    {
        internal int Calls { get; private set; }

        public Task<CentralPresentationMaterializationResult> SaveAsync(Guid captureId, string manifestIdentitySha256,
            IReadOnlyList<string> enabledLayerIdentitySha256, ClaimsPrincipal principal, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new CentralPresentationMaterializationResult(CentralPresentationMaterializationStatus.Invalid));
        }
    }

    private sealed class UnusedPublication : IPublicRecordPublicationService
    {
        public Task<PublicRecordPublicationResult> DecideAsync(Guid authorityObservatoryId, string actorUserId,
            PublicRecordSubject subject, PublicationDecisionState state, string projectionSchemaVersion, string reasonCode,
            CancellationToken cancellationToken = default) => throw new AssertFailedException("Publication is not exercised.");
    }

    // Only capture detail reads are exercised; any other read-service member fails the test explicitly.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
        Justification = "DispatchProxy constructs this type through reflection.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1852:Seal internal types",
        Justification = "DispatchProxy dynamically derives the generated interface proxy from this base type.")]
    internal class CaptureReadService : DispatchProxy
    {
        internal static INetworkOperationsReadService Create() => Create<INetworkOperationsReadService, CaptureReadService>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(INetworkOperationsReadService.GetCaptureAsync) => Task.FromResult<OperationsCaptureDetail?>(Detail((Guid)args![1]!)),
            _ => throw new AssertFailedException($"{targetMethod?.Name} is not exercised by capture detail image tests.")
        };
    }
}
