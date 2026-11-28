using Microsoft.EntityFrameworkCore;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Host.Jobs;
using OokiGrader.Host.Services;
using OokiGrader.Infrastructure.Persistence;

namespace OokiGrader.Host.Api;

internal static class CapabilitiesEndpoints
{
    private const string SelectedModel = AiProviderRuntime.GeminiModel;

    public static IEndpointRouteBuilder MapCapabilitiesEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/capabilities", GetAsync)
            .WithTags("Capabilities");
        return endpoints;
    }

    internal static async Task<RuntimeCapabilities> ReadAsync(
        OokiGraderDbContext db,
        IConfiguration configuration,
        IAiPromptBundleCatalog promptCatalog,
        CancellationToken cancellationToken)
    {
        var profiles = await db.AiTaskProfiles
            .AsNoTracking()
            .Where(profile =>
                profile.Active
                && AiTaskProfileRuntimePolicy.ReadyApprovalStates.Contains(
                    profile.ApprovalState))
            .Select(profile => new ReadyProfile(
                profile.TaskType,
                profile.ModelId,
                profile.ConnectionRevision,
                profile.PromptVersion,
                profile.SchemaVersion,
                profile.PromptContentHash,
                profile.AiConnection.Provider,
                profile.AiConnection.EndpointProfile,
                profile.AiConnection.ModelId,
                profile.AiConnection.CredentialRevision,
                profile.AiConnection.State,
                profile.AiConnection.LastCapabilityProbeState,
                profile.AiConnection.LastBatchCapabilityProbeState,
                profile.AiConnection
                    .LastBatchCapabilityProbeCredentialRevision))
            .ToArrayAsync(cancellationToken);

        var templateEnabled = configuration.GetValue(
            "Features:Ai.TemplateGeneration",
            false);
        var semanticEnabled = configuration.GetValue(
            "Features:Grading.Semantic",
            false);
        var reportsEnabled = configuration.GetValue(
            "Features:Reports.Pdf",
            false);
        var geminiDirectEnabled = configuration.GetValue(
            "Features:Ai.GeminiDirect",
            false);
        var openRouterEnabled = configuration.GetValue(
            "Features:Ai.OpenRouter",
            false);
        var standardAiEnabled = geminiDirectEnabled || openRouterEnabled;
        return new RuntimeCapabilities(
            new RuntimeReportCapabilities(reportsEnabled),
            new RuntimeAiCapabilities(
                Provider: AiProviders.GeminiDirect,
                ModelId: SelectedModel,
                TemplateGeneration: new RuntimeFeatureCapability(
                    templateEnabled && standardAiEnabled,
                    templateEnabled
                    && standardAiEnabled
                    && profiles.Any(profile => IsReady(
                        profile,
                        AiTaskTypes.TemplateExtraction,
                        promptCatalog.GetRequired(
                            AiTaskTypes.TemplateExtraction),
                        geminiDirectEnabled,
                        openRouterEnabled))),
                NameTranscription: new RuntimeFeatureCapability(
                    Enabled: standardAiEnabled,
                    Ready: standardAiEnabled
                    && profiles.Any(profile => IsReady(
                        profile,
                        AiTaskTypes.NameTranscription,
                        promptCatalog.GetRequired(
                            AiTaskTypes.NameTranscription),
                        geminiDirectEnabled,
                        openRouterEnabled))),
                SemanticGrading: new RuntimeFeatureCapability(
                    semanticEnabled && standardAiEnabled,
                    semanticEnabled
                    && standardAiEnabled
                    && profiles.Any(profile => IsReady(
                        profile,
                        AiTaskTypes.InitialGrading,
                        promptCatalog.GetRequired(
                            AiTaskTypes.InitialGrading),
                        geminiDirectEnabled,
                        openRouterEnabled))),
                GeminiBatch: new RuntimeFeatureCapability(
                    Enabled: semanticEnabled && geminiDirectEnabled,
                    Ready: semanticEnabled
                    && geminiDirectEnabled
                    && profiles.Any(profile =>
                        IsReady(
                            profile,
                            AiTaskTypes.InitialGrading,
                            promptCatalog.GetRequired(
                                AiTaskTypes.InitialGrading),
                            geminiDirectEnabled,
                            openRouterEnabled)
                        && profile.Provider == AiProviders.GeminiDirect
                        && profile.BatchProbeState == "passed"
                        && profile.BatchProbeCredentialRevision
                            == profile.ConnectionCredentialRevision)),
                OpenRouterEnabled: openRouterEnabled),
            new RuntimeSafetyCapabilities(
                configuration.GetValue(
                    "Features:Recognition.AutoAssign",
                    false),
                configuration.GetValue(
                    "Features:Grading.AutoFinalize",
                    false)));
    }

    internal static async Task<(string Mode, IResult? Error)> ResolveGradingModeAsync(
        string? mode, HttpContext context, OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        if (mode == "standard") return ("standard", null);
        if (mode is not (null or "batch"))
        {
            return (mode, ApiHelpers.Problem(context, StatusCodes.Status422UnprocessableEntity,
                "TEST_SESSION_GRADING_MODE_INVALID", "採点方法を確認してください",
                "すぐに採点する方法、またはまとめて採点する方法を選択してください。"));
        }

        var capabilities = await ReadAsync(db,
            context.RequestServices.GetRequiredService<IConfiguration>(),
            context.RequestServices.GetRequiredService<IAiPromptBundleCatalog>(),
            cancellationToken);
        if (capabilities.Ai.GeminiBatch.Ready) return ("batch", null);
        if (mode is null) return ("standard", null);
        return (mode, ApiHelpers.Problem(
            context, StatusCodes.Status409Conflict, "GEMINI_BATCH_UNAVAILABLE",
            "まとめ採点を利用できません", "管理者にAI設定の「Batch接続を確認」を依頼するか、「通常採点（即時）」を選択してください。"));
    }

    private static async Task<IResult> GetAsync(
        OokiGraderDbContext db,
        IConfiguration configuration,
        IAiPromptBundleCatalog promptCatalog,
        CancellationToken cancellationToken)
    {
        var capabilities = await ReadAsync(
            db,
            configuration,
            promptCatalog,
            cancellationToken);
        return Results.Ok(capabilities);
    }

    private static bool IsReady(
        ReadyProfile profile,
        string taskType,
        AiPromptBundle bundle,
        bool geminiDirectEnabled,
        bool openRouterEnabled) =>
        profile.TaskType == taskType
        && profile.ProfileModelId == profile.ConnectionModelId
        && profile.ProfileConnectionRevision
            == profile.ConnectionCredentialRevision
        && ((profile.Provider == AiProviders.GeminiDirect
                && geminiDirectEnabled)
            || (profile.Provider == AiProviders.OpenRouter
                && openRouterEnabled))
        && AiProviderCatalog.IsConnectionShapeValid(
            profile.Provider,
            profile.EndpointProfile,
            profile.ConnectionModelId)
        && AiProviderCatalog.SupportsImageTasks(
            profile.Provider,
            profile.ConnectionModelId)
        && profile.ConnectionState == "active"
        && profile.ProbeState == "passed"
        && profile.PromptVersion == bundle.PromptVersion
        && profile.SchemaVersion == bundle.SchemaVersion
        && profile.PromptContentHash == bundle.ContentHash;

    private sealed record ReadyProfile(
        string TaskType,
        string ProfileModelId,
        long ProfileConnectionRevision,
        string PromptVersion,
        string SchemaVersion,
        string PromptContentHash,
        string Provider,
        string EndpointProfile,
        string ConnectionModelId,
        int ConnectionCredentialRevision,
        string ConnectionState,
        string? ProbeState,
        string? BatchProbeState,
        int? BatchProbeCredentialRevision);
}

internal sealed record RuntimeCapabilities(
    RuntimeReportCapabilities Reports,
    RuntimeAiCapabilities Ai,
    RuntimeSafetyCapabilities Safety);

internal sealed record RuntimeReportCapabilities(bool PdfExport);

internal sealed record RuntimeAiCapabilities(
    string Provider,
    string ModelId,
    RuntimeFeatureCapability TemplateGeneration,
    RuntimeFeatureCapability NameTranscription,
    RuntimeFeatureCapability SemanticGrading,
    RuntimeFeatureCapability GeminiBatch,
    bool OpenRouterEnabled);

internal sealed record RuntimeFeatureCapability(bool Enabled, bool Ready);

internal sealed record RuntimeSafetyCapabilities(
    bool AutomaticAssignment,
    bool AutomaticFinalization);
