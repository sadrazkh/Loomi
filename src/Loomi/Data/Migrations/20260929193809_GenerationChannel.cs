using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Loomi.Data.Migrations
{
    /// <inheritdoc />
    public partial class GenerationChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Channel",
                table: "Generations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
            // Work already done keeps the door it came through where the bot's own records show it; nothing marked earlier API use, so it reads as Web.
            migrationBuilder.Sql("UPDATE Generations SET Channel = 1 WHERE Id IN (SELECT GenerationId FROM BotRequests);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Channel",
                table: "Generations");
        }
    }
}
