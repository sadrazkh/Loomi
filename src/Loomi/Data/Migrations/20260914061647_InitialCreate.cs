using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Loomi.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ConversationUrl = table.Column<string>(type: "TEXT", nullable: true),
                    BrowserProfileReference = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Generations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", maxLength: 12000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ParentGenerationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LocalImagePath = table.Column<string>(type: "TEXT", nullable: true),
                    ConversationUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Operation = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Generations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Generations_Generations_ParentGenerationId",
                        column: x => x.ParentGenerationId,
                        principalTable: "Generations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Generations_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Generations_ParentGenerationId",
                table: "Generations",
                column: "ParentGenerationId");

            migrationBuilder.CreateIndex(
                name: "IX_Generations_ProjectId",
                table: "Generations",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Generations_Status_CreatedAt",
                table: "Generations",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Generations");

            migrationBuilder.DropTable(
                name: "Projects");
        }
    }
}
