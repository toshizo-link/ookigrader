using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OokiGrader.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OokiGraderDbContext))]
[Migration("20261125000200_0034_RetireAutomaticAdjudication")]
public sealed class RetireAutomaticAdjudication : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Match the old worker's crash recovery. A dispatch with an unknown
        // outcome consumes its reservation; a request still waiting to run releases it.
        migrationBuilder.Sql("""
            UPDATE ai_budget_reservation AS b
            SET state = CASE WHEN a.state = 'dispatching' OR a.possible_duplicate = 1 THEN 'settled' ELSE 'released' END,
                actual_usd_micros = CASE WHEN a.state = 'dispatching' OR a.possible_duplicate = 1 THEN b.reserved_usd_micros ELSE 0 END,
                settled_at = unixepoch() * 1000
            FROM ai_request AS a
            WHERE b.ai_request_id = a.id AND b.state = 'reserved'
                AND a.purpose = 'adjudication' AND a.entity_type = 'questionResult'
                AND a.state IN ('prepared','retry_waiting','dispatching');

            UPDATE ai_request
            SET state = CASE WHEN state = 'dispatching' OR possible_duplicate = 1 THEN 'failed' ELSE 'cancelled' END,
                possible_duplicate = CASE WHEN state = 'dispatching' THEN 1 ELSE possible_duplicate END,
                error_code = CASE WHEN state = 'dispatching' OR possible_duplicate = 1
                    THEN 'ai_adjudication_dispatch_outcome_unknown' ELSE 'automatic_adjudication_retired' END,
                completed_at = unixepoch() * 1000, updated_at = unixepoch() * 1000, revision = revision + 1
            WHERE purpose = 'adjudication' AND entity_type = 'questionResult'
                AND state IN ('prepared','retry_waiting','dispatching');

            UPDATE background_job
            SET state = 'cancelled', error_code = 'automatic_adjudication_retired', safe_error_detail = NULL,
                lease_owner = NULL, lease_expires_at = NULL, completed_at = unixepoch() * 1000,
                updated_at = unixepoch() * 1000, revision = revision + 1
            WHERE type = 'gemini_answer_adjudication' AND state IN ('queued','leased','blocked','retry_waiting');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Reviving old paid requests could duplicate a charge or overwrite teacher work.
    }
}
