using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionLastActivityIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_chat_sessions_LastActivityAt",
                table: "chat_sessions",
                column: "LastActivityAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_chat_sessions_LastActivityAt",
                table: "chat_sessions");
        }
    }
}
