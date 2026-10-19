using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KanbanApi.Migrations
{
    /// <inheritdoc />
    public partial class CardChecklists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChecklistItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CardId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    IsCompleted = table.Column<bool>(type: "bit", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChecklistItems", x => x.Id);
                    table.CheckConstraint("CK_ChecklistItems_Position", "[Position] >= 0");
                    table.ForeignKey(
                        name: "FK_ChecklistItems_Cards_CardId",
                        column: x => x.CardId,
                        principalTable: "Cards",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistItems_CardId_Position",
                table: "ChecklistItems",
                columns: new[] { "CardId", "Position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM ChecklistItems) THROW 50006, 'Cannot downgrade while checklist items exist.', 1;");
            migrationBuilder.DropTable(
                name: "ChecklistItems");
        }
    }
}
