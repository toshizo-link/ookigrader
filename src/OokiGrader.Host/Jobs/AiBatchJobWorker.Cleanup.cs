using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;
using OokiGrader.Application.Abstractions;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Jobs;

public sealed partial class AiBatchJobWorker
{
    private sealed record CleanupPayload(string BatchId, bool InputDeleted = false,
        bool OutputDeleted = false, bool ProviderManagedOutput = false);

    private static void ScheduleCleanup(
        OokiGraderDbContext db, AiBatchEntity batch, DateTimeOffset now, string? correlationId)
    {
        batch.UpdatedAt = now;
        if (batch.ProviderInputFileName is null && batch.ProviderOutputFileName is null)
        {
            batch.CleanupState = "completed";
            return;
        }
        batch.CleanupState = "pending";
        EnqueueBatchJob(db, CleanupJobType, batch, $"ai-batch:{batch.Id}:cleanup", now, correlationId);
    }

    private Task RecoverHistoricalCleanupAsync(CancellationToken cancellationToken) =>
        _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
            var orphaned = await db.AiBatches.Where(batch =>
                (batch.CleanupState == "pending" || batch.CleanupState == "failed")
                && !db.BackgroundJobs.Any(job => job.DeduplicationKey == "ai-batch:" + batch.Id + ":cleanup"))
                .ToListAsync(token).ConfigureAwait(false);
            foreach (var batch in orphaned)
            {
                ScheduleCleanup(db, batch, _timeProvider.GetUtcNow(), null);
            }
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    private async Task CleanupBatchAsync(JobLease lease, CancellationToken cancellationToken)
    {
        var payload = Deserialize<CleanupPayload>(lease.PayloadJson);
        if (string.IsNullOrWhiteSpace(payload?.BatchId))
        {
            await FailJobAsync(lease.Id, "ai_batch_cleanup_payload_invalid", cancellationToken).ConfigureAwait(false);
            return;
        }
        await using var reader = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var batch = await reader.AiBatches.AsNoTracking().Include(x => x.AiConnection)
            .SingleOrDefaultAsync(x => x.Id == payload.BatchId, cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            await FailJobAsync(lease.Id, "ai_batch_missing", cancellationToken).ConfigureAwait(false);
            return;
        }

        var managedOutput = payload.ProviderManagedOutput;
        var remaining = new[] { payload.InputDeleted ? null : batch.ProviderInputFileName,
                payload.OutputDeleted || managedOutput ? null : batch.ProviderOutputFileName }
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        string? error = null;
        var retryable = false;
        if (batch.CleanupState == "pending" && remaining.Count != 0)
        {
            if (batch.Provider != AiProviders.GeminiDirect || batch.AiConnection.Provider != batch.Provider
                || batch.AiConnection.CredentialRevision != batch.ConnectionRevision)
            {
                error = "ai_batch_cleanup_credential_changed";
            }
            else
            {
                try
                {
                    using var secret = await _secretStore.ReadAsync(
                        new AiSecretReference(batch.AiConnection.SecretReference), cancellationToken).ConfigureAwait(false);
                    var connection = ToConnection(batch);
                    foreach (var file in remaining.ToArray())
                    {
                        try
                        {
                            await _batchProvider.DeleteFileAsync(connection, secret.Utf8Bytes, file, cancellationToken)
                                .ConfigureAwait(false);
                            remaining.Remove(file);
                        }
                        catch (AiProviderException exception) when (
                            file == batch.ProviderOutputFileName && file != batch.ProviderInputFileName
                            && (IsTerminal(batch.State) || batch.RemoteEndedAt is not null)
                            && GeminiBatchClient.IsProviderManagedOutput(batch.ProviderBatchName, file, exception))
                        {
                            managedOutput = true;
                            remaining.Remove(file);
                        }
                        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                        {
                            var providerFailure = exception as AiProviderException;
                            error = providerFailure?.SafeErrorCode ?? "gemini_batch_delete_failed";
                            retryable = true;
                        }
                    }
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    error = "ai_batch_cleanup_credentials_unavailable";
                    retryable = true;
                }
            }
        }

        await _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
            var job = await db.BackgroundJobs.SingleAsync(x => x.Id == lease.Id, token).ConfigureAwait(false);
            if (job.State != "leased" || job.LeaseOwner != lease.Owner)
            {
                return;
            }
            var stored = await db.AiBatches.SingleAsync(x => x.Id == batch.Id, token).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            if (error is not null || managedOutput)
            {
                job.PayloadJson = JsonSerializer.Serialize(new CleanupPayload(batch.Id,
                    batch.ProviderInputFileName is null || !remaining.Contains(batch.ProviderInputFileName),
                    !managedOutput && (batch.ProviderOutputFileName is null || !remaining.Contains(batch.ProviderOutputFileName)),
                    managedOutput));
            }
            if (stored.CleanupState != "pending" || error is null)
            {
                if (stored.CleanupState == "pending")
                {
                    stored.CleanupState = managedOutput ? "provider_managed_output" : "completed";
                    if (stored.ErrorCode == "gemini_batch_cleanup_failed")
                    {
                        stored.ErrorCode = null;
                        stored.SafeErrorDetail = null;
                    }
                    if (managedOutput)
                    {
                        stored.SafeErrorDetail ??= GeminiBatchClient.FileIdLengthError;
                    }
                }
                CompleteJob(job, now);
                if (managedOutput)
                {
                    job.SafeErrorDetail = GeminiBatchClient.FileIdLengthError;
                }
            }
            else if (retryable && job.AttemptCount < job.MaxAttempts)
            {
                RetryJob(job, error, now.Add(AiRetrySchedule.Delay(job.AttemptCount, batch.Id)));
            }
            else
            {
                stored.CleanupState = "failed";
                if (stored.ErrorCode is null or "gemini_batch_cleanup_failed")
                {
                    stored.ErrorCode = "gemini_batch_cleanup_failed";
                    stored.SafeErrorDetail = BoundedErrorCode(error);
                }
                FailJob(job, now, error);
            }
            stored.UpdatedAt = now;
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }
}
