using System.Security.Claims;
using System.Diagnostics.CodeAnalysis;
using Bunit;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Components.Shared;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using BrowserErrorEventArgs = Microsoft.AspNetCore.Components.Web.ErrorEventArgs;

namespace HVO.SkyMonitor.Tests.LogicHost.Components;

[SuppressMessage("Reliability", "CA2025:Do not pass disposables into unawaited tasks", Justification = "Tests intentionally hold refresh tasks to inspect pending UI; each task is completed and awaited before context disposal.")]
[TestClass]
[TestCategory("Unit")]
public sealed class CameraArchiveViewTests
{
    private static readonly Guid Observatory = Guid.NewGuid();
    private static readonly Guid Camera = Guid.NewGuid();
    private static readonly Guid Capture = Guid.NewGuid();
    private static readonly Guid Artifact = Guid.NewGuid();

    [TestMethod]
    public void HeadingTree_NestsTheExactCaptureUnderTheArchiveAndPreservesItsFocusTarget()
    {
        using var context = CreateContext(out _, out _);
        var cut = Render(context);

        cut.FindAll("h1,h2,h3,h4,h5,h6").Select(h => (h.LocalName, h.TextContent))
            .Should().Equal(("h2", "Camera archive"), ("h3", $"Capture {DateTimeOffset.UnixEpoch:u}"),
                ("h4", "Capture provenance"), ("h3", "Preview · default"));
        cut.Find("#archive-heading").GetAttribute("tabindex").Should().Be("-1");
        cut.Find("section.camera-archive").GetAttribute("aria-labelledby").Should().Be("archive-heading");
    }

    [TestMethod]
    public void Archive_UsesOneBoundedPageAndPreservesQueryAndCentralIdentityInLinks()
    {
        using var context = CreateContext(out var archive, out _);
        var cut = context.Render<CameraArchiveView>(p => p.Add(c => c.ObservatoryId, Observatory)
            .Add(c => c.CameraId, Camera).Add(c => c.Cursor, "opaque-page")
            .Add(c => c.Identity, "00042").Add(c => c.Product, "Preview").Add(c => c.From, "2026-10-01T00:00"));
        archive.ListCalls.Should().Be(1);
        archive.Filters.Single().Text.Should().Be("00042");
        archive.Filters.Single().Product.Should().Be(FrameArtifactRole.Preview);
        var link = cut.Find("a[data-capture]");
        link.Id.Should().Be($"archive-capture-{Capture:D}");
        link.GetAttribute("href").Should().Contain($"/archive/{Capture:D}?").And.Contain("cursor=opaque-page")
            .And.Contain("identity=00042").And.Contain("product=Preview").And.Contain($"focus={Capture:D}");
        cut.FindAll("img").Should().BeEmpty();
        archive.Cursors.Should().Equal(["opaque-page"]);
    }

    [TestMethod]
    public void ExactDetail_PlainPreviewAndReturnLinkKeepPageAndFocus()
    {
        using var context = CreateContext(out var archive, out _);
        var cut = context.Render<CameraArchiveView>(p => p.Add(c => c.ObservatoryId, Observatory).Add(c => c.CameraId, Camera)
            .Add(c => c.CaptureId, Capture).Add(c => c.Cursor, "page-two").Add(c => c.ObjectState, "Available"));
        cut.Find("img").GetAttribute("src").Should().Be("/selected-preview");
        cut.Find(".archive-back").GetAttribute("href").Should().Contain("cursor=page-two")
            .And.Contain("objectState=Available").And.Contain($"focus={Capture:D}");
        cut.Find(".archive-exact").TextContent.Should().Contain(Capture.ToString());
        archive.ListCalls.Should().Be(0);
    }

