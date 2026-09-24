using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qoder2Api.Models;

public class QoderPayload
{
    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("request_set_id")]
    public string RequestSetId { get; set; } = string.Empty;

    [JsonPropertyName("chat_record_id")]
    public string ChatRecordId { get; set; } = string.Empty;

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = true;

    [JsonPropertyName("chat_task")]
    public string ChatTask { get; set; } = "FREE_INPUT";

    [JsonPropertyName("is_reply")]
    public bool IsReply { get; set; } = true;

    [JsonPropertyName("is_retry")]
    public bool IsRetry { get; set; } = false;

    [JsonPropertyName("source")]
    public int Source { get; set; } = 1;

    [JsonPropertyName("version")]
    public string Version { get; set; } = "3";

    [JsonPropertyName("session_type")]
    public string SessionType { get; set; } = "qodercli";

    [JsonPropertyName("agent_id")]
    public string AgentId { get; set; } = "agent_common";

    [JsonPropertyName("task_id")]
    public string TaskId { get; set; } = "common";

    [JsonPropertyName("code_language")]
    public string CodeLanguage { get; set; } = "";

    [JsonPropertyName("chat_prompt")]
    public string ChatPrompt { get; set; } = "";

    [JsonPropertyName("image_urls")]
    public object? ImageUrls { get; set; } = null;

    [JsonPropertyName("aliyun_user_type")]
    public string AliyunUserType { get; set; } = "";

    [JsonPropertyName("system")]
    public string System { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<QoderMessageItem> Messages { get; set; } = [];

    [JsonPropertyName("tools")]
    public List<JsonElement> Tools { get; set; } = [];

    [JsonPropertyName("parameters")]
    public QoderParameters Parameters { get; set; } = new();

    [JsonPropertyName("chat_context")]
    public QoderChatContext ChatContext { get; set; } = new();

    [JsonPropertyName("model_config")]
    public QoderModelConfig ModelConfig { get; set; } = new();

    [JsonPropertyName("business")]
    public QoderBusiness Business { get; set; } = new();
}

public class QoderMessageItem
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<JsonElement>? ToolCalls { get; set; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }
}

public class QoderParameters
{
    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 32768;

    // 客户端传了就透传；没传则整字段省略（上游用自己的默认值），
    // 而不是塞一个"看起来像默认值"的常数——那会覆盖上游更合理的默认。
    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TopP { get; set; }
}

public class QoderChatContext
{
    [JsonPropertyName("chatPrompt")]
    public string ChatPrompt { get; set; } = "";

    [JsonPropertyName("imageUrls")]
    public object? ImageUrls { get; set; } = null;

    [JsonPropertyName("extra")]
    public QoderChatExtra Extra { get; set; } = new();

    [JsonPropertyName("features")]
    public List<object> Features { get; set; } = [];

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

public class QoderChatExtra
{
    [JsonPropertyName("context")]
    public List<object> Context { get; set; } = [];

    [JsonPropertyName("modelConfig")]
    public QoderModelRef ModelConfig { get; set; } = new();

    [JsonPropertyName("originalContent")]
    public string OriginalContent { get; set; } = "";
}

public class QoderModelRef
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "auto";

    [JsonPropertyName("is_reasoning")]
    public bool IsReasoning { get; set; } = false;
}

public class QoderModelConfig
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "auto";

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "Auto";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("format")]
    public string Format { get; set; } = "openai";

    [JsonPropertyName("is_vl")]
    public bool IsVl { get; set; } = true;

    [JsonPropertyName("is_reasoning")]
    public bool IsReasoning { get; set; } = false;

    [JsonPropertyName("api_key")]
    public string ApiKey { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "system";

    [JsonPropertyName("max_input_tokens")]
    public int MaxInputTokens { get; set; } = 180000;
}

public class QoderBusiness
{
    [JsonPropertyName("product")]
    public string Product { get; set; } = "cli";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "agent";

    [JsonPropertyName("stage")]
    public string Stage { get; set; } = "start";

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("begin_at")]
    public long BeginAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public class QoderUserStatusResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("email")]
    public string Email { get; set; } = "";

    [JsonPropertyName("userType")]
    public string UserType { get; set; } = "";

    [JsonPropertyName("quota")]
    public double Quota { get; set; }

    [JsonPropertyName("whitelistStatus")]
    public string? WhitelistStatus { get; set; }

    [JsonPropertyName("avatarUrl")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("isQuotaExceeded")]
    public bool IsQuotaExceeded { get; set; }

    [JsonPropertyName("plan")]
    public string Plan { get; set; } = "";

    [JsonPropertyName("userTag")]
    public string? UserTag { get; set; }

    [JsonPropertyName("nextResetAt")]
    public long NextResetAt { get; set; }
}
