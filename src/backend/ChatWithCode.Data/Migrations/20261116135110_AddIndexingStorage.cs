using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;
using Pgvector;

#nullable disable

namespace ChatWithCode.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIndexingStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.AddColumn<int>(
                name: "progress_done",
                table: "indexed_branches",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "progress_total",
                table: "indexed_branches",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "code_chunks",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    repository_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    path = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    language = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    start_line = table.Column<int>(type: "integer", nullable: false),
                    end_line = table.Column<int>(type: "integer", nullable: false),
                    symbol = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    content = table.Column<string>(type: "text", nullable: false),
                    commit_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    embedding = table.Column<Vector>(type: "vector(1536)", nullable: false),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true)
                        .Annotation("Npgsql:TsVectorConfig", "simple")
                        .Annotation("Npgsql:TsVectorProperties", new[] { "path", "symbol", "content" })
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_code_chunks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "indexed_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    indexed_branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    path = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    blob_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    chunk_count = table.Column<int>(type: "integer", nullable: false),
                    skip_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_indexed_files", x => x.id);
                    table.ForeignKey(
                        name: "fk_indexed_files_indexed_branches_indexed_branch_id",
                        column: x => x.indexed_branch_id,
                        principalTable: "indexed_branches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_code_chunks_embedding",
                table: "code_chunks",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_code_chunks_repository_id_branch_path",
                table: "code_chunks",
                columns: new[] { "repository_id", "branch", "path" });

            migrationBuilder.CreateIndex(
                name: "ix_code_chunks_search_vector",
                table: "code_chunks",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "ix_indexed_files_indexed_branch_id_path",
                table: "indexed_files",
                columns: new[] { "indexed_branch_id", "path" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "code_chunks");

            migrationBuilder.DropTable(
                name: "indexed_files");

            migrationBuilder.DropColumn(
                name: "progress_done",
                table: "indexed_branches");

            migrationBuilder.DropColumn(
                name: "progress_total",
                table: "indexed_branches");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
