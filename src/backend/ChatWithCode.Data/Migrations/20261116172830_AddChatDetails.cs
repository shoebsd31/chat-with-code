using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatWithCode.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "symbol",
                table: "citations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "used",
                table: "citations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_conversations_updated_at",
                table: "conversations",
                column: "updated_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_conversations_updated_at",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "symbol",
                table: "citations");

            migrationBuilder.DropColumn(
                name: "used",
                table: "citations");
        }
    }
}
