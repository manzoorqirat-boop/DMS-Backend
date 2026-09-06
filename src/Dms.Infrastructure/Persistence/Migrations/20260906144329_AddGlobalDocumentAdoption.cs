using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGlobalDocumentAdoption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "scope",
                schema: "dms",
                table: "controlled_documents",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "document_adoptions",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    adopted_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    adopted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    withdrawn_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    withdrawn_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    withdrawal_reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_adoptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_document_adoptions_controlled_documents_document_id",
                        column: x => x.document_id,
                        principalSchema: "dms",
                        principalTable: "controlled_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_document_adoptions_sites_site_id",
                        column: x => x.site_id,
                        principalSchema: "dms",
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_document_adoptions_site",
                schema: "dms",
                table: "document_adoptions",
                columns: new[] { "site_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ux_document_adoptions_active",
                schema: "dms",
                table: "document_adoptions",
                columns: new[] { "document_id", "site_id" },
                unique: true,
                filter: "is_active = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_adoptions",
                schema: "dms");

            migrationBuilder.DropColumn(
                name: "scope",
                schema: "dms",
                table: "controlled_documents");
        }
    }
}
