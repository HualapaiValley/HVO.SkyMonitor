using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AccountType",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_AccountType",
                table: "AspNetUsers",
                column: "AccountType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_AccountType",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "AccountType",
                table: "AspNetUsers");
        }
    }
}