    [TestMethod]
    public async Task RefreshAndAuthenticationLoss_ClearImagesAndMetadataBeforeAwaiting()
    {
        using var context = CreateContext(out var archive, out var auth);
        var cut = Render(context);
        var pending = new TaskCompletionSource<OperationsCaptureDetail?>(TaskCreationOptions.RunContinuationsAsynchronously);
        archive.Read = () => pending.Task;
        var refresh = cut.InvokeAsync(cut.Instance.RefreshAsync);
        cut.WaitForAssertion(() => cut.FindAll("img").Should().BeEmpty());
        await cut.InvokeAsync(() => auth.Change(new ClaimsPrincipal(new ClaimsIdentity())));
        pending.SetResult(Detail());
        await refresh;
        cut.Markup.Should().Contain("access was removed").And.NotContain("Capture provenance");
        cut.FindAll("img").Should().BeEmpty();
    }

    [TestMethod]
    public async Task ReadFailure_ClearsRetainedImageAndPrivateDiagnostics()
    {
        using var context = CreateContext(out var archive, out _);
        var cut = Render(context);
        archive.Read = () => throw new InvalidOperationException("private failure");
        await cut.InvokeAsync(cut.Instance.RefreshAsync);
        cut.Markup.Should().Contain("temporarily unavailable").And.NotContain("private failure").And.NotContain("Capture provenance");
        cut.FindAll("img").Should().BeEmpty();
    }

    [TestMethod]
    public async Task ForeignCaptureOrScope_NeverDisplaysImageOrMetadata()
    {
        foreach (var foreign in new[] { "capture", "camera", "observatory" })
        {
            using var context = CreateContext(out var archive, out _);
            var data = Detail();
            archive.Detail = data with
            {
                Capture = data.Capture with
                {
                    CaptureId = foreign == "capture" ? Guid.NewGuid() : Capture,
                    LogicalCameraId = foreign == "camera" ? Guid.NewGuid() : Camera,
                    ObservatoryId = foreign == "observatory" ? Guid.NewGuid() : Observatory
                }
            };
            var cut = Render(context);
            await cut.InvokeAsync(() => Task.CompletedTask);
            cut.FindAll("img").Should().BeEmpty();
            cut.Markup.Should().NotContain("Capture provenance");
        }
    }

    [TestMethod]
    public void UnavailableStates_KeepExactMetadataWithoutImages()
    {
        foreach (var state in Enum.GetValues<CentralArtifactObjectState>().Where(s => s != CentralArtifactObjectState.Available))
        {
            using var context = CreateContext(out var archive, out _);
            var data = Detail();
            archive.Detail = data with { Artifacts = [data.Artifacts.Single() with { ObjectState = state, StateReasonCode = "retained-reason" }] };
            var cut = Render(context);
            cut.FindAll("img").Should().BeEmpty();
            cut.Markup.Should().Contain("Capture provenance").And.Contain("retained-reason").And.Contain("no other image is substituted");
        }
    }

    [TestMethod]
    public async Task ImageFailure_RetainsExactMetadataAndRetryNeverSelectsAnotherCapture()
    {
        using var context = CreateContext(out var archive, out _);
        var cut = Render(context);
        await cut.Find("img").TriggerEventAsync("onerror", new BrowserErrorEventArgs());
        cut.FindAll("img").Should().BeEmpty();
        cut.Markup.Should().Contain("Capture provenance");
        await cut.FindAll("button").Single(b => b.TextContent == "Retry image").ClickAsync(new());
        cut.Find("img").GetAttribute("src").Should().Be("/selected-preview");
        archive.ListCalls.Should().Be(0);
        archive.Cursors.Should().BeEmpty();
    }

    [TestMethod]
    public void InvalidFiltersAndCursorFailWithoutDrainingOrEchoingInput()
    {
        using var context = CreateContext(out var archive, out _);
        archive.List = _ => throw new ArgumentException("private cursor body");
        var cut = context.Render<CameraArchiveView>(p => p.Add(c => c.ObservatoryId, Observatory).Add(c => c.CameraId, Camera)
            .Add(c => c.Cursor, "secret-cursor"));
        cut.Markup.Should().Contain("no longer available").And.NotContain("secret-cursor").And.NotContain("private cursor body");
        archive.ListCalls.Should().Be(1);
        cut.FindAll("a[data-capture]").Should().BeEmpty();
    }

