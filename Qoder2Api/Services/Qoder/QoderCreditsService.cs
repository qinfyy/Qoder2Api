using System.Net.Http.Headers;
using System.Text.Json;
using Qoder2Api.Models;

namespace Qoder2Api.Services.Qoder;

/// <summary>
/// 单个活动（campaigns 数组的一项）。逆向自客户端日志里 /sash/api/v1/me/campaigns 的 payload。
/// </summary>
public sealed class QoderCampaign
{
    public string CampaignId { get; set; } = "";
    public string CampaignKey { get; set; } = "";

    /// <summary>CLAIM_BENEFIT / VIEW_DETAILS。</summary>
    public string ActionType { get; set; } = "";

    /// <summary>CLAIMABLE / CLAIMED / ...</summary>
    public string ClaimStatus { get; set; } = "";

    /// <summary>福利类型，如 CREDITS。</summary>
    public string? BenefitKind { get; set; }

    /// <summary>福利数量，如 100（Credits）。</summary>
    public double? BenefitAmount { get; set; }

    /// <summary>活动起止（Unix 秒）。</summary>
    public long? StartAt { get; set; }
    public long? EndAt { get; set; }

    /// <summary>是否为「领取福利」类活动（不管领没领）。UI 靠它决定显不显示按钮。</summary>
    public bool IsBenefitClaim => ActionType.Equals("CLAIM_BENEFIT", StringComparison.OrdinalIgnoreCase);

    public bool IsClaimable =>
        IsBenefitClaim && ClaimStatus.Equals("CLAIMABLE", StringComparison.OrdinalIgnoreCase);

    public bool IsClaimed => ClaimStatus.Equals("CLAIMED", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 账号 Credits 快照。
///
/// 数据来源是 openapi.qoder.sh 的四个接口，逆向自官方客户端（out/main/index.js）：
/// - <c>/sash/api/v2/me/usage</c>                      余额（套餐内 / 资源包）
/// - <c>/sash/api/v1/ai-conversations/credits-summary</c> 近一年消耗、日峰值
/// - <c>/sash/api/v1/ai-conversations/seat-activity</c>   连续 / 最长 / 累计活跃天数
/// - <c>/sash/api/v1/me/campaigns</c>                     每日福利活动是否可领取
///
/// 这四个接口与推理接口不同：**不需要 COSY 签名**，只要 <c>Authorization: Bearer &lt;jobToken&gt;</c>
/// （客户端 Ky() 的构造）。所以直接复用账号已有的 JobToken 即可。
/// </summary>
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

/// <summary>
/// 拉取并缓存账号 Credits 信息。缓存是为了账号列表页——一屏十几个账号，
/// 每次渲染都打四个接口会很慢，而且这些数据（活跃天数、近一年消耗）变化很慢。
/// </summary>
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

    /// <summary>取缓存（可能过期），供 UI 先渲染、再后台刷新。</summary>
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

    /// <summary>
    /// 取快照。缓存新鲜且未强制刷新时直接返回缓存；同一账号的并发请求会合并成一次。
    /// </summary>
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

    /// <summary>批量拉取，用于账号列表页。单个失败不影响其余。</summary>
    public async Task<Dictionary<string, QoderCreditsSnapshot>> GetAllAsync(
        IEnumerable<string> accountIds, bool force = false, CancellationToken ct = default)
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

    /// <summary>
    /// 领取活动福利（「每天领 100 Credits」那个按钮）。
    ///
    /// 逆向自活动页 activity-iframe.js：<c>POST /sash/api/v1/me/campaigns/{campaignId}/claim</c>，
    /// **没有请求体**。官方客户端靠 Electron 的 onBeforeSendHeaders 给 iframe 的 XHR 自动注入
    /// 认证头，这里显式带上同样的头，所以不需要那个 iframe。
    ///
    /// 领取成功后强制重拉快照，让调用方立刻看到新余额和 claimStatus。
    /// </summary>
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
            using var req = new HttpRequestMessage(HttpMethod.Post, QoderConstants.OpenApiBaseUrl + path);
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
                return new QoderClaimResult(false, $"HTTP {(int)resp.StatusCode}：{Truncate(body, 200)}", null);
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
            var usage = TryGetJsonAsync("/sash/api/v2/me/usage", token, ct);
            var summary = TryGetJsonAsync("/sash/api/v1/ai-conversations/credits-summary", token, ct);
            var activity = TryGetJsonAsync("/sash/api/v1/ai-conversations/seat-activity", token, ct);
            var campaign = TryGetJsonAsync("/sash/api/v1/me/campaigns", token, ct);
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

    /// <summary>GET 并解析 JSON。失败返回 null，不抛——调用方按「该数据源不可用」处理。</summary>
    private async Task<JsonDocument?> TryGetJsonAsync(string path, string token, CancellationToken ct)
    {
        try
        {
            var http = _httpFactory.CreateClient(QoderHttp.ClientName);
            using var req = new HttpRequestMessage(HttpMethod.Get, QoderConstants.OpenApiBaseUrl + path);
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

    /// <summary>
    /// 官方客户端 Ky() 的请求头：openapi.qoder.sh 上的接口只要 Bearer token，
    /// 不像 api3.qoder.sh 的推理接口需要整套 COSY 签名。
    /// </summary>
    private static void ApplyAuthHeaders(HttpRequestMessage req, string token)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.TryAddWithoutValidation("Cosy-ClientType", QoderConstants.ClientType);
        req.Headers.TryAddWithoutValidation("User-Agent", "Qoder");
    }

    // ---------------------------------------------------------------- 解析 ----

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
        if (doc is null) { failures.Add("seat-activity"); return; }
        // 注意：上游字段名与 UI 用的名字不同，客户端 nKe() 做了重命名，这里按**原始字段**读。
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
                    camp.BenefitKind = GetString(b, "kind");
                    camp.BenefitAmount = GetDouble(b, "amount");
                }
                if (camp.CampaignId.Length > 0)
                {
                    snap.Campaigns.Add(camp);
                }
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

/// <summary>一次领取的结果。成功时带上刷新过的快照，调用方不必再拉一次。</summary>
public sealed record QoderClaimResult(bool Success, string? Error, QoderCreditsSnapshot? Snapshot);
