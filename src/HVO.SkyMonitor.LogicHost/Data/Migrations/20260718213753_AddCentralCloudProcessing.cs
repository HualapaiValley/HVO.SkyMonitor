using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCentralCloudProcessing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<string>(
                name: "ExpectedRecipeIdentitySha256",
                table: "CentralDerivativeJobs",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE [CentralDerivativeJobs]
                SET [ExpectedRecipeIdentitySha256] = [RequestedRecipeIdentitySha256];
                """);

            migrationBuilder.AlterColumn<string>(
                name: "ExpectedRecipeIdentitySha256",
                table: "CentralDerivativeJobs",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(64)",
                oldUnicode: false,
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ExpectedCentralArtifactId",
                table: "CentralDerivativeJobInputRequirements",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CentralClearReferenceDesignations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegistrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RigId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralClearReferenceDesignations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CentralClearReferenceDesignations_CentralArtifacts_CentralArtifactId",
                        column: x => x.CentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralClearReferenceDesignations_DeviceRegistrations_RegistrationId",
                        column: x => x.RegistrationId,
                        principalTable: "DeviceRegistrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralDerivativeJobCanonicalInputs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralDerivativeJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralDerivativeJobInputRequirementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    SchemaVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    CanonicalJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ByteLength = table.Column<int>(type: "int", nullable: false),
                    EnvironmentalObservationRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SelectedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralDerivativeJobCanonicalInputs", x => x.Id);
                    table.CheckConstraint("CK_CentralDerivativeJobCanonicalInputs_ByteLength", "[ByteLength] > 0");
                    table.CheckConstraint("CK_CentralDerivativeJobCanonicalInputs_Ordinal", "[Ordinal] >= 0");
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobCanonicalInputs_CentralDerivativeJobInputRequirements_CentralDerivativeJobId_CentralDerivativeJobInputRe~",
                        columns: x => new { x.CentralDerivativeJobId, x.CentralDerivativeJobInputRequirementId },
                        principalTable: "CentralDerivativeJobInputRequirements",
                        principalColumns: new[] { "CentralDerivativeJobId", "Id" });
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobCanonicalInputs_CentralDerivativeJobs_CentralDerivativeJobId",
                        column: x => x.CentralDerivativeJobId,
                        principalTable: "CentralDerivativeJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobCanonicalInputs_EnvironmentalObservations_EnvironmentalObservationRecordId",
                        column: x => x.EnvironmentalObservationRecordId,
                        principalTable: "EnvironmentalObservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobInputRequirements_ExpectedCentralArtifactId",
                table: "CentralDerivativeJobInputRequirements",
                column: "ExpectedCentralArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralClearReferenceDesignations_CentralArtifactId",
                table: "CentralClearReferenceDesignations",
                column: "CentralArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralClearReferenceDesignations_RegistrationId_RigId",
                table: "CentralClearReferenceDesignations",
                columns: new[] { "RegistrationId", "RigId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobCanonicalInputs_CentralDerivativeJobId_CentralDerivativeJobInputRequirementId",
                table: "CentralDerivativeJobCanonicalInputs",
                columns: new[] { "CentralDerivativeJobId", "CentralDerivativeJobInputRequirementId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobCanonicalInputs_CentralDerivativeJobId_Ordinal",
                table: "CentralDerivativeJobCanonicalInputs",
                columns: new[] { "CentralDerivativeJobId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobCanonicalInputs_EnvironmentalObservationRecordId",
                table: "CentralDerivativeJobCanonicalInputs",
                column: "EnvironmentalObservationRecordId");

            migrationBuilder.AddForeignKey(
                name: "FK_CentralDerivativeJobInputRequirements_CentralArtifacts_ExpectedCentralArtifactId",
                table: "CentralDerivativeJobInputRequirements",
                column: "ExpectedCentralArtifactId",
                principalTable: "CentralArtifacts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropForeignKey(
                name: "FK_CentralDerivativeJobInputRequirements_CentralArtifacts_ExpectedCentralArtifactId",
                table: "CentralDerivativeJobInputRequirements");

            migrationBuilder.DropTable(
                name: "CentralClearReferenceDesignations");

            migrationBuilder.DropTable(
                name: "CentralDerivativeJobCanonicalInputs");

            migrationBuilder.DropIndex(
                name: "IX_CentralDerivativeJobInputRequirements_ExpectedCentralArtifactId",
                table: "CentralDerivativeJobInputRequirements");

            migrationBuilder.DropColumn(
                name: "ExpectedRecipeIdentitySha256",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropColumn(
                name: "ExpectedCentralArtifactId",
                table: "CentralDerivativeJobInputRequirements");
        }
    }
}
