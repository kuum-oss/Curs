using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KanbanApi.Migrations
{
    /// <inheritdoc />
    public partial class PrivateChats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DirectChats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FirstUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SecondUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FirstReadId = table.Column<long>(type: "bigint", nullable: false),
                    SecondReadId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectChats", x => x.Id);
                    table.CheckConstraint("CK_DirectChats_Peers", "[FirstUserId] IS NULL OR [SecondUserId] IS NULL OR [FirstUserId] <> [SecondUserId]");
                    table.ForeignKey(
                        name: "FK_DirectChats_Users_FirstUserId",
                        column: x => x.FirstUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_DirectChats_Users_SecondUserId",
                        column: x => x.SecondUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "DirectMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ChatId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SenderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClientMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DirectMessages_DirectChats_ChatId",
                        column: x => x.ChatId,
                        principalTable: "DirectChats",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_DirectMessages_Users_SenderId",
                        column: x => x.SenderId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_DirectChats_FirstUserId_SecondUserId",
                table: "DirectChats",
                columns: new[] { "FirstUserId", "SecondUserId" },
                unique: true,
                filter: "[FirstUserId] IS NOT NULL AND [SecondUserId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DirectChats_SecondUserId",
                table: "DirectChats",
                column: "SecondUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectMessages_ChatId_ClientMessageId",
                table: "DirectMessages",
                columns: new[] { "ChatId", "ClientMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DirectMessages_ChatId_Id",
                table: "DirectMessages",
                columns: new[] { "ChatId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectMessages_SenderId",
                table: "DirectMessages",
                column: "SenderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM DirectMessages) THROW 50003, 'Cannot downgrade while private messages exist.', 1;");
            migrationBuilder.DropTable(
                name: "DirectMessages");

            migrationBuilder.DropTable(
                name: "DirectChats");
        }
    }
}
