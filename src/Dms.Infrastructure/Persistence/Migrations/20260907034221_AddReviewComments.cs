using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewComments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "review_comments",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    section_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    quoted_text = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    raised_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    raised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    response = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    responded_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    responded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_comments", x => x.id);
                    table.ForeignKey(
                        name: "fk_review_comments_controlled_documents_document_id",
                        column: x => x.document_id,
                        principalSchema: "dms",
                        principalTable: "controlled_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_review_comments_document_open",
                schema: "dms",
                table: "review_comments",
                columns: new[] { "document_id", "status", "severity" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "review_comments",
                schema: "dms");
        }
    }
}
