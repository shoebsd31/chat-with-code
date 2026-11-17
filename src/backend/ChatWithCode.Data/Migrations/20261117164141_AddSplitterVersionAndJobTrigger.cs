using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatWithCode.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSplitterVersionAndJobTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "splitter_version",
                table: "indexed_files",
                type: "integer",
                nullable: false,
                defaultValue: 1); // files indexed so far were split with version 1

            migrationBuilder.AddColumn<string>(
                name: "trigger",
                table: "index_jobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Admin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "splitter_version",
                table: "indexed_files");

            migrationBuilder.DropColumn(
                name: "trigger",
                table: "index_jobs");
        }
    }
}
