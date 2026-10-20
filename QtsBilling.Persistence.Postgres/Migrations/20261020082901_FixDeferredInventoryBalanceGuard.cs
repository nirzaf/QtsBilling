using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class FixDeferredInventoryBalanceGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A deferred trigger fires with the snapshot of the event that queued it, so a balance row
            // that was created and then updated in the same transaction was judged by the value it had
            // when it was inserted (zero) instead of the value it ended with. The guard now reads the
            // current persisted state, which is the state the commit actually establishes
            // (specification D04, finding F17).
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_inventory_balance_guard() RETURNS trigger AS $$
                DECLARE
                    v_sum      numeric(18,6);
                    v_onhand   numeric(18,6);
                    v_business uuid;
                    v_location uuid;
                    v_variant  uuid;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        v_sum := qts_check_inventory_movement_sum(OLD.business_id, OLD.location_id, OLD.variant_id);
                        IF v_sum <> 0 THEN
                            RAISE EXCEPTION 'Inventory balance for business %, location %, variant % cannot be deleted: movements still total %.',
                                OLD.business_id, OLD.location_id, OLD.variant_id, v_sum
                                USING ERRCODE = 'check_violation';
                        END IF;

                        RETURN NULL;
                    END IF;

                    v_business := NEW.business_id;
                    v_location := NEW.location_id;
                    v_variant := NEW.variant_id;

                    -- The current persisted state, not the snapshot of this event.
                    SELECT b.on_hand
                      INTO v_onhand
                      FROM inventory_balances b
                     WHERE b.business_id = v_business
                       AND b.location_id = v_location
                       AND b.variant_id = v_variant;

                    IF NOT FOUND THEN
                        v_onhand := 0;
                    END IF;

                    v_sum := qts_check_inventory_movement_sum(v_business, v_location, v_variant);

                    IF v_onhand <> v_sum THEN
                        RAISE EXCEPTION 'Inventory balance for business %, location %, variant % is %, but movements total % (balances are movement-derived).',
                            v_business, v_location, v_variant, v_onhand, v_sum
                            USING ERRCODE = 'check_violation';
                    END IF;

                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The previous guard compared the snapshot of the trigger event, which judged a balance row
            // by an intermediate value. Restoring it would reintroduce that defect.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_inventory_balance_guard() RETURNS trigger AS $$
                DECLARE
                    v_sum   numeric(18,6);
                    v_row   inventory_balances%ROWTYPE;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        v_sum := qts_check_inventory_movement_sum(OLD.business_id, OLD.location_id, OLD.variant_id);
                        IF v_sum <> 0 THEN
                            RAISE EXCEPTION 'Inventory balance for business %, location %, variant % cannot be deleted: movements still total %.',
                                OLD.business_id, OLD.location_id, OLD.variant_id, v_sum
                                USING ERRCODE = 'check_violation';
                        END IF;

                        RETURN NULL;
                    END IF;

                    v_row := NEW;
                    v_sum := qts_check_inventory_movement_sum(v_row.business_id, v_row.location_id, v_row.variant_id);

                    IF v_row.on_hand <> v_sum THEN
                        RAISE EXCEPTION 'Inventory balance for business %, location %, variant % is %, but movements total % (balances are movement-derived).',
                            v_row.business_id, v_row.location_id, v_row.variant_id, v_row.on_hand, v_sum
                            USING ERRCODE = 'check_violation';
                    END IF;

                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;
                """);
        }
    }
}
