using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class CentralTransientPayloadReleaseMigrationTests
{
    private const string PreviousMigration = "20260802053338_AddDurableArtifactVerificationReservation";

    [TestMethod]
    public async Task LegacyTerminalAndPendingRowsUpgradeWithoutHistoryMutation()
    {
        await using var database = CreateDatabase();
        try
        {
            var migrator = database.Context.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration).ConfigureAwait(false);
            var terminalEventId = Guid.NewGuid();
            var pendingEventId = Guid.NewGuid();
            var terminalReleaseId = Guid.NewGuid();
            var pendingReleaseId = Guid.NewGuid();
            var terminalRecordId = Guid.NewGuid();
            var pendingRecordId = Guid.NewGuid();
            var terminalUtc = new DateTimeOffset(2026, 8, 2, 17, 0, 0, TimeSpan.Zero);
            await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [CentralTransientEvents] ([Id], [AgentId], [EventId], [EventCreatedUtc])
                VALUES
                    ({terminalEventId}, {"issue-250-migration-terminal"}, {Guid.NewGuid()}, {terminalUtc}),
                    ({pendingEventId}, {"issue-250-migration-pending"}, {Guid.NewGuid()}, {terminalUtc});

                INSERT INTO [CentralTransientPayloadReleases]
                    ([ReleaseId], [CentralTransientEventId], [ActorIdentity], [IdempotencyKey],
                     [CanonicalRequestSha256], [State], [CreatedUtc], [CompletedUtc], [ReasonCode])
                VALUES
                    ({terminalReleaseId}, {terminalEventId}, {"migration"}, {"terminal"},
                     {new string('A', 64)}, N'Pending', {terminalUtc}, NULL, NULL),
                    ({pendingReleaseId}, {pendingEventId}, {"migration"}, {"pending"},
                     {new string('B', 64)}, N'Pending', {terminalUtc}, NULL, NULL);

                INSERT INTO [CentralTransientPayloadReleaseItems]
                    ([ReleaseId], [Ordinal], [Kind], [RecordId], [Outcome], [ReleasedUtc])
                VALUES
                    ({terminalReleaseId}, 0, N'SourceArtifact', {terminalRecordId}, N'Pending', NULL),
                    ({pendingReleaseId}, 0, N'SourceArtifact', {pendingRecordId}, N'Pending', NULL);

                UPDATE [CentralTransientPayloadReleaseItems]
                SET [Outcome] = N'Released', [ReleasedUtc] = {terminalUtc}
                WHERE [ReleaseId] = {terminalReleaseId} AND [Ordinal] = 0;

                UPDATE [CentralTransientPayloadReleases]
                SET [State] = N'Completed', [CompletedUtc] = {terminalUtc}
                WHERE [ReleaseId] = {terminalReleaseId};
                """).ConfigureAwait(false);

            await migrator.MigrateAsync().ConfigureAwait(false);
            (await database.Context.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).Should().BeEmpty();
            database.Context.ChangeTracker.Clear();

            var terminal = await database.Context.CentralTransientPayloadReleaseItems.AsNoTracking()
                .SingleAsync(item => item.ReleaseId == terminalReleaseId).ConfigureAwait(false);
            terminal.Outcome.Should().Be(CentralTransientPayloadReleaseItemOutcome.Released);
            terminal.ReleasedUtc.Should().Be(terminalUtc);
            terminal.ReservationToken.Should().BeNull();
            terminal.RequestedAtUtc.Should().BeNull();
            terminal.StorageReference.Should().BeNull();
            terminal.TargetRowVersion.Should().BeNull();
            terminal.TargetGeneration.Should().BeNull();
            terminal.RetryCount.Should().Be(0);
            terminal.RetryAtUtc.Should().BeNull();
            terminal.FailureReasonCode.Should().BeNull();
            terminal.RowVersion.Should().HaveCount(8);

            var pending = await database.Context.CentralTransientPayloadReleaseItems.AsNoTracking()
                .SingleAsync(item => item.ReleaseId == pendingReleaseId).ConfigureAwait(false);
            pending.Outcome.Should().Be(CentralTransientPayloadReleaseItemOutcome.Pending);
            pending.ReservationToken.Should().BeNull();
            pending.RequestedAtUtc.Should().BeNull();
            pending.RetryCount.Should().Be(0);
            pending.RowVersion.Should().HaveCount(8);
            var discoverable = await database.Context.Database.SqlQuery<Guid>($"""
                    SELECT TOP(1) release.[ReleaseId] AS [Value]
                    FROM [CentralTransientPayloadReleases] AS release
                    INNER JOIN [CentralTransientPayloadReleaseItems] AS item
                        ON item.[ReleaseId] = release.[ReleaseId]
                    WHERE release.[State] = N'Pending' AND item.[Outcome] = N'Pending'
                      AND item.[ReservationToken] IS NULL AND item.[RetryAtUtc] IS NULL
                    ORDER BY release.[CreatedUtc], release.[ReleaseId]
                    """)
                .SingleAsync().ConfigureAwait(false);
            discoverable.Should().Be(pendingReleaseId);

            var indexes = await database.Context.Database.SqlQuery<IndexShape>($"""
                    SELECT [name], [is_unique] AS [IsUnique], [filter_definition] AS [Filter]
                    FROM [sys].[indexes]
                    WHERE [object_id] = OBJECT_ID(N'[CentralTransientPayloadReleaseItems]')
                      AND [name] IN
                      (
                          N'IX_CentralTransientPayloadReleaseItems_Kind_RecordId',
                          N'IX_CentralTransientPayloadReleaseItems_ReleaseId_Kind_RecordId',
                          N'IX_CentralTransientPayloadReleaseItems_RetryAtUtc_RequestedAtUtc_ReleaseId_Ordinal'
                      )
                    """)
                .ToArrayAsync().ConfigureAwait(false);
            indexes.Should().ContainSingle(item =>
                item.Name == "IX_CentralTransientPayloadReleaseItems_Kind_RecordId" && !item.IsUnique);
            indexes.Should().ContainSingle(item =>
                item.Name == "IX_CentralTransientPayloadReleaseItems_ReleaseId_Kind_RecordId" && item.IsUnique);
            indexes.Should().ContainSingle(item =>
                item.Name == "IX_CentralTransientPayloadReleaseItems_RetryAtUtc_RequestedAtUtc_ReleaseId_Ordinal" &&
                item.Filter == "([Outcome]=N'Pending')");

            Func<Task> mutateTerminal = () => database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralTransientPayloadReleaseItems]
                SET [RetryCount] = 1
                WHERE [ReleaseId] = {terminalReleaseId} AND [Ordinal] = 0;
                """);
            await mutateTerminal.Should().ThrowAsync<SqlException>().ConfigureAwait(false);

            Func<Task> negativeRetry = () => database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralTransientPayloadReleaseItems]
                SET [RetryCount] = -1
                WHERE [ReleaseId] = {pendingReleaseId} AND [Ordinal] = 0;
                """);
            await negativeRetry.Should().ThrowAsync<SqlException>().ConfigureAwait(false);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task Migration_UpDownUpSucceedsWhenLegacyUniquenessPreconditionHolds()
    {
        await using var database = CreateDatabase();
        try
        {
            var migrator = database.Context.GetService<IMigrator>();
            await migrator.MigrateAsync().ConfigureAwait(false);
            await migrator.MigrateAsync(PreviousMigration).ConfigureAwait(false);
            await migrator.MigrateAsync().ConfigureAwait(false);
            (await database.Context.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).Should().BeEmpty();
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task Migration_DownRejectsCrossReleaseDuplicateHistoryBeforeMutation()
    {
        await using var database = CreateDatabase();
        try
        {
            var migrator = database.Context.GetService<IMigrator>();
            await migrator.MigrateAsync().ConfigureAwait(false);
            var eventIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
            var releaseIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
            var recordId = Guid.NewGuid();
            var now = new DateTimeOffset(2026, 8, 2, 19, 0, 0, TimeSpan.Zero);
            await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [CentralTransientEvents] ([Id], [AgentId], [EventId], [EventCreatedUtc])
                VALUES
                    ({eventIds[0]}, {"issue-250-down-a"}, {Guid.NewGuid()}, {now}),
                    ({eventIds[1]}, {"issue-250-down-b"}, {Guid.NewGuid()}, {now});
                INSERT INTO [CentralTransientPayloadReleases]
                    ([ReleaseId], [CentralTransientEventId], [ActorIdentity], [IdempotencyKey],
                     [CanonicalRequestSha256], [State], [CreatedUtc])
                VALUES
                    ({releaseIds[0]}, {eventIds[0]}, {"migration"}, {"a"}, {new string('A', 64)}, N'Pending', {now}),
                    ({releaseIds[1]}, {eventIds[1]}, {"migration"}, {"b"}, {new string('B', 64)}, N'Pending', {now});
                INSERT INTO [CentralTransientPayloadReleaseItems]
                    ([ReleaseId], [Ordinal], [Kind], [RecordId], [Outcome], [RetryCount])
                VALUES
                    ({releaseIds[0]}, 0, N'SourceArtifact', {recordId}, N'Pending', 0),
                    ({releaseIds[1]}, 0, N'SourceArtifact', {recordId}, N'Pending', 0);
                """).ConfigureAwait(false);

            Func<Task> downgrade = () => migrator.MigrateAsync(PreviousMigration);
            await downgrade.Should().ThrowAsync<SqlException>()
                .WithMessage("*cross-release target history contains duplicates*").ConfigureAwait(false);
            var failureColumnStillPresent = await database.Context.Database.SqlQuery<int>($"""
                    SELECT CASE WHEN COL_LENGTH(N'CentralTransientPayloadReleaseItems', N'FailureReasonCode') IS NULL
                        THEN 0 ELSE 1 END AS [Value]
                    """).SingleAsync().ConfigureAwait(false);
            failureColumnStillPresent.Should().Be(1);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task Migration_DownRejectsTerminalFailureHistoryBeforeMutation()
    {
        await using var database = CreateDatabase();
        try
        {
            var migrator = database.Context.GetService<IMigrator>();
            await migrator.MigrateAsync().ConfigureAwait(false);
            var eventId = Guid.NewGuid();
            var releaseId = Guid.NewGuid();
            var now = new DateTimeOffset(2026, 8, 2, 19, 30, 0, TimeSpan.Zero);
            await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [CentralTransientEvents] ([Id], [AgentId], [EventId], [EventCreatedUtc])
                VALUES ({eventId}, {"issue-250-down-failed"}, {Guid.NewGuid()}, {now});
                INSERT INTO [CentralTransientPayloadReleases]
                    ([ReleaseId], [CentralTransientEventId], [ActorIdentity], [IdempotencyKey],
                     [CanonicalRequestSha256], [State], [CreatedUtc])
                VALUES ({releaseId}, {eventId}, {"migration"}, {"failed"}, {new string('C', 64)}, N'Pending', {now});
                INSERT INTO [CentralTransientPayloadReleaseItems]
                    ([ReleaseId], [Ordinal], [Kind], [RecordId], [Outcome], [RetryCount])
                VALUES ({releaseId}, 0, N'SourceArtifact', {Guid.NewGuid()}, N'Pending', 0);
                UPDATE [CentralTransientPayloadReleaseItems]
                SET [Outcome] = N'Failed', [RequestedAtUtc] = {now}, [ReleasedUtc] = {now},
                    [StorageReference] = N'minio://skymonitor-artifacts/issue-250/down-failed.bin',
                    [TargetRowVersion] = 0x0102030405060708, [TargetGeneration] = 0,
                    [FailureReasonCode] = N'transient-retention.delete-retry-exhausted'
                WHERE [ReleaseId] = {releaseId};
                UPDATE [CentralTransientPayloadReleases]
                SET [State] = N'Failed', [CompletedUtc] = {now}, [ReasonCode] = N'transient-retention.item-failed'
                WHERE [ReleaseId] = {releaseId};
                """).ConfigureAwait(false);

            Func<Task> downgrade = () => migrator.MigrateAsync(PreviousMigration);
            await downgrade.Should().ThrowAsync<SqlException>()
                .WithMessage("*terminal failure history exists*").ConfigureAwait(false);
            var failureColumnStillPresent = await database.Context.Database.SqlQuery<int>($"""
                    SELECT CASE WHEN COL_LENGTH(N'CentralTransientPayloadReleaseItems', N'FailureReasonCode') IS NULL
                        THEN 0 ELSE 1 END AS [Value]
                    """).SingleAsync().ConfigureAwait(false);
            failureColumnStillPresent.Should().Be(1);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task ParentTransitionTrigger_RejectsInvalidAndAcceptsValidTerminalItemSets()
    {
        await using var database = CreateDatabase();
        try
        {
            await database.Context.Database.MigrateAsync().ConfigureAwait(false);
            var eventIds = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
            var releaseIds = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
            var now = new DateTimeOffset(2026, 8, 2, 20, 0, 0, TimeSpan.Zero);
            await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [CentralTransientEvents] ([Id], [AgentId], [EventId], [EventCreatedUtc]) VALUES
                    ({eventIds[0]}, N'parent-trigger-0', {Guid.NewGuid()}, {now}),
                    ({eventIds[1]}, N'parent-trigger-1', {Guid.NewGuid()}, {now}),
                    ({eventIds[2]}, N'parent-trigger-2', {Guid.NewGuid()}, {now}),
                    ({eventIds[3]}, N'parent-trigger-3', {Guid.NewGuid()}, {now});
                INSERT INTO [CentralTransientPayloadReleases]
                    ([ReleaseId], [CentralTransientEventId], [ActorIdentity], [IdempotencyKey],
                     [CanonicalRequestSha256], [State], [CreatedUtc]) VALUES
                    ({releaseIds[0]}, {eventIds[0]}, N'trigger', N'completed-failed', {new string('A', 64)}, N'Pending', {now}),
                    ({releaseIds[1]}, {eventIds[1]}, N'trigger', N'failed-pending', {new string('B', 64)}, N'Pending', {now}),
                    ({releaseIds[2]}, {eventIds[2]}, N'trigger', N'valid-completed', {new string('C', 64)}, N'Pending', {now}),
                    ({releaseIds[3]}, {eventIds[3]}, N'trigger', N'valid-failed', {new string('D', 64)}, N'Pending', {now});
                INSERT INTO [CentralTransientPayloadReleaseItems]
                    ([ReleaseId], [Ordinal], [Kind], [RecordId], [Outcome], [RetryCount]) VALUES
                    ({releaseIds[0]}, 0, N'SourceArtifact', {Guid.NewGuid()}, N'Pending', 0),
                    ({releaseIds[1]}, 0, N'SourceArtifact', {Guid.NewGuid()}, N'Pending', 0),
                    ({releaseIds[2]}, 0, N'SourceArtifact', {Guid.NewGuid()}, N'Pending', 0),
                    ({releaseIds[3]}, 0, N'SourceArtifact', {Guid.NewGuid()}, N'Pending', 0);
                UPDATE [CentralTransientPayloadReleaseItems]
                SET [Outcome] = N'Failed', [RequestedAtUtc] = {now}, [ReleasedUtc] = {now},
                    [StorageReference] = N'minio://skymonitor-artifacts/issue-250/trigger-failed.bin',
                    [TargetRowVersion] = 0x0102030405060708, [TargetGeneration] = 0,
                    [FailureReasonCode] = N'transient-retention.delete-retry-exhausted'
                WHERE [ReleaseId] IN ({releaseIds[0]}, {releaseIds[3]});
                UPDATE [CentralTransientPayloadReleaseItems]
                SET [Outcome] = N'Released', [RequestedAtUtc] = {now}, [ReleasedUtc] = {now},
                    [StorageReference] = N'minio://skymonitor-artifacts/issue-250/trigger-released.bin',
                    [TargetRowVersion] = 0x0102030405060708, [TargetGeneration] = 0
                WHERE [ReleaseId] = {releaseIds[2]};
                """).ConfigureAwait(false);

            Func<Task> completedWithFailed = () => database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralTransientPayloadReleases]
                SET [State] = N'Completed', [CompletedUtc] = {now}
                WHERE [ReleaseId] = {releaseIds[0]};
                """);
            await completedWithFailed.Should().ThrowAsync<SqlException>().ConfigureAwait(false);
            Func<Task> failedWithPending = () => database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralTransientPayloadReleases]
                SET [State] = N'Failed', [CompletedUtc] = {now}, [ReasonCode] = N'transient-retention.item-failed'
                WHERE [ReleaseId] = {releaseIds[1]};
                """);
            await failedWithPending.Should().ThrowAsync<SqlException>().ConfigureAwait(false);

            await database.Context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [CentralTransientPayloadReleases]
                SET [State] = N'Completed', [CompletedUtc] = {now}
                WHERE [ReleaseId] = {releaseIds[2]};
                UPDATE [CentralTransientPayloadReleases]
                SET [State] = N'Failed', [CompletedUtc] = {now}, [ReasonCode] = N'transient-retention.item-failed'
                WHERE [ReleaseId] = {releaseIds[3]};
                """).ConfigureAwait(false);
            var states = await database.Context.CentralTransientPayloadReleases.AsNoTracking()
                .Where(item => item.ReleaseId == releaseIds[2] || item.ReleaseId == releaseIds[3])
                .Select(item => item.State).ToArrayAsync().ConfigureAwait(false);
            states.Should().BeEquivalentTo(
                [CentralTransientPayloadReleaseState.Completed, CentralTransientPayloadReleaseState.Failed]);
        }
        finally
        {
            await database.Context.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private static MigrationDatabase CreateDatabase()
    {
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorPayloadReleaseMigration_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new(new ApplicationDbContext(options));
    }

    private sealed class IndexShape
    {
        public string Name { get; set; } = string.Empty;
        public bool IsUnique { get; set; }
        public string? Filter { get; set; }
    }

    private sealed class MigrationDatabase(ApplicationDbContext context) : IAsyncDisposable
    {
        public ApplicationDbContext Context { get; } = context;
        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }
}
