using Bunit;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.Tests.LogicHost.Components;

public sealed partial class CurrentSkyViewTests
{
    [TestMethod]
    public async Task AutomaticRefresh_KeepsTheOlderSelectedItemWithoutStartingAnotherSearch()
    {
        using var context = CreateContext(out var archive, out _);
        archive.Select = cursor => Task.FromResult<CameraCurrentSelection?>(cursor is null
            ? new("continue", null, null, null, null, 5, 5, "older-search") : Selected());
        var cut = Render(context);
        await cut.FindAll("button").Single(b => b.TextContent == "Search older retained captures").ClickAsync(new());
        await cut.InvokeAsync(cut.Instance.RefreshAsync);
        archive.Cursors.Should().Equal([null, "older-search"]);
        cut.Find("img").GetAttribute("src").Should().Be("/selected-preview");
        archive.ListCalls.Should().Be(0);
    }

    [TestMethod]
    public async Task AutomaticRefresh_PreservesAContinuationWithoutConsumingIt()
    {
        using var context = CreateContext(out var archive, out _);
        archive.Select = cursor => Task.FromResult<CameraCurrentSelection?>(cursor is null
            ? new("continue", null, null, null, null, 5, 5, "older-search") : Selected());
        var cut = Render(context);
        await cut.InvokeAsync(cut.Instance.RefreshAsync);
        archive.Cursors.Should().Equal([null]);
        cut.FindAll("img").Should().BeEmpty();
        await cut.FindAll("button").Single(b => b.TextContent == "Search older retained captures").ClickAsync(new());
        archive.Cursors.Should().Equal([null, "older-search"]);
        cut.Find("img").GetAttribute("src").Should().Be("/selected-preview");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AutomaticRefresh_TimerRevalidatesTheSamePosition(bool continuation)
    {
        using var context = CreateContext(out var archive, out _);
        var clock = new RevalidationClock();
        context.Services.AddSingleton<TimeProvider>(clock);
        archive.Select = _ => Task.FromResult<CameraCurrentSelection?>(continuation
            ? new("continue", null, null, null, null, 5, 5, "older-search") : Selected());
        var cut = Render(context);
        await cut.InvokeAsync(clock.Tick);
        cut.WaitForAssertion(() => archive.Revalidations.Should().ContainSingle());
        archive.Cursors.Should().Equal([null]);
        archive.Revalidations.Single().NextCursor.Should().Be(continuation ? "older-search" : null);
        if (!continuation) cut.Find("img").GetAttribute("src").Should().Be("/selected-preview");
    }

    [TestMethod]
    public async Task AutomaticRefresh_ClearsPendingContentAndCoalescesFurtherEvents()
    {
        using var context = CreateContext(out var archive, out _);
        var cut = Render(context);
        var pending = new TaskCompletionSource<CameraCurrentSelection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        archive.Revalidate = _ => pending.Task;
        var refresh = cut.InvokeAsync(cut.Instance.RefreshAsync);
        cut.WaitForAssertion(() => cut.FindAll("img").Should().BeEmpty());
        await cut.InvokeAsync(cut.Instance.RefreshAsync);
        archive.Revalidations.Should().ContainSingle();
        pending.SetResult(Selected());
        await refresh;
        archive.Cursors.Should().Equal([null]);
        cut.Find("img").GetAttribute("src").Should().Be("/selected-preview");
    }

    [TestMethod]
    public async Task AutomaticRefresh_UnavailableItemCannotBorrowAnotherImageAndCanRecoverExactly()
    {
        using var context = CreateContext(out var archive, out _);
        var cut = Render(context);
        archive.Revalidate = selected => Task.FromResult<CameraCurrentSelection?>(selected with { Status = "unavailable", ContentPath = null });
        await cut.InvokeAsync(cut.Instance.RefreshAsync);
        cut.FindAll("img").Should().BeEmpty();
        cut.Markup.Should().Contain("selected item is unavailable");
        archive.Cursors.Should().Equal([null]);
        archive.Revalidate = _ => Task.FromResult<CameraCurrentSelection?>(Selected());
        await cut.InvokeAsync(cut.Instance.RefreshAsync);
        cut.Find("img").GetAttribute("src").Should().Be("/selected-preview");
        archive.Cursors.Should().Equal([null]);
        archive.ListCalls.Should().Be(0);
    }

    private sealed class RevalidationClock : TimeProvider
    {
        private TimerCallback? callback;
        private object? state;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            dueTime.Should().Be(TimeSpan.FromSeconds(30));
            period.Should().Be(TimeSpan.FromSeconds(30));
            this.callback = callback;
            this.state = state;
            return new RevalidationTimer();
        }
        internal void Tick() => callback!(state);
        private sealed class RevalidationTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
