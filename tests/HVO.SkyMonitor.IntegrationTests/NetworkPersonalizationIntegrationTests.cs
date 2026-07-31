using FluentAssertions;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class NetworkPersonalizationIntegrationTests
{
    [TestMethod]
    public async Task SqlServer_FollowsAndCuratedHomeRemainUserScopedRoleBoundAndImmutable()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        var marker = Guid.NewGuid().ToString("N");
        var owner = await db.Users.SingleAsync(user => user.Email == TestUsers.Operator.Email).ConfigureAwait(false);
        var registeredUser = new ApplicationUser
        {
            UserName = $"network-user-{marker}",
            Email = $"network-user-{marker}@example.test",
            EmailConfirmed = true,
            AccountType = AccountType.User
        };
        var editor = new ApplicationUser
        {
            UserName = $"network-editor-{marker}",
            Email = $"network-editor-{marker}@example.test",
            EmailConfirmed = true,
            AccountType = AccountType.User
        };
        (await userManager.CreateAsync(registeredUser)).Succeeded.Should().BeTrue();
        (await userManager.CreateAsync(editor)).Succeeded.Should().BeTrue();
        if (!await roleManager.RoleExistsAsync(AuthorizationRoleNames.PlatformEditor).ConfigureAwait(false))
        {
            (await roleManager.CreateAsync(new IdentityRole(AuthorizationRoleNames.PlatformEditor)))
                .Succeeded.Should().BeTrue();
        }
        (await userManager.AddToRoleAsync(editor, AuthorizationRoleNames.PlatformEditor)).Succeeded.Should().BeTrue();

        var first = await CreatePublicObservatoryAsync(services, owner.Id, marker, "first").ConfigureAwait(false);
        var second = await CreatePublicObservatoryAsync(services, owner.Id, marker, "second").ConfigureAwait(false);
        var personalization = services.GetRequiredService<IRegisteredUserPersonalizationService>();

        (await personalization.FollowAsync(registeredUser.Id, first.Slug).ConfigureAwait(false))
            .Should().Be(PersonalizationMutationOutcome.Applied);
        (await personalization.FollowAsync(registeredUser.Id, second.Slug).ConfigureAwait(false))
            .Should().Be(PersonalizationMutationOutcome.Applied);
        (await personalization.UnfollowAsync(registeredUser.Id, first.Slug).ConfigureAwait(false))
            .Should().Be(PersonalizationMutationOutcome.Applied);
        (await personalization.UnfollowAsync(registeredUser.Id, first.Slug).ConfigureAwait(false))
            .Should().Be(PersonalizationMutationOutcome.Unchanged);
        db.ChangeTracker.Clear();
        (await db.RegisteredUserObservatoryFollows.AsNoTracking()
            .Where(item => item.UserId == registeredUser.Id)
            .Select(item => item.ObservatoryId)
            .ToArrayAsync().ConfigureAwait(false)).Should().Equal(second.Id);

        var connectionString = db.Database.GetConnectionString()!;
        var contextOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString).Options;
        await using var firstContext = new ApplicationDbContext(contextOptions);
        await using var secondContext = new ApplicationDbContext(contextOptions);
        var concurrentResults = await Task.WhenAll(
            new RegisteredUserPersonalizationService(firstContext, TimeProvider.System)
                .FollowAsync(registeredUser.Id, first.Slug),
            new RegisteredUserPersonalizationService(secondContext, TimeProvider.System)
                .FollowAsync(registeredUser.Id, first.Slug)).ConfigureAwait(false);
        concurrentResults.Should().ContainSingle(item => item == PersonalizationMutationOutcome.Applied);
        concurrentResults.Should().ContainSingle(item => item == PersonalizationMutationOutcome.Unchanged);
        db.ChangeTracker.Clear();
        (await db.RegisteredUserObservatoryFollows.AsNoTracking().CountAsync(item =>
            item.UserId == registeredUser.Id && item.ObservatoryId == first.Id).ConfigureAwait(false)).Should().Be(1);

        var curation = services.GetRequiredService<ICuratedPublicPlacementService>();
        (await curation.DecideObservatoryAsync(
            registeredUser.Id, first.Slug, CuratedPlacementState.Suppressed, null, "not-an-editor")
            .ConfigureAwait(false)).Should().Be(CuratedPlacementOutcome.NotFoundOrDenied);
        (await curation.DecideObservatoryAsync(
            editor.Id, first.Slug, CuratedPlacementState.Suppressed, null, "editor-suppressed")
            .ConfigureAwait(false)).Should().Be(CuratedPlacementOutcome.Applied);
        (await curation.DecideObservatoryAsync(
            editor.Id, second.Slug, CuratedPlacementState.Featured, 0, "editor-featured")
            .ConfigureAwait(false)).Should().Be(CuratedPlacementOutcome.Applied);

        var home = await services.GetRequiredService<IPublicNetworkReadService>().GetHomeAsync(50)
            .ConfigureAwait(false);
        home.Observatories.Should().NotContain(item => item.Slug == first.Slug);
        home.Observatories.Should().Contain(item => item.Slug == second.Slug);
        home.Observatories[0].Slug.Should().Be(second.Slug);

        var placementId = await db.CuratedPublicPlacementDecisions.AsNoTracking()
            .Where(item => item.ObservatoryId == second.Id)
            .Select(item => item.Id)
            .SingleAsync().ConfigureAwait(false);
        Func<Task> mutate = () => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [CuratedPublicPlacementDecisions] SET [ReasonCode] = N'changed' WHERE [Id] = {placementId}");
        await mutate.Should().ThrowAsync<SqlException>().WithMessage("*immutable*").ConfigureAwait(false);
    }

    private static async Task<(Guid Id, string Slug)> CreatePublicObservatoryAsync(
        IServiceProvider services,
        string ownerUserId,
        string marker,
        string suffix)
    {
        var observatory = await services.GetRequiredService<IObservatoryService>().CreateOrUpdateAsync(
            new ObservatoryUpsertRequest(
                null,
                ownerUserId,
                $"Network {suffix} {marker}",
                19.8,
                -155.4,
                4200,
                "Pacific/Honolulu",
                true)).ConfigureAwait(false);
        var slug = $"network-{suffix}-{marker}";
        var publication = services.GetRequiredService<IObservatoryPublicationService>();
        (await publication.SetProfileAsync(
            observatory.Id,
            ownerUserId,
            new ObservatoryPublicationProfileRequest(
                slug,
                $"Network {suffix}",
                "Public integration station",
                ObservatoryProfileVisibility.Public,
                false,
                false,
                "integration-release")).ConfigureAwait(false)).Outcome
            .Should().Be(ObservatoryPublicationMutationOutcome.Applied);
        (await publication.SetLocationDisclosureAsync(
            observatory.Id,
            ownerUserId,
            new ObservatoryLocationDisclosureRequest(
                ObservatoryLocationDisclosureLevel.Region,
                "US-HI",
                "Hawaii",
                null,
                null,
                null,
                "integration-release")).ConfigureAwait(false)).Outcome
            .Should().Be(ObservatoryPublicationMutationOutcome.Applied);
        return (observatory.Id, slug);
    }
}