    [TestMethod]
    public async Task TraceTruncation_IsExplicitAndDenialRecheckClearsAllEvidence()
    {
        using var context = CreateContext(out var archive, out _);
        var operations = new TraceOperations();
        context.Services.AddSingleton<INetworkOperationsReadService>(operations);
        var cut = Render(context);
        await cut.FindAll("button").Single(b => b.TextContent == "Load bounded capture trace").ClickAsync(new());
        cut.Markup.Should().Contain("Trace truncated");
        operations.Calls.Should().Be(1);
        await cut.InvokeAsync(cut.Instance.RefreshAsync);
        operations.OnRead = () => archive.Detail = null;
        await cut.FindAll("button").Single(b => b.TextContent == "Load bounded capture trace").ClickAsync(new());
        cut.Markup.Should().Contain("access was removed").And.NotContain("Capture provenance").And.NotContain("Trace truncated");
        cut.FindAll("img").Should().BeEmpty();
    }

    [TestMethod]
    public void PartialReconstructionAndRoleOnlyMetadata_NeverDisplaysUnavailableBytes()
    {
        using var context = CreateContext(out var archive, out _);
        var data = Detail();
        archive.Detail = data with { Artifacts = [data.Artifacts.Single() with { ReconstructionState = CentralReconstructionState.PendingReference }] };
        var cut = Render(context);
        cut.FindAll("img").Should().BeEmpty();
        cut.Markup.Should().Contain("PendingReference").And.Contain("Capture provenance");
        archive.Detail = data with { Artifacts = [], ArtifactsNextCursor = "retained-page" };
        cut.Render();
        cut.FindAll("img").Should().BeEmpty();
        cut.Markup.Should().Contain("first 50 artifact records");
    }

    [TestMethod]
    public async Task RawAuthorizationFailure_ClearsRetainedContentAndExplainsRetry()
    {
        using var context = CreateContext(out var archive, out _, out var module);
        module.SetupVoid("authorizeRawDownload", _ => true).SetException(new Microsoft.JSInterop.JSException("denied"));
        var data = Detail();
        archive.Detail = data with { Artifacts = [data.Artifacts.Single(), data.Artifacts.Single() with { ArtifactId = Guid.NewGuid(), Role = FrameArtifactRole.Raw }] };
        var cut = Render(context);
        await cut.FindAll("button").Single(b => b.TextContent == "Authorize raw download").ClickAsync(new());
        cut.Markup.Should().Contain("authorization failed or expired").And.NotContain("Capture provenance");
        cut.FindAll("img").Should().BeEmpty();
    }

