using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCentralDerivativeExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.DropIndex(
                name: "IX_CentralDerivativeJobs_SourceCentralArtifactId_TargetRole_TargetRecipeVersion",
                table: "CentralDerivativeJobs");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancellationRequestedAtUtc",
                table: "CentralDerivativeJobs",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancellationRequestedBy",
                table: "CentralDerivativeJobs",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InputSelectorJson",
                table: "CentralDerivativeJobs",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecipeName",
                table: "CentralDerivativeJobs",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecipeOptionsJson",
                table: "CentralDerivativeJobs",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestIdentitySha256",
                table: "CentralDerivativeJobs",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestedRecipeIdentitySha256",
                table: "CentralDerivativeJobs",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupersededByJobId",
                table: "CentralDerivativeJobs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetVariant",
                table: "CentralDerivativeJobs",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TraceParent",
                table: "CentralDerivativeJobs",
                type: "varchar(128)",
                unicode: false,
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TraceState",
                table: "CentralDerivativeJobs",
                type: "varchar(512)",
                unicode: false,
                maxLength: 512,
                nullable: true);

            var derivativeBackfillSql =
                """
                UPDATE jobs
                SET
                    [TargetVariant] = CASE [TargetRecipeVersion]
                        WHEN N'central-preview-v1' THEN N'central-preview'
                        WHEN N'central-annotated-preview-v1' THEN N'central-annotated-preview'
                        ELSE N''
                    END,
                    [RecipeName] = CASE [TargetRecipeVersion]
                        WHEN N'central-preview-v1' THEN N'encoded-preview'
                        WHEN N'central-annotated-preview-v1' THEN N'annotation'
                        ELSE N'legacy-unknown'
                    END,
                    [RecipeOptionsJson] = CASE [TargetRecipeVersion]
                        WHEN N'central-preview-v1' THEN N'{"asinhStrength":4,"blackPercentile":0.5,"jpegQuality":80,"outputEncoding":"Jpeg","whitePercentile":0.9999}'
                        WHEN N'central-annotated-preview-v1' THEN N'{"asinhStrength":4,"blackPercentile":0.5,"cardinalScale":2,"cardinalValue":255,"constellationLineBlue":255,"constellationLineGreen":160,"constellationLineOpacity":0.8,"constellationLineRed":96,"constellationLineThickness":1,"constellationLineValue":160,"drawCardinalDirections":false,"drawImageCircle":false,"drawLabels":true,"imageCircleValue":96,"jpegQuality":80,"labelScale":1,"markRadius":6,"markerValue":144,"outputEncoding":"Jpeg","whitePercentile":0.9999}'
                        ELSE N'{}'
                    END,
                    [InputSelectorJson] = N'{"kind":"Raw","recipeIdentitySha256":null,"role":"Raw","variant":null}',
                    [RequestedRecipeIdentitySha256] = CASE [TargetRecipeVersion]
                        WHEN N'central-preview-v1' THEN 'A3575E4DC3EC040D7E7D4B17B39FF3DE3A5947F6AC5230F8AACAF384F1FA4E9F'
                        WHEN N'central-annotated-preview-v1' THEN '0D47360EE5C1FEEE0D6726D9EE082AEE555D519326B348EDCF1C8A3E9D4EA426'
                            ELSE CONVERT(varchar(64), HASHBYTES('SHA2_256',
                            CONVERT(varchar(max), CONCAT('hvo-central-legacy-recipe-v1', CHAR(10), [TargetRecipeVersion]))), 2)
                    END
                FROM [CentralDerivativeJobs] AS jobs;

                UPDATE jobs
                SET [RequestIdentitySha256] = CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varchar(max), CONCAT(
                    'hvo-central-derivative-request-v2', CHAR(10),
                    LOWER(REPLACE(CONVERT(varchar(36), COALESCE(source.[DevicePublicId], source.[Id])), '-', '')), CHAR(10),
                    LOWER(REPLACE(CONVERT(varchar(36), source.[ArtifactId]), '-', '')), CHAR(10),
                    jobs.[TargetRole], CHAR(10), jobs.[TargetVariant], CHAR(10),
                    UPPER(jobs.[RequestedRecipeIdentitySha256])))), 2)
                FROM [CentralDerivativeJobs] AS jobs
                INNER JOIN [CentralArtifacts] AS source ON source.[Id] = jobs.[SourceCentralArtifactId];

                UPDATE jobs
                SET [Status] = N'Skipped',
                    [AvailableAtUtc] = NULL,
                    [LeaseOwner] = NULL,
                    [LeaseToken] = NULL,
                    [LeaseAcquiredAtUtc] = NULL,
                    [LeaseExpiresAtUtc] = NULL,
                    [LastError] = N'The legacy derivative source is not reconstructable.',
                    [UpdatedAtUtc] = CASE WHEN jobs.[UpdatedAtUtc] > SYSUTCDATETIME()
                        THEN jobs.[UpdatedAtUtc] ELSE SYSUTCDATETIME() END
                FROM [CentralDerivativeJobs] AS jobs
                INNER JOIN [CentralArtifacts] AS source ON source.[Id] = jobs.[SourceCentralArtifactId]
                WHERE source.[ReconstructionState] <> N'Complete'
                    AND jobs.[Status] IN (N'Pending', N'Leased', N'RetryableFailure');

                UPDATE jobs
                SET [Status] = CASE WHEN jobs.[AttemptCount] >= jobs.[MaxAttempts]
                        THEN N'TerminalFailure' ELSE N'RetryableFailure' END,
                    [AvailableAtUtc] = CASE WHEN jobs.[AttemptCount] >= jobs.[MaxAttempts]
                        THEN NULL ELSE SYSUTCDATETIME() END,
                    [LeaseOwner] = NULL,
                    [LeaseToken] = NULL,
                    [LeaseAcquiredAtUtc] = NULL,
                    [LeaseExpiresAtUtc] = NULL,
                    [LastFailedAtUtc] = SYSUTCDATETIME(),
                    [LastError] = N'The active lease was reset during the central derivative execution migration.',
                    [UpdatedAtUtc] = SYSUTCDATETIME()
                FROM [CentralDerivativeJobs] AS jobs
                WHERE jobs.[Status] = N'Leased';

                INSERT INTO [CentralDerivativeJobs] (
                    [Id], [SourceCentralArtifactId], [TargetRole], [TargetRecipeVersion], [TargetVariant],
                    [RecipeName], [RecipeOptionsJson], [InputSelectorJson], [RequestedRecipeIdentitySha256],
                    [RequestIdentitySha256], [Status], [AttemptCount], [MaxAttempts], [AvailableAtUtc],
                    [CreatedAtUtc], [UpdatedAtUtc])
                SELECT
                    NEWID(), source.[Id], N'Metadata', N'central-image-quality-v1', N'central-image-quality',
                    N'image-quality', N'{}', N'{"kind":"Raw","recipeIdentitySha256":null,"role":"Raw","variant":null}',
                    'A1AC1D797A0F58B3E7EAC84D145FAC45B2C66DEA602F28880328F56EA620C651',
                    identityValue.[RequestIdentitySha256], N'Pending', 0, 5, source.[ReceivedAtUtc],
                    source.[ReceivedAtUtc], source.[ReceivedAtUtc]
                FROM [CentralArtifacts] AS source
                CROSS APPLY (SELECT CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varchar(max), CONCAT(
                    'hvo-central-derivative-request-v2', CHAR(10),
                    LOWER(REPLACE(CONVERT(varchar(36), COALESCE(source.[DevicePublicId], source.[Id])), '-', '')), CHAR(10),
                    LOWER(REPLACE(CONVERT(varchar(36), source.[ArtifactId]), '-', '')), CHAR(10),
                    'Metadata', CHAR(10), 'central-image-quality', CHAR(10),
                    'A1AC1D797A0F58B3E7EAC84D145FAC45B2C66DEA602F28880328F56EA620C651'))), 2)
                    AS [RequestIdentitySha256]) AS identityValue
                WHERE source.[Role] = N'Raw'
                    AND source.[ObjectState] = N'Available'
                    AND source.[ReconstructionState] = N'Complete'
                    AND NOT EXISTS (
                        SELECT 1 FROM [CentralDerivativeJobs] AS existing
                        WHERE existing.[RequestIdentitySha256] = identityValue.[RequestIdentitySha256]);
                """;
            migrationBuilder.Sql($"EXEC(N'{derivativeBackfillSql.Replace("'", "''", StringComparison.Ordinal)}');");

            migrationBuilder.AlterColumn<string>(
                name: "TargetVariant",
                table: "CentralDerivativeJobs",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "RecipeName",
                table: "CentralDerivativeJobs",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "RecipeOptionsJson",
                table: "CentralDerivativeJobs",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "InputSelectorJson",
                table: "CentralDerivativeJobs",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(2048)",
                oldMaxLength: 2048,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "RequestedRecipeIdentitySha256",
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

            migrationBuilder.AlterColumn<string>(
                name: "RequestIdentitySha256",
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

            migrationBuilder.CreateTable(
                name: "CentralArtifactProcessingEvidence",
                columns: table => new
                {
                    CentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DevicePublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OutputIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    RequestedRecipeIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    RecipeIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    AlgorithmsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CompatibilityJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TotalIntegrationTicks = table.Column<long>(type: "bigint", nullable: false),
                    CentralDerivativeJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralArtifactProcessingEvidence", x => x.CentralArtifactId);
                    table.CheckConstraint("CK_CentralArtifactProcessingEvidence_AttemptNumber", "[AttemptNumber] > 0");
                    table.CheckConstraint("CK_CentralArtifactProcessingEvidence_TotalIntegrationTicks", "[TotalIntegrationTicks] >= 0");
                    table.ForeignKey(
                        name: "FK_CentralArtifactProcessingEvidence_CentralArtifacts_CentralArtifactId",
                        column: x => x.CentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CentralArtifactProcessingEvidence_CentralDerivativeJobs_CentralDerivativeJobId",
                        column: x => x.CentralDerivativeJobId,
                        principalTable: "CentralDerivativeJobs",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "CentralDerivativeJobAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralDerivativeJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    WorkerId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    LeaseAcquiredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    InputBytes = table.Column<long>(type: "bigint", nullable: false),
                    OutputBytes = table.Column<long>(type: "bigint", nullable: false),
                    RecipeDurationTicks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralDerivativeJobAttempts", x => x.Id);
                    table.CheckConstraint("CK_CentralDerivativeJobAttempts_AttemptNumber", "[AttemptNumber] > 0");
                    table.CheckConstraint("CK_CentralDerivativeJobAttempts_Bytes", "[InputBytes] >= 0 AND [OutputBytes] >= 0");
                    table.CheckConstraint("CK_CentralDerivativeJobAttempts_RecipeDurationTicks", "[RecipeDurationTicks] >= 0");
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobAttempts_CentralDerivativeJobs_CentralDerivativeJobId",
                        column: x => x.CentralDerivativeJobId,
                        principalTable: "CentralDerivativeJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobs_RequestIdentitySha256",
                table: "CentralDerivativeJobs",
                column: "RequestIdentitySha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobs_SourceCentralArtifactId_TargetRole_TargetRecipeVersion",
                table: "CentralDerivativeJobs",
                columns: new[] { "SourceCentralArtifactId", "TargetRole", "TargetRecipeVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobs_SupersededByJobId",
                table: "CentralDerivativeJobs",
                column: "SupersededByJobId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifactProcessingEvidence_CentralDerivativeJobId",
                table: "CentralArtifactProcessingEvidence",
                column: "CentralDerivativeJobId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifactProcessingEvidence_DevicePublicId_OutputIdentitySha256",
                table: "CentralArtifactProcessingEvidence",
                columns: new[] { "DevicePublicId", "OutputIdentitySha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobAttempts_CentralDerivativeJobId_AttemptNumber",
                table: "CentralDerivativeJobAttempts",
                columns: new[] { "CentralDerivativeJobId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobAttempts_Outcome_LeaseExpiresAtUtc",
                table: "CentralDerivativeJobAttempts",
                columns: new[] { "Outcome", "LeaseExpiresAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_CentralDerivativeJobs_CentralDerivativeJobs_SupersededByJobId",
                table: "CentralDerivativeJobs",
                column: "SupersededByJobId",
                principalTable: "CentralDerivativeJobs",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.DropForeignKey(
                name: "FK_CentralDerivativeJobs_CentralDerivativeJobs_SupersededByJobId",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropTable(
                name: "CentralArtifactProcessingEvidence");

            migrationBuilder.DropTable(
                name: "CentralDerivativeJobAttempts");

            migrationBuilder.DropIndex(
                name: "IX_CentralDerivativeJobs_RequestIdentitySha256",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropIndex(
                name: "IX_CentralDerivativeJobs_SourceCentralArtifactId_TargetRole_TargetRecipeVersion",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropIndex(
                name: "IX_CentralDerivativeJobs_SupersededByJobId",
                table: "CentralDerivativeJobs");

            migrationBuilder.Sql(
                """
                UPDATE [CentralDerivativeJobs]
                SET [Status] = N'TerminalFailure',
                    [AvailableAtUtc] = NULL,
                    [LeaseOwner] = NULL,
                    [LeaseToken] = NULL,
                    [LeaseAcquiredAtUtc] = NULL,
                    [LeaseExpiresAtUtc] = NULL,
                    [LastError] = COALESCE([LastError], N'The job was terminal when central execution was downgraded.')
                WHERE [Status] IN (N'CancelRequested', N'Canceled', N'Skipped', N'Quarantined', N'Superseded');

                WITH [RankedLegacyJobs] AS
                (
                    SELECT [Id], ROW_NUMBER() OVER
                    (
                        PARTITION BY [SourceCentralArtifactId], [TargetRole], [TargetRecipeVersion]
                        ORDER BY CASE WHEN [Status] = N'Completed' THEN 0 ELSE 1 END, [CreatedAtUtc], [Id]
                    ) AS [Ordinal]
                    FROM [CentralDerivativeJobs]
                )
                DELETE FROM [RankedLegacyJobs] WHERE [Ordinal] > 1;
                """);

            migrationBuilder.DropColumn(
                name: "CancellationRequestedAtUtc",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropColumn(
                name: "CancellationRequestedBy",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropColumn(
                name: "InputSelectorJson",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropColumn(
                name: "RecipeName",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropColumn(
                name: "RecipeOptionsJson",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropColumn(
                name: "RequestIdentitySha256",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropColumn(
                name: "RequestedRecipeIdentitySha256",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropColumn(
                name: "SupersededByJobId",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropColumn(
                name: "TargetVariant",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropColumn(
                name: "TraceParent",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropColumn(
                name: "TraceState",
                table: "CentralDerivativeJobs");

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobs_SourceCentralArtifactId_TargetRole_TargetRecipeVersion",
                table: "CentralDerivativeJobs",
                columns: new[] { "SourceCentralArtifactId", "TargetRole", "TargetRecipeVersion" },
                unique: true);
        }
    }
}
