using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Globalization;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Infrastructure.Tests;

public sealed class OpenRouterMigrationTests
{
    [Theory]
    [InlineData("high")]
    [InlineData("medium")]
    public async Task UpgradePreservesGeminiAndAllowsOneOpenRouterConnection(string gradingResolution)
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "ooki-grader-openrouter-migration-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        try
        {
            var options = Options(Path.Combine(rootPath, "upgrade.db"));
            var now = new DateTimeOffset(
                2026,
                8,
                6,
                9,
                0,
                0,
                TimeSpan.Zero);
            var clock = new TestClock(now);
            var geminiId = UlidId.New(now);
            var geminiProfileId = UlidId.New(now.AddMilliseconds(1));

            await using (var context = new OokiGraderDbContext(options, clock))
            {
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync(
                    "20260805000000_0015_NonModelAnswerTemplateSourceRole");
                // Seed the 0015 schema directly so the upgrade runs against the historical columns.
                var timestamp = now.ToUnixTimeMilliseconds();
                var staffId = UlidId.New(now);
                await context.Database.ExecuteSqlAsync($"""
                    INSERT INTO ai_connection
                        (id, provider, endpoint_profile, model_id, secret_reference, key_fingerprint,
                         credential_revision, timeout_seconds, concurrency_limit, state,
                         created_by_staff_user_id, created_at, updated_at, revision)
                    VALUES ({geminiId}, 'geminiDirect', 'googleGenerativeLanguage', 'gemini-3.5-flash-lite',
                            'secret:fixture', 'sha256:test', 1, 75, 2, 'active',
                            {staffId}, {timestamp}, {timestamp}, 1);
                    """);
                await context.Database.ExecuteSqlAsync($"""
                    INSERT INTO ai_task_profile
                        (id, name, task_type, ai_connection_id, connection_revision, model_id, processing_strategy,
                         prompt_version, schema_version, prompt_content_hash, thinking_level, media_resolution,
                         max_output_tokens, concurrency_limit, approval_state, active, created_by_staff_user_id,
                         created_at, updated_at, revision)
                    VALUES ({geminiProfileId}, 'grading', 'initialGrading', {geminiId}, 1, 'gemini-3.5-flash-lite',
                            'queued_standard', 'test-v1', 'test-v1', {new string('a', 64)}, 'low', {gradingResolution},
                            8192, 1, 'capability_passed', 1, {staffId}, {timestamp}, {timestamp}, 1);
                    """);

                await migrator.MigrateAsync();
            }

            await using (var context = new OokiGraderDbContext(options, clock))
            {
                var preserved = await context.AiConnections
                    .AsNoTracking()
                    .SingleAsync(item => item.Id == geminiId);
                Assert.Equal("geminiDirect", preserved.Provider);
                Assert.Equal(gradingResolution, preserved.InitialGradingMediaResolution);
                var preservedProfile = await context.AiTaskProfiles
                    .AsNoTracking()
                    .SingleAsync(item => item.Id == geminiProfileId);
                Assert.Equal(geminiId, preservedProfile.AiConnectionId);
                Assert.Equal(gradingResolution, preservedProfile.MediaResolution);

                context.AiConnections.Add(Connection(
                    UlidId.New(now.AddSeconds(1)),
                    "openRouter",
                    "openRouterChatCompletions",
                    "google/gemini-3.1-flash-lite",
                    now.AddSeconds(1)));
                await context.SaveChangesAsync();
                Assert.Equal(0, await ForeignKeyViolationCountAsync(context));
            }

            await using (var context = new OokiGraderDbContext(options, clock))
            {
                context.AiConnections.Add(Connection(
                    UlidId.New(now.AddSeconds(2)),
                    "openRouter",
                    "openRouterChatCompletions",
                    "google/gemini-3.1-pro",
                    now.AddSeconds(2)));
                await Assert.ThrowsAsync<DbUpdateException>(
                    () => context.SaveChangesAsync());
            }

            await using (var context = new OokiGraderDbContext(options, clock))
            {
                Assert.Equal(0, await ForeignKeyViolationCountAsync(context));
            }
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task FreshDatabaseAllowsOpenRouterButBatchRemainsGeminiOnly()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "ooki-grader-openrouter-migration-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        try
        {
            var options = Options(Path.Combine(rootPath, "fresh.db"));
            var now = new DateTimeOffset(
                2026,
                8,
                6,
                10,
                0,
                0,
                TimeSpan.Zero);
            var clock = new TestClock(now);
            var connection = Connection(
                UlidId.New(now),
                "openRouter",
                "openRouterChatCompletions",
                "google/gemini-3.1-flash-lite",
                now);
            var profile = Profile(
                UlidId.New(now.AddSeconds(1)),
                connection,
                now.AddSeconds(2));

            await using (var context = new OokiGraderDbContext(options, clock))
            {
                await context.Database.MigrateAsync();
                context.AddRange(connection, profile);
                await context.SaveChangesAsync();
            }

            await using (var context = new OokiGraderDbContext(options, clock))
            {
                context.AiBatches.Add(new AiBatchEntity
                {
                    Id = UlidId.New(now.AddSeconds(3)),
                    Provider = "openRouter",
                    ModelId = connection.ModelId,
                    AiConnectionId = connection.Id,
                    ConnectionRevision = connection.CredentialRevision,
                    AiTaskProfileId = profile.Id,
                    TaskProfileRevision = profile.Revision,
                    CompatibilityKey = "openrouter-batch-rejected",
                    ManifestHash = new string('b', 64),
                    DisplayName = "OpenRouter batch must be rejected",
                    RequestCount = 1,
                    PendingRequestCount = 1,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                await Assert.ThrowsAsync<DbUpdateException>(
                    () => context.SaveChangesAsync());
            }

            await using (var context = new OokiGraderDbContext(options, clock))
            {
                Assert.Equal(0, await ForeignKeyViolationCountAsync(context));
            }
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static DbContextOptions<OokiGraderDbContext> Options(
        string databasePath) =>
        new DbContextOptionsBuilder<OokiGraderDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                ForeignKeys = true,
                DefaultTimeout = 5,
                Pooling = false,
            }.ToString())
            .AddInterceptors(new SqlitePragmaConnectionInterceptor())
            .Options;

    private static AiConnectionEntity Connection(
        string id,
        string provider,
        string endpointProfile,
        string modelId,
        DateTimeOffset now) => new()
        {
            Id = id,
            Provider = provider,
            EndpointProfile = endpointProfile,
            ModelId = modelId,
            SecretReference = $"secret:{id}",
            KeyFingerprint = "sha256:test",
            State = "pending_probe",
            CreatedByStaffUserId = UlidId.New(now.AddMilliseconds(1)),
            CreatedAt = now,
            UpdatedAt = now,
        };

    private static AiTaskProfileEntity Profile(
        string id,
        AiConnectionEntity connection,
        DateTimeOffset now) => new()
        {
            Id = id,
            Name = $"{connection.Provider} initial grading",
            TaskType = "initialGrading",
            AiConnectionId = connection.Id,
            ConnectionRevision = connection.CredentialRevision,
            ModelId = connection.ModelId,
            ProcessingStrategy = "queued_standard",
            PromptVersion = "test-v1",
            SchemaVersion = "test-v1",
            PromptContentHash = new string('a', 64),
            ApprovalState = "capability_passed",
            CreatedByStaffUserId = UlidId.New(now.AddMilliseconds(1)),
            CreatedAt = now,
            UpdatedAt = now,
        };

    private static async Task<long> ForeignKeyViolationCountAsync(
        OokiGraderDbContext context)
    {
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database
            .GetDbConnection()
            .CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_foreign_key_check;";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
    }
}
