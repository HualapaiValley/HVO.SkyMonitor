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
public sealed partial class CurrentSkyViewTests
{
    private static readonly Guid Observatory = Guid.NewGuid();
    private static readonly Guid Camera = Guid.NewGuid();
    private static readonly Guid Capture = Guid.NewGuid();
    private static readonly Guid Artifact = Guid.NewGuid();

    [TestMethod]
    public void PlainPreview_UsesSelectedExactBytesAndIndependentTimesWithoutMutation()
    {
        using var context = CreateContext(out var archive, out _);
        var cut = Render(context);
        cut.WaitForAssertion(() => cut.Find("img").GetAttribute("src").Should().Be("/selected-preview"));
        cut.Markup.Should().Contain("First received").And.Contain("Captured").And.Contain("fleet health");
        cut.FindAll("svg").Should().BeEmpty();
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Contains("Save selected", StringComparison.Ordinal));
        archive.Cursors.Should().Equal([null]);
    }

    [TestMethod]
    public async Task ImageError_KeepsMetadataAndRetriesOnlyTheSelectedImage()
    {
        using var context = CreateContext(out var archive, out _);
        var cut = Render(context);
        await cut.Find("img").TriggerEventAsync("onerror", new BrowserErrorEventArgs());
        cut.FindAll("img").Should().BeEmpty();
        cut.Markup.Should().Contain("Capture provenance").And.Contain("no other image is substituted");
        await cut.FindAll("button").Single(b => b.TextContent == "Retry image").ClickAsync(new());
        cut.Find("img").GetAttribute("src").Should().Be("/selected-preview");
        archive.Cursors.Should().ContainSingle();
    }

    [TestMethod]
    public async Task Refresh_ClearsImageWhilePendingAndDeniedResponseCannotBorrowIt()
    {
        using var context = CreateContext(out var archive, out _);
        var cut = Render(context);
        var pending = new TaskCompletionSource<CameraCurrentSelection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        archive.Select = _ => pending.Task;
        var refresh = cut.InvokeAsync(cut.Instance.RefreshAsync);
        cut.WaitForAssertion(() => cut.FindAll("img").Should().BeEmpty());
        cut.Find("section.current-sky").GetAttribute("aria-busy").Should().Be("true");
        pending.SetResult(null);
        await refresh;
        cut.Markup.Should().Contain("access was removed").And.NotContain("Capture provenance");
    }

    [TestMethod]
    public async Task ScopeChange_SupersedesPendingRequestEvenWhenItIgnoresCancellation()
    {
        using var context = CreateContext(out var archive, out _);
        var pending = new TaskCompletionSource<CameraCurrentSelection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        archive.Select = _ => pending.Task;
        var cut = Render(context);
        archive.Select = _ => Task.FromResult<CameraCurrentSelection?>(null);
        cut.Render(p => p.Add(c => c.CameraId, Guid.NewGuid()));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("access was removed"));
        pending.SetResult(Selected());
        await cut.InvokeAsync(() => Task.CompletedTask);
        cut.FindAll("img").Should().BeEmpty();
        cut.Markup.Should().NotContain("Capture provenance");
    }

    [TestMethod]
    public async Task AuthenticationChange_ClearsSameCaptureAndRejectsOldActorResponse()
    {
        using var context = CreateContext(out var archive, out var auth);
        var cut = Render(context);
        var pending = new TaskCompletionSource<CameraCurrentSelection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        archive.Select = _ => pending.Task;
        var refresh = cut.InvokeAsync(cut.Instance.RefreshAsync);
        await cut.InvokeAsync(() => auth.Change(new ClaimsPrincipal(new ClaimsIdentity())));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("access was removed"));
        pending.SetResult(Selected());
        await refresh;
        cut.FindAll("img").Should().BeEmpty();
        cut.Markup.Should().NotContain("Capture provenance");
    }

    [TestMethod]
    public async Task BoundedContinuation_IsExplicitAndRefreshRestartsAtLatest()
    {
        using var context = CreateContext(out var archive, out _);
        archive.Select = cursor => Task.FromResult<CameraCurrentSelection?>(cursor is null
            ? new("continue", null, null, null, null, 5, 5, "protected-cursor") : Selected());
        var cut = Render(context);
        archive.Cursors.Should().Equal([null]);
        cut.FindAll("img").Should().BeEmpty();
        await cut.FindAll("button").Single(b => b.TextContent == "Search older retained captures").ClickAsync(new());
        cut.Find("img").Should().NotBeNull();
        await cut.InvokeAsync(cut.Instance.RefreshAsync);
        archive.Cursors.Should().Equal([null, "protected-cursor", null]);
        cut.FindAll("img").Should().BeEmpty();
    }

    [TestMethod]
    public async Task NoResultAndStorageFailure_RetainLatestMetadataWithoutAnyImage()
    {
        foreach (var status in new[] { "no-result", "dependency-unavailable" })
        {
            using var context = CreateContext(out var archive, out _);
            archive.Select = _ => Task.FromResult<CameraCurrentSelection?>(new(status, null, null, null, null, 1, 0, null));
            var cut = Render(context);
            cut.Markup.Should().Contain("Capture provenance");
            cut.FindAll("img").Should().BeEmpty();
            archive.ListCalls.Should().Be(1);
            archive.Select = _ => throw new InvalidOperationException("private service details");
            await cut.InvokeAsync(cut.Instance.RefreshAsync);
            cut.Markup.Should().Contain("temporarily unavailable").And.NotContain("private service details").And.NotContain("Capture provenance");
        }
    }

    [TestMethod]
    public void ForeignCaptureOrChangedArtifactState_NeverBorrowsImage()
    {
        foreach (var state in Enum.GetValues<CentralArtifactObjectState>())
        {
            using var context = CreateContext(out var archive, out _);
            var detail = Detail();
            archive.Detail = detail with { Artifacts = [detail.Artifacts.Single() with { ObjectState = state }] };
            var cut = Render(context);
            cut.FindAll("img").Should().HaveCount(state == CentralArtifactObjectState.Available ? 1 : 0);
            cut.Markup.Should().Contain("Capture provenance");
            archive.Detail = detail with { Capture = detail.Capture with { LogicalCameraId = Guid.NewGuid() } };
            cut.Render();
            cut.WaitForAssertion(() => cut.FindAll("img").Should().BeEmpty());
            cut.Markup.Should().NotContain("Capture provenance");
        }
    }

    [TestMethod]
    public void SelectedPreviewBeyondMetadataPage_IsRenderedWithoutDrainingArtifactHistory()
    {
        using var context = CreateContext(out var archive, out _);
        archive.Detail = Detail() with { Artifacts = [], ArtifactsNextCursor = "bounded-artifact-continuation" };
        var cut = Render(context);
        cut.Find("img").GetAttribute("src").Should().Be("/selected-preview");
        archive.ListCalls.Should().Be(0);
        cut.Markup.Should().Contain("one bounded metadata page");
        archive.Cursors.Should().ContainSingle();
    }

    [TestMethod]
    [DataRow("matching")]
    [DataRow("foreignCapture")]
    [DataRow("foreignBase")]
    public void LayeredPresentation_MustMatchBothSelectedCaptureAndBase(string scenario)
    {
        using var context = CreateContext(out _, out _);
        var presentations = (Presentations)context.Services.GetRequiredService<ICentralLayeredPresentationService>();
        presentations.Result = new(CentralLayeredPresentationStatus.Found,
            new(scenario == "foreignCapture" ? Guid.NewGuid() : Capture, Guid.NewGuid(),
                scenario == "foreignBase" ? Guid.NewGuid() : Artifact, "image/png", "manifest", "presentation", "svg",
                1, 1, [], "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"u8.ToArray()));
        var cut = Render(context);
        cut.Find("img").GetAttribute("src").Should().Be(scenario == "matching"
            ? $"/api/v1.0/captures/{Capture:D}/presentation/base" : "/selected-preview");
        cut.FindAll("svg").Should().HaveCount(scenario == "matching" ? 1 : 0);
    }

    private static IRenderedComponent<CurrentSkyView> Render(BunitContext context) => context.Render<CurrentSkyView>(p =>
        p.Add(c => c.ObservatoryId, Observatory).Add(c => c.CameraId, Camera));

    private static BunitContext CreateContext(out Archive archive, out Authentication auth)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./Components/Shared/CurrentSkyView.razor.js");
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
        internal Func<string?, Task<CameraCurrentSelection?>> Select { get; set; } = _ => Task.FromResult<CameraCurrentSelection?>(Selected());
        internal OperationsCaptureDetail? Detail { get; set; } = CurrentSkyViewTests.Detail();
        internal List<string?> Cursors { get; } = [];
        internal int ListCalls { get; private set; }
        internal int DetailCalls { get; private set; }
        public Task<CameraCurrentSelection?> SelectCurrentAsync(ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, string? cursor, CancellationToken cancellationToken = default)
        { Cursors.Add(cursor); return Select(cursor); }
        public Task<CameraArchivePage> ListCapturesAsync(ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, CameraArchiveFilter filter, int take, string? cursor, CancellationToken cancellationToken = default)
        { take.Should().Be(1); ListCalls++; return Task.FromResult(new CameraArchivePage([Selected().Capture!], null)); }
        public Task<OperationsCaptureDetail?> GetCaptureAsync(ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, Guid captureId, CancellationToken cancellationToken = default)
        { DetailCalls++; return Task.FromResult(Detail); }
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
        internal Func<Task<CentralLayeredPresentationResult>>? Read { get; set; }
        internal List<ClaimsPrincipal> Principals { get; } = [];
        public Task<CentralLayeredPresentationResult> GetAsync(Guid captureId, ClaimsPrincipal principal, CancellationToken cancellationToken = default)
        { captureId.Should().Be(Capture); Principals.Add(principal); return Read?.Invoke() ?? Task.FromResult(Result); }
    }
    private sealed class Materializer : ICentralPresentationMaterializer
    {
        public Task<CentralPresentationMaterializationResult> SaveAsync(Guid captureId, string manifestIdentitySha256, IReadOnlyList<string> selectedLayerIdentities, ClaimsPrincipal principal, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Current Sky is read-only.");
    }
}
