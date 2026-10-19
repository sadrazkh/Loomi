using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Loomi.Data.Migrations
{
    /// <inheritdoc />
    public partial class TimedPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PackageId",
                table: "Generations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PackageId",
                table: "CreditEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CreditPackages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Amount = table.Column<int>(type: "INTEGER", nullable: false),
                    StartsAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndsAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    EndedEarlyAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReminderSentAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndNoticeSentAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditPackages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CreditPackages_UserId_EndsAt",
                table: "CreditPackages",
                columns: new[] { "UserId", "EndsAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CreditPackages");

            migrationBuilder.DropColumn(
                name: "PackageId",
                table: "Generations");

            migrationBuilder.DropColumn(
                name: "PackageId",
                table: "CreditEntries");
        }
    }
}
