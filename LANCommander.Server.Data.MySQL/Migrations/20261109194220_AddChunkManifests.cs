using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LANCommander.Server.Data.MySQL.Migrations
{
    /// <inheritdoc />
    public partial class AddChunkManifests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ManifestBuiltOn",
                table: "Archive",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManifestError",
                table: "Archive",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ManifestHash",
                table: "Archive",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "ManifestStatus",
                table: "Archive",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Chunks",
                columns: table => new
                {
                    Hash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    StoredSize = table.Column<long>(type: "bigint", nullable: false),
                    Compression = table.Column<int>(type: "int", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Chunks", x => x.Hash);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ArchiveChunks",
                columns: table => new
                {
                    ArchiveId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ChunkHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchiveChunks", x => new { x.ArchiveId, x.ChunkHash });
                    table.ForeignKey(
                        name: "FK_ArchiveChunks_Archive_ArchiveId",
                        column: x => x.ArchiveId,
                        principalTable: "Archive",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ArchiveChunks_Chunks_ChunkHash",
                        column: x => x.ChunkHash,
                        principalTable: "Chunks",
                        principalColumn: "Hash",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Archive_ManifestStatus",
                table: "Archive",
                column: "ManifestStatus");

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveChunks_ChunkHash",
                table: "ArchiveChunks",
                column: "ChunkHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArchiveChunks");

            migrationBuilder.DropTable(
                name: "Chunks");

            migrationBuilder.DropIndex(
                name: "IX_Archive_ManifestStatus",
                table: "Archive");

            migrationBuilder.DropColumn(
                name: "ManifestBuiltOn",
                table: "Archive");

            migrationBuilder.DropColumn(
                name: "ManifestError",
                table: "Archive");

            migrationBuilder.DropColumn(
                name: "ManifestHash",
                table: "Archive");

            migrationBuilder.DropColumn(
                name: "ManifestStatus",
                table: "Archive");
        }
    }
}
