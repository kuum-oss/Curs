using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KanbanApi.Migrations
{
    /// <inheritdoc />
    public partial class NotificationCenter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    EventKey = table.Column<string>(type: "varchar(160)", unicode: false, maxLength: 160, nullable: false),
                    CardId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CommentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChatId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MessageId = table.Column<long>(type: "bigint", nullable: true),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReadAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.CheckConstraint("CK_Notifications_Kind", "[Kind] IN (0,1,2,3)");
                    table.ForeignKey(
                        name: "FK_Notifications_Users_ActorId",
                        column: x => x.ActorId,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Notifications_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_ActorId",
                table: "Notifications",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_EventKey",
                table: "Notifications",
                columns: new[] { "UserId", "EventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_ReadAtUtc_Id",
                table: "Notifications",
                columns: new[] { "UserId", "ReadAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM Notifications) THROW 50007, 'Cannot downgrade while notifications exist.', 1;");
            migrationBuilder.DropTable(
                name: "Notifications");
        }
    }
}
