using System.Text.Json;
using System.Text.Json.Serialization;

namespace reg.Models;

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

    /// <summary>OpenAI 新参数名，与 max_tokens 等价（两者都给时以 max_tokens 为准）。</summary>
    [JsonPropertyName("max_completion_tokens")]
    public int? MaxCompletionTokens { get; set; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }

    /// <summary>
    /// 流式附加选项。OpenAI 契约：<c>include_usage=true</c> 时必须在 [DONE] **之前**
    /// 发一个 <c>choices: []</c> + <c>usage</c> 的终止 chunk；缺省或 false 时**必须不发**。
    /// 这不是优化而是契约——没请求 usage 的客户端收到那个 chunk 会当成异常数据。
    /// </summary>
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

    /// <summary>客户端是否要求流式返回真实用量。</summary>
    [JsonIgnore]
    public bool IncludeUsage => StreamOptions?.IncludeUsage == true;

    /// <summary>生效的最大输出 token 数（max_tokens 优先，回落 max_completion_tokens）。</summary>
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

// ---------------------------------------------------------------------------
// 用量
// ---------------------------------------------------------------------------

/// <summary>token 用量的来源，用于区分"实测"与"估算"——两者绝不能混为一谈。</summary>
public enum UsageSource
{
    /// <summary>未知/未观测（哨兵：**不是** 0）。</summary>
    None = 0,

    /// <summary>上游 SSE 给出的真实值。</summary>
    Upstream = 1,

    /// <summary>本地估算（上游未给 usage 时的兜底）。</summary>
    Estimated = 2,
}

/// <summary>
/// OpenAI 规范的 usage。哨兵原则：观测缺失留 null 而非 0——
/// "测得 0" 与 "没观测到" 混同会伪造统计。序列化时 null 字段省略。
/// </summary>
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

    /// <summary>Qoder 私有：本次请求的实际扣费。不属于 OpenAI 规范，但值得透出。</summary>
    [JsonPropertyName("credits")]
    public double? Credits { get; set; }

    /// <summary>Qoder 私有：上游是否标记本次为计费请求。</summary>
    [JsonPropertyName("billable")]
    public bool? Billable { get; set; }

    /// <summary>来源标记（非 OpenAI 字段，便于排查"这个数是真的还是估的"）。</summary>
    [JsonPropertyName("x_usage_source")]
    public string? Source { get; set; }

    /// <summary>补全 total（上游偶有缺失时按 prompt+completion 相加）。</summary>
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
