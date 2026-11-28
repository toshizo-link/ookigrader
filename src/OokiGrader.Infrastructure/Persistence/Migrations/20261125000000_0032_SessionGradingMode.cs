using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OokiGrader.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OokiGraderDbContext))]
[Migration("20261125000000_0032_SessionGradingMode")]
public sealed class SessionGradingMode : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "grading_mode", table: "test_session",
            type: "TEXT", maxLength: 16, nullable: false, defaultValue: "standard");
        // Null denotes a request from before per-test mode selection.
        migrationBuilder.AddColumn<string>(name: "grading_mode", table: "ai_request",
            type: "TEXT", maxLength: 16, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE ai_request DROP COLUMN grading_mode;");
        migrationBuilder.Sql("ALTER TABLE test_session DROP COLUMN grading_mode;");
    }
}
