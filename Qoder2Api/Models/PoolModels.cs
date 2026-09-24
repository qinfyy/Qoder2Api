using System.Text.Json.Serialization;

namespace Qoder2Api.Models;

public enum PoolAccountState
{
    /// <summary>健康可用。</summary>
    Ready = 0,

    /// <summary>冷却中（限流/余额/404 等，到期自动恢复）。</summary>
    Cooling,

    /// <summary>熔断中（连续 5xx，指数退避）。</summary>
    Breaker,

    /// <summary>降权中（未知错误连败，临时出池）。</summary>
    Degraded,

    /// <summary>需要重新登录（凭证失效且无法自动续期）。</summary>
    NeedsRelogin,

    /// <summary>已停用（管理员手动停用，或池因连续会话失效自动禁用）。</summary>
    Disabled,
}

public sealed class PoolAccountStatus
{
    public string AccountId { get; set; } = "";

    [JsonPropertyName("user_name")]
    public string? UserName { get; set; }

    [JsonPropertyName("user_email")]
    public string? UserEmail { get; set; }

    [JsonPropertyName("plan_name")]
    public string? PlanName { get; set; }

    [JsonPropertyName("auth_method")]
    public string? AuthMethod { get; set; }

    /// <summary>池状态。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<PoolAccountState>))]
    public PoolAccountState State { get; set; }

    /// <summary>状态原因（运维可读，如 "429 rate limit" / "12153 session dead"）。</summary>
    public string? Reason { get; set; }

    /// <summary>是否由**管理员**停用（accounts.Status != active）。</summary>
    [JsonPropertyName("admin_disabled")]
    public bool AdminDisabled { get; set; }

    /// <summary>是否由**池自动**禁用（连续会话失效等）。</summary>
    [JsonPropertyName("auto_disabled")]
    public bool AutoDisabled { get; set; }

    /// <summary>当前状态剩余秒数（0 = 无时限或已到期）。</summary>
    [JsonPropertyName("remaining_sec")]
    public long RemainingSec { get; set; }

    /// <summary>状态截止时刻（Unix 毫秒，null = 无时限）。</summary>
    [JsonPropertyName("until_ms")]
    public long? UntilMs { get; set; }

    /// <summary>连续失败计数（连败降权进度）。</summary>
    [JsonPropertyName("consecutive_fails")]
    public int ConsecutiveFails { get; set; }

    /// <summary>累计成功次数（终身，仅展示）。</summary>
    [JsonPropertyName("success_count")]
    public long SuccessCount { get; set; }

    /// <summary>累计错误次数（终身，仅展示）。</summary>
    [JsonPropertyName("err_total")]
    public long ErrTotal { get; set; }

    /// <summary>
    /// 近期成功率（EMA 平滑，0..1）。**权重用它而不是终身成功率**——终身累计
    /// 只增不减，会让早期出过错的账号被永久压权且永不恢复。
    /// </summary>
    [JsonPropertyName("success_rate")]
    public double SuccessRate { get; set; }

    /// <summary>当前在途请求数。</summary>
    [JsonPropertyName("in_flight")]
    public int InFlight { get; set; }

    /// <summary>在途上限（0 = 不限）。</summary>
    [JsonPropertyName("in_flight_limit")]
    public int InFlightLimit { get; set; }

    /// <summary>熔断器连续失败计数（达到阈值触发熔断）。</summary>
    [JsonPropertyName("breaker_fails")]
    public int BreakerFails { get; set; }

    /// <summary>当前仍在限额的模型（模型级独立冷却未到期者）。</summary>
    [JsonPropertyName("rate_limited_models")]
    public List<string> RateLimitedModels { get; set; } = [];

    /// <summary>是否为首选账号（仅影响选号权重加成，不再是唯一路由依据）。</summary>
    [JsonPropertyName("is_preferred")]
    public bool IsPreferred { get; set; }

    /// <summary>最近一次被选中使用的时刻。</summary>
    [JsonPropertyName("last_used_ms")]
    public long? LastUsedMs { get; set; }
}

/// <summary>号池整体快照（供 UI 顶部徽章与 /api/pool/status 用）。</summary>
public sealed class PoolSnapshot
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("ready")]
    public int Ready { get; set; }

    [JsonPropertyName("cooling")]
    public int Cooling { get; set; }

    [JsonPropertyName("breaker")]
    public int Breaker { get; set; }

    [JsonPropertyName("degraded")]
    public int Degraded { get; set; }

    [JsonPropertyName("needs_relogin")]
    public int NeedsRelogin { get; set; }

    [JsonPropertyName("disabled")]
    public int Disabled { get; set; }

    /// <summary>池当前是否可服务（存在至少一个 Ready 账号）。</summary>
    [JsonPropertyName("servable")]
    public bool Servable { get; set; }

    [JsonPropertyName("accounts")]
    public List<PoolAccountStatus> Accounts { get; set; } = [];
}
