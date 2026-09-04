using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    AccountType = table.Column<int>(type: "int", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SecurityStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumber = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CentralObjectRecoveryDispositions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceObjectIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    SourceObjectKey = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false, collation: "Latin1_General_100_BIN2"),
                    TargetObjectKey = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true, collation: "Latin1_General_100_BIN2"),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OperationToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ByteLength = table.Column<long>(type: "bigint", nullable: false),
                    ContentChecksumSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    LastAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralObjectRecoveryDispositions", x => x.Id);
                    table.CheckConstraint("CK_CentralObjectRecoveryDispositions_AttemptCount", "[AttemptCount] >= 0");
                    table.CheckConstraint("CK_CentralObjectRecoveryDispositions_RetentionDeletion", "[OperationToken] IS NULL AND [CentralArtifactId] IS NULL AND [Kind] = 'OrphanQuarantine' AND [TargetObjectKey] IS NOT NULL OR [OperationToken] IS NOT NULL AND [CentralArtifactId] IS NOT NULL AND [Kind] = 'ExpiredDelete' AND [State] IN ('PendingDelete', 'Completed', 'Failed')");
                    table.CheckConstraint("CK_CentralObjectRecoveryDispositions_TokenizedState", "[OperationToken] IS NULL OR ([State] = 'PendingDelete' AND [CompletedAtUtc] IS NULL) OR ([State] = 'Completed' AND [CompletedAtUtc] IS NOT NULL AND [LastAttemptAtUtc] IS NOT NULL AND [AttemptCount] > 0 AND [NextAttemptAtUtc] IS NULL AND [ReasonCode] IS NULL AND [CompletedAtUtc] >= [LastAttemptAtUtc]) OR ([State] = 'Failed' AND [CompletedAtUtc] IS NULL AND [LastAttemptAtUtc] IS NOT NULL AND [AttemptCount] > 0 AND [NextAttemptAtUtc] IS NULL AND [ReasonCode] IS NOT NULL)");
                    table.CheckConstraint("CK_CentralObjectRecoveryDispositions_TokenizedTimestamps", "[OperationToken] IS NULL OR ([UpdatedAtUtc] >= [CreatedAtUtc] AND ([LastAttemptAtUtc] IS NULL OR [LastAttemptAtUtc] >= [CreatedAtUtc]))");
                });

            migrationBuilder.CreateTable(
                name: "CentralRecoveryCheckpoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    Phase = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ObjectPartition = table.Column<int>(type: "int", nullable: false),
                    ObjectCursor = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    StagingPartition = table.Column<int>(type: "int", nullable: false),
                    StagingCursor = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    NextInventoryAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    InventoryStartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastProgressAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastCompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastCycleAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastFailureAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FindingCount = table.Column<long>(type: "bigint", nullable: false),
                    FindingBytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralRecoveryCheckpoints", x => x.Id);
                });

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
                name: "DatabaseInitializationState",
                columns: table => new
                {
                    Id = table.Column<byte>(type: "tinyint", nullable: false),
                    InitializationVersion = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetMigrationId = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FailureStage = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseInitializationState", x => x.Id);
                    table.CheckConstraint("CK_DatabaseInitializationState_Completion", "([Status] = N'Completed' AND [CompletedAtUtc] IS NOT NULL AND [FailureStage] IS NULL) OR ([Status] <> N'Completed' AND [CompletedAtUtc] IS NULL)");
                    table.CheckConstraint("CK_DatabaseInitializationState_Singleton", "[Id] = 1");
                    table.CheckConstraint("CK_DatabaseInitializationState_Status", "[Status] IN (N'Running', N'Completed', N'Failed')");
                    table.CheckConstraint("CK_DatabaseInitializationState_Version", "[InitializationVersion] > 0");
                });

            migrationBuilder.CreateTable(
                name: "Observatories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LatitudeDegrees = table.Column<double>(type: "double precision", nullable: false),
                    LongitudeDegrees = table.Column<double>(type: "double precision", nullable: false),
                    ElevationMeters = table.Column<double>(type: "double precision", nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AllowedDeploymentRadiusMeters = table.Column<double>(type: "float", nullable: true),
                    CurrentLocationVersion = table.Column<long>(type: "bigint", nullable: true),
                    CurrentLocationCanonicalSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Observatories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictApplications",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ApplicationType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ClientId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ClientSecret = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClientType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ConsentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DisplayNames = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    JsonWebKeySet = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Permissions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PostLogoutRedirectUris = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Properties = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RedirectUris = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Requirements = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Settings = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictApplications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictScopes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ConcurrencyToken = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Descriptions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DisplayNames = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Properties = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Resources = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictScopes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RoleId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserPasskeys",
                columns: table => new
                {
                    CredentialId = table.Column<byte[]>(type: "varbinary(1024)", maxLength: 1024, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Data = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserPasskeys", x => x.CredentialId);
                    table.ForeignKey(
                        name: "FK_AspNetUserPasskeys_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    RoleId = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    LoginProvider = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RegisteredUserNotificationPreferences",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    InAppEnabled = table.Column<bool>(type: "bit", nullable: false),
                    EmailEnabled = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegisteredUserNotificationPreferences", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_RegisteredUserNotificationPreferences_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RegisteredUserSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegisteredUserSubscriptions", x => x.Id);
                    table.CheckConstraint("CK_RegisteredUserSubscriptions_Kind", "[Kind] = N'VerifiedEvent'");
                    table.ForeignKey(
                        name: "FK_RegisteredUserSubscriptions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                name: "RegisteredUserNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PublicRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReadUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeduplicationKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegisteredUserNotifications", x => x.Id);
                    table.CheckConstraint("CK_RegisteredUserNotifications_Kind", "[Kind] = N'VerifiedEventReleased'");
                    table.ForeignKey(
                        name: "FK_RegisteredUserNotifications_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RegisteredUserNotifications_CentralTransientEvents_CentralTransientEventId",
                        column: x => x.CentralTransientEventId,
                        principalTable: "CentralTransientEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RegisteredUserTransientEventBookmarks",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegisteredUserTransientEventBookmarks", x => new { x.UserId, x.CentralTransientEventId });
                    table.ForeignKey(
                        name: "FK_RegisteredUserTransientEventBookmarks_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RegisteredUserTransientEventBookmarks_CentralTransientEvents_CentralTransientEventId",
                        column: x => x.CentralTransientEventId,
                        principalTable: "CentralTransientEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AccessLevel = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ObservatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HashedKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUsedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiKeys_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ApiKeys_Observatories_ObservatoryId",
                        column: x => x.ObservatoryId,
                        principalTable: "Observatories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralProcessingOverrideVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CloudTransmissionThresholdMillionths = table.Column<int>(type: "int", nullable: true),
                    CentralValidationEnabled = table.Column<bool>(type: "bit", nullable: true),
                    EffectiveFromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SupersededAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralProcessingOverrideVersions", x => x.Id);
                    table.CheckConstraint("CK_CentralProcessingOverrideVersions_Threshold", "[CloudTransmissionThresholdMillionths] IS NULL OR [CloudTransmissionThresholdMillionths] BETWEEN 1 AND 999999");
                    table.CheckConstraint("CK_CentralProcessingOverrideVersions_Version", "[Version] > 0");
                    table.ForeignKey(
                        name: "FK_CentralProcessingOverrideVersions_Observatories_ObservatoryId",
                        column: x => x.ObservatoryId,
                        principalTable: "Observatories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CuratedPublicPlacementDecisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Surface = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ObservatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PublicRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DisplayOrder = table.Column<int>(type: "int", nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SupersedesDecisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CuratedPublicPlacementDecisions", x => x.Id);
                    table.CheckConstraint("CK_CuratedPublicPlacementDecisions_Order", "([State] = N'Featured' AND [DisplayOrder] BETWEEN 0 AND 999) OR ([State] <> N'Featured' AND [DisplayOrder] IS NULL)");
                    table.CheckConstraint("CK_CuratedPublicPlacementDecisions_State", "[State] IN (N'Featured', N'Suppressed', N'Cleared')");
                    table.CheckConstraint("CK_CuratedPublicPlacementDecisions_Subject", "([Surface] = N'HomeObservatory' AND [ObservatoryId] IS NOT NULL AND [PublicRecordId] IS NULL) OR ([Surface] = N'HomeEvent' AND [ObservatoryId] IS NULL AND [PublicRecordId] IS NOT NULL)");
                    table.CheckConstraint("CK_CuratedPublicPlacementDecisions_Surface", "[Surface] IN (N'HomeObservatory', N'HomeEvent')");
                    table.ForeignKey(
                        name: "FK_CuratedPublicPlacementDecisions_CuratedPublicPlacementDecisions_SupersedesDecisionId",
                        column: x => x.SupersedesDecisionId,
                        principalTable: "CuratedPublicPlacementDecisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CuratedPublicPlacementDecisions_Observatories_ObservatoryId",
                        column: x => x.ObservatoryId,
                        principalTable: "Observatories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeviceRegistrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ObservatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FriendlyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ObservatoryName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ObservatoryLatitudeDegrees = table.Column<double>(type: "float", nullable: false),
                    ObservatoryLongitudeDegrees = table.Column<double>(type: "float", nullable: false),
                    ObservatoryElevationMeters = table.Column<double>(type: "float", nullable: false),
                    ObservatoryTimeZoneId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ObservatoryLocationVersion = table.Column<long>(type: "bigint", nullable: true),
                    ObservatoryLocationCanonicalSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LocationEvidenceState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, defaultValue: "ObservatoryPinned"),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    OwnerDisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    OwnerEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    OwnerConfirmationMethod = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, defaultValue: "SelfAttested"),
                    OwnerConfirmationNotes = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    OwnerConfirmedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    VerificationCodeHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DevicePublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RegistrationTokenHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DeviceKeyHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    IssuedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedReason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ActivatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CurrentRigProfileVersion = table.Column<int>(type: "int", nullable: true),
                    CurrentRigProfileHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CurrentRigProfileUpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EnvelopeVersion = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, defaultValue: "v2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceRegistrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceRegistrations_Observatories_ObservatoryId",
                        column: x => x.ObservatoryId,
                        principalTable: "Observatories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EnvironmentalObservationSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdentitySha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ContentSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RigId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true, collation: "Latin1_General_100_BIN2"),
                    Provider = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SourceId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    MethodName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    MethodVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ParametersJson = table.Column<string>(type: "nvarchar(max)", maxLength: 32768, nullable: false),
                    ParametersSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnvironmentalObservationSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EnvironmentalObservationSources_Observatories_SiteId",
                        column: x => x.SiteId,
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
                name: "ObservatoryLocationVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CanonicalSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EffectiveFromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SupersededAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LatitudeDegrees = table.Column<double>(type: "float", nullable: false),
                    LongitudeDegrees = table.Column<double>(type: "float", nullable: false),
                    ElevationMeters = table.Column<double>(type: "float", nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AllowedDeploymentRadiusMeters = table.Column<double>(type: "float", nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObservatoryLocationVersions", x => x.Id);
                    table.CheckConstraint("CK_ObservatoryLocationVersions_Interval", "[SupersededAtUtc] IS NULL OR [SupersededAtUtc] >= [EffectiveFromUtc]");
                    table.CheckConstraint("CK_ObservatoryLocationVersions_Latitude", "[LatitudeDegrees] >= -90 AND [LatitudeDegrees] <= 90");
                    table.CheckConstraint("CK_ObservatoryLocationVersions_Longitude", "[LongitudeDegrees] >= -180 AND [LongitudeDegrees] <= 180");
                    table.CheckConstraint("CK_ObservatoryLocationVersions_Radius", "[AllowedDeploymentRadiusMeters] IS NULL OR [AllowedDeploymentRadiusMeters] >= 0");
                    table.CheckConstraint("CK_ObservatoryLocationVersions_Version", "[Version] >= 1");
                    table.ForeignKey(
                        name: "FK_ObservatoryLocationVersions_Observatories_ObservatoryId",
                        column: x => x.ObservatoryId,
                        principalTable: "Observatories",
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
                    table.CheckConstraint("CK_ObservatoryMembershipAudits_Action", "[Action] IN (N'Granted', N'RoleChanged', N'Removed')");
                    table.CheckConstraint("CK_ObservatoryMembershipAudits_NewRole", "[NewRole] IS NULL OR [NewRole] IN (N'Viewer', N'Manager', N'Owner')");
                    table.CheckConstraint("CK_ObservatoryMembershipAudits_PreviousRole", "[PreviousRole] IS NULL OR [PreviousRole] IN (N'Viewer', N'Manager', N'Owner')");
                    table.CheckConstraint("CK_ObservatoryMembershipAudits_Transition", "([Action] = N'Granted' AND [PreviousRole] IS NULL AND [NewRole] IS NOT NULL) OR ([Action] = N'RoleChanged' AND [PreviousRole] IS NOT NULL AND [NewRole] IS NOT NULL AND [PreviousRole] <> [NewRole]) OR ([Action] = N'Removed' AND [PreviousRole] IS NOT NULL AND [NewRole] IS NULL)");
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
                name: "RegisteredUserObservatoryFollows",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ObservatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegisteredUserObservatoryFollows", x => new { x.UserId, x.ObservatoryId });
                    table.ForeignKey(
                        name: "FK_RegisteredUserObservatoryFollows_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RegisteredUserObservatoryFollows_Observatories_ObservatoryId",
                        column: x => x.ObservatoryId,
                        principalTable: "Observatories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictAuthorizations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ApplicationId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreationDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Properties = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Scopes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictAuthorizations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenIddictAuthorizations_OpenIddictApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "OpenIddictApplications",
                        principalColumn: "Id");
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
                name: "CentralTransientPayloadReleaseItems",
                columns: table => new
                {
                    ReleaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReleasedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReservationToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StorageReference = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true, collation: "Latin1_General_100_BIN2"),
                    TargetRowVersion = table.Column<byte[]>(type: "varbinary(8)", maxLength: 8, nullable: true),
                    TargetGeneration = table.Column<long>(type: "bigint", nullable: true),
                    RetryCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    RetryAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FailureReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true, collation: "Latin1_General_100_BIN2"),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientPayloadReleaseItems", x => new { x.ReleaseId, x.Ordinal });
                    table.CheckConstraint("CK_CentralTransientPayloadReleaseItems_Ordinal", "[Ordinal] >= 0");
                    table.CheckConstraint("CK_CentralTransientPayloadReleaseItems_Outcome", "([Outcome] = 'Pending' AND [ReleasedUtc] IS NULL AND [FailureReasonCode] IS NULL) OR ([Outcome] IN ('Released', 'PreservedHeld') AND [ReleasedUtc] IS NOT NULL AND [FailureReasonCode] IS NULL) OR ([Outcome] = 'Failed' AND [ReleasedUtc] IS NOT NULL AND [FailureReasonCode] IS NOT NULL)");
                    table.CheckConstraint("CK_CentralTransientPayloadReleaseItems_Reservation", "(([RequestedAtUtc] IS NULL AND [StorageReference] IS NULL AND [TargetRowVersion] IS NULL AND [TargetGeneration] IS NULL) OR ([RequestedAtUtc] IS NOT NULL AND [StorageReference] IS NOT NULL AND [TargetRowVersion] IS NOT NULL AND DATALENGTH([TargetRowVersion]) = 8 AND [TargetGeneration] IS NOT NULL)) AND ([ReservationToken] IS NULL OR ([Outcome] = 'Pending' AND [RequestedAtUtc] IS NOT NULL AND [RetryAtUtc] IS NULL)) AND ([RetryAtUtc] IS NULL OR ([Outcome] = 'Pending' AND [ReservationToken] IS NULL AND [RequestedAtUtc] IS NOT NULL)) AND ([Outcome] = 'Pending' OR ([ReservationToken] IS NULL AND [RetryAtUtc] IS NULL))");
                    table.CheckConstraint("CK_CentralTransientPayloadReleaseItems_RetryCount", "[RetryCount] >= 0");
                    table.ForeignKey(
                        name: "FK_CentralTransientPayloadReleaseItems_CentralTransientPayloadReleases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "CentralTransientPayloadReleases",
                        principalColumn: "ReleaseId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeviceFleetStates",
                columns: table => new
                {
                    RegistrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BootSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ApparentClockOffsetSeconds = table.Column<double>(type: "float", nullable: false),
                    ClockDiagnostic = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    ReportedHealth = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    HasStoragePressure = table.Column<bool>(type: "bit", nullable: false),
                    HasRequiredLaneFailure = table.Column<bool>(type: "bit", nullable: false),
                    HasQuarantine = table.Column<bool>(type: "bit", nullable: false),
                    SoftwareVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ConfigurationSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StatusFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CurrentPayloadSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", maxLength: 65536, nullable: false),
                    LastSequenceGapUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SequenceGapCount = table.Column<long>(type: "bigint", nullable: false),
                    LastBootSessionChangeUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BootSessionChangeCount = table.Column<long>(type: "bigint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceFleetStates", x => x.RegistrationId);
                    table.CheckConstraint("CK_DeviceFleetStates_Sequence", "[Sequence] > 0");
                    table.ForeignKey(
                        name: "FK_DeviceFleetStates_DeviceRegistrations_RegistrationId",
                        column: x => x.RegistrationId,
                        principalTable: "DeviceRegistrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceHeartbeatRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RegistrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BootSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PayloadSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StatusFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ReportedHealth = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ClockDiagnostic = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    AdvancedCurrent = table.Column<bool>(type: "bit", nullable: false),
                    IsSignificantSnapshot = table.Column<bool>(type: "bit", nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", maxLength: 65536, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceHeartbeatRecords", x => x.Id);
                    table.CheckConstraint("CK_DeviceHeartbeatRecords_Sequence", "[Sequence] > 0");
                    table.ForeignKey(
                        name: "FK_DeviceHeartbeatRecords_DeviceRegistrations_RegistrationId",
                        column: x => x.RegistrationId,
                        principalTable: "DeviceRegistrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceRigProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegistrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DevicePublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    ConfigHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ConfigJson = table.Column<string>(type: "nvarchar(max)", maxLength: 262144, nullable: false),
                    ProfileName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ProfileVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ProfileSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SoftwareVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EffectiveFromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceRigProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceRigProfiles_DeviceRegistrations_RegistrationId",
                        column: x => x.RegistrationId,
                        principalTable: "DeviceRegistrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EnvironmentalObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RigId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true, collation: "Latin1_General_100_BIN2"),
                    SourceKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SourceIdentitySha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ObservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SchemaVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    NumericValue = table.Column<double>(type: "float", nullable: true),
                    BooleanValue = table.Column<bool>(type: "bit", nullable: true),
                    Quality = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Uncertainty = table.Column<double>(type: "float", nullable: true),
                    SubmittedNumericValue = table.Column<double>(type: "float", nullable: true),
                    SubmittedUnit = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ObservedFromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ObservedThroughUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ValidFromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ValidThroughUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StaleAfterUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ApparentClockOffsetSeconds = table.Column<double>(type: "float", nullable: false),
                    ClockDiagnostic = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    PayloadSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnvironmentalObservations", x => x.Id);
                    table.CheckConstraint("CK_EnvironmentalObservations_ObservedInterval", "([ObservedFromUtc] IS NULL AND [ObservedThroughUtc] IS NULL) OR ([ObservedFromUtc] IS NOT NULL AND [ObservedThroughUtc] IS NOT NULL AND [ObservedFromUtc] <= [ObservedThroughUtc])");
                    table.CheckConstraint("CK_EnvironmentalObservations_SubmittedValue", "([SubmittedNumericValue] IS NULL AND [SubmittedUnit] IS NULL) OR ([SubmittedNumericValue] IS NOT NULL AND [SubmittedUnit] IS NOT NULL)");
                    table.CheckConstraint("CK_EnvironmentalObservations_Uncertainty", "[Uncertainty] IS NULL OR [Uncertainty] >= 0");
                    table.CheckConstraint("CK_EnvironmentalObservations_Validity", "[ValidFromUtc] < [ValidThroughUtc] AND [StaleAfterUtc] >= [ValidFromUtc] AND [StaleAfterUtc] <= [ValidThroughUtc]");
                    table.CheckConstraint("CK_EnvironmentalObservations_Value", "([NumericValue] IS NOT NULL AND [BooleanValue] IS NULL) OR ([NumericValue] IS NULL AND [BooleanValue] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_EnvironmentalObservations_EnvironmentalObservationSources_SourceRecordId",
                        column: x => x.SourceRecordId,
                        principalTable: "EnvironmentalObservationSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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

            migrationBuilder.CreateTable(
                name: "DeviceDeploymentLocationVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegistrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DevicePublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ObservatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservatoryLocationVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservatoryLocationVersionNumber = table.Column<long>(type: "bigint", nullable: false),
                    ObservatoryLocationCanonicalSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LocationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CanonicalSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    SourceKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    HorizontalAccuracyMeters = table.Column<double>(type: "float", nullable: true),
                    EffectiveFromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EffectiveUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LatitudeDegrees = table.Column<double>(type: "float", nullable: false),
                    LongitudeDegrees = table.Column<double>(type: "float", nullable: false),
                    ElevationMeters = table.Column<double>(type: "float", nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ProposedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolvedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ConcurrencyToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceDeploymentLocationVersions", x => x.Id);
                    table.CheckConstraint("CK_DeviceDeploymentLocationVersions_Accuracy", "[HorizontalAccuracyMeters] IS NULL OR [HorizontalAccuracyMeters] >= 0");
                    table.CheckConstraint("CK_DeviceDeploymentLocationVersions_Interval", "[EffectiveUntilUtc] IS NULL OR [EffectiveUntilUtc] > [EffectiveFromUtc]");
                    table.CheckConstraint("CK_DeviceDeploymentLocationVersions_Latitude", "[LatitudeDegrees] >= -90 AND [LatitudeDegrees] <= 90");
                    table.CheckConstraint("CK_DeviceDeploymentLocationVersions_Longitude", "[LongitudeDegrees] >= -180 AND [LongitudeDegrees] <= 180");
                    table.CheckConstraint("CK_DeviceDeploymentLocationVersions_Resolution", "([Status] = N'Pending' AND [ResolvedAtUtc] IS NULL) OR ([Status] <> N'Pending' AND [ResolvedAtUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_DeviceDeploymentLocationVersions_Version", "[Version] >= 1 AND [ObservatoryLocationVersionNumber] >= 1");
                    table.ForeignKey(
                        name: "FK_DeviceDeploymentLocationVersions_DeviceRegistrations_RegistrationId",
                        column: x => x.RegistrationId,
                        principalTable: "DeviceRegistrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DeviceDeploymentLocationVersions_ObservatoryLocationVersions_ObservatoryLocationVersionId",
                        column: x => x.ObservatoryLocationVersionId,
                        principalTable: "ObservatoryLocationVersions",
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
                name: "OpenIddictTokens",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ApplicationId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    AuthorizationId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreationDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExpirationDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Properties = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RedemptionDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReferenceId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Type = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenIddictTokens_OpenIddictApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "OpenIddictApplications",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OpenIddictTokens_OpenIddictAuthorizations_AuthorizationId",
                        column: x => x.AuthorizationId,
                        principalTable: "OpenIddictAuthorizations",
                        principalColumn: "Id");
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
                    table.CheckConstraint("CK_CentralTransientNotificationDispatches_State", "([State] = 'Pending' AND [FencedUtc] IS NULL AND [CompletedUtc] IS NULL) OR ([State] = 'Fenced' AND [FencedUtc] IS NOT NULL AND [CompletedUtc] IS NULL) OR ([State] IN ('Sent', 'Failed', 'Suppressed') AND [CompletedUtc] IS NOT NULL)");
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
                name: "EnvironmentalObservationLineage",
                columns: table => new
                {
                    DerivedObservationRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    SourceObservationRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnvironmentalObservationLineage", x => new { x.DerivedObservationRecordId, x.Ordinal });
                    table.CheckConstraint("CK_EnvironmentalObservationLineage_Ordinal", "[Ordinal] >= 0");
                    table.ForeignKey(
                        name: "FK_EnvironmentalObservationLineage_EnvironmentalObservations_DerivedObservationRecordId",
                        column: x => x.DerivedObservationRecordId,
                        principalTable: "EnvironmentalObservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EnvironmentalObservationLineage_EnvironmentalObservations_SourceObservationRecordId",
                        column: x => x.SourceObservationRecordId,
                        principalTable: "EnvironmentalObservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralFrames",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegistrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LogicalCameraInstallationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DevicePublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    FrameId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CapturedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FirstReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RigProfileVersion = table.Column<int>(type: "int", nullable: true),
                    DeviceRigProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RigId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CaptureSequence = table.Column<long>(type: "bigint", nullable: true),
                    CycleEvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SceneProvenanceJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LocationEvidenceState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, defaultValue: "ReportedUnresolved")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralFrames", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CentralFrames_DeviceRigProfiles_DeviceRigProfileId",
                        column: x => x.DeviceRigProfileId,
                        principalTable: "DeviceRigProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralFrames_LogicalCameraInstallations_LogicalCameraInstallationId",
                        column: x => x.LogicalCameraInstallationId,
                        principalTable: "LogicalCameraInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeploymentLocationReconciliationWork",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceDeploymentLocationVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorityConcurrencyToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastErrorCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CaptureCount = table.Column<long>(type: "bigint", nullable: true),
                    DiscoveryCutoffUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DiscoveredCaptureCount = table.Column<long>(type: "bigint", nullable: false),
                    DiscoveryCursorFirstReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DiscoveryCursorCentralFrameId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CompletedCaptureCount = table.Column<long>(type: "bigint", nullable: false),
                    ScheduledArtifactCount = table.Column<long>(type: "bigint", nullable: false),
                    LastCompletedFirstReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastCompletedCentralFrameId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActiveBatchUpperFirstReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ActiveBatchUpperCentralFrameId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActiveBatchCaptureCount = table.Column<int>(type: "int", nullable: false),
                    SchedulingCentralFrameId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SchedulingCentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TraceParent = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    TraceState = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentLocationReconciliationWork", x => x.Id);
                    table.CheckConstraint("CK_DeploymentLocationReconciliationWork_ActiveBatch", "([ActiveBatchUpperFirstReceivedAtUtc] IS NULL AND [ActiveBatchUpperCentralFrameId] IS NULL AND [ActiveBatchCaptureCount] = 0 AND [SchedulingCentralFrameId] IS NULL AND [SchedulingCentralArtifactId] IS NULL) OR ([ActiveBatchUpperFirstReceivedAtUtc] IS NOT NULL AND [ActiveBatchUpperCentralFrameId] IS NOT NULL AND [ActiveBatchCaptureCount] > 0 AND (([SchedulingCentralFrameId] IS NULL AND [SchedulingCentralArtifactId] IS NULL) OR ([SchedulingCentralFrameId] IS NOT NULL AND [SchedulingCentralArtifactId] IS NOT NULL)))");
                    table.CheckConstraint("CK_DeploymentLocationReconciliationWork_Counts", "[AttemptCount] >= 0 AND [DiscoveredCaptureCount] >= 0 AND [CompletedCaptureCount] >= 0 AND [ScheduledArtifactCount] >= 0 AND [ActiveBatchCaptureCount] >= 0 AND ([CaptureCount] IS NULL OR ([CaptureCount] = [DiscoveredCaptureCount] AND [CompletedCaptureCount] + [ActiveBatchCaptureCount] <= [CaptureCount]))");
                    table.CheckConstraint("CK_DeploymentLocationReconciliationWork_Cursor", "([LastCompletedFirstReceivedAtUtc] IS NULL AND [LastCompletedCentralFrameId] IS NULL) OR ([LastCompletedFirstReceivedAtUtc] IS NOT NULL AND [LastCompletedCentralFrameId] IS NOT NULL)");
                    table.CheckConstraint("CK_DeploymentLocationReconciliationWork_Discovery", "([DiscoveryCutoffUtc] IS NULL AND [CaptureCount] IS NULL AND [DiscoveredCaptureCount] = 0 AND [DiscoveryCursorCentralFrameId] IS NULL) OR ([DiscoveryCutoffUtc] IS NOT NULL AND [CaptureCount] IS NULL AND [CompletedCaptureCount] = 0 AND [ActiveBatchCaptureCount] = 0 AND [LastCompletedCentralFrameId] IS NULL) OR ([DiscoveryCutoffUtc] IS NOT NULL AND [CaptureCount] IS NOT NULL)");
                    table.CheckConstraint("CK_DeploymentLocationReconciliationWork_DiscoveryCursor", "([DiscoveryCursorFirstReceivedAtUtc] IS NULL AND [DiscoveryCursorCentralFrameId] IS NULL) OR ([DiscoveryCursorFirstReceivedAtUtc] IS NOT NULL AND [DiscoveryCursorCentralFrameId] IS NOT NULL)");
                    table.CheckConstraint("CK_DeploymentLocationReconciliationWork_Lease", "([LeaseToken] IS NULL AND [LeaseOwner] IS NULL AND [LeaseExpiresAtUtc] IS NULL) OR ([LeaseToken] IS NOT NULL AND [LeaseOwner] IS NOT NULL AND [LeaseExpiresAtUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_DeploymentLocationReconciliationWork_State", "([Status] = N'Processing' AND [LeaseToken] IS NOT NULL AND [NextAttemptAtUtc] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] IN (N'Pending', N'Retry') AND [LeaseToken] IS NULL AND [NextAttemptAtUtc] IS NOT NULL AND [CompletedAtUtc] IS NULL AND ([Status] <> N'Retry' OR [LastErrorCode] IS NOT NULL)) OR ([Status] = N'Completed' AND [LeaseToken] IS NULL AND [NextAttemptAtUtc] IS NULL AND [LastErrorCode] IS NULL AND [CompletedAtUtc] IS NOT NULL AND [ActiveBatchUpperCentralFrameId] IS NULL AND [CaptureCount] IS NOT NULL AND [CompletedCaptureCount] = [CaptureCount])");
                    table.CheckConstraint("CK_DeploymentLocationReconciliationWork_Status", "[Status] IN (N'Pending', N'Processing', N'Retry', N'Completed')");
                    table.CheckConstraint("CK_DeploymentLocationReconciliationWork_Timestamps", "[UpdatedAtUtc] >= [CreatedAtUtc] AND ([StartedAtUtc] IS NULL OR [StartedAtUtc] >= [CreatedAtUtc]) AND ([CompletedAtUtc] IS NULL OR [CompletedAtUtc] >= [CreatedAtUtc])");
                    table.ForeignKey(
                        name: "FK_DeploymentLocationReconciliationWork_DeviceDeploymentLocationVersions_DeviceDeploymentLocationVersionId",
                        column: x => x.DeviceDeploymentLocationVersionId,
                        principalTable: "DeviceDeploymentLocationVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeploymentLocationResolutionAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceDeploymentLocationVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegistrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PreviousStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    NewStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentLocationResolutionAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeploymentLocationResolutionAudits_DeviceDeploymentLocationVersions_DeviceDeploymentLocationVersionId",
                        column: x => x.DeviceDeploymentLocationVersionId,
                        principalTable: "DeviceDeploymentLocationVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralFrameId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DevicePublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RecipeVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ManifestSchemaVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MediaType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ByteLength = table.Column<long>(type: "bigint", nullable: false),
                    ChecksumSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StorageReference = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Variant = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ObjectState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReconstructionState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StateReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ReconciledAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ObjectVerifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ObjectVerificationToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ObjectVerificationRequestedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ObjectVerificationRetryCount = table.Column<int>(type: "int", nullable: false),
                    ObjectVerificationRetryAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RetentionDeletionToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RetentionDeletionRequestedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RetentionDeletionCompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RecoveryGeneration = table.Column<long>(type: "bigint", nullable: false),
                    ReferenceRetryCount = table.Column<int>(type: "int", nullable: false),
                    ReferenceRetryAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralArtifacts", x => x.Id);
                    table.CheckConstraint("CK_CentralArtifacts_ObjectVerification", "([ObjectVerificationToken] IS NULL AND [ObjectVerificationRequestedAtUtc] IS NULL AND [ObjectVerificationRetryCount] = 0 AND [ObjectVerificationRetryAtUtc] IS NULL) OR ([ObjectVerificationToken] IS NOT NULL AND [ObjectVerificationRequestedAtUtc] IS NOT NULL AND [ObjectState] = N'Pending')");
                    table.CheckConstraint("CK_CentralArtifacts_RetentionDeletion", "[RetentionDeletionToken] IS NULL AND [RetentionDeletionRequestedAtUtc] IS NULL AND [RetentionDeletionCompletedAtUtc] IS NULL OR [RetentionDeletionToken] IS NOT NULL AND [RetentionDeletionRequestedAtUtc] IS NOT NULL AND [ObjectState] = 'Expired' AND ([RetentionDeletionCompletedAtUtc] IS NULL OR [RetentionDeletionCompletedAtUtc] >= [RetentionDeletionRequestedAtUtc])");
                    table.ForeignKey(
                        name: "FK_CentralArtifacts_CentralFrames_CentralFrameId",
                        column: x => x.CentralFrameId,
                        principalTable: "CentralFrames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CentralCaptureControls",
                columns: table => new
                {
                    CentralFrameId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedExposureTicks = table.Column<long>(type: "bigint", nullable: false),
                    EffectiveExposureTicks = table.Column<long>(type: "bigint", nullable: false),
                    RequestedGain = table.Column<double>(type: "float", nullable: false),
                    EffectiveGain = table.Column<double>(type: "float", nullable: false),
                    RequestedOffset = table.Column<double>(type: "float", nullable: true),
                    EffectiveOffset = table.Column<double>(type: "float", nullable: true),
                    TemperatureSetpointC = table.Column<double>(type: "float", nullable: true),
                    EffectiveTemperatureC = table.Column<double>(type: "float", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralCaptureControls", x => x.CentralFrameId);
                    table.ForeignKey(
                        name: "FK_CentralCaptureControls_CentralFrames_CentralFrameId",
                        column: x => x.CentralFrameId,
                        principalTable: "CentralFrames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CentralCaptureLocations",
                columns: table => new
                {
                    CentralFrameId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceDeploymentLocationVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LocationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    HorizontalAccuracyMeters = table.Column<double>(type: "float", nullable: true),
                    EffectiveFromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EffectiveUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralCaptureLocations", x => x.CentralFrameId);
                    table.CheckConstraint("CK_CentralCaptureLocations_Accuracy", "[HorizontalAccuracyMeters] IS NULL OR [HorizontalAccuracyMeters] >= 0");
                    table.CheckConstraint("CK_CentralCaptureLocations_Interval", "[EffectiveUntilUtc] IS NULL OR [EffectiveUntilUtc] > [EffectiveFromUtc]");
                    table.CheckConstraint("CK_CentralCaptureLocations_Version", "[Version] >= 1");
                    table.ForeignKey(
                        name: "FK_CentralCaptureLocations_CentralFrames_CentralFrameId",
                        column: x => x.CentralFrameId,
                        principalTable: "CentralFrames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CentralCaptureLocations_DeviceDeploymentLocationVersions_DeviceDeploymentLocationVersionId",
                        column: x => x.DeviceDeploymentLocationVersionId,
                        principalTable: "DeviceDeploymentLocationVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralCaptureProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralFrameId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DeviceRigProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralCaptureProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CentralCaptureProfiles_CentralFrames_CentralFrameId",
                        column: x => x.CentralFrameId,
                        principalTable: "CentralFrames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CentralCaptureProfiles_DeviceRigProfiles_DeviceRigProfileId",
                        column: x => x.DeviceRigProfileId,
                        principalTable: "DeviceRigProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralCaptureTimings",
                columns: table => new
                {
                    CentralFrameId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedStartUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExposureStartedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExposureEndedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReadoutCompletedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DurableIngressUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SetpointAppliedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralCaptureTimings", x => x.CentralFrameId);
                    table.ForeignKey(
                        name: "FK_CentralCaptureTimings_CentralFrames_CentralFrameId",
                        column: x => x.CentralFrameId,
                        principalTable: "CentralFrames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeploymentLocationReconciliationCaptures",
                columns: table => new
                {
                    DeploymentLocationReconciliationWorkId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorityConcurrencyToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralFrameId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FirstReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentLocationReconciliationCaptures", x => new { x.DeploymentLocationReconciliationWorkId, x.AuthorityConcurrencyToken, x.CentralFrameId });
                    table.ForeignKey(
                        name: "FK_DeploymentLocationReconciliationCaptures_CentralFrames_CentralFrameId",
                        column: x => x.CentralFrameId,
                        principalTable: "CentralFrames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DeploymentLocationReconciliationCaptures_DeploymentLocationReconciliationWork_DeploymentLocationReconciliationWorkId",
                        column: x => x.DeploymentLocationReconciliationWorkId,
                        principalTable: "DeploymentLocationReconciliationWork",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TokenSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
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
                name: "CentralArtifactIngestIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ManifestSchemaVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralArtifactIngestIdentities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CentralArtifactIngestIdentities_CentralArtifacts_CentralArtifactId",
                        column: x => x.CentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CentralArtifactLayouts",
                columns: table => new
                {
                    CentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Width = table.Column<int>(type: "int", nullable: false),
                    Height = table.Column<int>(type: "int", nullable: false),
                    StrideBytes = table.Column<int>(type: "int", nullable: false),
                    PixelFormat = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ByteOrder = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SampleDepthBits = table.Column<int>(type: "int", nullable: false),
                    ContainerDepthBits = table.Column<int>(type: "int", nullable: false),
                    Packing = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CfaPattern = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    BlackLevel = table.Column<double>(type: "float", nullable: true),
                    WhiteLevel = table.Column<double>(type: "float", nullable: true),
                    StoredCodeTransform = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LevelCodeSpace = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    NativeWidth = table.Column<int>(type: "int", nullable: true),
                    NativeHeight = table.Column<int>(type: "int", nullable: true),
                    RoiX = table.Column<int>(type: "int", nullable: true),
                    RoiY = table.Column<int>(type: "int", nullable: true),
                    RoiWidth = table.Column<int>(type: "int", nullable: true),
                    RoiHeight = table.Column<int>(type: "int", nullable: true),
                    BinX = table.Column<int>(type: "int", nullable: true),
                    BinY = table.Column<int>(type: "int", nullable: true),
                    BinningAlgorithm = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CfaOriginX = table.Column<int>(type: "int", nullable: true),
                    CfaOriginY = table.Column<int>(type: "int", nullable: true),
                    ByteLength = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralArtifactLayouts", x => x.CentralArtifactId);
                    table.ForeignKey(
                        name: "FK_CentralArtifactLayouts_CentralArtifacts_CentralArtifactId",
                        column: x => x.CentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CentralArtifactRecipes",
                columns: table => new
                {
                    CentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SemanticVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ImplementationVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OptionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OptionsSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralArtifactRecipes", x => x.CentralArtifactId);
                    table.ForeignKey(
                        name: "FK_CentralArtifactRecipes_CentralArtifacts_CentralArtifactId",
                        column: x => x.CentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CentralArtifactSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    SourceArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpectedRole = table.Column<int>(type: "int", nullable: true),
                    ExpectedVariant = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ExpectedRecipeIdentitySha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ExpectedProductIdentitySha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ExpectedMediaType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ExpectedWidthPixels = table.Column<int>(type: "int", nullable: true),
                    ExpectedHeightPixels = table.Column<int>(type: "int", nullable: true),
                    ExpectedLayoutIdentitySha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ExpectedCoordinateIdentitySha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ResolvedCentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralArtifactSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CentralArtifactSources_CentralArtifacts_CentralArtifactId",
                        column: x => x.CentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CentralArtifactSources_CentralArtifacts_ResolvedCentralArtifactId",
                        column: x => x.ResolvedCentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralClearReferenceDesignations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegistrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RigId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralClearReferenceDesignations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CentralClearReferenceDesignations_CentralArtifacts_CentralArtifactId",
                        column: x => x.CentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralClearReferenceDesignations_DeviceRegistrations_RegistrationId",
                        column: x => x.RegistrationId,
                        principalTable: "DeviceRegistrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralProcessingGraphRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Revision = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DefinitionIdentitySha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    PortablePlanIdentitySha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    EdgePlanIdentitySha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    CentralPlanIdentitySha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PublishedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    RetiredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RetiredByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    RetirementReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralProcessingGraphRevisions", x => x.Id);
                    table.CheckConstraint("CK_CentralProcessingGraphRevisions_Lifecycle", "([PublishedAtUtc] IS NULL AND [PublishedByUserId] IS NULL AND [RetiredAtUtc] IS NULL AND [RetiredByUserId] IS NULL AND [RetirementReasonCode] IS NULL) OR ([PublishedAtUtc] IS NOT NULL AND [PublishedByUserId] IS NOT NULL AND (([RetiredAtUtc] IS NULL AND [RetiredByUserId] IS NULL AND [RetirementReasonCode] IS NULL) OR ([RetiredAtUtc] >= [PublishedAtUtc] AND [RetiredByUserId] IS NOT NULL AND [RetirementReasonCode] IS NOT NULL)))");
                });

            migrationBuilder.CreateTable(
                name: "CentralProcessingGraphExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExecutionClass = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    RequestIdentitySha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DefinitionIdentitySha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    FrozenDefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CentralPlanIdentitySha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    FrozenCentralPlanJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExpectedSourceCount = table.Column<int>(type: "int", nullable: false),
                    ExpectedNodeCount = table.Column<int>(type: "int", nullable: false),
                    ExpectedDependencyCount = table.Column<int>(type: "int", nullable: false),
                    ExpectedOutputCount = table.Column<int>(type: "int", nullable: false),
                    ExpandedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ObservatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LogicalCameraId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LogicalCameraInstallationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstallationPublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnchorSourceCentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnchorSourceArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnchorSourceChecksumSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    Trigger = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    PredecessorExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancellationRequestedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralProcessingGraphExecutions", x => x.Id);
                    table.CheckConstraint("CK_CentralProcessingGraphExecutions_Class", "[ExecutionClass] IN (N'Live', N'Replay')");
                    table.CheckConstraint("CK_CentralProcessingGraphExecutions_Expansion", "([ExpandedAtUtc] IS NULL AND [Status] = N'Pending') OR ([ExpandedAtUtc] IS NOT NULL AND [ExpandedAtUtc] >= [CreatedAtUtc] AND [UpdatedAtUtc] >= [ExpandedAtUtc])");
                    table.CheckConstraint("CK_CentralProcessingGraphExecutions_ExpectedCounts", "[ExpectedSourceCount] >= 0 AND [ExpectedSourceCount] <= 64 AND [ExpectedNodeCount] >= 0 AND [ExpectedNodeCount] <= 1024 AND [ExpectedDependencyCount] >= 0 AND [ExpectedDependencyCount] <= 65536 AND [ExpectedOutputCount] >= 0 AND [ExpectedOutputCount] <= 65536");
                    table.CheckConstraint("CK_CentralProcessingGraphExecutions_Predecessor", "[PredecessorExecutionId] IS NULL OR [PredecessorExecutionId] <> [Id]");
                    table.CheckConstraint("CK_CentralProcessingGraphExecutions_Provenance", "LEN(LTRIM(RTRIM(REPLACE(REPLACE(REPLACE([ActorId], CHAR(9), N''), CHAR(10), N''), CHAR(13), N'')))) > 0 AND LEN(LTRIM(RTRIM(REPLACE(REPLACE(REPLACE([IdempotencyKey], CHAR(9), N''), CHAR(10), N''), CHAR(13), N'')))) > 0 AND LEN(LTRIM(RTRIM(REPLACE(REPLACE(REPLACE([ReasonCode], CHAR(9), N''), CHAR(10), N''), CHAR(13), N'')))) > 0 AND (([ExecutionClass] = N'Live' AND [Trigger] = N'Ingest' AND [AssignmentId] IS NOT NULL) OR ([ExecutionClass] = N'Replay' AND [Trigger] IN (N'Replay', N'Reprocess') AND [AssignmentId] IS NULL))");
                    table.CheckConstraint("CK_CentralProcessingGraphExecutions_Status", "[Status] IN (N'Pending', N'Running', N'Completed', N'CompletedWithOptionalFailures', N'Failed', N'CancelRequested', N'Canceled', N'Superseded')");
                    table.CheckConstraint("CK_CentralProcessingGraphExecutions_StatusTimestamps", "([Status] = N'Pending' AND [StartedAtUtc] IS NULL AND [CancellationRequestedAtUtc] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = N'Running' AND [StartedAtUtc] IS NOT NULL AND [CancellationRequestedAtUtc] IS NULL AND [CompletedAtUtc] IS NULL) OR ([Status] = N'CancelRequested' AND [CancellationRequestedAtUtc] IS NOT NULL AND [CompletedAtUtc] IS NULL) OR ([Status] IN (N'Completed', N'CompletedWithOptionalFailures') AND [StartedAtUtc] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL) OR ([Status] = N'Failed' AND [CompletedAtUtc] IS NOT NULL) OR ([Status] = N'Canceled' AND [CancellationRequestedAtUtc] IS NOT NULL AND [CompletedAtUtc] IS NOT NULL) OR ([Status] = N'Superseded' AND [CompletedAtUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_CentralProcessingGraphExecutions_Timestamps", "[UpdatedAtUtc] >= [CreatedAtUtc] AND ([StartedAtUtc] IS NULL OR [StartedAtUtc] >= [CreatedAtUtc]) AND ([CancellationRequestedAtUtc] IS NULL OR [CancellationRequestedAtUtc] >= [CreatedAtUtc]) AND ([CompletedAtUtc] IS NULL OR [CompletedAtUtc] >= [CreatedAtUtc])");
                    table.CheckConstraint("CK_CentralProcessingGraphExecutions_Trigger", "[Trigger] IN (N'Ingest', N'Replay', N'Reprocess')");
                    table.ForeignKey(
                        name: "FK_CentralProcessingGraphExecutions_CentralArtifacts_AnchorSourceCentralArtifactId",
                        column: x => x.AnchorSourceCentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralProcessingGraphExecutions_CentralProcessingGraphExecutions_PredecessorExecutionId",
                        column: x => x.PredecessorExecutionId,
                        principalTable: "CentralProcessingGraphExecutions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CentralProcessingGraphExecutions_CentralProcessingGraphRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "CentralProcessingGraphRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralProcessingGraphExecutions_LogicalCameraInstallations_LogicalCameraInstallationId",
                        column: x => x.LogicalCameraInstallationId,
                        principalTable: "LogicalCameraInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralProcessingGraphExecutions_LogicalCameras_LogicalCameraId",
                        column: x => x.LogicalCameraId,
                        principalTable: "LogicalCameras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralProcessingGraphExecutions_Observatories_ObservatoryId",
                        column: x => x.ObservatoryId,
                        principalTable: "Observatories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralDerivativeJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceCentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetRole = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TargetRecipeVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TargetVariant = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RecipeName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RecipeOptionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    InputSelectorJson = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    RequestedRecipeIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ExpectedRecipeIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    RequestIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    TraceParent = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true),
                    TraceState = table.Column<string>(type: "varchar(512)", unicode: false, maxLength: 512, nullable: true),
                    GraphExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GraphNodeId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true, collation: "Latin1_General_100_BIN2"),
                    GraphNodeOrdinal = table.Column<int>(type: "int", nullable: true),
                    SharedNodePlanIdentitySha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    FrozenNodePlanJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    GraphFailurePolicy = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    WaitKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ResolutionDeadlineUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolutionStartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolutionCompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    MissingInputOutcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    MinimumInputCount = table.Column<int>(type: "int", nullable: true),
                    StateReasonCode = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    InputSetIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
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
                    CancellationRequestedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancellationRequestedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SupersededByJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PredecessorJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RetainedResultCentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralDerivativeJobs", x => x.Id);
                    table.CheckConstraint("CK_CentralDerivativeJobs_AttemptCount", "[AttemptCount] >= 0 AND [AttemptCount] <= [MaxAttempts]");
                    table.CheckConstraint("CK_CentralDerivativeJobs_GraphFailurePolicy", "[GraphFailurePolicy] IS NULL OR [GraphFailurePolicy] IN (N'Required', N'Optional')");
                    table.CheckConstraint("CK_CentralDerivativeJobs_GraphOwnership", "([GraphExecutionId] IS NULL AND [GraphNodeId] IS NULL AND [GraphNodeOrdinal] IS NULL AND [SharedNodePlanIdentitySha256] IS NULL AND [FrozenNodePlanJson] IS NULL AND [GraphFailurePolicy] IS NULL) OR ([GraphExecutionId] IS NOT NULL AND [GraphNodeId] IS NOT NULL AND [GraphNodeOrdinal] >= 0 AND [SharedNodePlanIdentitySha256] IS NOT NULL AND [FrozenNodePlanJson] IS NOT NULL AND [GraphFailurePolicy] IS NOT NULL)");
                    table.CheckConstraint("CK_CentralDerivativeJobs_MaxAttempts", "[MaxAttempts] > 0");
                    table.CheckConstraint("CK_CentralDerivativeJobs_MinimumInputCount", "[MinimumInputCount] IS NULL OR [MinimumInputCount] >= 0");
                    table.CheckConstraint("CK_CentralDerivativeJobs_WaitKind", "[WaitKind] IS NULL OR [WaitKind] IN (N'Dependencies', N'Window')");
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobs_CentralArtifacts_ResultCentralArtifactId",
                        column: x => x.ResultCentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobs_CentralArtifacts_RetainedResultCentralArtifactId",
                        column: x => x.RetainedResultCentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobs_CentralArtifacts_SourceCentralArtifactId",
                        column: x => x.SourceCentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobs_CentralProcessingGraphExecutions_GraphExecutionId",
                        column: x => x.GraphExecutionId,
                        principalTable: "CentralProcessingGraphExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobs_CentralDerivativeJobs_PredecessorJobId",
                        column: x => x.PredecessorJobId,
                        principalTable: "CentralDerivativeJobs",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobs_CentralDerivativeJobs_SupersededByJobId",
                        column: x => x.SupersededByJobId,
                        principalTable: "CentralDerivativeJobs",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "CentralProcessingGraphExecutionSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    SourceId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OutputOrdinal = table.Column<int>(type: "int", nullable: false),
                    CentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactChecksumSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    ArtifactByteLength = table.Column<long>(type: "bigint", nullable: false),
                    SelectionEvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SelectionEvidenceSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    SelectedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralProcessingGraphExecutionSources", x => x.Id);
                    table.CheckConstraint("CK_CentralProcessingGraphExecutionSources_ByteLength", "[ArtifactByteLength] >= 0");
                    table.CheckConstraint("CK_CentralProcessingGraphExecutionSources_Ordinal", "[Ordinal] >= 0");
                    table.CheckConstraint("CK_CentralProcessingGraphExecutionSources_OutputOrdinal", "[OutputOrdinal] >= 0");
                    table.ForeignKey(
                        name: "FK_CentralProcessingGraphExecutionSources_CentralArtifacts_CentralArtifactId",
                        column: x => x.CentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralProcessingGraphExecutionSources_CentralProcessingGraphExecutions_ExecutionId",
                        column: x => x.ExecutionId,
                        principalTable: "CentralProcessingGraphExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralDerivativeJobOutputs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralDerivativeJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Variant = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProductKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ContractJson = table.Column<string>(type: "varchar(max)", unicode: false, nullable: false),
                    ContractIdentitySha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    ResultCentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResultOutputIdentitySha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    BoundAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralDerivativeJobOutputs", x => x.Id);
                    table.UniqueConstraint("AK_CentralDerivativeJobOutputs_CentralDerivativeJobId_Ordinal", x => new { x.CentralDerivativeJobId, x.Ordinal });
                    table.CheckConstraint("CK_CentralDerivativeJobOutputs_Binding", "([ResultCentralArtifactId] IS NULL AND [ResultOutputIdentitySha256] IS NULL AND [BoundAtUtc] IS NULL) OR ([ResultCentralArtifactId] IS NOT NULL AND [ResultOutputIdentitySha256] IS NOT NULL AND [BoundAtUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_CentralDerivativeJobOutputs_ContractIdentity", "ISJSON([ContractJson]) = 1 AND [ContractJson] NOT LIKE '%[^ -~]%' COLLATE Latin1_General_100_BIN2 AND [ContractIdentitySha256] = CONVERT(varchar(64), HASHBYTES('SHA2_256', [ContractJson]), 2)");
                    table.CheckConstraint("CK_CentralDerivativeJobOutputs_Ordinal", "[Ordinal] >= 0");
                    table.CheckConstraint("CK_CentralDerivativeJobOutputs_ProductKind", "[ProductKind] IN (N'PixelData', N'Metadata')");
                    table.CheckConstraint("CK_CentralDerivativeJobOutputs_Role", "[Role] IN (N'Raw', N'Calibrated', N'Combined', N'Preview', N'AnnotatedPreview', N'Metadata')");
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobOutputs_CentralArtifacts_ResultCentralArtifactId",
                        column: x => x.ResultCentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobOutputs_CentralDerivativeJobs_CentralDerivativeJobId",
                        column: x => x.CentralDerivativeJobId,
                        principalTable: "CentralDerivativeJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralDerivativeJobDependencies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConsumerJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Required = table.Column<bool>(type: "bit", nullable: false),
                    ProducerJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProducerSourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProducerOutputOrdinal = table.Column<int>(type: "int", nullable: true),
                    ConsumerInputOrdinal = table.Column<int>(type: "int", nullable: true),
                    ConsumerBindingName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true, collation: "Latin1_General_100_BIN2"),
                    ConsumerBindingKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralDerivativeJobDependencies", x => x.Id);
                    table.UniqueConstraint("AK_CentralDerivativeJobDependencies_ConsumerJobId_Id", x => new { x.ConsumerJobId, x.Id });
                    table.CheckConstraint("CK_CentralDerivativeJobDependencies_Binding", "([Kind] IN (N'Outcome', N'Ordering') AND [ProducerOutputOrdinal] IS NULL AND [ConsumerInputOrdinal] IS NULL AND [ConsumerBindingName] IS NULL AND [ConsumerBindingKind] IS NULL) OR ([Kind] IN (N'Artifact', N'CanonicalJson', N'Annotation') AND [ProducerOutputOrdinal] >= 0 AND [ConsumerInputOrdinal] >= 0 AND [ConsumerBindingName] IS NOT NULL AND [ConsumerBindingKind] IS NOT NULL)");
                    table.CheckConstraint("CK_CentralDerivativeJobDependencies_BindingKind", "[ConsumerBindingKind] IS NULL OR [ConsumerBindingKind] IN (N'PrimaryArtifact', N'AuxiliaryArtifact', N'CanonicalJson', N'Annotation')");
                    table.CheckConstraint("CK_CentralDerivativeJobDependencies_CompatibleBinding", "[Kind] IN (N'Outcome', N'Ordering') OR ([Kind] = N'Artifact' AND [ConsumerBindingKind] IN (N'PrimaryArtifact', N'AuxiliaryArtifact')) OR ([Kind] = N'CanonicalJson' AND [ConsumerBindingKind] = N'CanonicalJson') OR ([Kind] = N'Annotation' AND [ConsumerBindingKind] = N'Annotation')");
                    table.CheckConstraint("CK_CentralDerivativeJobDependencies_Kind", "[Kind] IN (N'Artifact', N'CanonicalJson', N'Annotation', N'Outcome', N'Ordering')");
                    table.CheckConstraint("CK_CentralDerivativeJobDependencies_Ordinal", "[Ordinal] >= 0");
                    table.CheckConstraint("CK_CentralDerivativeJobDependencies_Producer", "([ProducerJobId] IS NOT NULL AND [ProducerSourceId] IS NULL) OR ([ProducerJobId] IS NULL AND [ProducerSourceId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobDependencies_CentralDerivativeJobOutputs_ProducerJobId_ProducerOutputOrdinal",
                        columns: x => new { x.ProducerJobId, x.ProducerOutputOrdinal },
                        principalTable: "CentralDerivativeJobOutputs",
                        principalColumns: new[] { "CentralDerivativeJobId", "Ordinal" });
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobDependencies_CentralDerivativeJobs_ConsumerJobId",
                        column: x => x.ConsumerJobId,
                        principalTable: "CentralDerivativeJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobDependencies_CentralDerivativeJobs_ProducerJobId",
                        column: x => x.ProducerJobId,
                        principalTable: "CentralDerivativeJobs",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobDependencies_CentralProcessingGraphExecutions_ExecutionId",
                        column: x => x.ExecutionId,
                        principalTable: "CentralProcessingGraphExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobDependencies_CentralProcessingGraphExecutionSources_ProducerSourceId",
                        column: x => x.ProducerSourceId,
                        principalTable: "CentralProcessingGraphExecutionSources",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "CentralStructuredProcessingProducts",
                columns: table => new
                {
                    CentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OutputIdentitySha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProductKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ProductSchemaVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ContentIdentitySha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AlgorithmsJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CompatibilityJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    DescriptorJson = table.Column<string>(type: "nvarchar(max)", maxLength: 65536, nullable: false),
                    TotalIntegrationTicks = table.Column<long>(type: "bigint", nullable: false),
                    SourceIdentitySha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PresentationWidthPixels = table.Column<int>(type: "int", nullable: true),
                    PresentationHeightPixels = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralStructuredProcessingProducts", x => x.CentralArtifactId);
                    table.CheckConstraint("CK_CentralStructuredProcessingProducts_DescriptorJson_Length", "LEN([DescriptorJson]) <= 65536");
                    table.ForeignKey(
                        name: "FK_CentralStructuredProcessingProducts_CentralArtifacts_CentralArtifactId",
                        column: x => x.CentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                    table.UniqueConstraint("AK_CentralTransientObservationSources_ObservationId_EvidenceId_CentralArtifactId_ArtifactId_ArtifactChecksumSha256_ObservationS~", x => new { x.ObservationId, x.EvidenceId, x.CentralArtifactId, x.ArtifactId, x.ArtifactChecksumSha256, x.ObservationStartedUtc, x.ObservationEndedUtc });
                    table.CheckConstraint("CK_CentralTransientObservationSources_ObservedInterval", "[ObservationStartedUtc] <= [ObservationEndedUtc]");
                    table.ForeignKey(
                        name: "FK_CentralTransientObservationSources_CentralArtifacts_CentralArtifactId",
                        column: x => x.CentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralArtifactProcessingEvidence",
                columns: table => new
                {
                    CentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DevicePublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OutputIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    RequestedRecipeIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    RecipeIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    RecipeOperationKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    GraphProductContractIdentitySha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    ProductKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ProductSchemaVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ProductMediaType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
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

            migrationBuilder.CreateTable(
                name: "CentralDerivativeJobInputRequirements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralDerivativeJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    BindingName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    GraphDependencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GraphInputOrdinal = table.Column<int>(type: "int", nullable: true),
                    GraphInputBindingKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    SourceKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SequenceOffset = table.Column<int>(type: "int", nullable: true),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false),
                    SelectorJson = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    CompatibilityMode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ExpectedAgentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ExpectedRigId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ExpectedCaptureSequence = table.Column<long>(type: "bigint", nullable: true),
                    ExpectedCentralArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResolutionState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ResolutionReasonCode = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralDerivativeJobInputRequirements", x => x.Id);
                    table.UniqueConstraint("AK_CentralDerivativeJobInputRequirements_CentralDerivativeJobId_Id", x => new { x.CentralDerivativeJobId, x.Id });
                    table.CheckConstraint("CK_CentralDerivativeJobInputRequirements_GraphBinding", "([GraphDependencyId] IS NULL AND [GraphInputOrdinal] IS NULL AND [GraphInputBindingKind] IS NULL) OR ([GraphDependencyId] IS NOT NULL AND [GraphInputOrdinal] >= 0 AND [GraphInputBindingKind] IS NOT NULL)");
                    table.CheckConstraint("CK_CentralDerivativeJobInputRequirements_GraphInputBindingKind", "[GraphInputBindingKind] IS NULL OR [GraphInputBindingKind] IN (N'PrimaryArtifact', N'AuxiliaryArtifact', N'CanonicalJson', N'Annotation')");
                    table.CheckConstraint("CK_CentralDerivativeJobInputRequirements_GraphResolution", "[GraphDependencyId] IS NULL OR ([ResolutionState] = N'Waiting' AND [ExpectedCentralArtifactId] IS NULL AND [ResolvedAtUtc] IS NULL) OR ([ResolutionState] = N'Resolved' AND [ResolvedAtUtc] IS NOT NULL AND (([SourceKind] = N'Artifact' AND [ExpectedCentralArtifactId] IS NOT NULL) OR ([SourceKind] <> N'Artifact' AND [ExpectedCentralArtifactId] IS NULL))) OR ([ResolutionState] IN (N'Missing', N'Incompatible') AND [ExpectedCentralArtifactId] IS NULL AND [ResolvedAtUtc] IS NOT NULL AND LEN([ResolutionReasonCode]) > 0)");
                    table.CheckConstraint("CK_CentralDerivativeJobInputRequirements_Ordinal", "[Ordinal] >= 0");
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobInputRequirements_CentralArtifacts_ExpectedCentralArtifactId",
                        column: x => x.ExpectedCentralArtifactId,
                        principalTable: "CentralArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobInputRequirements_CentralDerivativeJobDependencies_CentralDerivativeJobId_GraphDependencyId",
                        columns: x => new { x.CentralDerivativeJobId, x.GraphDependencyId },
                        principalTable: "CentralDerivativeJobDependencies",
                        principalColumns: new[] { "ConsumerJobId", "Id" });
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobInputRequirements_CentralDerivativeJobs_CentralDerivativeJobId",
                        column: x => x.CentralDerivativeJobId,
                        principalTable: "CentralDerivativeJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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

            migrationBuilder.CreateTable(
                name: "CentralTransientValidationJobs",
                columns: table => new
                {
                    CentralDerivativeJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SubmissionSchemaVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SubmissionIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SubmittedCandidateJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExecutionOptionsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExecutionOptionsIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CommittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    OutcomeState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    OutcomeReasonCode = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2"),
                    OutcomeEvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OutcomeEvidenceIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    OutcomeRecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ProvisionalCentralDerivativeJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientValidationJobs", x => x.CentralDerivativeJobId);
                    table.CheckConstraint("CK_CentralTransientValidationJobs_CommitOutcome", "[CommittedAtUtc] IS NULL OR [OutcomeRecordedAtUtc] IS NOT NULL");
                    table.CheckConstraint("CK_CentralTransientValidationJobs_ExecutionOptions", "([ExecutionOptionsJson] IS NULL AND [ExecutionOptionsIdentitySha256] IS NULL) OR ([ExecutionOptionsJson] IS NOT NULL AND [ExecutionOptionsIdentitySha256] IS NOT NULL)");
                    table.CheckConstraint("CK_CentralTransientValidationJobs_Outcome", "([OutcomeRecordedAtUtc] IS NULL AND [OutcomeState] IS NULL AND [OutcomeReasonCode] IS NULL AND [OutcomeEvidenceJson] IS NULL AND [OutcomeEvidenceIdentitySha256] IS NULL) OR ([OutcomeRecordedAtUtc] IS NOT NULL AND [OutcomeState] IS NOT NULL AND [OutcomeReasonCode] IS NOT NULL AND [OutcomeEvidenceJson] IS NOT NULL AND [OutcomeEvidenceIdentitySha256] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_CentralTransientValidationJobs_CentralDerivativeJobs_CentralDerivativeJobId",
                        column: x => x.CentralDerivativeJobId,
                        principalTable: "CentralDerivativeJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralTransientValidationJobs_CentralTransientValidationJobs_ProvisionalCentralDerivativeJobId",
                        column: x => x.ProvisionalCentralDerivativeJobId,
                        principalTable: "CentralTransientValidationJobs",
                        principalColumn: "CentralDerivativeJobId");
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
                name: "CentralDerivativeJobCanonicalInputs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralDerivativeJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralDerivativeJobInputRequirementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    SchemaVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    CanonicalJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ByteLength = table.Column<int>(type: "int", nullable: false),
                    EnvironmentalObservationRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SelectedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralDerivativeJobCanonicalInputs", x => x.Id);
                    table.CheckConstraint("CK_CentralDerivativeJobCanonicalInputs_ByteLength", "[ByteLength] > 0");
                    table.CheckConstraint("CK_CentralDerivativeJobCanonicalInputs_Ordinal", "[Ordinal] >= 0");
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobCanonicalInputs_CentralDerivativeJobInputRequirements_CentralDerivativeJobId_CentralDerivativeJobInputRe~",
                        columns: x => new { x.CentralDerivativeJobId, x.CentralDerivativeJobInputRequirementId },
                        principalTable: "CentralDerivativeJobInputRequirements",
                        principalColumns: new[] { "CentralDerivativeJobId", "Id" });
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobCanonicalInputs_CentralDerivativeJobs_CentralDerivativeJobId",
                        column: x => x.CentralDerivativeJobId,
                        principalTable: "CentralDerivativeJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CentralDerivativeJobCanonicalInputs_EnvironmentalObservations_EnvironmentalObservationRecordId",
                        column: x => x.EnvironmentalObservationRecordId,
                        principalTable: "EnvironmentalObservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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
                    ObjectState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StateReasonCode = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ObjectVerifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CommittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    StorageReferenceSha256 = table.Column<byte[]>(type: "binary(32)", nullable: true, computedColumnSql: "CONVERT(binary(32), HASHBYTES('SHA2_256', [StorageReference]))", stored: true)
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
                    table.UniqueConstraint("AK_CentralTransientObservationBackgrounds_ObservationId_Ordinal_CentralArtifactId_ArtifactId_ArtifactChecksumSha256", x => new { x.ObservationId, x.Ordinal, x.CentralArtifactId, x.ArtifactId, x.ArtifactChecksumSha256 });
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
                    AgentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SubmittedEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AdoptedEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssociationIdentitySha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    CandidateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralTransientEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PersistedEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PersistedEventVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PersistedObservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PersistedAssessmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralTransientValidationIdentitySlots", x => x.Id);
                    table.CheckConstraint("CK_CentralTransientValidationIdentitySlots_Association", "([AdoptedEventId] IS NULL AND [AssociationIdentitySha256] IS NULL) OR ([AdoptedEventId] IS NOT NULL AND [AssociationIdentitySha256] IS NOT NULL)");
                    table.CheckConstraint("CK_CentralTransientValidationIdentitySlots_Ordinal", "[Ordinal] >= 0");
                    table.CheckConstraint("CK_CentralTransientValidationIdentitySlots_State", "([State] IN ('Reserved', 'Unused') AND [CentralTransientEventId] IS NULL AND [PersistedEventId] IS NULL AND [PersistedEventVersionId] IS NULL AND [PersistedObservationId] IS NULL AND [PersistedAssessmentId] IS NULL) OR ([State] = 'Committed' AND [CentralTransientEventId] IS NOT NULL AND [PersistedEventId] = COALESCE([AdoptedEventId], [SubmittedEventId]) AND [PersistedEventVersionId] IS NOT NULL AND [PersistedObservationId] = [ObservationId] AND [PersistedAssessmentId] = [AssessmentId])");
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
                        name: "FK_CentralTransientValidationIdentitySlots_CentralTransientEvents_CentralTransientEventId_PersistedEventId",
                        columns: x => new { x.CentralTransientEventId, x.PersistedEventId },
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

            migrationBuilder.CreateTable(
                name: "CentralProcessingGraphAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetHost = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ObservatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LogicalCameraId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EffectiveFromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EffectiveUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralProcessingGraphAssignments", x => x.Id);
                    table.CheckConstraint("CK_CentralProcessingGraphAssignments_EffectiveWindow", "[EffectiveUntilUtc] IS NULL OR [EffectiveUntilUtc] > [EffectiveFromUtc]");
                    table.CheckConstraint("CK_CentralProcessingGraphAssignments_Scope", "([Scope] = N'GlobalDefault' AND [ObservatoryId] IS NULL AND [LogicalCameraId] IS NULL) OR ([Scope] = N'Observatory' AND [ObservatoryId] IS NOT NULL AND [LogicalCameraId] IS NULL) OR ([Scope] = N'LogicalCamera' AND [ObservatoryId] IS NOT NULL AND [LogicalCameraId] IS NOT NULL)");
                    table.CheckConstraint("CK_CentralProcessingGraphAssignments_TargetHost", "[TargetHost] IN (N'Edge', N'Central')");
                    table.ForeignKey(
                        name: "FK_CentralProcessingGraphAssignments_CentralProcessingGraphRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "CentralProcessingGraphRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralProcessingGraphAssignments_LogicalCameras_LogicalCameraId",
                        column: x => x.LogicalCameraId,
                        principalTable: "LogicalCameras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralProcessingGraphAssignments_Observatories_ObservatoryId",
                        column: x => x.ObservatoryId,
                        principalTable: "Observatories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddForeignKey(
                name: "FK_CentralProcessingGraphExecutions_CentralProcessingGraphAssignments_AssignmentId",
                table: "CentralProcessingGraphExecutions",
                column: "AssignmentId",
                principalTable: "CentralProcessingGraphAssignments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.CreateTable(
                name: "CentralProcessingGraphDeliveryProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegistrationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LogicalCameraInstallationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstallationPublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpectedActiveLocalRevisionId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    CapabilitySnapshotSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    IssuedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralProcessingGraphDeliveryProposals", x => x.Id);
                    table.CheckConstraint("CK_CentralProcessingGraphDeliveryProposals_Expiry", "[ExpiresAtUtc] > [IssuedAtUtc]");
                    table.ForeignKey(
                        name: "FK_CentralProcessingGraphDeliveryProposals_CentralProcessingGraphAssignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "CentralProcessingGraphAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralProcessingGraphDeliveryProposals_CentralProcessingGraphRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "CentralProcessingGraphRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralProcessingGraphDeliveryProposals_DeviceRegistrations_RegistrationId",
                        column: x => x.RegistrationId,
                        principalTable: "DeviceRegistrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CentralProcessingGraphDeliveryProposals_LogicalCameraInstallations_LogicalCameraInstallationId",
                        column: x => x.LogicalCameraInstallationId,
                        principalTable: "LogicalCameraInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CentralProcessingGraphDeliveryFacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    LocalRevisionId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    DefinitionIdentitySha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    SharedPlanIdentitySha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    LocalPlanIdentitySha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    ReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralProcessingGraphDeliveryFacts", x => x.Id);
                    table.CheckConstraint("CK_CentralProcessingGraphDeliveryFacts_Kind", "[Kind] IN (N'Retrieved', N'Accepted', N'Rejected', N'Activated', N'RolledBack', N'Expired', N'Superseded')");
                    table.CheckConstraint("CK_CentralProcessingGraphDeliveryFacts_Source", "[Source] IN (N'LogicHost', N'CameraAgent')");
                    table.ForeignKey(
                        name: "FK_CentralProcessingGraphDeliveryFacts_CentralProcessingGraphDeliveryProposals_ProposalId",
                        column: x => x.ProposalId,
                        principalTable: "CentralProcessingGraphDeliveryProposals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "CentralRecoveryCheckpoints",
                columns: new[] { "Id", "FindingBytes", "FindingCount", "Generation", "InventoryStartedAtUtc", "LastCompletedAtUtc", "LastCycleAtUtc", "LastFailureAtUtc", "LastProgressAtUtc", "LeaseExpiresAtUtc", "LeaseToken", "NextInventoryAtUtc", "ObjectCursor", "ObjectPartition", "Phase", "StagingCursor", "StagingPartition" },
                values: new object[] { 1, 0L, 0L, 0L, null, null, null, null, null, null, null, new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, 0, "Idle", null, 0 });

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphExecutions_AnchorSourceCentralArtifactId",
                table: "CentralProcessingGraphExecutions",
                column: "AnchorSourceCentralArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphExecutions_AssignmentId",
                table: "CentralProcessingGraphExecutions",
                column: "AssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphExecutions_ExpandedAtUtc",
                table: "CentralProcessingGraphExecutions",
                column: "ExpandedAtUtc",
                filter: "[ExpandedAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphExecutions_ExecutionClass_ActorId_IdempotencyKey",
                table: "CentralProcessingGraphExecutions",
                columns: new[] { "ExecutionClass", "ActorId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphExecutions_LogicalCameraId",
                table: "CentralProcessingGraphExecutions",
                column: "LogicalCameraId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphExecutions_LogicalCameraInstallationId_CreatedAtUtc_Id",
                table: "CentralProcessingGraphExecutions",
                columns: new[] { "LogicalCameraInstallationId", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphExecutions_ObservatoryId",
                table: "CentralProcessingGraphExecutions",
                column: "ObservatoryId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphExecutions_PredecessorExecutionId",
                table: "CentralProcessingGraphExecutions",
                column: "PredecessorExecutionId",
                unique: true,
                filter: "[PredecessorExecutionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphExecutions_RequestIdentitySha256",
                table: "CentralProcessingGraphExecutions",
                column: "RequestIdentitySha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphExecutions_RevisionId",
                table: "CentralProcessingGraphExecutions",
                column: "RevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphExecutions_Status_CreatedAtUtc_Id",
                table: "CentralProcessingGraphExecutions",
                columns: new[] { "Status", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphExecutionSources_CentralArtifactId_ExecutionId",
                table: "CentralProcessingGraphExecutionSources",
                columns: new[] { "CentralArtifactId", "ExecutionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphExecutionSources_ExecutionId_Ordinal",
                table: "CentralProcessingGraphExecutionSources",
                columns: new[] { "ExecutionId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphExecutionSources_ExecutionId_SourceId_OutputOrdinal",
                table: "CentralProcessingGraphExecutionSources",
                columns: new[] { "ExecutionId", "SourceId", "OutputOrdinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphAssignments_LogicalCameraId",
                table: "CentralProcessingGraphAssignments",
                column: "LogicalCameraId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphAssignments_ObservatoryId",
                table: "CentralProcessingGraphAssignments",
                column: "ObservatoryId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphAssignments_RevisionId",
                table: "CentralProcessingGraphAssignments",
                column: "RevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphAssignments_TargetHost_EffectiveFromUtc_Id",
                table: "CentralProcessingGraphAssignments",
                columns: new[] { "TargetHost", "EffectiveFromUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphAssignments_TargetHost_LogicalCameraId_EffectiveFromUtc_Id",
                table: "CentralProcessingGraphAssignments",
                columns: new[] { "TargetHost", "LogicalCameraId", "EffectiveFromUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphAssignments_TargetHost_ObservatoryId_EffectiveFromUtc_Id",
                table: "CentralProcessingGraphAssignments",
                columns: new[] { "TargetHost", "ObservatoryId", "EffectiveFromUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphDeliveryFacts_ProposalId",
                table: "CentralProcessingGraphDeliveryFacts",
                column: "ProposalId",
                unique: true,
                filter: "[Kind] IN (N'Accepted', N'Rejected', N'Expired', N'Superseded')");

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphDeliveryFacts_ProposalId_RecordedAtUtc_Id",
                table: "CentralProcessingGraphDeliveryFacts",
                columns: new[] { "ProposalId", "RecordedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphDeliveryProposals_AssignmentId_RegistrationId_LogicalCameraInstallationId_CapabilitySnapshotSha256_Exp~",
                table: "CentralProcessingGraphDeliveryProposals",
                columns: new[] { "AssignmentId", "RegistrationId", "LogicalCameraInstallationId", "CapabilitySnapshotSha256", "ExpectedActiveLocalRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphDeliveryProposals_LogicalCameraInstallationId",
                table: "CentralProcessingGraphDeliveryProposals",
                column: "LogicalCameraInstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphDeliveryProposals_RegistrationId_LogicalCameraInstallationId_IssuedAtUtc_Id",
                table: "CentralProcessingGraphDeliveryProposals",
                columns: new[] { "RegistrationId", "LogicalCameraInstallationId", "IssuedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphDeliveryProposals_RevisionId",
                table: "CentralProcessingGraphDeliveryProposals",
                column: "RevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphRevisions_DefinitionIdentitySha256",
                table: "CentralProcessingGraphRevisions",
                column: "DefinitionIdentitySha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingGraphRevisions_Name_Revision",
                table: "CentralProcessingGraphRevisions",
                columns: new[] { "Name", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_HashedKey",
                table: "ApiKeys",
                column: "HashedKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_IsActive_ExpiresAt",
                table: "ApiKeys",
                columns: new[] { "IsActive", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_ObservatoryId",
                table: "ApiKeys",
                column: "ObservatoryId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_UserId",
                table: "ApiKeys",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_UserId_ObservatoryId",
                table: "ApiKeys",
                columns: new[] { "UserId", "ObservatoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true,
                filter: "[NormalizedName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserPasskeys_UserId",
                table: "AspNetUserPasskeys",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true,
                filter: "[NormalizedUserName] IS NOT NULL");

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
                name: "IX_CentralArtifactIngestIdentities_CentralArtifactId_ManifestSchemaVersion",
                table: "CentralArtifactIngestIdentities",
                columns: new[] { "CentralArtifactId", "ManifestSchemaVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifactIngestIdentities_IdempotencyKey",
                table: "CentralArtifactIngestIdentities",
                column: "IdempotencyKey",
                unique: true);

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
                name: "IX_CentralArtifacts_CentralFrameId_ArtifactId",
                table: "CentralArtifacts",
                columns: new[] { "CentralFrameId", "ArtifactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifacts_CentralFrameId_ReceivedAtUtc_ArtifactId",
                table: "CentralArtifacts",
                columns: new[] { "CentralFrameId", "ReceivedAtUtc", "ArtifactId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifacts_CentralFrameId_Role_RecipeVersion",
                table: "CentralArtifacts",
                columns: new[] { "CentralFrameId", "Role", "RecipeVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifacts_DevicePublicId_ArtifactId",
                table: "CentralArtifacts",
                columns: new[] { "DevicePublicId", "ArtifactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifacts_IdempotencyKey",
                table: "CentralArtifacts",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifacts_ObjectState_ObjectVerifiedAtUtc_ReceivedAtUtc_Id",
                table: "CentralArtifacts",
                columns: new[] { "ObjectState", "ObjectVerifiedAtUtc", "ReceivedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifacts_ObjectState_ReconstructionState_ReceivedAtUtc",
                table: "CentralArtifacts",
                columns: new[] { "ObjectState", "ReconstructionState", "ReceivedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifacts_ObjectState_RecoveryGeneration_Id",
                table: "CentralArtifacts",
                columns: new[] { "ObjectState", "RecoveryGeneration", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifacts_ObjectVerificationRetryAtUtc_ObjectVerificationRequestedAtUtc_Id",
                table: "CentralArtifacts",
                columns: new[] { "ObjectVerificationRetryAtUtc", "ObjectVerificationRequestedAtUtc", "Id" },
                filter: "[ObjectVerificationToken] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifacts_ReconstructionState_ReferenceRetryAtUtc_ReceivedAtUtc_Id",
                table: "CentralArtifacts",
                columns: new[] { "ReconstructionState", "ReferenceRetryAtUtc", "ReceivedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifacts_StorageReference",
                table: "CentralArtifacts",
                column: "StorageReference");

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifactSources_CentralArtifactId_Ordinal",
                table: "CentralArtifactSources",
                columns: new[] { "CentralArtifactId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifactSources_CentralArtifactId_SourceArtifactId",
                table: "CentralArtifactSources",
                columns: new[] { "CentralArtifactId", "SourceArtifactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralArtifactSources_ResolvedCentralArtifactId",
                table: "CentralArtifactSources",
                column: "ResolvedCentralArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralCaptureLocations_DeviceDeploymentLocationVersionId",
                table: "CentralCaptureLocations",
                column: "DeviceDeploymentLocationVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralCaptureLocations_LocationId_Version",
                table: "CentralCaptureLocations",
                columns: new[] { "LocationId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralCaptureProfiles_CentralFrameId_Kind",
                table: "CentralCaptureProfiles",
                columns: new[] { "CentralFrameId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralCaptureProfiles_DeviceRigProfileId",
                table: "CentralCaptureProfiles",
                column: "DeviceRigProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralClearReferenceDesignations_CentralArtifactId",
                table: "CentralClearReferenceDesignations",
                column: "CentralArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralClearReferenceDesignations_RegistrationId_RigId",
                table: "CentralClearReferenceDesignations",
                columns: new[] { "RegistrationId", "RigId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobDependencies_ConsumerJobId_Ordinal",
                table: "CentralDerivativeJobDependencies",
                columns: new[] { "ConsumerJobId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobDependencies_ExecutionId_ConsumerJobId",
                table: "CentralDerivativeJobDependencies",
                columns: new[] { "ExecutionId", "ConsumerJobId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobDependencies_ProducerJobId_ProducerOutputOrdinal",
                table: "CentralDerivativeJobDependencies",
                columns: new[] { "ProducerJobId", "ProducerOutputOrdinal" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobDependencies_ProducerSourceId",
                table: "CentralDerivativeJobDependencies",
                column: "ProducerSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobAttempts_EndedAtUtc",
                table: "CentralDerivativeJobAttempts",
                column: "EndedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobAttempts_CentralDerivativeJobId_AttemptNumber",
                table: "CentralDerivativeJobAttempts",
                columns: new[] { "CentralDerivativeJobId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobAttempts_Outcome_LeaseExpiresAtUtc",
                table: "CentralDerivativeJobAttempts",
                columns: new[] { "Outcome", "LeaseExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobCanonicalInputs_CentralDerivativeJobId_CentralDerivativeJobInputRequirementId",
                table: "CentralDerivativeJobCanonicalInputs",
                columns: new[] { "CentralDerivativeJobId", "CentralDerivativeJobInputRequirementId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobCanonicalInputs_CentralDerivativeJobId_Ordinal",
                table: "CentralDerivativeJobCanonicalInputs",
                columns: new[] { "CentralDerivativeJobId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobCanonicalInputs_EnvironmentalObservationRecordId",
                table: "CentralDerivativeJobCanonicalInputs",
                column: "EnvironmentalObservationRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobInputRequirements_CentralDerivativeJobId_Ordinal",
                table: "CentralDerivativeJobInputRequirements",
                columns: new[] { "CentralDerivativeJobId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobInputRequirements_CentralDerivativeJobId_GraphDependencyId",
                table: "CentralDerivativeJobInputRequirements",
                columns: new[] { "CentralDerivativeJobId", "GraphDependencyId" },
                filter: "[GraphDependencyId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobInputRequirements_ExpectedAgentId_ExpectedCaptureSequence_ResolutionState_CentralDerivativeJobId",
                table: "CentralDerivativeJobInputRequirements",
                columns: new[] { "ExpectedAgentId", "ExpectedCaptureSequence", "ResolutionState", "CentralDerivativeJobId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobInputRequirements_ExpectedCentralArtifactId",
                table: "CentralDerivativeJobInputRequirements",
                column: "ExpectedCentralArtifactId");

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

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobOutputs_ResultCentralArtifactId",
                table: "CentralDerivativeJobOutputs",
                column: "ResultCentralArtifactId",
                filter: "[ResultCentralArtifactId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobOutputs_ResultOutputIdentitySha256",
                table: "CentralDerivativeJobOutputs",
                column: "ResultOutputIdentitySha256",
                filter: "[ResultOutputIdentitySha256] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobs_CreatedAtUtc_Id",
                table: "CentralDerivativeJobs",
                columns: new[] { "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobs_GraphExecutionId_GraphNodeId",
                table: "CentralDerivativeJobs",
                columns: new[] { "GraphExecutionId", "GraphNodeId" },
                unique: true,
                filter: "[GraphExecutionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobs_GraphExecutionId_GraphNodeOrdinal",
                table: "CentralDerivativeJobs",
                columns: new[] { "GraphExecutionId", "GraphNodeOrdinal" },
                unique: true,
                filter: "[GraphExecutionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobs_PredecessorJobId",
                table: "CentralDerivativeJobs",
                column: "PredecessorJobId",
                unique: true,
                filter: "[PredecessorJobId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobs_RequestIdentitySha256",
                table: "CentralDerivativeJobs",
                column: "RequestIdentitySha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobs_ResultCentralArtifactId",
                table: "CentralDerivativeJobs",
                column: "ResultCentralArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobs_RetainedResultCentralArtifactId",
                table: "CentralDerivativeJobs",
                column: "RetainedResultCentralArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobs_SourceCentralArtifactId_TargetRole_TargetRecipeVersion",
                table: "CentralDerivativeJobs",
                columns: new[] { "SourceCentralArtifactId", "TargetRole", "TargetRecipeVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobs_Status_AvailableAtUtc_CreatedAtUtc_Id",
                table: "CentralDerivativeJobs",
                columns: new[] { "Status", "AvailableAtUtc", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobs_Status_LeaseExpiresAtUtc_CreatedAtUtc_Id",
                table: "CentralDerivativeJobs",
                columns: new[] { "Status", "LeaseExpiresAtUtc", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobs_Status_UpdatedAtUtc_ResolutionDeadlineUtc_CreatedAtUtc_Id",
                table: "CentralDerivativeJobs",
                columns: new[] { "Status", "UpdatedAtUtc", "ResolutionDeadlineUtc", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralDerivativeJobs_SupersededByJobId",
                table: "CentralDerivativeJobs",
                column: "SupersededByJobId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralFrames_AgentId_CapturedAtUtc",
                table: "CentralFrames",
                columns: new[] { "AgentId", "CapturedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralFrames_AgentId_CaptureSequence",
                table: "CentralFrames",
                columns: new[] { "AgentId", "CaptureSequence" },
                filter: "[CaptureSequence] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralFrames_DevicePublicId_CapturedAtUtc_FrameId",
                table: "CentralFrames",
                columns: new[] { "DevicePublicId", "CapturedAtUtc", "FrameId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralFrames_DevicePublicId_CaptureSequence",
                table: "CentralFrames",
                columns: new[] { "DevicePublicId", "CaptureSequence" },
                unique: true,
                filter: "[CaptureSequence] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralFrames_DevicePublicId_FrameId",
                table: "CentralFrames",
                columns: new[] { "DevicePublicId", "FrameId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralFrames_DeviceRigProfileId",
                table: "CentralFrames",
                column: "DeviceRigProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralFrames_LocationEvidenceState",
                table: "CentralFrames",
                column: "LocationEvidenceState");

            migrationBuilder.CreateIndex(
                name: "IX_CentralFrames_LogicalCameraInstallationId",
                table: "CentralFrames",
                column: "LogicalCameraInstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralFrames_LogicalCameraInstallationId_CapturedAtUtc_Id",
                table: "CentralFrames",
                columns: new[] { "LogicalCameraInstallationId", "CapturedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralFrames_ObservatoryId_CapturedAtUtc_Id",
                table: "CentralFrames",
                columns: new[] { "ObservatoryId", "CapturedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralFrames_RegistrationId",
                table: "CentralFrames",
                column: "RegistrationId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralFrames_RegistrationId_FirstReceivedAtUtc_Id",
                table: "CentralFrames",
                columns: new[] { "RegistrationId", "FirstReceivedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralObjectRecoveryDispositions_Kind_State_NextAttemptAtUtc_UpdatedAtUtc_Id",
                table: "CentralObjectRecoveryDispositions",
                columns: new[] { "Kind", "State", "NextAttemptAtUtc", "UpdatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralObjectRecoveryDispositions_SourceObjectIdentitySha256",
                table: "CentralObjectRecoveryDispositions",
                column: "SourceObjectIdentitySha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralObjectRecoveryDispositions_State_UpdatedAtUtc_Id",
                table: "CentralObjectRecoveryDispositions",
                columns: new[] { "State", "UpdatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingOverrideVersions_ObservatoryId",
                table: "CentralProcessingOverrideVersions",
                column: "ObservatoryId",
                unique: true,
                filter: "[SupersededAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingOverrideVersions_ObservatoryId_Version",
                table: "CentralProcessingOverrideVersions",
                columns: new[] { "ObservatoryId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralStructuredProcessingProducts_OutputIdentitySha256",
                table: "CentralStructuredProcessingProducts",
                column: "OutputIdentitySha256");

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
                name: "IX_CentralTransientContextDependencies_ContextCentralArtifactId_ExecutionOptionsIdentitySha256",
                table: "CentralTransientContextDependencies",
                columns: new[] { "ContextCentralArtifactId", "ExecutionOptionsIdentitySha256" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientContextDependencies_RequiredCentralDerivativeJobId",
                table: "CentralTransientContextDependencies",
                column: "RequiredCentralDerivativeJobId");

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
                name: "IX_CentralTransientEvents_AgentId_EventCreatedUtc_EventId",
                table: "CentralTransientEvents",
                columns: new[] { "AgentId", "EventCreatedUtc", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientEvents_EventCreatedUtc_Id",
                table: "CentralTransientEvents",
                columns: new[] { "EventCreatedUtc", "Id" });

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
                name: "IX_CentralTransientPayloadReleaseItems_Kind_RecordId",
                table: "CentralTransientPayloadReleaseItems",
                columns: new[] { "Kind", "RecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientPayloadReleaseItems_ReleaseId_Kind_RecordId",
                table: "CentralTransientPayloadReleaseItems",
                columns: new[] { "ReleaseId", "Kind", "RecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientPayloadReleaseItems_RetryAtUtc_RequestedAtUtc_ReleaseId_Ordinal",
                table: "CentralTransientPayloadReleaseItems",
                columns: new[] { "RetryAtUtc", "RequestedAtUtc", "ReleaseId", "Ordinal" },
                filter: "[Outcome] = N'Pending'")
                .Annotation("SqlServer:Include", new[] { "ReservationToken" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientPayloadReleases_CentralTransientEventId_ActorIdentity_IdempotencyKey",
                table: "CentralTransientPayloadReleases",
                columns: new[] { "CentralTransientEventId", "ActorIdentity", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientPayloadReleases_State_CreatedUtc_ReleaseId",
                table: "CentralTransientPayloadReleases",
                columns: new[] { "State", "CreatedUtc", "ReleaseId" });

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
                name: "IX_CentralTransientReprocessingRequests_CentralDerivativeJobId",
                table: "CentralTransientReprocessingRequests",
                column: "CentralDerivativeJobId");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientReprocessingRequests_CentralTransientEventId_ActorIdentity_IdempotencyKey",
                table: "CentralTransientReprocessingRequests",
                columns: new[] { "CentralTransientEventId", "ActorIdentity", "IdempotencyKey" },
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

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientValidationIdentitySlots_AgentId_SubmittedEventId",
                table: "CentralTransientValidationIdentitySlots",
                columns: new[] { "AgentId", "SubmittedEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientValidationIdentitySlots_AssessmentId",
                table: "CentralTransientValidationIdentitySlots",
                column: "AssessmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientValidationIdentitySlots_AssociationIdentitySha256",
                table: "CentralTransientValidationIdentitySlots",
                column: "AssociationIdentitySha256",
                unique: true,
                filter: "[AssociationIdentitySha256] IS NOT NULL");

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
                name: "IX_CentralTransientValidationIdentitySlots_CentralTransientEventId_PersistedAssessmentId",
                table: "CentralTransientValidationIdentitySlots",
                columns: new[] { "CentralTransientEventId", "PersistedAssessmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientValidationIdentitySlots_CentralTransientEventId_PersistedEventId",
                table: "CentralTransientValidationIdentitySlots",
                columns: new[] { "CentralTransientEventId", "PersistedEventId" });

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
                name: "IX_CentralTransientValidationJobs_ProvisionalCentralDerivativeJobId",
                table: "CentralTransientValidationJobs",
                column: "ProvisionalCentralDerivativeJobId",
                unique: true,
                filter: "[ProvisionalCentralDerivativeJobId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CentralTransientValidationJobs_SubmissionIdentitySha256",
                table: "CentralTransientValidationJobs",
                column: "SubmissionIdentitySha256",
                unique: true);

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

            migrationBuilder.CreateIndex(
                name: "IX_CuratedPublicPlacementDecisions_ObservatoryId",
                table: "CuratedPublicPlacementDecisions",
                column: "ObservatoryId");

            migrationBuilder.CreateIndex(
                name: "IX_CuratedPublicPlacementDecisions_SupersedesDecisionId",
                table: "CuratedPublicPlacementDecisions",
                column: "SupersedesDecisionId",
                unique: true,
                filter: "[SupersedesDecisionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CuratedPublicPlacementDecisions_Surface_ObservatoryId_OccurredAtUtc",
                table: "CuratedPublicPlacementDecisions",
                columns: new[] { "Surface", "ObservatoryId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CuratedPublicPlacementDecisions_Surface_PublicRecordId_OccurredAtUtc",
                table: "CuratedPublicPlacementDecisions",
                columns: new[] { "Surface", "PublicRecordId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentLocationReconciliationCaptures_CentralFrameId",
                table: "DeploymentLocationReconciliationCaptures",
                column: "CentralFrameId");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentLocationReconciliationCaptures_Work_Generation_Cursor",
                table: "DeploymentLocationReconciliationCaptures",
                columns: new[] { "DeploymentLocationReconciliationWorkId", "AuthorityConcurrencyToken", "FirstReceivedAtUtc", "CentralFrameId" });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentLocationReconciliationWork_DeviceDeploymentLocationVersionId",
                table: "DeploymentLocationReconciliationWork",
                column: "DeviceDeploymentLocationVersionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentLocationReconciliationWork_Status_LeaseExpiresAtUtc_CreatedAtUtc_Id",
                table: "DeploymentLocationReconciliationWork",
                columns: new[] { "Status", "LeaseExpiresAtUtc", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentLocationReconciliationWork_Status_NextAttemptAtUtc_CreatedAtUtc_Id",
                table: "DeploymentLocationReconciliationWork",
                columns: new[] { "Status", "NextAttemptAtUtc", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentLocationResolutionAudits_DeviceDeploymentLocationVersionId",
                table: "DeploymentLocationResolutionAudits",
                column: "DeviceDeploymentLocationVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentLocationResolutionAudits_RegistrationId_OccurredAtUtc",
                table: "DeploymentLocationResolutionAudits",
                columns: new[] { "RegistrationId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceDeploymentLocationVersions_ObservatoryId_Status",
                table: "DeviceDeploymentLocationVersions",
                columns: new[] { "ObservatoryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceDeploymentLocationVersions_ObservatoryLocationVersionId",
                table: "DeviceDeploymentLocationVersions",
                column: "ObservatoryLocationVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceDeploymentLocationVersions_RegistrationId_LocationId_Version_ObservatoryLocationVersionId",
                table: "DeviceDeploymentLocationVersions",
                columns: new[] { "RegistrationId", "LocationId", "Version", "ObservatoryLocationVersionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceDeploymentLocationVersions_RegistrationId_ProposedAtUtc_Id",
                table: "DeviceDeploymentLocationVersions",
                columns: new[] { "RegistrationId", "ProposedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceDeploymentLocationVersions_RegistrationId_Status_ProposedAtUtc_Id",
                table: "DeviceDeploymentLocationVersions",
                columns: new[] { "RegistrationId", "Status", "ProposedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceFleetStates_AgentInstanceId",
                table: "DeviceFleetStates",
                column: "AgentInstanceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceFleetStates_ReceivedAtUtc",
                table: "DeviceFleetStates",
                column: "ReceivedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceFleetStates_ReportedHealth_ReceivedAtUtc",
                table: "DeviceFleetStates",
                columns: new[] { "ReportedHealth", "ReceivedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceHeartbeatRecords_IsSignificantSnapshot_ReceivedAtUtc_Id",
                table: "DeviceHeartbeatRecords",
                columns: new[] { "IsSignificantSnapshot", "ReceivedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceHeartbeatRecords_RegistrationId_AgentInstanceId_Sequence",
                table: "DeviceHeartbeatRecords",
                columns: new[] { "RegistrationId", "AgentInstanceId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceHeartbeatRecords_RegistrationId_ReceivedAtUtc",
                table: "DeviceHeartbeatRecords",
                columns: new[] { "RegistrationId", "ReceivedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRegistrations_DeviceId",
                table: "DeviceRegistrations",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRegistrations_DeviceId_Status",
                table: "DeviceRegistrations",
                columns: new[] { "DeviceId", "Status" },
                unique: true,
                filter: "[Status] = N'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRegistrations_DeviceKeyHash",
                table: "DeviceRegistrations",
                column: "DeviceKeyHash",
                filter: "[DeviceKeyHash] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRegistrations_DevicePublicId",
                table: "DeviceRegistrations",
                column: "DevicePublicId",
                unique: true,
                filter: "[DevicePublicId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRegistrations_ObservatoryId_Status",
                table: "DeviceRegistrations",
                columns: new[] { "ObservatoryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRegistrations_ObservatoryId_Status_FriendlyName_Id",
                table: "DeviceRegistrations",
                columns: new[] { "ObservatoryId", "Status", "FriendlyName", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRigProfiles_DevicePublicId_ProfileName_ProfileVersion_ProfileSha256",
                table: "DeviceRigProfiles",
                columns: new[] { "DevicePublicId", "ProfileName", "ProfileVersion", "ProfileSha256" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRigProfiles_DevicePublicId_Version",
                table: "DeviceRigProfiles",
                columns: new[] { "DevicePublicId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRigProfiles_ObservatoryId",
                table: "DeviceRigProfiles",
                column: "ObservatoryId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRigProfiles_RegistrationId_Version",
                table: "DeviceRigProfiles",
                columns: new[] { "RegistrationId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentalObservationLineage_DerivedObservationRecordId_SourceObservationRecordId",
                table: "EnvironmentalObservationLineage",
                columns: new[] { "DerivedObservationRecordId", "SourceObservationRecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentalObservationLineage_SourceObservationRecordId",
                table: "EnvironmentalObservationLineage",
                column: "SourceObservationRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentalObservations_Retention",
                table: "EnvironmentalObservations",
                columns: new[] { "ReceivedAtUtc", "ValidThroughUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentalObservations_SourceKindObserved",
                table: "EnvironmentalObservations",
                columns: new[] { "SourceRecordId", "Kind", "ObservedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentalObservations_SourceKindValidity",
                table: "EnvironmentalObservations",
                columns: new[] { "SourceRecordId", "Kind", "ValidFromUtc", "ValidThroughUtc", "ObservedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentalObservations_SourceKindValidityEnd",
                table: "EnvironmentalObservations",
                columns: new[] { "SourceRecordId", "Kind", "ValidThroughUtc", "ValidFromUtc", "ObservedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentalObservations_SourceRecordId_ObservationId",
                table: "EnvironmentalObservations",
                columns: new[] { "SourceRecordId", "ObservationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentalObservations_TargetKindObserved",
                table: "EnvironmentalObservations",
                columns: new[] { "SiteId", "Kind", "ObservedAtUtc", "AgentId", "RigId", "SourceIdentitySha256", "ObservationId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentalObservations_TargetKindValidityEnd",
                table: "EnvironmentalObservations",
                columns: new[] { "SiteId", "AgentId", "RigId", "SourceKind", "Kind", "Quality", "ValidThroughUtc", "ValidFromUtc", "StaleAfterUtc", "ObservedAtUtc", "SourceIdentitySha256", "ObservationId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentalObservations_TargetScopeKindObserved",
                table: "EnvironmentalObservations",
                columns: new[] { "SiteId", "AgentId", "RigId", "SourceKind", "Kind", "Quality", "ObservedAtUtc", "ValidFromUtc", "SourceIdentitySha256", "ObservationId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentalObservationSources_IdentitySha256",
                table: "EnvironmentalObservationSources",
                column: "IdentitySha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentalObservationSources_SiteId_AgentId_RigId_Kind",
                table: "EnvironmentalObservationSources",
                columns: new[] { "SiteId", "AgentId", "RigId", "Kind" });

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
                name: "IX_LogicalCameraInstallations_LogicalCameraId_AssignedAtUtc_InstallationPublicId",
                table: "LogicalCameraInstallations",
                columns: new[] { "LogicalCameraId", "AssignedAtUtc", "InstallationPublicId" });

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
                name: "IX_LogicalCameras_ObservatoryId_Name_Id",
                table: "LogicalCameras",
                columns: new[] { "ObservatoryId", "Name", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_LogicalCameras_ObservatoryId_Name_Slug",
                table: "LogicalCameras",
                columns: new[] { "ObservatoryId", "Name", "Slug" });

            migrationBuilder.CreateIndex(
                name: "IX_LogicalCameras_ObservatoryId_Slug",
                table: "LogicalCameras",
                columns: new[] { "ObservatoryId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Observatories_Name",
                table: "Observatories",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Observatories_OwnerUserId",
                table: "Observatories",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ObservatoryInvitationDispositions_InvitationId",
                table: "ObservatoryInvitationDispositions",
                column: "InvitationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ObservatoryInvitations_ObservatoryId_ExpiresAtUtc_Id",
                table: "ObservatoryInvitations",
                columns: new[] { "ObservatoryId", "ExpiresAtUtc", "Id" });

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
                name: "IX_ObservatoryLocationVersions_CanonicalSha256",
                table: "ObservatoryLocationVersions",
                column: "CanonicalSha256");

            migrationBuilder.CreateIndex(
                name: "IX_ObservatoryLocationVersions_ObservatoryId",
                table: "ObservatoryLocationVersions",
                column: "ObservatoryId",
                unique: true,
                filter: "[SupersededAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ObservatoryLocationVersions_ObservatoryId_Version",
                table: "ObservatoryLocationVersions",
                columns: new[] { "ObservatoryId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ObservatoryMembershipAudits_ObservatoryId_OccurredAtUtc_Id",
                table: "ObservatoryMembershipAudits",
                columns: new[] { "ObservatoryId", "OccurredAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ObservatoryMemberships_ObservatoryId_AddedAtUtc_UserId",
                table: "ObservatoryMemberships",
                columns: new[] { "ObservatoryId", "AddedAtUtc", "UserId" });

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
                name: "IX_OpenIddictApplications_ClientId",
                table: "OpenIddictApplications",
                column: "ClientId",
                unique: true,
                filter: "[ClientId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictAuthorizations_ApplicationId_Status_Subject_Type",
                table: "OpenIddictAuthorizations",
                columns: new[] { "ApplicationId", "Status", "Subject", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictScopes_Name",
                table: "OpenIddictScopes",
                column: "Name",
                unique: true,
                filter: "[Name] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictTokens_ApplicationId_Status_Subject_Type",
                table: "OpenIddictTokens",
                columns: new[] { "ApplicationId", "Status", "Subject", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictTokens_AuthorizationId",
                table: "OpenIddictTokens",
                column: "AuthorizationId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictTokens_ReferenceId",
                table: "OpenIddictTokens",
                column: "ReferenceId",
                unique: true,
                filter: "[ReferenceId] IS NOT NULL");

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

            migrationBuilder.CreateIndex(
                name: "IX_RegisteredUserNotifications_CentralTransientEventId",
                table: "RegisteredUserNotifications",
                column: "CentralTransientEventId");

            migrationBuilder.CreateIndex(
                name: "IX_RegisteredUserNotifications_UserId_DeduplicationKey",
                table: "RegisteredUserNotifications",
                columns: new[] { "UserId", "DeduplicationKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegisteredUserNotifications_UserId_ReadUtc_CreatedUtc_Id",
                table: "RegisteredUserNotifications",
                columns: new[] { "UserId", "ReadUtc", "CreatedUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_RegisteredUserObservatoryFollows_ObservatoryId",
                table: "RegisteredUserObservatoryFollows",
                column: "ObservatoryId");

            migrationBuilder.CreateIndex(
                name: "IX_RegisteredUserObservatoryFollows_UserId_CreatedUtc_ObservatoryId",
                table: "RegisteredUserObservatoryFollows",
                columns: new[] { "UserId", "CreatedUtc", "ObservatoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_RegisteredUserSubscriptions_UserId_Kind",
                table: "RegisteredUserSubscriptions",
                columns: new[] { "UserId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegisteredUserTransientEventBookmarks_CentralTransientEventId",
                table: "RegisteredUserTransientEventBookmarks",
                column: "CentralTransientEventId");

            migrationBuilder.CreateIndex(
                name: "IX_RegisteredUserTransientEventBookmarks_UserId_CreatedUtc_CentralTransientEventId",
                table: "RegisteredUserTransientEventBookmarks",
                columns: new[] { "UserId", "CreatedUtc", "CentralTransientEventId" });

            migrationBuilder.CreateTable(
                name: "CentralProcessingRunners",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunnerId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ClientSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    OperatingSystem = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    OsArchitecture = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ProcessArchitecture = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RuntimeIdentifier = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FrameworkDescription = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ProcessorCount = table.Column<int>(type: "int", nullable: false),
                    TotalMemoryBytes = table.Column<long>(type: "bigint", nullable: false),
                    ResourceClass = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    GpuAvailable = table.Column<bool>(type: "bit", nullable: false),
                    LatencyClass = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CapabilitiesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EligibleRecipesJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    MaxConcurrency = table.Column<int>(type: "int", nullable: false),
                    MaxTransferBytes = table.Column<long>(type: "bigint", nullable: false),
                    WarmState = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ProcessId = table.Column<int>(type: "int", nullable: false),
                    ProcessStartedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Generation = table.Column<int>(type: "int", nullable: false),
                    AvailableSlots = table.Column<int>(type: "int", nullable: false),
                    RegisteredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastHeartbeatAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RetiredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralProcessingRunners", x => x.Id);
                    table.CheckConstraint("CK_CentralProcessingRunners_Capacity", "[MaxConcurrency] >= 1 AND [MaxConcurrency] <= 32 AND [AvailableSlots] >= 0 AND [AvailableSlots] <= [MaxConcurrency] AND [MaxTransferBytes] >= 1 AND [ProcessorCount] >= 1 AND [TotalMemoryBytes] >= 0 AND [Generation] >= 1");
                    table.CheckConstraint("CK_CentralProcessingRunners_Status", "[Status] IN (N'Active', N'Stale', N'Retired')");
                    table.CheckConstraint("CK_CentralProcessingRunners_Timestamps", "[UpdatedAtUtc] >= [RegisteredAtUtc] AND [LastHeartbeatAtUtc] >= [RegisteredAtUtc] AND (([Status] = N'Retired' AND [RetiredAtUtc] IS NOT NULL) OR ([Status] <> N'Retired' AND [RetiredAtUtc] IS NULL))");
                    table.CheckConstraint("CK_CentralProcessingRunners_WarmState", "[WarmState] IN (N'Cold', N'Warming', N'Warm', N'Degraded')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingRunners_ClientSubject",
                table: "CentralProcessingRunners",
                column: "ClientSubject");

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingRunners_RunnerId",
                table: "CentralProcessingRunners",
                column: "RunnerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingRunners_Status_LastHeartbeatAtUtc",
                table: "CentralProcessingRunners",
                columns: new[] { "Status", "LastHeartbeatAtUtc" });

            migrationBuilder.CreateTable(
                name: "CentralProcessingUsageRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObservatoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DevicePublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CentralDerivativeJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    RecipeName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ResourceClass = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    WorkerId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LeaseAcquiredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    InputBytes = table.Column<long>(type: "bigint", nullable: false),
                    OutputBytes = table.Column<long>(type: "bigint", nullable: false),
                    RecipeDurationTicks = table.Column<long>(type: "bigint", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CentralProcessingUsageRecords", x => x.Id);
                    table.CheckConstraint("CK_CentralProcessingUsageRecords_Counters", "[AttemptNumber] >= 1 AND [InputBytes] >= 0 AND [OutputBytes] >= 0 AND [RecipeDurationTicks] >= 0 AND [EndedAtUtc] >= [LeaseAcquiredAtUtc]");
                    table.CheckConstraint("CK_CentralProcessingUsageRecords_Outcome", "[Outcome] IN (N'Completed', N'RetryableFailure', N'TerminalFailure', N'LeaseExpired', N'Canceled', N'Skipped', N'Quarantined', N'Superseded')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingUsageRecords_CentralDerivativeJobId_AttemptNumber",
                table: "CentralProcessingUsageRecords",
                columns: new[] { "CentralDerivativeJobId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingUsageRecords_DevicePublicId_EndedAtUtc",
                table: "CentralProcessingUsageRecords",
                columns: new[] { "DevicePublicId", "EndedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingUsageRecords_ObservatoryId_EndedAtUtc",
                table: "CentralProcessingUsageRecords",
                columns: new[] { "ObservatoryId", "EndedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CentralProcessingUsageRecords_RecordedAtUtc",
                table: "CentralProcessingUsageRecords",
                column: "RecordedAtUtc");

            BaselineTriggerSql.CreateAll(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "CentralProcessingUsageRecords");


            migrationBuilder.DropTable(
                name: "CentralProcessingRunners");


            migrationBuilder.DropTable(
                name: "CentralProcessingGraphDeliveryFacts");

            migrationBuilder.DropTable(
                name: "CentralProcessingGraphDeliveryProposals");

            migrationBuilder.DropTable(
                name: "ApiKeys");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserPasskeys");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "CentralArtifactDownloadAuthorizations");

            migrationBuilder.DropTable(
                name: "CentralArtifactIngestIdentities");

            migrationBuilder.DropTable(
                name: "CentralArtifactLayouts");

            migrationBuilder.DropTable(
                name: "CentralArtifactProcessingEvidence");

            migrationBuilder.DropTable(
                name: "CentralArtifactRecipes");

            migrationBuilder.DropTable(
                name: "CentralArtifactSources");

            migrationBuilder.DropTable(
                name: "CentralCaptureControls");

            migrationBuilder.DropTable(
                name: "CentralCaptureLocations");

            migrationBuilder.DropTable(
                name: "CentralCaptureProfiles");

            migrationBuilder.DropTable(
                name: "CentralCaptureTimings");

            migrationBuilder.DropTable(
                name: "CentralClearReferenceDesignations");

            migrationBuilder.DropTable(
                name: "CentralDerivativeJobAttempts");

            migrationBuilder.DropTable(
                name: "CentralDerivativeJobCanonicalInputs");

            migrationBuilder.DropTable(
                name: "CentralDerivativeJobInputs");

            migrationBuilder.DropTable(
                name: "CentralObjectRecoveryDispositions");

            migrationBuilder.DropTable(
                name: "CentralProcessingOverrideVersions");

            migrationBuilder.DropTable(
                name: "CentralRecoveryCheckpoints");

            migrationBuilder.DropTable(
                name: "CentralStructuredProcessingProducts");

            migrationBuilder.DropTable(
                name: "CentralTransientAssessmentObservations");

            migrationBuilder.DropTable(
                name: "CentralTransientContextDependencies");

            migrationBuilder.DropTable(
                name: "CentralTransientDerivativeBackgrounds");

            migrationBuilder.DropTable(
                name: "CentralTransientEventCurrent");

            migrationBuilder.DropTable(
                name: "CentralTransientEventVersionAssessments");

            migrationBuilder.DropTable(
                name: "CentralTransientEventVersionDerivatives");

            migrationBuilder.DropTable(
                name: "CentralTransientEventVersionNotifications");

            migrationBuilder.DropTable(
                name: "CentralTransientEventVersionObservations");

            migrationBuilder.DropTable(
                name: "CentralTransientEventVersionReviews");

            migrationBuilder.DropTable(
                name: "CentralTransientExtractionSources");

            migrationBuilder.DropTable(
                name: "CentralTransientNotificationDispatches");

            migrationBuilder.DropTable(
                name: "CentralTransientPayloadReleaseItems");

            migrationBuilder.DropTable(
                name: "CentralTransientReprocessingRequests");

            migrationBuilder.DropTable(
                name: "CentralTransientReviewMutations");

            migrationBuilder.DropTable(
                name: "CentralTransientSubmissionAudits");

            migrationBuilder.DropTable(
                name: "CentralTransientValidationIdentitySlots");

            migrationBuilder.DropTable(
                name: "CentralTransientValidationOutcomeVersions");

            migrationBuilder.DropTable(
                name: "CuratedPublicPlacementDecisions");

            migrationBuilder.DropTable(
                name: "DatabaseInitializationState");

            migrationBuilder.DropTable(
                name: "DeploymentLocationReconciliationCaptures");

            migrationBuilder.DropTable(
                name: "DeploymentLocationResolutionAudits");

            migrationBuilder.DropTable(
                name: "DeviceFleetStates");

            migrationBuilder.DropTable(
                name: "DeviceHeartbeatRecords");

            migrationBuilder.DropTable(
                name: "EnvironmentalObservationLineage");

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
                name: "OpenIddictScopes");

            migrationBuilder.DropTable(
                name: "OpenIddictTokens");

            migrationBuilder.DropTable(
                name: "PublicRecordPublicationDecisions");

            migrationBuilder.DropTable(
                name: "RegisteredUserNotificationPreferences");

            migrationBuilder.DropTable(
                name: "RegisteredUserNotifications");

            migrationBuilder.DropTable(
                name: "RegisteredUserObservatoryFollows");

            migrationBuilder.DropTable(
                name: "RegisteredUserSubscriptions");

            migrationBuilder.DropTable(
                name: "RegisteredUserTransientEventBookmarks");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "CentralDerivativeJobInputRequirements");

            migrationBuilder.DropTable(
                name: "CentralDerivativeJobDependencies");

            migrationBuilder.DropTable(
                name: "CentralDerivativeJobOutputs");

            migrationBuilder.DropTable(
                name: "CentralProcessingGraphExecutionSources");

            migrationBuilder.DropTable(
                name: "CentralTransientDerivativeSources");

            migrationBuilder.DropTable(
                name: "CentralTransientObservationBackgrounds");

            migrationBuilder.DropTable(
                name: "CentralTransientExtractionReceipts");

            migrationBuilder.DropTable(
                name: "CentralTransientNotifications");

            migrationBuilder.DropTable(
                name: "CentralTransientPayloadReleases");

            migrationBuilder.DropTable(
                name: "CentralTransientReprocessingJobs");

            migrationBuilder.DropTable(
                name: "DeploymentLocationReconciliationWork");

            migrationBuilder.DropTable(
                name: "EnvironmentalObservations");

            migrationBuilder.DropTable(
                name: "ObservatoryInvitations");

            migrationBuilder.DropTable(
                name: "OpenIddictAuthorizations");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "CentralTransientDerivatives");

            migrationBuilder.DropTable(
                name: "CentralTransientObservations");

            migrationBuilder.DropTable(
                name: "CentralTransientValidationJobs");

            migrationBuilder.DropTable(
                name: "DeviceDeploymentLocationVersions");

            migrationBuilder.DropTable(
                name: "EnvironmentalObservationSources");

            migrationBuilder.DropTable(
                name: "OpenIddictApplications");

            migrationBuilder.DropTable(
                name: "CentralTransientDerivativeOutputIntents");

            migrationBuilder.DropTable(
                name: "CentralTransientReviews");

            migrationBuilder.DropTable(
                name: "CentralTransientObservationSources");

            migrationBuilder.DropTable(
                name: "ObservatoryLocationVersions");

            migrationBuilder.DropTable(
                name: "CentralTransientDerivativeJobs");

            migrationBuilder.DropTable(
                name: "CentralTransientAssessments");

            migrationBuilder.DropTable(
                name: "CentralDerivativeJobs");

            migrationBuilder.DropTable(
                name: "CentralProcessingGraphExecutions");

            migrationBuilder.DropTable(
                name: "CentralProcessingGraphAssignments");

            migrationBuilder.DropTable(
                name: "CentralProcessingGraphRevisions");

            migrationBuilder.DropTable(
                name: "CentralTransientEventVersions");

            migrationBuilder.DropTable(
                name: "CentralArtifacts");

            migrationBuilder.DropTable(
                name: "CentralTransientEvents");

            migrationBuilder.DropTable(
                name: "CentralFrames");

            migrationBuilder.DropTable(
                name: "DeviceRigProfiles");

            migrationBuilder.DropTable(
                name: "LogicalCameraInstallations");

            migrationBuilder.DropTable(
                name: "DeviceRegistrations");

            migrationBuilder.DropTable(
                name: "LogicalCameras");

            migrationBuilder.DropTable(
                name: "Observatories");
        }
    }
}
