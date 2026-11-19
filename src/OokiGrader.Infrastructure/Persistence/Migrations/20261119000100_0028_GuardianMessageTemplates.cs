using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OokiGrader.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OokiGraderDbContext))]
[Migration("20261119000100_0028_GuardianMessageTemplates")]
public sealed class GuardianMessageTemplates : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE guardian_message_templates (
                id TEXT NOT NULL PRIMARY KEY,
                pass TEXT NOT NULL,
                fail TEXT NOT NULL,
                unconfigured TEXT NOT NULL,
                revision INTEGER NOT NULL,
                CONSTRAINT ck_guardian_message_templates_singleton CHECK (id = 'school-manager')
            );
            ALTER TABLE guardian_delivery ADD COLUMN result_subject TEXT NULL;
            ALTER TABLE guardian_delivery ADD COLUMN result_title TEXT NULL;
            ALTER TABLE guardian_delivery ADD COLUMN result_verdict TEXT NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
            ALTER TABLE guardian_delivery DROP COLUMN result_subject;
            ALTER TABLE guardian_delivery DROP COLUMN result_title;
            ALTER TABLE guardian_delivery DROP COLUMN result_verdict;
            DROP TABLE guardian_message_templates;
            """);
}
