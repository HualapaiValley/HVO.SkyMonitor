using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class ObservatoryMembershipMigrationTests
{
    private const string PreviousMigration = "20260726234621_RefreshAnnotationV3DerivativeIdentity";

    [TestMethod]
    public async Task Upgrade_BackfillsOnlyLiveHumanOwnersAndRetainsRejectedAuditEvidence()
    {
        await using var database = CreateDatabase("Backfill");
        try
        {
            var migrator = database.Context.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration).ConfigureAwait(false);
            await InsertUserAsync(database.Context, "human-owner", AccountType.User).ConfigureAwait(false);
            await InsertUserAsync(database.Context, "system-owner", AccountType.System).ConfigureAwait(false);
            var humanObservatory = await InsertObservatoryAsync(database.Context, "human-owner").ConfigureAwait(false);
            var systemObservatory = await InsertObservatoryAsync(database.Context, "system-owner").ConfigureAwait(false);
            var missingObservatory = await InsertObservatoryAsync(database.Context, "missing-owner").ConfigureAwait(false);

            await migrator.MigrateAsync().ConfigureAwait(false);

            var memberships = await database.Context.ObservatoryMemberships.AsNoTracking().ToListAsync()
                .ConfigureAwait(false);
            memberships.Should().ContainSingle(item => item.ObservatoryId == humanObservatory
                && item.UserId == "human-owner"
                && item.Role == ObservatoryMembershipRole.Owner);
            var audits = await database.Context.ObservatoryMembershipAudits.AsNoTracking().ToListAsync()
                .ConfigureAwait(false);
            audits.Should().HaveCount(3);
            audits.Should().ContainSingle(item => item.ObservatoryId == humanObservatory
                && item.Action == ObservatoryMembershipAuditAction.LegacyBackfilled
                && item.NewRole == ObservatoryMembershipRole.Owner
                && item.ReasonCode == "legacy-owner-backfill");
            audits.Should().ContainSingle(item => item.ObservatoryId == systemObservatory
                && item.Action == ObservatoryMembershipAuditAction.LegacyRejected
                && item.ReasonCode == "legacy-owner-not-human");
            audits.Should().ContainSingle(item => item.ObservatoryId == missingObservatory
                && item.Action == ObservatoryMembershipAuditAction.LegacyRejected
                && item.ReasonCode == "legacy-owner-user-missing");
            (await database.Context.Observatories.AsNoTracking()
                .ToDictionaryAsync(item => item.Id, item => item.OwnerUserId).ConfigureAwait(false))
                .Should().Contain(new Dictionary<Guid, string>
                {
                    [humanObservatory] = "human-owner",
                    [systemObservatory] = "system-owner",
                    [missingObservatory] = "missing-owner"
                });

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
    public async Task CleanAndRepeatedMigration_ProduceNoPendingModelChanges()
    {
        await using var database = CreateDatabase("Clean");
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);

            (await database.Context.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).Should().BeEmpty();
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CaptureInstallationBackfill_UsesExclusiveRetirementBoundary()
    {
        await using var database = CreateDatabase("InstallationBoundary");
        try
        {
            var migrator = database.Context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260730030317_AddPublicCurationAndPersonalization")
                .ConfigureAwait(false);
            await InsertUserAsync(database.Context, "installation-owner", AccountType.User).ConfigureAwait(false);
            var observatoryId = await InsertObservatoryAsync(database.Context, "installation-owner")
                .ConfigureAwait(false);
            var registrationId = Guid.NewGuid();
            var devicePublicId = Guid.NewGuid();
            var logicalCameraId = Guid.NewGuid();
            var installationId = Guid.NewGuid();
            var beforeFrameId = Guid.NewGuid();
            var boundaryFrameId = Guid.NewGuid();
            var boundary = DateTimeOffset.UnixEpoch.AddDays(2);
            await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [DeviceRegistrations]
                    ([Id], [DeviceId], [ObservatoryId], [FriendlyName], [ObservatoryName],
                     [ObservatoryLatitudeDegrees], [ObservatoryLongitudeDegrees], [ObservatoryElevationMeters],
                     [ObservatoryTimeZoneId], [OwnerUserId], [OwnerDisplayName], [OwnerConfirmationMethod],
                     [Status], [VerificationCodeHash], [DevicePublicId], [IssuedAtUtc])
                VALUES
                    ({registrationId}, {"installation-agent"}, {observatoryId}, {"Installation camera"},
                     {"Installation observatory"}, {35d}, {-113d}, {500d}, {"UTC"}, {"installation-owner"},
                     {"Installation owner"}, {"SelfAttested"}, {"Active"}, {new string('A', 64)},
                     {devicePublicId}, {DateTimeOffset.UnixEpoch});

                INSERT INTO [LogicalCameras]
                    ([Id], [ObservatoryId], [Slug], [Name], [Description], [CreatedAtUtc], [CreatedByUserId])
                VALUES
                    ({logicalCameraId}, {observatoryId}, {"installation-camera"}, {"Installation camera"},
                     {"Migration boundary fixture"}, {DateTimeOffset.UnixEpoch}, {"installation-owner"});

                INSERT INTO [LogicalCameraInstallations]
                    ([Id], [LogicalCameraId], [RegistrationId], [InstallationPublicId], [AssignedAtUtc],
                     [RetiredAtUtc], [AssignedByUserId], [RetiredByUserId], [AssignmentReasonCode],
                     [RetirementReasonCode])
                VALUES
                    ({installationId}, {logicalCameraId}, {registrationId}, {Guid.NewGuid()},
                     {DateTimeOffset.UnixEpoch}, {boundary}, {"installation-owner"}, {"installation-owner"},
                     {"migration-fixture"}, {"replacement"});

                INSERT INTO [CentralFrames]
                    ([Id], [RegistrationId], [DevicePublicId], [ObservatoryId], [AgentId], [FrameId],
                     [CapturedAtUtc], [FirstReceivedAtUtc], [RigProfileVersion], [SceneProvenanceJson])
                VALUES
                    ({beforeFrameId}, {registrationId}, {devicePublicId}, {observatoryId}, {"installation-agent"},
                     {Guid.NewGuid()}, {boundary.AddTicks(-1)}, {boundary}, NULL, NULL),
                    ({boundaryFrameId}, {registrationId}, {devicePublicId}, {observatoryId}, {"installation-agent"},
                     {Guid.NewGuid()}, {boundary}, {boundary}, NULL, NULL);
                """).ConfigureAwait(false);

            await migrator.MigrateAsync().ConfigureAwait(false);
            database.Context.ChangeTracker.Clear();

            (await database.Context.CentralFrames.SingleAsync(item => item.Id == beforeFrameId)
                .ConfigureAwait(false)).LogicalCameraInstallationId.Should().Be(installationId);
            (await database.Context.CentralFrames.SingleAsync(item => item.Id == boundaryFrameId)
                .ConfigureAwait(false)).LogicalCameraInstallationId.Should().BeNull();
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

    private static Task<int> InsertUserAsync(ApplicationDbContext context, string id, AccountType accountType)
        => context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [AspNetUsers]
                ([Id], [AccountType], [UserName], [EmailConfirmed], [PhoneNumberConfirmed],
                 [TwoFactorEnabled], [LockoutEnabled], [AccessFailedCount])
            VALUES ({id}, {(int)accountType}, {id}, {false}, {false}, {false}, {false}, {0});
            """);

    private static async Task<Guid> InsertObservatoryAsync(ApplicationDbContext context, string ownerUserId)
    {
        var id = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [Observatories]
                ([Id], [OwnerUserId], [Name], [LatitudeDegrees], [LongitudeDegrees], [ElevationMeters],
                 [TimeZoneId], [CreatedAtUtc], [UpdatedAtUtc], [IsActive])
            VALUES ({id}, {ownerUserId}, {ownerUserId}, {35d}, {-113d}, {500d},
                    {"UTC"}, {DateTimeOffset.UnixEpoch}, NULL, {true});
            """).ConfigureAwait(false);
        return id;
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
