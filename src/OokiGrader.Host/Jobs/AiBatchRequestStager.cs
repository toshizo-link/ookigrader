using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Jobs;

public sealed record AiBatchStageRequest(
    string AiRequestId,
    string CompatibilityKey,
    AiProviderRequest ProviderRequest,
    int Priority = 0,
    string? CorrelationId = null,
    string ModelId = AiProviderCatalog.GeminiDefaultModelId);

public sealed record AiBatchStageResult(
    string BatchRequestId,
    string PreparationJobId,
    bool Created);

/// <summary>
/// Persists a provider request before it can be assembled into a remote batch.
/// The stored line is immutable and keyed by its SHA-256 digest.
/// </summary>
public sealed class AiBatchRequestStager(
    IDbContextFactory<OokiGraderDbContext> dbContextFactory,
    IWriteCoordinator writeCoordinator,
    IAiBatchProviderClient batchProvider,
    TimeProvider timeProvider)
{
    public const int SchemaVersion = 1;

    public Task<AiBatchStageResult> StageAsync(
        AiBatchStageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ProviderRequest);
        if (string.IsNullOrWhiteSpace(request.AiRequestId)
            || !IsSha256(request.CompatibilityKey)
            || request.Priority is < -1000 or > 1000)
        {
            throw new ArgumentException(
                "The batch staging request is invalid.",
                nameof(request));
        }

        var jsonLines = batchProvider.BuildJsonLines([request.ProviderRequest], request.ModelId);
        if (jsonLines.Length > 25_000_000)
        {
            throw new InvalidDataException("ai_batch_request_too_large");
        }

        var line = Encoding.UTF8.GetString(jsonLines).TrimEnd('\r', '\n');
        var requestHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(line)))
            .ToLowerInvariant();
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var aiRequest = await db.AiRequests
                .Include(item => item.AiTaskProfile)
                    .ThenInclude(item => item.AiConnection)
                .SingleOrDefaultAsync(
                    item => item.Id == request.AiRequestId,
                    token)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException(
                    $"AI request '{request.AiRequestId}' does not exist.");
            var eligible = await GeminiBatchEligibility.EligibleRequestIdsAsync(db, [aiRequest], token).ConfigureAwait(false);
            if (!eligible.Contains(aiRequest.Id))
                throw new InvalidOperationException("The AI request is not eligible for Gemini batch processing.");
            ValidateAiRequest(aiRequest, request);

            var existing = await db.AiBatchRequests
                .SingleOrDefaultAsync(
                    item => item.AiRequestId == aiRequest.Id,
                    token)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.CompatibilityKey != request.CompatibilityKey
                    || existing.RequestKey != request.ProviderRequest.RequestKey
                    || (existing.ProviderRequestHash != requestHash
                        && !MatchesLegacyGradingLine(existing, request.ProviderRequest, line)))
                {
                    throw new InvalidOperationException(
                        "A different immutable batch request is already staged.");
                }

                var existingJob = await db.BackgroundJobs
                    .SingleAsync(
                        item => item.DeduplicationKey
                            == PrepareDeduplicationKey(
                                existing.CompatibilityKey,
                                existing.Id),
                        token)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return new AiBatchStageResult(
                    existing.Id,
                    existingJob.Id,
                    Created: false);
            }

            var now = timeProvider.GetUtcNow();
            var batchRequest = new AiBatchRequestEntity
            {
                Id = UlidId.New(now),
                AiRequestId = aiRequest.Id,
                RequestKey = request.ProviderRequest.RequestKey,
                CompatibilityKey = request.CompatibilityKey,
                ProviderRequestJson = line,
                ProviderRequestHash = requestHash,
                ProviderRequestBytes = Encoding.UTF8.GetByteCount(line),
                State = "ready",
                CreatedAt = now,
                UpdatedAt = now,
            };
            aiRequest.State = "prepared";
            aiRequest.UpdatedAt = now;
            db.AiBatchRequests.Add(batchRequest);

            var job = new BackgroundJobEntity
            {
                Id = UlidId.New(now),
                Type = AiBatchJobWorker.PrepareJobType,
                SchemaVersion = SchemaVersion,
                DeduplicationKey = PrepareDeduplicationKey(
                    request.CompatibilityKey,
                    batchRequest.Id),
                Priority = request.Priority,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    compatibilityKey = request.CompatibilityKey,
                }),
                State = "queued",
                AttemptCount = 0,
                MaxAttempts = 100,
                NextAttemptAt = now,
                CorrelationId = request.CorrelationId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.BackgroundJobs.Add(job);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new AiBatchStageResult(
                batchRequest.Id,
                job.Id,
                Created: true);
        }, cancellationToken);
    }

    private static bool MatchesLegacyGradingLine(
        AiBatchRequestEntity existing, AiProviderRequest request, string line)
    {
        var staged = existing.ProviderRequestJson;
        // A restart can encounter a pre-upgrade initial-grading line with the
        // same rubric in escaped JSON and the user instruction after the images.
        if (request.TaskType != AiTaskTypes.InitialGrading || request.SchemaVersion != "submission_analysis_v2"
            || staged is null || Encoding.UTF8.GetByteCount(staged) != existing.ProviderRequestBytes
            || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(staged))),
                existing.ProviderRequestHash, StringComparison.Ordinal))
        {
            return false;
        }

        var previous = JsonNode.Parse(staged);
        if (previous?["request"]?["contents"]?[0]?["parts"] is not JsonArray parts
            || parts.Count != request.Media.Count + 1
            || parts[^1] is not JsonObject textPart
            || textPart["text"]?.GetValue<string>() is not { } oldInstruction)
        {
            return false;
        }

        var dataStart = request.UserInstruction.IndexOf('{');
        if (dataStart < 0 || oldInstruction.IndexOf('{') != dataStart
            || !oldInstruction.AsSpan(0, dataStart).SequenceEqual(request.UserInstruction.AsSpan(0, dataStart))
            || !JsonNode.DeepEquals(JsonNode.Parse(oldInstruction[dataStart..]),
                JsonNode.Parse(request.UserInstruction[dataStart..])))
        {
            return false;
        }

        // Compare the complete envelope, including every ordered page, the bound
        // response schema, generation settings and both request keys. The stored
        // JSON and its digest remain untouched.
        parts.RemoveAt(parts.Count - 1);
        textPart["text"] = request.UserInstruction;
        parts.Insert(0, textPart);
        return JsonNode.DeepEquals(previous, JsonNode.Parse(line));
    }

    private static void ValidateAiRequest(
        AiRequestEntity aiRequest,
        AiBatchStageRequest stage)
    {
        var profile = aiRequest.AiTaskProfile;
        if (aiRequest.RequestKey != stage.ProviderRequest.RequestKey
            || profile.ModelId != stage.ModelId
            || aiRequest.State is not ("prepared" or "budget_blocked")
            || !GeminiBatchEligibility.IsBatchRequest(aiRequest, profile))
        {
            throw new InvalidOperationException(
                "The AI request is not eligible for Gemini batch processing.");
        }
    }

    internal static string PrepareDeduplicationKey(
        string compatibilityKey,
        string batchRequestId) =>
        $"ai-batch:prepare:{compatibilityKey}:{batchRequestId}";

    private static bool IsSha256(string value) =>
        value.Length == 64
        && value.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f');
}
