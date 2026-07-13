using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeCentralFrames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.CreateTable(
                name: "CentralFrames",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegistrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DevicePublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    FrameId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CapturedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FirstReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RigProfileVersion = table.Column<int>(type: "int", nullable: true),
                    SceneProvenanceJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralFrames", x => x.Id);
                });

            migrationBuilder.Sql(
                """
                IF EXISTS (
                    SELECT 1
                    FROM DeviceImageUploads
                    WHERE FrameId IS NOT NULL AND ArtifactId IS NOT NULL AND AgentId IS NOT NULL
                        AND ArtifactRole IS NOT NULL AND RecipeVersion IS NOT NULL
                        AND ManifestSchemaVersion IS NOT NULL AND IdempotencyKey IS NOT NULL
                        AND ChecksumSha256 IS NOT NULL AND ByteLength IS NOT NULL
                    GROUP BY DevicePublicId, FrameId
                    HAVING COUNT(DISTINCT CONVERT(nvarchar(36), RegistrationId)) > 1
                        OR COUNT(DISTINCT CONVERT(nvarchar(36), ObservatoryId)) > 1
                        OR COUNT(DISTINCT AgentId) > 1
                        OR COUNT(DISTINCT CONVERT(nvarchar(48), CapturedAtUtc, 127)) > 1
                        OR COUNT(DISTINCT COALESCE(CONVERT(nvarchar(16), RigProfileVersion), N'<null>')) > 1
                        OR COUNT(DISTINCT CONVERT(varchar(64), HASHBYTES('SHA2_256', SceneProvenanceJson), 2)) > 1
                )
                    THROW 51000, 'Conflicting frame metadata prevents central frame normalization.', 1;

                INSERT INTO CentralFrames (
                    Id, RegistrationId, DevicePublicId, ObservatoryId, AgentId, FrameId,
                    CapturedAtUtc, FirstReceivedAtUtc, RigProfileVersion, SceneProvenanceJson)
                SELECT
                    NEWID(),
                    CONVERT(uniqueidentifier, MAX(CONVERT(nvarchar(36), RegistrationId))),
                    DevicePublicId,
                    CONVERT(uniqueidentifier, MAX(CONVERT(nvarchar(36), ObservatoryId))),
                    MAX(AgentId),
                    FrameId,
                    MIN(CapturedAtUtc),
                    MIN(ReceivedAtUtc),
                    MAX(RigProfileVersion),
                    MAX(SceneProvenanceJson)
                FROM DeviceImageUploads
                WHERE FrameId IS NOT NULL AND ArtifactId IS NOT NULL AND AgentId IS NOT NULL
                    AND ArtifactRole IS NOT NULL AND RecipeVersion IS NOT NULL
                    AND ManifestSchemaVersion IS NOT NULL AND IdempotencyKey IS NOT NULL
                    AND ChecksumSha256 IS NOT NULL AND ByteLength IS NOT NULL
                GROUP BY DevicePublicId, FrameId;
                """);

            migrationBuilder.CreateTable(
                name: "CentralArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralFrameId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RecipeVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ManifestSchemaVersion = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    MediaType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ByteLength = table.Column<long>(type: "bigint", nullable: false),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StorageReference = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralArtifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CentralArtifacts_CentralFrames_CentralFrameId",
                        column: x => x.CentralFrameId,
                        principalTable: "CentralFrames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO CentralArtifacts (
                    Id, CentralFrameId, ArtifactId, Role, RecipeVersion, ManifestSchemaVersion,
                    MediaType, ByteLength, ChecksumSha256, StorageReference, ReceivedAtUtc, IdempotencyKey)
                SELECT
                    upload.Id,
                    frame.Id,
                    upload.ArtifactId,
                    upload.ArtifactRole,
                    upload.RecipeVersion,
                    upload.ManifestSchemaVersion,
                    upload.ContentType,
                    upload.ByteLength,
                    UPPER(upload.ChecksumSha256),
                    upload.StorageReference,
                    upload.ReceivedAtUtc,
                    UPPER(upload.IdempotencyKey)
                FROM DeviceImageUploads AS upload
                INNER JOIN CentralFrames AS frame
                    ON frame.DevicePublicId = upload.DevicePublicId AND frame.FrameId = upload.FrameId
                WHERE upload.FrameId IS NOT NULL AND upload.ArtifactId IS NOT NULL AND upload.AgentId IS NOT NULL
                    AND upload.ArtifactRole IS NOT NULL AND upload.RecipeVersion IS NOT NULL
                    AND upload.ManifestSchemaVersion IS NOT NULL AND upload.IdempotencyKey IS NOT NULL
                    AND upload.ChecksumSha256 IS NOT NULL AND upload.ByteLength IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifacts_CentralFrameId_ArtifactId",
                table: "CentralArtifacts",
                columns: new[] { "CentralFrameId", "ArtifactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifacts_CentralFrameId_Role_RecipeVersion",
                table: "CentralArtifacts",
                columns: new[] { "CentralFrameId", "Role", "RecipeVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifacts_IdempotencyKey",
                table: "CentralArtifacts",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralFrames_AgentId_CapturedAtUtc",
                table: "CentralFrames",
                columns: new[] { "AgentId", "CapturedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralFrames_DevicePublicId_CapturedAtUtc_FrameId",
                table: "CentralFrames",
                columns: new[] { "DevicePublicId", "CapturedAtUtc", "FrameId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralFrames_DevicePublicId_FrameId",
                table: "CentralFrames",
                columns: new[] { "DevicePublicId", "FrameId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            throw new NotSupportedException(
                "NormalizeCentralFrames is irreversible because new ingest records are not duplicated into the legacy upload table.");
        }
    }
}
