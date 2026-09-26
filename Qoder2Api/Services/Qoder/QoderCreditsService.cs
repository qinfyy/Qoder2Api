using System.Net.Http.Headers;
using System.Text.Json;
using Qoder2Api.Models;

namespace Qoder2Api.Services.Qoder;

public sealed class QoderCampaign
{
    public string CampaignId { get; set; } = "";
    public string CampaignKey { get; set; } = "";

    /// <summary>CLAIM_BENEFIT（领福利）/ VIEW_DETAILS（纯展示，点一下算已读）。</summary>
    public string ActionType { get; set; } = "";

    /// <summary>CLAIMABLE / CLAIMED / ...</summary>
    public string ClaimStatus { get; set; } = "";

    /// <summary>福利类型，如 CREDITS。</summary>
    public string? BenefitKind { get; set; }

    /// <summary>福利数量，如 100（Credits）。</summary>
    public double? BenefitAmount { get; set; }

    /// <summary>限定的模型系列，如 ALL_MODELS / QWEN_SERIES。null = 不限（上游没给）。</summary>
    public string? ModelSeries { get; set; }

    /// <summary>
    /// 有效期模式：RELATIVE_DAYS（领取后 N 天）/ FIXED_END（固定到期日）。
    /// 「领取后 30 天有效」这句话里的 30 就来自
    /// <c>benefit.validity = {mode:"RELATIVE_DAYS", days:30}</c>，不是写死的文案。
    /// </summary>
    public string? ValidityMode { get; set; }
    public int? ValidityDays { get; set; }

    /// <summary>FIXED_END 模式下的到期时刻（UTC）。</summary>
    public DateTimeOffset? FixedEnd { get; set; }

    /// <summary>活动起止（Unix 秒）。「每天领」是 [当天 10:00, 次日 09:59] UTC+8。</summary>
    public long? StartAt { get; set; }
    public long? EndAt { get; set; }

    // --- placements[POPUP].content 的 i18n（zh / en 各一套，上游总是两套都给）---
    public string? TitleZh { get; set; }
    public string? TitleEn { get; set; }
    public string? DescriptionZh { get; set; }
    public string? DescriptionEn { get; set; }
    public string? ButtonTextZh { get; set; }
    public string? ButtonTextEn { get; set; }
    public string? DetailUrlZh { get; set; }
    public string? DetailUrlEn { get; set; }

    /// <summary>是否为「领取福利」类活动。UI 靠它决定显不显示领取按钮。</summary>
    public bool IsBenefitClaim => ActionType.Equals("CLAIM_BENEFIT", StringComparison.OrdinalIgnoreCase);

    public bool IsViewDetails => ActionType.Equals("VIEW_DETAILS", StringComparison.OrdinalIgnoreCase);

    public CampaignState State =>
        IsViewDetails || ClaimStatus.Equals("CLAIMABLE", StringComparison.OrdinalIgnoreCase)
            ? CampaignState.Claimable
            : ClaimStatus.Equals("CLAIMED", StringComparison.OrdinalIgnoreCase)
                ? CampaignState.Claimed
                : CampaignState.Ineligible;

    public bool IsClaimable => State == CampaignState.Claimable;
    public bool IsClaimed => State == CampaignState.Claimed;

    public DateTimeOffset? StartAtUtc =>
        StartAt is { } s ? DateTimeOffset.FromUnixTimeSeconds(s) : null;

    public DateTimeOffset? EndAtUtc =>
        EndAt is { } e ? DateTimeOffset.FromUnixTimeSeconds(e) : null;

    /// <summary>活动窗口是否已过。过期后按钮置灰——不然点了必然被上游 409 打回。</summary>
    public bool IsExpired(DateTimeOffset now) => EndAtUtc is { } end && now >= end;

    public long? CountdownMs(DateTimeOffset now)
    {
        if (EndAtUtc is not { } end) return null;
        long ms = (long)(end - now).TotalMilliseconds;
        return ms > 0 && ms <= 24 * 3600 * 1000 ? ms : null;
    }

    /// <summary>倒计时文案 HH:MM:SS（不足 1 小时则 00:MM:SS），与活动页 Ge() 一致。</summary>
    public static string FormatCountdown(long ms)
    {
        long s = Math.Max(0, ms / 1000);
        return $"{s / 3600:00}:{s % 3600 / 60:00}:{s % 60:00}";
    }

    public string Title()
    {
        if (!string.IsNullOrWhiteSpace(TitleZh)) return TitleZh;
        if (!string.IsNullOrWhiteSpace(TitleEn)) return TitleEn;
        return CampaignKey;
    }

