using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Loomi.Data.Migrations
{
    /// <inheritdoc />
    public partial class AnsweredReplies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReplyText",
                table: "Generations",
                type: "TEXT",
                maxLength: 8000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReplyText",
                table: "Generations");
        }
    }
}
