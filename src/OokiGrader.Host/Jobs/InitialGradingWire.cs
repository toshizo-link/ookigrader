using System.Text.Json;
using System.Text.Json.Nodes;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Application.Grading;

namespace OokiGrader.Host.Jobs;

internal static class InitialGradingWire
{
    // The immutable key also identifies the wire encoding after batch request
    // JSON is purged. Old queued requests and their retries keep their encoding.
    private const string CompactPrefix = "grade_c1_";

    internal static bool IsCompact(string key) =>
        key.StartsWith(CompactPrefix, StringComparison.Ordinal);

    internal static string RequestKey(string id, string provider) =>
        (provider == AiProviders.GeminiDirect ? CompactPrefix : "grade_") + id;

    internal static string RetryKey(string id, string previousKey) =>
        (IsCompact(previousKey) ? CompactPrefix : "grade_") + id;

    internal static JsonElement ResponseSchema(JsonElement canonical, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var schema = JsonNode.Parse(canonical.GetRawText())!;
        var properties = schema["properties"]!;
        properties["schema_version"]!["enum"] = new JsonArray(AiGradingResponseValidator.CompactSchemaVersion);
        var id = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = count };
        properties["missing_question_ids"]!["items"] = id.DeepClone();
        var result = properties["results"]!["items"]!;
        var full = result["properties"]!;
        result["properties"] = new JsonObject
        {
            ["q"] = id,
            ["m"] = full["evidence_media_index"]!.DeepClone(),
            ["t"] = full["transcription"]!.DeepClone(),
            ["l"] = full["legibility"]!.DeepClone(),
            ["b"] = full["blank"]!.DeepClone(),
            ["o"] = full["proposed_outcome"]!.DeepClone(),
            ["p"] = full["proposed_points_milli"]!.DeepClone(),
            ["c"] = full["confidence"]!.DeepClone(),
        };
        result["required"] = new JsonArray("q", "m", "t", "l", "b", "o", "p", "c");
        return JsonSerializer.SerializeToElement(schema);
    }
}
