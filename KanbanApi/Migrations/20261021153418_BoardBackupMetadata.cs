using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KanbanApi.Migrations
{
    /// <inheritdoc />
    public partial class BoardBackupMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceAuthorName",
                table: "Comments",
                type: "nvarchar(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceAssigneeName",
                table: "Cards",
                type: "nvarchar(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RestoreChecksum",
                table: "Boards",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM Boards WHERE RestoreChecksum IS NOT NULL) OR EXISTS (SELECT 1 FROM Cards WHERE SourceAssigneeName IS NOT NULL) OR EXISTS (SELECT 1 FROM Comments WHERE SourceAuthorName IS NOT NULL) THROW 50009, 'Cannot downgrade while imported board metadata exists.', 1;");
            migrationBuilder.DropColumn(
                name: "SourceAuthorName",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "SourceAssigneeName",
                table: "Cards");

            migrationBuilder.DropColumn(
                name: "RestoreChecksum",
                table: "Boards");
        }
    }
}
