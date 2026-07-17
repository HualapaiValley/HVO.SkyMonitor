using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEnvironmentalObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "EnvironmentalObservationLineage");

            migrationBuilder.DropTable(
                name: "EnvironmentalObservations");

            migrationBuilder.DropTable(
                name: "EnvironmentalObservationSources");
        }
    }
}
