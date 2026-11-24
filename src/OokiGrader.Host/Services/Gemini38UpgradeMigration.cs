using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Api;
using OokiGrader.Host.Jobs;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Services;

/// <summary>
/// The 0.9.17 upgrade selects the Gemini 3.8/3.5 routing policy before queued AI work
/// can run. Credentials and historical requests remain untouched. A real
/// capability probe, rather than the old model's probe, controls readiness.
/// </summary>
internal static class Gemini38UpgradeMigration
{
    internal const string ModelId = "gemini-3.8-flash";
    internal const string AuditEventType = "ai.upgrade.gemini38.0_9_17";

    public static async Task<bool> ApplyAsync(
        OokiGraderDbContext db,
        IAiPromptBundleCatalog promptCatalog,
        TimeProvider timeProvider,
        IAiProviderFeaturePolicy featurePolicy,
        IAiSecretStore secretStore,
        IAiProviderClientResolver providerResolver,
        CancellationToken cancellationToken = default)
    {
        var connection = await db.AiConnections
            .Where(item => item.Provider == AiProviders.GeminiDirect)
            .OrderByDescending(item => item.State != "disabled")
            .ThenByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (connection is null || connection.State == "disabled")
        {
            return false;
        }

        var previousUpgrade = await db.AuditEvents.AsNoTracking()
            .Where(item => item.EventType == AuditEventType && item.ObjectId == connection.Id)
            .OrderByDescending(item => item.OccurredAt).ThenByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (previousUpgrade is not null && !WasBlockedByTransientUpgrade(connection, previousUpgrade))
            return false;

        var previousModelId = connection.ModelId;
        // A model switch does not rotate or rewrite the stored API key. Its
        // credential revision and opaque secret reference must stay aligned.
        var result = await ProbeAsync(
            connection,
            featurePolicy,
            secretStore,
            providerResolver,
            cancellationToken);
        // An inconclusive probe for the replacement model says nothing about
        // the existing credentials or profiles. Retry on a subsequent startup.
        if (AiAdminEndpoints.IsTransientGeminiProbeFailure(connection, result))
            return false;

        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(
            cancellationToken);
        connection.ModelId = ModelId;
        connection.EndpointProfile = AiProviderCatalog.GeminiEndpointProfile;
        connection.TimeoutSeconds = 300;
        var ready = AiAdminEndpoints.IsSuccessfulImageProbe(connection, result);
        AiAdminEndpoints.ApplyProbeResult(connection, result, now);
        db.AiCapabilityProbes.Add(AiAdminEndpoints.BuildProbe(
            connection,
            result,
            now));
        await AiAdminEndpoints.ReconcileCurrentProfilesAsync(
            db,
            connection,
            connection.CreatedByStaffUserId,
            promptCatalog,
            now,
            replaceOtherProviders: ready && previousUpgrade is null,
            cancellationToken: cancellationToken,
            activate: ready);
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now),
            OccurredAt = now,
            EventType = AuditEventType,
            ObjectType = "ai_connection",
            ObjectId = connection.Id,
            Outcome = "succeeded",
            SafeMetadataJson = JsonSerializer.Serialize(new
            {
                previousModelId,
                modelId = ModelId,
                connection.CredentialRevision,
                capabilityProbeState = result.State,
                capabilityProbeErrorCode = result.SafeErrorCode,
            }),
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static bool WasBlockedByTransientUpgrade(AiConnectionEntity connection, AuditEventEntity audit)
    {
        // Earlier releases recorded even an inconclusive migration probe as complete.
        // Limit recovery to its exact blocked model, credential and probe; a later
        // administrator selection, reconnect or probe must not be overwritten.
        if (connection.State != "blocked" || connection.ModelId != ModelId
            || connection.LastCapabilityProbeState != "failed" || connection.LastCapabilityProbeAt != audit.OccurredAt
            || connection.LastCapabilityProbeErrorCode is not ("gemini_rate_limited" or "gemini_timeout"
                or "gemini_network_error" or "gemini_provider_unavailable")
            || audit.SafeMetadataJson is null)
            return false;
        try
        {
            using var document = JsonDocument.Parse(audit.SafeMetadataJson);
            var data = document.RootElement;
            return data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("CredentialRevision", out var revision) && revision.ValueKind == JsonValueKind.Number
                && revision.TryGetInt64(out var value) && value == connection.CredentialRevision
                && data.TryGetProperty("capabilityProbeErrorCode", out var error) && error.ValueKind == JsonValueKind.String
                && error.GetString() == connection.LastCapabilityProbeErrorCode;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<AiCapabilityProbeResult> ProbeAsync(
        AiConnectionEntity connection,
        IAiProviderFeaturePolicy featurePolicy,
        IAiSecretStore secretStore,
        IAiProviderClientResolver providerResolver,
        CancellationToken cancellationToken)
    {
        if (!featurePolicy.IsEnabled(connection.Provider))
        {
            return FailedProbe("ai_provider_feature_disabled");
        }

        if (string.IsNullOrWhiteSpace(connection.SecretReference))
        {
            return FailedProbe("ai_secret_unavailable");
        }

        try
        {
            using var secret = await secretStore.ReadAsync(
                new AiSecretReference(connection.SecretReference),
                cancellationToken);
            var settings = new AiConnectionSettings(
                connection.Id,
                connection.Provider,
                AiProviderCatalog.GeminiBaseAddress,
                ModelId,
                // Keep a network outage from holding up the Windows service's
                // upgrade startup. The configured request timeout is retained.
                TimeSpan.FromSeconds(25));
            return await providerResolver.GetRequired(connection.Provider)
                .ProbeAsync(settings, secret.Utf8Bytes, cancellationToken);
        }
        catch (AiProviderException exception)
        {
            return FailedProbe(exception.SafeErrorCode);
        }
        catch (KeyNotFoundException)
        {
            return FailedProbe("ai_secret_unavailable");
        }
        catch (Exception exception) when (exception is IOException
            or CryptographicException
            or FormatException
            or ArgumentException)
        {
            return FailedProbe("ai_secret_unavailable");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FailedProbe("gemini_timeout");
        }
    }

    private static AiCapabilityProbeResult FailedProbe(string safeErrorCode) =>
        new(false, false, false, false, false, "failed", safeErrorCode, null);
}
