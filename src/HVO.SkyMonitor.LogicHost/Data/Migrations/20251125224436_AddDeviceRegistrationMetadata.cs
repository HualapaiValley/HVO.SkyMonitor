using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{

    /// <inheritdoc />
    public partial class AddDeviceRegistrationMetadata : Migration
    {
        /// <inheritdoc />
        [SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "EF supplies migration builders")]
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ObservatoryElevationMeters",
                table: "DeviceRegistrations",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "ObservatoryLatitudeDegrees",
                table: "DeviceRegistrations",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "ObservatoryLongitudeDegrees",
                table: "DeviceRegistrations",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "ObservatoryName",
                table: "DeviceRegistrations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ObservatoryTimeZoneId",
                table: "DeviceRegistrations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "UTC");

            migrationBuilder.AddColumn<string>(
                name: "OwnerConfirmationMethod",
                table: "DeviceRegistrations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "SelfAttested");

            migrationBuilder.AddColumn<string>(
                name: "OwnerConfirmationNotes",
                table: "DeviceRegistrations",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OwnerConfirmedAtUtc",
                table: "DeviceRegistrations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerDisplayName",
                table: "DeviceRegistrations",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OwnerEmail",
                table: "DeviceRegistrations",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "DeviceRegistrations",
                type: "character varying(450)",
                maxLength: 450,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(@"UPDATE ""DeviceRegistrations"" dr
SET
    ""ObservatoryName"" = COALESCE(o.""Name"", 'Unknown Observatory'),
    ""ObservatoryLatitudeDegrees"" = COALESCE(o.""LatitudeDegrees"", 0),
    ""ObservatoryLongitudeDegrees"" = COALESCE(o.""LongitudeDegrees"", 0),
    ""ObservatoryElevationMeters"" = COALESCE(o.""ElevationMeters"", 0),
    ""ObservatoryTimeZoneId"" = COALESCE(NULLIF(o.""TimeZoneId"", ''), 'UTC'),
    ""OwnerUserId"" = COALESCE(o.""OwnerUserId"", 'unknown-owner'),
    ""OwnerDisplayName"" = COALESCE(o.""OwnerUserId"", 'unknown-owner'),
    ""OwnerConfirmedAtUtc"" = COALESCE(dr.""OwnerConfirmedAtUtc"", dr.""IssuedAtUtc"")
FROM ""Observatories"" o
WHERE dr.""ObservatoryId"" = o.""Id"";");
        }

        /// <inheritdoc />
        [SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "EF supplies migration builders")]
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ObservatoryElevationMeters",
                table: "DeviceRegistrations");

            migrationBuilder.DropColumn(
                name: "ObservatoryLatitudeDegrees",
                table: "DeviceRegistrations");

            migrationBuilder.DropColumn(
                name: "ObservatoryLongitudeDegrees",
                table: "DeviceRegistrations");

            migrationBuilder.DropColumn(
                name: "ObservatoryName",
                table: "DeviceRegistrations");

            migrationBuilder.DropColumn(
                name: "ObservatoryTimeZoneId",
                table: "DeviceRegistrations");

            migrationBuilder.DropColumn(
                name: "OwnerConfirmationMethod",
                table: "DeviceRegistrations");

            migrationBuilder.DropColumn(
                name: "OwnerConfirmationNotes",
                table: "DeviceRegistrations");

            migrationBuilder.DropColumn(
                name: "OwnerConfirmedAtUtc",
                table: "DeviceRegistrations");

            migrationBuilder.DropColumn(
                name: "OwnerDisplayName",
                table: "DeviceRegistrations");

            migrationBuilder.DropColumn(
                name: "OwnerEmail",
                table: "DeviceRegistrations");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "DeviceRegistrations");
        }
    }
}
