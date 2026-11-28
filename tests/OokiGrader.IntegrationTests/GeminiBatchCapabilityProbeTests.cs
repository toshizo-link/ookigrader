using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;
using OokiGrader.Host.Jobs;

namespace OokiGrader.IntegrationTests;

public sealed class GeminiBatchCapabilityProbeTests
{
    [Theory]
    [InlineData("gemini-3.8-flash", "LOW", 2_048)]
    [InlineData("gemini-3.5-flash-lite", "MINIMAL", 64)]
    public async Task ProbeUsesModelSupportedThinkingAndSufficientOutputBudget(
        string modelId, string expectedThinking, int expectedOutputTokens)
    {
        var provider = new ProbeBatchProvider();
        provider.Statuses.Enqueue(provider.Status(AiBatchRemoteState.Pending));
        provider.Statuses.Enqueue(provider.Status(AiBatchRemoteState.Cancelled));
        var probe = new GeminiBatchCapabilityProbe(provider, TimeProvider.System);
        await probe.ProbeAsync(Connection() with { ModelId = modelId },
            Encoding.UTF8.GetBytes("test-key"));
        Assert.NotNull(provider.Request);
        Assert.Equal(expectedThinking, provider.Request.ThinkingLevel);
        Assert.Equal(expectedOutputTokens, provider.Request.MaxOutputTokens);
        var media = Assert.Single(provider.Request.Media);
        Assert.Equal("image/png", media.MimeType);
        Assert.Equal(64u, BinaryPrimitives.ReadUInt32BigEndian(media.Bytes.Span.Slice(16, 4)));
        Assert.Equal(64u, BinaryPrimitives.ReadUInt32BigEndian(media.Bytes.Span.Slice(20, 4)));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(media.Bytes.Span)).ToLowerInvariant(), media.Sha256);
    }

    [Fact]
    public async Task PendingProbeCancelsPollsAndDeletesEveryResource()
    {
        var provider = new ProbeBatchProvider();
        provider.Statuses.Enqueue(provider.Status(
            AiBatchRemoteState.Pending));
        provider.Statuses.Enqueue(provider.Status(
            AiBatchRemoteState.Cancelled));
        var probe = new GeminiBatchCapabilityProbe(
            provider,
            TimeProvider.System);

        var result = await probe.ProbeAsync(
            Connection(),
            Encoding.UTF8.GetBytes("test-key"));

        Assert.Equal("passed", result.State);
        Assert.True(result.Available);
        Assert.True(result.CleanupSucceeded);
        Assert.Equal(1, provider.CreateCalls);
        Assert.Equal(1, provider.CancelCalls);
        Assert.Equal(1, provider.DeleteBatchCalls);
        Assert.Equal(["files/probe-input"], provider.DeletedFiles);
        Assert.Equal(
            [
                "upload",
                "create",
                "get",
                "cancel",
                "get",
                "delete-batch",
                "delete-file",
            ],
            provider.Calls);
    }

    [Fact]
    public async Task AmbiguousCreateAdoptsOneMatchWithoutCreatingAgain()
    {
        var provider = new ProbeBatchProvider
        {
            CreateFailure = new AiBatchCreateException(
                AiBatchCreateFailureKind.AmbiguousAfterSend,
                "gemini_batch_create_network_unknown",
                isTransient: true),
        };
        provider.Statuses.Enqueue(provider.Status(
            AiBatchRemoteState.Pending));
        provider.Statuses.Enqueue(provider.Status(
            AiBatchRemoteState.Cancelled));
        provider.MatchingListItems = 1;
        var probe = new GeminiBatchCapabilityProbe(
            provider,
            TimeProvider.System);

        var result = await probe.ProbeAsync(
            Connection(),
            Encoding.UTF8.GetBytes("test-key"));

        Assert.Equal("passed", result.State);
        Assert.True(result.CleanupSucceeded);
        Assert.Equal(1, provider.CreateCalls);
        Assert.Equal(1, provider.ListCalls);
        Assert.Equal(1, provider.CancelCalls);
        Assert.Equal(1, provider.DeleteBatchCalls);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(2, false, false)]
    [InlineData(0, true, false)]
    [InlineData(1, false, true)]
    public async Task AmbiguousCreateWithNoUniqueReceiptReportsUnconfirmedCleanup(
        int matches, bool timeout, bool incompleteList)
    {
        var provider = new ProbeBatchProvider
        {
            CreateFailure = new AiBatchCreateException(
                AiBatchCreateFailureKind.AmbiguousAfterSend, "gemini_batch_create_network_unknown", true),
            MatchingListItems = matches,
            TimeoutOnList = timeout,
            MoreListPages = incompleteList,
        };
        var probe = new GeminiBatchCapabilityProbe(provider, TimeProvider.System);

        var result = await probe.ProbeAsync(Connection(), "test-key"u8.ToArray());

        Assert.Equal("failed", result.State);
        Assert.False(result.Available);
        Assert.False(result.CleanupSucceeded);
        Assert.Equal("gemini_batch_probe_cleanup_failed", result.SafeErrorCode);
        Assert.Equal(1, provider.CreateCalls);
        Assert.Equal(incompleteList ? 3 : 1, provider.ListCalls);
        Assert.Equal(0, provider.CancelCalls);
        Assert.Equal(0, provider.DeleteBatchCalls);
        Assert.Equal(["files/probe-input"], provider.DeletedFiles);
    }

    [Theory]
    [InlineData(AiBatchCreateFailureKind.DefinitePreSend)]
    [InlineData(AiBatchCreateFailureKind.DefiniteRemoteRejection)]
    public async Task DefiniteCreateFailureNeedsOnlyInputFileCleanup(AiBatchCreateFailureKind failure)
    {
        var provider = new ProbeBatchProvider
        {
            CreateFailure = new AiBatchCreateException(failure, "gemini_batch_create_invalid", false),
        };
        var probe = new GeminiBatchCapabilityProbe(provider, TimeProvider.System);

        var result = await probe.ProbeAsync(Connection(), "test-key"u8.ToArray());

        Assert.False(result.Available);
        Assert.True(result.CleanupSucceeded);
        Assert.Equal("gemini_batch_create_invalid", result.SafeErrorCode);
        Assert.Equal(["upload", "create", "delete-file"], provider.Calls);
    }

    [Fact]
    public async Task CompletedProbeValidatesOutputAndCleansOutputFile()
    {
        var provider = new ProbeBatchProvider();
        provider.Statuses.Enqueue(provider.Status(
            AiBatchRemoteState.Succeeded,
            outputFileName: "files/probe-output"));
        var probe = new GeminiBatchCapabilityProbe(
            provider,
            TimeProvider.System);

        var result = await probe.ProbeAsync(
            Connection(),
            Encoding.UTF8.GetBytes("test-key"));

        Assert.Equal("passed", result.State);
        Assert.Equal(0, provider.CancelCalls);
        Assert.Equal(1, provider.ReadResultsCalls);
        Assert.Equal(
            ["files/probe-output", "files/probe-input"],
            provider.DeletedFiles);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ProviderManagedOutputAllowsBatchOnlyAfterAllOtherCleanupSucceeds(
        bool inputDeletionFailed, bool expectedAvailable)
    {
        var provider = new ProbeBatchProvider { FailFileCleanup = inputDeletionFailed };
        provider.CompleteWithProviderManagedOutput();
        var result = await new GeminiBatchCapabilityProbe(provider, TimeProvider.System)
            .ProbeAsync(Connection(), "test-key"u8.ToArray());

        Assert.Equal(expectedAvailable, result.Available);
        Assert.False(result.CleanupSucceeded);
        Assert.Equal(expectedAvailable ? "passed" : "failed", result.State);
        Assert.Equal(expectedAvailable ? "gemini_batch_probe_provider_managed_output" : "gemini_batch_probe_cleanup_failed",
            result.SafeErrorCode);
        Assert.Equal(1, provider.DeleteBatchCalls);
        Assert.Equal([Assert.IsType<string>(provider.RetainedOutput), "files/probe-input"], provider.DeletedFiles);
    }

    [Fact]
    public async Task CleanupFailureFailsCapabilityGate()
    {
        var provider = new ProbeBatchProvider
        {
            FailFileCleanup = true,
        };
        provider.Statuses.Enqueue(provider.Status(
            AiBatchRemoteState.Pending));
        provider.Statuses.Enqueue(provider.Status(
            AiBatchRemoteState.Cancelled));
        var probe = new GeminiBatchCapabilityProbe(
            provider,
            TimeProvider.System);

        var result = await probe.ProbeAsync(
            Connection(),
            Encoding.UTF8.GetBytes("test-key"));

        Assert.Equal("failed", result.State);
        Assert.False(result.Available);
        Assert.False(result.CleanupSucceeded);
        Assert.Equal(
            "gemini_batch_probe_cleanup_failed",
            result.SafeErrorCode);
    }

    private static AiConnectionSettings Connection() =>
        new(
            "connection",
            AiProviders.GeminiDirect,
            new Uri("https://generativelanguage.googleapis.com/"),
            GeminiBatchClient.SelectedModel,
            TimeSpan.FromSeconds(30));

    internal sealed class ProbeBatchProvider : IAiBatchProviderClient
    {
        private string? _displayName;
        private string? _requestKey;

        public string Provider => AiProviders.GeminiDirect;
        public Queue<AiBatchStatus> Statuses { get; } = new();
        public List<string> Calls { get; } = [];
        public List<string> DeletedFiles { get; } = [];
        public AiBatchCreateException? CreateFailure { get; set; }
        public int MatchingListItems { get; set; }
        public bool TimeoutOnList { get; set; }
        public bool MoreListPages { get; set; }
        public bool FailFileCleanup { get; set; }
        private string BatchId { get; set; } = "probe-1";
        public string? RetainedOutput { get; private set; }

        public void CompleteWithProviderManagedOutput()
        {
            BatchId = "00000000-0000-0000-0000-000000000001";
            RetainedOutput = "files/batch-" + BatchId;
            Statuses.Enqueue(Status(AiBatchRemoteState.Succeeded, RetainedOutput));
        }
        public int CreateCalls { get; private set; }
        public int ListCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public int DeleteBatchCalls { get; private set; }
        public int ReadResultsCalls { get; private set; }
        public AiProviderRequest? Request { get; private set; }

        public byte[] BuildJsonLines(
            IReadOnlyList<AiProviderRequest> requests, string modelId = AiProviderCatalog.GeminiDefaultModelId)
        {
            Request = Assert.Single(requests);
            _requestKey = Request.RequestKey;
            return "{}\n"u8.ToArray();
        }

        public Task<AiBatchInputFile> UploadJsonLinesAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            string displayName,
            ReadOnlyMemory<byte> jsonLines,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("upload");
            _displayName = displayName;
            return Task.FromResult(new AiBatchInputFile(
                "files/probe-input",
                null,
                null,
                jsonLines.Length));
        }

        public Task<AiBatchCreateReceipt> CreateAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            AiBatchCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("create");
            CreateCalls++;
            if (CreateFailure is not null)
            {
                throw CreateFailure;
            }

            return Task.FromResult(new AiBatchCreateReceipt(
                "batches/" + BatchId,
                request.DisplayName,
                DateTimeOffset.UtcNow));
        }

        public Task<AiBatchStatus> GetAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            string providerBatchName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("get");
            return Task.FromResult(Statuses.Dequeue());
        }

        public Task CancelAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            string providerBatchName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("cancel");
            CancelCalls++;
            return Task.CompletedTask;
        }

        public Task DeleteBatchAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            string providerBatchName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("delete-batch");
            DeleteBatchCalls++;
            return Task.CompletedTask;
        }

        public Task<AiBatchListPage> ListAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            string? pageToken = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("list");
            ListCalls++;
            if (TimeoutOnList) throw new OperationCanceledException();
            var batches = Enumerable.Range(1, MatchingListItems).Select(index =>
                Status(AiBatchRemoteState.Pending) with { ProviderBatchName = $"batches/probe-{index}" }).ToArray();
            return Task.FromResult(new AiBatchListPage(batches, MoreListPages ? $"page-{ListCalls + 1}" : null));
        }

        public Task<IReadOnlyList<AiBatchItemResult>> ReadResultsAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            AiBatchStatus completedBatch,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("read-results");
            ReadResultsCalls++;
            using var output = JsonDocument.Parse("""{"ok":true}""");
            IReadOnlyList<AiBatchItemResult> result =
            [
                new(
                    _requestKey!,
                    new AiProviderResponse(
                        AiProviders.GeminiDirect,
                        GeminiBatchClient.SelectedModel,
                        GeminiBatchClient.SelectedModel + "-001",
                        "probe-response",
                        "STOP",
                        output.RootElement.Clone(),
                        new AiUsage(1, 0, 1, 0, 2),
                        TimeSpan.Zero),
                    null),
            ];
            return Task.FromResult(result);
        }

        public Task DeleteFileAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            string providerFileName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("delete-file");
            DeletedFiles.Add(providerFileName);
            if (providerFileName == RetainedOutput)
                throw new AiProviderException(AiFailureKind.RequestRejected,
                    "gemini_delete_file_id_too_long", isTransient: false);
            if (FailFileCleanup)
            {
                throw new AiProviderException(
                    AiFailureKind.TransientProvider,
                    "synthetic_cleanup_failure",
                    isTransient: true);
            }

            return Task.CompletedTask;
        }

        public AiBatchStatus Status(
            AiBatchRemoteState state,
            string? outputFileName = null)
        {
            using var raw = JsonDocument.Parse("{}");
            return new AiBatchStatus(
                "batches/" + BatchId,
                _displayName,
                state,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                state is AiBatchRemoteState.Pending
                    or AiBatchRemoteState.Running
                        ? null
                        : DateTimeOffset.UtcNow,
                null,
                outputFileName,
                null,
                raw.RootElement.Clone());
        }
    }
}
