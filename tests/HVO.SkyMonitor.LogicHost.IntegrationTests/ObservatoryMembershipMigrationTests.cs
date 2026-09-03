using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class ObservatoryMembershipMigrationTests
{
    [TestMethod]
    public async Task CleanAndRepeatedMigration_ProduceNoPendingModelChanges()
    {
        await using var database = CreateDatabase("Clean");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);

            (await database.Context.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).Should().BeEmpty();

            var owner = new ApplicationUser
            {
                Id = "audit-owner",
                UserName = "audit-owner",
                AccountType = AccountType.User
            };
            var observatory = new Observatory
            {
                OwnerUserId = owner.Id,
                Name = "Audit observatory",
                TimeZoneId = "UTC",
                CreatedAtUtc = DateTimeOffset.UnixEpoch,
                IsActive = true
            };
            var audit = new ObservatoryMembershipAudit
            {
                Observatory = observatory,
                ObservatoryId = observatory.Id,
                TargetUserId = owner.Id,
                ActorUserId = owner.Id,
                Action = ObservatoryMembershipAuditAction.Granted,
                NewRole = ObservatoryMembershipRole.Owner,
                ReasonCode = "current-schema-test",
                OccurredAtUtc = DateTimeOffset.UnixEpoch
            };
            database.Context.AddRange(owner, observatory, audit);
            await database.Context.SaveChangesAsync().ConfigureAwait(false);

            Func<Task> updateAudit = () => database.Context.Database.ExecuteSqlRawAsync(
                "UPDATE [ObservatoryMembershipAudits] SET [ReasonCode] = N'changed'");
            Func<Task> deleteAudit = () => database.Context.Database.ExecuteSqlRawAsync(
                "DELETE FROM [ObservatoryMembershipAudits]");
            await updateAudit.Should().ThrowAsync<SqlException>().WithMessage("*immutable*").ConfigureAwait(false);
            await deleteAudit.Should().ThrowAsync<SqlException>().WithMessage("*immutable*").ConfigureAwait(false);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task ConcurrentOwnerRemoval_LeavesExactlyOneOwner()
    {
        await using var database = CreateDatabase("Concurrency");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var observatory = new Observatory
            {
                OwnerUserId = "owner-a",
                Name = "Concurrent owners",
                TimeZoneId = "UTC",
                CreatedAtUtc = DateTimeOffset.UnixEpoch,
                IsActive = true
            };
            database.Context.Users.AddRange(
                new ApplicationUser { Id = "owner-a", UserName = "owner-a", AccountType = AccountType.User },
                new ApplicationUser { Id = "owner-b", UserName = "owner-b", AccountType = AccountType.User });
            database.Context.Observatories.Add(observatory);
            database.Context.ObservatoryMemberships.AddRange(
                new ObservatoryMembership
                {
                    Observatory = observatory,
                    ObservatoryId = observatory.Id,
                    UserId = "owner-a",
                    Role = ObservatoryMembershipRole.Owner,
                    AddedAtUtc = DateTimeOffset.UnixEpoch
                },
                new ObservatoryMembership
                {
                    Observatory = observatory,
                    ObservatoryId = observatory.Id,
                    UserId = "owner-b",
                    Role = ObservatoryMembershipRole.Owner,
                    AddedAtUtc = DateTimeOffset.UnixEpoch
                });
            await database.Context.SaveChangesAsync().ConfigureAwait(false);

            await using var firstContext = CreateContext(database.ConnectionString);
            await using var secondContext = CreateContext(database.ConnectionString);
            var first = new ObservatoryMembershipService(firstContext, TimeProvider.System);
            var second = new ObservatoryMembershipService(secondContext, TimeProvider.System);
            var firstTask = first.RemoveAsync(observatory.Id, "owner-a", "owner-a");
            var secondTask = second.RemoveAsync(observatory.Id, "owner-b", "owner-b");

            var results = await Task.WhenAll(firstTask, secondTask).ConfigureAwait(false);

            results.Should().ContainSingle(item => item.Outcome == ObservatoryMembershipMutationOutcome.Applied);
            results.Should().ContainSingle(item => item.Outcome == ObservatoryMembershipMutationOutcome.LastOwner);
            database.Context.ChangeTracker.Clear();
            var owners = await database.Context.ObservatoryMemberships.AsNoTracking()
                .Where(item => item.ObservatoryId == observatory.Id
                    && item.Role == ObservatoryMembershipRole.Owner)
                .ToListAsync().ConfigureAwait(false);
            owners.Should().ContainSingle();
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private static MigrationDatabase CreateDatabase(string scenario)
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorMembership{scenario}_{Guid.NewGuid():N}"
        };
        return new MigrationDatabase(CreateContext(builder.ConnectionString), builder.ConnectionString);
    }

    private static ApplicationDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new ApplicationDbContext(options);
    }

    private sealed class MigrationDatabase(ApplicationDbContext context, string connectionString) : IAsyncDisposable
    {
        public ApplicationDbContext Context { get; } = context;
        public string ConnectionString { get; } = connectionString;
        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }
}
