using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Infrastructure.Tests;

public sealed class EditableGuardianMessageMigrationTests
{
    [Fact]
    public async Task UpgradePreservesTeacherWordingAndUpdatesOnlyUnchangedPresets()
    {
        const string before = "20261119000100_0028_GuardianMessageTemplates";
        const string previousFail = "添付のPDFで間違えた問題を確認し、次のテストに向けて復習しましょう。";
        const string teacherText = "先生からの連絡です。\nWe've sent the result.";
        const string header = "教科　＜教科＞\nテスト名　＜タイトル＞\n判定　＜結果＞";
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.Factory.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(before);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO guardian_message_templates (id, pass, fail, unconfigured, revision)
            VALUES ('school-manager', {teacherText}, {previousFail}, '', 7)
            """);
        await migrator.MigrateAsync();
        var stored = await db.GuardianMessageTemplates.AsNoTracking().SingleAsync();
        var defaults = new GuardianMessageTemplatesEntity();
        Assert.Equal(defaults.Title, stored.Title);
        Assert.Equal(header + "\n\n" + teacherText, stored.Pass);
        Assert.Equal(defaults.Fail, stored.Fail);
        Assert.Equal(header, stored.Unconfigured);
        Assert.Equal(8, stored.Revision);

        await migrator.MigrateAsync(before);
        Assert.Equal(teacherText, await db.Database.SqlQueryRaw<string>(
            "SELECT pass AS Value FROM guardian_message_templates").SingleAsync());
        Assert.Equal(previousFail, await db.Database.SqlQueryRaw<string>(
            "SELECT fail AS Value FROM guardian_message_templates").SingleAsync());
        Assert.Equal("", await db.Database.SqlQueryRaw<string>(
            "SELECT unconfigured AS Value FROM guardian_message_templates").SingleAsync());
    }
}
