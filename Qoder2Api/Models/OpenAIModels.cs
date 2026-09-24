using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qoder2Api.Models;

public class ChatCompletionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "auto";

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = [];

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("max_completion_tokens")]
    public int? MaxCompletionTokens { get; set; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }

    [JsonPropertyName("stream_options")]
    public StreamOptions? StreamOptions { get; set; }

    [JsonPropertyName("stop")]
    public JsonElement? Stop { get; set; }

    [JsonPropertyName("presence_penalty")]
    public double? PresencePenalty { get; set; }

    [JsonPropertyName("frequency_penalty")]
    public double? FrequencyPenalty { get; set; }

    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    [JsonPropertyName("n")]
    public int? N { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("tools")]
    public List<JsonElement>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    public JsonElement? ToolChoice { get; set; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; set; }

    [JsonIgnore]
    public bool IncludeUsage => StreamOptions?.IncludeUsage == true;

    [JsonIgnore]
    public int? EffectiveMaxTokens => MaxTokens ?? MaxCompletionTokens;
}

public class StreamOptions
{
    [JsonPropertyName("include_usage")]
    public bool IncludeUsage { get; set; }
}

public class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public object? Content { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<JsonElement>? ToolCalls { get; set; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    public string GetTextContent()
    {
        if (Content == null) return string.Empty;
        if (Content is string s) return s;
        if (Content is JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.String) return el.GetString() ?? string.Empty;
            if (el.ValueKind == JsonValueKind.Array)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var item in el.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var textProp))
                    {
                        sb.Append(textProp.GetString());
                    }
                }
                return sb.ToString();
            }
        }
        return Content.ToString() ?? string.Empty;
    }
}

public class ModelListResponse
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = "list";

    [JsonPropertyName("data")]
    public List<ModelItem> Data { get; set; } = [];
}

public class ModelItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("object")]
    public string Object { get; set; } = "model";

    [JsonPropertyName("created")]
    public long Created { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [JsonPropertyName("owned_by")]
    public string OwnedBy { get; set; } = "qoder";
}

public enum UsageSource
{
    None = 0,

    Upstream = 1,

    Estimated = 2,
}

public sealed class UsageInfo
{
    [JsonPropertyName("prompt_tokens")]
    public int? PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int? CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int? TotalTokens { get; set; }

    [JsonPropertyName("prompt_tokens_details")]
    public PromptTokensDetails? PromptTokensDetails { get; set; }

    [JsonPropertyName("completion_tokens_details")]
    public CompletionTokensDetails? CompletionTokensDetails { get; set; }

    // Qoder 私有：本次请求的实际扣费。不属于 OpenAI 规范，但值得透出。
    [JsonPropertyName("credits")]
    public double? Credits { get; set; }

    // Qoder 私有：上游是否标记本次为计费请求。
    [JsonPropertyName("billable")]
    public bool? Billable { get; set; }

    [JsonPropertyName("x_usage_source")]
    public string? Source { get; set; }

    public void EnsureTotal()
    {
        if (TotalTokens is null && PromptTokens is { } p && CompletionTokens is { } c)
        {
            TotalTokens = p + c;
        }
    }
}

public sealed class PromptTokensDetails
{
    [JsonPropertyName("cached_tokens")]
    public int? CachedTokens { get; set; }
}

public sealed class CompletionTokensDetails
{
    [JsonPropertyName("reasoning_tokens")]
    public int? ReasoningTokens { get; set; }
}
