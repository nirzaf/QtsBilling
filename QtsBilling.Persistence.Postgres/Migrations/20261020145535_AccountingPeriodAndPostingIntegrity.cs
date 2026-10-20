using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QtsBilling.Persistence.Postgres.Migrations
{
    /// <summary>
    /// Accounting-period integrity (specification task 10, finding F20): periods cannot overlap, a
    /// closed period cannot be reopened by an ordinary DML statement, and closed dates/business are
    /// immutable. Posting and closing serialize on the period row in application code.
    /// </summary>
    public partial class AccountingPeriodAndPostingIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION qts_accounting_period_guard() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        IF OLD.status <> 'Open' THEN
                            RAISE EXCEPTION 'Accounting period % is % and cannot be deleted.', OLD.id, OLD.status
                                USING ERRCODE = 'check_violation';
                        END IF;

                        IF EXISTS (SELECT 1 FROM journal_entries WHERE accounting_period_id = OLD.id) THEN
                            RAISE EXCEPTION 'Accounting period % has posted entries and cannot be deleted.', OLD.id
                                USING ERRCODE = 'check_violation';
                        END IF;

                        RETURN OLD;
                    END IF;

                    IF NEW.start_date > NEW.end_date THEN
                        RAISE EXCEPTION 'Accounting period % starts after it ends.', NEW.id
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF TG_OP = 'UPDATE' THEN
                        IF OLD.status <> 'Open' AND NEW.status = 'Open' THEN
                            RAISE EXCEPTION 'Accounting period % is % and cannot be reopened by a data change; use the approved finance procedure.',
                                NEW.id, OLD.status
                                USING ERRCODE = 'check_violation';
                        END IF;

                        IF OLD.status <> 'Open' THEN
                            IF NEW.start_date IS DISTINCT FROM OLD.start_date
                               OR NEW.end_date IS DISTINCT FROM OLD.end_date
                               OR NEW.business_id IS DISTINCT FROM OLD.business_id
                            THEN
                                RAISE EXCEPTION 'Accounting period % is %; its dates are immutable.', NEW.id, OLD.status
                                    USING ERRCODE = 'check_violation';
                            END IF;
                        END IF;
                    END IF;

                    IF EXISTS (
                        SELECT 1
                          FROM accounting_periods p
                         WHERE p.business_id = NEW.business_id
                           AND p.id <> NEW.id
                           AND daterange(p.start_date, p.end_date, '[]') && daterange(NEW.start_date, NEW.end_date, '[]')
                    ) THEN
                        RAISE EXCEPTION 'Accounting period % overlaps an existing period of the same business.', NEW.id
                            USING ERRCODE = 'check_violation';
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_accounting_periods_integrity
                    BEFORE INSERT OR UPDATE OR DELETE ON accounting_periods
                    FOR EACH ROW EXECUTE FUNCTION qts_accounting_period_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_accounting_periods_integrity ON accounting_periods;
                DROP FUNCTION IF EXISTS qts_accounting_period_guard();
                """);
        }
    }
}
