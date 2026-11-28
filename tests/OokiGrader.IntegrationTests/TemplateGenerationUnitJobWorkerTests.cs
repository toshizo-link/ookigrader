using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Domain.Templates;
using OokiGrader.Host.Common;
using OokiGrader.Host.Jobs;
using OokiGrader.Host.Services;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Infrastructure.Security;
using OokiGrader.Preprocessing;

namespace OokiGrader.IntegrationTests;

public sealed class TemplateGenerationUnitJobWorkerTests
{
    [Fact]
    public async Task DenseFillBlankTemplateCommitsOneValidatedExtraction()
    {
        await using var fixture = await WorkerFixture.CreateAsync(ProviderAction.Embedded);
        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.False(await fixture.Worker.ProcessNextAsync());
        Assert.Equal("MEDIUM", Assert.Single(fixture.Provider.Requests).ThinkingLevel);
        await using var db = await fixture.CreateDbContextAsync();
        var unit = await db.TemplateGenerationUnits.SingleAsync();
        Assert.Equal(TemplateGenerationUnitStatus.Extracted, unit.Status);
        Assert.Equal(1, (await db.AiRequests.SingleAsync()).AttemptNumber);
        Assert.Equal("settled", (await db.AiBudgetReservations.SingleAsync()).State);
    }

    [Fact]
    public async Task SpreadDetailsShareOneSourcePageAndOneExtractionRequest()
    {
        await using var fixture = await WorkerFixture.CreateAsync(ProviderAction.Embedded);
        fixture.PageExtractor.SupplyBookSpread = true;
        Assert.True(await fixture.Worker.ProcessNextAsync());
        var request = Assert.Single(fixture.Provider.Requests);
        Assert.Equal(["application/pdf", "image/jpeg", "image/jpeg"], request.Media.Select(part => part.MimeType));
        await using var db = await fixture.CreateDbContextAsync();
        var unit = await db.TemplateGenerationUnits.SingleAsync();
        Assert.Equal(TemplateGenerationUnitStatus.Extracted, unit.Status);
        Assert.Contains($"\"media_index\":1,\"page_id\":\"{unit.Id}:page:1\",\"side\":\"left\"", request.UserInstruction);
        Assert.Contains($"\"media_index\":2,\"page_id\":\"{unit.Id}:page:1\",\"side\":\"right\"", request.UserInstruction);
        Assert.Single(await db.AiRequests.ToListAsync());
    }

