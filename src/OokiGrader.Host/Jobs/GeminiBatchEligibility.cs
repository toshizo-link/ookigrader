using Microsoft.EntityFrameworkCore;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Host.Services;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Jobs;

internal static class GeminiBatchEligibility
{
    public static bool IsBatchRequest(AiRequestEntity request, AiTaskProfileEntity profile) =>
        request.GradingMode == "batch"
        || (request.GradingMode is null && profile.ProcessingStrategy == "gemini_batch");

    public static bool IsRetiredRunProfile(AiTaskProfileEntity profile, long originalRevision) =>
        !profile.Active && profile.Revision - 1 == originalRevision
        && AiTaskProfileRuntimePolicy.IsReadyApprovalState(profile.ApprovalState);

    public static async Task<HashSet<string>> EligibleRequestIdsAsync(OokiGraderDbContext db,
        IReadOnlyCollection<AiRequestEntity> requests, CancellationToken cancellationToken)
    {
        if (requests.Count == 0) return [];
        var first = requests.First();
        var profile = first.AiTaskProfile;
        if (requests.Any(row => row.AiTaskProfileId != profile.Id || row.TaskProfileRevision != first.TaskProfileRevision))
            return [];
        if (profile.Active)
            return CanDispatch(profile) && first.TaskProfileRevision == profile.Revision
                ? requests.Select(row => row.Id).ToHashSet(StringComparer.Ordinal) : [];
        if (!IsRetiredRunProfile(profile, first.TaskProfileRevision) || !CanDispatch(profile, continuingRun: true))
            return [];
        var sameLiveConnection = await db.AiTaskProfiles.AnyAsync(row => row.Active
            && row.TaskType == profile.TaskType && row.ModelId == profile.ModelId
            && row.AiConnectionId == profile.AiConnectionId, cancellationToken).ConfigureAwait(false);
        if (!sameLiveConnection) return [];
        var owners = requests.Select(row => row.EntityId).Distinct().ToArray();
        var continuations = await db.AiRequests.Where(row => owners.Contains(row.EntityId)
            && row.EntityType == "submission" && row.Purpose == AiTaskTypes.InitialGrading
            && row.AiTaskProfileId == profile.Id && row.TaskProfileRevision == first.TaskProfileRevision
            && row.State == "succeeded").Select(row => row.EntityId).Distinct().ToHashSetAsync(cancellationToken)
            .ConfigureAwait(false);
        return requests.Where(row => continuations.Contains(row.EntityId)).Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
    }

    public static bool CanDispatch(AiTaskProfileEntity profile, bool continuingRun = false)
    {
        var connection = profile.AiConnection;
        return (profile.Active || continuingRun) && profile.TaskType == AiTaskTypes.InitialGrading
            && AiTaskProfileRuntimePolicy.IsReadyApprovalState(profile.ApprovalState)
            && connection.Provider == AiProviders.GeminiDirect
            && profile.ModelId == connection.ModelId
            && profile.ConnectionRevision == connection.CredentialRevision
            && connection.State == "active" && connection.LastCapabilityProbeState == "passed"
            && connection.LastBatchCapabilityProbeState == "passed"
            && connection.LastBatchCapabilityProbeCredentialRevision == connection.CredentialRevision;
    }
}