    public string Description() =>
        !string.IsNullOrWhiteSpace(DescriptionZh) ? DescriptionZh : DescriptionEn ?? "";

    /// <summary>按钮文案。上游的 buttonText 常常是空串，此时由 UI 兜底。</summary>
    public string? ButtonText() =>
        !string.IsNullOrWhiteSpace(ButtonTextZh) ? ButtonTextZh
        : !string.IsNullOrWhiteSpace(ButtonTextEn) ? ButtonTextEn
        : null;

    public string? DetailUrl() =>
        !string.IsNullOrWhiteSpace(DetailUrlZh) ? DetailUrlZh : DetailUrlEn;

    /// <summary>「领取后 30 天有效」里的天数，给 UI 拼提示用。null = 上游没给有效期规则。</summary>
    public string? ValidityHint() => ValidityMode switch
    {
        "RELATIVE_DAYS" when ValidityDays is { } d => $"领取后 {d} 天有效",
        "FIXED_END" when FixedEnd is { } f => $"{f.ToLocalTime():yyyy-MM-dd HH:mm} 到期",
        _ => null,
    };
}

/// <summary>活动展示态。取值规则见 <see cref="QoderCampaign.State"/>。</summary>
public enum CampaignState
{
    Claimable,
    Claimed,
    Ineligible,
}


public sealed class QoderCreditsSnapshot
{
    public string AccountId { get; set; } = "";
    public DateTimeOffset FetchedAt { get; set; }
    public string? Error { get; set; }

    // --- /sash/api/v2/me/usage ---
    /// <summary>qoder / enterprise。enterprise 下没有 userQuota，额度在组织侧。</summary>
    public string? DisplayMode { get; set; }
    public string? UserType { get; set; }
    /// <summary>套餐内 Credits 已用。</summary>
    public double? PlanUsed { get; set; }
    /// <summary>套餐内 Credits 总量。</summary>
    public double? PlanTotal { get; set; }
    /// <summary>资源包（Add-on）已用。</summary>
    public double? AddOnUsed { get; set; }
    /// <summary>资源包总量。</summary>
    public double? AddOnTotal { get; set; }

    // --- credits-summary ---
    /// <summary>近一年 Credits 消耗。</summary>
    public double? TotalCredits { get; set; }
    /// <summary>近一年 Credits 日峰值。</summary>
    public double? PeakCredits { get; set; }

    // --- seat-activity ---
    public int? CurrentStreakDays { get; set; }
    public int? LongestStreakDays { get; set; }
    public int? TotalActiveDays { get; set; }

    // --- campaigns（每日福利）---
    /// <summary>是否有可领取的福利（上游给的聚合标志）。</summary>
    public bool CampaignClaimable { get; set; }
    /// <summary>活动页地址（growth-page/activity-iframe），仅在可领取时下发。</summary>
    public string? CampaignUrl { get; set; }
    /// <summary>活动明细。领取走 POST /sash/api/v1/me/campaigns/{campaignId}/claim。</summary>
    public List<QoderCampaign> Campaigns { get; set; } = [];

    /// <summary>套餐内剩余；总量缺失时返回 null。</summary>
    public double? PlanRemaining =>
        PlanTotal is { } t && PlanUsed is { } u ? Math.Max(0, t - u) : null;

    public double? AddOnRemaining =>
        AddOnTotal is { } t && AddOnUsed is { } u ? Math.Max(0, t - u) : null;
}

public sealed class QoderCreditsService
{
    /// <summary>缓存存活时长。活跃天数/年消耗按天变，5 分钟足够新鲜。</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly IHttpClientFactory _httpFactory;
    private readonly QoderAuthService _auth;
    private readonly ILogger<QoderCreditsService> _log;
    private readonly Lock _lock = new();

    private readonly Dictionary<string, QoderCreditsSnapshot> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<QoderCreditsSnapshot>> _inflight = new(StringComparer.Ordinal);

    public QoderCreditsService(IHttpClientFactory httpFactory, QoderAuthService auth, ILogger<QoderCreditsService> log)
    {
        _httpFactory = httpFactory;
        _auth = auth;
        _log = log;
    }

    public QoderCreditsSnapshot? Peek(string accountId)
    {
        lock (_lock)
        {
            return _cache.TryGetValue(accountId, out var s) ? s : null;
        }
    }

