using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KanbanApi.Migrations
{
    /// <inheritdoc />
    public partial class AccountRecoveryWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletionDueAtUtc",
                table: "Users",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletionRequestedAtUtc",
                table: "Users",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsProtectedDemo",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Users_DeletionDueAtUtc",
                table: "Users",
                column: "DeletionDueAtUtc");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Users_DeletionWindow",
                table: "Users",
                sql: "([DeletionRequestedAtUtc] IS NULL AND [DeletionDueAtUtc] IS NULL) OR ([DeletionRequestedAtUtc] IS NOT NULL AND [DeletionDueAtUtc] IS NOT NULL AND [DeletionDueAtUtc] = DATEADD(day, 7, [DeletionRequestedAtUtc]))");
            migrationBuilder.Sql("UPDATE Users SET IsProtectedDemo = 1 WHERE NormalizedEmail IN ('DEMO.ANASTASIIA@KANBAN.LOCAL','DEMO.OLENA@KANBAN.LOCAL','DEMO.DMYTRO@KANBAN.LOCAL','DEMO.IRYNA@KANBAN.LOCAL') OR Email IN ('demo.anastasiia@kanban.local','demo.olena@kanban.local','demo.dmytro@kanban.local','demo.iryna@kanban.local');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM Users WHERE DeletionDueAtUtc IS NOT NULL) THROW 50002, 'Cannot remove an active account recovery window.', 1;");
            migrationBuilder.DropIndex(
                name: "IX_Users_DeletionDueAtUtc",
                table: "Users");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Users_DeletionWindow",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DeletionDueAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DeletionRequestedAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsProtectedDemo",
                table: "Users");
        }
    }
}
