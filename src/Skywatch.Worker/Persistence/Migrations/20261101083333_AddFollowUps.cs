using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Skywatch.Worker.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFollowUps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "FollowUpOfMessageId",
                table: "Messages",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplyToTelegramMessageId",
                table: "Messages",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_FollowUpOfMessageId",
                table: "Messages",
                column: "FollowUpOfMessageId");

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_Messages_FollowUpOfMessageId",
                table: "Messages",
                column: "FollowUpOfMessageId",
                principalTable: "Messages",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Messages_Messages_FollowUpOfMessageId",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_FollowUpOfMessageId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "FollowUpOfMessageId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ReplyToTelegramMessageId",
                table: "Messages");
        }
    }
}
