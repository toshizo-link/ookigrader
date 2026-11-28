using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OokiGrader.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OokiGraderDbContext))]
[Migration("20261125000100_0033_ProviderManagedBatchOutput")]
public sealed class _0033_ProviderManagedBatchOutput : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        _0023_ConfigurableGeminiModel.RebuildAiBatchTable(migrationBuilder, constrainModel: false, allowProviderManagedOutput: true);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The old host can report that the output was not deleted and retain its
        // file reference and recorded reason.
        migrationBuilder.Sql("UPDATE ai_batch SET cleanup_state = 'failed' WHERE cleanup_state = 'provider_managed_output';");
        _0023_ConfigurableGeminiModel.RebuildAiBatchTable(migrationBuilder, constrainModel: false);
    }
}
