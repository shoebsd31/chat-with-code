using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatWithCode.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSuggestedQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "suggested_questions",
                table: "indexed_branches",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "suggestions_source",
                table: "indexed_branches",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "suggested_questions",
                table: "indexed_branches");

            migrationBuilder.DropColumn(
                name: "suggestions_source",
                table: "indexed_branches");
        }
    }
}
