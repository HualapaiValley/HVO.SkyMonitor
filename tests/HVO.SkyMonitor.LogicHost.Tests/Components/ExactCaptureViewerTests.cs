using System.Data.Common;
using System.Security.Claims;
using System.Text;
using Bunit;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Components.Shared;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using BrowserErrorEventArgs = Microsoft.AspNetCore.Components.Web.ErrorEventArgs;

namespace HVO.SkyMonitor.Tests.LogicHost.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class ExactCaptureViewerTests
{
    private static readonly Guid CaptureA = Guid.Parse("6d1c2a4e-3b7f-4e0a-9c21-5a8b7e2f0001");
    private static readonly Guid CaptureB = Guid.Parse("6d1c2a4e-3b7f-4e0a-9c21-5a8b7e2f0002");

    [TestMethod]
    public void PlainPreviewWithoutAnnotation_RendersTheExactBaseWithNoLayerControls()
    {
        var presentations = new RecordingPresentations(id => Found(Presentation(id, layers: false)));
        using var context = CreateContext(presentations, out _);

        var host = RenderViewer(context, CaptureA, Detail(CaptureA));

        host.Find(".layered-canvas img").GetAttribute("src").Should().Be(BasePath(CaptureA));
        host.Find(".layered-overlay").InnerHtml.Should().Be(Svg(CaptureA, layers: false));
        host.FindAll(".layer-controls input[type=checkbox]").Should().BeEmpty();
        host.Find("#layered-presentation-summary").TextContent.Should().Contain("0 selectable layers");
        host.Find("h1").TextContent.Should().Be($"Capture {CapturedAt(CaptureA):u}");
        host.Markup.Should().Contain("Capture provenance");
        presentations.Requests.Should().Equal(CaptureA);
    }

    [TestMethod]
    public void LayerControls_ToggleOnlyInTheBrowserAndChangeNoBytesOrPublication()
    {
        var presentations = new RecordingPresentations(id => Found(Presentation(id)));
        using var context = CreateContext(presentations, out var materializer);
        var host = RenderViewer(context, CaptureA, Detail(CaptureA));
        var overlay = host.Find(".layered-overlay").InnerHtml;

        var toggles = host.FindAll(".layer-controls input[type=checkbox]");
        toggles.Select(input => input.GetAttribute("data-layer-target"))
            .Should().Equal("layer-a-1", "layer-a-2");
        toggles.Select(input => input.GetAttribute("data-layer-identity"))
            .Should().Equal(LayerIdentity(CaptureA, 1), LayerIdentity(CaptureA, 2));
        toggles.Select(input => input.HasAttribute("checked")).Should().Equal(true, false);
        foreach (var toggle in toggles)
        {
            var change = () => toggle.Change(!toggle.HasAttribute("checked"));
            change.Should().Throw<MissingEventHandlerException>("layer visibility is a browser-only display choice");
        }

        overlay.Should().Be(Svg(CaptureA));
        host.Find(".layered-canvas img").GetAttribute("src").Should().Be(BasePath(CaptureA));
        context.JSInterop.VerifyInvoke("bindLayerToggles", 1);
        materializer.Calls.Should().Be(0);
        presentations.Requests.Should().Equal(CaptureA);
    }

    [TestMethod]
    public void EveryUnavailablePresentationState_ShowsTheExactCaptureMetadataOnly()
    {
        foreach (var status in Enum.GetValues<CentralLayeredPresentationStatus>()
                     .Where(status => status != CentralLayeredPresentationStatus.Found))
        {
            var presentations = new RecordingPresentations(_ => new CentralLayeredPresentationResult(status));
            using var context = CreateContext(presentations, out _);

            var host = RenderViewer(context, CaptureA, Detail(CaptureA));

            AssertMetadataOnly(host, CaptureA, because: status.ToString());
            host.FindAll("[role=status]").Should().BeEmpty(status.ToString());
        }
    }

