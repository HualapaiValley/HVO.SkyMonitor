using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReconstructableCentralIngest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.DropForeignKey(
                name: "FK_DeviceRigProfiles_DeviceRegistrations_RegistrationId",
                table: "DeviceRigProfiles");

            migrationBuilder.DropIndex(
                name: "IX_CentralArtifacts_CentralFrameId_Role_RecipeVersion",
                table: "CentralArtifacts");

            migrationBuilder.AddColumn<string>(
                name: "ProfileName",
                table: "DeviceRigProfiles",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProfileSha256",
                table: "DeviceRigProfiles",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProfileVersion",
                table: "DeviceRigProfiles",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CaptureSequence",
                table: "CentralFrames",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CycleEvidenceJson",
                table: "CentralFrames",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeviceRigProfileId",
                table: "CentralFrames",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RigId",
                table: "CentralFrames",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedUtc",
                table: "CentralArtifacts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ObjectState",
                table: "CentralArtifacts",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Available");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReconciledAtUtc",
                table: "CentralArtifacts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReconstructionState",
                table: "CentralArtifacts",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "LegacyIncomplete");

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "CentralArtifacts",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: Array.Empty<byte>());

            migrationBuilder.AddColumn<string>(
                name: "SourceId",
                table: "CentralArtifacts",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StateReasonCode",
                table: "CentralArtifacts",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Variant",
                table: "CentralArtifacts",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CentralArtifactIngestIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ManifestSchemaVersion = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralArtifactIngestIdentities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CentralArtifactIngestIdentities_CentralArtifacts_CentralArtifactId",
                        column: x => x.CentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CentralArtifactLayouts",
                columns: table => new
                {
                    CentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Width = table.Column<int>(type: "int", nullable: false),
                    Height = table.Column<int>(type: "int", nullable: false),
                    StrideBytes = table.Column<int>(type: "int", nullable: false),
                    PixelFormat = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ByteOrder = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SampleDepthBits = table.Column<int>(type: "int", nullable: false),
                    ContainerDepthBits = table.Column<int>(type: "int", nullable: false),
                    Packing = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CfaPattern = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    BlackLevel = table.Column<double>(type: "float", nullable: true),
                    WhiteLevel = table.Column<double>(type: "float", nullable: true),
                    ByteLength = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralArtifactLayouts", x => x.CentralArtifactId);
                    table.ForeignKey(
                        name: "FK_CentralArtifactLayouts_CentralArtifacts_CentralArtifactId",
                        column: x => x.CentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CentralArtifactRecipes",
                columns: table => new
                {
                    CentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SemanticVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ImplementationVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OptionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OptionsSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralArtifactRecipes", x => x.CentralArtifactId);
                    table.ForeignKey(
                        name: "FK_CentralArtifactRecipes_CentralArtifacts_CentralArtifactId",
                        column: x => x.CentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CentralArtifactSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    SourceArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResolvedCentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralArtifactSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CentralArtifactSources_CentralArtifacts_CentralArtifactId",
                        column: x => x.CentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CentralArtifactSources_CentralArtifacts_ResolvedCentralArtifactId",
                        column: x => x.ResolvedCentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralCaptureControls",
                columns: table => new
                {
                    CentralFrameId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedExposureTicks = table.Column<long>(type: "bigint", nullable: false),
                    EffectiveExposureTicks = table.Column<long>(type: "bigint", nullable: false),
                    RequestedGain = table.Column<double>(type: "float", nullable: false),
                    EffectiveGain = table.Column<double>(type: "float", nullable: false),
                    RequestedOffset = table.Column<double>(type: "float", nullable: true),
                    EffectiveOffset = table.Column<double>(type: "float", nullable: true),
                    TemperatureSetpointC = table.Column<double>(type: "float", nullable: true),
                    EffectiveTemperatureC = table.Column<double>(type: "float", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralCaptureControls", x => x.CentralFrameId);
                    table.ForeignKey(
                        name: "FK_CentralCaptureControls_CentralFrames_CentralFrameId",
                        column: x => x.CentralFrameId,
                        principalTable: "CentralFrames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CentralCaptureProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralFrameId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DeviceRigProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralCaptureProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CentralCaptureProfiles_CentralFrames_CentralFrameId",
                        column: x => x.CentralFrameId,
                        principalTable: "CentralFrames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CentralCaptureProfiles_DeviceRigProfiles_DeviceRigProfileId",
                        column: x => x.DeviceRigProfileId,
                        principalTable: "DeviceRigProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralCaptureTimings",
                columns: table => new
                {
                    CentralFrameId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedStartUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExposureStartedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExposureEndedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReadoutCompletedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DurableIngressUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SetpointAppliedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralCaptureTimings", x => x.CentralFrameId);
                    table.ForeignKey(
                        name: "FK_CentralCaptureTimings_CentralFrames_CentralFrameId",
                        column: x => x.CentralFrameId,
                        principalTable: "CentralFrames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRigProfiles_DevicePublicId_ProfileName_ProfileVersion_ProfileSha256",
                table: "DeviceRigProfiles",
                columns: new[] { "DevicePublicId", "ProfileName", "ProfileVersion", "ProfileSha256" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralFrames_DevicePublicId_CaptureSequence",
                table: "CentralFrames",
                columns: new[] { "DevicePublicId", "CaptureSequence" },
                unique: true,
                filter: "[CaptureSequence] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralFrames_DeviceRigProfileId",
                table: "CentralFrames",
                column: "DeviceRigProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifacts_CentralFrameId_Role_RecipeVersion",
                table: "CentralArtifacts",
                columns: new[] { "CentralFrameId", "Role", "RecipeVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifactIngestIdentities_CentralArtifactId_ManifestSchemaVersion",
                table: "CentralArtifactIngestIdentities",
                columns: new[] { "CentralArtifactId", "ManifestSchemaVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifactIngestIdentities_IdempotencyKey",
                table: "CentralArtifactIngestIdentities",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifactSources_CentralArtifactId_Ordinal",
                table: "CentralArtifactSources",
                columns: new[] { "CentralArtifactId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifactSources_CentralArtifactId_SourceArtifactId",
                table: "CentralArtifactSources",
                columns: new[] { "CentralArtifactId", "SourceArtifactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifactSources_ResolvedCentralArtifactId",
                table: "CentralArtifactSources",
                column: "ResolvedCentralArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralCaptureProfiles_CentralFrameId_Kind",
                table: "CentralCaptureProfiles",
                columns: new[] { "CentralFrameId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralCaptureProfiles_DeviceRigProfileId",
                table: "CentralCaptureProfiles",
                column: "DeviceRigProfileId");

            migrationBuilder.Sql("""
                EXEC(N'
                    INSERT INTO [CentralArtifactIngestIdentities]
                        ([Id], [CentralArtifactId], [ManifestSchemaVersion], [IdempotencyKey])
                    SELECT NEWID(), [Id], [ManifestSchemaVersion], [IdempotencyKey]
                    FROM [CentralArtifacts];

                    UPDATE [CentralArtifacts]
                    SET [StateReasonCode] = ''manifest.legacy-incomplete''
                    WHERE [ReconstructionState] = ''LegacyIncomplete'';
                ');
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_CentralFrames_DeviceRigProfiles_DeviceRigProfileId",
                table: "CentralFrames",
                column: "DeviceRigProfileId",
                principalTable: "DeviceRigProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DeviceRigProfiles_DeviceRegistrations_RegistrationId",
                table: "DeviceRigProfiles",
                column: "RegistrationId",
                principalTable: "DeviceRegistrations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.DropForeignKey(
                name: "FK_CentralFrames_DeviceRigProfiles_DeviceRigProfileId",
                table: "CentralFrames");

            migrationBuilder.DropForeignKey(
                name: "FK_DeviceRigProfiles_DeviceRegistrations_RegistrationId",
                table: "DeviceRigProfiles");

            migrationBuilder.DropTable(
                name: "CentralArtifactIngestIdentities");

            migrationBuilder.DropTable(
                name: "CentralArtifactLayouts");

            migrationBuilder.DropTable(
                name: "CentralArtifactRecipes");

            migrationBuilder.DropTable(
                name: "CentralArtifactSources");

            migrationBuilder.DropTable(
                name: "CentralCaptureControls");

            migrationBuilder.DropTable(
                name: "CentralCaptureProfiles");

            migrationBuilder.DropTable(
                name: "CentralCaptureTimings");

            migrationBuilder.DropIndex(
                name: "IX_DeviceRigProfiles_DevicePublicId_ProfileName_ProfileVersion_ProfileSha256",
                table: "DeviceRigProfiles");

            migrationBuilder.DropIndex(
                name: "IX_CentralFrames_DevicePublicId_CaptureSequence",
                table: "CentralFrames");

            migrationBuilder.DropIndex(
                name: "IX_CentralFrames_DeviceRigProfileId",
                table: "CentralFrames");

            migrationBuilder.DropIndex(
                name: "IX_CentralArtifacts_CentralFrameId_Role_RecipeVersion",
                table: "CentralArtifacts");

            migrationBuilder.DropColumn(
                name: "ProfileName",
                table: "DeviceRigProfiles");

            migrationBuilder.DropColumn(
                name: "ProfileSha256",
                table: "DeviceRigProfiles");

            migrationBuilder.DropColumn(
                name: "ProfileVersion",
                table: "DeviceRigProfiles");

            migrationBuilder.DropColumn(
                name: "CaptureSequence",
                table: "CentralFrames");

            migrationBuilder.DropColumn(
                name: "CycleEvidenceJson",
                table: "CentralFrames");

            migrationBuilder.DropColumn(
                name: "DeviceRigProfileId",
                table: "CentralFrames");

            migrationBuilder.DropColumn(
                name: "RigId",
                table: "CentralFrames");

            migrationBuilder.DropColumn(
                name: "CreatedUtc",
                table: "CentralArtifacts");

            migrationBuilder.DropColumn(
                name: "ObjectState",
                table: "CentralArtifacts");

            migrationBuilder.DropColumn(
                name: "ReconciledAtUtc",
                table: "CentralArtifacts");

            migrationBuilder.DropColumn(
                name: "ReconstructionState",
                table: "CentralArtifacts");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "CentralArtifacts");

            migrationBuilder.DropColumn(
                name: "SourceId",
                table: "CentralArtifacts");

            migrationBuilder.DropColumn(
                name: "StateReasonCode",
                table: "CentralArtifacts");

            migrationBuilder.DropColumn(
                name: "Variant",
                table: "CentralArtifacts");

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifacts_CentralFrameId_Role_RecipeVersion",
                table: "CentralArtifacts",
                columns: new[] { "CentralFrameId", "Role", "RecipeVersion" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_DeviceRigProfiles_DeviceRegistrations_RegistrationId",
                table: "DeviceRigProfiles",
                column: "RegistrationId",
                principalTable: "DeviceRegistrations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
