using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCentralTransientReviewAndDerivatives : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_CentralTransientObservationSources_ObservationId_EvidenceId_CentralArtifactId_ArtifactId_ArtifactChecksumSha256_ObservationS~",
                table: "CentralTransientObservationSources",
                columns: new[] { "ObservationId", "EvidenceId", "CentralArtifactId", "ArtifactId", "ArtifactChecksumSha256", "ObservationStartedUtc", "ObservationEndedUtc" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_CentralTransientObservationBackgrounds_ObservationId_Ordinal_CentralArtifactId_ArtifactId_ArtifactChecksumSha256",
                table: "CentralTransientObservationBackgrounds",
                columns: new[] { "ObservationId", "Ordinal", "CentralArtifactId", "ArtifactId", "ArtifactChecksumSha256" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEvents_EventCreatedUtc_Id",
                table: "CentralTransientEvents",
                columns: new[] { "EventCreatedUtc", "Id" });

            migrationBuilder.CreateTable(
                name: "CentralTransientDerivativeJobs",
                columns: table => new
                {
                    CentralDerivativeJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceEventVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProducerSchemaVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ProducerName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProducerVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RecipeIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OptionsIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CanonicalRequestJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CanonicalRequestSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CanonicalRequestByteLength = table.Column<int>(type: "int", nullable: false),
                    ExpectedOutputCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CommittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientDerivativeJobs", x => x.CentralDerivativeJobId);
                    table.UniqueConstraint("AK_CentralTransientDerivativeJobs_CentralTransientEventId_CentralDerivativeJobId", x => new { x.CentralTransientEventId, x.CentralDerivativeJobId });
                    table.UniqueConstraint("AK_CentralTransientDerivativeJobs_CentralTransientEventId_CentralDerivativeJobId_SourceEventVersionId", x => new { x.CentralTransientEventId, x.CentralDerivativeJobId, x.SourceEventVersionId });
                    table.CheckConstraint("CK_CentralTransientDerivativeJobs_OutputCount", "[ExpectedOutputCount] = 5");
                    table.CheckConstraint("CK_CentralTransientDerivativeJobs_RequestLength", "[CanonicalRequestByteLength] > 0");
                    table.ForeignKey(
                        name: "FK_CentralTransientDerivativeJobs_CentralDerivativeJobs_CentralDerivativeJobId",
                        column: x => x.CentralDerivativeJobId,
                        principalTable: "CentralDerivativeJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralTransientDerivativeJobs_CentralTransientEventVersions_CentralTransientEventId_SourceEventVersionId",
                        columns: x => new { x.CentralTransientEventId, x.SourceEventVersionId },
                        principalTable: "CentralTransientEventVersions",
                        principalColumns: new[] { "CentralTransientEventId", "EventVersionId" });
                    table.ForeignKey(
                        name: "FK_CentralTransientDerivativeJobs_CentralTransientEvents_CentralTransientEventId",
                        column: x => x.CentralTransientEventId,
                        principalTable: "CentralTransientEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientNotifications",
                columns: table => new
                {
                    NotificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2"),
                    SupersedesNotificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupersedesNotificationCreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientNotifications", x => x.NotificationId);
                    table.UniqueConstraint("AK_CentralTransientNotifications_CentralTransientEventId_NotificationId", x => new { x.CentralTransientEventId, x.NotificationId });
                    table.UniqueConstraint("AK_CentralTransientNotifications_CentralTransientEventId_NotificationId_CreatedUtc", x => new { x.CentralTransientEventId, x.NotificationId, x.CreatedUtc });
                    table.CheckConstraint("CK_CentralTransientNotifications_Predecessor", "([SupersedesNotificationId] IS NULL AND [SupersedesNotificationCreatedUtc] IS NULL) OR ([SupersedesNotificationId] IS NOT NULL AND [SupersedesNotificationCreatedUtc] IS NOT NULL AND [SupersedesNotificationCreatedUtc] < [CreatedUtc])");
                    table.ForeignKey(
                        name: "FK_CentralTransientNotifications_CentralTransientAssessments_CentralTransientEventId_AssessmentId",
                        columns: x => new { x.CentralTransientEventId, x.AssessmentId },
                        principalTable: "CentralTransientAssessments",
                        principalColumns: new[] { "CentralTransientEventId", "AssessmentId" });
                    table.ForeignKey(
                        name: "FK_CentralTransientNotifications_CentralTransientEvents_CentralTransientEventId",
                        column: x => x.CentralTransientEventId,
                        principalTable: "CentralTransientEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralTransientNotifications_CentralTransientNotifications_CentralTransientEventId_SupersedesNotificationId_SupersedesNotif~",
                        columns: x => new { x.CentralTransientEventId, x.SupersedesNotificationId, x.SupersedesNotificationCreatedUtc },
                        principalTable: "CentralTransientNotifications",
                        principalColumns: new[] { "CentralTransientEventId", "NotificationId", "CreatedUtc" });
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientPayloadReleases",
                columns: table => new
                {
                    ReleaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorIdentity = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CanonicalRequestSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReasonCode = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2"),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientPayloadReleases", x => x.ReleaseId);
                    table.CheckConstraint("CK_CentralTransientPayloadReleases_Completion", "([State] = 'Pending' AND [CompletedUtc] IS NULL) OR ([State] IN ('Completed', 'Failed') AND [CompletedUtc] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_CentralTransientPayloadReleases_CentralTransientEvents_CentralTransientEventId",
                        column: x => x.CentralTransientEventId,
                        principalTable: "CentralTransientEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientReprocessingJobs",
                columns: table => new
                {
                    CentralDerivativeJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceEventVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorIdentity = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CanonicalRequestJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CanonicalRequestSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CanonicalRequestByteLength = table.Column<int>(type: "int", nullable: false),
                    RequestIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProducerName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProducerVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RecipeIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OptionsIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OptionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CommittedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResultAssessmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResultEventVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientReprocessingJobs", x => x.CentralDerivativeJobId);
                    table.CheckConstraint("CK_CentralTransientReprocessingJobs_Commit", "([CommittedUtc] IS NULL AND [ResultAssessmentId] IS NULL AND [ResultEventVersionId] IS NULL) OR ([CommittedUtc] IS NOT NULL AND [ResultAssessmentId] IS NOT NULL AND [ResultEventVersionId] IS NOT NULL)");
                    table.CheckConstraint("CK_CentralTransientReprocessingJobs_RequestLength", "[CanonicalRequestByteLength] > 0");
                    table.ForeignKey(
                        name: "FK_CentralTransientReprocessingJobs_CentralDerivativeJobs_CentralDerivativeJobId",
                        column: x => x.CentralDerivativeJobId,
                        principalTable: "CentralDerivativeJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralTransientReprocessingJobs_CentralTransientAssessments_CentralTransientEventId_ResultAssessmentId",
                        columns: x => new { x.CentralTransientEventId, x.ResultAssessmentId },
                        principalTable: "CentralTransientAssessments",
                        principalColumns: new[] { "CentralTransientEventId", "AssessmentId" });
                    table.ForeignKey(
                        name: "FK_CentralTransientReprocessingJobs_CentralTransientEventVersions_CentralTransientEventId_ResultEventVersionId",
                        columns: x => new { x.CentralTransientEventId, x.ResultEventVersionId },
                        principalTable: "CentralTransientEventVersions",
                        principalColumns: new[] { "CentralTransientEventId", "EventVersionId" });
                    table.ForeignKey(
                        name: "FK_CentralTransientReprocessingJobs_CentralTransientEventVersions_CentralTransientEventId_SourceEventVersionId",
                        columns: x => new { x.CentralTransientEventId, x.SourceEventVersionId },
                        principalTable: "CentralTransientEventVersions",
                        principalColumns: new[] { "CentralTransientEventId", "EventVersionId" });
                    table.ForeignKey(
                        name: "FK_CentralTransientReprocessingJobs_CentralTransientEvents_CentralTransientEventId",
                        column: x => x.CentralTransientEventId,
                        principalTable: "CentralTransientEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientReviews",
                columns: table => new
                {
                    ReviewId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReviewerIdentity = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Disposition = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OverrideClassification = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    OverrideMeteorSeverity = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    OverrideConfidenceMillionths = table.Column<int>(type: "int", nullable: true),
                    ReasonCodesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SupersedesReviewId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupersedesReviewCreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientReviews", x => x.ReviewId);
                    table.UniqueConstraint("AK_CentralTransientReviews_CentralTransientEventId_ReviewId", x => new { x.CentralTransientEventId, x.ReviewId });
                    table.UniqueConstraint("AK_CentralTransientReviews_CentralTransientEventId_ReviewId_AssessmentId", x => new { x.CentralTransientEventId, x.ReviewId, x.AssessmentId });
                    table.UniqueConstraint("AK_CentralTransientReviews_CentralTransientEventId_ReviewId_CreatedUtc", x => new { x.CentralTransientEventId, x.ReviewId, x.CreatedUtc });
                    table.CheckConstraint("CK_CentralTransientReviews_Override", "([Disposition] = 'Overridden' AND [OverrideClassification] IS NOT NULL AND [OverrideConfidenceMillionths] IS NOT NULL) OR ([Disposition] <> 'Overridden' AND [OverrideClassification] IS NULL AND [OverrideMeteorSeverity] IS NULL AND [OverrideConfidenceMillionths] IS NULL)");
                    table.CheckConstraint("CK_CentralTransientReviews_OverrideConfidence", "[OverrideConfidenceMillionths] IS NULL OR ([OverrideConfidenceMillionths] >= 0 AND [OverrideConfidenceMillionths] <= 1000000)");
                    table.CheckConstraint("CK_CentralTransientReviews_Predecessor", "([SupersedesReviewId] IS NULL AND [SupersedesReviewCreatedUtc] IS NULL) OR ([SupersedesReviewId] IS NOT NULL AND [SupersedesReviewId] <> [ReviewId] AND [SupersedesReviewCreatedUtc] IS NOT NULL AND [SupersedesReviewCreatedUtc] < [CreatedUtc])");
                    table.ForeignKey(
                        name: "FK_CentralTransientReviews_CentralTransientAssessments_CentralTransientEventId_AssessmentId",
                        columns: x => new { x.CentralTransientEventId, x.AssessmentId },
                        principalTable: "CentralTransientAssessments",
                        principalColumns: new[] { "CentralTransientEventId", "AssessmentId" });
                    table.ForeignKey(
                        name: "FK_CentralTransientReviews_CentralTransientEvents_CentralTransientEventId",
                        column: x => x.CentralTransientEventId,
                        principalTable: "CentralTransientEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralTransientReviews_CentralTransientReviews_CentralTransientEventId_SupersedesReviewId_SupersedesReviewCreatedUtc",
                        columns: x => new { x.CentralTransientEventId, x.SupersedesReviewId, x.SupersedesReviewCreatedUtc },
                        principalTable: "CentralTransientReviews",
                        principalColumns: new[] { "CentralTransientEventId", "ReviewId", "CreatedUtc" });
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientDerivativeOutputIntents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralDerivativeJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DerivativeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactRole = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ArtifactVariant = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    MediaType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ByteLength = table.Column<long>(type: "bigint", nullable: false),
                    ChecksumSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OutputIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    StorageReference = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    StorageETag = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true, collation: "Latin1_General_100_BIN2"),
                    StorageReferenceSha256 = table.Column<byte[]>(type: "binary(32)", nullable: true, computedColumnSql: "CONVERT(binary(32), HASHBYTES('SHA2_256', [StorageReference]))", stored: true),
                    ObjectState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StateReasonCode = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ObjectVerifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CommittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientDerivativeOutputIntents", x => x.Id);
                    table.UniqueConstraint("AK_CentralTransientDerivativeOutputIntents_CentralTransientEventId_CentralDerivativeJobId_Id", x => new { x.CentralTransientEventId, x.CentralDerivativeJobId, x.Id });
                    table.CheckConstraint("CK_CentralTransientDerivativeOutputIntents_ByteLength", "[ByteLength] > 0");
                    table.CheckConstraint("CK_CentralTransientDerivativeOutputIntents_Commit", "[CommittedAtUtc] IS NULL OR ([ObjectState] IN ('Available', 'Expired') AND [ObjectVerifiedAtUtc] IS NOT NULL AND [StorageETag] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_CentralTransientDerivativeOutputIntents_CentralTransientDerivativeJobs_CentralTransientEventId_CentralDerivativeJobId",
                        columns: x => new { x.CentralTransientEventId, x.CentralDerivativeJobId },
                        principalTable: "CentralTransientDerivativeJobs",
                        principalColumns: new[] { "CentralTransientEventId", "CentralDerivativeJobId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientEventVersionNotifications",
                columns: table => new
                {
                    EventVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NotificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientEventVersionNotifications", x => new { x.EventVersionId, x.Ordinal });
                    table.CheckConstraint("CK_CentralTransientEventVersionNotifications_Ordinal", "[Ordinal] >= 0");
                    table.ForeignKey(
                        name: "FK_CentralTransientEventVersionNotifications_CentralTransientEventVersions_CentralTransientEventId_EventVersionId",
                        columns: x => new { x.CentralTransientEventId, x.EventVersionId },
                        principalTable: "CentralTransientEventVersions",
                        principalColumns: new[] { "CentralTransientEventId", "EventVersionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralTransientEventVersionNotifications_CentralTransientNotifications_CentralTransientEventId_NotificationId",
                        columns: x => new { x.CentralTransientEventId, x.NotificationId },
                        principalTable: "CentralTransientNotifications",
                        principalColumns: new[] { "CentralTransientEventId", "NotificationId" });
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientPayloadReleaseItems",
                columns: table => new
                {
                    ReleaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReleasedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientPayloadReleaseItems", x => new { x.ReleaseId, x.Ordinal });
                    table.CheckConstraint("CK_CentralTransientPayloadReleaseItems_Ordinal", "[Ordinal] >= 0");
                    table.ForeignKey(
                        name: "FK_CentralTransientPayloadReleaseItems_CentralTransientPayloadReleases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "CentralTransientPayloadReleases",
                        principalColumn: "ReleaseId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientEventCurrent",
                columns: table => new
                {
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LatestEventVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LatestVersion = table.Column<int>(type: "int", nullable: false),
                    ActiveAssessmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LatestReviewId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    EffectiveClassification = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    EffectiveMeteorSeverity = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    EffectiveConfidenceMillionths = table.Column<int>(type: "int", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientEventCurrent", x => x.CentralTransientEventId);
                    table.ForeignKey(
                        name: "FK_CentralTransientEventCurrent_CentralTransientAssessments_CentralTransientEventId_ActiveAssessmentId",
                        columns: x => new { x.CentralTransientEventId, x.ActiveAssessmentId },
                        principalTable: "CentralTransientAssessments",
                        principalColumns: new[] { "CentralTransientEventId", "AssessmentId" });
                    table.ForeignKey(
                        name: "FK_CentralTransientEventCurrent_CentralTransientEventVersions_CentralTransientEventId_LatestEventVersionId",
                        columns: x => new { x.CentralTransientEventId, x.LatestEventVersionId },
                        principalTable: "CentralTransientEventVersions",
                        principalColumns: new[] { "CentralTransientEventId", "EventVersionId" });
                    table.ForeignKey(
                        name: "FK_CentralTransientEventCurrent_CentralTransientEvents_CentralTransientEventId",
                        column: x => x.CentralTransientEventId,
                        principalTable: "CentralTransientEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralTransientEventCurrent_CentralTransientReviews_CentralTransientEventId_LatestReviewId",
                        columns: x => new { x.CentralTransientEventId, x.LatestReviewId },
                        principalTable: "CentralTransientReviews",
                        principalColumns: new[] { "CentralTransientEventId", "ReviewId" });
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientEventVersionReviews",
                columns: table => new
                {
                    EventVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientEventVersionReviews", x => new { x.EventVersionId, x.Ordinal });
                    table.CheckConstraint("CK_CentralTransientEventVersionReviews_Ordinal", "[Ordinal] >= 0");
                    table.ForeignKey(
                        name: "FK_CentralTransientEventVersionReviews_CentralTransientEventVersions_CentralTransientEventId_EventVersionId",
                        columns: x => new { x.CentralTransientEventId, x.EventVersionId },
                        principalTable: "CentralTransientEventVersions",
                        principalColumns: new[] { "CentralTransientEventId", "EventVersionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralTransientEventVersionReviews_CentralTransientReviews_CentralTransientEventId_ReviewId",
                        columns: x => new { x.CentralTransientEventId, x.ReviewId },
                        principalTable: "CentralTransientReviews",
                        principalColumns: new[] { "CentralTransientEventId", "ReviewId" });
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientNotificationDispatches",
                columns: table => new
                {
                    DispatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InitialNotificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LatestNotificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Recipient = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    RecipientIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FencedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReasonCode = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2"),
                    SupersedesDispatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RequestedByActorIdentity = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2"),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true, collation: "Latin1_General_100_BIN2"),
                    CanonicalRequestSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientNotificationDispatches", x => x.DispatchId);
                    table.CheckConstraint("CK_CentralTransientNotificationDispatches_Timestamps", "([FencedUtc] IS NULL OR [FencedUtc] >= [CreatedUtc]) AND ([CompletedUtc] IS NULL OR [FencedUtc] IS NULL OR [CompletedUtc] >= [FencedUtc])");
                    table.ForeignKey(
                        name: "FK_CentralTransientNotificationDispatches_CentralTransientEvents_CentralTransientEventId",
                        column: x => x.CentralTransientEventId,
                        principalTable: "CentralTransientEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralTransientNotificationDispatches_CentralTransientNotificationDispatches_SupersedesDispatchId",
                        column: x => x.SupersedesDispatchId,
                        principalTable: "CentralTransientNotificationDispatches",
                        principalColumn: "DispatchId");
                    table.ForeignKey(
                        name: "FK_CentralTransientNotificationDispatches_CentralTransientNotifications_CentralTransientEventId_InitialNotificationId",
                        columns: x => new { x.CentralTransientEventId, x.InitialNotificationId },
                        principalTable: "CentralTransientNotifications",
                        principalColumns: new[] { "CentralTransientEventId", "NotificationId" });
                    table.ForeignKey(
                        name: "FK_CentralTransientNotificationDispatches_CentralTransientNotifications_CentralTransientEventId_LatestNotificationId",
                        columns: x => new { x.CentralTransientEventId, x.LatestNotificationId },
                        principalTable: "CentralTransientNotifications",
                        principalColumns: new[] { "CentralTransientEventId", "NotificationId" });
                    table.ForeignKey(
                        name: "FK_CentralTransientNotificationDispatches_CentralTransientReviews_CentralTransientEventId_ReviewId_AssessmentId",
                        columns: x => new { x.CentralTransientEventId, x.ReviewId, x.AssessmentId },
                        principalTable: "CentralTransientReviews",
                        principalColumns: new[] { "CentralTransientEventId", "ReviewId", "AssessmentId" });
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientReviewMutations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorIdentity = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CanonicalRequestSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    PreviousEventVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PreviousReviewId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResultEventVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResultReviewId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResultRowVersion = table.Column<byte[]>(type: "varbinary(8)", maxLength: 8, nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientReviewMutations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CentralTransientReviewMutations_CentralTransientEventVersions_CentralTransientEventId_PreviousEventVersionId",
                        columns: x => new { x.CentralTransientEventId, x.PreviousEventVersionId },
                        principalTable: "CentralTransientEventVersions",
                        principalColumns: new[] { "CentralTransientEventId", "EventVersionId" });
                    table.ForeignKey(
                        name: "FK_CentralTransientReviewMutations_CentralTransientEventVersions_CentralTransientEventId_ResultEventVersionId",
                        columns: x => new { x.CentralTransientEventId, x.ResultEventVersionId },
                        principalTable: "CentralTransientEventVersions",
                        principalColumns: new[] { "CentralTransientEventId", "EventVersionId" });
                    table.ForeignKey(
                        name: "FK_CentralTransientReviewMutations_CentralTransientEvents_CentralTransientEventId",
                        column: x => x.CentralTransientEventId,
                        principalTable: "CentralTransientEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralTransientReviewMutations_CentralTransientReviews_CentralTransientEventId_PreviousReviewId",
                        columns: x => new { x.CentralTransientEventId, x.PreviousReviewId },
                        principalTable: "CentralTransientReviews",
                        principalColumns: new[] { "CentralTransientEventId", "ReviewId" });
                    table.ForeignKey(
                        name: "FK_CentralTransientReviewMutations_CentralTransientReviews_CentralTransientEventId_ResultReviewId",
                        columns: x => new { x.CentralTransientEventId, x.ResultReviewId },
                        principalTable: "CentralTransientReviews",
                        principalColumns: new[] { "CentralTransientEventId", "ReviewId" });
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientDerivatives",
                columns: table => new
                {
                    DerivativeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceEventVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralDerivativeJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OutputIntentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactRole = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ArtifactVariant = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    MediaType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ByteLength = table.Column<long>(type: "bigint", nullable: false),
                    ArtifactChecksumSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RecipeIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OptionsIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OutputIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    LimitationsJson = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientDerivatives", x => x.DerivativeId);
                    table.UniqueConstraint("AK_CentralTransientDerivatives_CentralTransientEventId_DerivativeId", x => new { x.CentralTransientEventId, x.DerivativeId });
                    table.CheckConstraint("CK_CentralTransientDerivatives_ByteLength", "[ByteLength] > 0");
                    table.CheckConstraint("CK_CentralTransientDerivatives_ReviewAssessment", "[ReviewId] IS NULL OR [AssessmentId] IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_CentralTransientDerivatives_CentralTransientAssessments_CentralTransientEventId_AssessmentId",
                        columns: x => new { x.CentralTransientEventId, x.AssessmentId },
                        principalTable: "CentralTransientAssessments",
                        principalColumns: new[] { "CentralTransientEventId", "AssessmentId" });
                    table.ForeignKey(
                        name: "FK_CentralTransientDerivatives_CentralTransientDerivativeJobs_CentralTransientEventId_CentralDerivativeJobId_SourceEventVersion~",
                        columns: x => new { x.CentralTransientEventId, x.CentralDerivativeJobId, x.SourceEventVersionId },
                        principalTable: "CentralTransientDerivativeJobs",
                        principalColumns: new[] { "CentralTransientEventId", "CentralDerivativeJobId", "SourceEventVersionId" });
                    table.ForeignKey(
                        name: "FK_CentralTransientDerivatives_CentralTransientDerivativeOutputIntents_CentralTransientEventId_CentralDerivativeJobId_OutputInt~",
                        columns: x => new { x.CentralTransientEventId, x.CentralDerivativeJobId, x.OutputIntentId },
                        principalTable: "CentralTransientDerivativeOutputIntents",
                        principalColumns: new[] { "CentralTransientEventId", "CentralDerivativeJobId", "Id" });
                    table.ForeignKey(
                        name: "FK_CentralTransientDerivatives_CentralTransientEventVersions_CentralTransientEventId_SourceEventVersionId",
                        columns: x => new { x.CentralTransientEventId, x.SourceEventVersionId },
                        principalTable: "CentralTransientEventVersions",
                        principalColumns: new[] { "CentralTransientEventId", "EventVersionId" });
                    table.ForeignKey(
                        name: "FK_CentralTransientDerivatives_CentralTransientEvents_CentralTransientEventId",
                        column: x => x.CentralTransientEventId,
                        principalTable: "CentralTransientEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralTransientDerivatives_CentralTransientReviews_CentralTransientEventId_ReviewId_AssessmentId",
                        columns: x => new { x.CentralTransientEventId, x.ReviewId, x.AssessmentId },
                        principalTable: "CentralTransientReviews",
                        principalColumns: new[] { "CentralTransientEventId", "ReviewId", "AssessmentId" });
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientDerivativeSources",
                columns: table => new
                {
                    DerivativeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EvidenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactChecksumSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ObservationStartedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ObservationEndedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientDerivativeSources", x => new { x.DerivativeId, x.Ordinal });
                    table.UniqueConstraint("AK_CentralTransientDerivativeSources_DerivativeId_Ordinal_ObservationId", x => new { x.DerivativeId, x.Ordinal, x.ObservationId });
                    table.CheckConstraint("CK_CentralTransientDerivativeSources_ObservedInterval", "[ObservationStartedUtc] <= [ObservationEndedUtc]");
                    table.CheckConstraint("CK_CentralTransientDerivativeSources_Ordinal", "[Ordinal] >= 0");
                    table.ForeignKey(
                        name: "FK_CentralTransientDerivativeSources_CentralTransientDerivatives_CentralTransientEventId_DerivativeId",
                        columns: x => new { x.CentralTransientEventId, x.DerivativeId },
                        principalTable: "CentralTransientDerivatives",
                        principalColumns: new[] { "CentralTransientEventId", "DerivativeId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralTransientDerivativeSources_CentralTransientObservationSources_ObservationId_EvidenceId_CentralArtifactId_ArtifactId_A~",
                        columns: x => new { x.ObservationId, x.EvidenceId, x.CentralArtifactId, x.ArtifactId, x.ArtifactChecksumSha256, x.ObservationStartedUtc, x.ObservationEndedUtc },
                        principalTable: "CentralTransientObservationSources",
                        principalColumns: new[] { "ObservationId", "EvidenceId", "CentralArtifactId", "ArtifactId", "ArtifactChecksumSha256", "ObservationStartedUtc", "ObservationEndedUtc" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralTransientDerivativeSources_CentralTransientObservations_CentralTransientEventId_ObservationId",
                        columns: x => new { x.CentralTransientEventId, x.ObservationId },
                        principalTable: "CentralTransientObservations",
                        principalColumns: new[] { "CentralTransientEventId", "ObservationId" });
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientEventVersionDerivatives",
                columns: table => new
                {
                    EventVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DerivativeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientEventVersionDerivatives", x => new { x.EventVersionId, x.Ordinal });
                    table.CheckConstraint("CK_CentralTransientEventVersionDerivatives_Ordinal", "[Ordinal] >= 0");
                    table.ForeignKey(
                        name: "FK_CentralTransientEventVersionDerivatives_CentralTransientDerivatives_CentralTransientEventId_DerivativeId",
                        columns: x => new { x.CentralTransientEventId, x.DerivativeId },
                        principalTable: "CentralTransientDerivatives",
                        principalColumns: new[] { "CentralTransientEventId", "DerivativeId" });
                    table.ForeignKey(
                        name: "FK_CentralTransientEventVersionDerivatives_CentralTransientEventVersions_CentralTransientEventId_EventVersionId",
                        columns: x => new { x.CentralTransientEventId, x.EventVersionId },
                        principalTable: "CentralTransientEventVersions",
                        principalColumns: new[] { "CentralTransientEventId", "EventVersionId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralTransientDerivativeBackgrounds",
                columns: table => new
                {
                    DerivativeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservationOrdinal = table.Column<int>(type: "int", nullable: false),
                    BackgroundOrdinal = table.Column<int>(type: "int", nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactChecksumSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientDerivativeBackgrounds", x => new { x.DerivativeId, x.ObservationOrdinal, x.BackgroundOrdinal });
                    table.CheckConstraint("CK_CentralTransientDerivativeBackgrounds_Ordinals", "[ObservationOrdinal] >= 0 AND [BackgroundOrdinal] >= 0");
                    table.ForeignKey(
                        name: "FK_CentralTransientDerivativeBackgrounds_CentralTransientDerivativeSources_DerivativeId_ObservationOrdinal_ObservationId",
                        columns: x => new { x.DerivativeId, x.ObservationOrdinal, x.ObservationId },
                        principalTable: "CentralTransientDerivativeSources",
                        principalColumns: new[] { "DerivativeId", "Ordinal", "ObservationId" });
                    table.ForeignKey(
                        name: "FK_CentralTransientDerivativeBackgrounds_CentralTransientDerivatives_CentralTransientEventId_DerivativeId",
                        columns: x => new { x.CentralTransientEventId, x.DerivativeId },
                        principalTable: "CentralTransientDerivatives",
                        principalColumns: new[] { "CentralTransientEventId", "DerivativeId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralTransientDerivativeBackgrounds_CentralTransientObservationBackgrounds_ObservationId_BackgroundOrdinal_CentralArtifact~",
                        columns: x => new { x.ObservationId, x.BackgroundOrdinal, x.CentralArtifactId, x.ArtifactId, x.ArtifactChecksumSha256 },
                        principalTable: "CentralTransientObservationBackgrounds",
                        principalColumns: new[] { "ObservationId", "Ordinal", "CentralArtifactId", "ArtifactId", "ArtifactChecksumSha256" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralTransientDerivativeBackgrounds_CentralTransientObservations_CentralTransientEventId_ObservationId",
                        columns: x => new { x.CentralTransientEventId, x.ObservationId },
                        principalTable: "CentralTransientObservations",
                        principalColumns: new[] { "CentralTransientEventId", "ObservationId" });
                });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivativeBackgrounds_CentralArtifactId",
                table: "CentralTransientDerivativeBackgrounds",
                column: "CentralArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivativeBackgrounds_CentralTransientEventId_DerivativeId",
                table: "CentralTransientDerivativeBackgrounds",
                columns: new[] { "CentralTransientEventId", "DerivativeId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivativeBackgrounds_CentralTransientEventId_ObservationId",
                table: "CentralTransientDerivativeBackgrounds",
                columns: new[] { "CentralTransientEventId", "ObservationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivativeBackgrounds_DerivativeId_ObservationOrdinal_CentralArtifactId",
                table: "CentralTransientDerivativeBackgrounds",
                columns: new[] { "DerivativeId", "ObservationOrdinal", "CentralArtifactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivativeBackgrounds_DerivativeId_ObservationOrdinal_ObservationId",
                table: "CentralTransientDerivativeBackgrounds",
                columns: new[] { "DerivativeId", "ObservationOrdinal", "ObservationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivativeBackgrounds_ObservationId_BackgroundOrdinal_CentralArtifactId_ArtifactId_ArtifactChecksumSha256",
                table: "CentralTransientDerivativeBackgrounds",
                columns: new[] { "ObservationId", "BackgroundOrdinal", "CentralArtifactId", "ArtifactId", "ArtifactChecksumSha256" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivativeJobs_CentralTransientEventId_SourceEventVersionId_RecipeIdentitySha256_OptionsIdentitySha256",
                table: "CentralTransientDerivativeJobs",
                columns: new[] { "CentralTransientEventId", "SourceEventVersionId", "RecipeIdentitySha256", "OptionsIdentitySha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivativeJobs_RequestIdentitySha256",
                table: "CentralTransientDerivativeJobs",
                column: "RequestIdentitySha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivativeOutputIntents_ArtifactId",
                table: "CentralTransientDerivativeOutputIntents",
                column: "ArtifactId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivativeOutputIntents_CentralDerivativeJobId_Kind",
                table: "CentralTransientDerivativeOutputIntents",
                columns: new[] { "CentralDerivativeJobId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivativeOutputIntents_CentralTransientEventId_OutputIdentitySha256",
                table: "CentralTransientDerivativeOutputIntents",
                columns: new[] { "CentralTransientEventId", "OutputIdentitySha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivativeOutputIntents_ObjectState_CreatedAtUtc",
                table: "CentralTransientDerivativeOutputIntents",
                columns: new[] { "ObjectState", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivativeOutputIntents_StorageReferenceSha256",
                table: "CentralTransientDerivativeOutputIntents",
                column: "StorageReferenceSha256");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivatives_ArtifactId",
                table: "CentralTransientDerivatives",
                column: "ArtifactId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivatives_CentralTransientEventId_AssessmentId",
                table: "CentralTransientDerivatives",
                columns: new[] { "CentralTransientEventId", "AssessmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivatives_CentralTransientEventId_CentralDerivativeJobId_OutputIntentId",
                table: "CentralTransientDerivatives",
                columns: new[] { "CentralTransientEventId", "CentralDerivativeJobId", "OutputIntentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivatives_CentralTransientEventId_CentralDerivativeJobId_SourceEventVersionId",
                table: "CentralTransientDerivatives",
                columns: new[] { "CentralTransientEventId", "CentralDerivativeJobId", "SourceEventVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivatives_CentralTransientEventId_OutputIdentitySha256",
                table: "CentralTransientDerivatives",
                columns: new[] { "CentralTransientEventId", "OutputIdentitySha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivatives_CentralTransientEventId_ReviewId_AssessmentId",
                table: "CentralTransientDerivatives",
                columns: new[] { "CentralTransientEventId", "ReviewId", "AssessmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivatives_CentralTransientEventId_SourceEventVersionId",
                table: "CentralTransientDerivatives",
                columns: new[] { "CentralTransientEventId", "SourceEventVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivativeSources_CentralArtifactId",
                table: "CentralTransientDerivativeSources",
                column: "CentralArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivativeSources_CentralTransientEventId_DerivativeId",
                table: "CentralTransientDerivativeSources",
                columns: new[] { "CentralTransientEventId", "DerivativeId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivativeSources_CentralTransientEventId_ObservationId",
                table: "CentralTransientDerivativeSources",
                columns: new[] { "CentralTransientEventId", "ObservationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivativeSources_DerivativeId_EvidenceId",
                table: "CentralTransientDerivativeSources",
                columns: new[] { "DerivativeId", "EvidenceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientDerivativeSources_ObservationId_EvidenceId_CentralArtifactId_ArtifactId_ArtifactChecksumSha256_ObservationSt~",
                table: "CentralTransientDerivativeSources",
                columns: new[] { "ObservationId", "EvidenceId", "CentralArtifactId", "ArtifactId", "ArtifactChecksumSha256", "ObservationStartedUtc", "ObservationEndedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEventCurrent_CentralTransientEventId_ActiveAssessmentId",
                table: "CentralTransientEventCurrent",
                columns: new[] { "CentralTransientEventId", "ActiveAssessmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEventCurrent_CentralTransientEventId_LatestEventVersionId",
                table: "CentralTransientEventCurrent",
                columns: new[] { "CentralTransientEventId", "LatestEventVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEventCurrent_CentralTransientEventId_LatestReviewId",
                table: "CentralTransientEventCurrent",
                columns: new[] { "CentralTransientEventId", "LatestReviewId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEventCurrent_UpdatedUtc_CentralTransientEventId",
                table: "CentralTransientEventCurrent",
                columns: new[] { "UpdatedUtc", "CentralTransientEventId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEventVersionDerivatives_CentralTransientEventId_DerivativeId",
                table: "CentralTransientEventVersionDerivatives",
                columns: new[] { "CentralTransientEventId", "DerivativeId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEventVersionDerivatives_CentralTransientEventId_EventVersionId",
                table: "CentralTransientEventVersionDerivatives",
                columns: new[] { "CentralTransientEventId", "EventVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEventVersionDerivatives_EventVersionId_DerivativeId",
                table: "CentralTransientEventVersionDerivatives",
                columns: new[] { "EventVersionId", "DerivativeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEventVersionNotifications_CentralTransientEventId_EventVersionId",
                table: "CentralTransientEventVersionNotifications",
                columns: new[] { "CentralTransientEventId", "EventVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEventVersionNotifications_CentralTransientEventId_NotificationId",
                table: "CentralTransientEventVersionNotifications",
                columns: new[] { "CentralTransientEventId", "NotificationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEventVersionNotifications_EventVersionId_NotificationId",
                table: "CentralTransientEventVersionNotifications",
                columns: new[] { "EventVersionId", "NotificationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEventVersionReviews_CentralTransientEventId_EventVersionId",
                table: "CentralTransientEventVersionReviews",
                columns: new[] { "CentralTransientEventId", "EventVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEventVersionReviews_CentralTransientEventId_ReviewId",
                table: "CentralTransientEventVersionReviews",
                columns: new[] { "CentralTransientEventId", "ReviewId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEventVersionReviews_EventVersionId_ReviewId",
                table: "CentralTransientEventVersionReviews",
                columns: new[] { "EventVersionId", "ReviewId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientNotificationDispatches_CentralTransientEventId_InitialNotificationId",
                table: "CentralTransientNotificationDispatches",
                columns: new[] { "CentralTransientEventId", "InitialNotificationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientNotificationDispatches_CentralTransientEventId_LatestNotificationId",
                table: "CentralTransientNotificationDispatches",
                columns: new[] { "CentralTransientEventId", "LatestNotificationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientNotificationDispatches_CentralTransientEventId_RequestedByActorIdentity_IdempotencyKey",
                table: "CentralTransientNotificationDispatches",
                columns: new[] { "CentralTransientEventId", "RequestedByActorIdentity", "IdempotencyKey" },
                unique: true,
                filter: "[RequestedByActorIdentity] IS NOT NULL AND [IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientNotificationDispatches_CentralTransientEventId_ReviewId_AssessmentId",
                table: "CentralTransientNotificationDispatches",
                columns: new[] { "CentralTransientEventId", "ReviewId", "AssessmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientNotificationDispatches_CentralTransientEventId_ReviewId_RecipientIdentitySha256",
                table: "CentralTransientNotificationDispatches",
                columns: new[] { "CentralTransientEventId", "ReviewId", "RecipientIdentitySha256" },
                unique: true,
                filter: "[SupersedesDispatchId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientNotificationDispatches_State_CreatedUtc",
                table: "CentralTransientNotificationDispatches",
                columns: new[] { "State", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientNotificationDispatches_SupersedesDispatchId",
                table: "CentralTransientNotificationDispatches",
                column: "SupersedesDispatchId",
                unique: true,
                filter: "[SupersedesDispatchId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientNotifications_CentralTransientEventId_AssessmentId",
                table: "CentralTransientNotifications",
                columns: new[] { "CentralTransientEventId", "AssessmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientNotifications_CentralTransientEventId_SupersedesNotificationId",
                table: "CentralTransientNotifications",
                columns: new[] { "CentralTransientEventId", "SupersedesNotificationId" },
                unique: true,
                filter: "[SupersedesNotificationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientNotifications_CentralTransientEventId_SupersedesNotificationId_SupersedesNotificationCreatedUtc",
                table: "CentralTransientNotifications",
                columns: new[] { "CentralTransientEventId", "SupersedesNotificationId", "SupersedesNotificationCreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientPayloadReleaseItems_Kind_RecordId",
                table: "CentralTransientPayloadReleaseItems",
                columns: new[] { "Kind", "RecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientPayloadReleases_CentralTransientEventId_ActorIdentity_IdempotencyKey",
                table: "CentralTransientPayloadReleases",
                columns: new[] { "CentralTransientEventId", "ActorIdentity", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientReprocessingJobs_CentralTransientEventId_ActorIdentity_IdempotencyKey",
                table: "CentralTransientReprocessingJobs",
                columns: new[] { "CentralTransientEventId", "ActorIdentity", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientReprocessingJobs_CentralTransientEventId_ResultAssessmentId",
                table: "CentralTransientReprocessingJobs",
                columns: new[] { "CentralTransientEventId", "ResultAssessmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientReprocessingJobs_CentralTransientEventId_ResultEventVersionId",
                table: "CentralTransientReprocessingJobs",
                columns: new[] { "CentralTransientEventId", "ResultEventVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientReprocessingJobs_CentralTransientEventId_SourceEventVersionId",
                table: "CentralTransientReprocessingJobs",
                columns: new[] { "CentralTransientEventId", "SourceEventVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientReprocessingJobs_RequestIdentitySha256",
                table: "CentralTransientReprocessingJobs",
                column: "RequestIdentitySha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientReviewMutations_CentralTransientEventId_ActorIdentity_IdempotencyKey",
                table: "CentralTransientReviewMutations",
                columns: new[] { "CentralTransientEventId", "ActorIdentity", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientReviewMutations_CentralTransientEventId_PreviousEventVersionId",
                table: "CentralTransientReviewMutations",
                columns: new[] { "CentralTransientEventId", "PreviousEventVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientReviewMutations_CentralTransientEventId_PreviousReviewId",
                table: "CentralTransientReviewMutations",
                columns: new[] { "CentralTransientEventId", "PreviousReviewId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientReviewMutations_CentralTransientEventId_ResultEventVersionId",
                table: "CentralTransientReviewMutations",
                columns: new[] { "CentralTransientEventId", "ResultEventVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientReviewMutations_CentralTransientEventId_ResultReviewId",
                table: "CentralTransientReviewMutations",
                columns: new[] { "CentralTransientEventId", "ResultReviewId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientReviews_CentralTransientEventId_AssessmentId",
                table: "CentralTransientReviews",
                columns: new[] { "CentralTransientEventId", "AssessmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientReviews_CentralTransientEventId_SupersedesReviewId",
                table: "CentralTransientReviews",
                columns: new[] { "CentralTransientEventId", "SupersedesReviewId" },
                unique: true,
                filter: "[SupersedesReviewId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientReviews_CentralTransientEventId_SupersedesReviewId_SupersedesReviewCreatedUtc",
                table: "CentralTransientReviews",
                columns: new[] { "CentralTransientEventId", "SupersedesReviewId", "SupersedesReviewCreatedUtc" });

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "CentralTransientPayloadReleaseItems",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "CentralTransientReprocessingRequests",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralDerivativeJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorIdentity = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RecipeIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OptionsIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientReprocessingRequests", x => x.RequestId);
                    table.ForeignKey(
                        name: "FK_CentralTransientReprocessingRequests_CentralTransientEvents_CentralTransientEventId",
                        column: x => x.CentralTransientEventId,
                        principalTable: "CentralTransientEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralTransientReprocessingRequests_CentralTransientReprocessingJobs_CentralDerivativeJobId",
                        column: x => x.CentralDerivativeJobId,
                        principalTable: "CentralTransientReprocessingJobs",
                        principalColumn: "CentralDerivativeJobId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientPayloadReleases_State_CreatedUtc_ReleaseId",
                table: "CentralTransientPayloadReleases",
                columns: new[] { "State", "CreatedUtc", "ReleaseId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_CentralTransientPayloadReleaseItems_Outcome",
                table: "CentralTransientPayloadReleaseItems",
                sql: "([Outcome] = 'Pending' AND [ReleasedUtc] IS NULL) OR ([Outcome] IN ('Released', 'PreservedHeld') AND [ReleasedUtc] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CentralTransientNotificationDispatches_State",
                table: "CentralTransientNotificationDispatches",
                sql: "([State] = 'Pending' AND [FencedUtc] IS NULL AND [CompletedUtc] IS NULL) OR ([State] = 'Fenced' AND [FencedUtc] IS NOT NULL AND [CompletedUtc] IS NULL) OR ([State] IN ('Sent', 'Failed', 'Suppressed') AND [CompletedUtc] IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientReprocessingRequests_CentralDerivativeJobId",
                table: "CentralTransientReprocessingRequests",
                column: "CentralDerivativeJobId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientReprocessingRequests_CentralTransientEventId_ActorIdentity_IdempotencyKey",
                table: "CentralTransientReprocessingRequests",
                columns: new[] { "CentralTransientEventId", "ActorIdentity", "IdempotencyKey" },
                unique: true);

            BackfillCurrentProjection(migrationBuilder);
            CreateImmutableEvidenceTriggers(migrationBuilder);
            CreateDerivativeClosureTriggers(migrationBuilder);
            CreateNotificationTriggers(migrationBuilder);
            CreateReprocessingTrigger(migrationBuilder);
            CreatePayloadReleaseTriggers(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [CentralTransientReviews])
                   OR EXISTS (SELECT 1 FROM [CentralTransientReviewMutations])
                   OR EXISTS (SELECT 1 FROM [CentralTransientDerivativeJobs])
                   OR EXISTS (SELECT 1 FROM [CentralTransientDerivativeOutputIntents])
                   OR EXISTS (SELECT 1 FROM [CentralTransientDerivatives])
                   OR EXISTS (SELECT 1 FROM [CentralTransientNotifications])
                   OR EXISTS (SELECT 1 FROM [CentralTransientReprocessingJobs])
                   OR EXISTS (SELECT 1 FROM [CentralTransientPayloadReleases])
                    THROW 51010, 'Cannot downgrade while durable transient review, derivative, notification, reprocessing, or release state exists.', 1;
                """);

            migrationBuilder.DropIndex(
                name: "IX_CentralTransientEvents_EventCreatedUtc_Id",
                table: "CentralTransientEvents");

            migrationBuilder.DropTable(
                name: "CentralTransientReprocessingRequests");

            migrationBuilder.DropTable(
                name: "CentralTransientDerivativeBackgrounds");

            migrationBuilder.DropTable(
                name: "CentralTransientEventCurrent");

            migrationBuilder.DropTable(
                name: "CentralTransientEventVersionDerivatives");

            migrationBuilder.DropTable(
                name: "CentralTransientEventVersionNotifications");

            migrationBuilder.DropTable(
                name: "CentralTransientEventVersionReviews");

            migrationBuilder.DropTable(
                name: "CentralTransientNotificationDispatches");

            migrationBuilder.DropTable(
                name: "CentralTransientPayloadReleaseItems");

            migrationBuilder.DropTable(
                name: "CentralTransientReprocessingJobs");

            migrationBuilder.DropTable(
                name: "CentralTransientReviewMutations");

            migrationBuilder.DropTable(
                name: "CentralTransientDerivativeSources");

            migrationBuilder.DropTable(
                name: "CentralTransientNotifications");

            migrationBuilder.DropTable(
                name: "CentralTransientPayloadReleases");

            migrationBuilder.DropTable(
                name: "CentralTransientDerivatives");

            migrationBuilder.DropTable(
                name: "CentralTransientDerivativeOutputIntents");

            migrationBuilder.DropTable(
                name: "CentralTransientReviews");

            migrationBuilder.DropTable(
                name: "CentralTransientDerivativeJobs");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_CentralTransientObservationSources_ObservationId_EvidenceId_CentralArtifactId_ArtifactId_ArtifactChecksumSha256_ObservationS~",
                table: "CentralTransientObservationSources");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_CentralTransientObservationBackgrounds_ObservationId_Ordinal_CentralArtifactId_ArtifactId_ArtifactChecksumSha256",
                table: "CentralTransientObservationBackgrounds");
        }

        private static void BackfillCurrentProjection(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ;WITH [LatestVersions] AS
                (
                    SELECT [CentralTransientEventId], [EventVersionId], [Version], [VersionCreatedUtc],
                           ROW_NUMBER() OVER (
                               PARTITION BY [CentralTransientEventId]
                               ORDER BY [Version] DESC) AS [VersionRank]
                    FROM [CentralTransientEventVersions]
                ),
                [ActiveAssessments] AS
                (
                    SELECT latest.[CentralTransientEventId], latest.[EventVersionId], latest.[Version],
                           latest.[VersionCreatedUtc], link.[AssessmentId], assessment.[Classification],
                           assessment.[MeteorSeverity], assessment.[ConfidenceMillionths],
                           ROW_NUMBER() OVER (
                               PARTITION BY latest.[CentralTransientEventId]
                               ORDER BY link.[Ordinal] DESC) AS [AssessmentRank]
                    FROM [LatestVersions] AS latest
                    INNER JOIN [CentralTransientEventVersionAssessments] AS link
                        ON link.[CentralTransientEventId] = latest.[CentralTransientEventId]
                       AND link.[EventVersionId] = latest.[EventVersionId]
                    INNER JOIN [CentralTransientAssessments] AS assessment
                        ON assessment.[CentralTransientEventId] = link.[CentralTransientEventId]
                       AND assessment.[AssessmentId] = link.[AssessmentId]
                    WHERE latest.[VersionRank] = 1
                )
                INSERT INTO [CentralTransientEventCurrent]
                    ([CentralTransientEventId], [LatestEventVersionId], [LatestVersion], [ActiveAssessmentId],
                     [LatestReviewId], [ReviewState], [EffectiveClassification], [EffectiveMeteorSeverity],
                     [EffectiveConfidenceMillionths], [UpdatedUtc])
                SELECT [CentralTransientEventId], [EventVersionId], [Version], [AssessmentId],
                       NULL, N'NeedsReview', [Classification], [MeteorSeverity], [ConfidenceMillionths],
                       [VersionCreatedUtc]
                FROM [ActiveAssessments]
                WHERE [AssessmentRank] = 1;
                """);
        }

        private static void CreateImmutableEvidenceTriggers(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[]
            {
                "CentralTransientReviews",
                "CentralTransientEventVersionReviews",
                "CentralTransientReviewMutations",
                "CentralTransientDerivatives",
                "CentralTransientDerivativeSources",
                "CentralTransientDerivativeBackgrounds",
                "CentralTransientEventVersionDerivatives",
                "CentralTransientNotifications",
                "CentralTransientEventVersionNotifications",
                "CentralTransientReprocessingRequests"
            })
            {
                MigrationSql.ExecuteBatch(migrationBuilder, $"""
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

            MigrationSql.ExecuteBatch(migrationBuilder, """
                CREATE TRIGGER [TR_CentralTransientDerivativeOutputIntents_TerminalImmutable]
                ON [CentralTransientDerivativeOutputIntents]
                AFTER INSERT, UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1
                        FROM deleted AS d
                        LEFT JOIN inserted AS i ON i.[Id] = d.[Id]
                        WHERE i.[Id] IS NULL
                           OR (d.[CommittedAtUtc] IS NOT NULL AND NOT (
                               d.[ObjectState] = N'Available'
                                AND i.[ObjectState] = N'Expired'
                                AND i.[CommittedAtUtc] = d.[CommittedAtUtc]
                                AND i.[ObjectVerifiedAtUtc] = d.[ObjectVerifiedAtUtc]
                                AND i.[StorageETag] = d.[StorageETag]))
                           OR i.[CentralDerivativeJobId] <> d.[CentralDerivativeJobId]
                           OR i.[CentralTransientEventId] <> d.[CentralTransientEventId]
                           OR i.[Kind] <> d.[Kind]
                           OR i.[DerivativeId] <> d.[DerivativeId]
                           OR i.[ArtifactId] <> d.[ArtifactId]
                           OR i.[ArtifactRole] <> d.[ArtifactRole]
                           OR i.[ArtifactVariant] <> d.[ArtifactVariant]
                           OR i.[MediaType] <> d.[MediaType]
                           OR i.[ByteLength] <> d.[ByteLength]
                           OR i.[ChecksumSha256] <> d.[ChecksumSha256]
                           OR i.[OutputIdentitySha256] <> d.[OutputIdentitySha256]
                           OR i.[StorageReference] <> d.[StorageReference]
                           OR i.[CreatedAtUtc] <> d.[CreatedAtUtc])
                        THROW 51000, 'Transient derivative output identity is immutable.', 1;
                    IF EXISTS (
                        SELECT 1
                        FROM inserted AS i
                        WHERE i.[CommittedAtUtc] IS NOT NULL
                          AND (i.[CommittedAtUtc] < i.[CreatedAtUtc]
                               OR i.[ObjectVerifiedAtUtc] < i.[CreatedAtUtc]
                               OR i.[ObjectVerifiedAtUtc] > i.[CommittedAtUtc]
                               OR NOT EXISTS (
                                   SELECT 1 FROM [CentralTransientDerivatives] AS derivative
                                   WHERE derivative.[CentralTransientEventId] = i.[CentralTransientEventId]
                                     AND derivative.[CentralDerivativeJobId] = i.[CentralDerivativeJobId]
                                     AND derivative.[OutputIntentId] = i.[Id]
                                     AND derivative.[DerivativeId] = i.[DerivativeId]
                                     AND derivative.[ArtifactId] = i.[ArtifactId]
                                     AND derivative.[ArtifactRole] = i.[ArtifactRole]
                                     AND derivative.[ArtifactVariant] = i.[ArtifactVariant]
                                     AND derivative.[MediaType] = i.[MediaType]
                                     AND derivative.[ByteLength] = i.[ByteLength]
                                     AND derivative.[ArtifactChecksumSha256] = i.[ChecksumSha256]
                                     AND derivative.[OutputIdentitySha256] = i.[OutputIdentitySha256])))
                        THROW 51000, 'Committed transient derivative output evidence is incomplete.', 1;
                END
                """);

            MigrationSql.ExecuteBatch(migrationBuilder, """
                CREATE TRIGGER [TR_CentralTransientDerivativeJobs_CommittedImmutable]
                ON [CentralTransientDerivativeJobs]
                AFTER INSERT, UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1
                        FROM deleted AS d
                        LEFT JOIN inserted AS i ON i.[CentralDerivativeJobId] = d.[CentralDerivativeJobId]
                        WHERE i.[CentralDerivativeJobId] IS NULL
                           OR d.[CommittedAtUtc] IS NOT NULL
                           OR i.[CentralTransientEventId] <> d.[CentralTransientEventId]
                           OR i.[SourceEventVersionId] <> d.[SourceEventVersionId]
                           OR i.[RequestIdentitySha256] <> d.[RequestIdentitySha256]
                           OR i.[ProducerSchemaVersion] <> d.[ProducerSchemaVersion]
                           OR i.[ProducerName] <> d.[ProducerName]
                           OR i.[ProducerVersion] <> d.[ProducerVersion]
                           OR i.[RecipeIdentitySha256] <> d.[RecipeIdentitySha256]
                           OR i.[OptionsIdentitySha256] <> d.[OptionsIdentitySha256]
                           OR i.[CanonicalRequestJson] <> d.[CanonicalRequestJson]
                           OR i.[CanonicalRequestSha256] <> d.[CanonicalRequestSha256]
                           OR i.[CanonicalRequestByteLength] <> d.[CanonicalRequestByteLength]
                           OR i.[ExpectedOutputCount] <> d.[ExpectedOutputCount]
                           OR i.[CreatedAtUtc] <> d.[CreatedAtUtc])
                        THROW 51000, 'Transient derivative job identity is immutable.', 1;
                    IF EXISTS (
                        SELECT 1
                        FROM inserted AS i
                        WHERE i.[CommittedAtUtc] IS NOT NULL
                          AND (i.[CommittedAtUtc] < i.[CreatedAtUtc]
                               OR (SELECT COUNT(*) FROM [CentralTransientDerivativeOutputIntents] AS intent
                                   WHERE intent.[CentralDerivativeJobId] = i.[CentralDerivativeJobId]
                                     AND intent.[CommittedAtUtc] IS NOT NULL) <> i.[ExpectedOutputCount]
                               OR (SELECT COUNT(*) FROM [CentralTransientDerivatives] AS derivative
                                   WHERE derivative.[CentralDerivativeJobId] = i.[CentralDerivativeJobId])
                                   <> i.[ExpectedOutputCount]))
                        THROW 51000, 'Committed transient derivative bundle is incomplete.', 1;
                END
                """);
        }

        private static void CreateDerivativeClosureTriggers(MigrationBuilder migrationBuilder)
        {
            MigrationSql.ExecuteBatch(migrationBuilder, """
                CREATE TRIGGER [TR_CentralTransientDerivativeOutputIntents_Closed]
                ON [CentralTransientDerivativeOutputIntents]
                AFTER INSERT
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1 FROM inserted AS i
                        INNER JOIN [CentralTransientDerivativeJobs] AS job
                            ON job.[CentralTransientEventId] = i.[CentralTransientEventId]
                           AND job.[CentralDerivativeJobId] = i.[CentralDerivativeJobId]
                        WHERE job.[CommittedAtUtc] IS NOT NULL)
                        THROW 51000, 'Committed transient derivative bundles are closed.', 1;
                END
                """);

            MigrationSql.ExecuteBatch(migrationBuilder, """
                CREATE TRIGGER [TR_CentralTransientDerivatives_Closed]
                ON [CentralTransientDerivatives]
                AFTER INSERT
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1 FROM inserted AS i
                        INNER JOIN [CentralTransientDerivativeJobs] AS job
                            ON job.[CentralDerivativeJobId] = i.[CentralDerivativeJobId]
                        INNER JOIN [CentralTransientDerivativeOutputIntents] AS intent
                            ON intent.[Id] = i.[OutputIntentId]
                        WHERE job.[CommittedAtUtc] IS NOT NULL OR intent.[CommittedAtUtc] IS NOT NULL)
                        THROW 51000, 'Committed transient derivative bundles are closed.', 1;
                END
                """);

            foreach (var table in new[]
            {
                "CentralTransientDerivativeSources",
                "CentralTransientDerivativeBackgrounds"
            })
            {
                MigrationSql.ExecuteBatch(migrationBuilder, $"""
                    CREATE TRIGGER [TR_{table}_Closed]
                    ON [{table}]
                    AFTER INSERT
                    AS
                    BEGIN
                        SET NOCOUNT ON;
                        IF EXISTS (
                            SELECT 1 FROM inserted AS i
                            INNER JOIN [CentralTransientDerivatives] AS derivative
                                ON derivative.[DerivativeId] = i.[DerivativeId]
                            INNER JOIN [CentralTransientDerivativeJobs] AS job
                                ON job.[CentralDerivativeJobId] = derivative.[CentralDerivativeJobId]
                            WHERE job.[CommittedAtUtc] IS NOT NULL)
                            THROW 51000, 'Committed transient derivative bundles are closed.', 1;
                    END
                    """);
            }
        }

        private static void CreateNotificationTriggers(MigrationBuilder migrationBuilder)
        {
            MigrationSql.ExecuteBatch(migrationBuilder, """
                CREATE TRIGGER [TR_CentralTransientNotificationDispatches_Insert]
                ON [CentralTransientNotificationDispatches]
                AFTER INSERT
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM inserted WHERE [State] NOT IN (N'Pending', N'Failed', N'Suppressed'))
                        THROW 51000, 'Initial transient notification dispatch state is invalid.', 1;
                    IF EXISTS (
                        SELECT 1 FROM inserted AS i
                        INNER JOIN [CentralTransientNotifications] AS notification
                            ON notification.[CentralTransientEventId] = i.[CentralTransientEventId]
                           AND notification.[NotificationId] = i.[InitialNotificationId]
                        INNER JOIN [CentralTransientReviews] AS review
                            ON review.[CentralTransientEventId] = i.[CentralTransientEventId]
                           AND review.[ReviewId] = i.[ReviewId]
                        WHERE i.[InitialNotificationId] <> i.[LatestNotificationId]
                           OR notification.[AssessmentId] <> i.[AssessmentId]
                           OR notification.[Channel] <> i.[Channel]
                           OR notification.[State] <> i.[State]
                           OR review.[AssessmentId] <> i.[AssessmentId])
                        THROW 51000, 'Transient notification dispatch lineage is invalid.', 1;
                END
                """);

            MigrationSql.ExecuteBatch(migrationBuilder, """
                CREATE TRIGGER [TR_CentralTransientNotificationDispatches_Transition]
                ON [CentralTransientNotificationDispatches]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1 FROM deleted AS d
                        LEFT JOIN inserted AS i ON i.[DispatchId] = d.[DispatchId]
                        WHERE i.[DispatchId] IS NULL
                           OR i.[CentralTransientEventId] <> d.[CentralTransientEventId]
                           OR i.[AssessmentId] <> d.[AssessmentId]
                           OR i.[ReviewId] <> d.[ReviewId]
                           OR i.[InitialNotificationId] <> d.[InitialNotificationId]
                           OR i.[Channel] <> d.[Channel]
                           OR ISNULL(i.[Recipient], N'') <> ISNULL(d.[Recipient], N'')
                           OR ISNULL(i.[RecipientIdentitySha256], '') <> ISNULL(d.[RecipientIdentitySha256], '')
                           OR i.[CreatedUtc] <> d.[CreatedUtc]
                           OR ISNULL(i.[SupersedesDispatchId], '00000000-0000-0000-0000-000000000000') <>
                              ISNULL(d.[SupersedesDispatchId], '00000000-0000-0000-0000-000000000000')
                           OR ISNULL(i.[RequestedByActorIdentity], N'') <> ISNULL(d.[RequestedByActorIdentity], N'')
                           OR ISNULL(i.[IdempotencyKey], N'') <> ISNULL(d.[IdempotencyKey], N'')
                           OR ISNULL(i.[CanonicalRequestSha256], '') <> ISNULL(d.[CanonicalRequestSha256], '')
                           OR d.[State] IN (N'Sent', N'Failed', N'Suppressed')
                           OR NOT ((d.[State] = N'Pending' AND i.[State] = N'Fenced' AND
                                    d.[FencedUtc] IS NULL AND i.[FencedUtc] IS NOT NULL AND
                                    d.[CompletedUtc] IS NULL AND i.[CompletedUtc] IS NULL)
                               OR (d.[State] = N'Fenced' AND i.[State] IN (N'Sent', N'Failed') AND
                                   i.[FencedUtc] = d.[FencedUtc] AND i.[CompletedUtc] IS NOT NULL))
                    )
                        THROW 51000, 'Transient notification dispatch transition is invalid.', 1;
                    IF EXISTS (
                        SELECT 1 FROM inserted AS i
                        INNER JOIN deleted AS d ON d.[DispatchId] = i.[DispatchId]
                        INNER JOIN [CentralTransientNotifications] AS notification
                            ON notification.[CentralTransientEventId] = i.[CentralTransientEventId]
                           AND notification.[NotificationId] = i.[LatestNotificationId]
                        WHERE notification.[AssessmentId] <> i.[AssessmentId]
                           OR notification.[Channel] <> i.[Channel]
                           OR (i.[State] IN (N'Sent', N'Failed') AND notification.[State] <> i.[State])
                           OR (i.[LatestNotificationId] <> d.[LatestNotificationId] AND
                               notification.[SupersedesNotificationId] <> d.[LatestNotificationId]))
                        THROW 51000, 'Transient notification dispatch lineage is invalid.', 1;
                END
                """);
        }

        private static void CreateReprocessingTrigger(MigrationBuilder migrationBuilder)
        {
            MigrationSql.ExecuteBatch(migrationBuilder, """
                CREATE TRIGGER [TR_CentralTransientReprocessingJobs_Immutable]
                ON [CentralTransientReprocessingJobs]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1 FROM deleted AS d
                        LEFT JOIN inserted AS i ON i.[CentralDerivativeJobId] = d.[CentralDerivativeJobId]
                        WHERE i.[CentralDerivativeJobId] IS NULL
                           OR d.[CommittedUtc] IS NOT NULL
                           OR i.[CentralTransientEventId] <> d.[CentralTransientEventId]
                           OR i.[SourceEventVersionId] <> d.[SourceEventVersionId]
                           OR i.[ActorIdentity] <> d.[ActorIdentity]
                           OR i.[IdempotencyKey] <> d.[IdempotencyKey]
                           OR i.[CanonicalRequestJson] <> d.[CanonicalRequestJson]
                           OR i.[CanonicalRequestSha256] <> d.[CanonicalRequestSha256]
                           OR i.[CanonicalRequestByteLength] <> d.[CanonicalRequestByteLength]
                           OR i.[RequestIdentitySha256] <> d.[RequestIdentitySha256]
                           OR i.[ProducerName] <> d.[ProducerName]
                           OR i.[ProducerVersion] <> d.[ProducerVersion]
                           OR i.[RecipeIdentitySha256] <> d.[RecipeIdentitySha256]
                           OR i.[OptionsIdentitySha256] <> d.[OptionsIdentitySha256]
                           OR i.[OptionsJson] <> d.[OptionsJson]
                           OR i.[CreatedUtc] <> d.[CreatedUtc]
                           OR i.[CommittedUtc] IS NULL)
                        THROW 51000, 'Transient reprocessing evidence is immutable.', 1;
                END
                """);
        }

        private static void CreatePayloadReleaseTriggers(MigrationBuilder migrationBuilder)
        {
            MigrationSql.ExecuteBatch(migrationBuilder, """
                CREATE TRIGGER [TR_CentralTransientPayloadReleases_Insert]
                ON [CentralTransientPayloadReleases]
                AFTER INSERT
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM inserted WHERE [State] <> N'Pending')
                        THROW 51000, 'Initial transient payload release state is invalid.', 1;
                END
                """);

            MigrationSql.ExecuteBatch(migrationBuilder, """
                CREATE TRIGGER [TR_CentralTransientPayloadReleases_Transition]
                ON [CentralTransientPayloadReleases]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1 FROM deleted AS d
                        LEFT JOIN inserted AS i ON i.[ReleaseId] = d.[ReleaseId]
                        WHERE i.[ReleaseId] IS NULL
                           OR d.[State] NOT IN (N'Pending', N'Failed')
                           OR i.[CentralTransientEventId] <> d.[CentralTransientEventId]
                           OR i.[ActorIdentity] <> d.[ActorIdentity]
                           OR i.[IdempotencyKey] <> d.[IdempotencyKey]
                           OR i.[CanonicalRequestSha256] <> d.[CanonicalRequestSha256]
                           OR i.[CreatedUtc] <> d.[CreatedUtc]
                           OR NOT ((d.[State] = N'Pending' AND i.[State] IN (N'Completed', N'Failed'))
                               OR (d.[State] = N'Failed' AND i.[State] = N'Pending' AND
                                   i.[CompletedUtc] IS NULL AND i.[ReasonCode] IS NULL))
                           OR (i.[State] = N'Completed' AND EXISTS (
                               SELECT 1 FROM [CentralTransientPayloadReleaseItems] AS item
                               WHERE item.[ReleaseId] = i.[ReleaseId] AND item.[Outcome] = N'Pending')))
                        THROW 51000, 'Transient payload release transition is invalid.', 1;
                END
                """);

            MigrationSql.ExecuteBatch(migrationBuilder, """
                CREATE TRIGGER [TR_CentralTransientPayloadReleaseItems_Transition]
                ON [CentralTransientPayloadReleaseItems]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1 FROM deleted AS d
                        LEFT JOIN inserted AS i ON i.[ReleaseId] = d.[ReleaseId] AND i.[Ordinal] = d.[Ordinal]
                        WHERE i.[ReleaseId] IS NULL
                           OR d.[Outcome] <> N'Pending'
                           OR i.[Kind] <> d.[Kind]
                           OR i.[RecordId] <> d.[RecordId]
                           OR i.[Outcome] NOT IN (N'Released', N'PreservedHeld')
                           OR i.[ReleasedUtc] IS NULL)
                        THROW 51000, 'Transient payload release item transition is invalid.', 1;
                END
                """);

            MigrationSql.ExecuteBatch(migrationBuilder, """
                CREATE TRIGGER [TR_CentralTransientPayloadReleaseItems_Closed]
                ON [CentralTransientPayloadReleaseItems]
                AFTER INSERT
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1 FROM inserted AS i
                        INNER JOIN [CentralTransientPayloadReleases] AS release
                            ON release.[ReleaseId] = i.[ReleaseId]
                        WHERE release.[State] <> N'Pending' OR i.[Outcome] <> N'Pending')
                        THROW 51000, 'Transient payload release items are closed.', 1;
                END
                """);
        }
    }
}
