using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStoredCodeLayoutSemantics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<int>(
                name: "BinX",
                table: "CentralArtifactLayouts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BinY",
                table: "CentralArtifactLayouts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BinningAlgorithm",
                table: "CentralArtifactLayouts",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CfaOriginX",
                table: "CentralArtifactLayouts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CfaOriginY",
                table: "CentralArtifactLayouts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LevelCodeSpace",
                table: "CentralArtifactLayouts",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NativeHeight",
                table: "CentralArtifactLayouts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NativeWidth",
                table: "CentralArtifactLayouts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RoiHeight",
                table: "CentralArtifactLayouts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RoiWidth",
                table: "CentralArtifactLayouts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RoiX",
                table: "CentralArtifactLayouts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RoiY",
                table: "CentralArtifactLayouts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StoredCodeTransform",
                table: "CentralArtifactLayouts",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            var storedCodeBackfillSql = """
                DECLARE @AffectedArtifacts TABLE (
                    [Id] uniqueidentifier NOT NULL PRIMARY KEY,
                    [IsRoot] bit NOT NULL
                );

                INSERT INTO @AffectedArtifacts ([Id], [IsRoot])
                SELECT artifacts.[Id], 1
                FROM [CentralArtifacts] AS artifacts
                INNER JOIN [CentralArtifactLayouts] AS layouts
                    ON layouts.[CentralArtifactId] = artifacts.[Id]
                WHERE layouts.[SampleDepthBits] < layouts.[ContainerDepthBits]
                  AND (layouts.[StoredCodeTransform] IS NULL OR layouts.[LevelCodeSpace] IS NULL)
                  AND artifacts.[ReconstructionState] <> 'Quarantined';

                WHILE 1 = 1
                BEGIN
                    INSERT INTO @AffectedArtifacts ([Id], [IsRoot])
                    SELECT DISTINCT sources.[CentralArtifactId], 0
                    FROM [CentralArtifactSources] AS sources
                    INNER JOIN @AffectedArtifacts AS affected
                        ON affected.[Id] = sources.[ResolvedCentralArtifactId]
                    WHERE NOT EXISTS (
                        SELECT 1 FROM @AffectedArtifacts AS existing
                        WHERE existing.[Id] = sources.[CentralArtifactId]);

                    IF @@ROWCOUNT = 0 BREAK;
                END;

                UPDATE artifacts
                SET artifacts.[ReconstructionState] = 'LegacyIncomplete',
                    artifacts.[StateReasonCode] = 'layout.stored-code-ambiguous',
                    artifacts.[ReferenceRetryCount] = 0,
                    artifacts.[ReferenceRetryAtUtc] = NULL
                FROM [CentralArtifacts] AS artifacts
                INNER JOIN @AffectedArtifacts AS affected
                    ON affected.[Id] = artifacts.[Id]
                WHERE affected.[IsRoot] = 1;

                UPDATE artifacts
                SET artifacts.[ReconstructionState] = 'PendingReference',
                    artifacts.[StateReasonCode] = 'lineage.source-unavailable',
                    artifacts.[ReconciledAtUtc] = NULL,
                    artifacts.[ReferenceRetryCount] = 0,
                    artifacts.[ReferenceRetryAtUtc] = NULL
                FROM [CentralArtifacts] AS artifacts
                INNER JOIN @AffectedArtifacts AS affected
                    ON affected.[Id] = artifacts.[Id]
                WHERE affected.[IsRoot] = 0
                  AND artifacts.[ReconstructionState] <> 'Quarantined';

                UPDATE sources
                SET sources.[ResolvedCentralArtifactId] = NULL
                FROM [CentralArtifactSources] AS sources
                INNER JOIN @AffectedArtifacts AS affected
                    ON affected.[Id] = sources.[ResolvedCentralArtifactId];

                UPDATE attempts
                SET attempts.[Outcome] = 'TerminalFailure',
                    attempts.[EndedAtUtc] = SYSUTCDATETIME(),
                    attempts.[ReasonCode] = 'source.layout-stored-code-ambiguous'
                FROM [CentralDerivativeJobAttempts] AS attempts
                INNER JOIN [CentralDerivativeJobs] AS jobs
                    ON jobs.[Id] = attempts.[CentralDerivativeJobId]
                WHERE attempts.[Outcome] = 'Leased'
                  AND (EXISTS (
                        SELECT 1 FROM @AffectedArtifacts AS affected
                        WHERE affected.[Id] = jobs.[SourceCentralArtifactId]
                            OR affected.[Id] = jobs.[ResultCentralArtifactId])
                    OR EXISTS (
                        SELECT 1
                        FROM [CentralDerivativeJobInputs] AS inputs
                        INNER JOIN @AffectedArtifacts AS affected
                            ON affected.[Id] = inputs.[CentralArtifactId]
                        WHERE inputs.[CentralDerivativeJobId] = jobs.[Id]));

                UPDATE jobs
                SET jobs.[Status] = 'TerminalFailure',
                    jobs.[StateReasonCode] = 'source.layout-stored-code-ambiguous',
                    jobs.[LastFailedAtUtc] = SYSUTCDATETIME(),
                    jobs.[LastError] = 'Source layout lacks stored-code semantics.',
                    jobs.[UpdatedAtUtc] = SYSUTCDATETIME(),
                    jobs.[AvailableAtUtc] = NULL,
                    jobs.[LeaseOwner] = NULL,
                    jobs.[LeaseToken] = NULL,
                    jobs.[LeaseAcquiredAtUtc] = NULL,
                    jobs.[LeaseExpiresAtUtc] = NULL
                FROM [CentralDerivativeJobs] AS jobs
                WHERE jobs.[Status] IN ('Waiting', 'Pending', 'Leased', 'RetryableFailure', 'CancelRequested', 'Completed')
                  AND (EXISTS (
                        SELECT 1 FROM @AffectedArtifacts AS affected
                        WHERE affected.[Id] = jobs.[SourceCentralArtifactId]
                            OR affected.[Id] = jobs.[ResultCentralArtifactId])
                    OR EXISTS (
                        SELECT 1
                        FROM [CentralDerivativeJobInputs] AS inputs
                        INNER JOIN @AffectedArtifacts AS affected
                            ON affected.[Id] = inputs.[CentralArtifactId]
                        WHERE inputs.[CentralDerivativeJobId] = jobs.[Id]));
                """;
            migrationBuilder.Sql($"EXEC(N'{storedCodeBackfillSql.Replace("'", "''", StringComparison.Ordinal)}');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropColumn(
                name: "BinX",
                table: "CentralArtifactLayouts");

            migrationBuilder.DropColumn(
                name: "BinY",
                table: "CentralArtifactLayouts");

            migrationBuilder.DropColumn(
                name: "BinningAlgorithm",
                table: "CentralArtifactLayouts");

            migrationBuilder.DropColumn(
                name: "CfaOriginX",
                table: "CentralArtifactLayouts");

            migrationBuilder.DropColumn(
                name: "CfaOriginY",
                table: "CentralArtifactLayouts");

            migrationBuilder.DropColumn(
                name: "LevelCodeSpace",
                table: "CentralArtifactLayouts");

            migrationBuilder.DropColumn(
                name: "NativeHeight",
                table: "CentralArtifactLayouts");

            migrationBuilder.DropColumn(
                name: "NativeWidth",
                table: "CentralArtifactLayouts");

            migrationBuilder.DropColumn(
                name: "RoiHeight",
                table: "CentralArtifactLayouts");

            migrationBuilder.DropColumn(
                name: "RoiWidth",
                table: "CentralArtifactLayouts");

            migrationBuilder.DropColumn(
                name: "RoiX",
                table: "CentralArtifactLayouts");

            migrationBuilder.DropColumn(
                name: "RoiY",
                table: "CentralArtifactLayouts");

            migrationBuilder.DropColumn(
                name: "StoredCodeTransform",
                table: "CentralArtifactLayouts");
        }
    }
}
