using System.Security.Claims;
using System.Diagnostics.CodeAnalysis;
using Bunit;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Components.Shared;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using BrowserErrorEventArgs = Microsoft.AspNetCore.Components.Web.ErrorEventArgs;

namespace HVO.SkyMonitor.Tests.LogicHost.Components;

public sealed partial class CurrentSkyViewTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task SameCaptureRevalidation_ResetsViewerAndJavaScriptEvenWithReusedSelection(bool delayed, bool reuseSelection)
    {
        using var context = CreateLifetimeContext(out var archive, out _, out var presentations, out var js);
        var selected = Selected();
        archive.Select = _ => Task.FromResult<CameraCurrentSelection?>(selected);
        var cut = Render(context);
        var original = cut.FindComponent<ExactCaptureViewer>().Instance;
        await cut.Find("img").TriggerEventAsync("onerror", new BrowserErrorEventArgs());
        cut.FindAll("svg").Should().BeEmpty();
        cut.FindAll("input[type=checkbox]").Should().OnlyContain(input => input.HasAttribute("disabled"));
        await cut.FindAll("button").Single(button => button.TextContent == "Retry image").ClickAsync(new());
        cut.Find("img").GetAttribute("src").Should().Be(presentations.Result.Presentation!.BaseContentPath);
        presentations.Principals.Should().ContainSingle("image retry must not reselect or reread a presentation");
        archive.Cursors.Should().ContainSingle();
        js.Imports.Should().Be(1);
        js.Bindings.Should().Be(2, "retry binds the restored layer controls using the existing module");
        await cut.Find("img").TriggerEventAsync("onerror", new BrowserErrorEventArgs());

        var pending = new TaskCompletionSource<CameraCurrentSelection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = reuseSelection ? selected : selected with { };
        archive.Revalidate = _ => delayed ? pending.Task : Task.FromResult<CameraCurrentSelection?>(next);
        var clearedRenders = new List<bool>();
        cut.OnMarkupUpdated += (_, _) => clearedRenders.Add(cut.FindAll(".current-freshness").Count == 0);
        var refresh = cut.InvokeAsync(cut.Instance.RefreshAsync);
        if (delayed)
        {
            cut.WaitForAssertion(() => cut.FindComponents<ExactCaptureViewer>().Should().BeEmpty());
            cut.FindAll("img, svg, .layer-controls, .current-freshness").Should().BeEmpty();
            cut.Markup.Should().NotContain("Capture provenance");
            cut.WaitForAssertion(() => js.Disposals.Should().Be(1));
            pending.SetResult(next);
        }
        await refresh;

        cut.FindComponent<ExactCaptureViewer>().Instance.IsBaseImageUnavailable.Should().BeFalse();
        cut.FindComponent<ExactCaptureViewer>().Instance.Should().NotBeSameAs(original);
        clearedRenders.Should().Contain(delayed, "only delayed reads expose the cleared detail in a render");
        if (!delayed) clearedRenders.Should().NotContain(true);
        cut.Find("img").GetAttribute("src").Should().Be(presentations.Result.Presentation!.BaseContentPath);
        cut.FindAll("svg").Should().ContainSingle();
        cut.Find("input[type=checkbox]").HasAttribute("disabled").Should().BeFalse();
        cut.Find("input[type=checkbox]").HasAttribute("checked").Should().BeTrue();
        cut.Find(".current-freshness").TextContent.Should().Contain("1970-01-01 00:00:00Z").And.Contain("1970-01-01 01:00:00Z");
        AssertReads(archive, presentations, 2);
        js.Imports.Should().Be(2);
        js.Bindings.Should().Be(3);
        cut.WaitForAssertion(() => js.Disposals.Should().Be(1));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SelectedBaseChange_ReplacesViewerAndRejectsPreviousLayeredBase(bool delayed)
    {
        using var context = CreateLifetimeContext(out var archive, out _, out var presentations, out var js);
        var cut = Render(context);
        var original = cut.FindComponent<ExactCaptureViewer>().Instance;
        var nextArtifact = Guid.NewGuid();
        archive.Detail = Detail() with { Artifacts = [Detail().Artifacts.Single() with { ArtifactId = nextArtifact, ContentPath = "/new-exact-preview" }] };
        var next = Selected() with { ArtifactId = nextArtifact, ContentPath = "/new-exact-preview" };
        var pending = new TaskCompletionSource<CameraCurrentSelection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        archive.Select = _ => delayed ? pending.Task : Task.FromResult<CameraCurrentSelection?>(next);
        var refresh = cut.FindAll("button").Single(button => button.TextContent == "Refresh Current Sky").ClickAsync(new());
        if (delayed)
        {
            cut.WaitForAssertion(() => cut.FindAll("img, svg").Should().BeEmpty());
            pending.SetResult(next);
        }
        await refresh;

        cut.FindComponent<ExactCaptureViewer>().Instance.Should().NotBeSameAs(original);
        cut.Find("img").GetAttribute("src").Should().Be("/new-exact-preview");
        cut.FindAll("svg, .layer-controls").Should().BeEmpty("the old presentation has a different base artifact");
        await cut.Find("img").TriggerEventAsync("onerror", new BrowserErrorEventArgs());
        cut.FindAll("img").Should().BeEmpty();
        await cut.FindAll("button").Single(button => button.TextContent == "Retry image").ClickAsync(new());
        cut.Find("img").GetAttribute("src").Should().Be("/new-exact-preview");
        AssertReads(archive, presentations, 2);
        js.Imports.Should().Be(1, "a plain Preview needs no layer module");
        cut.WaitForAssertion(() => js.Disposals.Should().Be(1));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AccessRevocation_RemovesViewerBeforeAuthorityCompletes(bool credentialRevoked)
    {
        using var context = CreateLifetimeContext(out var archive, out var auth, out var presentations, out var js);
        var cut = Render(context);
        var pending = new TaskCompletionSource<CameraCurrentSelection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        archive.Revalidate = _ => pending.Task;
        Task refresh;
        if (credentialRevoked)
        {
            await cut.InvokeAsync(() => auth.Change(new ClaimsPrincipal(new ClaimsIdentity())));
            refresh = Task.CompletedTask;
        }
        else
        {
            refresh = cut.InvokeAsync(cut.Instance.RefreshAsync);
        }
        cut.WaitForAssertion(() => cut.FindComponents<ExactCaptureViewer>().Should().BeEmpty());
        cut.FindAll("img, svg, .layer-controls, .current-freshness").Should().BeEmpty();
        cut.Markup.Should().NotContain("Capture provenance");
        cut.WaitForAssertion(() => js.Disposals.Should().Be(1));
        if (!credentialRevoked) pending.SetResult(null);
        await refresh;
        cut.Markup.Should().Contain("access was removed");
        (archive.Cursors.Count + archive.Revalidations.Count).Should().Be(credentialRevoked ? 1 : 2);
        archive.DetailCalls.Should().Be(1);
        presentations.Principals.Should().ContainSingle();
        js.Imports.Should().Be(1);
    }

    [TestMethod]
    public async Task AuthenticationChange_RecreatesSameCaptureForNewActorAndRejectsSupersededSelection()
    {
        using var context = CreateLifetimeContext(out var archive, out var auth, out var presentations, out var js);
        var cut = Render(context);
        var original = cut.FindComponent<ExactCaptureViewer>().Instance;
        var pending = new TaskCompletionSource<CameraCurrentSelection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        archive.Revalidate = _ => pending.Task;
        var oldRefresh = cut.InvokeAsync(cut.Instance.RefreshAsync);
        cut.WaitForAssertion(() => cut.FindComponents<ExactCaptureViewer>().Should().BeEmpty());
        archive.Revalidate = _ => Task.FromResult<CameraCurrentSelection?>(Selected());
        var nextActor = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "next-actor"), new Claim("account_type", "User")], "Identity.Application"));
        await cut.InvokeAsync(() => auth.Change(nextActor));
        cut.WaitForAssertion(() => presentations.Principals.Should().HaveCount(2));
        var current = cut.FindComponent<ExactCaptureViewer>().Instance;
        current.Should().NotBeSameAs(original);
        presentations.Principals.Last().Should().BeSameAs(nextActor);
        pending.SetResult(Selected() with { ContentPath = "/superseded-selection" });
        await oldRefresh;
        cut.FindComponent<ExactCaptureViewer>().Instance.Should().BeSameAs(current);
        cut.Markup.Should().NotContain("/superseded-selection");
        cut.FindAll("img").Should().ContainSingle();
        (archive.Cursors.Count + archive.Revalidations.Count).Should().Be(3);
        archive.DetailCalls.Should().Be(3, "the cancellation-ignoring fixture also answers the superseded detail read");
        presentations.Principals.Should().HaveCount(2);
        js.Imports.Should().Be(2);
        cut.WaitForAssertion(() => js.Disposals.Should().Be(1));
    }

    [TestMethod]
    [DataRow("user")]
    [DataRow("next-actor")]
    public async Task VisibleViewer_AuthenticationNotificationReadsBeforeHostRevalidation(string actorId)
    {
        using var context = CreateLifetimeContext(out var archive, out var auth, out var presentations, out var js);
        var cut = Render(context);
        var original = cut.FindComponent<ExactCaptureViewer>().Instance;
        var pending = new TaskCompletionSource<CameraCurrentSelection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        archive.Revalidate = _ => pending.Task;
        var actor = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, actorId), new Claim("account_type", "User")], "Identity.Application"));

        await cut.InvokeAsync(() => auth.Change(actor));

        cut.WaitForAssertion(() => cut.FindComponents<ExactCaptureViewer>().Should().BeEmpty());
        cut.FindAll("img, svg, .layer-controls, .current-freshness").Should().BeEmpty();
        cut.Markup.Should().NotContain("Capture provenance");
        presentations.Principals.Should().HaveCount(2, "the authentication cascade notifies the visible retiring viewer first");
        presentations.Principals.Last().Should().BeSameAs(actor);
        cut.WaitForAssertion(() => js.Disposals.Should().Be(1));
        await cut.InvokeAsync(() => pending.SetResult(Selected()));
        cut.WaitForAssertion(() => cut.FindComponents<ExactCaptureViewer>().Should().ContainSingle());

        cut.FindComponent<ExactCaptureViewer>().Instance.Should().NotBeSameAs(original);
        cut.Find("img").GetAttribute("src").Should().Be(presentations.Result.Presentation!.BaseContentPath);
        (archive.Cursors.Count + archive.Revalidations.Count).Should().Be(2);
        archive.DetailCalls.Should().Be(2);
        archive.ListCalls.Should().Be(0);
        presentations.Principals.Should().HaveCount(3, "the service-level authentication cascade reads the retiring viewer before the host replaces it");
        js.Imports.Should().Be(2);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SupersededPresentation_CannotRestoreLayersAfterRefreshOrRevocation(bool revoke)
    {
        using var context = CreateLifetimeContext(out var archive, out var auth, out var presentations, out var js);
        var oldResult = presentations.Result;
        var pending = new TaskCompletionSource<CentralLayeredPresentationResult>();
        presentations.Read = () => pending.Task;
        var cut = Render(context);
        var original = cut.FindComponent<ExactCaptureViewer>().Instance;
        cut.FindAll("svg").Should().BeEmpty();
        presentations.Read = null;
        presentations.Result = new(CentralLayeredPresentationStatus.Unavailable);
        if (revoke)
            await cut.InvokeAsync(() => auth.Change(new ClaimsPrincipal(new ClaimsIdentity())));
        else
            await cut.InvokeAsync(cut.Instance.RefreshAsync);
        // Complete on the renderer so the disposed component's continuation runs before assertions.
        await cut.InvokeAsync(() => pending.SetResult(oldResult));

        cut.FindAll("svg, .layer-controls").Should().BeEmpty();
        cut.Markup.Should().NotContain("old-presentation");
        if (revoke)
        {
            cut.FindComponents<ExactCaptureViewer>().Should().BeEmpty();
            cut.FindAll("img").Should().BeEmpty();
            cut.Markup.Should().NotContain("Capture provenance");
        }
        else
        {
            cut.FindComponent<ExactCaptureViewer>().Instance.Should().NotBeSameAs(original);
            cut.Find("img").GetAttribute("src").Should().Be("/selected-preview");
            AssertReads(archive, presentations, 2);
        }
        js.Imports.Should().Be(0, "a late result on the disposed viewer must not attach browser layer handlers");
    }

    private static BunitContext CreateLifetimeContext(out Archive archive, out Authentication auth,
        out Presentations presentations, out ViewerJavaScript js)
    {
        var context = CreateContext(out archive, out auth);
        js = new ViewerJavaScript();
        context.Services.AddSingleton<IJSRuntime>(js);
        presentations = (Presentations)context.Services.GetRequiredService<ICentralLayeredPresentationService>();
        presentations.Result = new(CentralLayeredPresentationStatus.Found,
            new(Capture, Guid.NewGuid(), Artifact, "image/png", "manifest", "old-presentation", "svg", 1, 1,
                [new("layer-identity", "Grid", "grid", 1, true, 1_000_000, "r1", "s1")],
                "<svg xmlns=\"http://www.w3.org/2000/svg\"><g id=\"grid\"></g></svg>"u8.ToArray()));
        return context;
    }

    private static void AssertReads(Archive archive, Presentations presentations, int expected)
    {
        archive.Cursors.Should().OnlyContain(cursor => cursor == null);
        (archive.Cursors.Count + archive.Revalidations.Count).Should().Be(expected);
        archive.DetailCalls.Should().Be(expected);
        archive.ListCalls.Should().Be(0);
        presentations.Principals.Should().HaveCount(expected);
    }

    // Count module handles and binds at the interop boundary; no browser heap/CPU claim is made.
    private sealed class ViewerJavaScript : IJSRuntime
    {
        internal int Imports { get; private set; }
        internal int Bindings { get; private set; }
        internal int Disposals { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The imported module handle transfers to the component, which disposes it; tests assert that ownership boundary.")]
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            identifier.Should().Be("import");
            var viewer = Equals(args![0], ExactCaptureViewer.ModulePath);
            if (viewer) Imports++;
            return ValueTask.FromResult((TValue)(object)new Module(this, viewer));
        }
        private sealed class Module(ViewerJavaScript owner, bool viewer) : IJSObjectReference
        {
            public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
            public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            {
                if (viewer)
                {
                    identifier.Should().Be("bindLayerToggles");
                    owner.Bindings++;
                }
                else identifier.Should().BeOneOf("attach", "detach");
                return ValueTask.FromResult(default(TValue)!);
            }
            public ValueTask DisposeAsync()
            {
                if (viewer) owner.Disposals++;
                return ValueTask.CompletedTask;
            }
        }
    }
}
