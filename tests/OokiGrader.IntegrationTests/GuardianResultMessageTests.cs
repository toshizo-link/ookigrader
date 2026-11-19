using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Identifiers;
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
        Assert.Contains("判定　合格", request.Body);
        var verified = await db.ExportRecords.AsNoTracking().SingleAsync(record => record.State == "verified");
        Assert.Equal(sent.ExportRecordId, verified.Id);
        Assert.Equal(verified.Sha256,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(request.AttachmentBytes)));
    }

    [Theory]
    [InlineData(1, "合格", "よくできました。")]
    [InlineData(0, "不合格", "次回もう一度挑戦しましょう。")]
    [InlineData(null, "合否基準未設定", "お知らせです。")]
    public async Task ResultMessageUsesFinalizedPdfAndMatchingParagraph(
        int? maximumMistakes, string verdict, string expectedParagraph)
    {
        await using var fixture = await AutomationFixture.CreateAsync();
        await fixture.ConfigureAsync(dryRun: false);
        await fixture.SeedFinalizedResultAsync();
        await using var db = await fixture.CreateDbContextAsync();
        var session = await db.TestSessions.SingleAsync();
        session.TitleOverride = "地理の小テスト";
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
            Pass = "よくできました。",
            Fail = "次回もう一度挑戦しましょう。",
            Unconfigured = "お知らせです。",
        });
        await db.SaveChangesAsync();
        Assert.True(await fixture.DeliveryProcessor.ProcessNextAsync());
        Assert.Equal($"教科　社会\nテスト名　地理の小テスト\n判定　{verdict}\n\n{expectedParagraph}",
            Assert.Single(fixture.Client.Requests).Body);
    }
}
