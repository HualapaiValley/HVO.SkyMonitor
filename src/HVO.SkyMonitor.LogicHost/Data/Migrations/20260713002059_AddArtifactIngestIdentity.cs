using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddArtifactIngestIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.AddColumn<Guid>(
                name: "FrameId",
                table: "DeviceImageUploads",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManifestSchemaVersion",
                table: "DeviceImageUploads",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecipeVersion",
                table: "DeviceImageUploads",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceImageUploads_AgentId_FrameId_ArtifactRole_RecipeVersion",
                table: "DeviceImageUploads",
                columns: new[] { "AgentId", "FrameId", "ArtifactRole", "RecipeVersion" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.DropIndex(
                name: "IX_DeviceImageUploads_AgentId_FrameId_ArtifactRole_RecipeVersion",
                table: "DeviceImageUploads");

            migrationBuilder.DropColumn(
                name: "FrameId",
                table: "DeviceImageUploads");

            migrationBuilder.DropColumn(
                name: "ManifestSchemaVersion",
                table: "DeviceImageUploads");

            migrationBuilder.DropColumn(
                name: "RecipeVersion",
                table: "DeviceImageUploads");
        }
    }
}
