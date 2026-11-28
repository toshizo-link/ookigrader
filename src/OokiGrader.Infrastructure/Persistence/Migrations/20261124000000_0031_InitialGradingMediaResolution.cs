using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OokiGrader.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OokiGraderDbContext))]
[Migration("20261124000000_0031_InitialGradingMediaResolution")]
public sealed class InitialGradingMediaResolution : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "initial_grading_media_resolution",
            table: "ai_connection",
            type: "TEXT",
            maxLength: 32,
            nullable: false,
            defaultValue: "medium");
        migrationBuilder.Sql("""
            UPDATE ai_connection SET initial_grading_media_resolution = 'high'
            WHERE provider = 'geminiDirect' AND EXISTS (
                SELECT 1 FROM ai_task_profile
                WHERE ai_connection_id = ai_connection.id AND task_type = 'initialGrading'
                    AND active = 1 AND media_resolution = 'high'
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE ai_connection DROP COLUMN initial_grading_media_resolution;");
    }
}
