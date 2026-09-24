using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace reg.Services.Qoder;

/// <summary>
/// 上游模型目录里单个模型的动态信息。
///
/// 静态部分（displayName / 别名 / 上下文上限）在 models.xml 里维护；
/// 这里的**倍率与促销**是上游实时下发的，会随低峰时段变化。
/// </summary>
public sealed class ModelCatalogEntry
{
    public string Key { get; set; } = "";
    public string? DisplayName { get; set; }

    /// <summary>当前计费倍率。1 = 标准；&lt;1 = 打折（低峰）。</summary>
    public double? PriceFactor { get; set; }

    /// <summary>原始倍率（促销前的价格），用于展示"省了多少"。</summary>
    public double? OriginalPriceFactor { get; set; }

    /// <summary>是否免费。</summary>
    public bool? IsFree { get; set; }

    /// <summary>是否默认模型。</summary>
    public bool? IsDefault { get; set; }

    /// <summary>是否新模型。</summary>
    public bool? IsNew { get; set; }

    /// <summary>标签，如 limitedTimeFree（限时免费）。</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>促销徽标，如「错峰 4 折」。仅 active 时有值。</summary>
    public string? PromotionLabel { get; set; }

    /// <summary>促销说明，如「错峰时段4折优惠（10 PM-8 AM UTC+8）」。</summary>
    public string? PromotionDescription { get; set; }

    /// <summary>折扣时段，如「22:00-08:00」。</summary>
    public string? PromotionWindow { get; set; }

    /// <summary>折扣系数（0.4 = 打 4 折）。</summary>
    public double? DiscountFactor { get; set; }

    /// <summary>促销前的倍率。</summary>
    public double? BeforePromotionPriceFactor { get; set; }

    /// <summary>是否启用。</summary>
    public bool? Enabled { get; set; }

    /// <summary>是否支持视觉输入。</summary>
    public bool? IsVl { get; set; }

    /// <summary>是否深度思考模型。</summary>
    public bool? IsReasoning { get; set; }

    /// <summary>上游上下文上限。</summary>
    public int? MaxInputTokens { get; set; }

    /// <summary>是否正处于促销中（promotion.active）。</summary>
    public bool IsPromotionActive => PromotionLabel is not null || DiscountFactor is not null;
}

public sealed class ModelCatalogSnapshot
{
    public DateTimeOffset FetchedAt { get; set; }
    public bool FromUpstream { get; set; }
    public string? Error { get; set; }
    public List<ModelCatalogEntry> Models { get; set; } = [];
}

