using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SettleFlow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RealtimeNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                -- Wake outbox publishers immediately instead of waiting for the next poll.
                CREATE OR REPLACE FUNCTION settleflow_notify_outbox() RETURNS trigger AS $$
                BEGIN
                    PERFORM pg_notify('settleflow_outbox', '');
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER outbox_notify AFTER INSERT ON "Outbox"
                    FOR EACH STATEMENT EXECUTE FUNCTION settleflow_notify_outbox();

                -- Live dashboard: announce payment changes (id/status/merchant only; clients fetch details).
                CREATE OR REPLACE FUNCTION settleflow_notify_payment() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'INSERT' OR NEW."Status" IS DISTINCT FROM OLD."Status" OR NEW."RefundedAmount" IS DISTINCT FROM OLD."RefundedAmount" THEN
                        PERFORM pg_notify('settleflow_payments', json_build_object(
                            'id', NEW."Id", 'status', NEW."Status", 'merchantId', NEW."MerchantId")::text);
                    END IF;
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER payments_notify AFTER INSERT OR UPDATE ON "Payments"
                    FOR EACH ROW EXECUTE FUNCTION settleflow_notify_payment();
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS payments_notify ON "Payments";
                DROP TRIGGER IF EXISTS outbox_notify ON "Outbox";
                DROP FUNCTION IF EXISTS settleflow_notify_payment();
                DROP FUNCTION IF EXISTS settleflow_notify_outbox();
                """);

        }
    }
}
