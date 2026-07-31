using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class AccountDeletionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 1, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task DeleteAsync_RejectsSoleOwnerWithoutChangingAccountOrMembership()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = AddUser(context, "sole-owner");
        var observatory = AddObservatory(context, user.Id);
        AddMembership(context, observatory, user.Id, ObservatoryMembershipRole.Owner);
        await context.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<IAccountDeletionService>();

        var result = await service.DeleteAsync(user);

        result.Outcome.Should().Be(AccountDeletionOutcome.LastOwner);
        (await context.Users.AnyAsync(item => item.Id == user.Id)).Should().BeTrue();
        (await context.ObservatoryMemberships.AnyAsync(item => item.UserId == user.Id)).Should().BeTrue();
        context.ObservatoryMembershipAudits.Should().BeEmpty();
    }

    [TestMethod]
    public async Task DeleteAsync_RemovesMembershipsAndRetainsAuditWhenOwnershipRemains()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var departing = AddUser(context, "departing");
        var remaining = AddUser(context, "remaining");
        var viewedObservatory = AddObservatory(context, remaining.Id, "Viewed");
        var ownedObservatory = AddObservatory(context, departing.Id, "Owned");
        AddMembership(context, viewedObservatory, departing.Id, ObservatoryMembershipRole.Viewer);
        AddMembership(context, viewedObservatory, remaining.Id, ObservatoryMembershipRole.Owner);
        AddMembership(context, ownedObservatory, departing.Id, ObservatoryMembershipRole.Owner);
        AddMembership(context, ownedObservatory, remaining.Id, ObservatoryMembershipRole.Owner);
        await context.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<IAccountDeletionService>();

        var result = await service.DeleteAsync(departing);

        result.Outcome.Should().Be(AccountDeletionOutcome.Deleted);
        (await context.Users.AnyAsync(item => item.Id == departing.Id)).Should().BeFalse();
        (await context.ObservatoryMemberships.AnyAsync(item => item.UserId == departing.Id)).Should().BeFalse();
        var audits = await context.ObservatoryMembershipAudits
            .Where(item => item.TargetUserId == departing.Id)
            .ToListAsync();
        audits.Should().HaveCount(2).And.OnlyContain(item =>
            item.Action == ObservatoryMembershipAuditAction.Removed
            && item.ActorUserId == departing.Id
            && item.ReasonCode == "account-deleted"
            && item.OccurredAtUtc == Now);
    }

    [TestMethod]
    public async Task DeleteAsync_RemovesSoleOwnerMembershipFromInactiveObservatory()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = AddUser(context, "inactive-owner");
        var observatory = AddObservatory(context, user.Id);
        observatory.IsActive = false;
        AddMembership(context, observatory, user.Id, ObservatoryMembershipRole.Owner);
        await context.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<IAccountDeletionService>();

        var result = await service.DeleteAsync(user);

        result.Outcome.Should().Be(AccountDeletionOutcome.Deleted);
        (await context.Users.AnyAsync(item => item.Id == user.Id)).Should().BeFalse();
        (await context.ObservatoryMemberships.AnyAsync(item => item.UserId == user.Id)).Should().BeFalse();
        (await context.Observatories.AnyAsync(item => item.Id == observatory.Id)).Should().BeTrue();
        var audit = await context.ObservatoryMembershipAudits.SingleAsync();
        audit.Should().Match<ObservatoryMembershipAudit>(item =>
            item.ObservatoryId == observatory.Id
            && item.TargetUserId == user.Id
            && item.Action == ObservatoryMembershipAuditAction.Removed
            && item.ReasonCode == "account-deleted");
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddIdentityCore<ApplicationUser>()
            .AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider());
        services.AddScoped<IAccountDeletionService, AccountDeletionService>();
        return services.BuildServiceProvider();
    }

    private static ApplicationUser AddUser(ApplicationDbContext context, string userId)
    {
        var user = new ApplicationUser
        {
            Id = userId,
            UserName = userId,
            NormalizedUserName = userId.ToUpperInvariant(),
            AccountType = AccountType.User
        };
        context.Users.Add(user);
        return user;
    }

    private static Observatory AddObservatory(
        ApplicationDbContext context,
        string historicalOwnerId,
        string name = "Observatory")
    {
        var observatory = new Observatory
        {
            OwnerUserId = historicalOwnerId,
            Name = name,
            TimeZoneId = "UTC",
            CreatedAtUtc = Now,
            IsActive = true
        };
        context.Observatories.Add(observatory);
        return observatory;
    }

    private static void AddMembership(
        ApplicationDbContext context,
        Observatory observatory,
        string userId,
        ObservatoryMembershipRole role)
        => context.ObservatoryMemberships.Add(new ObservatoryMembership
        {
            Observatory = observatory,
            ObservatoryId = observatory.Id,
            UserId = userId,
            Role = role,
            AddedAtUtc = Now
        });

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
