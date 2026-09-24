using System.Text.RegularExpressions;

namespace reg.Services.Qoder;

/// <summary>
/// 上游错误的分类。号池据此决定「罚不罚、罚多久、换不换号」。
///
/// 设计纪律（借自同类反代项目的骨架）：**分类只有一个入口 <see cref="QoderErrorClassifier.Classify"/>**，
/// 禁止在 endpoint / 代理层散写 if-else 判错误。新增错误形态一律改本文件。
///
/// 现实约束：抓包样本里**没有任何错误响应**（两个 HAR 的 statusCodeValue 全是 200，
/// 也没有 4xx/5xx 样本）。所以分类表是按「HTTP 状态码 + 信封 + 关键词」结构化推断的，
/// 不是从真实业务码反推的。判不出的形态一律落 <see cref="QoderErrorKind.Client"/>
/// （只换号不重罚）并打日志，便于后续据日志补表。
/// </summary>
public enum QoderErrorKind
{
    /// <summary>不是错误。</summary>
    None = 0,

    /// <summary>限流（429 / rate limit 文案）。软冷却，冷却时长对齐上游重置墙钟。</summary>
    SoftRate,

    /// <summary>
    /// 服务排队/繁忙（上游 403 + 10605 + <c>isQueued</c>）。
    ///
    /// **实测样本**（2026-09-24 真实抓到的响应体）：
    /// <code>
    /// {"code":"403","message":"{\"code\":\"10605\",\"message\":\"{\\\"isQueued\\\":true,
    ///  \\\"modelKey\\\":\\\"auto\\\",\\\"queueCount\\\":0,\\\"queueType\\\":\\\"p3\\\",
    ///  \\\"retryAfterSeconds\\\":30,\\\"serviceAvailable\\\":false,\\\"waitTime\\\":30}\"}"}
    /// </code>
    ///
    /// 这条样本推翻了「403 = 账号被封」的直觉假设：Qoder 用 403 表达**服务级繁忙**，
    /// 与账号健康无关。若按封号处理，会把完全健康的账号冷却 2 小时，
    /// 一次上游高峰就能把整个号池打成不可用。
    ///
    /// 处置：按上游给的 <c>retryAfterSeconds</c> 短冷却（拿不到就退避），
    /// 换号继续（其他账号未必在排队）；**不计入账号失败统计**——账号没做错任何事。
    /// </summary>
    ServiceBusy,

    /// <summary>额度/余额耗尽（402 / quota 文案）。硬冷却到次日签到，期间不参与兜底。</summary>
    HardCredit,

    /// <summary>模型级限流：账号本身健康，只有该模型不可用。写模型级冷却，切模型即豁免。</summary>
    ModelRateLimit,

    /// <summary>会话失效（401 / session not found）。连续若干次才判定账号死亡。</summary>
    SessionDead,

    /// <summary>上游偶发 404。固定短冷却，不按限流退避升级（路径缺失不是限流信号）。</summary>
    NotFound,

    /// <summary>上游 5xx。喂熔断器，指数退避。</summary>
    Server,

    /// <summary>
    /// 传输层失败：连不上上游（DNS/连接被拒/TLS 握手失败/超时）。
    ///
    /// 实测触发场景：上游网络抖动时 <c>SSL connection could not be established</c>。
    /// 这类失败与**账号**无关——同一时刻换任何账号都一样连不上。
    ///
    /// 处置：喂连败计数（连续多次说明本机到上游的链路有问题，该账号暂时别用），
    /// 但**不计入账号的成功率**。否则一次网络抖动就会永久拉低该账号的选号权重，
    /// 而它其实完全健康。
    /// </summary>
    Transport,

    /// <summary>账号级授权/风控故障（403 / forbidden / banned）。长冷却，不自动复活。</summary>
    AccountFault,

    /// <summary>内容策略拦截。**请求的问题不是账号的问题**——零动作、不轮转，原文透传。</summary>
    ContentBlocked,

    /// <summary>请求体本身有问题（参数错误）。不罚号，但**仍轮转**（不同账号模型权限可能不同）。</summary>
    BadParams,

    /// <summary>上下文超长。**请求的问题不是账号的问题**——零动作、不轮转，原文透传。</summary>
    PromptTooLong,

    /// <summary>其余 4xx 或无法归类的错误。只换号不重罚，喂连败计数兜底。</summary>
    Client,
}

