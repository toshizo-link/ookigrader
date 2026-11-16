using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using OokiGrader.SchoolManager.Delivery;
using PdfSharp.Pdf.IO;

namespace OokiGrader.IntegrationTests;

public sealed partial class SchoolManagerAutomationTests
{
    public enum InboxScenario { Acknowledged, AttachmentRejected, AcknowledgementLost }

    [WindowsEdgeTheory]
    [InlineData(InboxScenario.Acknowledged)]
    [InlineData(InboxScenario.AttachmentRejected)]
    [InlineData(InboxScenario.AcknowledgementLost)]
    public async Task FinalizedResultReachesOnlyItsGuardianAndIsNeverSentTwice(InboxScenario scenario)
    {
        var inbox = new OfflineSchoolManager(scenario);
        await using var fixture = await AutomationFixture.CreateAsync(inbox);
        await fixture.ConfigureAsync(dryRun: scenario == InboxScenario.Acknowledged);
        var submissionId = await fixture.SeedFinalizedResultAsync();
        Assert.True(await fixture.ResultPlanner.ProcessNextAsync());
        Assert.True(await fixture.PdfWorker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var export = await db.ExportRecords.AsNoTracking().SingleAsync();
        Assert.True(export.State == "verified", $"{export.State}: {export.ErrorCode} ({export.SafeErrorDetail})");
        Assert.Equal(submissionId, export.SubmissionId);
        Assert.Equal(3, export.PageCount);
        Assert.True(await fixture.DeliveryProcessor.ProcessNextAsync()); // Promote the verified PDF.
        if (scenario == InboxScenario.Acknowledged)
        {
            Assert.True(await fixture.DeliveryProcessor.ProcessNextAsync());
            var preview = await db.GuardianDeliveries.AsNoTracking().SingleAsync();
            Assert.Equal("ready", preview.State);
            Assert.NotNull(preview.LastDryRunAt);
            Assert.Null(preview.SentAt);
            Assert.Empty(inbox.Uploads);
            Assert.Empty(inbox.Messages);
            var settings = await db.SchoolManagerSettings.SingleAsync();
            settings.DryRun = false;
            await db.SaveChangesAsync();
        }

        Assert.True(await fixture.DeliveryProcessor.ProcessNextAsync());
        Assert.False(await fixture.Worker.ProcessCycleAsync()); // Includes interrupted-send reconciliation.
        Assert.False(await fixture.Worker.ProcessCycleAsync());
        var delivery = await db.GuardianDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal(scenario == InboxScenario.Acknowledged ? 2 : 1, delivery.AttemptCount);
        var upload = Assert.Single(inbox.Uploads);
        Assert.Equal("採点結果_20260915.pdf", upload.Name);
        Assert.Equal("application/pdf", upload.MediaType);
        Assert.Equal("application/pdf", upload.ContentType);
        Assert.Equal(export.Sha256, Convert.ToHexStringLower(SHA256.HashData(upload.Bytes)));
        using var receivedStream = new MemoryStream(upload.Bytes);
        using var receivedPdf = PdfReader.Open(receivedStream, PdfDocumentOpenMode.Import);
        Assert.Equal(3, receivedPdf.PageCount);
        Assert.Empty(inbox.UnexpectedRequests);

        if (scenario == InboxScenario.AttachmentRejected)
        {
            Assert.Empty(inbox.Messages);
            Assert.Equal("failed", delivery.State);
            Assert.Equal("attachment_upload_failed", delivery.LastErrorCode);
            Assert.Null(delivery.SchoolManagerThreadId);
            Assert.Null(delivery.SentAt);
            return;
        }

        var thread = Assert.Single(inbox.Messages);
        Assert.Equal(new TargetUser("matched-student", "class-a"), thread.TargetUser);
        Assert.Collection(thread.Members,
            sender => Assert.Equal(new Member("orgDepartment", null, "class-a"), sender),
            guardian => Assert.Equal(new Member("parent", "matched-guardian", null), guardian));
        Assert.Equal("採点結果のお知らせ", thread.Title);
        Assert.Equal("採点結果をお送りします。添付のPDFをご確認ください。", thread.Message.Body);
        Assert.Equal(new Attachment(upload.Name, 21001), Assert.Single(thread.Message.AttachedFiles));
        if (scenario == InboxScenario.AcknowledgementLost)
        {
            Assert.Equal("failed", delivery.State);
            Assert.Equal("message_send_outcome_unknown", delivery.LastErrorCode);
            Assert.Null(delivery.SentAt);
            Assert.Null(delivery.SchoolManagerThreadId);
        }
        else
        {
            Assert.Equal("sent", delivery.State);
            Assert.Equal("501", delivery.SchoolManagerThreadId);
            Assert.NotNull(delivery.SentAt);
            Assert.Null(delivery.LastErrorCode);
        }
    }

    // Only the remote application is simulated. Every browser request is intercepted before
    // navigation begins, including any unexpected origin; service workers are disabled.
    private sealed class OfflineSchoolManager(InboxScenario scenario) : ISchoolManagerClient
    {
        public ConcurrentQueue<Upload> Uploads { get; } = new();
        public ConcurrentQueue<InboxThread> Messages { get; } = new();
        public ConcurrentQueue<string> UnexpectedRequests { get; } = new();

        public Task<SchoolManagerDeliveryResult> DeliverAsync(
            SchoolManagerDeliveryRequest request,
            CancellationToken cancellationToken = default) =>
            PlaywrightSchoolManagerClient.DeliverInBrowserAsync(request, async browser =>
            {
                var context = await browser.NewContextAsync(new() { ServiceWorkers = ServiceWorkerPolicy.Block });
                await context.RouteAsync("**/*", HandleRequestAsync);
                return context;
            }, cancellationToken);

        private async Task HandleRequestAsync(IRoute route)
        {
            var request = route.Request;
            var uri = new Uri(request.Url);
            var path = uri.AbsolutePath;
            if (uri.GetLeftPart(UriPartial.Authority) == "https://fsm.flens.jp")
            {
                if (request.Method == "GET" && path is "/" or "/login" or "/home" or "/message-threads"
                    or "/message-thread-target-users" or "/message-thread-members-and-title"
                    or "/message-thread-details/0/create")
                {
                    using var resource = typeof(OfflineSchoolManager).Assembly.GetManifestResourceStream(
                        "OokiGrader.IntegrationTests.Fixtures.SchoolManagerDelivery.html")!;
                    using var reader = new StreamReader(resource);
                    await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", Body = await reader.ReadToEndAsync() });
                    return;
                }

                if (request.Method == "POST" && path == "/lms/v1.0/org/message-threads/messages/attached-files")
                {
                    var parameters = QueryHelpers.ParseQuery(uri.Query);
                    Uploads.Enqueue(new(
                        parameters["fileName"].ToString(),
                        parameters["mimeType"].ToString(),
                        request.Headers["content-type"],
                        request.PostDataBuffer ?? []));
                    if (scenario == InboxScenario.AttachmentRejected)
                        await route.FulfillAsync(new() { Status = 422 });
                    else
                        await route.FulfillAsync(new() { Json = new { id = 21001 } });
                    return;
                }

                if (request.Method == "POST" && path == "/lms/v1.0/org/message-threads")
                {
                    Messages.Enqueue(JsonSerializer.Deserialize<InboxThread>(request.PostData!, JsonSerializerOptions.Web)!);
                    if (scenario == InboxScenario.AcknowledgementLost)
                        await route.AbortAsync(); // The recipient already has the message.
                    else
                        await route.FulfillAsync(new() { Json = new { id = 501 } });
                    return;
                }
            }
            UnexpectedRequests.Enqueue($"{request.Method} {path}");
            await route.AbortAsync();
        }
    }

    private sealed record Upload(string Name, string MediaType, string ContentType, byte[] Bytes);
    private sealed record InboxThread(TargetUser TargetUser, Member[] Members, string Title, Message Message);
    private sealed record TargetUser(string UserId, string DepartmentId);
    private sealed record Member(string MemberType, string? UserId, string? DepartmentId);
    private sealed record Message(string Body, Attachment[] AttachedFiles);
    private sealed record Attachment(string FileName, long Id);
}

public sealed class WindowsEdgeTheoryAttribute : TheoryAttribute
{
    public WindowsEdgeTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "Edge", "Application", "msedge.exe")))
        {
            Skip = "The School Manager delivery integration tests require Windows and Microsoft Edge.";
        }
    }
}
