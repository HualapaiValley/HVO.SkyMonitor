using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableArtifactVerificationReservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ObjectVerificationRequestedAtUtc",
                table: "CentralArtifacts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ObjectVerificationToken",
                table: "CentralArtifacts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ObjectVerificationRetryAtUtc",
                table: "CentralArtifacts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ObjectVerificationRetryCount",
                table: "CentralArtifacts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifacts_ObjectVerificationRetryAtUtc_ObjectVerificationRequestedAtUtc_Id",
                table: "CentralArtifacts",
                columns: new[] { "ObjectVerificationRetryAtUtc", "ObjectVerificationRequestedAtUtc", "Id" },
                filter: "[ObjectVerificationToken] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CentralArtifacts_ObjectVerification",
                table: "CentralArtifacts",
                sql: "([ObjectVerificationToken] IS NULL AND [ObjectVerificationRequestedAtUtc] IS NULL AND [ObjectVerificationRetryCount] = 0 AND [ObjectVerificationRetryAtUtc] IS NULL) OR ([ObjectVerificationToken] IS NOT NULL AND [ObjectVerificationRequestedAtUtc] IS NOT NULL AND [ObjectState] = N'Pending')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "IX_CentralArtifacts_ObjectVerificationRetryAtUtc_ObjectVerificationRequestedAtUtc_Id",
                table: "CentralArtifacts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CentralArtifacts_ObjectVerification",
                table: "CentralArtifacts");

            migrationBuilder.DropColumn(
                name: "ObjectVerificationRequestedAtUtc",
                table: "CentralArtifacts");

            migrationBuilder.DropColumn(
                name: "ObjectVerificationRetryAtUtc",
                table: "CentralArtifacts");

            migrationBuilder.DropColumn(
                name: "ObjectVerificationRetryCount",
                table: "CentralArtifacts");

            migrationBuilder.DropColumn(
                name: "ObjectVerificationToken",
                table: "CentralArtifacts");
        }
    }
}
