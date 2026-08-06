using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCentralProcessingOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "CentralProcessingOverrideVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CloudTransmissionThresholdMillionths = table.Column<int>(type: "int", nullable: true),
                    CentralValidationEnabled = table.Column<bool>(type: "bit", nullable: true),
                    EffectiveFromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SupersededAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralProcessingOverrideVersions", x => x.Id);
                    table.CheckConstraint("CK_CentralProcessingOverrideVersions_Threshold", "[CloudTransmissionThresholdMillionths] IS NULL OR [CloudTransmissionThresholdMillionths] BETWEEN 1 AND 999999");
                    table.CheckConstraint("CK_CentralProcessingOverrideVersions_Version", "[Version] > 0");
                    table.ForeignKey(
                        name: "FK_CentralProcessingOverrideVersions_Observatories_ObservatoryId",
                        column: x => x.ObservatoryId,
                        principalTable: "Observatories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingOverrideVersions_ObservatoryId",
                table: "CentralProcessingOverrideVersions",
                column: "ObservatoryId",
                unique: true,
                filter: "[SupersededAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingOverrideVersions_ObservatoryId_Version",
                table: "CentralProcessingOverrideVersions",
                columns: new[] { "ObservatoryId", "Version" },
                unique: true);

            MigrationSql.ExecuteBatch(migrationBuilder, """
                CREATE TRIGGER [TR_CentralProcessingOverrideVersions_Transitions]
                ON [CentralProcessingOverrideVersions]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1
                        FROM deleted d
                        LEFT JOIN inserted i ON i.[Id] = d.[Id]
                        WHERE i.[Id] IS NULL
                           OR d.[SupersededAtUtc] IS NOT NULL
                           OR i.[SupersededAtUtc] IS NULL
                           OR i.[ObservatoryId] <> d.[ObservatoryId]
                           OR i.[Version] <> d.[Version]
                           OR ISNULL(i.[CloudTransmissionThresholdMillionths], -1) <> ISNULL(d.[CloudTransmissionThresholdMillionths], -1)
                           OR ISNULL(CONVERT(int, i.[CentralValidationEnabled]), -1) <> ISNULL(CONVERT(int, d.[CentralValidationEnabled]), -1)
                           OR i.[EffectiveFromUtc] <> d.[EffectiveFromUtc]
                           OR i.[ActorUserId] <> d.[ActorUserId]
                           OR i.[ReasonCode] <> d.[ReasonCode])
                    BEGIN
                        THROW 51000, 'Central processing override history is immutable.', 1;
                    END
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "CentralProcessingOverrideVersions");
        }
    }
}
