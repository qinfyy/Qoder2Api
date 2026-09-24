using System.Text.Json;
using System.Text.Json.Serialization;

namespace reg.Services.Qoder;

/// <summary>单个 (账号, 模型) 的模型级冷却持久化记录。</summary>
public sealed class ModelCooldownRecord
{
    [JsonPropertyName("until_ms")]
    public long UntilMs { get; set; }

    [JsonPropertyName("reset_at_ms")]
    public long ResetAtMs { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";
}

/// <summary>
/// 账号池状态的持久化记录（与 account_pool_states 表一一对应）。
/// 时间一律用 Unix 毫秒整数——避开 DateTimeOffset 在 SQLite 上的映射坑。
/// </summary>
public sealed class PoolStateRecord
{
    public string AccountId { get; set; } = "";

    public bool Disabled { get; set; }
    public string? DisabledReason { get; set; }

    public bool NeedsRelogin { get; set; }
    public string? NeedsReloginReason { get; set; }

    public long? CoolUntilMs { get; set; }
    public int CoolKind { get; set; }
    public string? CoolReason { get; set; }

    public long? BreakerUntilMs { get; set; }
    public int BreakerFails { get; set; }
    public int BreakerRetryCount { get; set; }

    public long? DegradeUntilMs { get; set; }
    public int ConsecutiveFails { get; set; }
    public int SoftStreak { get; set; }
    public int SessionDeadFails { get; set; }

    public long SuccessCount { get; set; }
    public long ErrTotal { get; set; }
    public double SuccessEma { get; set; } = 0.5;

    public long? LastSuccessMs { get; set; }
    public long? LastErrMs { get; set; }

    public string? ModelCooldownsJson { get; set; }
    public long UpdatedAtMs { get; set; }
}

/// <summary>
/// 池状态的内存 ↔ 持久化双向映射。
///
/// **持久化什么、不持久化什么**：
///   - 持久化：退避累积（BreakerRetryCount/SoftStreak）、冷却与熔断截止、模型级冷却、
///     连败计数、成功率 EMA。这些是"学到的知识"，重启丢失会导致重新踩坑
///     （例如软限流仍在退避中，重启后却从基数重新开始，或反复熔断只从最小退避起步）。
///   - 不持久化：InFlight / UsedSeq / LastUsedAt（纯运行态，重启后无意义）。
///
/// 落盘与恢复都做**惰性过滤**：已过期的截止时间不写、也不恢复（陈旧状态不复活）。
/// </summary>
public static class PoolStateStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static PoolStateRecord ToRecord(string accountId, PoolEntry e)
    {
        var now = DateTimeOffset.UtcNow;

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
                UntilMs = mc.Until.ToUnixTimeMilliseconds(),
                ResetAtMs = mc.ResetAt == default ? 0 : mc.ResetAt.ToUnixTimeMilliseconds(),
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
            CoolUntilMs = Live(e.CoolUntil, now),
            CoolKind = (int)e.CoolKind,
            CoolReason = e.CoolReason,
            BreakerUntilMs = Live(e.BreakerUntil, now),
            BreakerFails = e.BreakerFails,
            BreakerRetryCount = e.BreakerRetryCount,
            DegradeUntilMs = Live(e.DegradeUntil, now),
            ConsecutiveFails = e.ConsecutiveFails,
            SoftStreak = e.SoftStreak,
            SessionDeadFails = e.SessionDeadFails,
            SuccessCount = e.SuccessCount,
            ErrTotal = e.ErrTotal,
            SuccessEma = e.SuccessEma,
            LastSuccessMs = e.LastSuccessAt?.ToUnixTimeMilliseconds(),
            LastErrMs = e.LastErrorAt?.ToUnixTimeMilliseconds(),
            ModelCooldownsJson = models is null ? null : JsonSerializer.Serialize(models, JsonOpts),
            UpdatedAtMs = now.ToUnixTimeMilliseconds(),
        };
    }

    public static void ApplyToEntry(PoolEntry e, PoolStateRecord s)
    {
        var now = DateTimeOffset.UtcNow;
        e.Disabled = s.Disabled;
        e.DisabledReason = s.DisabledReason;
        e.NeedsRelogin = s.NeedsRelogin;
        e.NeedsReloginReason = s.NeedsReloginReason;

        e.CoolUntil = Revive(s.CoolUntilMs, now);
        e.CoolKind = (CoolKind)s.CoolKind;
        e.CoolReason = e.CoolUntil is null ? null : s.CoolReason;

        e.BreakerUntil = Revive(s.BreakerUntilMs, now);
        e.BreakerFails = s.BreakerFails;
        // 熔断已过期 → 退避指数归零（否则"越熔越长"会永久累积）。
        e.BreakerRetryCount = e.BreakerUntil is null ? 0 : s.BreakerRetryCount;

        e.DegradeUntil = Revive(s.DegradeUntilMs, now);
        e.ConsecutiveFails = s.ConsecutiveFails;
        e.SoftStreak = s.SoftStreak;
        e.SessionDeadFails = s.SessionDeadFails;

        e.SuccessCount = s.SuccessCount;
        e.ErrTotal = s.ErrTotal;
        e.SuccessEma = s.SuccessEma is >= 0 and <= 1 ? s.SuccessEma : 0.5;

        e.LastSuccessAt = FromMs(s.LastSuccessMs);
        e.LastErrorAt = FromMs(s.LastErrMs);

        if (!string.IsNullOrWhiteSpace(s.ModelCooldownsJson))
        {
            try
            {
                var models = JsonSerializer.Deserialize<Dictionary<string, ModelCooldownRecord>>(s.ModelCooldownsJson, JsonOpts);
                if (models is not null)
                {
                    foreach (var (model, mc) in models)
                    {
                        var until = FromMs(mc.UntilMs);
                        if (until is null || until <= now)
                        {
                            continue; // 已过期：不恢复
                        }
                        e.ModelCooldowns[model] = new ModelCooldown
                        {
                            Until = until.Value,
                            ResetAt = FromMs(mc.ResetAtMs) ?? default,
                            Reason = mc.Reason,
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PoolStateStore] 模型级冷却反序列化失败（忽略）: {ex.Message}");
            }
        }
    }

    /// <summary>仅返回仍有效的截止时刻（已过期的返回 null，即"不持久化"）。</summary>
    private static long? Live(DateTimeOffset? v, DateTimeOffset now) =>
        v is { } x && x > now ? x.ToUnixTimeMilliseconds() : null;

    /// <summary>恢复：已过期的时间点返回 null（等价于"该惩罚已结束"）。</summary>
    private static DateTimeOffset? Revive(long? ms, DateTimeOffset now)
    {
        if (ms is not { } v)
        {
            return null;
        }
        var t = DateTimeOffset.FromUnixTimeMilliseconds(v);
        return t > now ? t : null;
    }

    private static DateTimeOffset? FromMs(long? ms) =>
        ms is { } v && v > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(v) : null;
}
