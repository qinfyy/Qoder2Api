using System.Text.Json;
using System.Text.Json.Serialization;
using reg.Services.Qoder;

namespace reg.Endpoints;

/// <summary>
/// OpenAI 规范的错误响应。
///
/// 为什么必须用它而不是 <c>Results.Problem(...)</c>：后者返回的是 RFC 7807
/// <c>application/problem+json</c>，结构是 <c>{type,title,status,detail}</c>。
/// OpenAI 客户端（NextChat / Cherry Studio / Cursor / 各家 SDK）解析的是
/// <c>{"error":{"message","type","code","param"}}</c>——拿到 problem+json 会
/// 解析失败或显示成空白错误，用户完全看不到原因。
/// </summary>
public static class OpenAiErrors
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>构造 OpenAI 错误响应体。</summary>
    public static IResult Json(int status, string message, string type, string? code = null, string? param = null)
    {
        var body = new ErrorEnvelope
        {
            Error = new ErrorBody
            {
                Message = message,
                Type = type,
                Code = code,
                Param = param,
            },
        };
        return Results.Json(body, JsonOpts, statusCode: status);
    }

    /// <summary>API Key 无效/缺失（401）。</summary>
    public static IResult Unauthorized(string message) =>
        Json(StatusCodes.Status401Unauthorized, message, "invalid_request_error", "invalid_api_key");

    /// <summary>
    /// 池内无可用账号（503）。
    /// 用 503 而不是 401：401 会让客户端以为 API Key 错了、直接停止重试；
    /// 503 才是"服务暂时不可用、稍后再试"的正确信号。
    /// </summary>
    public static IResult NoAccount(string message) =>
        Json(StatusCodes.Status503ServiceUnavailable, message, "api_error", "no_healthy_account");

    /// <summary>把带分类的上游错误映射成 OpenAI 错误响应。</summary>
    public static IResult FromUpstream(QoderUpstreamException ex)
    {
        var (status, message) = ex.Kind switch
        {
            QoderErrorKind.SoftRate or QoderErrorKind.ModelRateLimit => (
                StatusCodes.Status429TooManyRequests,
                "所有可用账号当前均在限流中，请稍后重试。"),
            QoderErrorKind.ServiceBusy => (
                StatusCodes.Status503ServiceUnavailable,
                "上游服务当前繁忙（排队中），请稍后重试。"),
            QoderErrorKind.HardCredit => (
                StatusCodes.Status429TooManyRequests,
                "账号额度已耗尽，请等待额度重置或更换账号。"),
            QoderErrorKind.SessionDead or QoderErrorKind.AccountFault => (
                StatusCodes.Status401Unauthorized,
                "账号凭证失效或受限，请在管理页重新登录。"),
            QoderErrorKind.ContentBlocked => (
                StatusCodes.Status400BadRequest,
                "请求内容被上游安全策略拦截。"),
            QoderErrorKind.PromptTooLong => (
                StatusCodes.Status400BadRequest,
                "请求上下文超出模型上限。"),
            QoderErrorKind.BadParams => (
                StatusCodes.Status400BadRequest,
                "请求参数不被上游接受。"),
            QoderErrorKind.Transport => (
                StatusCodes.Status502BadGateway,
                "无法连接到上游服务（网络/链路问题），请稍后重试。"),
            _ => (StatusCodes.Status502BadGateway, "上游服务暂时不可用。"),
        };

        // 上游原文优先透传（它带 code/msg/requestId，比本地文案信息量大得多）。
        string detail = string.IsNullOrWhiteSpace(ex.RawBody) ? ex.Message : ex.RawBody;
        return Json(status, detail, "api_error", ex.Kind.ToCode());
    }

    private sealed class ErrorEnvelope
    {
        [JsonPropertyName("error")]
        public ErrorBody Error { get; set; } = new();
    }

    private sealed class ErrorBody
    {
        [JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "api_error";

        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("param")]
        public string? Param { get; set; }
    }
}
