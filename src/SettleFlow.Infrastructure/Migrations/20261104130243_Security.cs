using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SettleFlow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Security : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_IdempotencyKeys",
                table: "IdempotencyKeys");

            migrationBuilder.AddColumn<string>(
                name: "MerchantId",
                table: "IdempotencyKeys",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            // Backfill existing keys with the merchant of the payment they created.
            migrationBuilder.Sql("""
                UPDATE "IdempotencyKeys" k SET "MerchantId" = p."MerchantId"
                FROM "Payments" p WHERE p."Id" = k."PaymentId";
                """);

            migrationBuilder.AddPrimaryKey(
                name: "PK_IdempotencyKeys",
                table: "IdempotencyKeys",
                columns: new[] { "MerchantId", "Key" });

            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    ActorType = table.Column<string>(type: "text", nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetType = table.Column<string>(type: "text", nullable: true),
                    TargetId = table.Column<string>(type: "text", nullable: true),
                    Details = table.Column<string>(type: "text", nullable: true),
                    IpAddress = table.Column<string>(type: "text", nullable: true),
                    TraceId = table.Column<string>(type: "text", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Merchants",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ApiKeyHash = table.Column<string>(type: "text", nullable: false),
                    ApiKeyPrefix = table.Column<string>(type: "text", nullable: false),
                    Disabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    KeyRotatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Merchants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Disabled = table.Column<bool>(type: "boolean", nullable: false),
                    FailedLoginCount = table.Column<int>(type: "integer", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyKeys_CreatedAt",
                table: "IdempotencyKeys",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_Actor_OccurredAt",
                table: "AuditLog",
                columns: new[] { "Actor", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_OccurredAt",
                table: "AuditLog",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_Merchants_ApiKeyHash",
                table: "Merchants",
                column: "ApiKeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);

            // The audit log is append-only: reject any modification at the database level.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION settleflow_audit_append_only() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'AuditLog is append-only (% rejected)', TG_OP;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER audit_log_no_update_delete
                    BEFORE UPDATE OR DELETE ON "AuditLog"
                    FOR EACH ROW EXECUTE FUNCTION settleflow_audit_append_only();

                CREATE TRIGGER audit_log_no_truncate
                    BEFORE TRUNCATE ON "AuditLog"
                    FOR EACH STATEMENT EXECUTE FUNCTION settleflow_audit_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS audit_log_no_truncate ON "AuditLog";
                DROP TRIGGER IF EXISTS audit_log_no_update_delete ON "AuditLog";
                DROP FUNCTION IF EXISTS settleflow_audit_append_only();
                """);

            migrationBuilder.DropTable(
                name: "AuditLog");

            migrationBuilder.DropTable(
                name: "Merchants");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropPrimaryKey(
                name: "PK_IdempotencyKeys",
                table: "IdempotencyKeys");

            migrationBuilder.DropIndex(
                name: "IX_IdempotencyKeys_CreatedAt",
                table: "IdempotencyKeys");

            migrationBuilder.DropColumn(
                name: "MerchantId",
                table: "IdempotencyKeys");

            migrationBuilder.AddPrimaryKey(
                name: "PK_IdempotencyKeys",
                table: "IdempotencyKeys",
                column: "Key");
        }
    }
}
