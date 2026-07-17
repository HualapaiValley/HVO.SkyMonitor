using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCentralRecoveryRetrySchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.DropIndex(
                name: "IX_CentralArtifacts_ReconstructionState_ReceivedAtUtc_Id",
                table: "CentralArtifacts");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReferenceRetryAtUtc",
                table: "CentralArtifacts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReferenceRetryCount",
                table: "CentralArtifacts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifacts_ReconstructionState_ReferenceRetryAtUtc_ReceivedAtUtc_Id",
                table: "CentralArtifacts",
                columns: new[] { "ReconstructionState", "ReferenceRetryAtUtc", "ReceivedAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.DropIndex(
                name: "IX_CentralArtifacts_ReconstructionState_ReferenceRetryAtUtc_ReceivedAtUtc_Id",
                table: "CentralArtifacts");

            migrationBuilder.DropColumn(
                name: "ReferenceRetryAtUtc",
                table: "CentralArtifacts");

            migrationBuilder.DropColumn(
                name: "ReferenceRetryCount",
                table: "CentralArtifacts");

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifacts_ReconstructionState_ReceivedAtUtc_Id",
                table: "CentralArtifacts",
                columns: new[] { "ReconstructionState", "ReceivedAtUtc", "Id" });
        }
    }
}
