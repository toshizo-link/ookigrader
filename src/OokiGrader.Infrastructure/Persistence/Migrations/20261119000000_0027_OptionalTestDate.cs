using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OokiGrader.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OokiGraderDbContext))]
[Migration("20261119000000_0027_OptionalTestDate")]
public sealed class OptionalTestDate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        // Change the column in place to retain independent triggers and referencing tables.
        migrationBuilder.Sql("""
            DROP INDEX IX_test_session_test_date_state;
            ALTER TABLE test_session RENAME COLUMN test_date TO prior_test_date;
            ALTER TABLE test_session ADD COLUMN test_date TEXT;
            UPDATE test_session SET test_date = prior_test_date;
            ALTER TABLE test_session DROP COLUMN prior_test_date;
            CREATE INDEX IX_test_session_test_date_state ON test_session(test_date DESC, state);
            """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // The previous schema requires a real date on every session. If an undated
        // session exists, the NOT NULL check rolls back this migration atomically.
        migrationBuilder.Sql("""
            DROP INDEX IX_test_session_test_date_state;
            ALTER TABLE test_session RENAME COLUMN test_date TO prior_test_date;
            ALTER TABLE test_session ADD COLUMN test_date TEXT NOT NULL DEFAULT '0001-01-01';
            UPDATE test_session SET test_date = prior_test_date;
            ALTER TABLE test_session DROP COLUMN prior_test_date;
            CREATE INDEX IX_test_session_test_date_state ON test_session(test_date DESC, state);
            """);
}
