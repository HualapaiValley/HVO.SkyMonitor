using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCentralDerivativeJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.CreateTable(
                name: "CentralDerivativeJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceCentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetRole = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TargetRecipeVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    MaxAttempts = table.Column<int>(type: "int", nullable: false),
                    AvailableAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseAcquiredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastFailedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResultCentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralDerivativeJobs", x => x.Id);
                    table.CheckConstraint("CK_CentralDerivativeJobs_AttemptCount", "[AttemptCount] >= 0 AND [AttemptCount] <= [MaxAttempts]");
                    table.CheckConstraint("CK_CentralDerivativeJobs_MaxAttempts", "[MaxAttempts] > 0");
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobs_CentralArtifacts_ResultCentralArtifactId",
                        column: x => x.ResultCentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobs_CentralArtifacts_SourceCentralArtifactId",
                        column: x => x.SourceCentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO CentralDerivativeJobs (
                    Id, SourceCentralArtifactId, TargetRole, TargetRecipeVersion, Status,
                    AttemptCount, MaxAttempts, AvailableAtUtc, CreatedAtUtc, UpdatedAtUtc,
                    CompletedAtUtc, ResultCentralArtifactId)
                SELECT
                    NEWID(),
                    source.Id,
                    recipe.TargetRole,
                    recipe.RecipeVersion,
                    CASE WHEN target.Id IS NULL THEN N'Pending' ELSE N'Completed' END,
                    0,
                    5,
                    CASE WHEN target.Id IS NULL THEN source.ReceivedAtUtc ELSE NULL END,
                    source.ReceivedAtUtc,
                    CASE WHEN target.ReceivedAtUtc > source.ReceivedAtUtc THEN target.ReceivedAtUtc ELSE source.ReceivedAtUtc END,
                    CASE WHEN target.Id IS NULL THEN NULL
                        WHEN target.ReceivedAtUtc > source.ReceivedAtUtc THEN target.ReceivedAtUtc ELSE source.ReceivedAtUtc END,
                    target.Id
                FROM CentralArtifacts AS source
                CROSS JOIN (VALUES
                    (N'Preview', N'central-preview-v1'),
                    (N'AnnotatedPreview', N'central-annotated-preview-v1')
                ) AS recipe(TargetRole, RecipeVersion)
                OUTER APPLY (
                    SELECT TOP (1) candidate.Id, candidate.ReceivedAtUtc
                    FROM CentralArtifacts AS candidate
                    WHERE candidate.CentralFrameId = source.CentralFrameId
                        AND candidate.Role = recipe.TargetRole
                        AND candidate.RecipeVersion = recipe.RecipeVersion
                    ORDER BY candidate.ReceivedAtUtc, candidate.Id
                ) AS target
                WHERE source.Role = N'Raw';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobs_CreatedAtUtc_Id",
                table: "CentralDerivativeJobs",
                columns: new[] { "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobs_ResultCentralArtifactId",
                table: "CentralDerivativeJobs",
                column: "ResultCentralArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobs_SourceCentralArtifactId_TargetRole_TargetRecipeVersion",
                table: "CentralDerivativeJobs",
                columns: new[] { "SourceCentralArtifactId", "TargetRole", "TargetRecipeVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobs_Status_AvailableAtUtc_CreatedAtUtc_Id",
                table: "CentralDerivativeJobs",
                columns: new[] { "Status", "AvailableAtUtc", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobs_Status_LeaseExpiresAtUtc_CreatedAtUtc_Id",
                table: "CentralDerivativeJobs",
                columns: new[] { "Status", "LeaseExpiresAtUtc", "CreatedAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.DropTable(
                name: "CentralDerivativeJobs");
        }
    }
}
