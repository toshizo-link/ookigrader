using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;
using OokiGrader.Application.Abstractions;
using OokiGrader.Host.Api;
using OokiGrader.Host.Jobs;

namespace OokiGrader.IntegrationTests;

public sealed partial class AiBatchJobWorkerTests
{
    [Fact]
    public async Task CleanupRetriesOnlyFailedFileAcrossRestartWithoutRebillingOrResubmission()
    {
        await using var fixture = await BatchFixture.CreateAsync();
        await CompleteRemotePollAsync(fixture);
        fixture.Provider.FailingDeleteFile = "files/input-1";
        var worker = fixture.RestartWorker();
        Assert.True(await worker.ProcessNextAsync());
        Assert.Equal(["files/input-1", "files/output-1"], fixture.Provider.DeleteFiles.Order(StringComparer.Ordinal));
        await using (var db = fixture.CreateDbContext())
        {
            var batch = await db.AiBatches.SingleAsync();
            var job = await db.BackgroundJobs.SingleAsync(x => x.Type == AiBatchJobWorker.CleanupJobType);
            Assert.Equal("succeeded", batch.State);
            Assert.Equal("pending", batch.CleanupState);
            Assert.Equal("retry_waiting", job.State);
            Assert.InRange(job.NextAttemptAt - fixture.Time.GetUtcNow(), TimeSpan.FromSeconds(24), TimeSpan.FromSeconds(36));
        }
        Assert.False(await worker.ProcessNextAsync());
        fixture.Provider.FailingDeleteFile = null;
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await fixture.RestartWorker().ProcessNextAsync());
        Assert.Equal("files/input-1", fixture.Provider.DeleteFiles[2]);
        Assert.Equal(3, fixture.Provider.DeleteCalls);
        Assert.Equal(1, fixture.Provider.CreateCalls);
        Assert.Equal(1, fixture.Provider.UploadCalls);
        Assert.False(fixture.Provider.ObservedInsideWriteCoordinator);
        await using var verified = fixture.CreateDbContext();
        Assert.Equal("completed", (await verified.AiBatches.SingleAsync()).CleanupState);
        Assert.Equal("succeeded", (await verified.BackgroundJobs.SingleAsync(x => x.Type == AiBatchJobWorker.CleanupJobType)).State);
        Assert.Single(await verified.AiUsage.ToListAsync());
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("failed")]
    public async Task StartupRecoversHistoricalCleanupWithoutAQueuedJob(string cleanupState)
    {
        await using var fixture = await BatchFixture.CreateAsync();
        await CompleteRemotePollAsync(fixture);
        await using (var db = fixture.CreateDbContext())
        {
            var batch = await db.AiBatches.SingleAsync();
            batch.CleanupState = cleanupState;
            batch.ErrorCode = "gemini_batch_cleanup_failed";
            db.BackgroundJobs.Remove(await db.BackgroundJobs.SingleAsync(x => x.Type == AiBatchJobWorker.CleanupJobType));
            await db.SaveChangesAsync();
        }
        Assert.True(await fixture.RestartWorker().ProcessNextAsync());
        Assert.False(await fixture.RestartWorker().ProcessNextAsync());
        await using var verified = fixture.CreateDbContext();
        var completed = await verified.AiBatches.SingleAsync();
        Assert.Equal("completed", completed.CleanupState);
        Assert.Null(completed.ErrorCode);
        Assert.Equal(2, fixture.Provider.DeleteCalls);
        Assert.True(await verified.BackgroundJobs.AnyAsync(x => x.Type == AiBatchJobWorker.CleanupJobType && x.State == "succeeded"));
    }

    [Fact]
    public async Task UnrelatedDeleteRejectionRemainsEligibleForBoundedRetry()
    {
        await using var fixture = await BatchFixture.CreateAsync();
        await CompleteRemotePollAsync(fixture);
        fixture.Provider.FailingDeleteFile = "files/input-1";
        fixture.Provider.DeleteFailureIsTransient = false;
        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.False(await fixture.RestartWorker().ProcessNextAsync());
        await using var db = fixture.CreateDbContext();
        Assert.Equal("pending", (await db.AiBatches.SingleAsync()).CleanupState);
        Assert.Equal(2, fixture.Provider.DeleteCalls);
        Assert.True(await db.BackgroundJobs.AnyAsync(x => x.Type == AiBatchJobWorker.CleanupJobType
            && x.State == "retry_waiting" && x.AttemptCount == 1));
    }

    [Fact]
    public async Task GeneratedOutputRemainsProviderManagedAfterUploadedInputDeletionSucceeds()
    {
        await using var fixture = await BatchFixture.CreateAsync();
        var operationId = new string('a', 40);
        var output = "files/batch-" + operationId;
        fixture.Provider.RemoteStatus = FakeBatchProvider.Status(BatchFixture.DisplayNamePlaceholder,
            AiBatchRemoteState.Succeeded, "batches/" + operationId, fixture.Time.GetUtcNow()) with { OutputFileName = output };
        await CompleteRemotePollAsync(fixture);
        fixture.Provider.LengthRejectedFile = output;
        fixture.Provider.FailingDeleteFile = "files/input-1";
        Assert.True(await fixture.Worker.ProcessNextAsync());
        await using (var pending = fixture.CreateDbContext())
        {
            Assert.Equal("pending", (await pending.AiBatches.SingleAsync()).CleanupState);
        }
        fixture.Provider.FailingDeleteFile = null;
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await fixture.RestartWorker().ProcessNextAsync());
        await using var db = fixture.CreateDbContext();
        var batch = await db.AiBatches.SingleAsync();
        Assert.Equal("succeeded", batch.State);
        Assert.Equal("provider_managed_output", batch.CleanupState);
        Assert.Equal(output, batch.ProviderOutputFileName);
        Assert.Equal(GeminiBatchClient.FileIdLengthError, batch.SafeErrorDetail);
        var job = await db.BackgroundJobs.SingleAsync(x => x.Type == AiBatchJobWorker.CleanupJobType);
        Assert.Equal("succeeded", job.State);
        Assert.Equal(GeminiBatchClient.FileIdLengthError, job.SafeErrorDetail);
        Assert.Equal(1, fixture.Provider.DeleteFiles.Count(x => x == output));
        Assert.Equal(2, fixture.Provider.DeleteFiles.Count(x => x == "files/input-1"));
        Assert.Equal(1, fixture.Provider.CreateCalls);
    }

    [Fact]
    public async Task CleanupReclaimsExpiredFinalLeaseAndStopsAfterFinalFailure()
    {
        await using var fixture = await BatchFixture.CreateAsync();
        await CompleteRemotePollAsync(fixture);
        await using (var db = fixture.CreateDbContext())
        {
            var job = await db.BackgroundJobs.SingleAsync(x => x.Type == AiBatchJobWorker.CleanupJobType);
            job.State = "leased";
            job.AttemptCount = job.MaxAttempts;
            job.LeaseOwner = "crashed-worker";
            job.LeaseExpiresAt = fixture.Time.GetUtcNow().AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        fixture.Provider.FailingDeleteFile = "files/input-1";
        Assert.True(await fixture.RestartWorker().ProcessNextAsync());
        Assert.False(await fixture.RestartWorker().ProcessNextAsync());
        await using var verified = fixture.CreateDbContext();
        var batch = await verified.AiBatches.SingleAsync();
        Assert.Equal("failed", batch.CleanupState);
        Assert.Equal("succeeded", batch.State);
        Assert.Equal("gemini_batch_cleanup_failed", batch.ErrorCode);
        Assert.Equal(2, fixture.Provider.DeleteCalls);
        Assert.True(await verified.BackgroundJobs.AnyAsync(x => x.Type == AiBatchJobWorker.CleanupJobType && x.State == "failed"));
    }

    [Fact]
    public async Task AdminCancelAndRetryKeepBatchAndCleanupJobInSync()
    {
        await using var fixture = await BatchFixture.CreateAsync();
        await CompleteRemotePollAsync(fixture);
        await using var db = fixture.CreateDbContext();
        var job = await db.BackgroundJobs.SingleAsync(x => x.Type == AiBatchJobWorker.CleanupJobType);
        job.AttemptCount = job.MaxAttempts;
        await db.SaveChangesAsync();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "admin")], "test")),
        };
        var audit = new CleanupAudit();
        await AdminEndpoints.CancelJobAsync(job.Id, context, db, audit, CancellationToken.None);
        Assert.Equal("cancelled", job.State);
        Assert.Equal("failed", (await db.AiBatches.AsNoTracking().SingleAsync()).CleanupState);
        await AdminEndpoints.RetryJobAsync(job.Id, context, db, audit, CancellationToken.None);
        Assert.Equal("queued", job.State);
        Assert.Equal(0, job.AttemptCount);
        Assert.Equal("pending", (await db.AiBatches.AsNoTracking().SingleAsync()).CleanupState);
        fixture.Time.Advance(job.NextAttemptAt - fixture.Time.GetUtcNow());
        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.Equal("completed", (await db.AiBatches.AsNoTracking().SingleAsync()).CleanupState);
    }

    private sealed class CleanupAudit : IAuditSink
    {
        public Task<string> AppendAsync(AuditWrite auditEvent, CancellationToken cancellationToken = default) =>
            Task.FromResult(auditEvent.ObjectId);
    }

    private static async Task CompleteRemotePollAsync(BatchFixture fixture)
    {
        await fixture.StageAsync();
        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.True(await fixture.Worker.ProcessNextAsync());
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.Empty(fixture.Provider.DeleteFiles);
        await using var db = fixture.CreateDbContext();
        Assert.Equal("pending", (await db.AiBatches.SingleAsync()).CleanupState);
        Assert.True(await db.BackgroundJobs.AnyAsync(x => x.Type == AiBatchJobWorker.CleanupJobType && x.State == "queued"));
    }

    [Fact]
    public async Task CleanupConstraintUpgradeAndDowngradePreserveLedgerAndOutputReference()
    {
        const string previous = "20261125000000_0032_SessionGradingMode";
        await using var fixture = await BatchFixture.CreateAsync(migration: previous);
        await CompleteRemotePollAsync(fixture);
        await using var db = fixture.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync();
        var batch = await db.AiBatches.SingleAsync();
        batch.CleanupState = "provider_managed_output";
        batch.SafeErrorDetail = GeminiBatchClient.FileIdLengthError;
        await db.SaveChangesAsync();
        await migrator.MigrateAsync(previous);
        db.ChangeTracker.Clear();
        var preserved = await db.AiBatches.SingleAsync();
        Assert.Equal("failed", preserved.CleanupState);
        Assert.Equal("files/output-1", preserved.ProviderOutputFileName);
        Assert.Equal(GeminiBatchClient.FileIdLengthError, preserved.SafeErrorDetail);
        Assert.Equal(preserved.Id, (await db.AiBatchRequests.SingleAsync()).AiBatchId);
        Assert.Single(await db.AiUsage.ToListAsync());
        await using var check = db.Database.GetDbConnection().CreateCommand();
        check.CommandText = "PRAGMA foreign_key_check";
        Assert.Null(await check.ExecuteScalarAsync());
    }
}
