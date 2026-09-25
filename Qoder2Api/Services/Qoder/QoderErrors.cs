using System.Text.RegularExpressions;

namespace Qoder2Api.Services.Qoder;

public enum QoderErrorKind
{
    /// <summary>不是错误。</summary>
    None = 0,

    /// <summary>限流。</summary>
    SoftRate,

    /// <summary> 模型排队 </summary>
    ModelQueued,

    /// <summary> 服务排队/繁忙 </summary>
    ServiceBusy,

    /// <summary>额度/余额耗尽。</summary>
    HardCredit,

    /// <summary>模型级限流。</summary>
    ModelRateLimit,

    /// <summary>会话失效。</summary>
    SessionDead,

    /// <summary>上游 404。</summary>
    NotFound,

    /// <summary>请求的模型不存在（下游传了未登记的模型名）。</summary>
    ModelNotFound,

    /// <summary>上游 5xx。</summary>
    Server,

    /// <summary> 传输层失败。</summary>
    Transport,

    /// <summary>账号级授权/风控故障。</summary>
    AccountFault,

    /// <summary>内容策略拦截。</summary>
    ContentBlocked,

    /// <summary>请求体本身有问题。</summary>
    BadParams,

    /// <summary>上下文超长。</summary>
    PromptTooLong,

    /// <summary>其余 4xx 或无法归类的错误。</summary>
    Client,
}

public static class QoderErrorKindExtensions
{
    public static bool IsRetryable(this QoderErrorKind kind) => kind switch
    {
        QoderErrorKind.ContentBlocked => false,
        QoderErrorKind.PromptTooLong => false,
        QoderErrorKind.BadParams => false,
        QoderErrorKind.ModelNotFound => false,
        QoderErrorKind.None => false,
        _ => true,
    };

    public static bool PenalizesAccount(this QoderErrorKind kind) => kind switch
    {
        QoderErrorKind.ContentBlocked => false,
        QoderErrorKind.PromptTooLong => false,
        QoderErrorKind.BadParams => false,
        QoderErrorKind.ModelNotFound => false,
        QoderErrorKind.None => false,
        // 排队是正常状态，不是账号的错——惩罚它只会把健康账号冷却掉，
        // 反而逼着请求去换号、丢 prompt 缓存。
        QoderErrorKind.ModelQueued => false,
        _ => true,
    };

    public static bool CountsAsAccountFailure(this QoderErrorKind kind) => kind switch
    {
        // 服务级繁忙、传输层失败、模型排队都不是账号的锅，不该污染它的成功率。
        QoderErrorKind.ServiceBusy => false,
        QoderErrorKind.Transport => false,
        QoderErrorKind.ModelQueued => false,
        _ => kind.PenalizesAccount(),
    };

    public static string ToCode(this QoderErrorKind kind) => kind switch
    {
        QoderErrorKind.SoftRate => "rate_limit_exceeded",
        QoderErrorKind.ServiceBusy => "service_busy",
        QoderErrorKind.ModelQueued => "model_queued",
        QoderErrorKind.HardCredit => "insufficient_quota",
        QoderErrorKind.ModelRateLimit => "model_rate_limited",
        QoderErrorKind.SessionDead => "session_expired",
        QoderErrorKind.NotFound => "upstream_not_found",
        QoderErrorKind.ModelNotFound => "model_not_found",
        QoderErrorKind.Server => "upstream_error",
        QoderErrorKind.Transport => "upstream_unreachable",
        QoderErrorKind.AccountFault => "account_forbidden",
        QoderErrorKind.ContentBlocked => "content_blocked",
        QoderErrorKind.BadParams => "invalid_request_error",
        QoderErrorKind.PromptTooLong => "context_length_exceeded",
        _ => "upstream_error",
    };
}

public sealed class QoderUpstreamException : Exception
{
    public QoderErrorKind Kind { get; }

    public int? HttpStatus { get; }

    public string RawBody { get; }

    public QoderUpstreamException(QoderErrorKind kind, int? httpStatus, string rawBody, string message)
        : base(message)
    {
        Kind = kind;
        HttpStatus = httpStatus;
        RawBody = rawBody;
    }
}

