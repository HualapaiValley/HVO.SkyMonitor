using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCentralTransientRuntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.Sql("DROP TRIGGER [TR_CentralTransientValidationJobs_CommittedImmutable]");
            migrationBuilder.Sql("DROP TRIGGER [TR_CentralTransientValidationIdentitySlots_TerminalImmutable]");

            migrationBuilder.DropForeignKey(
                name: "FK_CentralTransientValidationIdentitySlots_CentralTransientEvents_CentralTransientEventId_EventId",
                table: "CentralTransientValidationIdentitySlots");

            migrationBuilder.DropIndex(
                name: "IX_CentralTransientValidationIdentitySlots_CentralTransientEventId_EventId",
                table: "CentralTransientValidationIdentitySlots");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CentralTransientValidationIdentitySlots_State",
                table: "CentralTransientValidationIdentitySlots");

            migrationBuilder.RenameColumn(
                name: "EventId",
                table: "CentralTransientValidationIdentitySlots",
                newName: "SubmittedEventId");

            migrationBuilder.AddColumn<string>(
                name: "ExecutionOptionsIdentitySha256",
                table: "CentralTransientValidationJobs",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "ExecutionOptionsJson",
                table: "CentralTransientValidationJobs",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutcomeEvidenceIdentitySha256",
                table: "CentralTransientValidationJobs",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "OutcomeEvidenceJson",
                table: "CentralTransientValidationJobs",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutcomeReasonCode",
                table: "CentralTransientValidationJobs",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OutcomeRecordedAtUtc",
                table: "CentralTransientValidationJobs",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutcomeState",
                table: "CentralTransientValidationJobs",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProvisionalCentralDerivativeJobId",
                table: "CentralTransientValidationJobs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AdoptedEventId",
                table: "CentralTransientValidationIdentitySlots",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssociationIdentitySha256",
                table: "CentralTransientValidationIdentitySlots",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<Guid>(
                name: "PersistedEventId",
                table: "CentralTransientValidationIdentitySlots",
                type: "uniqueidentifier",
                nullable: true);

            MigrationSql.ExecuteBatch(migrationBuilder, """
                UPDATE [CentralTransientValidationIdentitySlots]
                SET [PersistedEventId] = [SubmittedEventId]
                WHERE [State] = N'Committed' AND [PersistedEventId] IS NULL;

                UPDATE [CentralTransientValidationJobs]
                SET [OutcomeState] = N'NeedsReview',
                    [OutcomeReasonCode] = N'transient-validation.legacy-committed-output',
                    [OutcomeEvidenceJson] = N'{"schemaVersion":"central-transient-validation-outcome-v1","state":"needsReview","reasonCode":"transient-validation.legacy-committed-output"}',
                    [OutcomeEvidenceIdentitySha256] = '838176E680D644B5A682E66CE1124C46156DE0A6BD38E1C36B5D048BC7D43F3F',
                    [OutcomeRecordedAtUtc] = [CommittedAtUtc]
                WHERE [CommittedAtUtc] IS NOT NULL AND [OutcomeRecordedAtUtc] IS NULL;
                """);

            migrationBuilder.CreateTable(
                name: "CentralTransientContextDependencies",
                columns: table => new
                {
                    CentralDerivativeJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    ContextCentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequiredCentralDerivativeJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RequestedRecipeIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ExecutionOptionsIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientContextDependencies", x => new { x.CentralDerivativeJobId, x.Ordinal });
                    table.CheckConstraint("CK_CentralTransientContextDependencies_Ordinal", "[Ordinal] >= 0 AND [Ordinal] < 4");
                    table.ForeignKey(
                        name: "FK_CentralTransientContextDependencies_CentralArtifacts_ContextCentralArtifactId",
                        column: x => x.ContextCentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralTransientContextDependencies_CentralTransientValidationJobs_CentralDerivativeJobId",
                        column: x => x.CentralDerivativeJobId,
                        principalTable: "CentralTransientValidationJobs",
                        principalColumn: "CentralDerivativeJobId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralTransientContextDependencies_CentralTransientValidationJobs_RequiredCentralDerivativeJobId",
                        column: x => x.RequiredCentralDerivativeJobId,
                        principalTable: "CentralTransientValidationJobs",
                        principalColumn: "CentralDerivativeJobId");
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientValidationOutcomeVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralDerivativeJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    EvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EvidenceIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientValidationOutcomeVersions", x => x.Id);
                    table.CheckConstraint("CK_CentralTransientValidationOutcomeVersions_Version", "[Version] > 0");
                    table.ForeignKey(
                        name: "FK_CentralTransientValidationOutcomeVersions_CentralTransientValidationJobs_CentralDerivativeJobId",
                        column: x => x.CentralDerivativeJobId,
                        principalTable: "CentralTransientValidationJobs",
                        principalColumn: "CentralDerivativeJobId",
                        onDelete: ReferentialAction.Restrict);
                });

            MigrationSql.ExecuteBatch(migrationBuilder, """
                INSERT INTO [CentralTransientValidationOutcomeVersions]
                    ([Id], [CentralDerivativeJobId], [Version], [State], [ReasonCode], [EvidenceJson],
                     [EvidenceIdentitySha256], [RecordedAtUtc])
                SELECT NEWID(), [CentralDerivativeJobId], 1, [OutcomeState], [OutcomeReasonCode],
                       [OutcomeEvidenceJson], [OutcomeEvidenceIdentitySha256], [OutcomeRecordedAtUtc]
                FROM [CentralTransientValidationJobs]
                WHERE [OutcomeRecordedAtUtc] IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientValidationJobs_ProvisionalCentralDerivativeJobId",
                table: "CentralTransientValidationJobs",
                column: "ProvisionalCentralDerivativeJobId",
                unique: true,
                filter: "[ProvisionalCentralDerivativeJobId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CentralTransientValidationJobs_CommitOutcome",
                table: "CentralTransientValidationJobs",
                sql: "[CommittedAtUtc] IS NULL OR [OutcomeRecordedAtUtc] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CentralTransientValidationJobs_ExecutionOptions",
                table: "CentralTransientValidationJobs",
                sql: "([ExecutionOptionsJson] IS NULL AND [ExecutionOptionsIdentitySha256] IS NULL) OR ([ExecutionOptionsJson] IS NOT NULL AND [ExecutionOptionsIdentitySha256] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CentralTransientValidationJobs_Outcome",
                table: "CentralTransientValidationJobs",
                sql: "([OutcomeRecordedAtUtc] IS NULL AND [OutcomeState] IS NULL AND [OutcomeReasonCode] IS NULL AND [OutcomeEvidenceJson] IS NULL AND [OutcomeEvidenceIdentitySha256] IS NULL) OR ([OutcomeRecordedAtUtc] IS NOT NULL AND [OutcomeState] IS NOT NULL AND [OutcomeReasonCode] IS NOT NULL AND [OutcomeEvidenceJson] IS NOT NULL AND [OutcomeEvidenceIdentitySha256] IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientValidationIdentitySlots_AssociationIdentitySha256",
                table: "CentralTransientValidationIdentitySlots",
                column: "AssociationIdentitySha256",
                unique: true,
                filter: "[AssociationIdentitySha256] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientValidationIdentitySlots_CentralTransientEventId_PersistedEventId",
                table: "CentralTransientValidationIdentitySlots",
                columns: new[] { "CentralTransientEventId", "PersistedEventId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_CentralTransientValidationIdentitySlots_Association",
                table: "CentralTransientValidationIdentitySlots",
                sql: "([AdoptedEventId] IS NULL AND [AssociationIdentitySha256] IS NULL) OR ([AdoptedEventId] IS NOT NULL AND [AssociationIdentitySha256] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CentralTransientValidationIdentitySlots_State",
                table: "CentralTransientValidationIdentitySlots",
                sql: "([State] IN ('Reserved', 'Unused') AND [CentralTransientEventId] IS NULL AND [PersistedEventId] IS NULL AND [PersistedEventVersionId] IS NULL AND [PersistedObservationId] IS NULL AND [PersistedAssessmentId] IS NULL) OR ([State] = 'Committed' AND [CentralTransientEventId] IS NOT NULL AND [PersistedEventId] = COALESCE([AdoptedEventId], [SubmittedEventId]) AND [PersistedEventVersionId] IS NOT NULL AND [PersistedObservationId] = [ObservationId] AND [PersistedAssessmentId] = [AssessmentId])");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientContextDependencies_ContextCentralArtifactId_ExecutionOptionsIdentitySha256",
                table: "CentralTransientContextDependencies",
                columns: new[] { "ContextCentralArtifactId", "ExecutionOptionsIdentitySha256" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientContextDependencies_RequiredCentralDerivativeJobId",
                table: "CentralTransientContextDependencies",
                column: "RequiredCentralDerivativeJobId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientValidationOutcomeVersions_CentralDerivativeJobId_EvidenceIdentitySha256",
                table: "CentralTransientValidationOutcomeVersions",
                columns: new[] { "CentralDerivativeJobId", "EvidenceIdentitySha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientValidationOutcomeVersions_CentralDerivativeJobId_Version",
                table: "CentralTransientValidationOutcomeVersions",
                columns: new[] { "CentralDerivativeJobId", "Version" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CentralTransientValidationIdentitySlots_CentralTransientEvents_CentralTransientEventId_PersistedEventId",
                table: "CentralTransientValidationIdentitySlots",
                columns: new[] { "CentralTransientEventId", "PersistedEventId" },
                principalTable: "CentralTransientEvents",
                principalColumns: new[] { "Id", "EventId" });

            migrationBuilder.AddForeignKey(
                name: "FK_CentralTransientValidationJobs_CentralTransientValidationJobs_ProvisionalCentralDerivativeJobId",
                table: "CentralTransientValidationJobs",
                column: "ProvisionalCentralDerivativeJobId",
                principalTable: "CentralTransientValidationJobs",
                principalColumn: "CentralDerivativeJobId");

            MigrationSql.ExecuteBatch(migrationBuilder, """
                CREATE TRIGGER [TR_CentralTransientValidationJobs_CommittedImmutable]
                ON [CentralTransientValidationJobs]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1
                        FROM deleted AS d
                        LEFT JOIN inserted AS i ON i.[CentralDerivativeJobId] = d.[CentralDerivativeJobId]
                        WHERE i.[CentralDerivativeJobId] IS NULL
                           OR d.[CommittedAtUtc] IS NOT NULL
                           OR d.[OutcomeRecordedAtUtc] IS NOT NULL
                           OR i.[AgentId] <> d.[AgentId]
                           OR i.[SubmissionSchemaVersion] <> d.[SubmissionSchemaVersion]
                           OR i.[SubmissionIdentitySha256] <> d.[SubmissionIdentitySha256]
                           OR ISNULL(i.[ExecutionOptionsJson], N'') <> ISNULL(d.[ExecutionOptionsJson], N'')
                           OR ISNULL(i.[ExecutionOptionsIdentitySha256], '') <> ISNULL(d.[ExecutionOptionsIdentitySha256], '')
                           OR ISNULL(i.[ProvisionalCentralDerivativeJobId], '00000000-0000-0000-0000-000000000000') <>
                              ISNULL(d.[ProvisionalCentralDerivativeJobId], '00000000-0000-0000-0000-000000000000')
                           OR i.[CreatedAtUtc] <> d.[CreatedAtUtc]
                           OR (i.[CommittedAtUtc] IS NOT NULL AND i.[OutcomeRecordedAtUtc] IS NULL))
                    BEGIN
                        THROW 51000, 'Committed transient validation job identity is immutable.', 1;
                    END
                END
                """);

            MigrationSql.ExecuteBatch(migrationBuilder, """
                CREATE TRIGGER [TR_CentralTransientValidationIdentitySlots_TerminalImmutable]
                ON [CentralTransientValidationIdentitySlots]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1
                        FROM deleted AS d
                        LEFT JOIN inserted AS i ON i.[Id] = d.[Id]
                        WHERE i.[Id] IS NULL
                           OR d.[State] <> N'Reserved'
                           OR i.[CentralDerivativeJobId] <> d.[CentralDerivativeJobId]
                           OR i.[Ordinal] <> d.[Ordinal]
                           OR i.[SubmittedEventId] <> d.[SubmittedEventId]
                           OR i.[CandidateId] <> d.[CandidateId]
                           OR i.[ObservationId] <> d.[ObservationId]
                           OR i.[AssessmentId] <> d.[AssessmentId]
                           OR (d.[AdoptedEventId] IS NOT NULL AND
                               (i.[AdoptedEventId] IS NULL OR i.[AdoptedEventId] <> d.[AdoptedEventId]))
                           OR (d.[AssociationIdentitySha256] IS NOT NULL AND
                               (i.[AssociationIdentitySha256] IS NULL OR
                                i.[AssociationIdentitySha256] <> d.[AssociationIdentitySha256])))
                    BEGIN
                        THROW 51000, 'Terminal transient validation identity slots are immutable.', 1;
                    END
                END
                """);

            MigrationSql.ExecuteBatch(migrationBuilder, """
                CREATE TRIGGER [TR_CentralTransientValidationOutcomeVersions_Immutable]
                ON [CentralTransientValidationOutcomeVersions]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    IF (ROWCOUNT_BIG() = 0) RETURN;
                    THROW 51000, 'Transient validation outcome versions are immutable.', 1;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.Sql("DROP TRIGGER [TR_CentralTransientValidationOutcomeVersions_Immutable]");
            migrationBuilder.Sql("DROP TRIGGER [TR_CentralTransientValidationJobs_CommittedImmutable]");
            migrationBuilder.Sql("DROP TRIGGER [TR_CentralTransientValidationIdentitySlots_TerminalImmutable]");

            migrationBuilder.Sql("""
                UPDATE [CentralTransientValidationIdentitySlots]
                SET [SubmittedEventId] = [PersistedEventId]
                WHERE [State] = N'Committed' AND [PersistedEventId] IS NOT NULL;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_CentralTransientValidationIdentitySlots_CentralTransientEvents_CentralTransientEventId_PersistedEventId",
                table: "CentralTransientValidationIdentitySlots");

            migrationBuilder.DropForeignKey(
                name: "FK_CentralTransientValidationJobs_CentralTransientValidationJobs_ProvisionalCentralDerivativeJobId",
                table: "CentralTransientValidationJobs");

            migrationBuilder.DropTable(
                name: "CentralTransientContextDependencies");

            migrationBuilder.DropTable(
                name: "CentralTransientValidationOutcomeVersions");

            migrationBuilder.DropIndex(
                name: "IX_CentralTransientValidationJobs_ProvisionalCentralDerivativeJobId",
                table: "CentralTransientValidationJobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CentralTransientValidationJobs_CommitOutcome",
                table: "CentralTransientValidationJobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CentralTransientValidationJobs_ExecutionOptions",
                table: "CentralTransientValidationJobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CentralTransientValidationJobs_Outcome",
                table: "CentralTransientValidationJobs");

            migrationBuilder.DropIndex(
                name: "IX_CentralTransientValidationIdentitySlots_AssociationIdentitySha256",
                table: "CentralTransientValidationIdentitySlots");

            migrationBuilder.DropIndex(
                name: "IX_CentralTransientValidationIdentitySlots_CentralTransientEventId_PersistedEventId",
                table: "CentralTransientValidationIdentitySlots");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CentralTransientValidationIdentitySlots_Association",
                table: "CentralTransientValidationIdentitySlots");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CentralTransientValidationIdentitySlots_State",
                table: "CentralTransientValidationIdentitySlots");

            migrationBuilder.DropColumn(
                name: "ExecutionOptionsIdentitySha256",
                table: "CentralTransientValidationJobs");

            migrationBuilder.DropColumn(
                name: "ExecutionOptionsJson",
                table: "CentralTransientValidationJobs");

            migrationBuilder.DropColumn(
                name: "OutcomeEvidenceIdentitySha256",
                table: "CentralTransientValidationJobs");

            migrationBuilder.DropColumn(
                name: "OutcomeEvidenceJson",
                table: "CentralTransientValidationJobs");

            migrationBuilder.DropColumn(
                name: "OutcomeReasonCode",
                table: "CentralTransientValidationJobs");

            migrationBuilder.DropColumn(
                name: "OutcomeRecordedAtUtc",
                table: "CentralTransientValidationJobs");

            migrationBuilder.DropColumn(
                name: "OutcomeState",
                table: "CentralTransientValidationJobs");

            migrationBuilder.DropColumn(
                name: "ProvisionalCentralDerivativeJobId",
                table: "CentralTransientValidationJobs");

            migrationBuilder.DropColumn(
                name: "AdoptedEventId",
                table: "CentralTransientValidationIdentitySlots");

            migrationBuilder.DropColumn(
                name: "AssociationIdentitySha256",
                table: "CentralTransientValidationIdentitySlots");

            migrationBuilder.DropColumn(
                name: "PersistedEventId",
                table: "CentralTransientValidationIdentitySlots");

            migrationBuilder.RenameColumn(
                name: "SubmittedEventId",
                table: "CentralTransientValidationIdentitySlots",
                newName: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientValidationIdentitySlots_CentralTransientEventId_EventId",
                table: "CentralTransientValidationIdentitySlots",
                columns: new[] { "CentralTransientEventId", "EventId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_CentralTransientValidationIdentitySlots_State",
                table: "CentralTransientValidationIdentitySlots",
                sql: "([State] IN ('Reserved', 'Unused') AND [CentralTransientEventId] IS NULL AND [PersistedEventVersionId] IS NULL AND [PersistedObservationId] IS NULL AND [PersistedAssessmentId] IS NULL) OR ([State] = 'Committed' AND [CentralTransientEventId] IS NOT NULL AND [PersistedEventVersionId] IS NOT NULL AND [PersistedObservationId] = [ObservationId] AND [PersistedAssessmentId] = [AssessmentId])");

            migrationBuilder.AddForeignKey(
                name: "FK_CentralTransientValidationIdentitySlots_CentralTransientEvents_CentralTransientEventId_EventId",
                table: "CentralTransientValidationIdentitySlots",
                columns: new[] { "CentralTransientEventId", "EventId" },
                principalTable: "CentralTransientEvents",
                principalColumns: new[] { "Id", "EventId" });

            migrationBuilder.Sql("""
                CREATE TRIGGER [TR_CentralTransientValidationJobs_CommittedImmutable]
                ON [CentralTransientValidationJobs]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1
                        FROM deleted AS d
                        LEFT JOIN inserted AS i ON i.[CentralDerivativeJobId] = d.[CentralDerivativeJobId]
                        WHERE i.[CentralDerivativeJobId] IS NULL
                           OR d.[CommittedAtUtc] IS NOT NULL
                           OR i.[CommittedAtUtc] IS NULL
                           OR i.[AgentId] <> d.[AgentId]
                           OR i.[SubmissionSchemaVersion] <> d.[SubmissionSchemaVersion]
                           OR i.[SubmissionIdentitySha256] <> d.[SubmissionIdentitySha256]
                           OR i.[CreatedAtUtc] <> d.[CreatedAtUtc])
                    BEGIN
                        THROW 51000, 'Committed transient validation job identity is immutable.', 1;
                    END
                END
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER [TR_CentralTransientValidationIdentitySlots_TerminalImmutable]
                ON [CentralTransientValidationIdentitySlots]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1
                        FROM deleted AS d
                        LEFT JOIN inserted AS i ON i.[Id] = d.[Id]
                        WHERE i.[Id] IS NULL
                           OR d.[State] <> N'Reserved'
                           OR i.[CentralDerivativeJobId] <> d.[CentralDerivativeJobId]
                           OR i.[Ordinal] <> d.[Ordinal]
                           OR i.[EventId] <> d.[EventId]
                           OR i.[CandidateId] <> d.[CandidateId]
                           OR i.[ObservationId] <> d.[ObservationId]
                           OR i.[AssessmentId] <> d.[AssessmentId])
                    BEGIN
                        THROW 51000, 'Terminal transient validation identity slots are immutable.', 1;
                    END
                END
                """);
        }
    }
}
