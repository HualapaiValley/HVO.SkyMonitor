using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddArtifactIngestMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<Guid>(
                name: "ArtifactId",
                table: "DeviceImageUploads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArtifactRole",
                table: "DeviceImageUploads",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ByteLength",
                table: "DeviceImageUploads",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChecksumSha256",
                table: "DeviceImageUploads",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "DeviceImageUploads",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceImageUploads_IdempotencyKey",
                table: "DeviceImageUploads",
                column: "IdempotencyKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "IX_DeviceImageUploads_IdempotencyKey",
                table: "DeviceImageUploads");

            migrationBuilder.DropColumn(
                name: "ArtifactId",
                table: "DeviceImageUploads");

            migrationBuilder.DropColumn(
                name: "ArtifactRole",
                table: "DeviceImageUploads");

            migrationBuilder.DropColumn(
                name: "ByteLength",
                table: "DeviceImageUploads");

            migrationBuilder.DropColumn(
                name: "ChecksumSha256",
                table: "DeviceImageUploads");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "DeviceImageUploads");
        }
    }
}