public static class QoderErrorKindExtensions
{
    /// <summary>
    /// 是否应该换一个账号重试。
    /// 内容拦截与上下文超长是**请求级**问题——同一个 body 换任何号都会得到相同结果，
    /// 轮转只是白白消耗其他账号的配额，且会把真实错误掩盖成"全部账号不可用"。
    /// </summary>
    public static bool IsRetryable(this QoderErrorKind kind) => kind switch
    {
        QoderErrorKind.ContentBlocked => false,
        QoderErrorKind.PromptTooLong => false,
        QoderErrorKind.None => false,
        _ => true,
    };

    /// <summary>
    /// 是否要对该账号施加惩罚（冷却/熔断/降权）。
    /// 「请求的问题」类错误（内容拦截/上下文超长/参数错误）**不罚号**——账号没做错任何事。
    /// </summary>
    public static bool PenalizesAccount(this QoderErrorKind kind) => kind switch
    {
        QoderErrorKind.ContentBlocked => false,
        QoderErrorKind.PromptTooLong => false,
        QoderErrorKind.BadParams => false,
        QoderErrorKind.None => false,
        _ => true,
    };

    /// <summary>
    /// 是否把这次失败计入**账号的**失败统计（err_total / 成功率 EMA）。
    ///
    /// 与 <see cref="PenalizesAccount"/> 的区别在于"要不要短暂避让"与"算不算账号的锅"
    /// 是两件事：<see cref="QoderErrorKind.ServiceBusy"/> 需要短冷却（避免继续打一个
    /// 正在排队的服务），但那是**服务级**状况、账号本身完全健康，记到账号头上会让
    /// 它的成功率被上游高峰永久拉低。
    /// </summary>
    public static bool CountsAsAccountFailure(this QoderErrorKind kind) => kind switch
    {
        // 服务级繁忙与传输层失败都不是账号的锅，不该污染它的成功率。
        QoderErrorKind.ServiceBusy => false,
        QoderErrorKind.Transport => false,
        _ => kind.PenalizesAccount(),
    };

    public static string ToCode(this QoderErrorKind kind) => kind switch
    {
        QoderErrorKind.SoftRate => "rate_limit_exceeded",
        QoderErrorKind.ServiceBusy => "service_busy",
        QoderErrorKind.HardCredit => "insufficient_quota",
        QoderErrorKind.ModelRateLimit => "model_rate_limited",
        QoderErrorKind.SessionDead => "session_expired",
        QoderErrorKind.NotFound => "upstream_not_found",
        QoderErrorKind.Server => "upstream_error",
        QoderErrorKind.Transport => "upstream_unreachable",
        QoderErrorKind.AccountFault => "account_forbidden",
        QoderErrorKind.ContentBlocked => "content_blocked",
        QoderErrorKind.BadParams => "invalid_request_error",
        QoderErrorKind.PromptTooLong => "context_length_exceeded",
        _ => "upstream_error",
    };
}

/// <summary>
/// 带分类的上游错误。代理层抛出它，endpoint 据此决定轮换与惩罚。
/// </summary>
public sealed class QoderUpstreamException : Exception
{
    public QoderErrorKind Kind { get; }

    /// <summary>HTTP 状态码（信封内错误时为 null，因为那时 HTTP 本身是 200）。</summary>
    public int? HttpStatus { get; }

    /// <summary>上游原始响应体（透传给客户端时用原文，不加工）。</summary>
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
    // ---- 关键词表（统一小写比较；中文按原文比较）----

    /// <summary>额度耗尽。命中的都是"计费余额没了"，与"限流"是两回事。</summary>
    private static readonly string[] HardCreditMarkers =
    [
        "insufficient credit", "insufficient_quota", "no credit", "credit exhausted",
        "credits exhausted", "out of credit", "quota exceeded", "quota exhaust",
        "payment required", "not enough credit", "insufficient balance", "balance is insufficient",
        "积分不足", "额度不足", "余额不足", "积分用完", "额度用尽", "没有积分", "配额不足",
    ];

    /// <summary>
    /// 限流。注意 "usage limit" 归这里（用量节流），不归余额。
    /// **刻意不收裸 "too many"**：它会命中 "too many tokens"（那是上下文超长，
    /// 属请求级错误、不该罚号），只收完整短语 "too many requests"。
    /// </summary>
    private static readonly string[] SoftRateMarkers =
    [
        "rate limit", "rate-limit", "ratelimit", "too many requests",
        "usage limit", "throttl", "overloaded",
        "请求过于频繁", "限流", "频繁", "稍后重试",
    ];