    [TestMethod]
    public void PresentationReadFailure_ShowsTemporaryUnavailabilityWithTheExactCaptureMetadata()
    {
        foreach (var failure in new Exception[] { new InvalidOperationException("read"), new TestDbException("read") })
        {
            var presentations = new RecordingPresentations(_ => throw failure);
            using var context = CreateContext(presentations, out _);

            var host = RenderViewer(context, CaptureA, Detail(CaptureA));

            AssertMetadataOnly(host, CaptureA, because: failure.GetType().Name);
            host.Find(".alert[role=status]").TextContent.Should().Be("Capture evidence is temporarily unavailable.");
        }
    }

    [TestMethod]
    public void HostStatusMessage_IsShownAheadOfThePresentation()
    {
        var presentations = new RecordingPresentations(id => Found(Presentation(id)));
        using var context = CreateContext(presentations, out _);

        var host = RenderViewer(context, CaptureA, Detail(CaptureA), statusMessage: "The image is now publicly released.");

        host.Find(".alert[role=status]").TextContent.Should().Be("The image is now publicly released.");
        host.Find(".layered-canvas img").GetAttribute("src").Should().Be(BasePath(CaptureA));
    }

    [TestMethod]
    public async Task BaseImageError_ClearsTheBaseAndLayersAndRetryRequestsTheSameExactBase()
    {
        var presentations = new RecordingPresentations(id => Found(Presentation(id)));
        using var context = CreateContext(presentations, out _);
        var host = RenderViewer(context, CaptureA, Detail(CaptureA));

        await host.Find(".layered-canvas img").TriggerEventAsync("onerror", new BrowserErrorEventArgs());

        host.FindAll("img").Should().BeEmpty();
        host.FindAll("svg").Should().BeEmpty();
        host.Find(".layered-unavailable[role=status]").TextContent.Should().Contain("could not be displayed");
        host.Find("h1").TextContent.Should().Be($"Capture {CapturedAt(CaptureA):u}");
        host.FindAll(".layer-controls input[type=checkbox]").Should().OnlyContain(input => input.HasAttribute("disabled"));

        await host.Find(".layered-unavailable button").ClickAsync(new());

        host.Find(".layered-canvas img").GetAttribute("src").Should().Be(BasePath(CaptureA));
        host.Find(".layered-overlay").InnerHtml.Should().Be(Svg(CaptureA));
        presentations.Requests.Should().Equal([CaptureA], "a retry reloads the same exact base without a new presentation read");
    }

    [TestMethod]
    public void UnauthenticatedViewer_NeverRequestsTheLayeredPresentation()
    {
        var presentations = new RecordingPresentations(id => Found(Presentation(id)));
        using var context = CreateContext(presentations, out _, new ClaimsPrincipal(new ClaimsIdentity()));

        var host = RenderViewer(context, CaptureA, Detail(CaptureA));

        AssertMetadataOnly(host, CaptureA, because: "an anonymous principal has no presentation access");
        presentations.Requests.Should().BeEmpty();
    }

    [TestMethod]
    public void PresentationForAnotherCapture_IsNeverBorrowed()
    {
        var presentations = new RecordingPresentations(_ => Found(Presentation(CaptureB)));
        using var context = CreateContext(presentations, out _);

        var host = RenderViewer(context, CaptureA, Detail(CaptureA));

        AssertMetadataOnly(host, CaptureA, because: "the service answered for a different capture");
        host.Markup.Should().NotContain(BasePath(CaptureB)).And.NotContain(PresentationIdentity(CaptureB));
    }

    [TestMethod]
    public void DetailForAnotherCapture_RendersNothingAndRequestsNothing()
    {
        var presentations = new RecordingPresentations(id => Found(Presentation(id)));
        using var context = CreateContext(presentations, out _);

        var host = RenderViewer(context, CaptureA, Detail(CaptureB));

        host.Markup.Trim().Should().BeEmpty();
        presentations.Requests.Should().BeEmpty();
        host.FindComponent<ExactCaptureViewer>().Instance.DisplayedCaptureId.Should().BeNull();
    }

