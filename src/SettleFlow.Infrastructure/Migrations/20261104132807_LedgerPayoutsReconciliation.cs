using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SettleFlow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LedgerPayoutsReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PayoutId",
                table: "Refunds",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FeeAmount",
                table: "Payments",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PayoutId",
                table: "Payments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FeeBps",
                table: "Merchants",
                type: "integer",
                nullable: false,
                defaultValue: 150);

            migrationBuilder.AddColumn<decimal>(
                name: "FeeFixed",
                table: "Merchants",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0.25m);

            migrationBuilder.CreateTable(
                name: "LedgerAccounts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Owner = table.Column<string>(type: "text", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LedgerTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reference = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerTransactions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Payouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MerchantId = table.Column<string>(type: "text", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    GrossAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    FeeAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RefundAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    NetAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PaymentCount = table.Column<int>(type: "integer", nullable: false),
                    RefundCount = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reference = table.Column<string>(type: "text", nullable: false),
                    PeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PaidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payouts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TriggeredBy = table.Column<string>(type: "text", nullable: false),
                    Matched = table.Column<int>(type: "integer", nullable: false),
                    Discrepancies = table.Column<int>(type: "integer", nullable: false),
                    LedgerBalanced = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LedgerEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<string>(type: "character varying(160)", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LedgerEntries_LedgerAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "LedgerAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LedgerEntries_LedgerTransactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "LedgerTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Operation = table.Column<string>(type: "text", nullable: false),
                    Reference = table.Column<Guid>(type: "uuid", nullable: true),
                    MerchantId = table.Column<string>(type: "text", nullable: true),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    SettleFlowAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    AcquirerAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Details = table.Column<string>(type: "text", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolvedBy = table.Column<string>(type: "text", nullable: true),
                    ResolutionNote = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReconciliationItems_ReconciliationRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "ReconciliationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_CompletedAt",
                table: "Refunds",
                column: "CompletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_PayoutId",
                table: "Refunds",
                column: "PayoutId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_MerchantId_Currency_SettledAt",
                table: "Payments",
                columns: new[] { "MerchantId", "Currency", "SettledAt" },
                filter: "\"PayoutId\" IS NULL AND \"SettledAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_PayoutId",
                table: "Payments",
                column: "PayoutId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_SettledAt",
                table: "Payments",
                column: "SettledAt");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_AccountId",
                table: "LedgerEntries",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_TransactionId",
                table: "LedgerEntries",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerTransactions_OccurredAt",
                table: "LedgerTransactions",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerTransactions_Type_Reference",
                table: "LedgerTransactions",
                columns: new[] { "Type", "Reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payouts_MerchantId_CreatedAt",
                table: "Payouts",
                columns: new[] { "MerchantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Payouts_Reference",
                table: "Payouts",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationItems_RunId_Kind",
                table: "ReconciliationItems",
                columns: new[] { "RunId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationRuns_StartedAt",
                table: "ReconciliationRuns",
                column: "StartedAt");

            migrationBuilder.Sql("""
                -- Double-entry invariant, checked at COMMIT: every ledger transaction sums to zero.
                CREATE OR REPLACE FUNCTION settleflow_ledger_check_balanced() RETURNS trigger AS $$
                DECLARE total numeric;
                BEGIN
                    SELECT COALESCE(SUM("Amount"), 0) INTO total FROM "LedgerEntries" WHERE "TransactionId" = NEW."TransactionId";
                    IF total <> 0 THEN
                        RAISE EXCEPTION 'Ledger transaction % is unbalanced (entries sum to %)', NEW."TransactionId", total;
                    END IF;
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;

                CREATE CONSTRAINT TRIGGER ledger_entries_balanced
                    AFTER INSERT ON "LedgerEntries"
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION settleflow_ledger_check_balanced();

                -- The ledger is append-only: corrections are new (reversing) transactions.
                CREATE OR REPLACE FUNCTION settleflow_ledger_append_only() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'The ledger is append-only (% on % rejected)', TG_OP, TG_TABLE_NAME;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER ledger_entries_no_change BEFORE UPDATE OR DELETE ON "LedgerEntries"
                    FOR EACH ROW EXECUTE FUNCTION settleflow_ledger_append_only();
                CREATE TRIGGER ledger_transactions_no_change BEFORE UPDATE OR DELETE ON "LedgerTransactions"
                    FOR EACH ROW EXECUTE FUNCTION settleflow_ledger_append_only();
                CREATE TRIGGER ledger_no_truncate BEFORE TRUNCATE ON "LedgerEntries"
                    FOR EACH STATEMENT EXECUTE FUNCTION settleflow_ledger_append_only();

                -- Backfill: post payments settled (and refunds completed) before the ledger existed.
                -- Fees were not charged then, so the whole amount goes to the merchant.
                INSERT INTO "LedgerAccounts" ("Id", "Kind", "Owner", "Currency", "CreatedAt")
                SELECT DISTINCT a.id, a.kind, a.owner, a.ccy, now() FROM (
                    SELECT 'acquirer:clearing:' || "Currency" AS id, 'acquirer_clearing' AS kind, 'acquirer' AS owner, "Currency" AS ccy
                    FROM "Payments" WHERE "SettledAt" IS NOT NULL OR "Status" IN ('Settled','PartiallyRefunded','Refunded')
                    UNION
                    SELECT 'merchant:' || "MerchantId" || ':balance:' || "Currency", 'merchant_balance', "MerchantId", "Currency"
                    FROM "Payments" WHERE "SettledAt" IS NOT NULL OR "Status" IN ('Settled','PartiallyRefunded','Refunded')
                ) a ON CONFLICT DO NOTHING;

                CREATE TEMP TABLE _backfill ON COMMIT DROP AS
                SELECT gen_random_uuid() AS tx, p."Id" AS ref, p."Currency" AS ccy, p."MerchantId" AS merchant,
                       COALESCE(p."CapturedAmount", p."Amount") AS amount
                FROM "Payments" p
                WHERE p."Status" IN ('Settled','PartiallyRefunded','Refunded');

                INSERT INTO "LedgerTransactions" ("Id", "Type", "Reference", "Currency", "Description", "OccurredAt")
                SELECT tx, 'payment_settled', ref, ccy, 'Backfilled settlement (pre-ledger, no fee)', now() FROM _backfill;
                INSERT INTO "LedgerEntries" ("TransactionId", "AccountId", "Amount")
                SELECT tx, 'acquirer:clearing:' || ccy, amount FROM _backfill
                UNION ALL
                SELECT tx, 'merchant:' || merchant || ':balance:' || ccy, -amount FROM _backfill;

                CREATE TEMP TABLE _backfill_refunds ON COMMIT DROP AS
                SELECT gen_random_uuid() AS tx, r."Id" AS ref, r."Currency" AS ccy, r."MerchantId" AS merchant, r."Amount" AS amount
                FROM "Refunds" r WHERE r."Status" = 'Succeeded';

                INSERT INTO "LedgerTransactions" ("Id", "Type", "Reference", "Currency", "Description", "OccurredAt")
                SELECT tx, 'refund', ref, ccy, 'Backfilled refund (pre-ledger)', now() FROM _backfill_refunds;
                INSERT INTO "LedgerEntries" ("TransactionId", "AccountId", "Amount")
                SELECT tx, 'merchant:' || merchant || ':balance:' || ccy, amount FROM _backfill_refunds
                UNION ALL
                SELECT tx, 'acquirer:clearing:' || ccy, -amount FROM _backfill_refunds;

                UPDATE "Payments" SET "FeeAmount" = 0 WHERE "Status" IN ('Settled','PartiallyRefunded','Refunded') AND "FeeAmount" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS ledger_no_truncate ON "LedgerEntries";
                DROP TRIGGER IF EXISTS ledger_transactions_no_change ON "LedgerTransactions";
                DROP TRIGGER IF EXISTS ledger_entries_no_change ON "LedgerEntries";
                DROP TRIGGER IF EXISTS ledger_entries_balanced ON "LedgerEntries";
                DROP FUNCTION IF EXISTS settleflow_ledger_append_only();
                DROP FUNCTION IF EXISTS settleflow_ledger_check_balanced();
                """);

            migrationBuilder.DropTable(
                name: "LedgerEntries");

            migrationBuilder.DropTable(
                name: "Payouts");

            migrationBuilder.DropTable(
                name: "ReconciliationItems");

            migrationBuilder.DropTable(
                name: "LedgerAccounts");

            migrationBuilder.DropTable(
                name: "LedgerTransactions");

            migrationBuilder.DropTable(
                name: "ReconciliationRuns");

            migrationBuilder.DropIndex(
                name: "IX_Refunds_CompletedAt",
                table: "Refunds");

            migrationBuilder.DropIndex(
                name: "IX_Refunds_PayoutId",
                table: "Refunds");

            migrationBuilder.DropIndex(
                name: "IX_Payments_MerchantId_Currency_SettledAt",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_PayoutId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_SettledAt",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PayoutId",
                table: "Refunds");

            migrationBuilder.DropColumn(
                name: "FeeAmount",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PayoutId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "FeeBps",
                table: "Merchants");

            migrationBuilder.DropColumn(
                name: "FeeFixed",
                table: "Merchants");
        }
    }
}
