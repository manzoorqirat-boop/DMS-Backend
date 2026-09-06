using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "dms");

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_label = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    details = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "document_number_sequences",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_key = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    last_sequence = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_number_sequences", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "document_status_stamps",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    stamps_json = table.Column<string>(type: "jsonb", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_status_stamps", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "document_types",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_types", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "password_policies",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    minimum_length = table.Column<int>(type: "integer", nullable: false),
                    expiry_days = table.Column<int>(type: "integer", nullable: false),
                    history_count = table.Column<int>(type: "integer", nullable: false),
                    max_failed_attempts = table.Column<int>(type: "integer", nullable: false),
                    lockout_minutes = table.Column<int>(type: "integer", nullable: false),
                    require_complexity = table.Column<bool>(type: "boolean", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_password_policies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pending_actions",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    timing = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    subject_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_label = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    countersigner_permission = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rejection_reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pending_actions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "scheduled_job_runs",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    trigger = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    items_processed = table.Column<int>(type: "integer", nullable: false),
                    detail = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scheduled_job_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "signature_policies",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    points_json = table.Column<string>(type: "jsonb", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_signature_policies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sites",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sites", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    full_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    department = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    designation = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    employee_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    password_hash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    must_change_password = table.Column<bool>(type: "boolean", nullable: false),
                    password_last_changed = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    password_history = table.Column<string>(type: "jsonb", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    failed_signing_attempts = table.Column<int>(type: "integer", nullable: false),
                    locked_out_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_login_attempts = table.Column<int>(type: "integer", nullable: false),
                    login_locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "document_templates",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    template_version = table.Column<int>(type: "integer", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    validated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    validation_issues = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_templates", x => x.id);
                    table.ForeignKey(
                        name: "fk_document_templates_document_types_document_type_id",
                        column: x => x.document_type_id,
                        principalSchema: "dms",
                        principalTable: "document_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "metadata_field_definitions",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    is_required = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_metadata_field_definitions", x => x.id);
                    table.ForeignKey(
                        name: "fk_metadata_field_definitions_document_types_document_type_id",
                        column: x => x.document_type_id,
                        principalSchema: "dms",
                        principalTable: "document_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "action_signatures",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pending_action_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    full_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    department = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    designation = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    meaning = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    signed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_action_signatures", x => x.id);
                    table.ForeignKey(
                        name: "fk_action_signatures_pending_actions_pending_action_id",
                        column: x => x.pending_action_id,
                        principalSchema: "dms",
                        principalTable: "pending_actions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_rules",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    document_type_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    recipient_mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    recipient_role_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lead_days = table.Column<int>(type: "integer", nullable: false),
                    repeat_every_days = table.Column<int>(type: "integer", nullable: false),
                    subject_template = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    body_template = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    created_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_notification_rules_document_types_document_type_id",
                        column: x => x.document_type_id,
                        principalSchema: "dms",
                        principalTable: "document_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_notification_rules_roles_recipient_role_id",
                        column: x => x.recipient_role_id,
                        principalSchema: "dms",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "role_permissions",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_permissions", x => x.id);
                    table.ForeignKey(
                        name: "fk_role_permissions_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "dms",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "departments",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_departments", x => x.id);
                    table.ForeignKey(
                        name: "fk_departments_sites_site_id",
                        column: x => x.site_id,
                        principalSchema: "dms",
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "numbering_rules",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pattern = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_numbering_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_numbering_rules_document_types_document_type_id",
                        column: x => x.document_type_id,
                        principalSchema: "dms",
                        principalTable: "document_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_numbering_rules_sites_site_id",
                        column: x => x.site_id,
                        principalSchema: "dms",
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "retention_policies",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    retention_years = table.Column<int>(type: "integer", nullable: false),
                    trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_retention_policies", x => x.id);
                    table.ForeignKey(
                        name: "fk_retention_policies_document_types_document_type_id",
                        column: x => x.document_type_id,
                        principalSchema: "dms",
                        principalTable: "document_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_retention_policies_sites_site_id",
                        column: x => x.site_id,
                        principalSchema: "dms",
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "review_policies",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    review_interval_months = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_policies", x => x.id);
                    table.ForeignKey(
                        name: "fk_review_policies_document_types_document_type_id",
                        column: x => x.document_type_id,
                        principalSchema: "dms",
                        principalTable: "document_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_review_policies_sites_site_id",
                        column: x => x.site_id,
                        principalSchema: "dms",
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workflow_definitions",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_definitions", x => x.id);
                    table.ForeignKey(
                        name: "fk_workflow_definitions_document_types_document_type_id",
                        column: x => x.document_type_id,
                        principalSchema: "dms",
                        principalTable: "document_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_workflow_definitions_sites_site_id",
                        column: x => x.site_id,
                        principalSchema: "dms",
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_user_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    recipient_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    kind = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    dedupe_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    subject_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_notifications_users_recipient_user_id",
                        column: x => x.recipient_user_id,
                        principalSchema: "dms",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "controlled_documents",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    title = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    working_copy_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    parent_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    annexure_number = table.Column<int>(type: "integer", nullable: true),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_current_revision = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    author = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: true),
                    approved_copy_key = table.Column<string>(type: "text", nullable: true),
                    approved_content_hash = table.Column<string>(type: "text", nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_review_date = table.Column<DateOnly>(type: "date", nullable: true),
                    last_reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_reviewed_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    obsolete_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    retain_until = table.Column<DateOnly>(type: "date", nullable: true),
                    content_destroyed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disposition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    disposition_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    disposition_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_controlled_documents", x => x.id);
                    table.ForeignKey(
                        name: "fk_controlled_documents_controlled_documents_parent_document_id",
                        column: x => x.parent_document_id,
                        principalSchema: "dms",
                        principalTable: "controlled_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_controlled_documents_departments_department_id",
                        column: x => x.department_id,
                        principalSchema: "dms",
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_controlled_documents_document_templates_template_id",
                        column: x => x.template_id,
                        principalSchema: "dms",
                        principalTable: "document_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_controlled_documents_document_types_document_type_id",
                        column: x => x.document_type_id,
                        principalSchema: "dms",
                        principalTable: "document_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_controlled_documents_sites_site_id",
                        column: x => x.site_id,
                        principalSchema: "dms",
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_role_assignments",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_role_assignments", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_role_assignments_departments_department_id",
                        column: x => x.department_id,
                        principalSchema: "dms",
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_role_assignments_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "dms",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_role_assignments_sites_site_id",
                        column: x => x.site_id,
                        principalSchema: "dms",
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_role_assignments_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "dms",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workflow_step_definitions",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_order = table.Column<int>(type: "integer", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    step_label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_step_definitions", x => x.id);
                    table.ForeignKey(
                        name: "fk_workflow_step_definitions_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "dms",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_workflow_step_definitions_workflow_definitions_workflow_def~",
                        column: x => x.workflow_definition_id,
                        principalSchema: "dms",
                        principalTable: "workflow_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_distributions",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    copy_number = table.Column<int>(type: "integer", nullable: false),
                    copy_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    issued_to_department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    issued_to_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    issued_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    print_limit = table.Column<int>(type: "integer", nullable: true),
                    print_count = table.Column<int>(type: "integer", nullable: false),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acknowledged_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    returned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    returned_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    closure_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_distributions", x => x.id);
                    table.ForeignKey(
                        name: "fk_document_distributions_controlled_documents_document_id",
                        column: x => x.document_id,
                        principalSchema: "dms",
                        principalTable: "controlled_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_document_distributions_departments_issued_to_department_id",
                        column: x => x.issued_to_department_id,
                        principalSchema: "dms",
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "editing_sessions",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    session_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    closure_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    save_count = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_editing_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_editing_sessions_controlled_documents_document_id",
                        column: x => x.document_id,
                        principalSchema: "dms",
                        principalTable: "controlled_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "signature_requests",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_order = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    step_label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_signature_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_signature_requests_controlled_documents_document_id",
                        column: x => x.document_id,
                        principalSchema: "dms",
                        principalTable: "controlled_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_signature_requests_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "dms",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "print_events",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    distribution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    print_sequence = table.Column<int>(type: "integer", nullable: false),
                    printed_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    watermark = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    printed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_print_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_print_events_document_distributions_distribution_id",
                        column: x => x.distribution_id,
                        principalSchema: "dms",
                        principalTable: "document_distributions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "electronic_signatures",
                schema: "dms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    signature_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    full_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    department = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    designation = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    meaning = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    signed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_electronic_signatures", x => x.id);
                    table.ForeignKey(
                        name: "fk_electronic_signatures_controlled_documents_document_id",
                        column: x => x.document_id,
                        principalSchema: "dms",
                        principalTable: "controlled_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_electronic_signatures_signature_requests_signature_request_~",
                        column: x => x.signature_request_id,
                        principalSchema: "dms",
                        principalTable: "signature_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_electronic_signatures_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "dms",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_action_signatures_pending_action_id",
                schema: "dms",
                table: "action_signatures",
                column: "pending_action_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_actor",
                schema: "dms",
                table: "audit_events",
                column: "actor");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_entity_occurred",
                schema: "dms",
                table: "audit_events",
                columns: new[] { "entity_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_occurred",
                schema: "dms",
                table: "audit_events",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "ix_controlled_documents_current",
                schema: "dms",
                table: "controlled_documents",
                column: "is_current_revision");

            migrationBuilder.CreateIndex(
                name: "ix_controlled_documents_department_status",
                schema: "dms",
                table: "controlled_documents",
                columns: new[] { "department_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_controlled_documents_disposition_due",
                schema: "dms",
                table: "controlled_documents",
                column: "retain_until",
                filter: "retain_until IS NOT NULL AND disposition IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_controlled_documents_next_review",
                schema: "dms",
                table: "controlled_documents",
                column: "next_review_date",
                filter: "next_review_date IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_controlled_documents_parent_annexure",
                schema: "dms",
                table: "controlled_documents",
                columns: new[] { "parent_document_id", "annexure_number" });

            migrationBuilder.CreateIndex(
                name: "ix_controlled_documents_site_id",
                schema: "dms",
                table: "controlled_documents",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "ix_controlled_documents_template_id",
                schema: "dms",
                table: "controlled_documents",
                column: "template_id");

            migrationBuilder.CreateIndex(
                name: "ux_controlled_documents_family_revision",
                schema: "dms",
                table: "controlled_documents",
                columns: new[] { "family_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_controlled_documents_number_revision",
                schema: "dms",
                table: "controlled_documents",
                columns: new[] { "document_number", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_controlled_documents_one_current_per_family",
                schema: "dms",
                table: "controlled_documents",
                column: "family_id",
                unique: true,
                filter: "is_current_revision = true");

            migrationBuilder.CreateIndex(
                name: "ux_controlled_documents_type_title_current",
                schema: "dms",
                table: "controlled_documents",
                columns: new[] { "document_type_id", "title" },
                unique: true,
                filter: "is_current_revision = true");

            migrationBuilder.CreateIndex(
                name: "ux_departments_site_code",
                schema: "dms",
                table: "departments",
                columns: new[] { "site_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_distributions_issued_to_department_id",
                schema: "dms",
                table: "document_distributions",
                column: "issued_to_department_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_distributions_status",
                schema: "dms",
                table: "document_distributions",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_document_distributions_copy_number",
                schema: "dms",
                table: "document_distributions",
                columns: new[] { "document_id", "copy_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_document_number_sequences_scope",
                schema: "dms",
                table: "document_number_sequences",
                columns: new[] { "site_id", "department_id", "document_type_id", "period_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_templates_type_status",
                schema: "dms",
                table: "document_templates",
                columns: new[] { "document_type_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_document_templates_one_active_per_type",
                schema: "dms",
                table: "document_templates",
                column: "document_type_id",
                unique: true,
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ux_document_templates_type_version",
                schema: "dms",
                table: "document_templates",
                columns: new[] { "document_type_id", "template_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_types_is_active",
                schema: "dms",
                table: "document_types",
                column: "is_active");

            migrationBuilder.CreateIndex(
                name: "ux_document_types_code",
                schema: "dms",
                table: "document_types",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_editing_sessions_key",
                schema: "dms",
                table: "editing_sessions",
                column: "session_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_editing_sessions_one_active_per_document",
                schema: "dms",
                table: "editing_sessions",
                column: "document_id",
                unique: true,
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_electronic_signatures_document_signed",
                schema: "dms",
                table: "electronic_signatures",
                columns: new[] { "document_id", "signed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_electronic_signatures_user_id",
                schema: "dms",
                table: "electronic_signatures",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_electronic_signatures_request",
                schema: "dms",
                table: "electronic_signatures",
                column: "signature_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_metadata_field_definitions_type_tag",
                schema: "dms",
                table: "metadata_field_definitions",
                columns: new[] { "document_type_id", "tag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_rules_document_type_id",
                schema: "dms",
                table: "notification_rules",
                column: "document_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_rules_kind_enabled",
                schema: "dms",
                table: "notification_rules",
                columns: new[] { "kind", "is_enabled" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_rules_recipient_role_id",
                schema: "dms",
                table: "notification_rules",
                column: "recipient_role_id");

            migrationBuilder.CreateIndex(
                name: "ux_notification_rules_scope",
                schema: "dms",
                table: "notification_rules",
                columns: new[] { "kind", "document_type_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_notifications_pending",
                schema: "dms",
                table: "notifications",
                column: "status",
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_recipient_created",
                schema: "dms",
                table: "notifications",
                columns: new[] { "recipient_user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_notifications_dedupe_key",
                schema: "dms",
                table: "notifications",
                column: "dedupe_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_numbering_rules_site_id",
                schema: "dms",
                table: "numbering_rules",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "ux_numbering_rules_scope",
                schema: "dms",
                table: "numbering_rules",
                columns: new[] { "document_type_id", "site_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_pending_actions_status_created",
                schema: "dms",
                table: "pending_actions",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_pending_actions_subject",
                schema: "dms",
                table: "pending_actions",
                columns: new[] { "subject_type", "subject_id" });

            migrationBuilder.CreateIndex(
                name: "ix_print_events_document_printed",
                schema: "dms",
                table: "print_events",
                columns: new[] { "document_id", "printed_at" });

            migrationBuilder.CreateIndex(
                name: "ux_print_events_distribution_sequence",
                schema: "dms",
                table: "print_events",
                columns: new[] { "distribution_id", "print_sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_retention_policies_site_id",
                schema: "dms",
                table: "retention_policies",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "ux_retention_policies_scope",
                schema: "dms",
                table: "retention_policies",
                columns: new[] { "document_type_id", "site_id", "trigger" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_review_policies_site_id",
                schema: "dms",
                table: "review_policies",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "ux_review_policies_scope",
                schema: "dms",
                table: "review_policies",
                columns: new[] { "document_type_id", "site_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ux_role_permissions_role_permission",
                schema: "dms",
                table: "role_permissions",
                columns: new[] { "role_id", "permission" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_roles_code",
                schema: "dms",
                table: "roles",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_scheduled_job_runs_job_started",
                schema: "dms",
                table: "scheduled_job_runs",
                columns: new[] { "job_name", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_signature_requests_status",
                schema: "dms",
                table: "signature_requests",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_signature_requests_user_status",
                schema: "dms",
                table: "signature_requests",
                columns: new[] { "user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_signature_requests_document_step",
                schema: "dms",
                table: "signature_requests",
                columns: new[] { "document_id", "step_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_sites_code",
                schema: "dms",
                table: "sites",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_role_assignments_department_id",
                schema: "dms",
                table: "user_role_assignments",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_role_assignments_role_id",
                schema: "dms",
                table: "user_role_assignments",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_role_assignments_site_id",
                schema: "dms",
                table: "user_role_assignments",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "ux_user_role_assignment_scope",
                schema: "dms",
                table: "user_role_assignments",
                columns: new[] { "user_id", "role_id", "site_id", "department_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ux_users_user_name",
                schema: "dms",
                table: "users",
                column: "user_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workflow_definitions_site_id",
                schema: "dms",
                table: "workflow_definitions",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "ux_workflow_definitions_one_active_per_scope",
                schema: "dms",
                table: "workflow_definitions",
                columns: new[] { "document_type_id", "site_id" },
                unique: true,
                filter: "is_active = true")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_workflow_step_definitions_role_id",
                schema: "dms",
                table: "workflow_step_definitions",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ux_workflow_step_definitions_order",
                schema: "dms",
                table: "workflow_step_definitions",
                columns: new[] { "workflow_definition_id", "step_order" },
                unique: true);
        
            // Append-only enforcement for the audit trail and signatures. Applied here rather
            // than left to the application, because a guard that lives only in the app is one
            // that a direct database connection walks straight past.
            migrationBuilder.Sql(System.IO.File.ReadAllText(System.IO.Path.Combine(
                System.AppContext.BaseDirectory, "Persistence", "Migrations", "AuditImmutability.sql")));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "action_signatures",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "document_number_sequences",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "document_status_stamps",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "editing_sessions",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "electronic_signatures",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "metadata_field_definitions",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "notification_rules",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "notifications",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "numbering_rules",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "password_policies",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "print_events",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "retention_policies",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "review_policies",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "role_permissions",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "scheduled_job_runs",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "signature_policies",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "user_role_assignments",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "workflow_step_definitions",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "pending_actions",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "signature_requests",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "document_distributions",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "roles",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "workflow_definitions",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "users",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "controlled_documents",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "departments",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "document_templates",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "sites",
                schema: "dms");

            migrationBuilder.DropTable(
                name: "document_types",
                schema: "dms");
        }
    }
}