    [TestMethod]
    public void NewCaptureIdentity_DropsThePreviousCapturesImageBeforeItsOwnPresentationResolves()
    {
        var pending = new TaskCompletionSource<CentralLayeredPresentationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var presentations = new RecordingPresentations(
            id => Found(Presentation(id)),
            id => id == CaptureB ? pending.Task : null);
        using var context = CreateContext(presentations, out _);
        var host = RenderViewer(context, CaptureA, Detail(CaptureA));
        host.Find(".layered-canvas img").GetAttribute("src").Should().Be(BasePath(CaptureA));

        Rerender(host, CaptureB, Detail(CaptureB));

        host.Find("h1").TextContent.Should().Be($"Capture {CapturedAt(CaptureB):u}");
        host.FindAll("img").Should().BeEmpty("capture B's presentation has not resolved");
        host.Markup.Should().NotContain(BasePath(CaptureA)).And.NotContain(PresentationIdentity(CaptureA))
            .And.NotContain(ManifestIdentity(CaptureA));

        var viewer = host.FindComponent<ExactCaptureViewer>();
        var renders = viewer.RenderCount;
        pending.SetResult(new CentralLayeredPresentationResult(CentralLayeredPresentationStatus.Unavailable));

        viewer.WaitForState(() => viewer.RenderCount > renders);
        presentations.Requests.Should().Equal(CaptureA, CaptureB);
        AssertMetadataOnly(host, CaptureB, because: "capture B has no presentation of its own");
        host.Markup.Should().NotContain(BasePath(CaptureA)).And.NotContain(PresentationIdentity(CaptureA));
    }

    [TestMethod]
    public void LatePresentationForThePreviousCapture_IsDiscarded()
    {
        var pending = new TaskCompletionSource<CentralLayeredPresentationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var presentations = new RecordingPresentations(
            id => Found(Presentation(id)),
            id => id == CaptureA ? pending.Task : null);
        using var context = CreateContext(presentations, out _);
        var host = RenderViewer(context, CaptureA, Detail(CaptureA));

        Rerender(host, CaptureB, Detail(CaptureB));
        host.WaitForAssertion(() => host.Find(".layered-canvas img").GetAttribute("src").Should().Be(BasePath(CaptureB)));
        var viewer = host.FindComponent<ExactCaptureViewer>();
        var renders = viewer.RenderCount;
        pending.SetResult(Found(Presentation(CaptureA)));

        viewer.WaitForState(() => viewer.RenderCount > renders);
        presentations.Requests.Should().Equal(CaptureA, CaptureB);
        host.Find(".layered-canvas img").GetAttribute("src").Should().Be(BasePath(CaptureB));
        host.Markup.Should().NotContain(PresentationIdentity(CaptureA)).And.NotContain(BasePath(CaptureA));
    }

    [TestMethod]
    public void EachExactCapture_IsRequestedWithTheCurrentPrincipal()
    {
        var presentations = new RecordingPresentations(id => Found(Presentation(id)));
        var principal = Owner();
        using var context = CreateContext(presentations, out _, principal);
        var host = RenderViewer(context, CaptureA, Detail(CaptureA));

        Rerender(host, CaptureB, Detail(CaptureB));
        host.WaitForAssertion(() => host.Find(".layered-canvas img").GetAttribute("src").Should().Be(BasePath(CaptureB)));

        presentations.Requests.Should().Equal(CaptureA, CaptureB);
        presentations.Principals.Should().AllSatisfy(requested => requested.Should().BeSameAs(principal));
    }

    [TestMethod]
    public async Task SameCaptureDetailRefresh_KeepsPresentationAndImageStateWithoutRereading()
    {
        var presentations = new RecordingPresentations(id => Found(Presentation(id)));
        using var context = CreateContext(presentations, out _);
        var host = RenderViewer(context, CaptureA, Detail(CaptureA));
        var viewer = host.FindComponent<ExactCaptureViewer>();
        await host.Find(".layered-canvas img").TriggerEventAsync("onerror", new BrowserErrorEventArgs());

        Rerender(host, CaptureA, Detail(CaptureA) with { ArtifactsNextCursor = "next" });

        presentations.Requests.Should().Equal(CaptureA);
        viewer.Instance.IsBaseImageUnavailable.Should().BeTrue("a detail refresh does not reload the base image");
        host.FindAll(".layered-unavailable").Should().ContainSingle();
        host.Markup.Should().Contain(PresentationIdentity(CaptureA));
    }

