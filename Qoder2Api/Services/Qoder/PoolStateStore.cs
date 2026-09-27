using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qoder2Api.Services.Qoder;

public sealed class ModelCooldownRecord
{
    public DateTime? Until { get; set; }

    public DateTime? ResetAt { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";
}

public sealed class PoolStateRecord
{
    public string AccountId { get; set; } = "";

    public bool Disabled { get; set; }
    public string? DisabledReason { get; set; }

    public bool NeedsRelogin { get; set; }
    public string? NeedsReloginReason { get; set; }

    public DateTime? CoolUntil { get; set; }
    public int CoolKind { get; set; }
    public string? CoolReason { get; set; }

    public DateTime? BreakerUntil { get; set; }
    public int BreakerFails { get; set; }
    public int BreakerRetryCount { get; set; }

    public DateTime? DegradeUntil { get; set; }
    public int ConsecutiveFails { get; set; }
    public int SoftStreak { get; set; }
    public int SessionDeadFails { get; set; }

    public long SuccessCount { get; set; }
    public long ErrTotal { get; set; }
    public double SuccessEma { get; set; } = 0.5;

    public DateTime? LastSuccessAt { get; set; }
    public DateTime? LastErrorAt { get; set; }

    public string? ModelCooldownsJson { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class PoolStateStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static PoolStateRecord ToRecord(string accountId, PoolEntry e, Time time)
    {
        var now = time.NowLocal;

        // 只保留未过期的模型级冷却（过期的写进去也是噪音，恢复时还得再滤一遍）。
        Dictionary<string, ModelCooldownRecord>? models = null;
        foreach (var (model, mc) in e.ModelCooldowns)
        {
            if (now >= mc.Until)
            {
                continue;
            }
            models ??= new Dictionary<string, ModelCooldownRecord>(StringComparer.Ordinal);
            models[model] = new ModelCooldownRecord
            {
                Until = mc.Until,
                ResetAt = mc.ResetAt == default ? null : mc.ResetAt,
                Reason = mc.Reason,
            };
        }

        return new PoolStateRecord
        {
            AccountId = accountId,
            Disabled = e.Disabled,
            DisabledReason = e.DisabledReason,
            NeedsRelogin = e.NeedsRelogin,
            NeedsReloginReason = e.NeedsReloginReason,
            // 已过期的截止时间不写（惰性过滤），避免重启后凭空复活一段冷却。
            CoolUntil = Live(e.CoolUntil, now),
            CoolKind = (int)e.CoolKind,
            CoolReason = e.CoolReason,
            BreakerUntil = Live(e.BreakerUntil, now),
            BreakerFails = e.BreakerFails,
            BreakerRetryCount = e.BreakerRetryCount,
            DegradeUntil = Live(e.DegradeUntil, now),
            ConsecutiveFails = e.ConsecutiveFails,
            SoftStreak = e.SoftStreak,
            SessionDeadFails = e.SessionDeadFails,
            SuccessCount = e.SuccessCount,
            ErrTotal = e.ErrTotal,
            SuccessEma = e.SuccessEma,
            LastSuccessAt = e.LastSuccessAt,
            LastErrorAt = e.LastErrorAt,
            ModelCooldownsJson = models is null ? null : JsonSerializer.Serialize(models, JsonOpts),
            UpdatedAt = now,
        };
    }

    public static void ApplyToEntry(PoolEntry e, PoolStateRecord s, Time time, ILogger? log = null)
    {
        var now = time.NowLocal;
        e.Disabled = s.Disabled;
        e.DisabledReason = s.DisabledReason;
        e.NeedsRelogin = s.NeedsRelogin;
        e.NeedsReloginReason = s.NeedsReloginReason;

        e.CoolUntil = Live(s.CoolUntil, now);
        e.CoolKind = (CoolKind)s.CoolKind;
        e.CoolReason = e.CoolUntil is null ? null : s.CoolReason;

        e.BreakerUntil = Live(s.BreakerUntil, now);
        e.BreakerFails = s.BreakerFails;
        // 熔断已过期 → 退避指数归零（否则"越熔越长"会永久累积）。
        e.BreakerRetryCount = e.BreakerUntil is null ? 0 : s.BreakerRetryCount;

        e.DegradeUntil = Live(s.DegradeUntil, now);
        e.ConsecutiveFails = s.ConsecutiveFails;
        e.SoftStreak = s.SoftStreak;
        e.SessionDeadFails = s.SessionDeadFails;

        e.SuccessCount = s.SuccessCount;
        e.ErrTotal = s.ErrTotal;
        e.SuccessEma = s.SuccessEma is >= 0 and <= 1 ? s.SuccessEma : 0.5;

        e.LastSuccessAt = s.LastSuccessAt;
        e.LastErrorAt = s.LastErrorAt;

        if (!string.IsNullOrWhiteSpace(s.ModelCooldownsJson))
        {
            try
            {
                var models = JsonSerializer.Deserialize<Dictionary<string, ModelCooldownRecord>>(s.ModelCooldownsJson, JsonOpts);
                if (models is not null)
                {
                    foreach (var (model, mc) in models)
                    {
                        var until = mc.Until;
                        if (until is null || until <= now)
                        {
                            continue; // 已过期：不恢复
                        }
                        e.ModelCooldowns[model] = new ModelCooldown
                        {
                            Until = until.Value,
                            ResetAt = mc.ResetAt ?? default,
                            Reason = mc.Reason,
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                log?.LogWarning(ex, "模型级冷却反序列化失败，该账号的模型冷却将被忽略");
            }
        }
    }

    private static DateTime? Live(DateTime? v, DateTime now) =>
        v is { } x && x > now ? x : null;
}
