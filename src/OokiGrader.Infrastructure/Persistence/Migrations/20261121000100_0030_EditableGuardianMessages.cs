using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OokiGrader.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OokiGraderDbContext))]
[Migration("20261121000100_0030_EditableGuardianMessages")]
public sealed class EditableGuardianMessages : Migration
{
    // Freeze both versions of the presets here so later wording changes cannot alter an upgrade.
    private const string LegacyHeader = "教科　＜教科＞\nテスト名　＜タイトル＞\n判定　＜結果＞";
    private const string Introduction =
        "いつも大変お世話になっております。\n先日実施いたしました暗記テストの結果をお知らせいたします。\n\n" +
        "教科：＜教科＞\n範囲：＜タイトル＞\n結果：＜結果＞\n\n" +
        "答案をPDFで添付しておりますので、あわせてご確認ください。";
    private static readonly (string Column, string Old, string New)[] Presets =
    [
        ("pass", "合格おめでとうございます。詳しい採点結果は添付のPDFをご覧ください。",
            Introduction + "\nよく準備して臨むことができました。この調子で、次の範囲にも取り組んでいきましょう。ご家庭でもぜひ声をかけてあげてください。"),
        ("fail", "添付のPDFで間違えた問題を確認し、次のテストに向けて復習しましょう。",
            Introduction + "\n次回の通塾時に、本人の都合のよいタイミングで再テストを受けることができます。それまでに該当範囲をもう一度見直しておくよう、ご家庭でもお声がけいただけますと幸いです。"),
        ("unconfigured", "採点結果をお送りします。添付のPDFをご確認ください。", Introduction),
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE guardian_message_templates ADD COLUMN title TEXT NOT NULL DEFAULT '暗記テスト採点結果のお知らせ';");
        foreach (var (column, previous, current) in Presets)
        {
            migrationBuilder.Sql($"""
                UPDATE guardian_message_templates SET {column} = CASE {column}
                    WHEN '{previous}' THEN '{current}'
                    WHEN '' THEN '{LegacyHeader}'
                    ELSE '{LegacyHeader}' || char(10, 10) || {column} END;
                """);
        }
        migrationBuilder.Sql("UPDATE guardian_message_templates SET revision = revision + 1;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var (column, previous, current) in Presets)
        {
            migrationBuilder.Sql($"""
                UPDATE guardian_message_templates SET {column} = CASE
                    WHEN {column} = '{current}' THEN '{previous}'
                    WHEN {column} = '{LegacyHeader}' THEN ''
                    WHEN instr({column}, '{LegacyHeader}' || char(10, 10)) = 1
                        THEN substr({column}, length('{LegacyHeader}') + 3)
                    ELSE {column} END;
                """);
        }
        migrationBuilder.Sql("ALTER TABLE guardian_message_templates DROP COLUMN title;");
    }
}