    [TestMethod]
    public async Task SavedStack_ReportsTheExactCaptureToTheHost()
    {
        var presentations = new RecordingPresentations(id => Found(Presentation(id)));
        using var context = CreateContext(presentations, out var materializer);
        materializer.Result = new CentralPresentationMaterializationResult(CentralPresentationMaterializationStatus.Saved,
            new CentralPresentationMaterializationReceipt(CaptureA, Guid.NewGuid(), new string('6', 64), new string('7', 64),
                10, "/content", Replayed: false));
        context.JSInterop.SetupModule(ExactCaptureViewer.ModulePath)
            .Setup<string[]>("selectedLayerIdentities", _ => true).SetResult([LayerIdentity(CaptureA, 1)]);
        var saved = new List<Guid>();
        var host = RenderViewer(context, CaptureA, Detail(CaptureA), stackSaved: saved.Add);

        await host.FindAll("button").Single(button => button.TextContent.Contains("Save selected stack", StringComparison.Ordinal))
            .ClickAsync(new());

        saved.Should().Equal(CaptureA);
        materializer.Requests.Should().Equal((CaptureA, ManifestIdentity(CaptureA), LayerIdentity(CaptureA, 1)));
        host.Markup.Should().Contain("The selected stack was saved as an immutable central artifact.");
        presentations.Requests.Should().Equal(CaptureA);
    }

    [TestMethod]
    public void ViewerRole_HasNoSaveControl()
    {
        var presentations = new RecordingPresentations(id => Found(Presentation(id)));
        using var context = CreateContext(presentations, out _);

        var host = RenderViewer(context, CaptureA, Detail(CaptureA) with { EffectiveRole = ObservatoryMembershipRole.Viewer });

        host.FindAll(".layer-controls input[type=checkbox]").Should().HaveCount(2);
        host.FindAll("button").Should().NotContain(button => button.TextContent.Contains("Save selected stack", StringComparison.Ordinal));
    }

    private static void AssertMetadataOnly(IRenderedComponent<CascadingValue<OperationsCaptureDetail>> host, Guid captureId, string because)
    {
        host.FindAll("img").Should().BeEmpty(because);
        host.FindAll("svg").Should().BeEmpty(because);
        host.FindAll(".layered-presentation").Should().BeEmpty(because);
        host.FindAll(".layer-controls").Should().BeEmpty(because);
        host.Find("h1").TextContent.Should().Be($"Capture {CapturedAt(captureId):u}", because);
        host.Markup.Should().Contain("Capture provenance", because);
    }

    private static IRenderedComponent<CascadingValue<OperationsCaptureDetail>> RenderViewer(
        BunitContext context,
        Guid captureId,
        OperationsCaptureDetail detail,
        string? statusMessage = null,
        Action<Guid>? stackSaved = null)
        => context.Render<CascadingValue<OperationsCaptureDetail>>(parameters => parameters
            .Add(cascade => cascade.Value, detail)
            .AddChildContent<ExactCaptureViewer>(viewer => viewer
                .Add(component => component.CaptureId, captureId)
                .Add(component => component.StatusMessage, statusMessage)
                .Add(component => component.StackSaved, stackSaved ?? (_ => { }))));

    private static void Rerender(IRenderedComponent<CascadingValue<OperationsCaptureDetail>> host, Guid captureId, OperationsCaptureDetail detail)
        => host.Render(parameters => parameters
            .Add(cascade => cascade.Value, detail)
            .AddChildContent<ExactCaptureViewer>(viewer => viewer.Add(component => component.CaptureId, captureId)));

