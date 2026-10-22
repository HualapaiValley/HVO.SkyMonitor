using System.Security.Claims;
using Bunit;
using FluentAssertions;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Components.Pages.Operations;
using HVO.SkyMonitor.LogicHost.Components.Shared;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HVO.SkyMonitor.Tests.LogicHost.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class OperationsScopeShellTests
{
    [TestMethod]
    [DataRow("Viewer")]
    [DataRow("Manager")]
    [DataRow("Owner")]
    public async Task HumanRoles_ReadAuthorizedScopeWithoutEnablingUndeliveredWorkspaces(string role)
    {
        await using var context = CreateContext(out _);
        var scope = await SeedAsync(context, "Station", "Camera", Enum.Parse<ObservatoryMembershipRole>(role));
        var cut = context.Render<OperationsScopeShell>(p => p
            .Add(c => c.ObservatoryId, scope.ObservatoryId).Add(c => c.CameraId, scope.CameraId));

        cut.WaitForAssertion(() => cut.Find("h1").TextContent.Should().Be("Camera"));
        cut.Find(".scope-role").TextContent.Should().Contain(role);
        cut.Find("nav[aria-label='Breadcrumb']").TextContent.Should().Contain("Station").And.Contain("Camera");
        cut.Find(".scope-tabs [aria-current='page']").TextContent.Should().Be("Scope overview");
        cut.FindAll(".scope-tabs a").Select(a => a.TextContent).Should()
            .Equal("Scope overview", "Current Sky", "Installation and capture history");
        cut.FindAll("a").Should().NotContain(a => a.GetAttribute("href")!.Contains("processing", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DualMembership_ForeignCameraInAnAuthorizedObservatoryPathRevealsNoNamesOrChildren()
    {
        await using var context = CreateContext(out _);
        var a = await SeedAsync(context, "Station A", "Camera A");
        var b = await SeedAsync(context, "Secret station B", "Secret camera B");
        var cut = context.Render<OperationsScopeShell>(p => p.Add(c => c.ObservatoryId, a.ObservatoryId)
            .Add(c => c.CameraId, b.CameraId).AddChildContent("protected child"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Scope not found")
            .And.NotContain("Station A").And.NotContain("Secret").And.NotContain("protected child"));
        cut.Render(p => p.Add(c => c.CameraId, a.CameraId));
        cut.WaitForAssertion(() => cut.Find("h1").TextContent.Should().Be("Camera A"));
    }

    [TestMethod]
    public async Task OtherObservatoryMemberAndAnonymousCaller_CannotReadTheSelectedScope()
    {
        await using var context = CreateContext(out var authentication);
        var a = await SeedAsync(context, "Hidden A", "Hidden camera", userId: "other");
        await SeedAsync(context, "Accessible B", "B camera");
        var cut = context.Render<OperationsScopeShell>(p => p.Add(c => c.ObservatoryId, a.ObservatoryId));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Scope not found").And.NotContain("Hidden"));

        await cut.InvokeAsync(() => authentication.Change(new ClaimsPrincipal(new ClaimsIdentity())));
        cut.Render(p => p.Add(c => c.ObservatoryId, (Guid?)null));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Scope not found").And.NotContain("Accessible B"));
    }

    [TestMethod]
    public async Task RevokedMembership_ClearsTheExistingCircuitAndItsChildOnRefresh()
    {
        await using var context = CreateContext(out _);
        var a = await SeedAsync(context, "Retained station", "Retained camera");
        var cut = context.Render<OperationsScopeShell>(p => p.Add(c => c.ObservatoryId, a.ObservatoryId)
            .Add(c => c.CameraId, a.CameraId).AddChildContent("authorized child"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Retained camera").And.Contain("authorized child"));
        await using (var scope = context.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.ObservatoryMemberships.Remove(await db.ObservatoryMemberships.SingleAsync());
            await db.SaveChangesAsync();
        }

        await cut.InvokeAsync(cut.Instance.RefreshAsync);

        cut.Markup.Should().Contain("Scope not found").And.NotContain("Retained")
            .And.NotContain("authorized child");
    }

    [TestMethod]
    public async Task AuthenticationChange_ClearsLabelsAndProtectedChildrenImmediately()
    {
        await using var context = CreateContext(out var authentication);
        var a = await SeedAsync(context, "Protected station", "Protected camera");
        var cut = context.Render<OperationsScopeShell>(p => p.Add(c => c.ObservatoryId, a.ObservatoryId)
            .AddChildContent("authorized child"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Protected station"));

        await cut.InvokeAsync(() => authentication.Change(Principal("different")));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Scope not found")
            .And.NotContain("Protected station").And.NotContain("authorized child"));
    }

    [TestMethod]
    public async Task AuthenticationChange_HidesPreviousActorBeforeRevalidationCompletes()
    {
        await using var context = CreateContext(out var authentication);
        var a = await SeedAsync(context, "Private station", "Private camera");
        var cut = context.Render<OperationsScopeShell>(p => p.Add(c => c.ObservatoryId, a.ObservatoryId)
            .AddChildContent("private child"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Private station").And.Contain("private child"));
        var replacement = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        authentication.Pending = replacement.Task;
        try
        {
            await cut.InvokeAsync(() => authentication.Change(Principal("different")));
            cut.WaitForAssertion(() => cut.Markup.Should().Contain("Checking scope access")
                .And.NotContain("Private station").And.NotContain("private child"));
        }
        finally
        {
            replacement.TrySetResult(new(Principal("different")));
        }
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Scope not found"));
    }

    [TestMethod]
    public async Task DelayedPriorNavigation_CannotReplaceTheNewScope()
    {
        await using var context = CreateContext(out var authentication);
        var a = await SeedAsync(context, "Old station", "Old camera");
        var b = await SeedAsync(context, "New station", "New camera");
        var pending = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        authentication.Pending = pending.Task;
        var cut = context.Render<OperationsScopeShell>(p => p.Add(c => c.ObservatoryId, a.ObservatoryId)
            .Add(c => c.CameraId, a.CameraId));
        cut.Markup.Should().Contain("Checking scope access");

        authentication.Pending = null;
        cut.Render(p => p.Add(c => c.ObservatoryId, b.ObservatoryId).Add(c => c.CameraId, b.CameraId));
        cut.WaitForAssertion(() => cut.Find("h1").TextContent.Should().Be("New camera"));
        pending.SetResult(new(Principal("member")));
        await cut.InvokeAsync(() => Task.CompletedTask);
        cut.Find("h1").TextContent.Should().Be("New camera");
    }

    [TestMethod]
    public async Task LargeScopeLists_ReplaceBoundedPagesAndRetainTheirCursorsInScopeLinks()
    {
        await using var context = CreateContext(out _);
        var selected = await SeedAsync(context, "Station 000", "Camera 000", cameraCount: 63);
        for (var i = 1; i < 63; i++) await SeedAsync(context, $"Station {i:D3}", "Camera");
        var cut = context.Render<OperationsScopeShell>(p => p.Add(c => c.ObservatoryId, selected.ObservatoryId));
        cut.WaitForAssertion(() => cut.FindAll("nav[aria-label='Observatory scopes'] > a").Should().HaveCount(25));
        cut.FindAll("nav[aria-label='Camera scopes'] > a").Should().HaveCount(25);
        var firstObservatories = ScopeNames(cut, "Observatory");
        var obsToken = NextToken(cut, "observatory", "observatoryPage");
        var cameraToken = NextToken(cut, "camera", "cameraPage");

        cut.Render(p => p.Add(c => c.ObservatoryPage, obsToken).Add(c => c.CameraPage, cameraToken));
        cut.WaitForAssertion(() => ScopeNames(cut, "Observatory").Should().NotIntersectWith(firstObservatories));
        ScopeNames(cut, "Observatory").Should().HaveCount(25);
        ScopeNames(cut, "Camera").Should().HaveCount(25);
        var selectedLink = cut.Find("nav[aria-label='Camera scopes'] > a").GetAttribute("href")!;
        var query = QueryHelpers.ParseQuery(new Uri(new Uri("https://test/"), selectedLink).Query);
        query["observatoryPage"].ToString().Should().Be(obsToken);
        query["cameraPage"].ToString().Should().Be(cameraToken);

        cut.Render(p => p.Add(c => c.ObservatoryPage, NextToken(cut, "observatory", "observatoryPage"))
            .Add(c => c.CameraPage, NextToken(cut, "camera", "cameraPage")));
        cut.WaitForAssertion(() => ScopeNames(cut, "Observatory").Should().HaveCount(13));
        ScopeNames(cut, "Camera").Should().HaveCount(13);
        cut.FindAll("a").Should().NotContain(a => a.TextContent.StartsWith("Next ", StringComparison.Ordinal));
        cut.Render(p => p.Add(c => c.ObservatoryPage, (string?)null).Add(c => c.CameraPage, (string?)null));
        cut.WaitForAssertion(() => ScopeNames(cut, "Observatory").Should().Equal(firstObservatories));
    }

    [TestMethod]
    public async Task CameraContinuation_CannotBeReusedInAnotherObservatoryOrByAnotherActor()
    {
        await using var context = CreateContext(out var authentication);
        var a = await SeedAsync(context, "Station A", "Camera A", cameraCount: 30);
        var b = await SeedAsync(context, "Station B", "Camera B");
        var cut = context.Render<OperationsScopeShell>(p => p.Add(c => c.ObservatoryId, a.ObservatoryId));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Next camera page"));
        var token = NextToken(cut, "camera", "cameraPage");

        cut.Render(p => p.Add(c => c.ObservatoryId, b.ObservatoryId).Add(c => c.CameraPage, token));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("navigation page is no longer available")
            .And.NotContain("Station B").And.NotContain("Camera B"));
        await cut.InvokeAsync(() => authentication.Change(Principal("other")));
        cut.Render(p => p.Add(c => c.ObservatoryId, a.ObservatoryId));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("navigation page is no longer available"));
    }

    [TestMethod]
    [DataRow("invalid")]
    [DataRow("")]
    public async Task InvalidAndEmptyMembershipStates_AreExplicitAndNeverExposeDiagnostics(string token)
    {
        await using var context = CreateContext(out _);
        var cut = context.Render<OperationsScopeShell>(p => p.Add(c => c.ObservatoryPage, token));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(token.Length > 0
            ? "navigation page is no longer available" : "No accessible observatories"));
        cut.Markup.Should().NotContain("Current Sky").And.NotContain("Archive");
    }

    [TestMethod]
    public async Task ServiceOutage_ClearsLabelsAndShowsOnlySanitizedFailure()
    {
        await using var context = CreateContext(out _);
        context.Services.RemoveAll<INetworkOperationsReadService>();
        context.Services.AddScoped<INetworkOperationsReadService>(_ => throw new InvalidOperationException("password /private/storage"));
        var cut = context.Render<OperationsScopeShell>(p => p.AddChildContent("private child"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("temporarily unavailable")
            .And.NotContain("password").And.NotContain("/private").And.NotContain("private child"));
    }

    [TestMethod]
    public async Task ScopedCredential_NeverListsOtherMembershipsOrAcceptsTheirPaths()
    {
        await using var context = CreateContext(out var authentication);
        var a = await SeedAsync(context, "Allowed station", "Allowed camera");
        var b = await SeedAsync(context, "Other station", "Other camera");
        authentication.Change(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new(ClaimTypes.NameIdentifier, "member"), new("account_type", "User"),
            new(ApiKeyClaims.AuthenticationType, ApiKeyAuthenticationOptions.AuthenticationScheme),
            new(ApiKeyClaims.AccessLevel, nameof(ApiKeyAccessLevel.Read)),
            new(ApiKeyClaims.ObservatoryId, a.ObservatoryId.ToString("D"))
        ], ApiKeyAuthenticationOptions.AuthenticationScheme)));
        var cut = context.Render<OperationsScopeShell>();
        cut.WaitForAssertion(() => ScopeNames(cut, "Observatory").Should().Equal("Allowed station"));

        cut.Render(p => p.Add(c => c.ObservatoryId, b.ObservatoryId).Add(c => c.CameraId, b.CameraId));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Scope not found").And.NotContain("Other station").And.NotContain("Other camera"));
    }

    [TestMethod]
    public async Task SharedRoutePage_ResetsOmittedScopeWhenNavigatingBackToNetwork()
    {
        await using var context = CreateContext(out _);
        var a = await SeedAsync(context, "Station", "Camera");
        var cut = context.Render<OperationsWorkspace>(p => p.Add(c => c.ObservatoryId, a.ObservatoryId).Add(c => c.CameraId, a.CameraId));
        cut.WaitForAssertion(() => cut.Find("h1").TextContent.Should().Be("Camera"));
        cut.Render();
        cut.WaitForAssertion(() => cut.Find("h1").TextContent.Should().Be("Network"));
    }

    private static string[] ScopeNames(IRenderedComponent<OperationsScopeShell> cut, string kind)
        => cut.FindAll($"nav[aria-label='{kind} scopes'] > a").Select(a => a.TextContent).ToArray();

    private static string NextToken(IRenderedComponent<OperationsScopeShell> cut, string kind, string parameter)
    {
        var link = cut.FindAll(".scope-pagination a").Single(a => a.TextContent == $"Next {kind} page");
        return QueryHelpers.ParseQuery(new Uri(new Uri("https://test/"), link.GetAttribute("href")!).Query)[parameter].ToString();
    }

    private static BunitContext CreateContext(out MutableAuthentication authentication)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./Components/Shared/OperationsScopeShell.razor.js");
        authentication = new MutableAuthentication();
        context.Services.AddSingleton<AuthenticationStateProvider>(authentication);
        context.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        context.Services.AddSingleton(TimeProvider.System);
        var databaseName = Guid.NewGuid().ToString("N");
        context.Services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(databaseName));
        context.Services.AddScoped<INetworkOperationsReadService, NetworkOperationsReadService>();
        return context;
    }

    private static async Task<(Guid ObservatoryId, Guid CameraId)> SeedAsync(BunitContext context,
        string observatoryName, string cameraName, ObservatoryMembershipRole role = ObservatoryMembershipRole.Viewer,
        string userId = "member", int cameraCount = 1)
    {
        await using var scope = context.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (!await db.Users.AnyAsync(u => u.Id == userId)) db.Users.Add(new ApplicationUser { Id = userId, UserName = userId, AccountType = AccountType.User });
        var observatory = new Observatory { Name = observatoryName, OwnerUserId = userId };
        db.Observatories.Add(observatory);
        db.ObservatoryMemberships.Add(new ObservatoryMembership { ObservatoryId = observatory.Id, UserId = userId, Role = role });
        var cameraId = Guid.NewGuid();
        for (var i = 0; i < cameraCount; i++)
            db.LogicalCameras.Add(new LogicalCamera
            {
                Id = i == 0 ? cameraId : Guid.NewGuid(),
                ObservatoryId = observatory.Id,
                Name = cameraCount == 1 ? cameraName : $"Camera {i:D3}",
                CreatedByUserId = userId,
                Slug = $"camera-{i}"
            });
        await db.SaveChangesAsync();
        return (observatory.Id, cameraId);
    }

    private static ClaimsPrincipal Principal(string userId) => new(new ClaimsIdentity(
        [new(ClaimTypes.NameIdentifier, userId), new("account_type", "User")], IdentityConstants.ApplicationScheme));

    private sealed class MutableAuthentication : AuthenticationStateProvider
    {
        private ClaimsPrincipal principal = Principal("member");
        internal Task<AuthenticationState>? Pending { get; set; }
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Pending ?? Task.FromResult(new AuthenticationState(principal));
        internal void Change(ClaimsPrincipal value)
        {
            principal = value;
            NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        }
    }
}
