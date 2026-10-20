using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KanbanApi.Migrations
{
    /// <inheritdoc />
    public partial class ChatCardSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SharedCardId",
                table: "DirectMessages",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM DirectMessages WHERE SharedCardId IS NOT NULL) THROW 50008, 'Cannot downgrade while shared card links exist.', 1;");
            migrationBuilder.DropColumn(
                name: "SharedCardId",
                table: "DirectMessages");
        }
    }
}
