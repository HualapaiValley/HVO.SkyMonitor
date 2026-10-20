using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.OrbitalPlanning;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

/// <summary>Unit composition of the real host authorization, UI adapter and planner over retained local test inputs.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentSatellitePlanningUiIntegrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("missing-name")]
    [DataRow("999999")]
    [DataRow("/private/missing?password=do-not-expose")]
    public async Task AuthenticatedUnmatchedFilterKeepsSnapshotLimitedNoMatchesAsync(string filter)
    {
        var input = new PlanningTestInputs(realEngine: true);
        var request = await input.RequestAsync(filter).ConfigureAwait(false);
        var authentication = new Authentication();
        using var services = CreateServices(authentication, input.Planner);
        using var scope = services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ICameraAgentSatellitePlanningUiService>();
        var result = await service.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind, result.Message);
        Assert.IsNotNull(result.Value);
        Assert.AreEqual(CameraAgentSatellitePlanningOutcome.NoMatches, result.Value.Outcome);
        Assert.IsNull(result.Value.Value);
        Assert.AreEqual("No matching objects in this snapshot (matching 0; selected 0). This does not establish global absence.", result.Value.Message);
        Assert.DoesNotContain(filter, result.Value.Message, StringComparison.Ordinal);
        Assert.AreEqual(1, input.Snapshots.Reads);
    }

    [TestMethod]
    [DataRow(256, CameraAgentSatellitePlanningOutcome.NoMatches)]
    [DataRow(257, CameraAgentSatellitePlanningOutcome.Invalid)]
    public async Task AuthenticatedFilterLengthBoundaryKeepsItsTypedOutcomeAsync(int length, CameraAgentSatellitePlanningOutcome expected)
    {
        var input = new PlanningTestInputs();
        var request = await input.RequestAsync(new string('x', length)).ConfigureAwait(false);
        using var services = CreateServices(new Authentication(), input.Planner);
        using var scope = services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ICameraAgentSatellitePlanningUiService>()
            .SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OperatorUiResultKind.Success, result.Kind, result.Message);
        Assert.IsNotNull(result.Value);
        Assert.AreEqual(expected, result.Value.Outcome);
        Assert.AreEqual(length == 256 ? 1 : 0, input.Snapshots.Reads);
        Assert.AreEqual(0, input.Engine.Searches);
        if (expected == CameraAgentSatellitePlanningOutcome.Invalid)
            Assert.AreEqual("The planning request is outside the supported bounds.", result.Value.Message);
    }

    [TestMethod]
    public async Task DeniedSearchAndArcDoNotReachTheRealPlannerAsync()
    {
        var input = new PlanningTestInputs();
        var request = await input.RequestAsync("missing-name").ConfigureAwait(false);
        var authentication = new Authentication { User = new(new ClaimsIdentity()) };
        using var services = CreateServices(authentication, input.Planner);
        using var scope = services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ICameraAgentSatellitePlanningUiService>();
        var search = await service.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        var arc = await service.GetArcAsync(new string('A', 64), new string('B', 64), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, search.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, arc.Kind);
        Assert.AreEqual(0, input.Snapshots.Reads);
        Assert.AreEqual(0, input.Engine.Searches);
        Assert.IsNull(input.Engine.LastArc);
    }

    [TestMethod]
    public async Task MissingPlannerRemainsUnavailableOnlyAfterAuthorizationAsync()
    {
        var input = new PlanningTestInputs();
        var request = await input.RequestAsync("missing-name").ConfigureAwait(false);
        var authentication = new Authentication();
        using var services = CreateServices(authentication, planner: null);
        using var scope = services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ICameraAgentSatellitePlanningUiService>();
        var availableOwner = await service.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OperatorUiResultKind.Unavailable, availableOwner.Kind);
        authentication.User = new(new ClaimsIdentity());
        var denied = await service.SearchAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, denied.Kind);
        Assert.AreEqual(0, input.Snapshots.Reads);
    }

    private static ServiceProvider CreateServices(Authentication authentication, ICameraAgentSatellitePlanner? planner)
    {
        var manager = new Mock<UserManager<ApplicationUser>>(Mock.Of<IUserStore<ApplicationUser>>(), Options.Create(new IdentityOptions()),
            new PasswordHasher<ApplicationUser>(), Array.Empty<IUserValidator<ApplicationUser>>(), Array.Empty<IPasswordValidator<ApplicationUser>>(),
            new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), Mock.Of<IServiceProvider>(), NullLogger<UserManager<ApplicationUser>>.Instance);
        manager.SetupGet(value => value.SupportsUserSecurityStamp).Returns(true);
        manager.Setup(value => value.FindByIdAsync("planning-owner")).ReturnsAsync(new ApplicationUser
        {
            Id = "planning-owner",
            IsSiteOwner = true,
            PasswordChangeRequired = false,
            NormalizedEmail = "PLANNING@CAMERAAGENT.TEST",
            SecurityStamp = "planning-stamp"
        });
        manager.Setup(value => value.GetSecurityStampAsync(It.IsAny<ApplicationUser>())).ReturnsAsync("planning-stamp");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(manager.Object);
        services.AddSingleton<ILookupNormalizer, UpperInvariantLookupNormalizer>();
        services.AddSingleton(Options.Create(new LocalIdentityOptions { AdminEmail = "planning@cameraagent.test" }));
        services.AddSingleton<AuthenticationStateProvider>(authentication);
        services.AddCameraAgentAuthorization();
        if (planner is not null) services.AddSingleton(planner);
        services.AddCameraAgentSatellitePlanningUi();
        return services.BuildServiceProvider();
    }

    private sealed class Authentication : AuthenticationStateProvider
    {
        internal ClaimsPrincipal User { get; set; } = new(new ClaimsIdentity(
        [
            new(ClaimTypes.NameIdentifier, "planning-owner"),
            new(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType),
            new(new IdentityOptions().ClaimsIdentity.SecurityStampClaimType, "planning-stamp")
        ], IdentityConstants.ApplicationScheme));

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(User));
    }
}