public static class QoderErrorClassifier
{
    // 额度耗尽。</summary>
    private static readonly string[] HardCreditMarkers =
    [
        "insufficient credit", "insufficient_quota", "no credit", "credit exhausted",
        "credits exhausted", "out of credit", "quota exceeded", "quota exhaust",
        "payment required", "not enough credit", "insufficient balance", "balance is insufficient",
        "积分不足", "额度不足", "余额不足", "积分用完", "额度用尽", "没有积分", "配额不足",
    ];

    // 限流。
    private static readonly string[] SoftRateMarkers =
    [
        "rate limit", "rate-limit", "ratelimit", "too many requests",
        "usage limit", "throttl", "overloaded",
        "请求过于频繁", "限流", "频繁", "稍后重试",
    ];

    // 模型级限流
    private static readonly string[] ModelRateLimitMarkers =
    [
        "model rate limit", "model is rate limited", "this model", "model usage limit",
        "model has reached", "current model",
        "该模型", "模型限流", "模型达到上限", "当前模型",
    ];

    // 服务排队/繁忙的标记（实测样本 code 10605）。
    private static readonly string[] ServiceBusyMarkers =
    [
        "isqueued", "serviceavailable", "retryafterseconds", "waittime",
        "queuecount", "queuetype",
        "排队", "服务繁忙", "稍后重试",
    ];

    // 会话失效。
    private static readonly string[] SessionDeadMarkers =
    [
        "session not found", "session expired", "session is invalid", "offline user session",
        "invalid session", "token expired", "token is expired", "unauthenticated",
        "会话失效", "登录已过期", "凭证过期",
    ];

    // 账号级授权/风控。
    private static readonly string[] AccountFaultMarkers =
    [
        "forbidden", "request illegal", "banned", "not activated", "account suspended",
        "unauthorized account", "risk control",
        "账号被封", "账号异常", "未激活",
    ];

    // 内容策略拦截。
    private static readonly string[] ContentBlockedMarkers =
    [
        "content policy", "security policy", "blocked by", "content filter",
        "unapproved channel", "illegal api invocation", "safety",
        "内容审核", "违规", "敏感",
    ];

    // 上下文超长。
    private static readonly string[] PromptTooLongMarkers =
    [
        "prompt is too long", "context length", "too many tokens", "maximum context",
        "exceeds the maximum", "input is too long", "token limit",
        "上下文超长", "超出长度",
    ];

    // 参数错误。
    private static readonly string[] BadParamsMarkers =
    [
        "unmarshal", "invalid request", "invalid parameter", "missing required",
        "malformed", "parse error", "invalid json", "validation failed",
        "参数错误", "格式错误",
    ];

    // 上游「将在 … 重置」的中文文案。
    private static readonly Regex ResetCn = new(@"将在\s*(.+?)\s*重置", RegexOptions.Compiled);

