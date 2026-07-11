using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{

    /// <inheritdoc />
    public partial class AddObservatories : Migration
    {
        /// <inheritdoc />
        [SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "EF supplies migration builders")]
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Observatories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LatitudeDegrees = table.Column<double>(type: "double precision", nullable: false),
                    LongitudeDegrees = table.Column<double>(type: "double precision", nullable: false),
                    ElevationMeters = table.Column<double>(type: "double precision", nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Observatories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Observatories_Name",
                table: "Observatories",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Observatories_OwnerUserId",
                table: "Observatories",
                column: "OwnerUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_DeviceRegistrations_Observatories_ObservatoryId",
                table: "DeviceRegistrations",
                column: "ObservatoryId",
                principalTable: "Observatories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        [SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "EF supplies migration builders")]
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DeviceRegistrations_Observatories_ObservatoryId",
                table: "DeviceRegistrations");

            migrationBuilder.DropTable(
                name: "Observatories");
        }
    }
}
