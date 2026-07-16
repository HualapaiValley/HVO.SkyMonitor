using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCentralDerivativeWindows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.AddColumn<string>(
                name: "InputSetIdentitySha256",
                table: "CentralDerivativeJobs",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MissingInputOutcome",
                table: "CentralDerivativeJobs",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PredecessorJobId",
                table: "CentralDerivativeJobs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResolutionCompletedAtUtc",
                table: "CentralDerivativeJobs",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResolutionDeadlineUtc",
                table: "CentralDerivativeJobs",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResolutionStartedAtUtc",
                table: "CentralDerivativeJobs",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RetainedResultCentralArtifactId",
                table: "CentralDerivativeJobs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StateReasonCode",
                table: "CentralDerivativeJobs",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CentralDerivativeJobInputRequirements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralDerivativeJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    BindingName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SourceKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SequenceOffset = table.Column<int>(type: "int", nullable: true),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false),
                    SelectorJson = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    CompatibilityMode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ExpectedAgentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ExpectedRigId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ExpectedCaptureSequence = table.Column<long>(type: "bigint", nullable: true),
                    ResolutionState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ResolutionReasonCode = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralDerivativeJobInputRequirements", x => x.Id);
                    table.UniqueConstraint("AK_CentralDerivativeJobInputRequirements_CentralDerivativeJobId_Id", x => new { x.CentralDerivativeJobId, x.Id });
                    table.CheckConstraint("CK_CentralDerivativeJobInputRequirements_Ordinal", "[Ordinal] >= 0");
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobInputRequirements_CentralDerivativeJobs_CentralDerivativeJobId",
                        column: x => x.CentralDerivativeJobId,
                        principalTable: "CentralDerivativeJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CentralDerivativeJobInputs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralDerivativeJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralDerivativeJobInputRequirementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    CentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CaptureSequence = table.Column<long>(type: "bigint", nullable: true),
                    CompatibilityJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CompatibilitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ByteLength = table.Column<long>(type: "bigint", nullable: false),
                    SelectedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralDerivativeJobInputs", x => x.Id);
                    table.CheckConstraint("CK_CentralDerivativeJobInputs_ByteLength", "[ByteLength] >= 0");
                    table.CheckConstraint("CK_CentralDerivativeJobInputs_Ordinal", "[Ordinal] >= 0");
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobInputs_CentralArtifacts_CentralArtifactId",
                        column: x => x.CentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobInputs_CentralDerivativeJobInputRequirements_CentralDerivativeJobId_CentralDerivativeJobInputRequirement~",
                        columns: x => new { x.CentralDerivativeJobId, x.CentralDerivativeJobInputRequirementId },
                        principalTable: "CentralDerivativeJobInputRequirements",
                        principalColumns: new[] { "CentralDerivativeJobId", "Id" });
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobInputs_CentralDerivativeJobs_CentralDerivativeJobId",
                        column: x => x.CentralDerivativeJobId,
                        principalTable: "CentralDerivativeJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                DECLARE @BackfilledRequirements TABLE
                (
                    CentralDerivativeJobId uniqueidentifier NOT NULL,
                    RequirementId uniqueidentifier NOT NULL
                );

                INSERT INTO [CentralDerivativeJobInputRequirements]
                    ([Id], [CentralDerivativeJobId], [Ordinal], [BindingName], [SourceKind],
                     [SequenceOffset], [IsRequired], [SelectorJson], [CompatibilityMode],
                     [ExpectedAgentId], [ExpectedRigId], [ExpectedCaptureSequence],
                     [ResolutionState], [ResolutionReasonCode], [ResolvedAtUtc])
                OUTPUT inserted.[CentralDerivativeJobId], inserted.[Id]
                    INTO @BackfilledRequirements ([CentralDerivativeJobId], [RequirementId])
                SELECT NEWID(), job.[Id], 0, N'input', N'Artifact', 0, CAST(1 AS bit),
                       job.[InputSelectorJson], N'None', frame.[AgentId], frame.[RigId],
                       frame.[CaptureSequence], N'Resolved', NULL, job.[CreatedAtUtc]
                FROM [CentralDerivativeJobs] AS job
                INNER JOIN [CentralArtifacts] AS artifact ON artifact.[Id] = job.[SourceCentralArtifactId]
                INNER JOIN [CentralFrames] AS frame ON frame.[Id] = artifact.[CentralFrameId];

                INSERT INTO [CentralDerivativeJobInputs]
                    ([Id], [CentralDerivativeJobId], [CentralDerivativeJobInputRequirementId],
                     [Ordinal], [CentralArtifactId], [CaptureSequence], [CompatibilityJson],
                     [CompatibilitySha256], [ByteLength], [SelectedAtUtc])
                SELECT NEWID(), job.[Id], requirement.[RequirementId], 0,
                       artifact.[Id], frame.[CaptureSequence], N'{}',
                       '44136FA355B3678A1146AD16F7E8649E94FB4FC21FE77E8310C060F61CAFF8A',
                       artifact.[ByteLength], job.[CreatedAtUtc]
                FROM [CentralDerivativeJobs] AS job
                INNER JOIN @BackfilledRequirements AS requirement
                    ON requirement.[CentralDerivativeJobId] = job.[Id]
                INNER JOIN [CentralArtifacts] AS artifact ON artifact.[Id] = job.[SourceCentralArtifactId]
                INNER JOIN [CentralFrames] AS frame ON frame.[Id] = artifact.[CentralFrameId];

                UPDATE [CentralDerivativeJobs]
                SET [ResolutionStartedAtUtc] = [CreatedAtUtc],
                    [ResolutionCompletedAtUtc] = COALESCE([CompletedAtUtc], [CreatedAtUtc]),
                    [InputSetIdentitySha256] = CONVERT(varchar(64), HASHBYTES('SHA2_256',
                        CONCAT('hvo-central-derivative-legacy-input-set-v1:', [Id], ':', [SourceCentralArtifactId])), 2);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CentralFrames_AgentId_CaptureSequence",
                table: "CentralFrames",
                columns: new[] { "AgentId", "CaptureSequence" },
                filter: "[CaptureSequence] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobs_PredecessorJobId",
                table: "CentralDerivativeJobs",
                column: "PredecessorJobId",
                unique: true,
                filter: "[PredecessorJobId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobs_RetainedResultCentralArtifactId",
                table: "CentralDerivativeJobs",
                column: "RetainedResultCentralArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobs_Status_UpdatedAtUtc_ResolutionDeadlineUtc_CreatedAtUtc_Id",
                table: "CentralDerivativeJobs",
                columns: new[] { "Status", "UpdatedAtUtc", "ResolutionDeadlineUtc", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobInputRequirements_CentralDerivativeJobId_Ordinal",
                table: "CentralDerivativeJobInputRequirements",
                columns: new[] { "CentralDerivativeJobId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobInputRequirements_ExpectedAgentId_ExpectedCaptureSequence_ResolutionState_CentralDerivativeJobId",
                table: "CentralDerivativeJobInputRequirements",
                columns: new[] { "ExpectedAgentId", "ExpectedCaptureSequence", "ResolutionState", "CentralDerivativeJobId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobInputs_CentralArtifactId_CentralDerivativeJobId",
                table: "CentralDerivativeJobInputs",
                columns: new[] { "CentralArtifactId", "CentralDerivativeJobId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobInputs_CentralDerivativeJobId_CentralArtifactId",
                table: "CentralDerivativeJobInputs",
                columns: new[] { "CentralDerivativeJobId", "CentralArtifactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobInputs_CentralDerivativeJobId_CentralDerivativeJobInputRequirementId",
                table: "CentralDerivativeJobInputs",
                columns: new[] { "CentralDerivativeJobId", "CentralDerivativeJobInputRequirementId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobInputs_CentralDerivativeJobId_Ordinal",
                table: "CentralDerivativeJobInputs",
                columns: new[] { "CentralDerivativeJobId", "Ordinal" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CentralDerivativeJobs_CentralArtifacts_RetainedResultCentralArtifactId",
                table: "CentralDerivativeJobs",
                column: "RetainedResultCentralArtifactId",
                principalTable: "CentralArtifacts",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_CentralDerivativeJobs_CentralDerivativeJobs_PredecessorJobId",
                table: "CentralDerivativeJobs",
                column: "PredecessorJobId",
                principalTable: "CentralDerivativeJobs",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.DropForeignKey(
                name: "FK_CentralDerivativeJobs_CentralArtifacts_RetainedResultCentralArtifactId",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropForeignKey(
                name: "FK_CentralDerivativeJobs_CentralDerivativeJobs_PredecessorJobId",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropTable(
                name: "CentralDerivativeJobInputs");

            migrationBuilder.DropTable(
                name: "CentralDerivativeJobInputRequirements");

            migrationBuilder.DropIndex(
                name: "IX_CentralFrames_AgentId_CaptureSequence",
                table: "CentralFrames");

            migrationBuilder.DropIndex(
                name: "IX_CentralDerivativeJobs_PredecessorJobId",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropIndex(
                name: "IX_CentralDerivativeJobs_RetainedResultCentralArtifactId",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropIndex(
                name: "IX_CentralDerivativeJobs_Status_UpdatedAtUtc_ResolutionDeadlineUtc_CreatedAtUtc_Id",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropColumn(
                name: "InputSetIdentitySha256",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropColumn(
                name: "MissingInputOutcome",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropColumn(
                name: "PredecessorJobId",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropColumn(
                name: "ResolutionCompletedAtUtc",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropColumn(
                name: "ResolutionDeadlineUtc",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropColumn(
                name: "ResolutionStartedAtUtc",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropColumn(
                name: "RetainedResultCentralArtifactId",
                table: "CentralDerivativeJobs");

            migrationBuilder.DropColumn(
                name: "StateReasonCode",
                table: "CentralDerivativeJobs");
        }
    }
}
