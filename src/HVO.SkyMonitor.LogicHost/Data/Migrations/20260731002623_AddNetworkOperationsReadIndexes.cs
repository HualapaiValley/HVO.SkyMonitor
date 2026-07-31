using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNetworkOperationsReadIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateIndex(
                name: "IX_ObservatoryMemberships_ObservatoryId_AddedAtUtc_UserId",
                table: "ObservatoryMemberships",
                columns: new[] { "ObservatoryId", "AddedAtUtc", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ObservatoryInvitations_ObservatoryId_ExpiresAtUtc_Id",
                table: "ObservatoryInvitations",
                columns: new[] { "ObservatoryId", "ExpiresAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_LogicalCameras_ObservatoryId_Name_Id",
                table: "LogicalCameras",
                columns: new[] { "ObservatoryId", "Name", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_LogicalCameras_ObservatoryId_Name_Slug",
                table: "LogicalCameras",
                columns: new[] { "ObservatoryId", "Name", "Slug" });

            migrationBuilder.CreateIndex(
                name: "IX_LogicalCameraInstallations_LogicalCameraId_AssignedAtUtc_InstallationPublicId",
                table: "LogicalCameraInstallations",
                columns: new[] { "LogicalCameraId", "AssignedAtUtc", "InstallationPublicId" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRegistrations_ObservatoryId_Status_FriendlyName_Id",
                table: "DeviceRegistrations",
                columns: new[] { "ObservatoryId", "Status", "FriendlyName", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralFrames_LogicalCameraInstallationId_CapturedAtUtc_Id",
                table: "CentralFrames",
                columns: new[] { "LogicalCameraInstallationId", "CapturedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralFrames_ObservatoryId_CapturedAtUtc_Id",
                table: "CentralFrames",
                columns: new[] { "ObservatoryId", "CapturedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifacts_CentralFrameId_ReceivedAtUtc_ArtifactId",
                table: "CentralArtifacts",
                columns: new[] { "CentralFrameId", "ReceivedAtUtc", "ArtifactId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "IX_ObservatoryMemberships_ObservatoryId_AddedAtUtc_UserId",
                table: "ObservatoryMemberships");

            migrationBuilder.DropIndex(
                name: "IX_ObservatoryInvitations_ObservatoryId_ExpiresAtUtc_Id",
                table: "ObservatoryInvitations");

            migrationBuilder.DropIndex(
                name: "IX_LogicalCameras_ObservatoryId_Name_Id",
                table: "LogicalCameras");

            migrationBuilder.DropIndex(
                name: "IX_LogicalCameras_ObservatoryId_Name_Slug",
                table: "LogicalCameras");

            migrationBuilder.DropIndex(
                name: "IX_LogicalCameraInstallations_LogicalCameraId_AssignedAtUtc_InstallationPublicId",
                table: "LogicalCameraInstallations");

            migrationBuilder.DropIndex(
                name: "IX_DeviceRegistrations_ObservatoryId_Status_FriendlyName_Id",
                table: "DeviceRegistrations");

            migrationBuilder.DropIndex(
                name: "IX_CentralFrames_LogicalCameraInstallationId_CapturedAtUtc_Id",
                table: "CentralFrames");

            migrationBuilder.DropIndex(
                name: "IX_CentralFrames_ObservatoryId_CapturedAtUtc_Id",
                table: "CentralFrames");

            migrationBuilder.DropIndex(
                name: "IX_CentralArtifacts_CentralFrameId_ReceivedAtUtc_ArtifactId",
                table: "CentralArtifacts");
        }
    }
}