    /// <summary>
    /// 模型级限流的**窄短语**：必须与"账号整体被限"区分开。
    /// 刻意不收裸 "model"/"模型"——几乎所有错误文案都可能提到模型，
    /// 那会把账号级限流误判成模型级，进而错误地给出"切模型豁免"。
    /// 只收明确表达「是这个模型被限」的短语。
    /// </summary>
    private static readonly string[] ModelRateLimitMarkers =
    [
        "model rate limit", "model is rate limited", "this model", "model usage limit",
        "model has reached", "current model",
        "该模型", "模型限流", "模型达到上限", "当前模型",
    ];

    /// <summary>
    /// 服务排队/繁忙的标记（实测样本 code 10605）。命中即说明"服务忙"而非"账号坏"，
    /// 必须优先于 403→封号 的判定。
    /// </summary>
    private static readonly string[] ServiceBusyMarkers =
    [
        "isqueued", "serviceavailable", "retryafterseconds", "waittime",
        "queuecount", "queuetype",
        "排队", "服务繁忙", "稍后重试",
    ];

    /// <summary>会话失效。</summary>
    private static readonly string[] SessionDeadMarkers =
    [
        "session not found", "session expired", "session is invalid", "offline user session",
        "invalid session", "token expired", "token is expired", "unauthenticated",
        "会话失效", "登录已过期", "凭证过期",
    ];

    /// <summary>账号级授权/风控。</summary>
    private static readonly string[] AccountFaultMarkers =
    [
        "forbidden", "request illegal", "banned", "not activated", "account suspended",
        "unauthorized account", "risk control",
        "账号被封", "账号异常", "未激活",
    ];

    /// <summary>内容策略拦截。</summary>
    private static readonly string[] ContentBlockedMarkers =
    [
        "content policy", "security policy", "blocked by", "content filter",
        "unapproved channel", "illegal api invocation", "safety",
        "内容审核", "违规", "敏感",
    ];

    /// <summary>上下文超长。</summary>
    private static readonly string[] PromptTooLongMarkers =
    [
        "prompt is too long", "context length", "too many tokens", "maximum context",
        "exceeds the maximum", "input is too long", "token limit",
        "上下文超长", "超出长度",
    ];

    /// <summary>参数错误。</summary>
    private static readonly string[] BadParamsMarkers =
    [
        "unmarshal", "invalid request", "invalid parameter", "missing required",
        "malformed", "parse error", "invalid json", "validation failed",
        "参数错误", "格式错误",
    ];

    /// <summary>上游「将在 … 重置」的中文墙钟文案。</summary>
    private static readonly Regex ResetCn = new(@"将在\s*(.+?)\s*重置", RegexOptions.Compiled);

    /// <summary>上游「reset at YYYY-MM-DD HH:MM:SS」的英文墙钟文案。</summary>
    private static readonly Regex ResetEn = new(
        @"reset\s+at\s+(\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 单一分类入口。
    /// </summary>
    /// <param name="httpStatus">HTTP 状态码；信封内错误传 null（此时 HTTP 是 200）。</param>
    /// <param name="body">上游响应体原文。</param>
    public static QoderErrorKind Classify(int? httpStatus, string? body)
    {
        string lower = (body ?? string.Empty).ToLowerInvariant();
        string raw = body ?? string.Empty;

        // 判定顺序即优先级，自上而下短路。顺序是有讲究的：
        // 「余额耗尽」必须先于「限流」——429 也可能带 quota 文案，那种情况罚得该更重。

        if (ContainsAny(lower, raw, HardCreditMarkers) || httpStatus == 402)
        {
            return QoderErrorKind.HardCredit;
        }

        // 服务排队/繁忙必须**先于** AccountFault 判定。
        // 实测：Qoder 用 403 表达服务级排队（code 10605 + isQueued），
        // 若按 403→封号 处理，一次上游高峰就会把健康账号冷却 2 小时、整个池打成不可用。
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

    /// <summary>
    /// 从限流响应里解析上游给出的**权威重置时刻**。
    ///
    /// 这是「指数退避把全池推到封顶」的根治办法：上游明确说了什么时候恢复，
    /// 就该精确对齐那个墙钟，而不是盲目翻倍。判不出返回 null，由调用方退避。
    /// </summary>
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

    /// <summary>
    /// 从服务排队响应里解析上游明示的等待秒数（<c>retryAfterSeconds</c>）。
    /// 这是上游给的权威避让时长，比本地退避猜测准确。判不出返回 null。
    /// </summary>
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
    /// <summary>仅当以后缀结尾时移除（string.TrimSuffix 在 .NET 里不存在）。</summary>
    public static string TrimSuffix(this string s, string suffix) =>
        s.EndsWith(suffix, StringComparison.Ordinal) ? s[..^suffix.Length] : s;
}
