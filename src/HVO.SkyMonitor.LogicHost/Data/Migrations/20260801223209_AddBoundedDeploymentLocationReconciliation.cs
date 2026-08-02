using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBoundedDeploymentLocationReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "DeploymentLocationReconciliationWork",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceDeploymentLocationVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorityConcurrencyToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastErrorCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CaptureCount = table.Column<long>(type: "bigint", nullable: true),
                    DiscoveryCutoffUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DiscoveredCaptureCount = table.Column<long>(type: "bigint", nullable: false),
                    DiscoveryCursorFirstReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DiscoveryCursorCentralFrameId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CompletedCaptureCount = table.Column<long>(type: "bigint", nullable: false),
                    ScheduledArtifactCount = table.Column<long>(type: "bigint", nullable: false),
                    LastCompletedFirstReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastCompletedCentralFrameId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActiveBatchUpperFirstReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ActiveBatchUpperCentralFrameId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActiveBatchCaptureCount = table.Column<int>(type: "int", nullable: false),
                    SchedulingCentralFrameId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SchedulingCentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TraceParent = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    TraceState = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentLocationReconciliationWork", x => x.Id);
                    table.CheckConstraint("CK_DeploymentLocationReconciliationWork_ActiveBatch", "([ActiveBatchUpperFirstReceivedAtUtc] IS NULL AND [ActiveBatchUpperCentralFrameId] IS NULL AND [ActiveBatchCaptureCount] = 0 AND [SchedulingCentralFrameId] IS NULL AND [SchedulingCentralArtifactId] IS NULL) OR ([ActiveBatchUpperFirstReceivedAtUtc] IS NOT NULL AND [ActiveBatchUpperCentralFrameId] IS NOT NULL AND [ActiveBatchCaptureCount] > 0 AND (([SchedulingCentralFrameId] IS NULL AND [SchedulingCentralArtifactId] IS NULL) OR ([SchedulingCentralFrameId] IS NOT NULL AND [SchedulingCentralArtifactId] IS NOT NULL)))");
                    table.CheckConstraint("CK_DeploymentLocationReconciliationWork_Counts", "[AttemptCount] >= 0 AND [DiscoveredCaptureCount] >= 0 AND [CompletedCaptureCount] >= 0 AND [ScheduledArtifactCount] >= 0 AND [ActiveBatchCaptureCount] >= 0 AND ([CaptureCount] IS NULL OR ([CaptureCount] = [DiscoveredCaptureCount] AND [CompletedCaptureCount] + [ActiveBatchCaptureCount] <= [CaptureCount]))");
                    table.CheckConstraint("CK_DeploymentLocationReconciliationWork_Cursor", "([LastCompletedFirstReceivedAtUtc] IS NULL AND [LastCompletedCentralFrameId] IS NULL) OR ([LastCompletedFirstReceivedAtUtc] IS NOT NULL AND [LastCompletedCentralFrameId] IS NOT NULL)");
                    table.CheckConstraint("CK_DeploymentLocationReconciliationWork_Discovery", "([DiscoveryCutoffUtc] IS NULL AND [CaptureCount] IS NULL AND [DiscoveredCaptureCount] = 0 AND [DiscoveryCursorCentralFrameId] IS NULL) OR ([DiscoveryCutoffUtc] IS NOT NULL AND [CaptureCount] IS NULL AND [CompletedCaptureCount] = 0 AND [ActiveBatchCaptureCount] = 0 AND [LastCompletedCentralFrameId] IS NULL) OR ([DiscoveryCutoffUtc] IS NOT NULL AND [CaptureCount] IS NOT NULL)");
                    table.CheckConstraint("CK_DeploymentLocationReconciliationWork_DiscoveryCursor", "([DiscoveryCursorFirstReceivedAtUtc] IS NULL AND [DiscoveryCursorCentralFrameId] IS NULL) OR ([DiscoveryCursorFirstReceivedAtUtc] IS NOT NULL AND [DiscoveryCursorCentralFrameId] IS NOT NULL)");
                    table.CheckConstraint("CK_DeploymentLocationReconciliationWork_Lease", "([LeaseToken] IS NULL AND [LeaseOwner] IS NULL AND [LeaseExpiresAtUtc] IS NULL) OR ([LeaseToken] IS NOT NULL AND [LeaseOwner] IS NOT NULL AND [LeaseExpiresAtUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_DeploymentLocationReconciliationWork_State", "([Status] = N'Processing' AND [LeaseToken] IS NOT NULL AND [NextAttemptAtUtc] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] IN (N'Pending', N'Retry') AND [LeaseToken] IS NULL AND [NextAttemptAtUtc] IS NOT NULL AND [CompletedAtUtc] IS NULL AND ([Status] <> N'Retry' OR [LastErrorCode] IS NOT NULL)) OR ([Status] = N'Completed' AND [LeaseToken] IS NULL AND [NextAttemptAtUtc] IS NULL AND [LastErrorCode] IS NULL AND [CompletedAtUtc] IS NOT NULL AND [ActiveBatchUpperCentralFrameId] IS NULL AND [CaptureCount] IS NOT NULL AND [CompletedCaptureCount] = [CaptureCount])");
                    table.CheckConstraint("CK_DeploymentLocationReconciliationWork_Status", "[Status] IN (N'Pending', N'Processing', N'Retry', N'Completed')");
                    table.CheckConstraint("CK_DeploymentLocationReconciliationWork_Timestamps", "[UpdatedAtUtc] >= [CreatedAtUtc] AND ([StartedAtUtc] IS NULL OR [StartedAtUtc] >= [CreatedAtUtc]) AND ([CompletedAtUtc] IS NULL OR [CompletedAtUtc] >= [CreatedAtUtc])");
                    table.ForeignKey(
                        name: "FK_DeploymentLocationReconciliationWork_DeviceDeploymentLocationVersions_DeviceDeploymentLocationVersionId",
                        column: x => x.DeviceDeploymentLocationVersionId,
                        principalTable: "DeviceDeploymentLocationVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeploymentLocationReconciliationCaptures",
                columns: table => new
                {
                    DeploymentLocationReconciliationWorkId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorityConcurrencyToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralFrameId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FirstReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentLocationReconciliationCaptures", x => new { x.DeploymentLocationReconciliationWorkId, x.AuthorityConcurrencyToken, x.CentralFrameId });
                    table.ForeignKey(
                        name: "FK_DeploymentLocationReconciliationCaptures_CentralFrames_CentralFrameId",
                        column: x => x.CentralFrameId,
                        principalTable: "CentralFrames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DeploymentLocationReconciliationCaptures_DeploymentLocationReconciliationWork_DeploymentLocationReconciliationWorkId",
                        column: x => x.DeploymentLocationReconciliationWorkId,
                        principalTable: "DeploymentLocationReconciliationWork",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CentralFrames_RegistrationId_FirstReceivedAtUtc_Id",
                table: "CentralFrames",
                columns: new[] { "RegistrationId", "FirstReceivedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentLocationReconciliationCaptures_CentralFrameId",
                table: "DeploymentLocationReconciliationCaptures",
                column: "CentralFrameId");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentLocationReconciliationCaptures_Work_Generation_Cursor",
                table: "DeploymentLocationReconciliationCaptures",
                columns: new[] { "DeploymentLocationReconciliationWorkId", "AuthorityConcurrencyToken", "FirstReceivedAtUtc", "CentralFrameId" });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentLocationReconciliationWork_DeviceDeploymentLocationVersionId",
                table: "DeploymentLocationReconciliationWork",
                column: "DeviceDeploymentLocationVersionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentLocationReconciliationWork_Status_LeaseExpiresAtUtc_CreatedAtUtc_Id",
                table: "DeploymentLocationReconciliationWork",
                columns: new[] { "Status", "LeaseExpiresAtUtc", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentLocationReconciliationWork_Status_NextAttemptAtUtc_CreatedAtUtc_Id",
                table: "DeploymentLocationReconciliationWork",
                columns: new[] { "Status", "NextAttemptAtUtc", "CreatedAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "DeploymentLocationReconciliationCaptures");

            migrationBuilder.DropTable(
                name: "DeploymentLocationReconciliationWork");

            migrationBuilder.DropIndex(
                name: "IX_CentralFrames_RegistrationId_FirstReceivedAtUtc_Id",
                table: "CentralFrames");
        }
    }
}
