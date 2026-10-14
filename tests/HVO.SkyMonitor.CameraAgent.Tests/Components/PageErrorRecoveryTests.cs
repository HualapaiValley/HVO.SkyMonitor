using System.Reflection;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Layout;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.Common.Infrastructure.Diagnostics;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class PageErrorRecoveryTests
{
    [TestMethod]
    public async Task RenderFailureKeepsNavigationAndCanRetry()
    {
        using var context = CreateContext(interactive: true);
        var state = new FailureState { FailRender = true };
        var cut = context.Render<MainLayout>(parameters => parameters.Add(layout => layout.Body, PageBody(state)));

        Assert.IsNotNull(cut.Find("nav[aria-label='Test navigation']"));
        Assert.IsNotNull(cut.Find(".content-container .error-display"));
        Assert.IsFalse(cut.Markup.Contains("private-error-sentinel", StringComparison.Ordinal));
        Assert.AreEqual("test-correlation", cut.Find(".error-display code").TextContent);
        state.FailRender = false;
        await cut.Find(".error-display button").ClickAsync().ConfigureAwait(false);

        cut.WaitForElement(".test-page");
        Assert.HasCount(0, cut.FindAll(".error-display"));
    }

    [TestMethod]
    public async Task EventFailureIsCaughtAndNewPageClearsTheBoundary()
    {
        using var context = CreateContext(interactive: true);
        var cut = context.Render<MainLayout>(parameters => parameters.Add(layout => layout.Body, PageBody(new FailureState())));

        await cut.Find(".test-page").ClickAsync().ConfigureAwait(false);

        cut.WaitForElement(".error-display");
        Assert.IsNotNull(cut.Find("nav[aria-label='Test navigation']"));
        Assert.IsFalse(cut.Markup.Contains("private-error-sentinel", StringComparison.Ordinal));
        cut.Render(parameters => parameters.Add(layout => layout.Body,
            builder => builder.AddMarkupContent(0, "<h1>Next page</h1>")));

        Assert.AreEqual("Next page", cut.Find("h1").TextContent);
        Assert.HasCount(0, cut.FindAll(".error-display"));
    }

    [TestMethod]
    public void StaticFailureHasWorkingLinksAndNoInertButtons()
    {
        using var context = CreateContext(interactive: false);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/Account/Login?returnUrl=%2Foperations");
        var cut = context.Render<MainLayout>(parameters => parameters.Add(layout => layout.Body, PageBody(new FailureState { FailRender = true })));

        var panel = cut.Find(".error-display");
        Assert.HasCount(0, panel.QuerySelectorAll("button"));
        var reload = panel.QuerySelector("a[data-enhance-nav='false']");
        Assert.IsNotNull(reload);
        Assert.AreEqual("http://localhost/Account/Login?returnUrl=%2Foperations", reload.GetAttribute("href"));
        Assert.AreEqual("Reload page", reload.TextContent);
        Assert.IsNotNull(panel.QuerySelector("a[href='/']"));
    }

    [TestMethod]
    public void DedicatedErrorPageRemainsStaticAndPreservesEncodedDiagnostics()
    {
        using var context = CreateContext(interactive: false);
        var http = new DefaultHttpContext { TraceIdentifier = "request-id" };
        http.Request.QueryString = new QueryString("?correlationId=%3Cscript%3Eprivate%3C%2Fscript%3E");
        var cut = context.Render<Error>(parameters => parameters.AddCascadingValue<HttpContext>(http));

        Assert.IsNotNull(typeof(Error).GetCustomAttribute<ExcludeFromInteractiveRoutingAttribute>());
        var identifiers = cut.FindAll("code");
        Assert.HasCount(2, identifiers);
        Assert.AreEqual("<script>private</script>", identifiers[0].TextContent);
        Assert.AreEqual("request-id", identifiers[1].TextContent);
        Assert.HasCount(0, cut.FindAll("script, button"));
        Assert.AreEqual("Return home", cut.Find("a[href='/']").TextContent);
        Assert.IsFalse(cut.Markup.Contains("Reload page", StringComparison.Ordinal));
    }

    private static BunitContext CreateContext(bool interactive)
    {
        var context = new BunitContext();
        context.Services.AddSingleton<ICorrelationIdAccessor>(new TestCorrelationIdAccessor());
        context.Services.AddSingleton<IErrorBoundaryLogger>(new TestErrorBoundaryLogger());
        context.ComponentFactories.AddStub<MainLayoutNavigation>("<nav aria-label='Test navigation'>Navigation</nav>");
        context.ComponentFactories.AddStub<ArchiveNavigation>();
        context.ComponentFactories.AddStub<MainLayoutFooter>();
        context.SetRendererInfo(new RendererInfo(interactive ? "Server" : "Static", interactive));
        return context;
    }

    private static RenderFragment PageBody(FailureState state) => builder =>
    {
        builder.OpenComponent<FaultingPage>(0);
        builder.AddAttribute(1, nameof(FaultingPage.State), state);
        builder.CloseComponent();
    };

    private sealed class FailureState
    {
        public bool FailRender { get; set; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
        Justification = "The Blazor renderer instantiates this test component through OpenComponent.")]
    private sealed class FaultingPage : ComponentBase
    {
        [Parameter]
        public FailureState State { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (State.FailRender)
            {
                throw new InvalidOperationException("private-error-sentinel");
            }

            builder.OpenElement(0, "button");
            builder.AddAttribute(1, "class", "test-page");
            builder.AddAttribute(2, "onclick", EventCallback.Factory.Create(this, Fail));
            builder.AddContent(3, "Page action");
            builder.CloseElement();
        }

        private static void Fail() => throw new InvalidOperationException("private-error-sentinel");
    }

    private sealed class TestCorrelationIdAccessor : ICorrelationIdAccessor
    {
        public string? GetCorrelationId() => "test-correlation";
    }

    private sealed class TestErrorBoundaryLogger : IErrorBoundaryLogger
    {
        public ValueTask LogErrorAsync(Exception exception) => ValueTask.CompletedTask;
    }
}
