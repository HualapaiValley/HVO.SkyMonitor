using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeploymentLocationAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<double>(
                name: "AllowedDeploymentRadiusMeters",
                table: "Observatories",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentLocationCanonicalSha256",
                table: "Observatories",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CurrentLocationVersion",
                table: "Observatories",
                type: "bigint",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "EnvelopeVersion",
                table: "DeviceRegistrations",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "v2",
                oldClrType: typeof(string),
                oldType: "nvarchar(16)",
                oldMaxLength: 16,
                oldDefaultValue: "v1");

            migrationBuilder.AddColumn<string>(
                name: "LocationEvidenceState",
                table: "DeviceRegistrations",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "LegacyIncomplete");

            migrationBuilder.AddColumn<string>(
                name: "ObservatoryLocationCanonicalSha256",
                table: "DeviceRegistrations",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ObservatoryLocationVersion",
                table: "DeviceRegistrations",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationEvidenceState",
                table: "CentralFrames",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "LegacyIncomplete");

            migrationBuilder.CreateTable(
                name: "ObservatoryLocationVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CanonicalSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EffectiveFromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SupersededAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LatitudeDegrees = table.Column<double>(type: "float", nullable: false),
                    LongitudeDegrees = table.Column<double>(type: "float", nullable: false),
                    ElevationMeters = table.Column<double>(type: "float", nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AllowedDeploymentRadiusMeters = table.Column<double>(type: "float", nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObservatoryLocationVersions", x => x.Id);
                    table.CheckConstraint("CK_ObservatoryLocationVersions_Interval", "[SupersededAtUtc] IS NULL OR [SupersededAtUtc] >= [EffectiveFromUtc]");
                    table.CheckConstraint("CK_ObservatoryLocationVersions_Latitude", "[LatitudeDegrees] >= -90 AND [LatitudeDegrees] <= 90");
                    table.CheckConstraint("CK_ObservatoryLocationVersions_Longitude", "[LongitudeDegrees] >= -180 AND [LongitudeDegrees] <= 180");
                    table.CheckConstraint("CK_ObservatoryLocationVersions_Radius", "[AllowedDeploymentRadiusMeters] IS NULL OR [AllowedDeploymentRadiusMeters] >= 0");
                    table.CheckConstraint("CK_ObservatoryLocationVersions_Version", "[Version] >= 1");
                    table.ForeignKey(
                        name: "FK_ObservatoryLocationVersions_Observatories_ObservatoryId",
                        column: x => x.ObservatoryId,
                        principalTable: "Observatories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeviceDeploymentLocationVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegistrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DevicePublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ObservatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservatoryLocationVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservatoryLocationVersionNumber = table.Column<long>(type: "bigint", nullable: false),
                    ObservatoryLocationCanonicalSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LocationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CanonicalSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    SourceKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    HorizontalAccuracyMeters = table.Column<double>(type: "float", nullable: true),
                    EffectiveFromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EffectiveUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LatitudeDegrees = table.Column<double>(type: "float", nullable: false),
                    LongitudeDegrees = table.Column<double>(type: "float", nullable: false),
                    ElevationMeters = table.Column<double>(type: "float", nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ProposedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolvedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ConcurrencyToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceDeploymentLocationVersions", x => x.Id);
                    table.CheckConstraint("CK_DeviceDeploymentLocationVersions_Accuracy", "[HorizontalAccuracyMeters] IS NULL OR [HorizontalAccuracyMeters] >= 0");
                    table.CheckConstraint("CK_DeviceDeploymentLocationVersions_Interval", "[EffectiveUntilUtc] IS NULL OR [EffectiveUntilUtc] > [EffectiveFromUtc]");
                    table.CheckConstraint("CK_DeviceDeploymentLocationVersions_Latitude", "[LatitudeDegrees] >= -90 AND [LatitudeDegrees] <= 90");
                    table.CheckConstraint("CK_DeviceDeploymentLocationVersions_Longitude", "[LongitudeDegrees] >= -180 AND [LongitudeDegrees] <= 180");
                    table.CheckConstraint("CK_DeviceDeploymentLocationVersions_Resolution", "([Status] = N'Pending' AND [ResolvedAtUtc] IS NULL) OR ([Status] <> N'Pending' AND [ResolvedAtUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_DeviceDeploymentLocationVersions_Version", "[Version] >= 1 AND [ObservatoryLocationVersionNumber] >= 1");
                    table.ForeignKey(
                        name: "FK_DeviceDeploymentLocationVersions_DeviceRegistrations_RegistrationId",
                        column: x => x.RegistrationId,
                        principalTable: "DeviceRegistrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DeviceDeploymentLocationVersions_ObservatoryLocationVersions_ObservatoryLocationVersionId",
                        column: x => x.ObservatoryLocationVersionId,
                        principalTable: "ObservatoryLocationVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralCaptureLocations",
                columns: table => new
                {
                    CentralFrameId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceDeploymentLocationVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LocationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    HorizontalAccuracyMeters = table.Column<double>(type: "float", nullable: true),
                    EffectiveFromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EffectiveUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralCaptureLocations", x => x.CentralFrameId);
                    table.CheckConstraint("CK_CentralCaptureLocations_Accuracy", "[HorizontalAccuracyMeters] IS NULL OR [HorizontalAccuracyMeters] >= 0");
                    table.CheckConstraint("CK_CentralCaptureLocations_Interval", "[EffectiveUntilUtc] IS NULL OR [EffectiveUntilUtc] > [EffectiveFromUtc]");
                    table.CheckConstraint("CK_CentralCaptureLocations_Version", "[Version] >= 1");
                    table.ForeignKey(
                        name: "FK_CentralCaptureLocations_CentralFrames_CentralFrameId",
                        column: x => x.CentralFrameId,
                        principalTable: "CentralFrames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CentralCaptureLocations_DeviceDeploymentLocationVersions_DeviceDeploymentLocationVersionId",
                        column: x => x.DeviceDeploymentLocationVersionId,
                        principalTable: "DeviceDeploymentLocationVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeploymentLocationResolutionAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceDeploymentLocationVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegistrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PreviousStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    NewStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentLocationResolutionAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeploymentLocationResolutionAudits_DeviceDeploymentLocationVersions_DeviceDeploymentLocationVersionId",
                        column: x => x.DeviceDeploymentLocationVersionId,
                        principalTable: "DeviceDeploymentLocationVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CentralFrames_LocationEvidenceState",
                table: "CentralFrames",
                column: "LocationEvidenceState");

            migrationBuilder.CreateIndex(
                name: "IX_CentralFrames_RegistrationId",
                table: "CentralFrames",
                column: "RegistrationId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralCaptureLocations_DeviceDeploymentLocationVersionId",
                table: "CentralCaptureLocations",
                column: "DeviceDeploymentLocationVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralCaptureLocations_LocationId_Version",
                table: "CentralCaptureLocations",
                columns: new[] { "LocationId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentLocationResolutionAudits_DeviceDeploymentLocationVersionId",
                table: "DeploymentLocationResolutionAudits",
                column: "DeviceDeploymentLocationVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentLocationResolutionAudits_RegistrationId_OccurredAtUtc",
                table: "DeploymentLocationResolutionAudits",
                columns: new[] { "RegistrationId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceDeploymentLocationVersions_ObservatoryId_Status",
                table: "DeviceDeploymentLocationVersions",
                columns: new[] { "ObservatoryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceDeploymentLocationVersions_ObservatoryLocationVersionId",
                table: "DeviceDeploymentLocationVersions",
                column: "ObservatoryLocationVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceDeploymentLocationVersions_RegistrationId_LocationId_Version_ObservatoryLocationVersionId",
                table: "DeviceDeploymentLocationVersions",
                columns: new[] { "RegistrationId", "LocationId", "Version", "ObservatoryLocationVersionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceDeploymentLocationVersions_RegistrationId_ProposedAtUtc_Id",
                table: "DeviceDeploymentLocationVersions",
                columns: new[] { "RegistrationId", "ProposedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceDeploymentLocationVersions_RegistrationId_Status_ProposedAtUtc_Id",
                table: "DeviceDeploymentLocationVersions",
                columns: new[] { "RegistrationId", "Status", "ProposedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ObservatoryLocationVersions_CanonicalSha256",
                table: "ObservatoryLocationVersions",
                column: "CanonicalSha256");

            migrationBuilder.CreateIndex(
                name: "IX_ObservatoryLocationVersions_ObservatoryId",
                table: "ObservatoryLocationVersions",
                column: "ObservatoryId",
                unique: true,
                filter: "[SupersededAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ObservatoryLocationVersions_ObservatoryId_Version",
                table: "ObservatoryLocationVersions",
                columns: new[] { "ObservatoryId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "CentralCaptureLocations");

            migrationBuilder.DropTable(
                name: "DeploymentLocationResolutionAudits");

            migrationBuilder.DropTable(
                name: "DeviceDeploymentLocationVersions");

            migrationBuilder.DropTable(
                name: "ObservatoryLocationVersions");

            migrationBuilder.DropIndex(
                name: "IX_CentralFrames_LocationEvidenceState",
                table: "CentralFrames");

            migrationBuilder.DropIndex(
                name: "IX_CentralFrames_RegistrationId",
                table: "CentralFrames");

            migrationBuilder.DropColumn(
                name: "AllowedDeploymentRadiusMeters",
                table: "Observatories");

            migrationBuilder.DropColumn(
                name: "CurrentLocationCanonicalSha256",
                table: "Observatories");

            migrationBuilder.DropColumn(
                name: "CurrentLocationVersion",
                table: "Observatories");

            migrationBuilder.DropColumn(
                name: "LocationEvidenceState",
                table: "DeviceRegistrations");

            migrationBuilder.DropColumn(
                name: "ObservatoryLocationCanonicalSha256",
                table: "DeviceRegistrations");

            migrationBuilder.DropColumn(
                name: "ObservatoryLocationVersion",
                table: "DeviceRegistrations");

            migrationBuilder.DropColumn(
                name: "LocationEvidenceState",
                table: "CentralFrames");

            migrationBuilder.AlterColumn<string>(
                name: "EnvelopeVersion",
                table: "DeviceRegistrations",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "v1",
                oldClrType: typeof(string),
                oldType: "nvarchar(16)",
                oldMaxLength: 16,
                oldDefaultValue: "v2");
        }
    }
}
