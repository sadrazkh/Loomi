using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Loomi.Data.Migrations
{
    /// <inheritdoc />
    public partial class BotRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BotRequests",
                columns: table => new
                {
                    GenerationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    StatusMessageId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotRequests", x => x.GenerationId);
                    table.ForeignKey(
                        name: "FK_BotRequests_Generations_GenerationId",
                        column: x => x.GenerationId,
                        principalTable: "Generations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BotRequests_ChatId_CreatedAt",
                table: "BotRequests",
                columns: new[] { "ChatId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BotRequests");
        }
    }
}
