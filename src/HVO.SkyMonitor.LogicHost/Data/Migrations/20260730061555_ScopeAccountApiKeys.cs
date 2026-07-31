using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HVO.SkyMonitor.LogicHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScopeAccountApiKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<Guid>(
                name: "ObservatoryId",
                table: "ApiKeys",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_ObservatoryId",
                table: "ApiKeys",
                column: "ObservatoryId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_UserId_ObservatoryId",
                table: "ApiKeys",
                columns: new[] { "UserId", "ObservatoryId" });

            migrationBuilder.AddForeignKey(
                name: "FK_ApiKeys_Observatories_ObservatoryId",
                table: "ApiKeys",
                column: "ObservatoryId",
                principalTable: "Observatories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropForeignKey(
                name: "FK_ApiKeys_Observatories_ObservatoryId",
                table: "ApiKeys");

            migrationBuilder.DropIndex(
                name: "IX_ApiKeys_ObservatoryId",
                table: "ApiKeys");

            migrationBuilder.DropIndex(
                name: "IX_ApiKeys_UserId_ObservatoryId",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "ObservatoryId",
                table: "ApiKeys");
        }
    }
}
