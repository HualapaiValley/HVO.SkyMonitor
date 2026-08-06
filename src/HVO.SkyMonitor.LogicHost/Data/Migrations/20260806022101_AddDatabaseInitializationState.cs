using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDatabaseInitializationState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "DatabaseInitializationState");
        }
    }
}
