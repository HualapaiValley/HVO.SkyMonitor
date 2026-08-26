using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStructuredProcessingProducts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<int>(
                name: "ExpectedRole",
                table: "CentralArtifactSources",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpectedVariant",
                table: "CentralArtifactSources",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpectedRecipeIdentitySha256",
                table: "CentralArtifactSources",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ManifestSchemaVersion",
                table: "CentralArtifacts",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(16)",
                oldMaxLength: 16);

            migrationBuilder.AlterColumn<string>(
                name: "ManifestSchemaVersion",
                table: "CentralArtifactIngestIdentities",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(16)",
                oldMaxLength: 16);

            migrationBuilder.CreateTable(
                name: "CentralStructuredProcessingProducts",
                columns: table => new
                {
                    CentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OutputIdentitySha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProductKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ProductSchemaVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SourceIdentitySha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ContentIdentitySha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DescriptorJson = table.Column<string>(type: "nvarchar(max)", maxLength: 65536, nullable: false),
                    AlgorithmsJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CompatibilityJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    TotalIntegrationTicks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralStructuredProcessingProducts", x => x.CentralArtifactId);
                    table.CheckConstraint(
                        "CK_CentralStructuredProcessingProducts_DescriptorJson_Length",
                        "LEN([DescriptorJson]) <= 65536");
                    table.ForeignKey(
                        name: "FK_CentralStructuredProcessingProducts_CentralArtifacts_CentralArtifactId",
                        column: x => x.CentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CentralStructuredProcessingProducts_OutputIdentitySha256",
                table: "CentralStructuredProcessingProducts",
                column: "OutputIdentitySha256");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [CentralArtifacts]
                           WHERE [ManifestSchemaVersion] = N'hvo-structured-processing-product-v1')
                    THROW 51000, 'Cannot downgrade while structured processing products remain archived.', 1;
                IF EXISTS (SELECT 1 FROM [CentralArtifacts] WHERE LEN([ManifestSchemaVersion]) > 16)
                    THROW 51000, 'Cannot narrow CentralArtifacts.ManifestSchemaVersion while longer schema values remain.', 1;
                IF EXISTS (SELECT 1 FROM [CentralArtifactIngestIdentities] WHERE LEN([ManifestSchemaVersion]) > 16)
                    THROW 51000, 'Cannot narrow CentralArtifactIngestIdentities.ManifestSchemaVersion while longer schema values remain.', 1;
                """);

            migrationBuilder.DropTable(
                name: "CentralStructuredProcessingProducts");

            migrationBuilder.DropColumn(
                name: "ExpectedRecipeIdentitySha256",
                table: "CentralArtifactSources");

            migrationBuilder.DropColumn(
                name: "ExpectedRole",
                table: "CentralArtifactSources");

            migrationBuilder.DropColumn(
                name: "ExpectedVariant",
                table: "CentralArtifactSources");

            migrationBuilder.AlterColumn<string>(
                name: "ManifestSchemaVersion",
                table: "CentralArtifacts",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "ManifestSchemaVersion",
                table: "CentralArtifactIngestIdentities",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64);
        }
    }
}
