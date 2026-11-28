using System.Net;
using System.Text;
using System.Text.Json;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;

namespace OokiGrader.ProviderContract.Tests;

public sealed class GeminiBatchClientTests
{
    [Theory]
    [InlineData("INVALID_ARGUMENT", "DeleteFileRequest.name: File ID (name excluding 'files/') cannot be more than 40 characters.", true)]
    [InlineData("INVALID_ARGUMENT", "Unrelated invalid file parameter.", false)]
    [InlineData("PERMISSION_DENIED", "DeleteFileRequest.name: File ID cannot be more than 40 characters.", false)]
    public async Task RecognizesOnlyCorrelatedGeneratedOutputWithVerifiedDeleteFileLengthError(
        string status, string message, bool expected)
    {
        var id = new string('a', 40);
        var output = "files/batch-" + id;
        var handler = new DelegateHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { error = new { status, message } })),
            });
        });
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<AiProviderException>(() => new GeminiBatchClient(http)
            .DeleteFileAsync(Connection(), "test"u8.ToArray(), output));
        Assert.Equal(expected, GeminiBatchClient.IsProviderManagedOutput("batches/" + id, output, failure));
        Assert.False(GeminiBatchClient.IsProviderManagedOutput("batches/other", output, failure));
        Assert.False(GeminiBatchClient.IsProviderManagedOutput("batches/a", "files/batch-a", failure));
        Assert.False(GeminiBatchClient.IsProviderManagedOutput("batches/" + id, "files/" + id, failure));
    }

    private const string DisplayName =
        "ooki-01K14P4A2GBB7W4K1M1M1M1M1M-0123456789ab";

    [Theory]
    [InlineData("gemini-3.8-flash", "MEDIUM")]
    [InlineData("gemini-3.7-flash-preview", "MEDIUM")]
    [InlineData("gemini-3.5-flash-lite", "MEDIUM")]
    [InlineData("gemini-3.1-pro-preview", "MEDIUM")]
    [InlineData("gemini-3-pro-preview", "LOW")]
    [InlineData("gemini-3.1-flash-lite-image", "MINIMAL")]
    [InlineData("gemini-2.5-flash", null)]
    [InlineData("gemini-2.5-pro", null)]
    public async Task StandardAndBatchUseTheSameTaskSpecificThinking(string modelId, string? templateLevel)
    {
        string? standardBody = null;
        using var http = new HttpClient(new DelegateHandler(async (request, token) =>
        {
            standardBody = await request.Content!.ReadAsStringAsync(token);
            return JsonResponse("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"{\"ok\":true}"}]}}]}""");
        }));
        var batch = new GeminiBatchClient(http);
        var direct = new GeminiDirectClient(http);
        foreach (var task in new[] { AiTaskTypes.TemplateExtraction, AiTaskTypes.InitialGrading })
        {
            var level = AiProviderCatalog.DefaultThinkingLevel(AiProviders.GeminiDirect, modelId, task);
            Assert.Equal(level, AiProviderCatalog.DefaultThinkingLevel(AiProviders.OpenRouter, "google/" + modelId, task));
            var request = Request("settings") with
            {
                TaskType = task, ThinkingLevel = level.ToUpperInvariant(), MaxOutputTokens = 32_768,
            };
            await direct.GenerateAsync(Connection() with { ModelId = modelId }, "test-key"u8.ToArray(), request);
            using var standard = JsonDocument.Parse(standardBody!);
            using var line = JsonDocument.Parse(batch.BuildJsonLines([request], modelId));
            var config = line.RootElement.GetProperty("request").GetProperty("generationConfig").GetProperty("thinkingConfig");
            Assert.Equal(standard.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig").GetRawText(), config.GetRawText());
            if (templateLevel is null)
            {
                Assert.False(config.TryGetProperty("thinkingLevel", out _));
                Assert.Equal(task == AiTaskTypes.TemplateExtraction ? 4_096 : 512, config.GetProperty("thinkingBudget").GetInt32());
            }
            else
            {
                Assert.False(config.TryGetProperty("thinkingBudget", out _));
                var expected = task == AiTaskTypes.TemplateExtraction || templateLevel == "MINIMAL" ? templateLevel : "LOW";
                Assert.Equal(expected, config.GetProperty("thinkingLevel").GetString());
            }
        }
    }

    [Fact]
    public void BuildJsonLinesUsesStableKeysAndGemini35SafeConfiguration()
    {
        var client = new GeminiBatchClient(new HttpClient());

        var bytes = client.BuildJsonLines(
            [Request("key-1"), Request("key-2") with { TaskType = AiTaskTypes.Adjudication }]);

        var lines = Encoding.UTF8.GetString(bytes)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("key-1", first.RootElement.GetProperty("key").GetString());
        var request = first.RootElement.GetProperty("request");
        var configuration = request.GetProperty("generationConfig");
        Assert.Equal(
            "application/json",
            configuration.GetProperty("responseMimeType").GetString());
        Assert.Equal(
            "MINIMAL",
            configuration
                .GetProperty("thinkingConfig")
                .GetProperty("thinkingLevel")
                .GetString());
        Assert.False(configuration.TryGetProperty("temperature", out _));
        Assert.False(configuration.TryGetProperty("topP", out _));
        Assert.False(configuration.TryGetProperty("topK", out _));
        Assert.False(configuration.TryGetProperty("candidateCount", out _));
        var parts = request.GetProperty("contents")[0].GetProperty("parts");
        Assert.Equal("user", parts[0].GetProperty("text").GetString());
        Assert.Equal("synthetic-image"u8.ToArray(),
            parts[1].GetProperty("inline_data").GetProperty("data").GetBytesFromBase64());
        using var second = JsonDocument.Parse(lines[1]);
        var recheckParts = second.RootElement.GetProperty("request").GetProperty("contents")[0].GetProperty("parts");
        Assert.Equal(JsonValueKind.Object, recheckParts[0].GetProperty("inline_data").ValueKind);
        Assert.Equal("user", recheckParts[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task UploadAndCreateUseOfficialRestEndpointsWithoutRetryHeaders()
    {
        var requests = new List<CapturedRequest>();
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.ToDictionary(
                    item => item.Key,
                    item => string.Join(",", item.Value),
                    StringComparer.OrdinalIgnoreCase),
                request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (requests.Count == 1)
            {
                var response = JsonResponse("{}");
                response.Headers.TryAddWithoutValidation(
                    "X-Goog-Upload-URL",
                    "https://generativelanguage.googleapis.com/upload-session/one");
                return response;
            }

            if (requests.Count == 2)
            {
                return JsonResponse(
                    """
                    {
                      "file": {
                        "name": "files/input-1",
                        "uri": "https://generativelanguage.googleapis.com/v1beta/files/input-1",
                        "expirationTime": "2026-07-29T00:00:00Z"
                      }
                    }
                    """);
            }

            return JsonResponse(
                $$"""
                {
                  "name": "batches/batch-1",
                  "metadata": {
                    "displayName": "{{DisplayName}}",
                    "createTime": "2026-07-27T00:00:00Z",
                    "state": "JOB_STATE_PENDING"
                  },
                  "done": false
                }
                """);
        });
        var client = new GeminiBatchClient(new HttpClient(handler));
        var jsonLines = client.BuildJsonLines([Request("key-1")]);

        var file = await client.UploadJsonLinesAsync(
            Connection(),
            Encoding.UTF8.GetBytes("secret-test-key"),
            DisplayName,
            jsonLines);
        var receipt = await client.CreateAsync(
            Connection(),
            Encoding.UTF8.GetBytes("secret-test-key"),
            new AiBatchCreateRequest(
                DisplayName,
                new string('a', 64),
                file.ProviderFileName,
                1));

        Assert.Equal("files/input-1", file.ProviderFileName);
        Assert.Equal("batches/batch-1", receipt.ProviderBatchName);
        Assert.Equal(3, requests.Count);
        Assert.Equal(
            "/upload/v1beta/files",
            requests[0].Uri.AbsolutePath);
        Assert.Equal(
            "resumable",
            requests[0].Headers["X-Goog-Upload-Protocol"]);
        Assert.Equal(
            "upload, finalize",
            requests[1].Headers["X-Goog-Upload-Command"]);
        Assert.Equal(
            $"/v1beta/models/{AiProviderCatalog.GeminiDefaultModelId}:batchGenerateContent",
            requests[2].Uri.AbsolutePath);
        Assert.DoesNotContain(
            requests[2].Headers.Keys,
            key => key.Contains("idempot", StringComparison.OrdinalIgnoreCase));
        using var createBody = JsonDocument.Parse(requests[2].Body!);
        Assert.Equal(
            DisplayName,
            createBody.RootElement
                .GetProperty("batch")
                .GetProperty("display_name")
                .GetString());
        Assert.Equal(
            "files/input-1",
            createBody.RootElement
                .GetProperty("batch")
                .GetProperty("input_config")
                .GetProperty("file_name")
                .GetString());
    }

    [Fact]
    public async Task CreateClassifiesNetworkLossAfterSendAsAmbiguous()
    {
        var client = new GeminiBatchClient(
            new HttpClient(new DelegateHandler((_, _) =>
                throw new HttpRequestException("synthetic"))));

        var exception = await Assert.ThrowsAsync<AiBatchCreateException>(() =>
            client.CreateAsync(
                Connection(),
                Encoding.UTF8.GetBytes("test-key"),
                new AiBatchCreateRequest(
                    DisplayName,
                    new string('a', 64),
                    "files/input-1",
                    1)));

        Assert.Equal(
            AiBatchCreateFailureKind.AmbiguousAfterSend,
            exception.Kind);
        Assert.Equal(
            "gemini_batch_create_network_unknown",
            exception.SafeErrorCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, AiBatchCreateFailureKind.DefiniteRemoteRejection)]
    [InlineData(HttpStatusCode.TooManyRequests, AiBatchCreateFailureKind.DefiniteRemoteRejection)]
    [InlineData(HttpStatusCode.RequestTimeout, AiBatchCreateFailureKind.AmbiguousAfterSend)]
    [InlineData(HttpStatusCode.InternalServerError, AiBatchCreateFailureKind.AmbiguousAfterSend)]
    [InlineData(HttpStatusCode.BadGateway, AiBatchCreateFailureKind.AmbiguousAfterSend)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AiBatchCreateFailureKind.AmbiguousAfterSend)]
    [InlineData(HttpStatusCode.GatewayTimeout, AiBatchCreateFailureKind.AmbiguousAfterSend)]
    public async Task CreateSeparatesExplicitRejectionsFromUncertainDispatch(
        HttpStatusCode statusCode, AiBatchCreateFailureKind expectedOutcome)
    {
        var calls = 0;
        using var http = new HttpClient(new DelegateHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }));
        var client = new GeminiBatchClient(http);

        var exception = await Assert.ThrowsAsync<AiBatchCreateException>(() =>
            client.CreateAsync(
                Connection(),
                Encoding.UTF8.GetBytes("test-key"),
                new AiBatchCreateRequest(
                    DisplayName,
                    new string('a', 64),
                    "files/input-1",
                    1)));

        Assert.Equal(expectedOutcome, exception.Kind);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false, "gemini_batch_create_json_invalid")]
    [InlineData(true, "gemini_batch_response_too_large")]
    public async Task CreateTreatsUnreadableAcceptedReceiptAsAmbiguous(bool oversized, string expectedCode)
    {
        var calls = 0;
        using var http = new HttpClient(new DelegateHandler((_, _) =>
        {
            calls++;
            var response = JsonResponse("{");
            if (oversized) response.Content.Headers.ContentLength = 16 * 1024 * 1024;
            return Task.FromResult(response);
        }));
        var error = await Assert.ThrowsAsync<AiBatchCreateException>(() =>
            new GeminiBatchClient(http).CreateAsync(Connection(), "fixture"u8.ToArray(),
                new AiBatchCreateRequest(DisplayName, new string('a', 64), "files/input-1", 1)));
        Assert.Equal(AiBatchCreateFailureKind.AmbiguousAfterSend, error.Kind);
        Assert.Equal(expectedCode, error.SafeErrorCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetAndReadInlineResultsUseMetadataKeyNotArrayPosition()
    {
        var handler = new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(
                $$"""
                {
                  "name": "batches/batch-1",
                  "metadata": {
                    "displayName": "{{DisplayName}}",
                    "state": "JOB_STATE_SUCCEEDED",
                    "createTime": "2026-07-27T00:00:00Z",
                    "batchStats": {"requestCount":"1","successfulRequestCount":"1"}
                  },
                  "done": true,
                  "response": {
                    "inlinedResponses": {
                      "inlinedResponses": [{
                          "metadata": {"key": "key-2"},
                          "response": {
                            "candidates": [{
                              "content": {"parts": [{"text": "{\"ok\":true}"}]},
                              "finishReason": "STOP"
                            }],
                            "usageMetadata": {
                              "promptTokenCount": 10,
                              "candidatesTokenCount": 2,
                              "thoughtsTokenCount": 1,
                              "totalTokenCount": "13"
                            },
                            "modelVersion": "gemini-3.5-flash-lite-001",
                            "responseId": "response-2"
                          }
                      }]
                    }
                  }
                }
                """)));
        var client = new GeminiBatchClient(new HttpClient(handler));

        var status = await client.GetAsync(
            Connection(),
            Encoding.UTF8.GetBytes("test-key"),
            "batches/batch-1");
        var results = await client.ReadResultsAsync(
            Connection(),
            Encoding.UTF8.GetBytes("test-key"),
            status);

        Assert.Equal(AiBatchRemoteState.Succeeded, status.State);
        Assert.Equal(1, status.Stats?.RequestCount);
        var result = Assert.Single(results);
        Assert.Equal("key-2", result.RequestKey);
        Assert.NotNull(result.Response);
        Assert.True(
            result.Response.StructuredOutput
                .GetProperty("ok")
                .GetBoolean());
        Assert.Equal(13, result.Response.Usage.TotalTokens);
    }

    [Fact]
    public async Task GetRecognizesOfficialTopLevelResponsesFile()
    {
        var handler = new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(
                $$"""
                {
                  "name": "batches/batch-1",
                  "metadata": {
                    "displayName": "{{DisplayName}}",
                    "state": "BATCH_STATE_SUCCEEDED"
                  },
                  "done": true,
                  "response": {
                    "responsesFile": "files/output-official"
                  }
                }
                """)));
        var client = new GeminiBatchClient(new HttpClient(handler));

        var status = await client.GetAsync(
            Connection(),
            Encoding.UTF8.GetBytes("test-key"),
            "batches/batch-1");

        Assert.Equal(AiBatchRemoteState.Succeeded, status.State);
        Assert.Equal("files/output-official", status.OutputFileName);
    }

    [Fact]
    public async Task ReadInlineResultsRejectsTruncatedItemWithoutTreatingItAsProviderOutage()
    {
        var client = new GeminiBatchClient(new HttpClient(
            new DelegateHandler((_, _) => Task.FromResult(JsonResponse(
                $$$"""
                {
                  "name": "batches/batch-1",
                  "metadata": {"displayName": "{{{DisplayName}}}", "state": "JOB_STATE_SUCCEEDED"},
                  "done": true,
                  "response": {"inlinedResponses": {"inlinedResponses": [{
                    "metadata": {"key": "truncated-key"},
                    "response": {"candidates": [{"finishReason": "MAX_TOKENS",
                      "content": {"parts": [{"text": "{\"incomplete\":"}]}}]}
                  }]}}
                }
                """)))));
        var status = await client.GetAsync(Connection(), Encoding.UTF8.GetBytes("test-key"),
            "batches/batch-1");
        var result = Assert.Single(await client.ReadResultsAsync(Connection(),
            Encoding.UTF8.GetBytes("test-key"), status));
        Assert.Equal("truncated-key", result.RequestKey);
        Assert.Null(result.Response);
        Assert.Equal("gemini_output_limit_exceeded", result.SafeErrorCode);
    }

    [Fact]
    public async Task CancelUsesOfficialBatchCancelMethod()
    {
        HttpMethod? observedMethod = null;
        string? observedPath = null;
        string? observedKey = null;
        var handler = new DelegateHandler((request, _) =>
        {
            observedMethod = request.Method;
            observedPath = request.RequestUri?.AbsolutePath;
            observedKey = request.Headers.GetValues("x-goog-api-key").Single();
            return Task.FromResult(JsonResponse("{}"));
        });
        var client = new GeminiBatchClient(new HttpClient(handler));

        await client.CancelAsync(
            Connection(),
            Encoding.UTF8.GetBytes("test-key"),
            "batches/batch-1");

        Assert.Equal(HttpMethod.Post, observedMethod);
        Assert.Equal(
            "/v1beta/batches/batch-1:cancel",
            observedPath);
        Assert.Equal("test-key", observedKey);
    }

    [Fact]
    public async Task GetRecognizesOfficialCancelledOperationError()
    {
        var handler = new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(
                """
                {
                  "name": "batches/batch-1",
                  "done": true,
                  "error": {
                    "code": 1,
                    "message": "cancelled"
                  }
                }
                """)));
        var client = new GeminiBatchClient(new HttpClient(handler));

        var status = await client.GetAsync(
            Connection(),
            Encoding.UTF8.GetBytes("test-key"),
            "batches/batch-1");

        Assert.Equal(AiBatchRemoteState.Cancelled, status.State);
    }

    [Theory]
    [InlineData("""{"name":"batches/batch-1","done":true,"error":{"code":13}}""", AiBatchRemoteState.Failed)]
    [InlineData("""{"name":"batches/batch-1","metadata":{"state":"BATCH_STATE_RUNNING"},"done":true,"error":{"code":13}}""", AiBatchRemoteState.Failed)]
    [InlineData("""{"name":"batches/batch-1","done":true,"response":{"responsesFile":"files/output-1"}}""", AiBatchRemoteState.Succeeded)]
    public async Task GetUsesTheTerminalOperationResultWhenMetadataIsMissingOrStillRunning(
        string envelope, AiBatchRemoteState expected)
    {
        using var http = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(envelope))));
        var status = await new GeminiBatchClient(http).GetAsync(
            Connection(), "fixture"u8.ToArray(), "batches/batch-1");

        Assert.Equal(expected, status.State);
    }

    [Fact]
    public async Task DeleteBatchUsesOfficialOperationDeleteMethod()
    {
        HttpMethod? observedMethod = null;
        string? observedPath = null;
        var handler = new DelegateHandler((request, _) =>
        {
            observedMethod = request.Method;
            observedPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(JsonResponse("{}"));
        });
        var client = new GeminiBatchClient(new HttpClient(handler));

        await client.DeleteBatchAsync(
            Connection(),
            Encoding.UTF8.GetBytes("test-key"),
            "batches/batch-1");

        Assert.Equal(HttpMethod.Delete, observedMethod);
        Assert.Equal("/v1beta/batches/batch-1", observedPath);
    }

    [Fact]
    public async Task ReadFileResultsDownloadsTheOfficialMediaResourceDirectlyAndParsesJsonLines()
    {
        var calls = 0;
        var handler = new DelegateHandler((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://generativelanguage.googleapis.com/download/v1beta/files/output-1:download?alt=media",
                request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("x-goog-api-key")));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"key":"key-1","response":{"candidates":[{"content":{"parts":[{"text":"{\"ok\":true}"}]},"finishReason":"STOP"}],"usageMetadata":{"totalTokenCount":3},"responseId":"r1"}}

                    """,
                    Encoding.UTF8,
                    "application/jsonl"),
            });
        });
        var client = new GeminiBatchClient(new HttpClient(handler));
        using var raw = JsonDocument.Parse(
            """
            {
              "name":"batches/batch-1",
              "metadata":{"state":"JOB_STATE_SUCCEEDED"}
            }
            """);
        var status = new AiBatchStatus(
            "batches/batch-1",
            DisplayName,
            AiBatchRemoteState.Succeeded,
            null,
            null,
            null,
            null,
            "files/output-1",
            null,
            raw.RootElement.Clone());

        var results = await client.ReadResultsAsync(
            Connection(),
            Encoding.UTF8.GetBytes("test-key"),
            status);

        Assert.Equal(1, calls);
        var item = Assert.Single(results);
        Assert.Equal("key-1", item.RequestKey);
        Assert.Equal("r1", item.Response!.ProviderResponseId);
    }

    [Theory]
    [InlineData("https://example.invalid/")]
    [InlineData("https://generativelanguage.googleapis.com:8443/")]
    public async Task BatchClientRejectsOtherOriginsBeforeSendingCredentials(string baseAddress)
    {
        using var http = new HttpClient(new DelegateHandler((_, _) =>
            throw new InvalidOperationException("Unexpected HTTP request.")));
        var client = new GeminiBatchClient(http);
        var error = await Assert.ThrowsAsync<AiProviderException>(() => client.GetAsync(
            Connection() with { BaseAddress = new Uri(baseAddress) }, "fixture"u8.ToArray(), "batches/batch-1"));
        Assert.Equal("gemini_batch_configuration_invalid", error.SafeErrorCode);
    }

    [Fact]
    public async Task BatchClientAllowsValidAlternativeModelToReachProvider()
    {
        var calls = 0;
        var client = new GeminiBatchClient(
            new HttpClient(new DelegateHandler((_, _) =>
            {
                calls++;
                return Task.FromResult(JsonResponse("{}"));
            })));

        await Assert.ThrowsAsync<AiProviderException>(() =>
            client.GetAsync(
                Connection() with { ModelId = "gemini-3.6-flash" },
                Encoding.UTF8.GetBytes("test-key"),
                "batches/batch-1"));

        Assert.Equal(1, calls);
    }

    private static AiProviderRequest Request(string requestKey)
    {
        using var schema = JsonDocument.Parse(
            """{"type":"object","additionalProperties":false,"properties":{"ok":{"type":"boolean"}},"required":["ok"]}""");
        var media = Encoding.UTF8.GetBytes("synthetic-image");
        return new AiProviderRequest(
            requestKey,
            AiTaskTypes.InitialGrading,
            "prompt-v1",
            "schema-v1",
            "system",
            "user",
            schema.RootElement.Clone(),
            [
                new AiMediaPart(
                    "image/png",
                    media,
                    Convert.ToHexString(
                            System.Security.Cryptography.SHA256.HashData(media))
                        .ToLowerInvariant()),
            ]);
    }

    private static AiConnectionSettings Connection() =>
        new(
            "connection-1",
            AiProviders.GeminiDirect,
            new Uri("https://generativelanguage.googleapis.com/"),
            GeminiBatchClient.SelectedModel,
            TimeSpan.FromSeconds(30));

    private static HttpResponseMessage JsonResponse(string value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        Dictionary<string, string> Headers,
        string? Body);

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
