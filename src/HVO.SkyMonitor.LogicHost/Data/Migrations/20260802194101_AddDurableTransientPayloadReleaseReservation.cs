using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableTransientPayloadReleaseReservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "IX_CentralTransientPayloadReleaseItems_Kind_RecordId",
                table: "CentralTransientPayloadReleaseItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CentralTransientPayloadReleaseItems_Outcome",
                table: "CentralTransientPayloadReleaseItems");

            migrationBuilder.AddColumn<string>(
                name: "FailureReasonCode",
                table: "CentralTransientPayloadReleaseItems",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RequestedAtUtc",
                table: "CentralTransientPayloadReleaseItems",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReservationToken",
                table: "CentralTransientPayloadReleaseItems",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetryAtUtc",
                table: "CentralTransientPayloadReleaseItems",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetryCount",
                table: "CentralTransientPayloadReleaseItems",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "CentralTransientPayloadReleaseItems",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: Array.Empty<byte>());

            migrationBuilder.AddColumn<string>(
                name: "StorageReference",
                table: "CentralTransientPayloadReleaseItems",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<long>(
                name: "TargetGeneration",
                table: "CentralTransientPayloadReleaseItems",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "TargetRowVersion",
                table: "CentralTransientPayloadReleaseItems",
                type: "varbinary(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientPayloadReleaseItems_Kind_RecordId",
                table: "CentralTransientPayloadReleaseItems",
                columns: new[] { "Kind", "RecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientPayloadReleaseItems_ReleaseId_Kind_RecordId",
                table: "CentralTransientPayloadReleaseItems",
                columns: new[] { "ReleaseId", "Kind", "RecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientPayloadReleaseItems_RetryAtUtc_RequestedAtUtc_ReleaseId_Ordinal",
                table: "CentralTransientPayloadReleaseItems",
                columns: new[] { "RetryAtUtc", "RequestedAtUtc", "ReleaseId", "Ordinal" },
                filter: "[Outcome] = N'Pending'")
                .Annotation("SqlServer:Include", new[] { "ReservationToken" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_CentralTransientPayloadReleaseItems_Outcome",
                table: "CentralTransientPayloadReleaseItems",
                sql: "([Outcome] = 'Pending' AND [ReleasedUtc] IS NULL AND [FailureReasonCode] IS NULL) OR ([Outcome] IN ('Released', 'PreservedHeld') AND [ReleasedUtc] IS NOT NULL AND [FailureReasonCode] IS NULL) OR ([Outcome] = 'Failed' AND [ReleasedUtc] IS NOT NULL AND [FailureReasonCode] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CentralTransientPayloadReleaseItems_Reservation",
                table: "CentralTransientPayloadReleaseItems",
                sql: "(([RequestedAtUtc] IS NULL AND [StorageReference] IS NULL AND [TargetRowVersion] IS NULL AND [TargetGeneration] IS NULL) OR ([RequestedAtUtc] IS NOT NULL AND [StorageReference] IS NOT NULL AND [TargetRowVersion] IS NOT NULL AND DATALENGTH([TargetRowVersion]) = 8 AND [TargetGeneration] IS NOT NULL)) AND ([ReservationToken] IS NULL OR ([Outcome] = 'Pending' AND [RequestedAtUtc] IS NOT NULL AND [RetryAtUtc] IS NULL)) AND ([RetryAtUtc] IS NULL OR ([Outcome] = 'Pending' AND [ReservationToken] IS NULL AND [RequestedAtUtc] IS NOT NULL)) AND ([Outcome] = 'Pending' OR ([ReservationToken] IS NULL AND [RetryAtUtc] IS NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CentralTransientPayloadReleaseItems_RetryCount",
                table: "CentralTransientPayloadReleaseItems",
                sql: "[RetryCount] >= 0");

            MigrationSql.ExecuteBatch(migrationBuilder, """
                ALTER TRIGGER [TR_CentralTransientPayloadReleases_Transition]
                ON [CentralTransientPayloadReleases]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1 FROM deleted AS d
                        LEFT JOIN inserted AS i ON i.[ReleaseId] = d.[ReleaseId]
                        WHERE i.[ReleaseId] IS NULL
                           OR d.[State] <> N'Pending'
                           OR i.[CentralTransientEventId] <> d.[CentralTransientEventId]
                           OR i.[ActorIdentity] <> d.[ActorIdentity]
                           OR i.[IdempotencyKey] <> d.[IdempotencyKey]
                           OR i.[CanonicalRequestSha256] <> d.[CanonicalRequestSha256]
                           OR i.[CreatedUtc] <> d.[CreatedUtc]
                           OR i.[State] NOT IN (N'Completed', N'Failed')
                           OR (i.[State] = N'Completed' AND EXISTS (
                               SELECT 1 FROM [CentralTransientPayloadReleaseItems] AS item
                               WHERE item.[ReleaseId] = i.[ReleaseId]
                                 AND item.[Outcome] IN (N'Pending', N'Failed')))
                           OR (i.[State] = N'Failed' AND
                               (EXISTS (
                                   SELECT 1 FROM [CentralTransientPayloadReleaseItems] AS item
                                   WHERE item.[ReleaseId] = i.[ReleaseId] AND item.[Outcome] = N'Pending')
                                OR NOT EXISTS (
                                   SELECT 1 FROM [CentralTransientPayloadReleaseItems] AS item
                                   WHERE item.[ReleaseId] = i.[ReleaseId] AND item.[Outcome] = N'Failed'))))
                        THROW 51000, 'Transient payload release transition is invalid.', 1;
                END
                """);

            MigrationSql.ExecuteBatch(migrationBuilder, """
                ALTER TRIGGER [TR_CentralTransientPayloadReleaseItems_Transition]
                ON [CentralTransientPayloadReleaseItems]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1
                        FROM deleted AS d
                        LEFT JOIN inserted AS i
                            ON i.[ReleaseId] = d.[ReleaseId] AND i.[Ordinal] = d.[Ordinal]
                        WHERE i.[ReleaseId] IS NULL
                           OR i.[Kind] <> d.[Kind]
                           OR i.[RecordId] <> d.[RecordId]
                           OR d.[Outcome] <> N'Pending'
                           OR i.[Outcome] NOT IN (N'Pending', N'Released', N'PreservedHeld', N'Failed')
                           OR i.[RetryCount] < d.[RetryCount]
                           OR (i.[Outcome] = N'Pending' AND
                               (i.[ReleasedUtc] IS NOT NULL OR i.[FailureReasonCode] IS NOT NULL))
                           OR (i.[Outcome] IN (N'Released', N'PreservedHeld') AND
                               (i.[ReleasedUtc] IS NULL OR i.[FailureReasonCode] IS NOT NULL OR
                                i.[ReservationToken] IS NOT NULL OR i.[RetryAtUtc] IS NOT NULL))
                           OR (i.[Outcome] = N'Failed' AND
                               (i.[ReleasedUtc] IS NULL OR i.[FailureReasonCode] IS NULL OR
                                i.[ReservationToken] IS NOT NULL OR i.[RetryAtUtc] IS NOT NULL)))
                        THROW 51000, 'Transient payload release item transition is invalid.', 1;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT [Kind], [RecordId]
                    FROM [CentralTransientPayloadReleaseItems]
                    GROUP BY [Kind], [RecordId]
                    HAVING COUNT_BIG(*) > 1)
                    THROW 51000, 'Cannot downgrade transient payload release items while cross-release target history contains duplicates.', 1;
                IF EXISTS (
                    SELECT 1 FROM [CentralTransientPayloadReleaseItems]
                    WHERE [Outcome] = N'Failed')
                    THROW 51000, 'Cannot downgrade transient payload release items while terminal failure history exists.', 1;
                """);

            migrationBuilder.Sql("""
                ALTER TRIGGER [TR_CentralTransientPayloadReleases_Transition]
                ON [CentralTransientPayloadReleases]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1 FROM deleted AS d
                        LEFT JOIN inserted AS i ON i.[ReleaseId] = d.[ReleaseId]
                        WHERE i.[ReleaseId] IS NULL
                           OR d.[State] NOT IN (N'Pending', N'Failed')
                           OR i.[CentralTransientEventId] <> d.[CentralTransientEventId]
                           OR i.[ActorIdentity] <> d.[ActorIdentity]
                           OR i.[IdempotencyKey] <> d.[IdempotencyKey]
                           OR i.[CanonicalRequestSha256] <> d.[CanonicalRequestSha256]
                           OR i.[CreatedUtc] <> d.[CreatedUtc]
                           OR NOT ((d.[State] = N'Pending' AND i.[State] IN (N'Completed', N'Failed'))
                               OR (d.[State] = N'Failed' AND i.[State] = N'Pending' AND
                                   i.[CompletedUtc] IS NULL AND i.[ReasonCode] IS NULL))
                           OR (i.[State] = N'Completed' AND EXISTS (
                               SELECT 1 FROM [CentralTransientPayloadReleaseItems] AS item
                               WHERE item.[ReleaseId] = i.[ReleaseId] AND item.[Outcome] = N'Pending')))
                        THROW 51000, 'Transient payload release transition is invalid.', 1;
                END
                """);

            migrationBuilder.Sql("""
                ALTER TRIGGER [TR_CentralTransientPayloadReleaseItems_Transition]
                ON [CentralTransientPayloadReleaseItems]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1 FROM deleted AS d
                        LEFT JOIN inserted AS i ON i.[ReleaseId] = d.[ReleaseId] AND i.[Ordinal] = d.[Ordinal]
                        WHERE i.[ReleaseId] IS NULL
                           OR d.[Outcome] <> N'Pending'
                           OR i.[Kind] <> d.[Kind]
                           OR i.[RecordId] <> d.[RecordId]
                           OR i.[Outcome] NOT IN (N'Released', N'PreservedHeld')
                           OR i.[ReleasedUtc] IS NULL)
                        THROW 51000, 'Transient payload release item transition is invalid.', 1;
                END
                """);
            migrationBuilder.DropIndex(
                name: "IX_CentralTransientPayloadReleaseItems_Kind_RecordId",
                table: "CentralTransientPayloadReleaseItems");

            migrationBuilder.DropIndex(
                name: "IX_CentralTransientPayloadReleaseItems_ReleaseId_Kind_RecordId",
                table: "CentralTransientPayloadReleaseItems");

            migrationBuilder.DropIndex(
                name: "IX_CentralTransientPayloadReleaseItems_RetryAtUtc_RequestedAtUtc_ReleaseId_Ordinal",
                table: "CentralTransientPayloadReleaseItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CentralTransientPayloadReleaseItems_Outcome",
                table: "CentralTransientPayloadReleaseItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CentralTransientPayloadReleaseItems_Reservation",
                table: "CentralTransientPayloadReleaseItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CentralTransientPayloadReleaseItems_RetryCount",
                table: "CentralTransientPayloadReleaseItems");

            migrationBuilder.DropColumn(
                name: "FailureReasonCode",
                table: "CentralTransientPayloadReleaseItems");

            migrationBuilder.DropColumn(
                name: "RequestedAtUtc",
                table: "CentralTransientPayloadReleaseItems");

            migrationBuilder.DropColumn(
                name: "ReservationToken",
                table: "CentralTransientPayloadReleaseItems");

            migrationBuilder.DropColumn(
                name: "RetryAtUtc",
                table: "CentralTransientPayloadReleaseItems");

            migrationBuilder.DropColumn(
                name: "RetryCount",
                table: "CentralTransientPayloadReleaseItems");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "CentralTransientPayloadReleaseItems");

            migrationBuilder.DropColumn(
                name: "StorageReference",
                table: "CentralTransientPayloadReleaseItems");

            migrationBuilder.DropColumn(
                name: "TargetGeneration",
                table: "CentralTransientPayloadReleaseItems");

            migrationBuilder.DropColumn(
                name: "TargetRowVersion",
                table: "CentralTransientPayloadReleaseItems");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientPayloadReleaseItems_Kind_RecordId",
                table: "CentralTransientPayloadReleaseItems",
                columns: new[] { "Kind", "RecordId" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_CentralTransientPayloadReleaseItems_Outcome",
                table: "CentralTransientPayloadReleaseItems",
                sql: "([Outcome] = 'Pending' AND [ReleasedUtc] IS NULL) OR ([Outcome] IN ('Released', 'PreservedHeld') AND [ReleasedUtc] IS NOT NULL)");
        }
    }
}
