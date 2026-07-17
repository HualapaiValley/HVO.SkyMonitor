using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCentralRecoveryInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.AlterColumn<string>(
                name: "StorageReference",
                table: "CentralArtifacts",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(512)",
                oldMaxLength: 512);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ObjectVerifiedAtUtc",
                table: "CentralArtifacts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RecoveryGeneration",
                table: "CentralArtifacts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "CentralObjectRecoveryDispositions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceObjectIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    SourceObjectKey = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false, collation: "Latin1_General_100_BIN2"),
                    TargetObjectKey = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true, collation: "Latin1_General_100_BIN2"),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ByteLength = table.Column<long>(type: "bigint", nullable: false),
                    ContentChecksumSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralObjectRecoveryDispositions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CentralRecoveryCheckpoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    Phase = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ObjectPartition = table.Column<int>(type: "int", nullable: false),
                    ObjectCursor = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    StagingPartition = table.Column<int>(type: "int", nullable: false),
                    StagingCursor = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    NextInventoryAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    InventoryStartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastProgressAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastCompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastCycleAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastFailureAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FindingCount = table.Column<long>(type: "bigint", nullable: false),
                    FindingBytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralRecoveryCheckpoints", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifacts_ObjectState_ObjectVerifiedAtUtc_ReceivedAtUtc_Id",
                table: "CentralArtifacts",
                columns: new[] { "ObjectState", "ObjectVerifiedAtUtc", "ReceivedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifacts_ObjectState_RecoveryGeneration_Id",
                table: "CentralArtifacts",
                columns: new[] { "ObjectState", "RecoveryGeneration", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifacts_ReconstructionState_ReceivedAtUtc_Id",
                table: "CentralArtifacts",
                columns: new[] { "ReconstructionState", "ReceivedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifacts_StorageReference",
                table: "CentralArtifacts",
                column: "StorageReference");

            migrationBuilder.CreateIndex(
                name: "IX_CentralObjectRecoveryDispositions_SourceObjectIdentitySha256",
                table: "CentralObjectRecoveryDispositions",
                column: "SourceObjectIdentitySha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralObjectRecoveryDispositions_State_UpdatedAtUtc_Id",
                table: "CentralObjectRecoveryDispositions",
                columns: new[] { "State", "UpdatedAtUtc", "Id" });

            migrationBuilder.InsertData(
                table: "CentralRecoveryCheckpoints",
                columns: new[] { "Id", "FindingBytes", "FindingCount", "Generation", "NextInventoryAtUtc", "ObjectPartition", "Phase", "StagingPartition" },
                values: new object[] { 1, 0L, 0L, 0L, DateTimeOffset.UnixEpoch, 0, "Idle", 0 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.DropTable(
                name: "CentralObjectRecoveryDispositions");

            migrationBuilder.DropTable(
                name: "CentralRecoveryCheckpoints");

            migrationBuilder.DropIndex(
                name: "IX_CentralArtifacts_ObjectState_ObjectVerifiedAtUtc_ReceivedAtUtc_Id",
                table: "CentralArtifacts");

            migrationBuilder.DropIndex(
                name: "IX_CentralArtifacts_ObjectState_RecoveryGeneration_Id",
                table: "CentralArtifacts");

            migrationBuilder.DropIndex(
                name: "IX_CentralArtifacts_ReconstructionState_ReceivedAtUtc_Id",
                table: "CentralArtifacts");

            migrationBuilder.DropIndex(
                name: "IX_CentralArtifacts_StorageReference",
                table: "CentralArtifacts");

            migrationBuilder.DropColumn(
                name: "ObjectVerifiedAtUtc",
                table: "CentralArtifacts");

            migrationBuilder.DropColumn(
                name: "RecoveryGeneration",
                table: "CentralArtifacts");

            migrationBuilder.AlterColumn<string>(
                name: "StorageReference",
                table: "CentralArtifacts",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(512)",
                oldMaxLength: 512,
                oldCollation: "Latin1_General_100_BIN2");
        }
    }
}
