using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCentralTransientValidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "CentralTransientEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventCreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientEvents", x => x.Id);
                    table.UniqueConstraint("AK_CentralTransientEvents_AgentId_EventId", x => new { x.AgentId, x.EventId });
                    table.UniqueConstraint("AK_CentralTransientEvents_Id_EventId", x => new { x.Id, x.EventId });
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientObservationSources",
                columns: table => new
                {
                    ObservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EvidenceSchemaVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    EvidenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocatorSchemaVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    LocatorKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactRole = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ArtifactVariant = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ArtifactRecipeIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ArtifactChecksumSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ObservationStartedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ObservationEndedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TimingQuality = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TimingProvenanceSource = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    TimingProvenanceVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientObservationSources", x => x.ObservationId);
                    table.CheckConstraint("CK_CentralTransientObservationSources_ObservedInterval", "[ObservationStartedUtc] <= [ObservationEndedUtc]");
                    table.ForeignKey(
                        name: "FK_CentralTransientObservationSources_CentralArtifacts_CentralArtifactId",
                        column: x => x.CentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientValidationJobs",
                columns: table => new
                {
                    CentralDerivativeJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SubmissionSchemaVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SubmissionIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CommittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientValidationJobs", x => x.CentralDerivativeJobId);
                    table.ForeignKey(
                        name: "FK_CentralTransientValidationJobs_CentralDerivativeJobs_CentralDerivativeJobId",
                        column: x => x.CentralDerivativeJobId,
                        principalTable: "CentralDerivativeJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientAssessments",
                columns: table => new
                {
                    AssessmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Authority = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Classification = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    MeteorSeverity = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ConfidenceMillionths = table.Column<int>(type: "int", nullable: false),
                    SupersedesAssessmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupersedesAssessmentCreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ProducerSchemaVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ProducerKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ProducerName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProducerVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RecipeIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ReceiptSchemaVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ExecutionIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OptionsIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CanonicalReceiptJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CanonicalReceiptSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CanonicalReceiptByteLength = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientAssessments", x => x.AssessmentId);
                    table.UniqueConstraint("AK_CentralTransientAssessments_CentralTransientEventId_AssessmentId", x => new { x.CentralTransientEventId, x.AssessmentId });
                    table.UniqueConstraint("AK_CentralTransientAssessments_CentralTransientEventId_AssessmentId_CreatedUtc", x => new { x.CentralTransientEventId, x.AssessmentId, x.CreatedUtc });
                    table.CheckConstraint("CK_CentralTransientAssessments_Confidence", "[ConfidenceMillionths] >= 0 AND [ConfidenceMillionths] <= 1000000");
                    table.CheckConstraint("CK_CentralTransientAssessments_Predecessor", "([SupersedesAssessmentId] IS NULL AND [SupersedesAssessmentCreatedUtc] IS NULL) OR ([SupersedesAssessmentId] IS NOT NULL AND [SupersedesAssessmentId] <> [AssessmentId] AND [SupersedesAssessmentCreatedUtc] IS NOT NULL AND [SupersedesAssessmentCreatedUtc] < [CreatedUtc])");
                    table.CheckConstraint("CK_CentralTransientAssessments_ReceiptLength", "[CanonicalReceiptByteLength] > 0");
                    table.ForeignKey(
                        name: "FK_CentralTransientAssessments_CentralTransientAssessments_CentralTransientEventId_SupersedesAssessmentId_SupersedesAssessmentC~",
                        columns: x => new { x.CentralTransientEventId, x.SupersedesAssessmentId, x.SupersedesAssessmentCreatedUtc },
                        principalTable: "CentralTransientAssessments",
                        principalColumns: new[] { "CentralTransientEventId", "AssessmentId", "CreatedUtc" });
                    table.ForeignKey(
                        name: "FK_CentralTransientAssessments_CentralTransientEvents_CentralTransientEventId",
                        column: x => x.CentralTransientEventId,
                        principalTable: "CentralTransientEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientEventVersions",
                columns: table => new
                {
                    EventVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    PreviousVersionNumber = table.Column<int>(type: "int", nullable: true),
                    PreviousEventVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PreviousVersionCreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    VersionCreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FirstObservedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastObservedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SchemaVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CanonicalEventJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CanonicalEventSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CanonicalEventByteLength = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientEventVersions", x => x.EventVersionId);
                    table.UniqueConstraint("AK_CentralTransientEventVersions_CentralTransientEventId_EventVersionId", x => new { x.CentralTransientEventId, x.EventVersionId });
                    table.UniqueConstraint("AK_CentralTransientEventVersions_CentralTransientEventId_Version_EventVersionId_VersionCreatedUtc", x => new { x.CentralTransientEventId, x.Version, x.EventVersionId, x.VersionCreatedUtc });
                    table.CheckConstraint("CK_CentralTransientEventVersions_ObservedInterval", "[FirstObservedUtc] <= [LastObservedUtc] AND [LastObservedUtc] <= [VersionCreatedUtc]");
                    table.CheckConstraint("CK_CentralTransientEventVersions_Predecessor", "([Version] = 1 AND [PreviousVersionNumber] IS NULL AND [PreviousEventVersionId] IS NULL AND [PreviousVersionCreatedUtc] IS NULL) OR ([Version] > 1 AND [PreviousVersionNumber] IS NOT NULL AND [PreviousVersionNumber] = [Version] - 1 AND [PreviousEventVersionId] IS NOT NULL AND [PreviousEventVersionId] <> [EventVersionId] AND [PreviousVersionCreatedUtc] IS NOT NULL AND [PreviousVersionCreatedUtc] < [VersionCreatedUtc])");
                    table.CheckConstraint("CK_CentralTransientEventVersions_ReceiptLength", "[CanonicalEventByteLength] > 0");
                    table.CheckConstraint("CK_CentralTransientEventVersions_Version", "[Version] > 0");
                    table.ForeignKey(
                        name: "FK_CentralTransientEventVersions_CentralTransientEventVersions_CentralTransientEventId_PreviousVersionNumber_PreviousEventVersi~",
                        columns: x => new { x.CentralTransientEventId, x.PreviousVersionNumber, x.PreviousEventVersionId, x.PreviousVersionCreatedUtc },
                        principalTable: "CentralTransientEventVersions",
                        principalColumns: new[] { "CentralTransientEventId", "Version", "EventVersionId", "VersionCreatedUtc" });
                    table.ForeignKey(
                        name: "FK_CentralTransientEventVersions_CentralTransientEvents_CentralTransientEventId",
                        column: x => x.CentralTransientEventId,
                        principalTable: "CentralTransientEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientObservations",
                columns: table => new
                {
                    ObservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DetectorInputIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CalibrationIdentity = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    MaskIdentity = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProcessingProfileIdentity = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OriginatingCandidateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExtractionProducerSchemaVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ExtractionProducerKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ExtractionProducerName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ExtractionProducerVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ExtractionRecipeIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ExtractionReceiptIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    GeometryJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FeaturesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceReferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientObservations", x => x.ObservationId);
                    table.UniqueConstraint("AK_CentralTransientObservations_CentralTransientEventId_ObservationId", x => new { x.CentralTransientEventId, x.ObservationId });
                    table.CheckConstraint("CK_CentralTransientObservations_SourceReference", "[SourceReferenceId] = [ObservationId]");
                    table.ForeignKey(
                        name: "FK_CentralTransientObservations_CentralTransientEvents_CentralTransientEventId",
                        column: x => x.CentralTransientEventId,
                        principalTable: "CentralTransientEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralTransientObservations_CentralTransientObservationSources_SourceReferenceId",
                        column: x => x.SourceReferenceId,
                        principalTable: "CentralTransientObservationSources",
                        principalColumn: "ObservationId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientExtractionReceipts",
                columns: table => new
                {
                    CentralDerivativeJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SchemaVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ExtractionIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OptionsIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CanonicalReceiptJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CanonicalReceiptSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CanonicalReceiptByteLength = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientExtractionReceipts", x => x.CentralDerivativeJobId);
                    table.CheckConstraint("CK_CentralTransientExtractionReceipts_ReceiptLength", "[CanonicalReceiptByteLength] > 0");
                    table.ForeignKey(
                        name: "FK_CentralTransientExtractionReceipts_CentralTransientValidationJobs_CentralDerivativeJobId",
                        column: x => x.CentralDerivativeJobId,
                        principalTable: "CentralTransientValidationJobs",
                        principalColumn: "CentralDerivativeJobId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientEventVersionAssessments",
                columns: table => new
                {
                    EventVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientEventVersionAssessments", x => new { x.EventVersionId, x.Ordinal });
                    table.CheckConstraint("CK_CentralTransientEventVersionAssessments_Ordinal", "[Ordinal] >= 0");
                    table.ForeignKey(
                        name: "FK_CentralTransientEventVersionAssessments_CentralTransientAssessments_CentralTransientEventId_AssessmentId",
                        columns: x => new { x.CentralTransientEventId, x.AssessmentId },
                        principalTable: "CentralTransientAssessments",
                        principalColumns: new[] { "CentralTransientEventId", "AssessmentId" });
                    table.ForeignKey(
                        name: "FK_CentralTransientEventVersionAssessments_CentralTransientEventVersions_CentralTransientEventId_EventVersionId",
                        columns: x => new { x.CentralTransientEventId, x.EventVersionId },
                        principalTable: "CentralTransientEventVersions",
                        principalColumns: new[] { "CentralTransientEventId", "EventVersionId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientAssessmentObservations",
                columns: table => new
                {
                    AssessmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientAssessmentObservations", x => new { x.AssessmentId, x.Ordinal });
                    table.CheckConstraint("CK_CentralTransientAssessmentObservations_Ordinal", "[Ordinal] >= 0");
                    table.ForeignKey(
                        name: "FK_CentralTransientAssessmentObservations_CentralTransientAssessments_CentralTransientEventId_AssessmentId",
                        columns: x => new { x.CentralTransientEventId, x.AssessmentId },
                        principalTable: "CentralTransientAssessments",
                        principalColumns: new[] { "CentralTransientEventId", "AssessmentId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralTransientAssessmentObservations_CentralTransientObservations_CentralTransientEventId_ObservationId",
                        columns: x => new { x.CentralTransientEventId, x.ObservationId },
                        principalTable: "CentralTransientObservations",
                        principalColumns: new[] { "CentralTransientEventId", "ObservationId" });
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientEventVersionObservations",
                columns: table => new
                {
                    EventVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientEventVersionObservations", x => new { x.EventVersionId, x.Ordinal });
                    table.CheckConstraint("CK_CentralTransientEventVersionObservations_Ordinal", "[Ordinal] >= 0");
                    table.ForeignKey(
                        name: "FK_CentralTransientEventVersionObservations_CentralTransientEventVersions_CentralTransientEventId_EventVersionId",
                        columns: x => new { x.CentralTransientEventId, x.EventVersionId },
                        principalTable: "CentralTransientEventVersions",
                        principalColumns: new[] { "CentralTransientEventId", "EventVersionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralTransientEventVersionObservations_CentralTransientObservations_CentralTransientEventId_ObservationId",
                        columns: x => new { x.CentralTransientEventId, x.ObservationId },
                        principalTable: "CentralTransientObservations",
                        principalColumns: new[] { "CentralTransientEventId", "ObservationId" });
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientObservationBackgrounds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    CentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactRole = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ArtifactVariant = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ArtifactRecipeIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ArtifactChecksumSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientObservationBackgrounds", x => x.Id);
                    table.CheckConstraint("CK_CentralTransientObservationBackgrounds_Ordinal", "[Ordinal] >= 0");
                    table.ForeignKey(
                        name: "FK_CentralTransientObservationBackgrounds_CentralArtifacts_CentralArtifactId",
                        column: x => x.CentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralTransientObservationBackgrounds_CentralTransientObservations_ObservationId",
                        column: x => x.ObservationId,
                        principalTable: "CentralTransientObservations",
                        principalColumn: "ObservationId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientValidationIdentitySlots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralDerivativeJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CandidateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PersistedEventVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PersistedObservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PersistedAssessmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientValidationIdentitySlots", x => x.Id);
                    table.CheckConstraint("CK_CentralTransientValidationIdentitySlots_Ordinal", "[Ordinal] >= 0");
                    table.CheckConstraint("CK_CentralTransientValidationIdentitySlots_State", "([State] IN ('Reserved', 'Unused') AND [CentralTransientEventId] IS NULL AND [PersistedEventVersionId] IS NULL AND [PersistedObservationId] IS NULL AND [PersistedAssessmentId] IS NULL) OR ([State] = 'Committed' AND [CentralTransientEventId] IS NOT NULL AND [PersistedEventVersionId] IS NOT NULL AND [PersistedObservationId] = [ObservationId] AND [PersistedAssessmentId] = [AssessmentId])");
                    table.ForeignKey(
                        name: "FK_CentralTransientValidationIdentitySlots_CentralTransientAssessments_CentralTransientEventId_PersistedAssessmentId",
                        columns: x => new { x.CentralTransientEventId, x.PersistedAssessmentId },
                        principalTable: "CentralTransientAssessments",
                        principalColumns: new[] { "CentralTransientEventId", "AssessmentId" });
                    table.ForeignKey(
                        name: "FK_CentralTransientValidationIdentitySlots_CentralTransientEventVersions_CentralTransientEventId_PersistedEventVersionId",
                        columns: x => new { x.CentralTransientEventId, x.PersistedEventVersionId },
                        principalTable: "CentralTransientEventVersions",
                        principalColumns: new[] { "CentralTransientEventId", "EventVersionId" });
                    table.ForeignKey(
                        name: "FK_CentralTransientValidationIdentitySlots_CentralTransientEvents_CentralTransientEventId_EventId",
                        columns: x => new { x.CentralTransientEventId, x.EventId },
                        principalTable: "CentralTransientEvents",
                        principalColumns: new[] { "Id", "EventId" });
                    table.ForeignKey(
                        name: "FK_CentralTransientValidationIdentitySlots_CentralTransientObservations_CentralTransientEventId_PersistedObservationId",
                        columns: x => new { x.CentralTransientEventId, x.PersistedObservationId },
                        principalTable: "CentralTransientObservations",
                        principalColumns: new[] { "CentralTransientEventId", "ObservationId" });
                    table.ForeignKey(
                        name: "FK_CentralTransientValidationIdentitySlots_CentralTransientValidationJobs_CentralDerivativeJobId",
                        column: x => x.CentralDerivativeJobId,
                        principalTable: "CentralTransientValidationJobs",
                        principalColumn: "CentralDerivativeJobId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientExtractionSources",
                columns: table => new
                {
                    CentralDerivativeJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Position = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DetectorInputIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EvidenceSchemaVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    EvidenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocatorSchemaVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    LocatorKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactRole = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ArtifactVariant = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ArtifactRecipeIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ArtifactChecksumSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ObservationStartedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ObservationEndedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TimingQuality = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TimingProvenanceSource = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    TimingProvenanceVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientExtractionSources", x => new { x.CentralDerivativeJobId, x.Ordinal });
                    table.CheckConstraint("CK_CentralTransientExtractionSources_ObservedInterval", "[ObservationStartedUtc] <= [ObservationEndedUtc]");
                    table.CheckConstraint("CK_CentralTransientExtractionSources_Ordinal", "[Ordinal] >= 0");
                    table.ForeignKey(
                        name: "FK_CentralTransientExtractionSources_CentralArtifacts_CentralArtifactId",
                        column: x => x.CentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralTransientExtractionSources_CentralTransientExtractionReceipts_CentralDerivativeJobId",
                        column: x => x.CentralDerivativeJobId,
                        principalTable: "CentralTransientExtractionReceipts",
                        principalColumn: "CentralDerivativeJobId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientAssessmentObservations_AssessmentId_ObservationId",
                table: "CentralTransientAssessmentObservations",
                columns: new[] { "AssessmentId", "ObservationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientAssessmentObservations_CentralTransientEventId_AssessmentId",
                table: "CentralTransientAssessmentObservations",
                columns: new[] { "CentralTransientEventId", "AssessmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientAssessmentObservations_CentralTransientEventId_ObservationId",
                table: "CentralTransientAssessmentObservations",
                columns: new[] { "CentralTransientEventId", "ObservationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientAssessments_CentralTransientEventId_ProducerSchemaVersion_ProducerKind_ProducerName_ProducerVersion_RecipeId~",
                table: "CentralTransientAssessments",
                columns: new[] { "CentralTransientEventId", "ProducerSchemaVersion", "ProducerKind", "ProducerName", "ProducerVersion", "RecipeIdentitySha256", "ExecutionIdentitySha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientAssessments_CentralTransientEventId_SupersedesAssessmentId",
                table: "CentralTransientAssessments",
                columns: new[] { "CentralTransientEventId", "SupersedesAssessmentId" },
                unique: true,
                filter: "[SupersedesAssessmentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientAssessments_CentralTransientEventId_SupersedesAssessmentId_SupersedesAssessmentCreatedUtc",
                table: "CentralTransientAssessments",
                columns: new[] { "CentralTransientEventId", "SupersedesAssessmentId", "SupersedesAssessmentCreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEvents_AgentId_EventCreatedUtc_EventId",
                table: "CentralTransientEvents",
                columns: new[] { "AgentId", "EventCreatedUtc", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEventVersionAssessments_CentralTransientEventId_AssessmentId",
                table: "CentralTransientEventVersionAssessments",
                columns: new[] { "CentralTransientEventId", "AssessmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEventVersionAssessments_CentralTransientEventId_EventVersionId",
                table: "CentralTransientEventVersionAssessments",
                columns: new[] { "CentralTransientEventId", "EventVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEventVersionAssessments_EventVersionId_AssessmentId",
                table: "CentralTransientEventVersionAssessments",
                columns: new[] { "EventVersionId", "AssessmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEventVersionObservations_CentralTransientEventId_EventVersionId",
                table: "CentralTransientEventVersionObservations",
                columns: new[] { "CentralTransientEventId", "EventVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEventVersionObservations_CentralTransientEventId_ObservationId",
                table: "CentralTransientEventVersionObservations",
                columns: new[] { "CentralTransientEventId", "ObservationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEventVersionObservations_EventVersionId_ObservationId",
                table: "CentralTransientEventVersionObservations",
                columns: new[] { "EventVersionId", "ObservationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEventVersions_CentralTransientEventId_PreviousEventVersionId",
                table: "CentralTransientEventVersions",
                columns: new[] { "CentralTransientEventId", "PreviousEventVersionId" },
                unique: true,
                filter: "[PreviousEventVersionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEventVersions_CentralTransientEventId_PreviousVersionNumber_PreviousEventVersionId_PreviousVersionCreatedUtc",
                table: "CentralTransientEventVersions",
                columns: new[] { "CentralTransientEventId", "PreviousVersionNumber", "PreviousEventVersionId", "PreviousVersionCreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEventVersions_CentralTransientEventId_Version",
                table: "CentralTransientEventVersions",
                columns: new[] { "CentralTransientEventId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientExtractionReceipts_ExtractionIdentitySha256",
                table: "CentralTransientExtractionReceipts",
                column: "ExtractionIdentitySha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientExtractionSources_CentralArtifactId",
                table: "CentralTransientExtractionSources",
                column: "CentralArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientExtractionSources_CentralDerivativeJobId_EvidenceId",
                table: "CentralTransientExtractionSources",
                columns: new[] { "CentralDerivativeJobId", "EvidenceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientObservationBackgrounds_CentralArtifactId",
                table: "CentralTransientObservationBackgrounds",
                column: "CentralArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientObservationBackgrounds_ObservationId_CentralArtifactId",
                table: "CentralTransientObservationBackgrounds",
                columns: new[] { "ObservationId", "CentralArtifactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientObservationBackgrounds_ObservationId_Ordinal",
                table: "CentralTransientObservationBackgrounds",
                columns: new[] { "ObservationId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientObservations_OriginatingCandidateId",
                table: "CentralTransientObservations",
                column: "OriginatingCandidateId",
                unique: true,
                filter: "[OriginatingCandidateId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientObservations_SourceReferenceId",
                table: "CentralTransientObservations",
                column: "SourceReferenceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientObservationSources_CentralArtifactId",
                table: "CentralTransientObservationSources",
                column: "CentralArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientObservationSources_EvidenceId",
                table: "CentralTransientObservationSources",
                column: "EvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientValidationIdentitySlots_AssessmentId",
                table: "CentralTransientValidationIdentitySlots",
                column: "AssessmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientValidationIdentitySlots_CandidateId",
                table: "CentralTransientValidationIdentitySlots",
                column: "CandidateId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientValidationIdentitySlots_CentralDerivativeJobId_Ordinal",
                table: "CentralTransientValidationIdentitySlots",
                columns: new[] { "CentralDerivativeJobId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientValidationIdentitySlots_CentralTransientEventId_EventId",
                table: "CentralTransientValidationIdentitySlots",
                columns: new[] { "CentralTransientEventId", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientValidationIdentitySlots_CentralTransientEventId_PersistedAssessmentId",
                table: "CentralTransientValidationIdentitySlots",
                columns: new[] { "CentralTransientEventId", "PersistedAssessmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientValidationIdentitySlots_CentralTransientEventId_PersistedEventVersionId",
                table: "CentralTransientValidationIdentitySlots",
                columns: new[] { "CentralTransientEventId", "PersistedEventVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientValidationIdentitySlots_CentralTransientEventId_PersistedObservationId",
                table: "CentralTransientValidationIdentitySlots",
                columns: new[] { "CentralTransientEventId", "PersistedObservationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientValidationIdentitySlots_ObservationId",
                table: "CentralTransientValidationIdentitySlots",
                column: "ObservationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientValidationJobs_SubmissionIdentitySha256",
                table: "CentralTransientValidationJobs",
                column: "SubmissionIdentitySha256",
                unique: true);

            foreach (var table in new[]
            {
                "CentralTransientEvents",
                "CentralTransientEventVersions",
                "CentralTransientObservations",
                "CentralTransientObservationSources",
                "CentralTransientObservationBackgrounds",
                "CentralTransientAssessments",
                "CentralTransientEventVersionObservations",
                "CentralTransientEventVersionAssessments",
                "CentralTransientAssessmentObservations",
                "CentralTransientExtractionReceipts",
                "CentralTransientExtractionSources"
            })
            {
                migrationBuilder.Sql($"""
                    CREATE TRIGGER [TR_{table}_Immutable]
                    ON [{table}]
                    AFTER UPDATE, DELETE
                    AS
                    BEGIN
                        SET NOCOUNT ON;
                        THROW 51000, 'Committed transient evidence is immutable.', 1;
                    END
                    """);
            }

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "CentralTransientAssessmentObservations");

            migrationBuilder.DropTable(
                name: "CentralTransientEventVersionAssessments");

            migrationBuilder.DropTable(
                name: "CentralTransientEventVersionObservations");

            migrationBuilder.DropTable(
                name: "CentralTransientExtractionSources");

            migrationBuilder.DropTable(
                name: "CentralTransientObservationBackgrounds");

            migrationBuilder.DropTable(
                name: "CentralTransientValidationIdentitySlots");

            migrationBuilder.DropTable(
                name: "CentralTransientExtractionReceipts");

            migrationBuilder.DropTable(
                name: "CentralTransientAssessments");

            migrationBuilder.DropTable(
                name: "CentralTransientEventVersions");

            migrationBuilder.DropTable(
                name: "CentralTransientObservations");

            migrationBuilder.DropTable(
                name: "CentralTransientValidationJobs");

            migrationBuilder.DropTable(
                name: "CentralTransientEvents");

            migrationBuilder.DropTable(
                name: "CentralTransientObservationSources");
        }
    }
}
