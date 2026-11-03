using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Loomi.Data.Migrations
{
    /// <inheritdoc />
    public partial class GrowthFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FromBot",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "AnnouncedAt",
                table: "Templates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StarterKey",
                table: "Templates",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastNudgeAt",
                table: "TelegramLinks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LowCreditNudgeAt",
                table: "TelegramLinks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NotificationsOff",
                table: "TelegramLinks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsGift",
                table: "CreditPackages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "LoginLinks",
                columns: table => new
                {
                    Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoginLinks", x => x.Hash);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Templates_StarterKey",
                table: "Templates",
                column: "StarterKey",
                unique: true,
                filter: "\"StarterKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LoginLinks_UserId",
                table: "LoginLinks",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoginLinks");

            migrationBuilder.DropIndex(
                name: "IX_Templates_StarterKey",
                table: "Templates");

            migrationBuilder.DropColumn(
                name: "FromBot",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AnnouncedAt",
                table: "Templates");

            migrationBuilder.DropColumn(
                name: "StarterKey",
                table: "Templates");

            migrationBuilder.DropColumn(
                name: "LastNudgeAt",
                table: "TelegramLinks");

            migrationBuilder.DropColumn(
                name: "LowCreditNudgeAt",
                table: "TelegramLinks");

            migrationBuilder.DropColumn(
                name: "NotificationsOff",
                table: "TelegramLinks");

            migrationBuilder.DropColumn(
                name: "IsGift",
                table: "CreditPackages");
        }
    }
}
