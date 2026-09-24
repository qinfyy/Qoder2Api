using reg.Models;

namespace reg.Services.Qoder;

/// <summary>冷却类型。硬冷却的账号不参与「全冷却兜底」——调了必然再失败，白费一轮轮换。</summary>
public enum CoolKind
{
    /// <summary>软冷却：限流/404 等，到期即可恢复。</summary>
    Soft = 0,

    /// <summary>硬冷却：余额耗尽，要等签到/充值才可能恢复。</summary>
    Hard = 1,
}

/// <summary>单个 (账号, 模型) 的模型级独立冷却记录。</summary>
public sealed class ModelCooldown
{
    public DateTimeOffset Until { get; set; }

    /// <summary>上游给出的权威重置时刻（未截断）。用于台账展示，无则等于 <see cref="Until"/>。</summary>
    public DateTimeOffset ResetAt { get; set; }

    public string Reason { get; set; } = "";
}

/// <summary>
/// 单个账号的池运行时状态 + 状态机。
///
/// **正交性不变量**：可选择性由五个互不干扰的维度决定——
/// 禁用 / 账号级冷却 / 模型级冷却 / 熔断 / 连败降权。
/// <see cref="IsHealthy"/> 写成**并列或门**：任一截止未到期即不可选。这样天然实现
/// 「多机制并存取更长者、不叠加」，不需要显式比较长短，也就不会重复计罚。
///
/// **并发契约**：本类型的所有字段只在持有 <see cref="QoderPool"/> 的锁时读写。
/// 因此不需要 Interlocked/volatile——锁本身就是同步边界。唯一的例外是
/// <see cref="Account"/> 引用，它在账号列表刷新时被整体替换。
/// </summary>
public sealed class PoolEntry
{
    public PoolEntry(AccountRecord account)
    {
        Account = account;
    }

    /// <summary>账号凭证。账号列表刷新时整体替换，池状态保持不变。</summary>
    public AccountRecord Account { get; set; }

    // ---- 维度一：禁用 ----
    /// <summary>池自动禁用（连续会话失效等）。与管理员手动停用（Account.Status）独立。</summary>
    public bool Disabled { get; set; }
    public string? DisabledReason { get; set; }

    /// <summary>
    /// 需要重新登录的终态。与 Disabled 的区别：这是「凭证失效且无法自动续期」，
    /// 账号本身没坏，重新登录即可恢复。
    ///
    /// 为什么必须有它：设备流账号的凭证 30 天过期且全项目没有任何刷新逻辑，
    /// 没有这个终态的话，池会**反复选中一个必然 401 的账号**，每次白费一轮
    /// 上游往返和一次轮换名额，直到它被熔断为止。
    /// </summary>
    public bool NeedsRelogin { get; set; }
    public string? NeedsReloginReason { get; set; }

    // ---- 维度二：账号级冷却 ----
    public DateTimeOffset? CoolUntil { get; set; }
    public CoolKind CoolKind { get; set; }
    public string? CoolReason { get; set; }

    /// <summary>
    /// 连续软冷却次数（指数退避的指数）。只在**真正进入一次新冷却**时递增，
    /// 由成功/解冻清零。用于「无重置时间」时的有界退避。
    /// </summary>
    public int SoftStreak { get; set; }

    // ---- 维度三：模型级冷却 ----
    /// <summary>
    /// 模型 → 独立冷却。与账号级冷却**正交**：模型级限流只写这里、不写 CoolUntil，
    /// 因此多个模型同时限流时各自独立计时、互不覆盖（单个字段做不到这点），
    /// 且切到别的模型即可正常使用（模型豁免）。
    /// </summary>
    public Dictionary<string, ModelCooldown> ModelCooldowns { get; } = new(StringComparer.Ordinal);

    // ---- 维度四：熔断 ----
    public DateTimeOffset? BreakerUntil { get; set; }
    public int BreakerFails { get; set; }

    /// <summary>已熔断次数（指数退避的指数）。成功时清零。</summary>
    public int BreakerRetryCount { get; set; }

    // ---- 维度五：连败降权 ----
    public DateTimeOffset? DegradeUntil { get; set; }
    public int ConsecutiveFails { get; set; }

    // ---- 会话失效连续计数 ----
    public int SessionDeadFails { get; set; }