    // 上游「reset at YYYY-MM-DD HH:MM:SS」的英文墙钟文案。
    private static readonly Regex ResetEn = new(
        @"reset\s+at\s+(\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 单一分类入口。
    public static QoderErrorKind Classify(int? httpStatus, string? body)
    {
        string lower = (body ?? string.Empty).ToLowerInvariant();
        string raw = body ?? string.Empty;


        if (ContainsAny(lower, raw, HardCreditMarkers) || httpStatus == 402)
        {
            return QoderErrorKind.HardCredit;
        }

        // 必须先于 AccountFault 判定：Qoder 用 403 表达服务级排队，
        // 按封号处理会让一次上游高峰把整个池冷却 2 小时。
        // 用结构化解析而非子串匹配，避免被正文里偶然出现的同名串误导。
        if (QoderQueueParser.HasModelQueuedCode(raw) || QoderQueueParser.Parse(raw)?.IsQueued == true)
        {
            return QoderErrorKind.ModelQueued;
        }

        if (ContainsAny(lower, raw, ServiceBusyMarkers))
        {
            return QoderErrorKind.ServiceBusy;
        }

        if (ContainsAny(lower, raw, SessionDeadMarkers) || httpStatus == 401)
        {
            return QoderErrorKind.SessionDead;
        }

        if (ContainsAny(lower, raw, AccountFaultMarkers) || httpStatus == 403)
        {
            return QoderErrorKind.AccountFault;
        }

        // 模型级限流：必须是「限流信号 + 模型指向」同时成立才判，避免把
        // 「模型不存在」之类的参数错误误判成限流。
        if (httpStatus == 429 || ContainsAny(lower, raw, SoftRateMarkers))
        {
            if (ContainsAny(lower, raw, ModelRateLimitMarkers))
            {
                return QoderErrorKind.ModelRateLimit;
            }
            return QoderErrorKind.SoftRate;
        }

        if (httpStatus is 400 or 413 or 422)
        {
            // 400 类：先区分「请求太长」「内容被拦」「参数错」三种零/轻惩罚形态。
            if (ContainsAny(lower, raw, PromptTooLongMarkers))
            {
                return QoderErrorKind.PromptTooLong;
            }
            if (ContainsAny(lower, raw, ContentBlockedMarkers))
            {
                return QoderErrorKind.ContentBlocked;
            }
            if (ContainsAny(lower, raw, BadParamsMarkers))
            {
                return QoderErrorKind.BadParams;
            }
            // 400 但判不出具体形态：仍按参数错误处理（不罚号、换号再试）。
            return QoderErrorKind.BadParams;
        }

        if (httpStatus == 404)
        {
            return QoderErrorKind.NotFound;
        }

        if (httpStatus >= 500)
        {
            return QoderErrorKind.Server;
        }

        // 关键词兜底：有些错误以 HTTP 200 + 业务错误码的形式返回（信封 statusCodeValue
        // 非 200），此时 httpStatus 为 null，只能靠文案判。
        if (ContainsAny(lower, raw, PromptTooLongMarkers))
        {
            return QoderErrorKind.PromptTooLong;
        }
        if (ContainsAny(lower, raw, ContentBlockedMarkers))
        {
            return QoderErrorKind.ContentBlocked;
        }

        if (httpStatus >= 400 || !string.IsNullOrWhiteSpace(body))
        {
            return QoderErrorKind.Client;
        }

        return QoderErrorKind.None;
    }

    public static DateTimeOffset? ParseRateReset(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var m = ResetCn.Match(body);
        string? ts = m.Success ? m.Groups[1].Value : null;
        if (ts is null)
        {
            var en = ResetEn.Match(body);
            if (en.Success)
            {
                ts = en.Groups[1].Value;
            }
        }
        if (string.IsNullOrWhiteSpace(ts))
        {
            return null;
        }

        ts = ts.Trim().TrimSuffix(" UTC+8").Trim();

        // 上游文案用的是东八区墙钟，按本地时间解析后原样返回（不强行换时区，
        // 避免把已经正确的本地时刻又偏移一次）。
        string[] formats =
        [
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss",
            "yyyy/MM/dd HH:mm:ss", "MM-dd HH:mm:ss", "HH:mm:ss",
        ];
        if (DateTime.TryParseExact(ts, formats, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Local));
        }
        if (DateTimeOffset.TryParse(ts, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dto))
        {
            return dto;
        }
        return null;
    }

    public static int? ParseRetryAfterSeconds(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        // 上游把内层 JSON 层层转义成了字符串（实测样本里字段名前有三层反斜杠：
        // \\\"retryAfterSeconds\\\":30）。逐层剥反斜杠再匹配，比写一个能吃任意层数
        // 转义的正则可靠得多。
        string cleaned = body.Replace("\\", "");
        var m = Regex.Match(cleaned, @"retryAfterSeconds\D*?(\d+)", RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            m = Regex.Match(cleaned, @"retry_after\D*?(\d+)", RegexOptions.IgnoreCase);
        }
        if (m.Success && int.TryParse(m.Groups[1].Value, out var secs) && secs > 0)
        {
            return Math.Min(secs, 3600); // 钳到 1 小时，防上游给出异常大的值
        }
        return null;
    }

    private static bool ContainsAny(string lower, string raw, string[] markers)
    {
        foreach (var m in markers)
        {
            if (lower.Contains(m, StringComparison.Ordinal) || raw.Contains(m, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}

internal static class StringTrimExtensions
{
    public static string TrimSuffix(this string s, string suffix) => s.EndsWith(suffix, StringComparison.Ordinal) ? s[..^suffix.Length] : s;
}
