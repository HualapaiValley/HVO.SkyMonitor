using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceRigProfilesAndUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CurrentRigProfileHash",
                table: "DeviceRegistrations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CurrentRigProfileUpdatedAtUtc",
                table: "DeviceRegistrations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CurrentRigProfileVersion",
                table: "DeviceRegistrations",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DeviceImageUploads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RegistrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DevicePublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObservatoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    RigProfileVersion = table.Column<int>(type: "integer", nullable: true),
                    CapturedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    FileName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PayloadBase64Length = table.Column<int>(type: "integer", nullable: false),
                    StorageReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceImageUploads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceRigProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RegistrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DevicePublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObservatoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ConfigHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ConfigJson = table.Column<string>(type: "character varying(262144)", maxLength: 262144, nullable: false),
                    SoftwareVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EffectiveFromUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceRigProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceRigProfiles_DeviceRegistrations_RegistrationId",
                        column: x => x.RegistrationId,
                        principalTable: "DeviceRegistrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceImageUploads_CapturedAtUtc",
                table: "DeviceImageUploads",
                column: "CapturedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceImageUploads_DevicePublicId",
                table: "DeviceImageUploads",
                column: "DevicePublicId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceImageUploads_ObservatoryId",
                table: "DeviceImageUploads",
                column: "ObservatoryId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceImageUploads_RegistrationId",
                table: "DeviceImageUploads",
                column: "RegistrationId");

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceImageUploads");

            migrationBuilder.DropTable(
                name: "DeviceRigProfiles");

            migrationBuilder.DropColumn(
                name: "CurrentRigProfileHash",
                table: "DeviceRegistrations");

            migrationBuilder.DropColumn(
                name: "CurrentRigProfileUpdatedAtUtc",
                table: "DeviceRegistrations");

            migrationBuilder.DropColumn(
                name: "CurrentRigProfileVersion",
                table: "DeviceRegistrations");
        }
    }
}
