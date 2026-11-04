using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SettleFlow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FraudPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ChargebackAmount",
                table: "Payments",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<double>(
                name: "RiskModelScore",
                table: "Payments",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RiskRuleSetVersion",
                table: "Payments",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Blocklist",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Value = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Blocklist", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Chargebacks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    MerchantId = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Fee = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    ReasonCode = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Chargebacks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FraudRuleSets",
                columns: table => new
                {
                    Version = table.Column<int>(type: "integer", nullable: false),
                    RulesJson = table.Column<string>(type: "jsonb", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Comment = table.Column<string>(type: "text", nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FraudRuleSets", x => x.Version);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Blocklist_Type_Value",
                table: "Blocklist",
                columns: new[] { "Type", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Chargebacks_CreatedAt",
                table: "Chargebacks",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Chargebacks_PaymentId",
                table: "Chargebacks",
                column: "PaymentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FraudRuleSets_Active",
                table: "FraudRuleSets",
                column: "Active",
                unique: true,
                filter: "\"Active\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Blocklist");

            migrationBuilder.DropTable(
                name: "Chargebacks");

            migrationBuilder.DropTable(
                name: "FraudRuleSets");

            migrationBuilder.DropColumn(
                name: "ChargebackAmount",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "RiskModelScore",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "RiskRuleSetVersion",
                table: "Payments");
        }
    }
}
