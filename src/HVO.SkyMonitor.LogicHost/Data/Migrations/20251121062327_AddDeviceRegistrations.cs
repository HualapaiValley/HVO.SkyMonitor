using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceRegistrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "DeviceRegistrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ObservatoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    FriendlyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    VerificationCodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DevicePublicId = table.Column<Guid>(type: "uuid", nullable: true),
                    RegistrationTokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DeviceKeyHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    IssuedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ActivatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EnvelopeVersion = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "v1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceRegistrations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRegistrations_DeviceId",
                table: "DeviceRegistrations",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRegistrations_DeviceId_Status",
                table: "DeviceRegistrations",
                columns: new[] { "DeviceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRegistrations_DevicePublicId",
                table: "DeviceRegistrations",
                column: "DevicePublicId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRegistrations_ObservatoryId_Status",
                table: "DeviceRegistrations",
                columns: new[] { "ObservatoryId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "DeviceRegistrations");
        }
    }
}