    [Fact]
    public async Task UnconfirmedAnswerRemovalIsFlaggedOnTheFirstDraft()
    {
        await using var fixture = await WorkerFixture.CreateAsync(ProviderAction.BadRemoval);
        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.False(await fixture.Worker.ProcessNextAsync());
        Assert.Single(fixture.Provider.Requests);
        await using var db = await fixture.CreateDbContextAsync();
        var unit = await db.TemplateGenerationUnits.SingleAsync();
        Assert.Contains("question.filled_answer_removal_unconfirmed", unit.ExtractionDraftJson);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public async Task ReusesSavedExtractionAfterInterruptedOrExplicitlyRetriedLegacyAudit(
        bool explicitRetry, bool changedKey, bool rotated)
    {
        await using var fixture = await WorkerFixture.CreateAsync(rotated
            ? [ProviderAction.RotatedExtraction]
            : [ProviderAction.Embedded, ProviderAction.Embedded]);
        Assert.True(await fixture.Worker.ProcessNextAsync());
        await using var db = await fixture.CreateDbContextAsync();
        var first = await db.AiRequests.SingleAsync(request => request.AttemptNumber == 1);
        var unit = await db.TemplateGenerationUnits.Include(item => item.Batch).SingleAsync();
        var job = await db.BackgroundJobs.SingleAsync();
        // Recreate the pre-upgrade state after the first response was accepted,
        // before any draft was published, with an interrupted verification call.
        unit.ExtractionDraftJson = null;
        unit.ExtractionDraftHash = null;
        unit.Status = explicitRetry ? TemplateGenerationUnitStatus.Failed : TemplateGenerationUnitStatus.Generating;
        unit.Batch.Status = explicitRetry ? TemplateGenerationBatchStatus.Failed : TemplateGenerationBatchStatus.Generating;
        unit.Batch.CompletedUnitCount = 0;
        unit.Batch.FailedUnitCount = explicitRetry ? 1 : 0;
        job.State = explicitRetry ? "failed" : "leased";
        job.LeaseOwner = explicitRetry ? null : "old-worker";
        job.LeaseExpiresAt = explicitRetry ? null : first.CreatedAt.AddMinutes(-1);
        job.AttemptCount = job.MaxAttempts;
        var lastExtractionId = await db.AiRequests.OrderByDescending(item => item.AttemptNumber)
            .Select(item => item.Id).FirstAsync();
        var auditId = UlidId.New(first.CreatedAt);
        var audit = new AiRequestEntity
        {
            Id = auditId, RequestKey = $"template_unit_run_{job.Id}_3_{auditId}",
            AiTaskProfileId = first.AiTaskProfileId, TaskProfileRevision = first.TaskProfileRevision,
            Purpose = first.Purpose, EntityType = first.EntityType, EntityId = first.EntityId,
            EntityRevision = first.EntityRevision, InputManifestHash = first.InputManifestHash,
            AttemptNumber = 3, RetryOfAiRequestId = lastExtractionId,
            State = explicitRetry ? "failed" : "dispatching", DispatchAttempt = 1,
            CreatedAt = first.CreatedAt, UpdatedAt = first.UpdatedAt,
        };
        db.AiRequests.Add(audit);
        var profile = await db.AiTaskProfiles.SingleAsync();
        if (!rotated)
        {
            var contentHash = await fixture.GetLegacyFirstPassHashAsync(first.RequestKey, releasedOrientation: true);
            var oldManifest = JsonSerializer.Serialize(new
            {
                contentHash,
                connectionId = profile.AiConnectionId,
                credentialRevision = profile.ConnectionRevision,
                taskProfileId = profile.Id,
                taskProfileRevision = first.TaskProfileRevision,
                profile.MaxOutputTokens,
                mediaResolution = "MEDIA_RESOLUTION_HIGH",
                thinkingLevel = "LOW",
            }, WorkerJsonOptions);
            first.InputManifestHash = Sha256(Encoding.UTF8.GetBytes(oldManifest));
            var replacement = (AiTaskProfileEntity)db.Entry(profile).CurrentValues.ToObject();
            profile.ThinkingLevel = "low";
            profile.PromptContentHash = ApprovedPromptBundleCatalog.LegacyTemplateExtractionContentHash;
            profile.Active = false;
            await db.SaveChangesAsync();
            replacement.Id = UlidId.New(first.CreatedAt);
            replacement.Revision = 1;
            if (changedKey)
            {
                var connection = await db.AiConnections.SingleAsync();
                connection.CredentialRevision++;
                replacement.ConnectionRevision = connection.CredentialRevision;
            }
            db.AiTaskProfiles.Add(replacement);
        }
        await db.SaveChangesAsync();
        if (explicitRetry) await fixture.RetryFailedAsync("upgrade-retry");

        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.Equal(TemplateGenerationUnitStatus.Extracted,
            (await db.TemplateGenerationUnits.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(changedKey ? 2 : 1, fixture.Provider.Requests.Count);
        Assert.Equal("failed", (await db.AiRequests.AsNoTracking().SingleAsync(item => item.Id == audit.Id)).State);
    }

    [Theory]
    [InlineData(AiFailureKind.Authentication, "gemini_authentication_failed", "AI_AUTHENTICATION_FAILED")]
    [InlineData(AiFailureKind.RateLimited, "gemini_rate_limited", "AI_RATE_LIMITED")]
    [InlineData(AiFailureKind.Timeout, "gemini_timeout", "AI_TIMEOUT")]
    [InlineData(AiFailureKind.TransientProvider, "gemini_internal_error", "AI_SERVICE_BUSY")]
    [InlineData(AiFailureKind.TransientProvider, "gemini_provider_unavailable", "AI_SERVICE_BUSY")]
    [InlineData(AiFailureKind.TransientProvider, "gemini_network_error", "AI_CONNECTION_INTERRUPTED")]
    [InlineData(AiFailureKind.TransientProvider, "openrouter_network_error", "AI_CONNECTION_INTERRUPTED")]
    [InlineData(AiFailureKind.RequestRejected, "gemini_response_schema_invalid", "AI_RESPONSE_SCHEMA_REJECTED")]
    [InlineData(AiFailureKind.RequestRejected, "gemini_thinking_config_invalid", "AI_THINKING_CONFIG_INVALID")]
    [InlineData(AiFailureKind.InvalidConfiguration, "gemini_model_not_found", "AI_MODEL_UNAVAILABLE")]
    [InlineData(AiFailureKind.InvalidResponse, "gemini_output_limit_exceeded", "AI_OUTPUT_LIMIT_EXCEEDED")]
    [InlineData(AiFailureKind.InvalidResponse, "gemini_json_invalid", "AI_STRUCTURED_OUTPUT_INVALID")]
    [InlineData(AiFailureKind.RequestRejected, "provider-secret-must-not-escape", "AI_REQUEST_REJECTED")]
    public async Task ProviderFailuresRemainSpecificSafeAndClearOnManualRetry(
        AiFailureKind kind,
        string safeProviderCode,
        string expectedErrorCode)
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            ProviderAction.Fail, ProviderAction.Extract);
        fixture.Provider.Failure = new AiProviderException(kind, safeProviderCode,
            isTransient: false, innerException: new InvalidOperationException("secret-detail-must-not-escape"));

        Assert.True(await fixture.Worker.ProcessNextAsync());
        await using (var db = await fixture.CreateDbContextAsync())
        {
            var request = await db.AiRequests.AsNoTracking().SingleAsync();
            var unit = await db.TemplateGenerationUnits.AsNoTracking().SingleAsync();
            var job = await db.BackgroundJobs.AsNoTracking().SingleAsync();
            Assert.Equal(expectedErrorCode, request.ErrorCode);
            Assert.Null(request.SafeErrorDetail);
            Assert.Equal(expectedErrorCode, job.ErrorCode);
            Assert.Contains(expectedErrorCode, unit.WarningsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("must-not-escape", unit.WarningsJson, StringComparison.Ordinal);
        }
        var retried = await fixture.RetryFailedAsync("retry-specific-error");
        Assert.DoesNotContain(Assert.Single(retried.Units).Warnings.EnumerateArray(),
            warning => warning.GetProperty("code").GetString() == expectedErrorCode);
        Assert.True(await fixture.Worker.ProcessNextAsync());
        await using var finalDb = await fixture.CreateDbContextAsync();
        Assert.Equal(TemplateGenerationUnitStatus.Extracted,
            await finalDb.TemplateGenerationUnits.Select(unit => unit.Status).SingleAsync());
    }

    private static readonly JsonSerializerOptions WorkerJsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public async Task ExpiredLeaseRecoversAmbiguousDispatchWithoutProviderCall()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        const long reservedUsdMicros = 37;
        await fixture.SeedExpiredDispatchingRunAsync(reservedUsdMicros);

        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.Empty(fixture.Provider.Requests);

        await using var db = await fixture.CreateDbContextAsync();
        var request = await db.AiRequests.AsNoTracking().SingleAsync();
        var reservation = await db.AiBudgetReservations
            .AsNoTracking()
            .SingleAsync();
        var unit = await db.TemplateGenerationUnits.AsNoTracking().SingleAsync();
        var batch = await db.TemplateGenerationBatches.AsNoTracking().SingleAsync();
        var job = await db.BackgroundJobs.AsNoTracking().SingleAsync();

        Assert.Equal("failed", request.State);
        Assert.True(request.PossibleDuplicate);
        Assert.Equal("AI_GENERATION_INTERRUPTED", request.ErrorCode);
        Assert.Equal("recovered_dispatching_request", request.SafeErrorDetail);
        Assert.NotNull(request.CompletedAt);
        Assert.Equal("settled", reservation.State);
        Assert.Equal(reservedUsdMicros, reservation.ReservedUsdMicros);
        Assert.Equal(reservedUsdMicros, reservation.ActualUsdMicros);
        Assert.NotNull(reservation.SettledAt);
        Assert.Equal(TemplateGenerationUnitStatus.Failed, unit.Status);
        Assert.Equal(TemplateGenerationBatchStatus.Failed, batch.Status);
        Assert.Equal("AI_GENERATION_INTERRUPTED", batch.LastErrorCode);
        Assert.Equal("failed", job.State);
        Assert.Equal("AI_GENERATION_INTERRUPTED", job.ErrorCode);
        Assert.Equal(job.MaxAttempts, job.AttemptCount);
        Assert.Null(job.LeaseOwner);
        Assert.Null(job.LeaseExpiresAt);
        Assert.NotNull(job.CompletedAt);
    }

    [Fact]
    public async Task UprightUnitMakesExactlyOneProviderCall()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            ProviderAction.Extract);