    // ---- 统计 ----
    public long SuccessCount { get; set; }
    public long ErrTotal { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }

    /// <summary>
    /// 近期成功率的指数移动平均（0..1，初值 0.5 中性）。
    /// **选号权重用它，而不是终身成功率**——SuccessCount/ErrTotal 是终身累计、
    /// 只增不减，拿它算成功率会让早期出过错的账号被永久压权且永不恢复。
    /// EMA 天然遗忘旧历史：α=0.15 时约 15 次观测后旧值权重降到 10% 以下。
    /// </summary>
    public double SuccessEma { get; set; } = 0.5;

    // ---- 选号运行态 ----
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>
    /// 单调递增的选中序号。**LRU 兜底必须用它而不是墙钟**：Windows 上
    /// <c>DateTimeOffset.UtcNow</c> 精度只有 ~0.5ms，高并发下多个账号的 LastUsedAt
    /// 会完全相等，基于时间的比较会恒选到同一个，防惊群失效。
    /// </summary>
    public long UsedSeq { get; set; }

    /// <summary>在途请求数。只在池锁内增减（选号与占用合并为一次原子操作，无 TOCTOU）。</summary>
    public int InFlight { get; set; }

    /// <summary>在途租约的兜底回收时刻：超过它仍在途的计数视为泄漏，强制归零。</summary>
    public DateTimeOffset? LeaseDeadline { get; set; }

    // ---------------------------------------------------------------------
    // 判定
    // ---------------------------------------------------------------------

    /// <summary>管理员是否停用了该账号。</summary>
    public bool AdminDisabled => !string.Equals(Account.Status, "active", StringComparison.OrdinalIgnoreCase);

    /// <summary>账号是否彻底不可用（管理员停用 / 池禁用 / 需重登）。</summary>
    public bool IsUnusable =>
        AdminDisabled || Disabled || NeedsRelogin;

    /// <summary>
    /// 账号对该模型是否可选。**并列或门**——见类型注释。
    /// </summary>
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

    /// <summary>该模型是否正处于模型级冷却（切到别的模型不受影响）。</summary>
    public bool IsModelCooled(DateTimeOffset now, string model)
    {
        return ModelCooldowns.TryGetValue(model, out var mc) && now < mc.Until;
    }

    /// <summary>
    /// 当前生效的最近截止时刻（冷却/熔断/降权取最早者）；不在任何惩罚期返回 null。
    /// 供「全冷却兜底」挑最早到期者。
    /// </summary>
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

    /// <summary>当前对外的状态枚举 + 原因 + 截止时刻。</summary>
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

    /// <summary>清空全部惩罚状态（人工解冻）。</summary>
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

    /// <summary>
    /// 清空**冷却域**（账号级冷却 + 模型级冷却 + 软退避计数 + 禁用），**不动熔断器**。
    /// 签到解冻/余额恢复走这里：那证明的是"计费通道恢复"，不证明"上游服务健康"，
    /// 熔断（连续 5xx 信号）不该被它覆盖。
    /// </summary>
    public void ClearCooling()
    {
        CoolUntil = null;
        CoolKind = CoolKind.Soft;
        CoolReason = null;
        SoftStreak = 0;
        ModelCooldowns.Clear();
    }

    /// <summary>惰性清理已过期的模型级冷却（防字典无限增长）。调用方需持锁。</summary>
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

    /// <summary>记录一次成功：清空**全部**失败态（成功是账号已恢复的最强证据）。</summary>
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
        // 刻意**不清** ModelCooldowns：模型级冷却每模型独立计时，
        // 在模型 A 上成功不该抹掉模型 B 的冷却截止。
        // 也刻意**不清** NeedsRelogin：能成功就说明凭证有效，但这条路径
        // 在 NeedsRelogin 为真时根本不会被选中，属于防御性冗余。
    }

    /// <summary>记录一次错误（仅用于统计与 EMA，具体惩罚由池决定）。</summary>
    public void NoteError(DateTimeOffset now)
    {
        ErrTotal++;
        LastErrorAt = now;
        SuccessEma *= 1 - EmaAlpha;
    }

    /// <summary>成功率的 EMA 平滑系数。0.15 ≈ 15 次观测后旧值权重降到 10% 以下。</summary>
    private const double EmaAlpha = 0.15;
}
