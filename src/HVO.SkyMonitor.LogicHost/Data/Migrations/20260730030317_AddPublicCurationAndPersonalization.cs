using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicCurationAndPersonalization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

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

            migrationBuilder.Sql("""
                CREATE TRIGGER [TR_CuratedPublicPlacementDecisions_Immutable]
                ON [CuratedPublicPlacementDecisions]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    IF (ROWCOUNT_BIG() = 0) RETURN;
                    SET NOCOUNT ON;
                    THROW 51000, 'Curated public placement decision history is immutable.', 1;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql("DROP TRIGGER [TR_CuratedPublicPlacementDecisions_Immutable]");

            migrationBuilder.DropTable(
                name: "CuratedPublicPlacementDecisions");

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
        }
    }
}
