using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableFleetStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.DropIndex(
                name: "IX_DeviceRegistrations_DeviceId_Status",
                table: "DeviceRegistrations");

            migrationBuilder.DropIndex(
                name: "IX_DeviceRegistrations_DevicePublicId",
                table: "DeviceRegistrations");

            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM [DeviceRegistrations]
                    WHERE [Status] = N'Active'
                    GROUP BY [DeviceId], [Status]
                    HAVING COUNT(*) > 1)
                    THROW 51002, 'Durable fleet status requires unique active device identities.', 1;
                IF EXISTS (
                    SELECT 1 FROM [DeviceRegistrations]
                    WHERE [DevicePublicId] IS NOT NULL
                    GROUP BY [DevicePublicId]
                    HAVING COUNT(*) > 1)
                    THROW 51003, 'Durable fleet status requires unique device public identities.', 1;
                """);

            migrationBuilder.CreateTable(
                name: "DeviceFleetStates",
                columns: table => new
                {
                    RegistrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BootSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ApparentClockOffsetSeconds = table.Column<double>(type: "float", nullable: false),
                    ClockDiagnostic = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    ReportedHealth = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    HasStoragePressure = table.Column<bool>(type: "bit", nullable: false),
                    HasRequiredLaneFailure = table.Column<bool>(type: "bit", nullable: false),
                    HasQuarantine = table.Column<bool>(type: "bit", nullable: false),
                    SoftwareVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ConfigurationSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StatusFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CurrentPayloadSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", maxLength: 65536, nullable: false),
                    LastSequenceGapUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SequenceGapCount = table.Column<long>(type: "bigint", nullable: false),
                    LastBootSessionChangeUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BootSessionChangeCount = table.Column<long>(type: "bigint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceFleetStates", x => x.RegistrationId);
                    table.CheckConstraint("CK_DeviceFleetStates_Sequence", "[Sequence] > 0");
                    table.ForeignKey(
                        name: "FK_DeviceFleetStates_DeviceRegistrations_RegistrationId",
                        column: x => x.RegistrationId,
                        principalTable: "DeviceRegistrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceHeartbeatRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RegistrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BootSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PayloadSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StatusFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ReportedHealth = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ClockDiagnostic = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    AdvancedCurrent = table.Column<bool>(type: "bit", nullable: false),
                    IsSignificantSnapshot = table.Column<bool>(type: "bit", nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", maxLength: 65536, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceHeartbeatRecords", x => x.Id);
                    table.CheckConstraint("CK_DeviceHeartbeatRecords_Sequence", "[Sequence] > 0");
                    table.ForeignKey(
                        name: "FK_DeviceHeartbeatRecords_DeviceRegistrations_RegistrationId",
                        column: x => x.RegistrationId,
                        principalTable: "DeviceRegistrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRegistrations_DeviceId_Status",
                table: "DeviceRegistrations",
                columns: new[] { "DeviceId", "Status" },
                unique: true,
                filter: "[Status] = N'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRegistrations_DeviceKeyHash",
                table: "DeviceRegistrations",
                column: "DeviceKeyHash",
                filter: "[DeviceKeyHash] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRegistrations_DevicePublicId",
                table: "DeviceRegistrations",
                column: "DevicePublicId",
                unique: true,
                filter: "[DevicePublicId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceFleetStates_AgentInstanceId",
                table: "DeviceFleetStates",
                column: "AgentInstanceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceFleetStates_ReceivedAtUtc",
                table: "DeviceFleetStates",
                column: "ReceivedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceFleetStates_ReportedHealth_ReceivedAtUtc",
                table: "DeviceFleetStates",
                columns: new[] { "ReportedHealth", "ReceivedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceHeartbeatRecords_IsSignificantSnapshot_ReceivedAtUtc_Id",
                table: "DeviceHeartbeatRecords",
                columns: new[] { "IsSignificantSnapshot", "ReceivedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceHeartbeatRecords_RegistrationId_AgentInstanceId_Sequence",
                table: "DeviceHeartbeatRecords",
                columns: new[] { "RegistrationId", "AgentInstanceId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceHeartbeatRecords_RegistrationId_ReceivedAtUtc",
                table: "DeviceHeartbeatRecords",
                columns: new[] { "RegistrationId", "ReceivedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.DropTable(
                name: "DeviceFleetStates");

            migrationBuilder.DropTable(
                name: "DeviceHeartbeatRecords");

            migrationBuilder.DropIndex(
                name: "IX_DeviceRegistrations_DeviceId_Status",
                table: "DeviceRegistrations");

            migrationBuilder.DropIndex(
                name: "IX_DeviceRegistrations_DeviceKeyHash",
                table: "DeviceRegistrations");

            migrationBuilder.DropIndex(
                name: "IX_DeviceRegistrations_DevicePublicId",
                table: "DeviceRegistrations");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRegistrations_DeviceId_Status",
                table: "DeviceRegistrations",
                columns: new[] { "DeviceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRegistrations_DevicePublicId",
                table: "DeviceRegistrations",
                column: "DevicePublicId");
        }
    }
}
