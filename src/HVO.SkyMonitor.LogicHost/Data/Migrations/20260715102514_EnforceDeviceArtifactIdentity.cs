using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnforceDeviceArtifactIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.DropForeignKey(
                name: "FK_DeviceRegistrations_Observatories_ObservatoryId",
                table: "DeviceRegistrations");

            migrationBuilder.AddColumn<Guid>(
                name: "DevicePublicId",
                table: "CentralArtifacts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.Sql("""
                EXEC(N'
                    UPDATE artifact
                    SET artifact.DevicePublicId = frame.DevicePublicId
                    FROM CentralArtifacts AS artifact
                    INNER JOIN CentralFrames AS frame ON frame.Id = artifact.CentralFrameId
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM CentralArtifacts AS duplicateArtifact
                        INNER JOIN CentralFrames AS duplicateFrame
                            ON duplicateFrame.Id = duplicateArtifact.CentralFrameId
                        WHERE duplicateArtifact.Id <> artifact.Id
                            AND duplicateArtifact.ArtifactId = artifact.ArtifactId
                            AND duplicateFrame.DevicePublicId = frame.DevicePublicId);
                ');
                """);

            migrationBuilder.Sql("""
                EXEC(N'
                    CREATE UNIQUE INDEX [IX_CentralArtifacts_DevicePublicId_ArtifactId]
                    ON [CentralArtifacts] ([DevicePublicId], [ArtifactId])
                    WHERE [DevicePublicId] IS NOT NULL;
                ');
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifacts_ObjectState_ReconstructionState_ReceivedAtUtc",
                table: "CentralArtifacts",
                columns: new[] { "ObjectState", "ReconstructionState", "ReceivedAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_DeviceRegistrations_Observatories_ObservatoryId",
                table: "DeviceRegistrations",
                column: "ObservatoryId",
                principalTable: "Observatories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.DropForeignKey(
                name: "FK_DeviceRegistrations_Observatories_ObservatoryId",
                table: "DeviceRegistrations");

            migrationBuilder.DropIndex(
                name: "IX_CentralArtifacts_DevicePublicId_ArtifactId",
                table: "CentralArtifacts");

            migrationBuilder.DropIndex(
                name: "IX_CentralArtifacts_ObjectState_ReconstructionState_ReceivedAtUtc",
                table: "CentralArtifacts");

            migrationBuilder.DropColumn(
                name: "DevicePublicId",
                table: "CentralArtifacts");

            migrationBuilder.AddForeignKey(
                name: "FK_DeviceRegistrations_Observatories_ObservatoryId",
                table: "DeviceRegistrations",
                column: "ObservatoryId",
                principalTable: "Observatories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