/// <summary>
/// 上游模型目录（/api/v2/model/list）。
///
/// 逆向自客户端 SDK 的 <c>listModelsFromRemote</c>：
/// <code>
/// GET /api/v2/model/list?Encode=1     （另有 outerProviders 变体，本代理用不到）
/// endpointType: infer  → 走与普通推理相同的 COSY 签名
/// </code>
/// 客户端以 **2 分钟**（常量 eec = 12e4）为周期同步；本服务周期可配，默认对齐。
///
/// 用途：拿到 models.xml 里没有的实时信息——计费倍率（price_factor）、
/// 是否限时免费、以及"低峰折扣进行中"（promotion）。有了倍率就能在选号时
/// 优先挑便宜的模型，或在前端提示当前处于折扣时段。
/// </summary>
public sealed class QoderModelCatalog
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<QoderModelCatalog> _log;
    private readonly Lock _lock = new();

    private ModelCatalogSnapshot _last = new() { FetchedAt = DateTimeOffset.MinValue };

    public QoderModelCatalog(IHttpClientFactory httpFactory, ILogger<QoderModelCatalog> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    /// <summary>上次成功抓取的快照（供 UI 与 /api/models 展示）。</summary>
    public ModelCatalogSnapshot Last
    {
        get
        {
            lock (_lock)
            {
                return _last;
            }
        }
    }

    /// <summary>
    /// 拉取上游模型目录。取不到账号或请求失败时返回带 Error 的快照，不抛异常——
    /// 目录只是增强信息，缺失不应影响代理主流程。
    /// </summary>
    public async Task<ModelCatalogSnapshot> RefreshAsync(CosyCreds creds, CancellationToken ct = default)
    {
        string url = QoderConstants.ModelListURL;
        try
        {
            var headers = CosySigner.BuildCosyHeaders([], url, creds);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var (k, v) in headers)
            {
                req.Headers.TryAddWithoutValidation(k, v);
            }
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            var http = _httpFactory.CreateClient(QoderHttp.ClientName);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                return Fail($"上游返回 HTTP {(int)resp.StatusCode}：{Trim(errBody)}");
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            var models = ParseModelList(body);
            if (models.Count == 0)
            {
                return Fail($"响应里没有解析出任何模型：{Trim(body)}");
            }

            var snap = new ModelCatalogSnapshot
            {
                FetchedAt = DateTimeOffset.UtcNow,
                FromUpstream = true,
                Models = models,
            };
            lock (_lock)
            {
                _last = snap;
            }
            _log.LogInformation("已同步上游模型目录 {Count} 个模型", models.Count);
            return snap;
        }
        catch (OperationCanceledException)
        {
            return Fail("请求被取消");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private ModelCatalogSnapshot Fail(string error)
    {
        _log.LogWarning("同步上游模型目录失败：{Error}", error);
        var snap = new ModelCatalogSnapshot
        {
            FetchedAt = DateTimeOffset.UtcNow,
            FromUpstream = false,
            Error = error,
        };
        lock (_lock)
        {
            _last = snap;
        }
        return snap;
    }

    /// <summary>
    /// 解析上游响应。顶层是**按场景分组**的字典（chat / assistant / inline / quest / ...），
    /// 我们只用 chat——对应普通对话。实测（2026-09-24）chat 组有 15 个模型。
    /// </summary>
    public static List<ModelCatalogEntry> ParseModelList(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return [];
            }
            if (!root.TryGetProperty("chat", out var chat) || chat.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            return chat.EnumerateArray().Select(ParseEntry).Where(e => e.Key.Length > 0).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ModelCatalogEntry ParseEntry(JsonElement e)
    {
        string key = FirstString(e, "key", "model_key", "modelKey", "model", "id", "name") ?? "";

        var entry = new ModelCatalogEntry
        {
            Key = key,
            DisplayName = FirstString(e, "display_name", "displayName", "label", "title", "name"),
            PriceFactor = FirstDouble(e, "price_factor", "priceFactor", "factor", "rate", "multiplier"),
            OriginalPriceFactor = FirstDouble(e, "original_price_factor", "originalPriceFactor", "orig_price_factor"),
            IsFree = FirstBool(e, "is_free", "isFree", "free"),
            IsDefault = FirstBool(e, "is_default", "isDefault", "default"),
            IsNew = FirstBool(e, "is_new", "isNew"),
            Enabled = FirstBool(e, "enable", "enabled"),
            Tags = ReadTags(e),
        };

        // promotion（实测结构）：{active,badge:{zh,en},description,window_start,window_end,
        // discount_factor,before_promotion_price_factor,rule_id,...}
        // 只有 active=true 才算"折扣进行中"。
        if (e.TryGetProperty("promotion", out var prom) && prom.ValueKind == JsonValueKind.Object)
        {
            bool active = FirstBool(prom, "active") ?? true;
            if (active)
            {
                entry.PromotionLabel = ReadLocalized(prom, "badge");
                entry.PromotionDescription = ReadLocalized(prom, "description");
                entry.PromotionWindow = ReadWindow(prom);
                entry.DiscountFactor = FirstDouble(prom, "discount_factor");
                entry.BeforePromotionPriceFactor = FirstDouble(prom, "before_promotion_price_factor");
            }
        }

        return entry;
    }

    private static List<string> ReadTags(JsonElement e)
    {
        if (!e.TryGetProperty("tags", out var t) || t.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return t.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!)
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static string? FirstString(JsonElement e, params string[] names)
    {
        foreach (var n in names)
        {
            if (!e.TryGetProperty(n, out var v))
            {
                continue;
            }
            if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
            {
                return s;
            }
        }
        return null;
    }

    private static double? FirstDouble(JsonElement e, params string[] names)
    {
        foreach (var n in names)
        {
            if (!e.TryGetProperty(n, out var v))
            {
                continue;
            }
            if (v.ValueKind == JsonValueKind.Number)
            {
                return v.GetDouble();
            }
        }
        return null;
    }

    private static bool? FirstBool(JsonElement e, params string[] names)
    {
        foreach (var n in names)
        {
            if (!e.TryGetProperty(n, out var v))
            {
                continue;
            }
            if (v.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return v.GetBoolean();
            }
        }
        return null;
    }

    private static long? FirstLong(JsonElement e, params string[] names)
    {
        foreach (var n in names)
        {
            if (!e.TryGetProperty(n, out var v))
            {
                continue;
            }
            if (v.ValueKind == JsonValueKind.Number)
            {
                return v.TryGetInt64(out var l) ? l : (long)v.GetDouble();
            }
        }
        return null;
    }

    /// <summary>取 {zh,en} 形态的本地化文本，优先中文。</summary>
    private static string? ReadLocalized(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
        {
            return null;
        }
        if (v.ValueKind == JsonValueKind.String)
        {
            return v.GetString();
        }
        if (v.ValueKind == JsonValueKind.Object)
        {
            return FirstString(v, "zh", "zh-CN", "zh_CN", "cn") ?? FirstString(v, "en", "en-US");
        }
        return null;
    }

    /// <summary>折扣时段，如 "22:00-08:00"。</summary>
    private static string? ReadWindow(JsonElement prom)
    {
        string? start = FirstString(prom, "window_start");
        string? end = FirstString(prom, "window_end");
        if (start is null && end is null)
        {
            return null;
        }
        return $"{start ?? "?"}-{end ?? "?"}";
    }

    private static string Trim(string s)
    {
        s = s.Replace("\n", " ").Replace("\r", "");
        return s.Length <= 200 ? s : s[..200];
    }
}
