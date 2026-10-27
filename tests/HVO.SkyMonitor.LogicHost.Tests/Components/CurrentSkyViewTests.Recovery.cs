using Bunit;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Components.Shared;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using OperationsCurrentSky = HVO.SkyMonitor.LogicHost.Components.Pages.Operations.OperationsCurrentSky;

namespace HVO.SkyMonitor.Tests.LogicHost.Components;

public sealed partial class CurrentSkyViewTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AutomaticRefresh_ShellRecoveryPreservesThePagePosition(bool continuationOnly)
    {
        using var context = CreateContext(out var archive, out _);
        var operations = new RecoveringScope();
        context.Services.AddSingleton<INetworkOperationsReadService>(operations);
        context.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        context.JSInterop.SetupModule("./Components/Shared/OperationsScopeShell.razor.js");
        archive.Select = cursor => Task.FromResult<CameraCurrentSelection?>(cursor is null
            ? new("continue", null, null, null, null, 5, 5, "older-search") : Selected());
        var cut = context.Render<OperationsCurrentSky>(p => p.Add(c => c.ObservatoryId, Observatory).Add(c => c.CameraId, Camera));
        if (!continuationOnly)
            await cut.FindAll("button").Single(b => b.TextContent == "Search older retained captures").ClickAsync(new());
        var original = cut.FindComponent<CurrentSkyView>().Instance;
        var cursors = archive.Cursors.ToArray();
        var shell = cut.FindComponent<OperationsScopeShell>();
        operations.Fail = true;
        await shell.InvokeAsync(shell.Instance.RefreshAsync);
        cut.FindComponents<CurrentSkyView>().Should().BeEmpty();
        cut.FindAll("img").Should().BeEmpty();
        operations.Fail = false;
        await shell.InvokeAsync(shell.Instance.RefreshAsync);
        cut.FindComponent<CurrentSkyView>().Instance.Should().NotBeSameAs(original);
        archive.Cursors.Should().Equal(cursors);
        archive.Revalidations.Should().ContainSingle();
        if (continuationOnly)
        {
            cut.FindAll("img").Should().BeEmpty();
            archive.Revalidations.Single().NextCursor.Should().Be("older-search");
            await cut.FindAll("button").Single(b => b.TextContent == "Search older retained captures").ClickAsync(new());
            archive.Cursors.Last().Should().Be("older-search");
        }
        cut.Find("img").GetAttribute("src").Should().Be("/selected-preview");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AutomaticRefresh_SupersededOlderSearchKeepsItsCommittedContinuation(bool authenticationNotification)
    {
        using var context = CreateContext(out var archive, out var auth);
        archive.Select = _ => Task.FromResult<CameraCurrentSelection?>(new("continue", null, null, null, null, 5, 5, "older-search"));
        var cut = Render(context);
        var pending = new TaskCompletionSource<CameraCurrentSelection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        archive.Select = _ => pending.Task;
        var navigation = cut.FindAll("button").Single(b => b.TextContent == "Search older retained captures").ClickAsync(new());
        try
        {
            if (authenticationNotification)
            {
                var principal = (await auth.GetAuthenticationStateAsync()).User;
                await cut.InvokeAsync(() => auth.Change(principal));
            }
            else cut.Render();
            cut.WaitForAssertion(() => archive.Revalidations.Should().ContainSingle());
            archive.Revalidations.Single().NextCursor.Should().Be("older-search");
        }
        finally
        {
            pending.TrySetResult(Selected());
            await navigation;
        }
        cut.FindAll("img").Should().BeEmpty("a superseded navigation response must not restore content");
        cut.FindAll("button").Should().Contain(b => b.TextContent == "Search older retained captures");
    }

    private sealed class RecoveringScope : INetworkOperationsReadService
    {
        internal bool Fail { get; set; }
        public Task<OperationsObservatorySummary?> GetObservatoryAsync(string userId, Guid observatoryId, CancellationToken cancellationToken = default)
            => Fail ? throw new InvalidOperationException("temporary scope failure")
                : Task.FromResult<OperationsObservatorySummary?>(new(Observatory, "Observatory", ObservatoryMembershipRole.Owner, 1, 0, 0, 0));
        public Task<OperationsCameraDetail?> GetCameraAsync(string userId, Guid logicalCameraId, CancellationToken cancellationToken = default)
            => Task.FromResult<OperationsCameraDetail?>(new(Camera, Observatory, "Camera", "", [], null));
        public Task<OperationsObservatoryPage> ListObservatoriesAsync(string userId, int take, string? cursor, CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationsObservatoryPage([], null));
        public Task<OperationsCameraFleetPage> ListObservatoryCamerasAsync(string userId, Guid observatoryId, int take, string? cursor, CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationsCameraFleetPage([], null));
        public Task<OperationsRegistrationPage> ListObservatoryRegistrationsAsync(string userId, Guid observatoryId, int take, string? cursor, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Unexpected operations read.");
        public Task<OperationsObservatoryDetail?> GetObservatoryDetailAsync(string userId, Guid observatoryId, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Unexpected operations read.");
        public Task<OperationsObservatoryDetail?> GetPublicationAuthorityAsync(string userId, Guid observatoryId, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Unexpected operations read.");
        public Task<OperationsCameraInstallationPage> ListCameraInstallationsAsync(string userId, Guid logicalCameraId, int take, string? cursor, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Unexpected operations read.");
        public Task<OperationsCapturePage> ListCapturesAsync(string userId, Guid? observatoryId, Guid? logicalCameraId, int take, string? cursor, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Unexpected operations read.");
        public Task<OperationsCaptureDetail?> GetCaptureAsync(string userId, Guid captureId, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Unexpected operations read.");
        public Task<OperationsCaptureTrace?> GetCaptureTraceAsync(string userId, Guid captureId, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Unexpected operations read.");
        public Task<OperationsArtifactPage> ListCaptureArtifactsAsync(string userId, Guid captureId, int take, string? cursor, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Unexpected operations read.");
        public Task<OperationsJobPage> ListJobsAsync(string userId, Guid? observatoryId, int take, string? cursor, CancellationToken cancellationToken = default)
            => throw new AssertFailedException("Unexpected operations read.");
    }
}
