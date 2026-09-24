using reg.Models;

namespace reg.Services.Qoder;

public enum CoolKind
{
    /// <summary>软冷却。</summary>
    Soft = 0,

    /// <summary>硬冷却。</summary>
    Hard = 1,
}

public sealed class ModelCooldown
{
    public DateTimeOffset Until { get; set; }

    public DateTimeOffset ResetAt { get; set; }

    public string Reason { get; set; } = "";
}

public sealed class PoolEntry
{
    public PoolEntry(AccountRecord account)
    {
        Account = account;
    }

    public AccountRecord Account { get; set; }

    public bool Disabled { get; set; }
    public string? DisabledReason { get; set; }

    public bool NeedsRelogin { get; set; }
    public string? NeedsReloginReason { get; set; }

    public DateTimeOffset? CoolUntil { get; set; }
    public CoolKind CoolKind { get; set; }
    public string? CoolReason { get; set; }

    public int SoftStreak { get; set; }

    public Dictionary<string, ModelCooldown> ModelCooldowns { get; } = new(StringComparer.Ordinal);

    public DateTimeOffset? BreakerUntil { get; set; }
    public int BreakerFails { get; set; }

    public int BreakerRetryCount { get; set; }

    public DateTimeOffset? DegradeUntil { get; set; }
    public int ConsecutiveFails { get; set; }

    public int SessionDeadFails { get; set; }

    public long SuccessCount { get; set; }
    public long ErrTotal { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public double SuccessEma { get; set; } = 0.5;

    public DateTimeOffset? LastUsedAt { get; set; }

    public long UsedSeq { get; set; }

    public int InFlight { get; set; }

    public DateTimeOffset? LeaseDeadline { get; set; }

    public bool AdminDisabled => !string.Equals(Account.Status, "active", StringComparison.OrdinalIgnoreCase);

    public bool IsUnusable => AdminDisabled || Disabled || NeedsRelogin;

    public bool IsHealthy(DateTimeOffset now, string? model)
    {
        if (IsUnusable)
        {
            return false;
        }
        if (CoolUntil is { } cu && now < cu)
        {
            return false;
        }
        if (BreakerUntil is { } bu && now < bu)
        {
            return false;
        }
        if (DegradeUntil is { } du && now < du)
        {
            return false;
        }
        if (!string.IsNullOrEmpty(model) && IsModelCooled(now, model))
        {
            return false;
        }
        return true;
    }

    public bool IsModelCooled(DateTimeOffset now, string model)
    {
        return ModelCooldowns.TryGetValue(model, out var mc) && now < mc.Until;
    }

    public DateTimeOffset? EarliestExpiry(DateTimeOffset now)
    {
        DateTimeOffset? t = null;
        void Consider(DateTimeOffset? v)
        {
            if (v is { } x && now < x && (t is null || x < t))
            {
                t = x;
            }
        }
        Consider(CoolUntil);
        Consider(BreakerUntil);
        Consider(DegradeUntil);
        return t;
    }

    public (PoolAccountState State, string? Reason, DateTimeOffset? Until) Describe(DateTimeOffset now)
    {
        if (AdminDisabled || Disabled)
        {
            return (PoolAccountState.Disabled, DisabledReason ?? (AdminDisabled ? "管理员停用" : null), null);
        }
        if (NeedsRelogin)
        {
            return (PoolAccountState.NeedsRelogin, NeedsReloginReason, null);
        }
        if (BreakerUntil is { } bu && now < bu)
        {
            return (PoolAccountState.Breaker, CoolReason ?? "连续失败熔断", bu);
        }
        if (CoolUntil is { } cu && now < cu)
        {
            return (PoolAccountState.Cooling, CoolReason, cu);
        }
        if (DegradeUntil is { } du && now < du)
        {
            return (PoolAccountState.Degraded, "连续失败降权", du);
        }
        return (PoolAccountState.Ready, null, null);
    }

    public void ClearAllPenalties()
    {
        Disabled = false;
        DisabledReason = null;
        NeedsRelogin = false;
        NeedsReloginReason = null;
        CoolUntil = null;
        CoolKind = CoolKind.Soft;
        CoolReason = null;
        SoftStreak = 0;
        ModelCooldowns.Clear();
        BreakerUntil = null;
        BreakerFails = 0;
        BreakerRetryCount = 0;
        DegradeUntil = null;
        ConsecutiveFails = 0;
        SessionDeadFails = 0;
    }

    public void ClearCooling()
    {
        CoolUntil = null;
        CoolKind = CoolKind.Soft;
        CoolReason = null;
        SoftStreak = 0;
        ModelCooldowns.Clear();
    }

    public void PruneExpiredModelCooldowns(DateTimeOffset now)
    {
        if (ModelCooldowns.Count == 0)
        {
            return;
        }
        List<string>? dead = null;
        foreach (var (model, mc) in ModelCooldowns)
        {
            if (now >= mc.Until)
            {
                (dead ??= []).Add(model);
            }
        }
        if (dead is null)
        {
            return;
        }
        foreach (var m in dead)
        {
            ModelCooldowns.Remove(m);
        }
    }

    public void NoteSuccess(DateTimeOffset now)
    {
        SuccessCount++;
        LastSuccessAt = now;
        SuccessEma = SuccessEma * (1 - EmaAlpha) + EmaAlpha;

        BreakerFails = 0;
        BreakerRetryCount = 0;
        BreakerUntil = null;
        SoftStreak = 0;
        SessionDeadFails = 0;
        ConsecutiveFails = 0;
        DegradeUntil = null;
    }

    public void NoteError(DateTimeOffset now)
    {
        ErrTotal++;
        LastErrorAt = now;
        SuccessEma *= 1 - EmaAlpha;
    }

    private const double EmaAlpha = 0.15;
}