        Assert.True(await fixture.Worker.ProcessNextAsync());

        Assert.Single(fixture.Provider.Requests);
        Assert.Single(fixture.PageExtractor.Calls);
        Assert.Empty(fixture.PageExtractor.Calls[0]);
        await using var db = await fixture.CreateDbContextAsync();
        var unit = await db.TemplateGenerationUnits.AsNoTracking().SingleAsync();
        var request = await db.AiRequests.AsNoTracking().SingleAsync();
        Assert.Equal(TemplateGenerationUnitStatus.Extracted, unit.Status);
        Assert.Equal(0, unit.OrientationAttemptCount);
        Assert.Equal(1, request.AttemptNumber);
        Assert.Equal("succeeded", request.State);
    }

    [Fact]
    public async Task RotatedPageReturnsItsQuestionsAndCorrectedPdfWithOneProviderCall()
    {
        await using var fixture = await WorkerFixture.CreateAsync(ProviderAction.RotatedExtraction);
        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.Single(fixture.Provider.Requests);
        Assert.Equal(2, fixture.PageExtractor.Calls.Count);
        Assert.Equal(90, fixture.PageExtractor.Calls[1][1]);
        await using var db = await fixture.CreateDbContextAsync();
        var unit = await db.TemplateGenerationUnits.SingleAsync();
        Assert.Equal(TemplateGenerationUnitStatus.Extracted, unit.Status);
        Assert.Equal(1, unit.OrientationAttemptCount);
        Assert.Equal("pageRangeAndRotation", (await db.TemplateGenerationDerivedSources.SingleAsync()).DerivationType);
        Assert.Equal("succeeded", (await db.AiRequests.SingleAsync()).State);
    }

    [Fact]
    public async Task NewRotationOnlyResponseFailsWithoutAnExtraProviderCall()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            ProviderAction.Rotate);

        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.False(await fixture.Worker.ProcessNextAsync());

        Assert.Single(fixture.Provider.Requests);
        Assert.Single(fixture.PageExtractor.Calls);
        await using var db = await fixture.CreateDbContextAsync();
        var unit = await db.TemplateGenerationUnits.AsNoTracking().SingleAsync();
        var batch = await db.TemplateGenerationBatches.AsNoTracking().SingleAsync();
        var job = await db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(TemplateGenerationUnitStatus.Failed, unit.Status);
        Assert.Equal(TemplateGenerationBatchStatus.Failed, batch.Status);
        Assert.Equal("ORIENTATION_RESPONSE_INVALID", batch.LastErrorCode);
        Assert.Equal("failed", job.State);
        Assert.Equal("ORIENTATION_RESPONSE_INVALID", job.ErrorCode);
        Assert.Contains(
            "ORIENTATION_RESPONSE_INVALID",
            unit.WarningsJson,
            StringComparison.Ordinal);
        Assert.Equal(1, await db.AiRequests.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task ManualRetryAfterFirstProviderFailureStartsFreshLinkedRun()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            ProviderAction.Fail,
            ProviderAction.Extract);

        Assert.True(await fixture.Worker.ProcessNextAsync());
        var retried = await fixture.RetryFailedAsync("retry-first-provider-failure");

        var queued = Assert.Single(retried.Units);
        Assert.Equal(TemplateGenerationUnitStatus.Queued, queued.Status);
        Assert.Equal(0, queued.OrientationAttemptCount);
        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.Equal(2, fixture.Provider.Requests.Count);

        await using var db = await fixture.CreateDbContextAsync();
        var jobs = await db.BackgroundJobs.AsNoTracking().ToArrayAsync();
        var requests = await db.AiRequests
            .AsNoTracking()
            .OrderBy(item => item.Id)
            .ToArrayAsync();
        Assert.Equal(2, jobs.Length);
        Assert.Equal(2, requests.Length);
        var failed = Assert.Single(requests, item => item.State == "failed");
        var succeeded = Assert.Single(requests, item => item.State == "succeeded");
        Assert.Equal(failed.Id, succeeded.RetryOfAiRequestId);
        Assert.All(jobs, job => Assert.InRange(
            CountRequestsForRun(job.Id, requests),
            1,
            2));
        Assert.Equal(
            TemplateGenerationUnitStatus.Extracted,
            await db.TemplateGenerationUnits
                .AsNoTracking()
                .Select(item => item.Status)
                .SingleAsync());
    }

    [Theory]
    [InlineData(false, 90)]
    [InlineData(true, 0)]
    public async Task OldRotationOnlyResultCanBeResumedOnce(bool undoEarlierTurn, int expectedDegrees)
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            undoEarlierTurn ? ProviderAction.CounterRotatedExtraction : ProviderAction.Extract);
        await fixture.SeedSavedRotationAsync();
        Assert.True(await fixture.Worker.ProcessNextAsync());
        await using var db = await fixture.CreateDbContextAsync();
        var job = await db.BackgroundJobs.SingleAsync();
        Assert.True(job.State == "succeeded", job.ErrorCode);
        Assert.Single(fixture.Provider.Requests);
        var unit = await db.TemplateGenerationUnits.SingleAsync();
        Assert.Equal(TemplateGenerationUnitStatus.Extracted, unit.Status);
        var rotations = JsonSerializer.Deserialize<AppliedPageRotation[]>(unit.AppliedRotationsJson, WorkerJsonOptions)!;
        Assert.Equal(expectedDegrees, rotations.SingleOrDefault()?.ClockwiseDegrees ?? 0);
        Assert.Equal(2, await db.AiRequests.CountAsync());
    }

    [Fact]
    public async Task AuditAndFinalCheckDoNotTriggerClassificationCalls()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            ProviderAction.Extract);

        Assert.True(await fixture.Worker.ProcessNextAsync());

        var providerRequest = Assert.Single(fixture.Provider.Requests);
        Assert.Equal(AiTaskTypes.TemplateExtraction, providerRequest.TaskType);
        Assert.Equal("template-extract-v2.1.0", providerRequest.PromptVersion);
        Assert.Equal("template_extract_v6", providerRequest.SchemaVersion);
        await using var db = await fixture.CreateDbContextAsync();
        Assert.Equal(
            TemplateGenerationBatchStatus.NeedsFinalCheck,
            await db.TemplateGenerationBatches
                .AsNoTracking()
                .Select(item => item.Status)
                .SingleAsync());
        var request = await db.AiRequests.AsNoTracking().SingleAsync();
        Assert.Equal("template_generation_unit", request.EntityType);
        Assert.Equal(AiTaskTypes.TemplateExtraction, request.Purpose);
        Assert.Contains(
            await db.AuditEvents.AsNoTracking().ToArrayAsync(),
            item => item.EventType == "TemplateUnitExtracted");
    }

    [Fact]
    public async Task RecreatedBatchReusesDerivedObjectWithUnitScopedProvenance()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            ProviderAction.Extract,
            ProviderAction.Extract);

        Assert.True(await fixture.Worker.ProcessNextAsync());
        await fixture.QueueRecreatedBatchAsync();
        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.False(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var units = await db.TemplateGenerationUnits
            .AsNoTracking()
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .ToArrayAsync();
        var derivedSources = await db.TemplateGenerationDerivedSources
            .AsNoTracking()
            .OrderBy(item => item.UnitId)
            .ToArrayAsync();
        var derivedReferences = await db.FileReferences
            .AsNoTracking()
            .Where(item => item.OwnerType == "template_generation_unit"
                && item.Purpose == "derived_source")
            .OrderBy(item => item.OwnerId)
            .ToArrayAsync();
        var derivedObjects = await db.FileObjects
            .AsNoTracking()
            .Where(item => item.StorageClass
                == ContentStorageClass.TemplateDerived.ToString())
            .ToArrayAsync();

        Assert.Equal(2, units.Length);
        Assert.All(
            units,
            item => Assert.Equal(
                TemplateGenerationUnitStatus.Extracted,
                item.Status));
        Assert.Equal(
            units.Select(item => item.Id).Order(),
            derivedSources.Select(item => item.UnitId).Order());
        Assert.Equal(
            units.Select(item => item.Id).Order(),
            derivedReferences.Select(item => item.OwnerId).Order());
        Assert.Equal(2, derivedSources.Select(item => item.FileReferenceId).Distinct().Count());
        Assert.Single(derivedSources.Select(item => item.DerivedContentSha256).Distinct());
        Assert.Single(derivedObjects);
        Assert.Single(derivedReferences.Select(item => item.FileObjectId).Distinct());
        Assert.Equal(derivedObjects[0].Id, derivedReferences[0].FileObjectId);
        Assert.Equal(2, derivedObjects[0].ReferenceCountCache);
    }

    private enum ProviderAction
    {
        Extract,
        Rotate,
        RotatedExtraction,
        CounterRotatedExtraction,
        Fail,
        BadRemoval,
        Embedded,
    }

    private static int CountRequestsForRun(
        string jobId,
        IEnumerable<AiRequestEntity> requests) =>
        RequestsForRun(jobId, requests).Length;

    private static AiRequestEntity[] RequestsForRun(
        string jobId,
        IEnumerable<AiRequestEntity> requests) =>
        requests
            .Where(item => item.RequestKey.StartsWith(
                $"template_unit_run_{jobId}_",
                StringComparison.Ordinal))
            .OrderBy(item => item.AttemptNumber)
            .ToArray();

    private sealed class WorkerFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;
        private readonly MemoryContentStore _contentStore;
        private readonly IUlidGenerator _ids;
        private readonly TimeProvider _timeProvider;

        private WorkerFixture(
            SqliteConnection connection,
            ServiceProvider services,
            RecordingProvider provider,
            RecordingPageRangeExtractor pageExtractor,
            MemoryContentStore contentStore,
            IUlidGenerator ids,
            TimeProvider timeProvider)
        {
            _connection = connection;
            _services = services;
            _contentStore = contentStore;
            _ids = ids;
            _timeProvider = timeProvider;
            Provider = provider;
            PageExtractor = pageExtractor;
            Worker = services.GetRequiredService<TemplateGenerationUnitJobWorker>();
        }

        public TemplateGenerationUnitJobWorker Worker { get; }
        public RecordingProvider Provider { get; }
        public RecordingPageRangeExtractor PageExtractor { get; }
        public string StaffId { get; private set; } = string.Empty;

        public static async Task<WorkerFixture> CreateAsync(
            params ProviderAction[] actions)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var now = new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);
            var timeProvider = new FixedTimeProvider(now);
            var contentStore = new MemoryContentStore();
            var pageExtractor = new RecordingPageRangeExtractor();
            var provider = new RecordingProvider(actions);
            var secretStore = new InMemoryAiSecretStore();
            var ids = new UlidGenerator(timeProvider);
            var promptCatalog = new ApprovedPromptBundleCatalog();
            var writeCoordinator = new SemaphoreWriteCoordinator();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(timeProvider);
            services.AddSingleton<IUlidGenerator>(ids);
            services.AddSingleton<IWriteCoordinator>(writeCoordinator);
            services.AddSingleton<IContentStore>(contentStore);
            services.AddSingleton<IPdfPageRangeExtractor>(pageExtractor);
            services.AddSingleton<IAiProviderClient>(provider);
            services.AddSingleton<IAiProviderClientResolver>(
                new AiProviderClientResolver([provider]));
            services.AddSingleton<IAiProviderFeaturePolicy>(
                AiProviderFeaturePolicy.AllowAll);
            services.AddSingleton<IAiPromptBundleCatalog>(promptCatalog);
            services.AddSingleton<IAiSecretStore>(secretStore);
            services.AddSingleton(
                Options.Create(new TemplateGenerationUnitJobWorkerOptions()));
            services.AddDbContextFactory<OokiGraderDbContext>(
                options => options.UseSqlite(connection));
            services.AddSingleton<TemplateGenerationUnitJobWorker>();
            var serviceProvider = services.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true,
                });
            try
            {
                var fixture = new WorkerFixture(
                    connection,
                    serviceProvider,
                    provider,
                    pageExtractor,
                    contentStore,
                    ids,
                    timeProvider);
                await fixture.SeedAsync(
                    contentStore,
                    secretStore,
                    ids,
                    timeProvider,
                    promptCatalog);
                return fixture;
            }
            catch
            {
                await serviceProvider.DisposeAsync();
                await connection.DisposeAsync();
                throw;
            }
        }

        public Task<OokiGraderDbContext> CreateDbContextAsync() =>
            _services
                .GetRequiredService<IDbContextFactory<OokiGraderDbContext>>()
                .CreateDbContextAsync();

        public async Task<TemplateGenerationBatchSnapshot> RetryFailedAsync(
            string operationId)
        {
            await using var db = await CreateDbContextAsync();
            var batch = await db.TemplateGenerationBatches
                .AsNoTracking()
                .SingleAsync();
            var batchService = new TemplateGenerationBatchService(
                db,
                _contentStore,
                new OnePageCountReader(),
                new TemplateUnitPlanner(),
                _ids,
                _timeProvider,
                Options.Create(new TemplateGenerationBatchOptions()),
                _services.GetRequiredService<IAiPromptBundleCatalog>(),
                _services.GetRequiredService<IAiProviderFeaturePolicy>());
            var service = new TemplateGenerationFinalizationService(
                db,
                _ids,
                _timeProvider,
                batchService);
            return await service.RetryAsync(
                new RetryTemplateGenerationBatchCommand(
                    batch.Id,
                    batch.Revision,
                    StaffId,
                    IsAdministrator: false,
                    operationId,
                    CorrelationId: operationId),
                CancellationToken.None);
        }

        public async Task<string> GetLegacyFirstPassHashAsync(string requestKey, bool releasedOrientation = false)
        {
            await using var db = await CreateDbContextAsync();
            var unit = await db.TemplateGenerationUnits.SingleAsync();
            var batch = await db.TemplateGenerationBatches.SingleAsync();
            var taskProfile = await db.AiTaskProfiles
                .Include(item => item.AiConnection)
                .SingleAsync();
            var source = await db.FileReferences
                .AsNoTracking()
                .Include(item => item.FileObject)
                .SingleAsync(item => item.OwnerType == "upload_session"
                    && item.OwnerId == batch.SourceId
                    && item.Purpose == "template_source");
            var profile = JsonSerializer.Deserialize<TemplateGenerationProfile>(
                    unit.GenerationProfileJson,
                    WorkerJsonOptions)
                ?? throw new InvalidOperationException(
                    "The seeded unit generation profile is missing.");
            var instruction = TemplateExtractionInstructionBuilder.Build(
                requestKey,
                unit.Id,
                profile,
                rotationsWereApplied: false, legacyOrientationContract: releasedOrientation);
            var derivedBytes = Encoding.UTF8.GetBytes(
                $"derived:{profile.FirstPage}-{profile.LastPage}:");
            var derivedSha256 = Sha256(derivedBytes);
            var bundle = _services
                .GetRequiredService<IAiPromptBundleCatalog>()
                .GetRequired(AiTaskTypes.TemplateExtraction);
            var canonical = JsonSerializer.Serialize(
                new
                {
                    batchPlanHash = batch.PlanHash,
                    unitId = unit.Id,
                    profileHash = profile.ComputeHash(),
                    profile.FirstPage,
                    profile.LastPage,
                    profile.StepSetIndex,
                    profile.StepVariationIndex,
                    sha256 = source.FileObject.Sha256,
                    derivedSha256,
                    rotations = Array.Empty<AppliedPageRotation>(),
                    instruction.Fingerprint,
                    promptBundleHash = releasedOrientation ? ApprovedPromptBundleCatalog.LegacyTemplateExtractionContentHash : bundle.ContentHash,
                    bundle.PromptVersion,
                    bundle.SchemaVersion,
                    provider = taskProfile.AiConnection.Provider,
                    model = taskProfile.AiConnection.ModelId,
                    preprocessingPipeline =
                        PreprocessingOptions.DefaultPipelineVersion,
                    attemptNumber = 1,
                },
                WorkerJsonOptions);
            return Sha256(Encoding.UTF8.GetBytes(canonical));
        }

        public async Task SeedSavedRotationAsync()
        {
            await SeedExpiredDispatchingRunAsync(37);
            await using var db = await CreateDbContextAsync();
            var unit = await db.TemplateGenerationUnits.SingleAsync();
            var request = await db.AiRequests.SingleAsync();
            var profile = await db.AiTaskProfiles.SingleAsync();
            var job = await db.BackgroundJobs.SingleAsync();
            job.AttemptCount = 0;
            var json = JsonSerializer.Serialize(new
            {
                schema_version = "template_extract_v6", request_key = request.RequestKey, action = "rotate",
                orientation = new { pages = new[] { new { page_id = $"{unit.Id}:page:1", clockwise_degrees_to_upright = 90, confidence = 0.99 } } },
                metadata = (object?)null, pages = Array.Empty<object>(),
            });
            request.State = "succeeded";
            request.InputManifestHash = await GetLegacyFirstPassHashAsync(request.RequestKey, releasedOrientation: true);
            request.CreatedAt = (await db.AiConnections.SingleAsync()).UpdatedAt.AddSeconds(1);
            request.ValidatedResponseJson = json;
            request.AcceptedResponseHash = Sha256(Encoding.UTF8.GetBytes(json));
            profile.PromptContentHash = ApprovedPromptBundleCatalog.LegacyTemplateExtractionContentHash;
            // The pre-connection-binding format compares the approved profile revision.
            await db.SaveChangesAsync();
            request.TaskProfileRevision = profile.Revision;
            await db.SaveChangesAsync();
        }

        public async Task SeedExpiredDispatchingRunAsync(long reservedUsdMicros)
        {
            await using var db = await CreateDbContextAsync();
            var now = _timeProvider.GetUtcNow();
            var job = await db.BackgroundJobs.SingleAsync();
            var unit = await db.TemplateGenerationUnits.SingleAsync();
            var taskProfile = await db.AiTaskProfiles.SingleAsync();
            var requestId = _ids.NewId();
            var requestKey = $"template_unit_run_{job.Id}_1_{requestId}";
            job.State = "leased";
            job.LeaseOwner = "crashed-worker";
            job.LeaseExpiresAt = now.AddSeconds(-1);
            job.AttemptCount = job.MaxAttempts;
            job.StartedAt = now.AddMinutes(-1);
            job.UpdatedAt = now.AddMinutes(-1);
            unit.Status = TemplateGenerationUnitStatus.Generating;
            db.AiRequests.Add(new AiRequestEntity
            {
                Id = requestId,
                RequestKey = requestKey,
                AiTaskProfileId = taskProfile.Id,
                TaskProfileRevision = taskProfile.Revision,
                Purpose = AiTaskTypes.TemplateExtraction,
                EntityType = "template_generation_unit",
                EntityId = unit.Id,
                EntityRevision = unit.Revision,
                InputManifestHash = await GetLegacyFirstPassHashAsync(requestKey),
                AttemptNumber = 1,
                State = "dispatching",
                DispatchAttempt = 1,
                CreatedAt = now.AddMinutes(-1),
                UpdatedAt = now.AddMinutes(-1),
                DispatchedAt = now.AddMinutes(-1),
            });
            db.AiBudgetReservations.Add(new AiBudgetReservationEntity
            {
                Id = _ids.NewId(),
                AiRequestId = requestId,
                UsageDay = DateOnly.FromDateTime(now.UtcDateTime),
                UsageMonth = $"{now.Year:0000}-{now.Month:00}",
                ReservedUsdMicros = reservedUsdMicros,
                ActualUsdMicros = 0,
                State = "reserved",
                CreatedAt = now.AddMinutes(-1),
            });
            await db.SaveChangesAsync();
        }

        public async Task QueueRecreatedBatchAsync()
        {
            await using var db = await CreateDbContextAsync();
            var source = await db.UploadSessions
                .AsNoTracking()
                .SingleAsync(item => item.Purpose == "template_source");
            var service = new TemplateGenerationBatchService(
                db,
                _services.GetRequiredService<IContentStore>(),
                new OnePageCountReader(),
                new TemplateUnitPlanner(),
                _services.GetRequiredService<IUlidGenerator>(),
                _services.GetRequiredService<TimeProvider>(),
                Options.Create(new TemplateGenerationBatchOptions()),
                _services.GetRequiredService<IAiPromptBundleCatalog>(),
                _services.GetRequiredService<IAiProviderFeaturePolicy>());
            var created = await service.CreateAsync(
                new CreateTemplateGenerationBatchCommand(
                    source.Id,
                    TestType.Hop,
                    "算数",
                    AnswerStyle: null,
                    ExpectedSourceRowVersion: source.Revision,
                    source.CreatedByStaffUserId,
                    IsAdministrator: false,
                    OperationId: Guid.NewGuid().ToString(),
                    CorrelationId: "unit-worker-recreate"),
                CancellationToken.None);
            db.ChangeTracker.Clear();
            await service.GenerateAsync(
                new GenerateTemplateGenerationBatchCommand(
                    created.Id,
                    created.RowVersion,
                    source.CreatedByStaffUserId,
                    IsAdministrator: false,
                    OperationId: Guid.NewGuid().ToString(),
                    CorrelationId: "unit-worker-recreate-generate"),
                CancellationToken.None);
        }

        private async Task SeedAsync(
            MemoryContentStore contentStore,
            InMemoryAiSecretStore secretStore,
            UlidGenerator ids,
            TimeProvider timeProvider,
            ApprovedPromptBundleCatalog promptCatalog)
        {
            await using var db = await CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
            var now = timeProvider.GetUtcNow();
            var staffId = ids.NewId();
            StaffId = staffId;
            var uploadId = ids.NewId();
            var connectionId = ids.NewId();
            var sourceBytes = Encoding.UTF8.GetBytes("fixture-pdf-source");
            var sourceHash = Sha256(sourceBytes);
            contentStore.Add(sourceHash, sourceBytes);
            var secretReference = await secretStore.WriteAsync(
                connectionId,
                1,
                "fixture-provider-key".AsMemory());
            var bundle = promptCatalog.GetRequired(AiTaskTypes.TemplateExtraction);
            db.StaffUsers.Add(new StaffUserEntity
            {
                Id = staffId,
                Username = "unit.worker",
                UsernameNormalized = "unit.worker",
                DisplayName = "生成担当",
                PasswordHash = "argon2id:test",
                PasswordAlgorithm = "argon2id",
                PasswordAlgorithmVersion = 1,
                Status = "active",
                CredentialChangedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.UploadSessions.Add(new UploadSessionEntity
            {
                Id = uploadId,
                CreatedByStaffUserId = staffId,
                Purpose = "template_source",
                DestinationType = "template_source",
                OriginalFileName = "HOP算数_小学4年.pdf",
                DeclaredMimeType = "application/pdf",
                ExpectedBytes = sourceBytes.Length,
                CurrentBytes = sourceBytes.Length,
                FinalSha256 = sourceHash,
                IncomingRelativePath = "incoming/source.part",
                State = "completed",
                ExpiresAt = now.AddHours(24),
                CreatedAt = now,
                UpdatedAt = now,
            });
            var fileObjectId = ids.NewId();
            db.FileObjects.Add(new FileObjectEntity
            {
                Id = fileObjectId,
                Sha256 = sourceHash,
                Bytes = sourceBytes.Length,
                VerifiedMime = "application/pdf",
                Extension = "pdf",
                RelativeObjectPath = $"template/source/{sourceHash}.pdf",
                StorageClass = ContentStorageClass.TemplateSource.ToString(),
                RetentionClass = "template_source",
                State = "available",
                CreatedAt = now,
                VerifiedAt = now,
                ReferenceCountCache = 1,
            });
            db.FileReferences.Add(new FileReferenceEntity
            {
                Id = ids.NewId(),
                FileObjectId = fileObjectId,
                OwnerType = "upload_session",
                OwnerId = uploadId,
                Purpose = "template_source",
                RetentionAnchorAt = now,
                CreatedAt = now,
            });
            db.AiConnections.Add(new AiConnectionEntity
            {
                Id = connectionId,
                Provider = AiProviders.GeminiDirect,
                EndpointProfile = AiProviderCatalog.GeminiEndpointProfile,
                ModelId = "gemini-3.5-flash-lite",
                SecretReference = secretReference.Value,
                KeyFingerprint = new string('a', 64),
                CredentialRevision = 1,
                TimeoutSeconds = 75,
                ConcurrencyLimit = 2,
                State = "active",
                LastCapabilityProbeState = "passed",
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.AiTaskProfiles.Add(new AiTaskProfileEntity
            {
                Id = ids.NewId(),
                Name = "Template unit fixture",
                TaskType = AiTaskTypes.TemplateExtraction,
                AiConnectionId = connectionId,
                ConnectionRevision = 1,
                ModelId = "gemini-3.5-flash-lite",
                ProcessingStrategy = "queued_standard",
                PromptVersion = bundle.PromptVersion,
                SchemaVersion = bundle.SchemaVersion,
                PromptContentHash = bundle.ContentHash,
                ThinkingLevel = "medium",
                MediaResolution = "high",
                MaxOutputTokens = 8_192,
                ConcurrencyLimit = 1,
                ApprovalState = "production_approved",
                Active = true,
                ActivatedAt = now,
                ActivatedByStaffUserId = staffId,
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var batchService = new TemplateGenerationBatchService(
                db,
                contentStore,
                new OnePageCountReader(),
                new TemplateUnitPlanner(),
                ids,
                timeProvider,
                Options.Create(new TemplateGenerationBatchOptions()),
                promptCatalog,
                AiProviderFeaturePolicy.AllowAll);
            var created = await batchService.CreateAsync(
                new CreateTemplateGenerationBatchCommand(
                    uploadId,
                    TestType.Hop,
                    "算数",
                    AnswerStyle: null,
                    ExpectedSourceRowVersion: 1,
                    staffId,
                    IsAdministrator: false,
                    OperationId: Guid.NewGuid().ToString(),
                    CorrelationId: "unit-worker-create"),
                CancellationToken.None);
            db.ChangeTracker.Clear();
            await batchService.GenerateAsync(
                new GenerateTemplateGenerationBatchCommand(
                    created.Id,
                    created.RowVersion,
                    staffId,
                    IsAdministrator: false,
                    OperationId: Guid.NewGuid().ToString(),
                    CorrelationId: "unit-worker-generate"),
                CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class RecordingProvider : IAiProviderClient
    {
        private readonly Queue<ProviderAction> _actions;

        public RecordingProvider(IEnumerable<ProviderAction> actions)
        {
            _actions = new Queue<ProviderAction>(actions);
        }

        public string Provider => AiProviders.GeminiDirect;
        public List<AiProviderRequest> Requests { get; } = [];
        public AiProviderException? Failure { get; set; }

        public Task<AiProviderResponse> GenerateAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            AiProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request with
            {
                Media = request.Media.Select(item => item with
                {
                    Bytes = item.Bytes.ToArray(),
                }).ToArray(),
            });
            var action = _actions.Count == 0
                ? throw new InvalidOperationException("Unexpected provider call.")
                : _actions.Dequeue();
            if (action == ProviderAction.Fail)
            {
                throw Failure ?? new AiProviderException(
                    AiFailureKind.TransientProvider,
                    "fixture_provider_unavailable",
                    isTransient: true);
            }

            return Task.FromResult(CreateResponse(connection, request, action));
        }

        public Task<AiCapabilityProbeResult> ProbeAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static AiProviderResponse CreateResponse(
            AiConnectionSettings connection,
            AiProviderRequest request,
            ProviderAction action)
        {
            var contractStart = request.UserInstruction.LastIndexOf(
                "{\"schema_version\"",
                StringComparison.Ordinal);
            using var contract = JsonDocument.Parse(
                request.UserInstruction[contractStart..]);
            var requestKey = contract.RootElement
                .GetProperty("request_key")
                .GetString()!;
            var source = contract.RootElement.GetProperty("sources")[0];
            var sourceId = source.GetProperty("source_id").GetString()!;
            var pageIds = source.GetProperty("pages")
                .EnumerateArray()
                .Select(item => item.GetProperty("page_id").GetString()!)
                .ToArray();
            var orientation = pageIds.Select(pageId => new
            {
                page_id = pageId,
                clockwise_degrees_to_upright =
                    action switch
                    {
                        ProviderAction.Rotate or ProviderAction.RotatedExtraction => 90,
                        ProviderAction.CounterRotatedExtraction => 270,
                        _ => 0,
                    },
                confidence = 0.99,
            }).ToArray();
            var questionCount = action == ProviderAction.Embedded ? 3 : 1;
            object[] pages = action == ProviderAction.Rotate
                ? []
                :
                [
                    new
                    {
                        source_id = sourceId,
                        page_number = 1,
                        detected_answer_slot_count = questionCount,
                        questions = Enumerable.Range(1, questionCount)
                            .Select(index => new
                            {
                                source_key = $"page-1-slot-{index}",
                                display_label = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                major_question_label = (string?)null,
                                middle_question_label = "設問",
                                minor_question_label = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                question_text = action == ProviderAction.Embedded ? "1 + 1 は［　］です。" : "1 + 1 はいくつですか。",
                                answer_slot_ordinal = index,
                                answer_slot_count = 1,
                                filled_answer_removed = action != ProviderAction.BadRemoval,
                                is_embedded_fill_blank = action == ProviderAction.Embedded,
                                question_type = "numeric",
                                expected_answer = "2",
                                answer_provenance = "ai_proposed",
                                answer_source = (object?)null,
                                accepted_variants = Array.Empty<string>(),
                                suggested_points_milli = 1_000,
                                allow_non_kanji_suggestion = false,
                                requires_complete_answer_suggestion = false,
                                answer_order_insensitive_suggestion = false,
                                requires_teacher_answer = false,
                                confidence = 0.99,
                                warnings = Array.Empty<string>(),
                            }).ToArray(),
                    },
                ];
            var root = JsonSerializer.SerializeToElement(new
            {
                schema_version = "template_extract_v6",
                request_key = requestKey,
                action = action == ProviderAction.Rotate ? "rotate" : "extract",
                orientation = new { pages = orientation },
                metadata = action == ProviderAction.Rotate
                    ? null
                    : new
                    {
                        printed_test_name = "HOP算数 第1回",
                        printed_grade_label = "小学4年",
                        grade_confidence = 0.99,
                        warnings = Array.Empty<string>(),
                    },
                pages,
            });
            return new AiProviderResponse(
                AiProviders.GeminiDirect,
                connection.ModelId,
                connection.ModelId,
                $"fixture-response-{requestKey}",
                "STOP",
                root,
                new AiUsage(100, 0, 100, 0, 200),
                TimeSpan.FromMilliseconds(10));
        }
    }

    private sealed class RecordingPageRangeExtractor : IPdfPageRangeExtractor
    {
        private static readonly string[] Sides = ["left", "right"];
        public List<IReadOnlyDictionary<int, int>> Calls { get; } = [];
        public bool SupplyBookSpread { get; set; }

        public Task<DerivedPdfResult> ExtractAsync(
            Stream source, string sourceName, int firstPage, int lastPage,
            IReadOnlyDictionary<int, int> rotations, CancellationToken cancellationToken = default) =>
            ExtractForProviderAsync(source, sourceName, firstPage, lastPage, rotations, 0, cancellationToken);

        public Task<DerivedPdfResult> ExtractForProviderAsync(
            Stream source,
            string sourceName,
            int firstPage,
            int lastPage,
            IReadOnlyDictionary<int, int> rotations,
            int maximumProviderMediaBytes,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(new Dictionary<int, int>(rotations));
            var rotationText = string.Join(
                ",",
                rotations.OrderBy(item => item.Key)
                    .Select(item => $"{item.Key}:{item.Value}"));
            var bytes = Encoding.UTF8.GetBytes(
                $"derived:{firstPage}-{lastPage}:{rotationText}");
            var applied = rotations.OrderBy(item => item.Key)
                .Select(item => new AppliedPageRotation(
                    $"page-{item.Key}",
                    item.Key,
                    item.Value,
                    "fixture",
                    0.99))
                .ToArray();
            return Task.FromResult(new DerivedPdfResult(
                bytes,
                Sha256(bytes),
                lastPage - firstPage + 1,
                firstPage,
                lastPage,
                applied)
            {
                BookSpreadDetails = SupplyBookSpread && maximumProviderMediaBytes > 0
                    ? Sides.Select(side =>
                    {
                        var image = Encoding.UTF8.GetBytes(side);
                        return new BookSpreadDetail(firstPage, side, new ImageArtifact(
                            "image/jpeg", "jpg", 800, 1200, image, Sha256(image)));
                    }).ToArray()
                    : [],
            });
        }
    }

    private sealed class MemoryContentStore : IContentStore
    {
        private readonly Dictionary<string, byte[]> _content =
            new(StringComparer.Ordinal);

        public void Add(string sha256, byte[] content)
        {
            _content[sha256] = content.ToArray();
        }

        public async Task<ContentWriteResult> PutAsync(
            Stream source,
            ContentStorageClass storageClass,
            string verifiedExtension,
            CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            var hash = Sha256(bytes);
            var deduplicated = _content.ContainsKey(hash);
            _content[hash] = bytes;
            return new ContentWriteResult(
                new ContentObjectLocator(
                    storageClass,
                    hash,
                    bytes.LongLength,
                    verifiedExtension),
                $"{storageClass}/{hash}.{verifiedExtension}",
                deduplicated);
        }

        public Task<Stream> OpenReadAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(
                new MemoryStream(_content[locator.Sha256], writable: false));
        }

        public Task<bool> ExistsAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_content.ContainsKey(locator.Sha256));

        public Task DeleteAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default)
        {
            _content.Remove(locator.Sha256);
            return Task.CompletedTask;
        }
    }

    private sealed class OnePageCountReader : IPdfPageCountReader
    {
        public Task<int> GetPageCountAsync(
            Stream source,
            int maximumPages,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(1);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
