using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class ObservatoryMembershipServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task SetRoleAndRemove_ApplyOneAuditedTransitionAndTreatReplayAsUnchanged()
    {
        await using var context = CreateContext();
        var observatory = SeedObservatory(context, "owner", "member");
        var service = CreateService(context);

        var granted = await service.SetRoleAsync(
            observatory.Id, "owner", "member", ObservatoryMembershipRole.Viewer).ConfigureAwait(false);
        var replayed = await service.SetRoleAsync(
            observatory.Id, "owner", "member", ObservatoryMembershipRole.Viewer).ConfigureAwait(false);
        var changed = await service.SetRoleAsync(
            observatory.Id, "owner", "member", ObservatoryMembershipRole.Manager).ConfigureAwait(false);
        var removed = await service.RemoveAsync(observatory.Id, "owner", "member").ConfigureAwait(false);

        granted.Should().Be(new ObservatoryMembershipMutationResult(
            ObservatoryMembershipMutationOutcome.Applied, ObservatoryMembershipRole.Viewer));
        replayed.Should().Be(new ObservatoryMembershipMutationResult(
            ObservatoryMembershipMutationOutcome.Unchanged, ObservatoryMembershipRole.Viewer));
        changed.Should().Be(new ObservatoryMembershipMutationResult(
            ObservatoryMembershipMutationOutcome.Applied, ObservatoryMembershipRole.Manager));
        removed.Should().Be(new ObservatoryMembershipMutationResult(
            ObservatoryMembershipMutationOutcome.Applied));
        (await context.ObservatoryMemberships.AnyAsync(item => item.UserId == "member").ConfigureAwait(false))
            .Should().BeFalse();
        var audits = await context.ObservatoryMembershipAudits
            .Where(item => item.TargetUserId == "member")
            .OrderBy(item => item.Action)
            .ToListAsync().ConfigureAwait(false);
        audits.Should().HaveCount(3);
        audits.Should().ContainSingle(item => item.Action == ObservatoryMembershipAuditAction.Granted
            && item.PreviousRole == null && item.NewRole == ObservatoryMembershipRole.Viewer);
        audits.Should().ContainSingle(item => item.Action == ObservatoryMembershipAuditAction.RoleChanged
            && item.PreviousRole == ObservatoryMembershipRole.Viewer
            && item.NewRole == ObservatoryMembershipRole.Manager);
        audits.Should().ContainSingle(item => item.Action == ObservatoryMembershipAuditAction.Removed
            && item.PreviousRole == ObservatoryMembershipRole.Manager && item.NewRole == null);
    }

    [TestMethod]
    public async Task LastOwner_CannotBeDemotedOrRemoved_ButSecondOwnerAllowsRemoval()
    {
        await using var context = CreateContext();
        var observatory = SeedObservatory(context, "owner", "second-owner");
        var service = CreateService(context);

        var demoted = await service.SetRoleAsync(
            observatory.Id, "owner", "owner", ObservatoryMembershipRole.Manager).ConfigureAwait(false);
        var removed = await service.RemoveAsync(observatory.Id, "owner", "owner").ConfigureAwait(false);
        var secondGranted = await service.SetRoleAsync(
            observatory.Id, "owner", "second-owner", ObservatoryMembershipRole.Owner).ConfigureAwait(false);
        var firstRemoved = await service.RemoveAsync(observatory.Id, "owner", "owner").ConfigureAwait(false);

        demoted.Outcome.Should().Be(ObservatoryMembershipMutationOutcome.LastOwner);
        removed.Outcome.Should().Be(ObservatoryMembershipMutationOutcome.LastOwner);
        secondGranted.Outcome.Should().Be(ObservatoryMembershipMutationOutcome.Applied);
        firstRemoved.Outcome.Should().Be(ObservatoryMembershipMutationOutcome.Applied);
        var remaining = await context.ObservatoryMemberships
            .Where(item => item.ObservatoryId == observatory.Id)
            .ToListAsync().ConfigureAwait(false);
        remaining.Should().ContainSingle(item => item.UserId == "second-owner"
            && item.Role == ObservatoryMembershipRole.Owner);
    }

    [TestMethod]
    public async Task Mutation_RequiresLiveHumanOwnerAndTarget()
    {
        await using var context = CreateContext();
        var observatory = SeedObservatory(context, "owner", "manager", "system");
        context.Users.Single(item => item.Id == "system").AccountType = AccountType.System;
        context.ObservatoryMemberships.Add(new ObservatoryMembership
        {
            ObservatoryId = observatory.Id,
            UserId = "manager",
            Role = ObservatoryMembershipRole.Manager,
            AddedAtUtc = Now
        });
        await context.SaveChangesAsync().ConfigureAwait(false);
        var service = CreateService(context);

        var managerAttempt = await service.SetRoleAsync(
            observatory.Id, "manager", "manager", ObservatoryMembershipRole.Owner).ConfigureAwait(false);
        var systemAttempt = await service.SetRoleAsync(
            observatory.Id, "owner", "system", ObservatoryMembershipRole.Viewer).ConfigureAwait(false);
        var missingAttempt = await service.SetRoleAsync(
            observatory.Id, "owner", "missing", ObservatoryMembershipRole.Viewer).ConfigureAwait(false);

        managerAttempt.Outcome.Should().Be(ObservatoryMembershipMutationOutcome.NotFoundOrDenied);
        systemAttempt.Outcome.Should().Be(ObservatoryMembershipMutationOutcome.TargetUserNotFound);
        missingAttempt.Outcome.Should().Be(ObservatoryMembershipMutationOutcome.TargetUserNotFound);
        (await context.ObservatoryMembershipAudits.CountAsync().ConfigureAwait(false)).Should().Be(0);
        (await service.GetRoleAsync(observatory.Id, "system").ConfigureAwait(false)).Should().BeNull();
        (await service.ListAsync(observatory.Id, "manager", 50, null).ConfigureAwait(false)).Items.Should().BeEmpty();
    }

    [TestMethod]
    public async Task SaveChanges_RejectsMembershipAuditMutationAndDeletion()
    {
        await using var context = CreateContext();
        var observatory = SeedObservatory(context, "owner", "member");
        var service = CreateService(context);
        _ = await service.SetRoleAsync(
            observatory.Id, "owner", "member", ObservatoryMembershipRole.Viewer).ConfigureAwait(false);
        var audit = await context.ObservatoryMembershipAudits.SingleAsync().ConfigureAwait(false);

        audit.ReasonCode = "changed";
        Func<Task> mutate = () => context.SaveChangesAsync();

        await mutate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*immutable*").ConfigureAwait(false);

        context.Entry(audit).State = EntityState.Unchanged;
        context.ObservatoryMembershipAudits.Remove(audit);
        Func<Task> delete = () => context.SaveChangesAsync();

        await delete.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*immutable*").ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ListAsync_UsesStableBoundedPagesAndRejectsMalformedCursor()
    {
        await using var context = CreateContext();
        var userIds = Enumerable.Range(0, 55).Select(index => $"member-{index:D2}").ToArray();
        var observatory = SeedObservatory(context, "owner", userIds);
        context.ObservatoryMemberships.AddRange(userIds.Select(userId => new ObservatoryMembership
        {
            ObservatoryId = observatory.Id,
            UserId = userId,
            Role = ObservatoryMembershipRole.Viewer,
            AddedAtUtc = Now
        }));
        await context.SaveChangesAsync().ConfigureAwait(false);
        var service = CreateService(context);

        var first = await service.ListAsync(observatory.Id, "owner", 50, null).ConfigureAwait(false);
        var second = await service.ListAsync(observatory.Id, "owner", 50, first.NextCursor).ConfigureAwait(false);

        first.Items.Should().HaveCount(50);
        first.NextCursor.Should().NotBeNull();
        second.Items.Should().HaveCount(6);
        second.NextCursor.Should().BeNull();
        first.Items.Concat(second.Items).Select(item => item.UserId)
            .Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        Func<Task> malformed = () => service.ListAsync(observatory.Id, "owner", 50, "not-base64");
        await malformed.Should().ThrowAsync<ArgumentException>().WithParameterName("cursor").ConfigureAwait(false);
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static Observatory SeedObservatory(ApplicationDbContext context, string ownerId, params string[] userIds)
    {
        context.Users.Add(new ApplicationUser { Id = ownerId, UserName = ownerId, AccountType = AccountType.User });
        foreach (var userId in userIds)
        {
            context.Users.Add(new ApplicationUser { Id = userId, UserName = userId, AccountType = AccountType.User });
        }
        var observatory = new Observatory
        {
            OwnerUserId = ownerId,
            Name = "Membership test observatory",
            TimeZoneId = "UTC",
            CreatedAtUtc = Now,
            IsActive = true
        };
        context.Observatories.Add(observatory);
        context.ObservatoryMemberships.Add(new ObservatoryMembership
        {
            ObservatoryId = observatory.Id,
            UserId = ownerId,
            Role = ObservatoryMembershipRole.Owner,
            AddedAtUtc = Now
        });
        context.SaveChanges();
        return observatory;
    }

    private static ObservatoryMembershipService CreateService(ApplicationDbContext context)
        => new(context, new FixedTimeProvider());

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
