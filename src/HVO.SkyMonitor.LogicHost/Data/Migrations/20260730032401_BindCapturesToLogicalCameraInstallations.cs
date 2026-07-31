using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class BindCapturesToLogicalCameraInstallations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<Guid>(
                name: "LogicalCameraInstallationId",
                table: "CentralFrames",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE frame
                SET [LogicalCameraInstallationId] = installation.[Id]
                FROM [CentralFrames] AS frame
                INNER JOIN [LogicalCameraInstallations] AS installation
                    ON installation.[RegistrationId] = frame.[RegistrationId]
                   AND installation.[AssignedAtUtc] <= frame.[CapturedAtUtc]
                    AND (installation.[RetiredAtUtc] IS NULL
                         OR frame.[CapturedAtUtc] < installation.[RetiredAtUtc])
                WHERE frame.[LogicalCameraInstallationId] IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CentralFrames_LogicalCameraInstallationId",
                table: "CentralFrames",
                column: "LogicalCameraInstallationId");

            migrationBuilder.AddForeignKey(
                name: "FK_CentralFrames_LogicalCameraInstallations_LogicalCameraInstallationId",
                table: "CentralFrames",
                column: "LogicalCameraInstallationId",
                principalTable: "LogicalCameraInstallations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE TRIGGER [TR_CentralFrames_InstallationImmutable]
                ON [CentralFrames]
                AFTER UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF UPDATE([LogicalCameraInstallationId]) AND EXISTS (
                        SELECT 1
                        FROM inserted AS current_row
                        INNER JOIN deleted AS previous_row ON previous_row.[Id] = current_row.[Id]
                        WHERE current_row.[LogicalCameraInstallationId] <> previous_row.[LogicalCameraInstallationId]
                           OR current_row.[LogicalCameraInstallationId] IS NULL
                              AND previous_row.[LogicalCameraInstallationId] IS NOT NULL
                           OR current_row.[LogicalCameraInstallationId] IS NOT NULL
                              AND previous_row.[LogicalCameraInstallationId] IS NULL)
                    BEGIN
                        THROW 51000, 'Capture installation authority is immutable.', 1;
                    END
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [TR_CentralFrames_InstallationImmutable];");

            migrationBuilder.DropForeignKey(
                name: "FK_CentralFrames_LogicalCameraInstallations_LogicalCameraInstallationId",
                table: "CentralFrames");

            migrationBuilder.DropIndex(
                name: "IX_CentralFrames_LogicalCameraInstallationId",
                table: "CentralFrames");

            migrationBuilder.DropColumn(
                name: "LogicalCameraInstallationId",
                table: "CentralFrames");
        }
    }
}
