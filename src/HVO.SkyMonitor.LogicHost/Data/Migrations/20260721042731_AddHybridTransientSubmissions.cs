using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHybridTransientSubmissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql("DROP TRIGGER [TR_CentralTransientValidationJobs_CommittedImmutable]");
            migrationBuilder.Sql("DROP TRIGGER [TR_CentralTransientValidationIdentitySlots_TerminalImmutable]");

            migrationBuilder.AddColumn<string>(
                name: "SubmittedCandidateJson",
                table: "CentralTransientValidationJobs",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgentId",
                table: "CentralTransientValidationIdentitySlots",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.Sql("""
                UPDATE slot
                SET slot.[AgentId] = validation.[AgentId]
                FROM [CentralTransientValidationIdentitySlots] AS slot
                INNER JOIN [CentralTransientValidationJobs] AS validation
                    ON validation.[CentralDerivativeJobId] = slot.[CentralDerivativeJobId]
                """);

            migrationBuilder.AlterColumn<string>(
                name: "AgentId",
                table: "CentralTransientValidationIdentitySlots",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128,
                oldNullable: true,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.CreateTable(
                name: "CentralTransientSubmissionAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DevicePublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CandidateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClaimedSubmissionIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    PayloadSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ExistingCentralDerivativeJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientSubmissionAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CentralTransientSubmissionAudits_CentralDerivativeJobs_ExistingCentralDerivativeJobId",
                        column: x => x.ExistingCentralDerivativeJobId,
                        principalTable: "CentralDerivativeJobs",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientValidationIdentitySlots_AgentId_SubmittedEventId",
                table: "CentralTransientValidationIdentitySlots",
                columns: new[] { "AgentId", "SubmittedEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientSubmissionAudits_CandidateId",
                table: "CentralTransientSubmissionAudits",
                column: "CandidateId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientSubmissionAudits_DevicePublicId_PayloadSha256_ReasonCode",
                table: "CentralTransientSubmissionAudits",
                columns: new[] { "DevicePublicId", "PayloadSha256", "ReasonCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientSubmissionAudits_EventId",
                table: "CentralTransientSubmissionAudits",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientSubmissionAudits_ExistingCentralDerivativeJobId",
                table: "CentralTransientSubmissionAudits",
                column: "ExistingCentralDerivativeJobId");

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
                           OR d.[OutcomeRecordedAtUtc] IS NOT NULL
                           OR i.[AgentId] <> d.[AgentId]
                           OR i.[SubmissionSchemaVersion] <> d.[SubmissionSchemaVersion]
                           OR i.[SubmissionIdentitySha256] <> d.[SubmissionIdentitySha256]
                           OR (i.[SubmittedCandidateJson] IS NULL AND d.[SubmittedCandidateJson] IS NOT NULL)
                           OR (i.[SubmittedCandidateJson] IS NOT NULL AND d.[SubmittedCandidateJson] IS NULL)
                           OR i.[SubmittedCandidateJson] <> d.[SubmittedCandidateJson]
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
                           OR i.[AgentId] <> d.[AgentId]
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

            migrationBuilder.Sql("""
                CREATE TRIGGER [TR_CentralTransientSubmissionAudits_Immutable]
                ON [CentralTransientSubmissionAudits]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    IF (ROWCOUNT_BIG() = 0) RETURN;
                    SET NOCOUNT ON;
                    THROW 51000, 'Hybrid transient submission audit evidence is immutable.', 1;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql("DROP TRIGGER [TR_CentralTransientValidationJobs_CommittedImmutable]");
            migrationBuilder.Sql("DROP TRIGGER [TR_CentralTransientValidationIdentitySlots_TerminalImmutable]");

            migrationBuilder.DropTable(
                name: "CentralTransientSubmissionAudits");

            migrationBuilder.DropIndex(
                name: "IX_CentralTransientValidationIdentitySlots_AgentId_SubmittedEventId",
                table: "CentralTransientValidationIdentitySlots");

            migrationBuilder.DropColumn(
                name: "SubmittedCandidateJson",
                table: "CentralTransientValidationJobs");

            migrationBuilder.DropColumn(
                name: "AgentId",
                table: "CentralTransientValidationIdentitySlots");

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
        }
    }
}
