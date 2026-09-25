using System.Text.Json;
using System.Text.Json.Serialization;
using Qoder2Api.Services.Qoder;

namespace Qoder2Api.Endpoints;


public static class OpenAiErrors
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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

    public static IResult Unauthorized(string message) =>
        Json(StatusCodes.Status401Unauthorized, message, "invalid_request_error", "invalid_api_key");

    public static IResult NoAccount(string message) =>
        Json(StatusCodes.Status503ServiceUnavailable, message, "api_error", "no_healthy_account");

    /// <summary>模型不存在。对齐 OpenAI 的 model_not_found（404）。</summary>
    public static IResult ModelNotFound(string? model) =>
        Json(StatusCodes.Status404NotFound,
            $"模型 `{model}` 不存在（请在 models.xml 中登记）。",
            "invalid_request_error", "model_not_found", "model");

    public static IResult FromUpstream(QoderUpstreamException ex)
    {
        var (status, fallback) = ex.Kind switch
        {
            QoderErrorKind.SoftRate or QoderErrorKind.ModelRateLimit => (
                StatusCodes.Status429TooManyRequests,
                "所有可用账号当前均在限流中，请稍后重试。"),
            QoderErrorKind.ServiceBusy => (
                StatusCodes.Status503ServiceUnavailable,
                "上游服务当前繁忙（排队中），请稍后重试。"),
            QoderErrorKind.ModelQueued => (
                StatusCodes.Status503ServiceUnavailable,
                "模型当前排队时间过长，请稍后重试。"),
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
            QoderErrorKind.ModelNotFound => (
                StatusCodes.Status404NotFound,
                "模型不存在。"),
            QoderErrorKind.Transport => (
                StatusCodes.Status502BadGateway,
                "无法连接到上游服务（网络/链路问题），请稍后重试。"),
            _ => (StatusCodes.Status502BadGateway, "上游服务暂时不可用。"),
        };

        string detail = !string.IsNullOrWhiteSpace(ex.RawBody) ? ex.RawBody
            : !string.IsNullOrWhiteSpace(ex.Message) ? ex.Message
            : fallback;
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
