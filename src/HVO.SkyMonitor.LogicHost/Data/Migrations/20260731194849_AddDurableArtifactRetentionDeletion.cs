using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableArtifactRetentionDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "CentralObjectRecoveryDispositions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "CentralArtifactId",
                table: "CentralObjectRecoveryDispositions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAtUtc",
                table: "CentralObjectRecoveryDispositions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAttemptAtUtc",
                table: "CentralObjectRecoveryDispositions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAtUtc",
                table: "CentralObjectRecoveryDispositions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OperationToken",
                table: "CentralObjectRecoveryDispositions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetentionDeletionCompletedAtUtc",
                table: "CentralArtifacts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetentionDeletionRequestedAtUtc",
                table: "CentralArtifacts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RetentionDeletionToken",
                table: "CentralArtifacts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralObjectRecoveryDispositions_Kind_State_NextAttemptAtUtc_UpdatedAtUtc_Id",
                table: "CentralObjectRecoveryDispositions",
                columns: new[] { "Kind", "State", "NextAttemptAtUtc", "UpdatedAtUtc", "Id" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_CentralObjectRecoveryDispositions_AttemptCount",
                table: "CentralObjectRecoveryDispositions",
                sql: "[AttemptCount] >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CentralObjectRecoveryDispositions_RetentionDeletion",
                table: "CentralObjectRecoveryDispositions",
                sql: "[OperationToken] IS NULL AND [CentralArtifactId] IS NULL OR [OperationToken] IS NOT NULL AND [CentralArtifactId] IS NOT NULL AND [Kind] = 'ExpiredDelete' AND [State] IN ('PendingDelete', 'Completed', 'Failed')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CentralObjectRecoveryDispositions_TokenizedState",
                table: "CentralObjectRecoveryDispositions",
                sql: "[OperationToken] IS NULL OR ([State] = 'PendingDelete' AND [CompletedAtUtc] IS NULL) OR ([State] = 'Completed' AND [CompletedAtUtc] IS NOT NULL AND [LastAttemptAtUtc] IS NOT NULL AND [AttemptCount] > 0 AND [NextAttemptAtUtc] IS NULL AND [ReasonCode] IS NULL AND [CompletedAtUtc] >= [LastAttemptAtUtc]) OR ([State] = 'Failed' AND [CompletedAtUtc] IS NULL AND [LastAttemptAtUtc] IS NOT NULL AND [AttemptCount] > 0 AND [NextAttemptAtUtc] IS NULL AND [ReasonCode] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CentralObjectRecoveryDispositions_TokenizedTimestamps",
                table: "CentralObjectRecoveryDispositions",
                sql: "[OperationToken] IS NULL OR ([UpdatedAtUtc] >= [CreatedAtUtc] AND ([LastAttemptAtUtc] IS NULL OR [LastAttemptAtUtc] >= [CreatedAtUtc]))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CentralArtifacts_RetentionDeletion",
                table: "CentralArtifacts",
                sql: "[RetentionDeletionToken] IS NULL AND [RetentionDeletionRequestedAtUtc] IS NULL AND [RetentionDeletionCompletedAtUtc] IS NULL OR [RetentionDeletionToken] IS NOT NULL AND [RetentionDeletionRequestedAtUtc] IS NOT NULL AND [ObjectState] = 'Expired' AND ([RetentionDeletionCompletedAtUtc] IS NULL OR [RetentionDeletionCompletedAtUtc] >= [RetentionDeletionRequestedAtUtc])");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "IX_CentralObjectRecoveryDispositions_Kind_State_NextAttemptAtUtc_UpdatedAtUtc_Id",
                table: "CentralObjectRecoveryDispositions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CentralObjectRecoveryDispositions_AttemptCount",
                table: "CentralObjectRecoveryDispositions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CentralObjectRecoveryDispositions_RetentionDeletion",
                table: "CentralObjectRecoveryDispositions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CentralObjectRecoveryDispositions_TokenizedState",
                table: "CentralObjectRecoveryDispositions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CentralObjectRecoveryDispositions_TokenizedTimestamps",
                table: "CentralObjectRecoveryDispositions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CentralArtifacts_RetentionDeletion",
                table: "CentralArtifacts");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "CentralObjectRecoveryDispositions");

            migrationBuilder.DropColumn(
                name: "CentralArtifactId",
                table: "CentralObjectRecoveryDispositions");

            migrationBuilder.DropColumn(
                name: "CompletedAtUtc",
                table: "CentralObjectRecoveryDispositions");

            migrationBuilder.DropColumn(
                name: "LastAttemptAtUtc",
                table: "CentralObjectRecoveryDispositions");

            migrationBuilder.DropColumn(
                name: "NextAttemptAtUtc",
                table: "CentralObjectRecoveryDispositions");

            migrationBuilder.DropColumn(
                name: "OperationToken",
                table: "CentralObjectRecoveryDispositions");

            migrationBuilder.DropColumn(
                name: "RetentionDeletionCompletedAtUtc",
                table: "CentralArtifacts");

            migrationBuilder.DropColumn(
                name: "RetentionDeletionRequestedAtUtc",
                table: "CentralArtifacts");

            migrationBuilder.DropColumn(
                name: "RetentionDeletionToken",
                table: "CentralArtifacts");
        }
    }
}