    public Dictionary<string, QoderCreditsSnapshot> PeekAll()
    {
        lock (_lock)
        {
            return new Dictionary<string, QoderCreditsSnapshot>(_cache, StringComparer.Ordinal);
        }
    }

    public Task<QoderCreditsSnapshot> GetAsync(string accountId, bool force = false, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!force && _cache.TryGetValue(accountId, out var cached)
                && DateTimeOffset.UtcNow - cached.FetchedAt < CacheTtl)
            {
                return Task.FromResult(cached);
            }
            if (_inflight.TryGetValue(accountId, out var running))
            {
                return running;
            }
            var task = FetchAsync(accountId, ct);
            _inflight[accountId] = task;
            return task;
        }
    }

    public async Task<Dictionary<string, QoderCreditsSnapshot>> GetAllAsync(IEnumerable<string> accountIds, bool force = false, CancellationToken ct = default)
    {
        var ids = accountIds.Distinct(StringComparer.Ordinal).ToList();
        var tasks = ids.Select(id => GetAsync(id, force, ct)).ToList();
        var results = await Task.WhenAll(tasks);
        return ids.Zip(results).ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal);
    }

    /// <summary>账号被删除时清掉缓存，避免同 id 复用时显示上一个账号的数据。</summary>
    public void Forget(string accountId)
    {
        lock (_lock)
        {
            _cache.Remove(accountId);
            _inflight.Remove(accountId);
        }
    }

    public async Task<QoderClaimResult> ClaimAsync(string accountId, string campaignId, CancellationToken ct = default)
    {
        var acc = _auth.Database.GetAccountById(accountId);
        if (acc is null)
        {
            return new QoderClaimResult(false, "账号不存在", null);
        }

        string? token = acc.JobToken;
        if (string.IsNullOrEmpty(token)) token = acc.DeviceToken;
        if (string.IsNullOrEmpty(token))
        {
            return new QoderClaimResult(false, "账号无有效凭证", null);
        }

        try
        {
            var http = _httpFactory.CreateClient(QoderHttp.ClientName);
            string path = $"/sash/api/v1/me/campaigns/{Uri.EscapeDataString(campaignId)}/claim";
            // openapi 域按账号区域取（国际版 openapi.qoder.sh / 国内版 openapi.qoder.com.cn）。
            using var req = new HttpRequestMessage(HttpMethod.Post, QoderEndpoints.ForRaw(acc.Region).OpenApiUrl(path));
            ApplyAuthHeaders(req, token);
            // 不设 Content-Type：这个接口没有请求体（活动页也是裸 POST + keepalive）。
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(RequestTimeout);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);

            string body = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("领取福利失败 account={Account} campaign={Campaign} HTTP {Status}: {Body}",
                    Short(accountId), campaignId, (int)resp.StatusCode, Truncate(body, 200));
                return new QoderClaimResult(false, DescribeClaimFailure((int)resp.StatusCode, body), null);
            }

            // 2xx 但没拿到 CLAIMED：上游把「今天已经领过了」之类的情形也归到 2xx，
            // 不校验就会把没发钱的领取报成成功。
            if (!ClaimsConfirmed(body))
            {
                _log.LogWarning("领取返回 2xx 但未见 CLAIMED account={Account} campaign={Campaign}: {Body}",
                    Short(accountId), campaignId, Truncate(body, 200));
                return new QoderClaimResult(false, "领取失败，请稍后重试（上游未确认到账）", null);
            }

            _log.LogInformation("已领取福利 account={Account} campaign={Campaign}", Short(accountId), campaignId);
            var snap = await GetAsync(accountId, force: true, ct);
            return new QoderClaimResult(true, null, snap);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "领取福利异常 account={Account} campaign={Campaign}", Short(accountId), campaignId);
            return new QoderClaimResult(false, ex.Message, null);
        }
    }

    /// <summary>响应体里带 status:"CLAIMED"（顶层或 data 下）才算领成功。</summary>
    private static bool ClaimsConfirmed(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                root = data;
            }
            return root.ValueKind == JsonValueKind.Object
                && GetString(root, "status")?.Equals("CLAIMED", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch
        {
            return false;
        }
    }

    private static string DescribeClaimFailure(int status, string body)
    {
        string? code = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            code = GetString(doc.RootElement, "errorCode");
        }
        catch { /* 错误体不是 JSON，按状态码兜底 */ }

        string message = status switch
        {
            401 or 403 => "登录状态不可用，请关闭后重试",
            409 => "活动已结束或当前账号不符合领取条件",
            429 or 503 => "操作过于频繁，请稍后重试",
            404 => "活动不存在或已结束",
            _ => "领取失败，请稍后重试",
        };
        return code is null ? $"{message}（HTTP {status}）" : $"{message}（{code}）";
    }


    private async Task<QoderCreditsSnapshot> FetchAsync(string accountId, CancellationToken ct)
    {
        var snap = new QoderCreditsSnapshot { AccountId = accountId, FetchedAt = DateTimeOffset.UtcNow };
        try
        {
            var acc = _auth.Database.GetAccountById(accountId);
            if (acc is null)
            {
                snap.Error = "账号不存在";
                return snap;
            }

            string? token = acc.JobToken;
            if (string.IsNullOrEmpty(token)) token = acc.DeviceToken;
            if (string.IsNullOrEmpty(token))
            {
                snap.Error = "账号无有效凭证";
                return snap;
            }

            // 四个接口互不依赖，并发拉；任一失败只记在自己的字段上（客户端也是 allSettled 语义）。
            var endpoints = QoderEndpoints.ForRaw(acc.Region);
            var usage = TryGetJsonAsync(endpoints, "/sash/api/v2/me/usage", token, ct);
            var summary = TryGetJsonAsync(endpoints, "/sash/api/v1/ai-conversations/credits-summary", token, ct);
            var activity = TryGetJsonAsync(endpoints, "/sash/api/v1/ai-conversations/seat-activity", token, ct);
            var campaign = TryGetJsonAsync(endpoints, "/sash/api/v1/me/campaigns", token, ct);
            await Task.WhenAll(usage, summary, activity, campaign);

            var failures = new List<string>();
            ParseUsage(await usage, snap, failures);
            ParseSummary(await summary, snap, failures);
            ParseActivity(await activity, snap, failures);
            ParseCampaign(await campaign, snap, failures);

            if (failures.Count == 4)
            {
                snap.Error = "全部接口均失败（凭证可能已失效）";
            }
            else if (failures.Count > 0)
            {
                snap.Error = "部分接口失败: " + string.Join(", ", failures);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            snap.Error = "请求被取消";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "拉取 Credits 失败 account={Account}", Short(accountId));
            snap.Error = ex.Message;
        }
        finally
        {
            lock (_lock)
            {
                _cache[accountId] = snap;
                _inflight.Remove(accountId);
            }
        }
        return snap;
    }

    private async Task<JsonDocument?> TryGetJsonAsync(QoderEndpoints endpoints, string path, string token, CancellationToken ct)
    {
        try
        {
            var http = _httpFactory.CreateClient(QoderHttp.ClientName);
            using var req = new HttpRequestMessage(HttpMethod.Get, endpoints.OpenApiUrl(path));
            ApplyAuthHeaders(req, token);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(RequestTimeout);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("Credits 接口 {Path} 返回 HTTP {Status}", path, (int)resp.StatusCode);
                return null;
            }
            string body = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
            return JsonDocument.Parse(body);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Credits 接口 {Path} 请求失败", path);
            return null;
        }
    }

    private static void ApplyAuthHeaders(HttpRequestMessage req, string token)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // 必须用 OpenApiClientType(10)：带 5 的话 campaigns 接口会返回空列表，
        // 「每天领 100 Credits」整张卡片都拿不到。详见 QoderConstants.OpenApiClientType。
        req.Headers.TryAddWithoutValidation("Cosy-ClientType", QoderConstants.OpenApiClientType);
        req.Headers.TryAddWithoutValidation("User-Agent", "Qoder");
    }

    private static void ParseUsage(JsonDocument? doc, QoderCreditsSnapshot snap, List<string> failures)
    {
        if (doc is null) { failures.Add("usage"); return; }
        var root = doc.RootElement;
        snap.DisplayMode = GetString(root, "displayMode");
        if (root.TryGetProperty("qoderUsage", out var qu) && qu.ValueKind == JsonValueKind.Object)
        {
            snap.UserType = GetString(qu, "userType");
            if (qu.TryGetProperty("userQuota", out var uq) && uq.ValueKind == JsonValueKind.Object)
            {
                snap.PlanUsed = GetDouble(uq, "used");
                snap.PlanTotal = GetDouble(uq, "total");
            }
            if (qu.TryGetProperty("addOnQuota", out var aq) && aq.ValueKind == JsonValueKind.Object)
            {
                snap.AddOnUsed = GetDouble(aq, "used");
                snap.AddOnTotal = GetDouble(aq, "total");
            }
        }
    }

    private static void ParseSummary(JsonDocument? doc, QoderCreditsSnapshot snap, List<string> failures)
    {
        if (doc is null) { failures.Add("credits-summary"); return; }
        snap.TotalCredits = GetDouble(doc.RootElement, "totalCredits");
        snap.PeakCredits = GetDouble(doc.RootElement, "peakCredits");
    }

    private static void ParseActivity(JsonDocument? doc, QoderCreditsSnapshot snap, List<string> failures)
    {
        if (doc is null) {
            failures.Add("seat-activity"); return;
        }
        var root = doc.RootElement;
        snap.CurrentStreakDays = GetInt(root, "currentConsecutiveDays");
        snap.LongestStreakDays = GetInt(root, "maxConsecutiveDays");
        snap.TotalActiveDays = GetInt(root, "cumulativeActiveDays");
    }

    private static void ParseCampaign(JsonDocument? doc, QoderCreditsSnapshot snap, List<string> failures)
    {
        if (doc is null) { failures.Add("campaigns"); return; }
        var root = doc.RootElement;
        snap.CampaignClaimable = root.TryGetProperty("claimable", out var c) && c.ValueKind == JsonValueKind.True;
        string? url = GetString(root, "campaignUrl");
        snap.CampaignUrl = string.IsNullOrWhiteSpace(url) ? null : url;

        if (root.TryGetProperty("campaigns", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var camp = new QoderCampaign
                {
                    CampaignId = GetString(e, "campaignId") ?? "",
                    CampaignKey = GetString(e, "campaignKey") ?? "",
                    ActionType = GetString(e, "actionType") ?? "",
                    ClaimStatus = GetString(e, "claimStatus") ?? "",
                    StartAt = GetLong(e, "startAt"),
                    EndAt = GetLong(e, "endAt"),
                };
                if (e.TryGetProperty("benefit", out var b) && b.ValueKind == JsonValueKind.Object)
                {
                    ParseBenefit(b, camp);
                }
                ParsePlacement(e, camp);
                if (camp.CampaignId.Length > 0)
                {
                    snap.Campaigns.Add(camp);
                }
            }
        }
    }

    private static void ParseBenefit(JsonElement b, QoderCampaign camp)
    {
        camp.BenefitKind = GetString(b, "kind");
        camp.BenefitAmount = GetDouble(b, "amount");

        // benefit.modelScope.modelSeries.key —— 上游测试数据里见过 ALL_MODELS / QWEN_SERIES
        if (b.TryGetProperty("modelScope", out var ms) && ms.ValueKind == JsonValueKind.Object
            && ms.TryGetProperty("modelSeries", out var series) && series.ValueKind == JsonValueKind.Object)
        {
            camp.ModelSeries = GetString(series, "key");
        }

        // benefit.validity —— 「领取后 30 天有效」的 30 在这，不是文案写死的
        if (b.TryGetProperty("validity", out var v) && v.ValueKind == JsonValueKind.Object)
        {
            camp.ValidityMode = GetString(v, "mode");
            camp.ValidityDays = GetInt(v, "days");
            if (GetString(v, "fixedEnd") is { } fixedEnd
                && DateTimeOffset.TryParse(fixedEnd, out var parsed))
            {
                camp.FixedEnd = parsed;
            }
        }
    }

    private static void ParsePlacement(JsonElement e, QoderCampaign camp)
    {
        if (!e.TryGetProperty("placements", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var want in new[] { "POPUP", "USAGE" })
        {
            foreach (var p in arr.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                if (!string.Equals(GetString(p, "type"), want, StringComparison.OrdinalIgnoreCase)) continue;
                if (!p.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object) continue;

                if (content.TryGetProperty("zh", out var zh) && zh.ValueKind == JsonValueKind.Object)
                {
                    camp.TitleZh = GetString(zh, "title");
                    camp.DescriptionZh = GetString(zh, "description");
                    camp.ButtonTextZh = GetString(zh, "buttonText");
                    camp.DetailUrlZh = GetString(zh, "detailUrl");
                }
                if (content.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.Object)
                {
                    camp.TitleEn = GetString(en, "title");
                    camp.DescriptionEn = GetString(en, "description");
                    camp.ButtonTextEn = GetString(en, "buttonText");
                    camp.DetailUrlEn = GetString(en, "detailUrl");
                }
                return;
            }
        }
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? GetDouble(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static int? GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;

    private static long? GetLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : null;

    private static string Short(string s) => s.Length > 8 ? s[..8] : s;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

public sealed record QoderClaimResult(bool Success, string? Error, QoderCreditsSnapshot? Snapshot);
