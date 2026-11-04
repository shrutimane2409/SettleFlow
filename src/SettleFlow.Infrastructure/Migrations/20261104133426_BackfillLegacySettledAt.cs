using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SettleFlow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BackfillLegacySettledAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Payments settled before SettledAt existed (pre phase 2) were posted to the ledger by the previous
            // migration but would never be paid out, because payouts select by SettledAt. Use the last update
            // time as the settlement time, and record the captured amount explicitly.
            migrationBuilder.Sql("""
                UPDATE "Payments"
                SET "SettledAt" = "UpdatedAt",
                    "CapturedAmount" = COALESCE("CapturedAmount", "Amount")
                WHERE "SettledAt" IS NULL AND "Status" IN ('Settled', 'PartiallyRefunded', 'Refunded');
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