    [TestMethod]
    public async Task ScopeChange_RejectsPendingOldCaptureEvenWhenCancellationIsIgnored()
    {
        using var context = CreateContext(out var archive, out _);
        var pending = new TaskCompletionSource<OperationsCaptureDetail?>(TaskCreationOptions.RunContinuationsAsynchronously);
        archive.Read = () => pending.Task;
        var cut = Render(context);
        archive.Read = () => Task.FromResult<OperationsCaptureDetail?>(null);
        cut.Render(p => p.Add(c => c.CameraId, Guid.NewGuid()));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("access was removed"));
        pending.SetResult(Detail());
        await cut.InvokeAsync(() => Task.CompletedTask);
        cut.Markup.Should().NotContain("Capture provenance");
        cut.FindAll("img").Should().BeEmpty();
    }

    [TestMethod]
    public void ExactLayeredPresentation_TakesPrecedenceOverAnotherPlainPreviewVariant()
    {
        using var context = CreateContext(out _, out _);
        var presentations = (Presentations)context.Services.GetRequiredService<ICentralLayeredPresentationService>();
        presentations.Result = new(CentralLayeredPresentationStatus.Found,
            new(Capture, Guid.NewGuid(), Guid.NewGuid(), "image/png", "manifest", "presentation", "svg",
                1, 1, [], "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"u8.ToArray()));
        var cut = Render(context);
        cut.Find("img").GetAttribute("src").Should().Be($"/api/v1.0/captures/{Capture:D}/presentation/base");
        cut.FindAll("svg").Should().HaveCount(1);
    }

    private static IRenderedComponent<CameraArchiveView> Render(BunitContext context) => context.Render<CameraArchiveView>(p =>
        p.Add(c => c.ObservatoryId, Observatory).Add(c => c.CameraId, Camera).Add(c => c.CaptureId, Capture));

    private static BunitContext CreateContext(out Archive archive, out Authentication auth) => CreateContext(out archive, out auth, out _);

    private static BunitContext CreateContext(out Archive archive, out Authentication auth, out BunitJSModuleInterop module)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        module = context.JSInterop.SetupModule("./Components/Shared/CameraArchiveView.razor.js");
        context.JSInterop.SetupModule(ExactCaptureViewer.ModulePath);
        archive = new Archive();
        auth = new Authentication();
        context.Services.AddSingleton<AuthenticationStateProvider>(auth);
        context.Services.AddCascadingAuthenticationState();
        context.Services.AddLogging();
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<ILogicalCameraArchiveReadService>(archive);
        context.Services.AddSingleton<ICentralLayeredPresentationService>(new Presentations());
        context.Services.AddSingleton<ICentralPresentationMaterializer>(new Materializer());
        return context;
    }

    private static CameraCurrentSelection Selected() => new("found", new(Capture, Capture, Guid.NewGuid(),
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), 1), Artifact, "/selected-preview", "checksum", 1, 1, null);

    private static OperationsCaptureDetail Detail() => new(
        new(Capture, Observatory, Camera, Guid.NewGuid(), Guid.NewGuid(), "Station", "Camera", DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddHours(1), "rig", 1, null, 1, 1, false, true, false),
        [new(Artifact, Guid.NewGuid(), FrameArtifactRole.Preview, null, "v1", "image/png", 100, "checksum",
            CentralArtifactObjectState.Available, CentralReconstructionState.Complete, null, DateTimeOffset.UnixEpoch,
            "/selected-preview", "agent", 0, null, null, null, "v1", false, false)], null, ObservatoryMembershipRole.Owner,
        new("agent", Capture, default, null, null, null, null, null, null, null, null, null, null, null, []));

    private sealed class Archive : ILogicalCameraArchiveReadService
    {
        public Task<CameraCurrentSelection?> RevalidateCurrentAsync(ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, CameraCurrentSelection selection, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Archive navigation does not revalidate Current Sky.");
        internal Func<string?, Task<CameraCurrentSelection?>> Select { get; set; } = _ => Task.FromResult<CameraCurrentSelection?>(Selected());
        internal OperationsCaptureDetail? Detail { get; set; } = CameraArchiveViewTests.Detail();
        internal List<string?> Cursors { get; } = [];
        internal Func<string?, Task<CameraArchivePage>> List { get; set; } = _ => Task.FromResult(new CameraArchivePage([Selected().Capture!], "protected-next"));
        internal Func<Task<OperationsCaptureDetail?>>? Read { get; set; }
        internal List<CameraArchiveFilter> Filters { get; } = [];
        internal int ListCalls { get; private set; }
        public Task<CameraCurrentSelection?> SelectCurrentAsync(ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, string? cursor, CancellationToken cancellationToken = default)
        { Cursors.Add(cursor); return Select(cursor); }
        public Task<CameraArchivePage> ListCapturesAsync(ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, CameraArchiveFilter filter, int take, string? cursor, CancellationToken cancellationToken = default)
        { take.Should().Be(25); ListCalls++; Filters.Add(filter); Cursors.Add(cursor); return List(cursor); }
        public Task<OperationsCaptureDetail?> GetCaptureAsync(ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, Guid captureId, CancellationToken cancellationToken = default) => Read?.Invoke() ?? Task.FromResult(Detail);
    }
    private sealed class Authentication : AuthenticationStateProvider
    {
        private Task<AuthenticationState> current = Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "user"), new Claim("account_type", "User")], "Identity.Application"))));
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => current;
        internal void Change(ClaimsPrincipal principal) { current = Task.FromResult(new AuthenticationState(principal)); NotifyAuthenticationStateChanged(current); }
    }
    private sealed class Presentations : ICentralLayeredPresentationService
    {
        internal CentralLayeredPresentationResult Result { get; set; } = new(CentralLayeredPresentationStatus.Unavailable);
        public Task<CentralLayeredPresentationResult> GetAsync(Guid captureId, ClaimsPrincipal principal, CancellationToken cancellationToken = default)
            => Task.FromResult(Result);
    }
    private sealed class Materializer : ICentralPresentationMaterializer
    {
        public Task<CentralPresentationMaterializationResult> SaveAsync(Guid captureId, string manifestIdentitySha256, IReadOnlyList<string> selectedLayerIdentities, ClaimsPrincipal principal, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Current Sky is read-only.");
    }
    private sealed class TraceOperations : INetworkOperationsReadService
    {
        internal int Calls { get; private set; }
        internal Action? OnRead { get; set; }
        public Task<OperationsObservatoryPage> ListObservatoriesAsync(
        string userId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default) => throw new AssertFailedException("Unexpected operations read.");
        public Task<OperationsObservatorySummary?> GetObservatoryAsync(
        string userId,
        Guid observatoryId,
        CancellationToken cancellationToken = default) => throw new AssertFailedException("Unexpected operations read.");
        public Task<OperationsCameraFleetPage> ListObservatoryCamerasAsync(
        string userId,
        Guid observatoryId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default) => throw new AssertFailedException("Unexpected operations read.");
        public Task<OperationsRegistrationPage> ListObservatoryRegistrationsAsync(
        string userId,
        Guid observatoryId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default) => throw new AssertFailedException("Unexpected operations read.");
        public Task<OperationsObservatoryDetail?> GetObservatoryDetailAsync(
        string userId,
        Guid observatoryId,
        CancellationToken cancellationToken = default) => throw new AssertFailedException("Unexpected operations read.");
        public Task<OperationsObservatoryDetail?> GetPublicationAuthorityAsync(
        string userId,
        Guid observatoryId,
        CancellationToken cancellationToken = default) => throw new AssertFailedException("Unexpected operations read.");
        public Task<OperationsCameraDetail?> GetCameraAsync(
        string userId,
        Guid logicalCameraId,
        CancellationToken cancellationToken = default) => throw new AssertFailedException("Unexpected operations read.");
        public Task<OperationsCameraInstallationPage> ListCameraInstallationsAsync(
        string userId,
        Guid logicalCameraId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default) => throw new AssertFailedException("Unexpected operations read.");
        public Task<OperationsCapturePage> ListCapturesAsync(
        string userId,
        Guid? observatoryId,
        Guid? logicalCameraId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default) => throw new AssertFailedException("Unexpected operations read.");
        public Task<OperationsCaptureDetail?> GetCaptureAsync(
        string userId,
        Guid captureId,
        CancellationToken cancellationToken = default) => throw new AssertFailedException("Unexpected operations read.");
        public Task<OperationsCaptureTrace?> GetCaptureTraceAsync(
        string userId,
        Guid captureId,
        CancellationToken cancellationToken = default)
        { Calls++; OnRead?.Invoke(); return Task.FromResult<OperationsCaptureTrace?>(new([], [], [], [], true)); }
        public Task<OperationsArtifactPage> ListCaptureArtifactsAsync(
        string userId,
        Guid captureId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default) => throw new AssertFailedException("Unexpected operations read.");
        public Task<OperationsJobPage> ListJobsAsync(
        string userId,
        Guid? observatoryId,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default) => throw new AssertFailedException("Unexpected operations read.");
    }
}