    private static BunitContext CreateContext(
        RecordingPresentations presentations,
        out CountingMaterializer materializer,
        ClaimsPrincipal? principal = null)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule(ExactCaptureViewer.ModulePath);
        context.Services.AddSingleton<AuthenticationStateProvider>(new FixedAuthentication(principal ?? Owner()));
        context.Services.AddCascadingAuthenticationState();
        context.Services.AddSingleton<ICentralLayeredPresentationService>(presentations);
        materializer = new CountingMaterializer();
        context.Services.AddSingleton<ICentralPresentationMaterializer>(materializer);
        return context;
    }

    private static ClaimsPrincipal Owner() => new(new ClaimsIdentity(
        [new(ClaimTypes.NameIdentifier, "owner"), new("account_type", "User")], "Identity.Application"));

    private static CentralLayeredPresentationResult Found(CentralLayeredPresentation presentation)
        => new(CentralLayeredPresentationStatus.Found, presentation);

    private static string BasePath(Guid captureId) => $"/api/v1.0/captures/{captureId:D}/presentation/base";

    private static string Prefix(Guid captureId) => captureId == CaptureA ? "a" : "b";

    private static DateTimeOffset CapturedAt(Guid captureId)
        => captureId == CaptureA
            ? new DateTimeOffset(2026, 10, 16, 4, 0, 0, TimeSpan.Zero)
            : new DateTimeOffset(2026, 10, 16, 4, 0, 30, TimeSpan.Zero);

    private static string PresentationIdentity(Guid captureId) => Prefix(captureId) + new string('1', 63);

    private static string ManifestIdentity(Guid captureId) => Prefix(captureId) + new string('2', 63);

    private static string LayerIdentity(Guid captureId, int layer) => Prefix(captureId) + new string((char)('2' + layer), 63);

    private static string Svg(Guid captureId, bool layers = true)
        => layers
            ? $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 4 3\"><g id=\"layer-{Prefix(captureId)}-1\"></g><g id=\"layer-{Prefix(captureId)}-2\"></g></svg>"
            : "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 4 3\"></svg>";

    private static OperationsCaptureDetail Detail(Guid captureId) => new(
        new OperationsCaptureSummary(captureId, Guid.Empty, null, null, null, "Station", "Camera",
            CapturedAt(captureId), CapturedAt(captureId), "rig", 1, null, 7, 0, false, true, false),
        [],
        null,
        ObservatoryMembershipRole.Owner,
        new OperationsCaptureProvenanceSummary("agent", captureId, default, null, null, null, null, null, null, null,
            null, null, null, null, []));

    private static CentralLayeredPresentation Presentation(Guid captureId, bool layers = true)
    {
        var prefix = Prefix(captureId);
        GroupedSvgPresentationLayer[] presentationLayers = layers
            ?
            [
                new(LayerIdentity(captureId, 1), "Constellations", $"layer-{prefix}-1", 10, true, 1_000_000, "r1", "s1"),
                new(LayerIdentity(captureId, 2), "Grid", $"layer-{prefix}-2", 20, false, 1_000_000, "r1", "s1")
            ]
            : [];
        return new CentralLayeredPresentation(captureId, Guid.Empty, Guid.Empty, "image/jpeg", ManifestIdentity(captureId),
            PresentationIdentity(captureId), new string('5', 64), 4, 3, presentationLayers,
            Encoding.UTF8.GetBytes(Svg(captureId, layers)));
    }

    private sealed class FixedAuthentication(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class RecordingPresentations(
        Func<Guid, CentralLayeredPresentationResult> resolve,
        Func<Guid, Task<CentralLayeredPresentationResult>?>? defer = null) : ICentralLayeredPresentationService
    {
        internal List<Guid> Requests { get; } = [];
        internal List<ClaimsPrincipal> Principals { get; } = [];

        public Task<CentralLayeredPresentationResult> GetAsync(
            Guid captureId, ClaimsPrincipal principal, CancellationToken cancellationToken = default)
        {
            Requests.Add(captureId);
            Principals.Add(principal);
            return defer?.Invoke(captureId) ?? Task.FromResult(resolve(captureId));
        }
    }

    private sealed class CountingMaterializer : ICentralPresentationMaterializer
    {
        internal int Calls => Requests.Count;
        internal List<(Guid CaptureId, string Manifest, string Layers)> Requests { get; } = [];
        internal CentralPresentationMaterializationResult Result { get; set; } =
            new(CentralPresentationMaterializationStatus.Invalid);

        public Task<CentralPresentationMaterializationResult> SaveAsync(Guid captureId, string manifestIdentitySha256,
            IReadOnlyList<string> enabledLayerIdentitySha256, ClaimsPrincipal principal, CancellationToken cancellationToken = default)
        {
            Requests.Add((captureId, manifestIdentitySha256, string.Join(",", enabledLayerIdentitySha256)));
            return Task.FromResult(Result);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1032:Implement standard exception constructors",
        Justification = "This private test double only represents a database provider failure.")]
    private sealed class TestDbException(string message) : DbException(message);
}
