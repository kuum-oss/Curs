using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KanbanApi.Migrations
{
    /// <inheritdoc />
    public partial class BoardHistoryStatistics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CompletionColumnId",
                table: "Boards",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InsightsSinceUtc",
                table: "Boards",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "SYSUTCDATETIME()");

            migrationBuilder.CreateTable(
                name: "BoardActivities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BoardId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CardId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CardTitle = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Field = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Before = table.Column<string>(type: "nvarchar(max)", maxLength: 10000, nullable: true),
                    After = table.Column<string>(type: "nvarchar(max)", maxLength: 10000, nullable: true),
                    BeforeStateJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AfterStateJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FromColumnId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ToColumnId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CardCreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BoardActivities_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BoardActivities_BoardId_CardId_CreatedAtUtc",
                table: "BoardActivities",
                columns: new[] { "BoardId", "CardId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BoardActivities_BoardId_Id",
                table: "BoardActivities",
                columns: new[] { "BoardId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_BoardActivities_BoardId_ToColumnId_CreatedAtUtc",
                table: "BoardActivities",
                columns: new[] { "BoardId", "ToColumnId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [BoardActivities]) OR EXISTS (SELECT 1 FROM [Boards] WHERE [CompletionColumnId] IS NOT NULL) THROW 50011, 'History or completion settings must not be lost by downgrade.', 1;");
            migrationBuilder.DropTable(
                name: "BoardActivities");

            migrationBuilder.DropColumn(
                name: "CompletionColumnId",
                table: "Boards");

            migrationBuilder.DropColumn(
                name: "InsightsSinceUtc",
                table: "Boards");
        }
    }
}
