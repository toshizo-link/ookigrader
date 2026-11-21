using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.SchoolManager;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.IntegrationTests;

public sealed partial class SchoolManagerAutomationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsentResultRebuildsTheSameDeliveryWhenCriteriaChangeBeforeRendering(bool deliveryObservedFailure)
    {
        await using var fixture = await AutomationFixture.CreateAsync();
        await fixture.ConfigureAsync(dryRun: false);
        await fixture.SeedFinalizedResultAsync();
        Assert.True(await fixture.ResultPlanner.ProcessNextAsync());
        await using var db = await fixture.CreateDbContextAsync();
        var original = await db.GuardianDeliveries.AsNoTracking().SingleAsync();
        var session = await db.TestSessions.SingleAsync();
        session.TestDate = null;
        session.MaximumMistakesToPass = 0;
        await db.SaveChangesAsync();
        Assert.True(await fixture.PdfWorker.ProcessNextAsync());
        var failedExport = await db.ExportRecords.AsNoTracking().SingleAsync();
        Assert.Equal("export_source_changed", failedExport.ErrorCode);
        if (deliveryObservedFailure)
            Assert.True(await fixture.DeliveryProcessor.ProcessNextAsync());

        Assert.True(await fixture.ResultPlanner.ProcessNextAsync());
        Assert.True(await fixture.PdfWorker.ProcessNextAsync());
        Assert.True(await fixture.DeliveryProcessor.ProcessNextAsync());
        Assert.True(await fixture.DeliveryProcessor.ProcessNextAsync());
        Assert.False(await fixture.Worker.ProcessCycleAsync());
        var sent = await db.GuardianDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal(original.Id, sent.Id);
        Assert.NotEqual(original.ExportRecordId, sent.ExportRecordId);
        Assert.Equal("sent", sent.State);
        Assert.Equal(1, sent.AttemptCount);
        var request = Assert.Single(fixture.Client.Requests);
        Assert.Contains("結果：合格", request.Body);
        var verified = await db.ExportRecords.AsNoTracking().SingleAsync(record => record.State == "verified");
        Assert.Equal(sent.ExportRecordId, verified.Id);
        Assert.Equal(verified.Sha256,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(request.AttachmentBytes)));
    }

    [Theory]
    [InlineData(1, "合格", "よくできました。")]
    [InlineData(0, "不合格", "次回もう一度挑戦しましょう。")]
    [InlineData(null, "合否基準未設定", "お知らせです。")]
    public async Task ResultMessageUsesFinalizedPdfAndMatchingTemplate(
        int? maximumMistakes, string verdict, string expectedParagraph)
    {
        await using var fixture = await AutomationFixture.CreateAsync();
        await fixture.ConfigureAsync(dryRun: false);
        await fixture.SeedFinalizedResultAsync();
        await using var db = await fixture.CreateDbContextAsync();
        var session = await db.TestSessions.SingleAsync();
        session.TitleOverride = "地理＜結果＞";
        session.TemplateSubjectSnapshot = "社会";
        session.MaximumMistakesToPass = maximumMistakes;
        var result = await db.QuestionResults.Include(q => q.GradingRun).SingleAsync();
        var correction = new ResultRevisionEntity
        {
            Id = UlidId.New(fixture.Now),
            QuestionResultId = result.Id,
            RevisionNumber = 2,
            SupersedesRevisionId = result.CurrentRevisionId,
            Source = "teacher_override",
            ReasonCode = "teacher_confirmed",
            ActorStaffUserId = result.GradingRun.FinalizedByStaffUserId,
            CreatedAt = fixture.Now,
            AwardedPointsMilli = 0,
            Outcome = "incorrect",
        };
        result.CurrentRevisionId = correction.Id;
        result.GradingRun.EarnedPointsMilli = 0;
        db.ResultRevisions.Add(correction);
        await db.SaveChangesAsync();
        Assert.True(await fixture.ResultPlanner.ProcessNextAsync());
        Assert.True(await fixture.PdfWorker.ProcessNextAsync());
        Assert.True(await fixture.DeliveryProcessor.ProcessNextAsync());

        // The report is already exported. Its message must describe the same result,
        // even if staff have since changed the test or edited their wording.
        session.MaximumMistakesToPass = 2;
        session.TitleOverride = "後日のテスト";
        db.GuardianMessageTemplates.Add(new GuardianMessageTemplatesEntity
        {
            Title = "＜教科＞／＜タイトル＞　＜結果＞",
            Pass = "よくできました。\n＜結果＞・＜結果＞",
            Fail = "次回もう一度挑戦しましょう。\n＜結果＞・＜結果＞",
            Unconfigured = "お知らせです。\n＜結果＞・＜結果＞",
        });
        await db.SaveChangesAsync();
        Assert.True(await fixture.DeliveryProcessor.ProcessNextAsync());
        var sent = Assert.Single(fixture.Client.Requests);
        Assert.Equal($"社会／地理＜結果＞　{verdict}", sent.Title);
        Assert.Equal($"{expectedParagraph}\n{verdict}・{verdict}", sent.Body);
    }

    [Theory]
    [InlineData("pass", "合格", "\nよく準備して臨むことができました。この調子で、次の範囲にも取り組んでいきましょう。ご家庭でもぜひ声をかけてあげてください。")]
    [InlineData("fail", "不合格", "\n次回の通塾時に、本人の都合のよいタイミングで再テストを受けることができます。それまでに該当範囲をもう一度見直しておくよう、ご家庭でもお声がけいただけますと幸いです。")]
    [InlineData("unconfigured", "合否基準未設定", "")]
    public void PresetsRenderTheRequestedGuardianCopy(string verdict, string label, string closing)
    {
        var result = GuardianResultMessage.Format(new GuardianDeliveryEntity
        {
            ResultSubject = "理科", ResultTitle = "てこ・滑車", ResultVerdict = verdict,
        }, new GuardianMessageTemplatesEntity());
        Assert.Equal("暗記テスト採点結果のお知らせ", result.Title);
        Assert.Equal("いつも大変お世話になっております。\n先日実施いたしました暗記テストの結果をお知らせいたします。\n\n" +
            $"教科：理科\n範囲：てこ・滑車\n結果：{label}\n\n" +
            "答案をPDFで添付しておりますので、あわせてご確認ください。" + closing, result.Body);
    }
}
