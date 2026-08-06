using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNetworkOperationsAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "CentralArtifactDownloadAuthorizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    MembershipRole = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RangeStart = table.Column<long>(type: "bigint", nullable: true),
                    RangeEnd = table.Column<long>(type: "bigint", nullable: true),
                    IssuedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralArtifactDownloadAuthorizations", x => x.Id);
                    table.CheckConstraint("CK_CentralArtifactDownloadAuthorizations_Expiry", "[ExpiresAtUtc] > [IssuedAtUtc]");
                    table.CheckConstraint("CK_CentralArtifactDownloadAuthorizations_MembershipRole", "[MembershipRole] IN (N'Viewer', N'Manager', N'Owner')");
                    table.CheckConstraint("CK_CentralArtifactDownloadAuthorizations_Range", "([RangeStart] IS NULL AND [RangeEnd] IS NULL) OR ([RangeStart] >= 0 AND [RangeEnd] >= [RangeStart])");
                    table.ForeignKey(
                        name: "FK_CentralArtifactDownloadAuthorizations_CentralArtifacts_CentralArtifactId",
                        column: x => x.CentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralArtifactDownloadAuthorizations_Observatories_ObservatoryId",
                        column: x => x.ObservatoryId,
                        principalTable: "Observatories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LogicalCameras",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeactivatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogicalCameras", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LogicalCameras_Observatories_ObservatoryId",
                        column: x => x.ObservatoryId,
                        principalTable: "Observatories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ObservatoryInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    TargetEmailSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    OfferedRole = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    InvitedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    IssuedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AcceptanceTokenSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    CanonicalSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObservatoryInvitations", x => x.Id);
                    table.CheckConstraint("CK_ObservatoryInvitations_Expiry", "[ExpiresAtUtc] > [IssuedAtUtc]");
                    table.CheckConstraint("CK_ObservatoryInvitations_OfferedRole", "[OfferedRole] IN (N'Viewer', N'Manager', N'Owner')");
                    table.ForeignKey(
                        name: "FK_ObservatoryInvitations_Observatories_ObservatoryId",
                        column: x => x.ObservatoryId,
                        principalTable: "Observatories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ObservatoryLocationDisclosureVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    DisclosureLevel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RegionCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RegionLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PublicLatitudeDegrees = table.Column<double>(type: "float", nullable: true),
                    PublicLongitudeDegrees = table.Column<double>(type: "float", nullable: true),
                    PublicPrecisionMeters = table.Column<double>(type: "float", nullable: true),
                    SourceObservatoryLocationVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EffectiveFromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SupersededAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CanonicalSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObservatoryLocationDisclosureVersions", x => x.Id);
                    table.CheckConstraint("CK_ObservatoryLocationDisclosureVersions_Level", "[DisclosureLevel] IN (N'Hidden', N'Region', N'Approximate', N'Exact')");
                    table.CheckConstraint("CK_ObservatoryLocationDisclosureVersions_Projection", "([DisclosureLevel] = N'Hidden' AND [RegionCode] IS NULL AND [RegionLabel] IS NULL AND [PublicLatitudeDegrees] IS NULL AND [PublicLongitudeDegrees] IS NULL AND [PublicPrecisionMeters] IS NULL AND [SourceObservatoryLocationVersionId] IS NULL) OR ([DisclosureLevel] = N'Region' AND [RegionCode] IS NOT NULL AND [RegionLabel] IS NOT NULL AND [PublicLatitudeDegrees] IS NULL AND [PublicLongitudeDegrees] IS NULL AND [PublicPrecisionMeters] IS NULL AND [SourceObservatoryLocationVersionId] IS NULL) OR ([DisclosureLevel] = N'Approximate' AND [PublicLatitudeDegrees] BETWEEN -90 AND 90 AND [PublicLongitudeDegrees] BETWEEN -180 AND 180 AND [PublicPrecisionMeters] > 0 AND [SourceObservatoryLocationVersionId] IS NOT NULL) OR ([DisclosureLevel] = N'Exact' AND [PublicLatitudeDegrees] BETWEEN -90 AND 90 AND [PublicLongitudeDegrees] BETWEEN -180 AND 180 AND [PublicPrecisionMeters] >= 0 AND [SourceObservatoryLocationVersionId] IS NOT NULL)");
                    table.CheckConstraint("CK_ObservatoryLocationDisclosureVersions_Version", "[Version] > 0");
                    table.ForeignKey(
                        name: "FK_ObservatoryLocationDisclosureVersions_Observatories_ObservatoryId",
                        column: x => x.ObservatoryId,
                        principalTable: "Observatories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ObservatoryLocationDisclosureVersions_ObservatoryLocationVersions_SourceObservatoryLocationVersionId",
                        column: x => x.SourceObservatoryLocationVersionId,
                        principalTable: "ObservatoryLocationVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ObservatoryMembershipAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PreviousRole = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    NewRole = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    ReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObservatoryMembershipAudits", x => x.Id);
                    table.CheckConstraint("CK_ObservatoryMembershipAudits_Action", "[Action] IN (N'Granted', N'RoleChanged', N'Removed', N'LegacyBackfilled', N'LegacyRejected')");
                    table.CheckConstraint("CK_ObservatoryMembershipAudits_NewRole", "[NewRole] IS NULL OR [NewRole] IN (N'Viewer', N'Manager', N'Owner')");
                    table.CheckConstraint("CK_ObservatoryMembershipAudits_PreviousRole", "[PreviousRole] IS NULL OR [PreviousRole] IN (N'Viewer', N'Manager', N'Owner')");
                    table.CheckConstraint("CK_ObservatoryMembershipAudits_Transition", "([Action] IN (N'Granted', N'LegacyBackfilled') AND [PreviousRole] IS NULL AND [NewRole] IS NOT NULL) OR ([Action] = N'RoleChanged' AND [PreviousRole] IS NOT NULL AND [NewRole] IS NOT NULL AND [PreviousRole] <> [NewRole]) OR ([Action] = N'Removed' AND [PreviousRole] IS NOT NULL AND [NewRole] IS NULL) OR ([Action] = N'LegacyRejected' AND [PreviousRole] IS NULL AND [NewRole] IS NULL)");
                    table.ForeignKey(
                        name: "FK_ObservatoryMembershipAudits_Observatories_ObservatoryId",
                        column: x => x.ObservatoryId,
                        principalTable: "Observatories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ObservatoryMemberships",
                columns: table => new
                {
                    ObservatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AddedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObservatoryMemberships", x => new { x.ObservatoryId, x.UserId });
                    table.CheckConstraint("CK_ObservatoryMemberships_Role", "[Role] IN (N'Viewer', N'Manager', N'Owner')");
                    table.ForeignKey(
                        name: "FK_ObservatoryMemberships_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ObservatoryMemberships_Observatories_ObservatoryId",
                        column: x => x.ObservatoryId,
                        principalTable: "Observatories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ObservatoryPublicationProfileVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    PublicSlug = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PublicDisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PublicDescription = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ProfileVisibility = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PublishEnvironmentalSummary = table.Column<bool>(type: "bit", nullable: false),
                    AllowAutomaticVerifiedEventInclusion = table.Column<bool>(type: "bit", nullable: false),
                    EffectiveFromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SupersededAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CanonicalSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObservatoryPublicationProfileVersions", x => x.Id);
                    table.CheckConstraint("CK_ObservatoryPublicationProfileVersions_Version", "[Version] > 0");
                    table.CheckConstraint("CK_ObservatoryPublicationProfileVersions_Visibility", "[ProfileVisibility] IN (N'Private', N'Public')");
                    table.ForeignKey(
                        name: "FK_ObservatoryPublicationProfileVersions_Observatories_ObservatoryId",
                        column: x => x.ObservatoryId,
                        principalTable: "Observatories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LogicalCameraInstallations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LogicalCameraId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegistrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstallationPublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RetiredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReplacesInstallationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssignedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RetiredByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    AssignmentReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RetirementReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogicalCameraInstallations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LogicalCameraInstallations_DeviceRegistrations_RegistrationId",
                        column: x => x.RegistrationId,
                        principalTable: "DeviceRegistrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LogicalCameraInstallations_LogicalCameraInstallations_ReplacesInstallationId",
                        column: x => x.ReplacesInstallationId,
                        principalTable: "LogicalCameraInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LogicalCameraInstallations_LogicalCameras_LogicalCameraId",
                        column: x => x.LogicalCameraId,
                        principalTable: "LogicalCameras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PublicRecordPublicationDecisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorityObservatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    LogicalCameraId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceEventVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CentralTransientDerivativeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProjectionSchemaVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EffectiveUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SupersedesDecisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicRecordPublicationDecisions", x => x.Id);
                    table.CheckConstraint("CK_PublicRecordPublicationDecisions_State", "[State] IN (N'Released', N'Withdrawn')");
                    table.CheckConstraint("CK_PublicRecordPublicationDecisions_Subject", "([SubjectKind] = N'LogicalCamera' AND [LogicalCameraId] IS NOT NULL AND [CentralArtifactId] IS NULL AND [CentralTransientEventId] IS NULL AND [SourceEventVersionId] IS NULL AND [CentralTransientDerivativeId] IS NULL) OR ([SubjectKind] = N'Artifact' AND [LogicalCameraId] IS NULL AND [CentralArtifactId] IS NOT NULL AND [CentralTransientEventId] IS NULL AND [SourceEventVersionId] IS NULL AND [CentralTransientDerivativeId] IS NULL) OR ([SubjectKind] = N'TransientEvent' AND [LogicalCameraId] IS NULL AND [CentralArtifactId] IS NULL AND [CentralTransientEventId] IS NOT NULL AND [SourceEventVersionId] IS NOT NULL AND [CentralTransientDerivativeId] IS NULL) OR ([SubjectKind] = N'TransientDerivative' AND [LogicalCameraId] IS NULL AND [CentralArtifactId] IS NULL AND [CentralTransientEventId] IS NULL AND [SourceEventVersionId] IS NULL AND [CentralTransientDerivativeId] IS NOT NULL)");
                    table.CheckConstraint("CK_PublicRecordPublicationDecisions_SubjectKind", "[SubjectKind] IN (N'LogicalCamera', N'Artifact', N'TransientEvent', N'TransientDerivative')");
                    table.ForeignKey(
                        name: "FK_PublicRecordPublicationDecisions_CentralArtifacts_CentralArtifactId",
                        column: x => x.CentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PublicRecordPublicationDecisions_CentralTransientDerivatives_CentralTransientDerivativeId",
                        column: x => x.CentralTransientDerivativeId,
                        principalTable: "CentralTransientDerivatives",
                        principalColumn: "DerivativeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PublicRecordPublicationDecisions_CentralTransientEventVersions_SourceEventVersionId",
                        column: x => x.SourceEventVersionId,
                        principalTable: "CentralTransientEventVersions",
                        principalColumn: "EventVersionId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PublicRecordPublicationDecisions_CentralTransientEvents_CentralTransientEventId",
                        column: x => x.CentralTransientEventId,
                        principalTable: "CentralTransientEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PublicRecordPublicationDecisions_LogicalCameras_LogicalCameraId",
                        column: x => x.LogicalCameraId,
                        principalTable: "LogicalCameras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PublicRecordPublicationDecisions_Observatories_AuthorityObservatoryId",
                        column: x => x.AuthorityObservatoryId,
                        principalTable: "Observatories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PublicRecordPublicationDecisions_PublicRecordPublicationDecisions_SupersedesDecisionId",
                        column: x => x.SupersedesDecisionId,
                        principalTable: "PublicRecordPublicationDecisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ObservatoryInvitationDispositions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvitationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObservatoryInvitationDispositions", x => x.Id);
                    table.CheckConstraint("CK_ObservatoryInvitationDispositions_Action", "[Action] IN (N'Accepted', N'Declined', N'Revoked', N'Expired')");
                    table.ForeignKey(
                        name: "FK_ObservatoryInvitationDispositions_ObservatoryInvitations_InvitationId",
                        column: x => x.InvitationId,
                        principalTable: "ObservatoryInvitations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifactDownloadAuthorizations_ActorUserId_IssuedAtUtc_Id",
                table: "CentralArtifactDownloadAuthorizations",
                columns: new[] { "ActorUserId", "IssuedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifactDownloadAuthorizations_CentralArtifactId_IssuedAtUtc_Id",
                table: "CentralArtifactDownloadAuthorizations",
                columns: new[] { "CentralArtifactId", "IssuedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifactDownloadAuthorizations_ObservatoryId",
                table: "CentralArtifactDownloadAuthorizations",
                column: "ObservatoryId");

            migrationBuilder.CreateIndex(
                name: "IX_LogicalCameraInstallations_InstallationPublicId",
                table: "LogicalCameraInstallations",
                column: "InstallationPublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LogicalCameraInstallations_LogicalCameraId",
                table: "LogicalCameraInstallations",
                column: "LogicalCameraId",
                unique: true,
                filter: "[RetiredAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LogicalCameraInstallations_RegistrationId",
                table: "LogicalCameraInstallations",
                column: "RegistrationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LogicalCameraInstallations_ReplacesInstallationId",
                table: "LogicalCameraInstallations",
                column: "ReplacesInstallationId",
                unique: true,
                filter: "[ReplacesInstallationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LogicalCameras_ObservatoryId_Slug",
                table: "LogicalCameras",
                columns: new[] { "ObservatoryId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ObservatoryInvitationDispositions_InvitationId",
                table: "ObservatoryInvitationDispositions",
                column: "InvitationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ObservatoryInvitations_ObservatoryId_TargetUserId_IssuedAtUtc",
                table: "ObservatoryInvitations",
                columns: new[] { "ObservatoryId", "TargetUserId", "IssuedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ObservatoryLocationDisclosureVersions_ObservatoryId",
                table: "ObservatoryLocationDisclosureVersions",
                column: "ObservatoryId",
                unique: true,
                filter: "[SupersededAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ObservatoryLocationDisclosureVersions_ObservatoryId_Version",
                table: "ObservatoryLocationDisclosureVersions",
                columns: new[] { "ObservatoryId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ObservatoryLocationDisclosureVersions_SourceObservatoryLocationVersionId",
                table: "ObservatoryLocationDisclosureVersions",
                column: "SourceObservatoryLocationVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_ObservatoryMembershipAudits_ObservatoryId_OccurredAtUtc_Id",
                table: "ObservatoryMembershipAudits",
                columns: new[] { "ObservatoryId", "OccurredAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ObservatoryMemberships_ObservatoryId_Role",
                table: "ObservatoryMemberships",
                columns: new[] { "ObservatoryId", "Role" });

            migrationBuilder.CreateIndex(
                name: "IX_ObservatoryMemberships_UserId_ObservatoryId",
                table: "ObservatoryMemberships",
                columns: new[] { "UserId", "ObservatoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_ObservatoryPublicationProfileVersions_ObservatoryId",
                table: "ObservatoryPublicationProfileVersions",
                column: "ObservatoryId",
                unique: true,
                filter: "[SupersededAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ObservatoryPublicationProfileVersions_ObservatoryId_Version",
                table: "ObservatoryPublicationProfileVersions",
                columns: new[] { "ObservatoryId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ObservatoryPublicationProfileVersions_PublicSlug",
                table: "ObservatoryPublicationProfileVersions",
                column: "PublicSlug",
                unique: true,
                filter: "[SupersededAtUtc] IS NULL AND [ProfileVisibility] = N'Public'");

            migrationBuilder.CreateIndex(
                name: "IX_PublicRecordPublicationDecisions_AuthorityObservatoryId_OccurredAtUtc_Id",
                table: "PublicRecordPublicationDecisions",
                columns: new[] { "AuthorityObservatoryId", "OccurredAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicRecordPublicationDecisions_CentralArtifactId",
                table: "PublicRecordPublicationDecisions",
                column: "CentralArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicRecordPublicationDecisions_CentralTransientDerivativeId",
                table: "PublicRecordPublicationDecisions",
                column: "CentralTransientDerivativeId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicRecordPublicationDecisions_CentralTransientEventId",
                table: "PublicRecordPublicationDecisions",
                column: "CentralTransientEventId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicRecordPublicationDecisions_LogicalCameraId",
                table: "PublicRecordPublicationDecisions",
                column: "LogicalCameraId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicRecordPublicationDecisions_PublicId",
                table: "PublicRecordPublicationDecisions",
                column: "PublicId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicRecordPublicationDecisions_SourceEventVersionId",
                table: "PublicRecordPublicationDecisions",
                column: "SourceEventVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_PublicRecordPublicationDecisions_SupersedesDecisionId",
                table: "PublicRecordPublicationDecisions",
                column: "SupersedesDecisionId",
                unique: true,
                filter: "[SupersedesDecisionId] IS NOT NULL");

            MigrationSql.ExecuteBatch(migrationBuilder, """
                INSERT INTO [ObservatoryMemberships] ([ObservatoryId], [UserId], [Role], [AddedAtUtc])
                SELECT observatory.[Id], observatory.[OwnerUserId], N'Owner', observatory.[CreatedAtUtc]
                FROM [Observatories] AS observatory
                INNER JOIN [AspNetUsers] AS owner
                    ON owner.[Id] = observatory.[OwnerUserId] AND owner.[AccountType] = 0;

                INSERT INTO [ObservatoryMembershipAudits]
                    ([Id], [ObservatoryId], [TargetUserId], [ActorUserId], [Action], [PreviousRole],
                     [NewRole], [ReasonCode], [OccurredAtUtc])
                SELECT NEWID(), observatory.[Id], observatory.[OwnerUserId], NULL, N'LegacyBackfilled', NULL,
                       N'Owner', N'legacy-owner-backfill', observatory.[CreatedAtUtc]
                FROM [Observatories] AS observatory
                INNER JOIN [AspNetUsers] AS owner
                    ON owner.[Id] = observatory.[OwnerUserId] AND owner.[AccountType] = 0;

                INSERT INTO [ObservatoryMembershipAudits]
                    ([Id], [ObservatoryId], [TargetUserId], [ActorUserId], [Action], [PreviousRole],
                     [NewRole], [ReasonCode], [OccurredAtUtc])
                SELECT NEWID(), observatory.[Id], observatory.[OwnerUserId], NULL, N'LegacyRejected', NULL,
                       NULL,
                       CASE WHEN owner.[Id] IS NULL
                           THEN N'legacy-owner-user-missing'
                           ELSE N'legacy-owner-not-human'
                       END,
                       observatory.[CreatedAtUtc]
                FROM [Observatories] AS observatory
                LEFT JOIN [AspNetUsers] AS owner ON owner.[Id] = observatory.[OwnerUserId]
                WHERE owner.[Id] IS NULL OR owner.[AccountType] <> 0;

                INSERT INTO [ObservatoryPublicationProfileVersions]
                    ([Id], [ObservatoryId], [Version], [PublicSlug], [PublicDisplayName], [PublicDescription],
                     [ProfileVisibility], [PublishEnvironmentalSummary], [AllowAutomaticVerifiedEventInclusion],
                     [EffectiveFromUtc], [SupersededAtUtc], [ActorUserId], [ReasonCode], [CanonicalSha256])
                SELECT NEWID(), observatory.[Id], 1,
                       N'legacy-' + LOWER(REPLACE(CONVERT(nvarchar(36), observatory.[Id]), N'-', N'')),
                       observatory.[Name], N'', N'Private', 0, 0, observatory.[CreatedAtUtc], NULL,
                       N'migration', N'legacy-private-default',
                       CONVERT(varchar(64), HASHBYTES('SHA2_256',
                           CONVERT(varbinary(max), N'legacy-private:' + CONVERT(nvarchar(36), observatory.[Id]))), 2)
                FROM [Observatories] AS observatory;

                INSERT INTO [ObservatoryLocationDisclosureVersions]
                    ([Id], [ObservatoryId], [Version], [DisclosureLevel], [RegionCode], [RegionLabel],
                     [PublicLatitudeDegrees], [PublicLongitudeDegrees], [PublicPrecisionMeters],
                     [SourceObservatoryLocationVersionId], [EffectiveFromUtc], [SupersededAtUtc],
                     [ActorUserId], [ReasonCode], [CanonicalSha256])
                SELECT NEWID(), observatory.[Id], 1, N'Hidden', NULL, NULL, NULL, NULL, NULL, NULL,
                       observatory.[CreatedAtUtc], NULL, N'migration', N'legacy-hidden-default',
                       CONVERT(varchar(64), HASHBYTES('SHA2_256',
                           CONVERT(varbinary(max), N'legacy-hidden:' + CONVERT(nvarchar(36), observatory.[Id]))), 2)
                FROM [Observatories] AS observatory;

                INSERT INTO [LogicalCameras]
                    ([Id], [ObservatoryId], [Slug], [Name], [Description], [CreatedAtUtc], [DeactivatedAtUtc],
                     [CreatedByUserId])
                SELECT registration.[Id], registration.[ObservatoryId],
                       N'legacy-' + LOWER(REPLACE(CONVERT(nvarchar(36), registration.[DevicePublicId]), N'-', N'')),
                       registration.[FriendlyName], N'',
                       COALESCE(registration.[ActivatedAtUtc], registration.[IssuedAtUtc]), NULL, N'migration'
                FROM [DeviceRegistrations] AS registration
                WHERE registration.[DevicePublicId] IS NOT NULL;

                INSERT INTO [LogicalCameraInstallations]
                    ([Id], [LogicalCameraId], [RegistrationId], [InstallationPublicId], [AssignedAtUtc],
                     [RetiredAtUtc], [ReplacesInstallationId], [AssignedByUserId], [RetiredByUserId],
                     [AssignmentReasonCode], [RetirementReasonCode])
                SELECT NEWID(), registration.[Id], registration.[Id], registration.[DevicePublicId],
                       COALESCE(registration.[ActivatedAtUtc], registration.[IssuedAtUtc]),
                       CASE WHEN registration.[Status] = N'Revoked'
                           THEN COALESCE(registration.[LastSeenUtc], registration.[ActivatedAtUtc], registration.[IssuedAtUtc])
                           ELSE NULL
                       END,
                       NULL, N'migration',
                       CASE WHEN registration.[Status] = N'Revoked' THEN N'migration' ELSE NULL END,
                       N'legacy-registration-backfill',
                       CASE WHEN registration.[Status] = N'Revoked' THEN N'legacy-registration-revoked' ELSE NULL END
                FROM [DeviceRegistrations] AS registration
                WHERE registration.[DevicePublicId] IS NOT NULL;

                IF EXISTS (
                    SELECT 1
                    FROM [Observatories] AS observatory
                    INNER JOIN [AspNetUsers] AS owner
                        ON owner.[Id] = observatory.[OwnerUserId] AND owner.[AccountType] = 0
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM [ObservatoryMemberships] AS membership
                        WHERE membership.[ObservatoryId] = observatory.[Id]
                          AND membership.[UserId] = observatory.[OwnerUserId]
                          AND membership.[Role] = N'Owner'))
                BEGIN
                    THROW 51000, 'A live human legacy owner could not be backfilled.', 1;
                END;

                IF EXISTS (
                    SELECT 1
                    FROM [Observatories] AS observatory
                    WHERE NOT EXISTS (
                        SELECT 1 FROM [ObservatoryMembershipAudits] AS audit
                        WHERE audit.[ObservatoryId] = observatory.[Id])
                       OR NOT EXISTS (
                        SELECT 1 FROM [ObservatoryPublicationProfileVersions] AS profile
                        WHERE profile.[ObservatoryId] = observatory.[Id] AND profile.[SupersededAtUtc] IS NULL)
                       OR NOT EXISTS (
                        SELECT 1 FROM [ObservatoryLocationDisclosureVersions] AS disclosure
                        WHERE disclosure.[ObservatoryId] = observatory.[Id] AND disclosure.[SupersededAtUtc] IS NULL))
                BEGIN
                    THROW 51000, 'Legacy network authority disposition evidence is incomplete.', 1;
                END;
                """);

            CreateImmutableTrigger(migrationBuilder, "ObservatoryMembershipAudits", "Observatory membership audit evidence");
            CreateImmutableTrigger(migrationBuilder, "CentralArtifactDownloadAuthorizations", "Central artifact download authorization evidence");
            CreateImmutableTrigger(migrationBuilder, "PublicRecordPublicationDecisions", "Public record publication decision history");
            CreateImmutableTrigger(migrationBuilder, "ObservatoryInvitations", "Observatory invitation history");
            CreateImmutableTrigger(migrationBuilder, "ObservatoryInvitationDispositions", "Observatory invitation disposition history");

            MigrationSql.ExecuteBatch(migrationBuilder, """
                CREATE TRIGGER [TR_ObservatoryPublicationProfileVersions_Transitions]
                ON [ObservatoryPublicationProfileVersions]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    IF (ROWCOUNT_BIG() = 0) RETURN;
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM deleted AS d LEFT JOIN inserted AS i ON i.[Id] = d.[Id] WHERE i.[Id] IS NULL)
                       OR NOT UPDATE([SupersededAtUtc])
                       OR UPDATE([Id]) OR UPDATE([ObservatoryId]) OR UPDATE([Version]) OR UPDATE([PublicSlug])
                       OR UPDATE([PublicDisplayName]) OR UPDATE([PublicDescription]) OR UPDATE([ProfileVisibility])
                       OR UPDATE([PublishEnvironmentalSummary]) OR UPDATE([AllowAutomaticVerifiedEventInclusion])
                       OR UPDATE([EffectiveFromUtc]) OR UPDATE([ActorUserId]) OR UPDATE([ReasonCode])
                       OR UPDATE([CanonicalSha256])
                       OR EXISTS (
                           SELECT 1 FROM deleted AS d INNER JOIN inserted AS i ON i.[Id] = d.[Id]
                           WHERE d.[SupersededAtUtc] IS NOT NULL OR i.[SupersededAtUtc] IS NULL)
                    BEGIN
                        THROW 51000, 'Publication profile versions allow only one terminal supersession.', 1;
                    END;
                END
                """);

            MigrationSql.ExecuteBatch(migrationBuilder, """
                CREATE TRIGGER [TR_ObservatoryLocationDisclosureVersions_Transitions]
                ON [ObservatoryLocationDisclosureVersions]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    IF (ROWCOUNT_BIG() = 0) RETURN;
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM deleted AS d LEFT JOIN inserted AS i ON i.[Id] = d.[Id] WHERE i.[Id] IS NULL)
                       OR NOT UPDATE([SupersededAtUtc])
                       OR UPDATE([Id]) OR UPDATE([ObservatoryId]) OR UPDATE([Version]) OR UPDATE([DisclosureLevel])
                       OR UPDATE([RegionCode]) OR UPDATE([RegionLabel]) OR UPDATE([PublicLatitudeDegrees])
                       OR UPDATE([PublicLongitudeDegrees]) OR UPDATE([PublicPrecisionMeters])
                       OR UPDATE([SourceObservatoryLocationVersionId]) OR UPDATE([EffectiveFromUtc])
                       OR UPDATE([ActorUserId]) OR UPDATE([ReasonCode]) OR UPDATE([CanonicalSha256])
                       OR EXISTS (
                           SELECT 1 FROM deleted AS d INNER JOIN inserted AS i ON i.[Id] = d.[Id]
                           WHERE d.[SupersededAtUtc] IS NOT NULL OR i.[SupersededAtUtc] IS NULL)
                    BEGIN
                        THROW 51000, 'Location disclosure versions allow only one terminal supersession.', 1;
                    END;
                END
                """);

            MigrationSql.ExecuteBatch(migrationBuilder, """
                CREATE TRIGGER [TR_LogicalCameraInstallations_Transitions]
                ON [LogicalCameraInstallations]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    IF (ROWCOUNT_BIG() = 0) RETURN;
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM deleted AS d LEFT JOIN inserted AS i ON i.[Id] = d.[Id] WHERE i.[Id] IS NULL)
                       OR NOT UPDATE([RetiredAtUtc])
                       OR UPDATE([Id]) OR UPDATE([LogicalCameraId]) OR UPDATE([RegistrationId])
                       OR UPDATE([InstallationPublicId]) OR UPDATE([AssignedAtUtc]) OR UPDATE([ReplacesInstallationId])
                       OR UPDATE([AssignedByUserId]) OR UPDATE([AssignmentReasonCode])
                       OR EXISTS (
                           SELECT 1 FROM deleted AS d INNER JOIN inserted AS i ON i.[Id] = d.[Id]
                           WHERE d.[RetiredAtUtc] IS NOT NULL OR i.[RetiredAtUtc] IS NULL
                              OR i.[RetiredByUserId] IS NULL OR i.[RetirementReasonCode] IS NULL)
                    BEGIN
                        THROW 51000, 'Logical camera installations allow only one terminal retirement.', 1;
                    END;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql("DROP TRIGGER [TR_LogicalCameraInstallations_Transitions]");
            migrationBuilder.Sql("DROP TRIGGER [TR_ObservatoryLocationDisclosureVersions_Transitions]");
            migrationBuilder.Sql("DROP TRIGGER [TR_ObservatoryPublicationProfileVersions_Transitions]");
            migrationBuilder.Sql("DROP TRIGGER [TR_ObservatoryInvitationDispositions_Immutable]");
            migrationBuilder.Sql("DROP TRIGGER [TR_ObservatoryInvitations_Immutable]");
            migrationBuilder.Sql("DROP TRIGGER [TR_PublicRecordPublicationDecisions_Immutable]");
            migrationBuilder.Sql("DROP TRIGGER [TR_CentralArtifactDownloadAuthorizations_Immutable]");
            migrationBuilder.Sql("DROP TRIGGER [TR_ObservatoryMembershipAudits_Immutable]");

            migrationBuilder.DropTable(
                name: "CentralArtifactDownloadAuthorizations");

            migrationBuilder.DropTable(
                name: "LogicalCameraInstallations");

            migrationBuilder.DropTable(
                name: "ObservatoryInvitationDispositions");

            migrationBuilder.DropTable(
                name: "ObservatoryLocationDisclosureVersions");

            migrationBuilder.DropTable(
                name: "ObservatoryMembershipAudits");

            migrationBuilder.DropTable(
                name: "ObservatoryMemberships");

            migrationBuilder.DropTable(
                name: "ObservatoryPublicationProfileVersions");

            migrationBuilder.DropTable(
                name: "PublicRecordPublicationDecisions");

            migrationBuilder.DropTable(
                name: "ObservatoryInvitations");

            migrationBuilder.DropTable(
                name: "LogicalCameras");
        }

        private static void CreateImmutableTrigger(
            MigrationBuilder migrationBuilder,
            string tableName,
            string evidenceName)
        {
            MigrationSql.ExecuteBatch(migrationBuilder, $$"""
                CREATE TRIGGER [TR_{{tableName}}_Immutable]
                ON [{{tableName}}]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    IF (ROWCOUNT_BIG() = 0) RETURN;
                    SET NOCOUNT ON;
                    THROW 51000, '{{evidenceName}} is immutable.', 1;
                END
                """);
        }
    }
}
