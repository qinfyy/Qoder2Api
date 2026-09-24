using reg.Models;
using reg.Services.Database;

namespace reg.Services.Qoder;

/// <summary>号池调优参数（可经 appsettings 的 "Pool" 节覆盖）。</summary>
public sealed class PoolOptions
{
    /// <summary>单请求最多换几个账号。</summary>
    public int MaxRotate { get; set; } = 3;

    /// <summary>单账号在途请求上限；0 = 不限。</summary>
    public int MaxInFlightPerAccount { get; set; } = 3;

    /// <summary>连续 5xx 达到该次数触发熔断。</summary>
    public int BreakerThreshold { get; set; } = 3;

    /// <summary>熔断基础时长（每熔断一次翻倍，封顶 <see cref="BreakerCooldownMax"/>）。</summary>
    public TimeSpan BreakerCooldown { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan BreakerCooldownMax { get; set; } = TimeSpan.FromHours(6);

    /// <summary>软冷却指数退避的封顶时长。</summary>
    public TimeSpan SoftRateMax { get; set; } = TimeSpan.FromHours(2);

    /// <summary>未知错误连败达到该次数触发降权。</summary>
    public int DegradeThreshold { get; set; } = 5;

    /// <summary>降权（临时出池）时长。</summary>
    public TimeSpan DegradeCooldown { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>连续会话失效达到该次数判定账号死亡（自动禁用）。</summary>
    public int SessionDeadThreshold { get; set; } = 3;

    /// <summary>闲置补偿：每小时恢复的权重。</summary>
    public double IdleWeightPerHour { get; set; } = 0.5;

    /// <summary>闲置补偿的权重上限。</summary>
    public double IdleWeightMax { get; set; } = 5.0;

    /// <summary>Top-N 短名单大小（只在其中加权抽签，用于打散热点）。</summary>
    public int ShortlistSize { get; set; } = 5;

    /// <summary>在途租约的兜底回收时长（防泄漏）。</summary>
    public TimeSpan LeaseTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>状态落盘周期。</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// 在途租约。**必须**在 endpoint 的 finally 里 Dispose——它是账号在途计数归零的
/// 唯一保证。实现为幂等（重复 Dispose 安全），并带 TTL 兜底（池会强制回收超期租约），
/// 即使将来有人写出漏 Dispose 的路径，池也不会永久"漏"在途数。
/// </summary>
public sealed class AccountLease : IDisposable
{
    private QoderPool? _pool;
    private int _disposed;

    internal AccountLease(QoderPool pool, string accountId)
    {
        _pool = pool;
        AccountId = accountId;
    }

    public string AccountId { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        Interlocked.Exchange(ref _pool, null)?.Release(AccountId);
    }
}

/// <summary>
/// 号池：内存持有全部账号的健康状态，提供加权选号、在途租约与状态迁移。
///
/// **锁纪律（重要）**：<see cref="_lock"/> 内只做纯内存状态迁移——
/// 绝不做 I/O、绝不做 await、绝不触发事件回调、绝不写日志。原因：
///   1. 抢不到锁的线程是**阻塞**在 Monitor 上的，会占着线程池线程；
///      持锁做 I/O 会把线程池吃干，进而拖垮 Blazor Server 的渲染调度
///      （Blazor Server 对线程池饥饿极其敏感，表现为整个管理页"点不动"）。
///   2. 事件回调（OnAuthStateChanged）的订阅方会同步查库（见 Home.razor），
///      在锁内触发等于持着全局池锁去等 SQLite，最坏可停摆数十秒。
/// 持久化一律交给后台 flusher，请求路径只改内存 + 置脏。
/// </summary>
public sealed class QoderPool
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, PoolEntry> _entries = new(StringComparer.Ordinal);
    private readonly SqliteDbService _db;
    private readonly PoolOptions _opt;

    private long _pickSeq;
    private volatile bool _dirty;

    /// <summary>最近一次成功落盘的时刻（供 UI/日志观测）。</summary>
    public DateTimeOffset? LastFlushedAt { get; private set; }

    public PoolOptions Options => _opt;

    public QoderPool(SqliteDbService db, PoolOptions options)
    {
        _db = db;
        _opt = options;
        LoadFromDb();
    }

    // ---------------------------------------------------------------------
    // 账号注册表
    // ---------------------------------------------------------------------

    /// <summary>
    /// 用最新的账号列表对齐池：新账号加入、消失的账号剔除（状态随之丢弃）、
    /// 已有账号只换凭证引用、**保留健康状态**。
    /// </summary>
    public void SyncAccounts(IReadOnlyList<AccountRecord> accounts)
    {
        bool changed = false;
        lock (_lock)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var acc in accounts)
            {
                seen.Add(acc.Id);
                if (_entries.TryGetValue(acc.Id, out var e))
                {
                    e.Account = acc; // 保留池状态，只换凭证
                }
                else
                {
                    _entries[acc.Id] = new PoolEntry(acc);
                    changed = true;
                }
            }
            foreach (var id in _entries.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _entries.Remove(id);
                changed = true;
            }
        }
        if (changed)
        {
            _dirty = true;
        }
    }

    /// <summary>从数据库加载持久化的池状态（启动时调用一次）。</summary>
    private void LoadFromDb()
    {
        try
        {
            var accounts = _db.GetAllAccounts();
            var states = _db.GetAllPoolStates();
            lock (_lock)
            {
                foreach (var acc in accounts)
                {
                    var entry = new PoolEntry(acc);
                    if (states.TryGetValue(acc.Id, out var s))
                    {
                        PoolStateStore.ApplyToEntry(entry, s);
                    }
                    _entries[acc.Id] = entry;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QoderPool] 加载池状态失败（将从空状态开始）: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------------
    // 选号 + 在途租约（合并为一次锁内原子操作，避免 TOCTOU）
    // ---------------------------------------------------------------------

    /// <summary>
    /// 选一个账号并**同时**占用一个在途名额。
    ///
    /// 为什么选号与占用必须合并：分成两次加锁的话，两个并发请求可以同时通过
    /// "该账号最近未被选中"的检查、然后都选中它——靠时间窗口去补这个竞态只能
    /// 降低概率，无法消除。合并后，进入者串行地看到前一个已经写好的 LastUsedAt/InFlight。
    /// </summary>
    /// <param name="model">请求的模型（用于模型级冷却豁免）；空表示不限模型。</param>
    /// <param name="boundAccountId">API Key 绑定的账号（**优先，非独占**：不健康时回落池）。</param>
    /// <param name="tried">本次请求已试过的账号（请求级轮换）。</param>
    public AccountLease? TryAcquire(string? model, string? boundAccountId, IReadOnlySet<string>? tried)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            SweepLeasesLocked(now);
            PruneLocked(now);

            // 1. 绑定账号优先（粘性）。绑定语义是"优先"不是"独占"——它不健康时
            //    立刻回落池选号，否则一个绑定的号被限流就会拖死整个 API Key。
            if (!string.IsNullOrEmpty(boundAccountId)
                && (tried is null || !tried.Contains(boundAccountId))
                && _entries.TryGetValue(boundAccountId, out var bound)
                && bound.IsHealthy(now, model)
                && !InFlightFullLocked(bound))
            {
                return OccupyLocked(bound, now);
            }

            // 2. 普通选号
            var candidates = new List<PoolEntry>();
            foreach (var (id, e) in _entries)
            {
                if (tried is not null && tried.Contains(id))
                {
                    continue;
                }
                if (!e.IsHealthy(now, model))
                {
                    continue;
                }
                if (InFlightFullLocked(e))
                {
                    continue;
                }
                candidates.Add(e);
            }

            if (candidates.Count == 0)
            {
                // 全冷却兜底：挑最早到期的软冷却/熔断号做半开试探。
                // 硬冷却（余额耗尽）**排除**——调了必然再失败，白费一轮轮换与上游往返。
                var fallback = PickEarliestExpiryLocked(now, model, tried);
                return fallback is null ? null : OccupyLocked(fallback, now);
            }

            var picked = PickWeightedLocked(candidates, now);
            return OccupyLocked(picked, now);
        }
    }

    /// <summary>占用名额并记录选中（调用方需持锁）。</summary>
    private AccountLease OccupyLocked(PoolEntry e, DateTimeOffset now)
    {
        e.InFlight++;
        e.LastUsedAt = now;
        e.UsedSeq = ++_pickSeq;
        // 租约 TTL 兜底：只要还有在途就不断续期，最后一个 Release 时归零。
        e.LeaseDeadline = now + _opt.LeaseTtl;
        return new AccountLease(this, e.Account.Id);
    }

    /// <summary>释放一个在途名额（幂等，减到 0 为止）。</summary>
    internal void Release(string accountId)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(accountId, out var e) && e.InFlight > 0)
            {
                e.InFlight--;
                if (e.InFlight == 0)
                {
                    e.LeaseDeadline = null;
                }
            }
        }
    }

    /// <summary>回收超期租约（防漏 Dispose 导致的永久在途泄漏）。</summary>
    private void SweepLeasesLocked(DateTimeOffset now)
    {
        foreach (var e in _entries.Values)
        {
            if (e.InFlight > 0 && e.LeaseDeadline is { } dl && now > dl)
            {
                Console.WriteLine($"[QoderPool] 回收超期在途租约 account={Short(e.Account.Id)} 泄漏计数={e.InFlight}");
                e.InFlight = 0;
                e.LeaseDeadline = null;
            }
        }
    }

    private void PruneLocked(DateTimeOffset now)
    {
        foreach (var e in _entries.Values)
        {
            e.PruneExpiredModelCooldowns(now);
        }
    }

    private bool InFlightFullLocked(PoolEntry e)
    {
        int limit = _opt.MaxInFlightPerAccount;
        return limit > 0 && e.InFlight >= limit;
    }

    /// <summary>
    /// 全冷却兜底：在非禁用的软冷却/熔断/降权账号中选截止最早的一个。
    /// 这些账号"可能已经恢复"，失败成本仅一轮轮换；而硬冷却号调了必然失败。
    /// </summary>
    private PoolEntry? PickEarliestExpiryLocked(DateTimeOffset now, string? model, IReadOnlySet<string>? tried)
    {
        PoolEntry? best = null;
        DateTimeOffset bestExpiry = default;
        foreach (var (id, e) in _entries)
        {
            if (tried is not null && tried.Contains(id))
            {
                continue;
            }
            if (e.IsUnusable)
            {
                continue; // 禁用/需重登的账号永不参与兜底
            }
            if (e.CoolKind == CoolKind.Hard && e.CoolUntil is { } cu && now < cu)
            {
                continue; // 余额耗尽号：等签到，调了必 402
            }
            if (InFlightFullLocked(e))
            {
                continue;
            }
            // 该模型仍在独立冷却中的账号也不参与——换了也是同一个模型，照样失败。
            if (!string.IsNullOrEmpty(model) && e.IsModelCooled(now, model))
            {
                continue;
            }
            var exp = e.EarliestExpiry(now);
            if (exp is null)
            {
                continue;
            }
            if (best is null || exp.Value < bestExpiry)
            {
                best = e;
                bestExpiry = exp.Value;
            }
        }
        if (best is not null)
        {
            Console.WriteLine($"[QoderPool] 兜底选中最早到期账号 account={Short(best.Account.Id)} until={bestExpiry:HH:mm:ss}");
        }
        return best;
    }

    /// <summary>
    /// 加权随机选号：先按权重取 Top-N 短名单，再在短名单内加权抽签。
    /// 短名单的作用是打散热点（避免永远打同一个号），加权抽签保留偏好。
    /// </summary>
    private PoolEntry PickWeightedLocked(List<PoolEntry> candidates, DateTimeOffset now)
    {
        double maxQuota = 0;
        foreach (var e in candidates)
        {
            if (e.Account.Quota > maxQuota)
            {
                maxQuota = e.Account.Quota;
            }
        }

        // 权重只算一次（不在比较器里现算，否则是 O(n log n) 次冗余浮点计算）
        var scored = new List<(PoolEntry Entry, double Weight)>(candidates.Count);
        foreach (var e in candidates)
        {
            scored.Add((e, WeightOf(e, maxQuota, now)));
        }

        // 等权重洗牌：权重全等时按字典序截断会让靠后的账号永远进不了 Top-N
        // （被"饿死"，而 LRU 兜底又只在短名单内转——这是惊群集中到单号的根因）。
        if (scored.Count > _opt.ShortlistSize && scored.All(s => Math.Abs(s.Weight - scored[0].Weight) < 1e-9))
        {
            Shuffle(scored);
        }

        scored.Sort((a, b) => b.Weight.CompareTo(a.Weight));
        var all = scored; // 保留全量（降序），供 LRU 兜底在全量范围里选最旧者
        var shortlist = scored.Count > _opt.ShortlistSize
            ? scored.GetRange(0, _opt.ShortlistSize)
            : scored;

        // 防并发撞号：短名单里剔掉"刚刚才被选中"的账号（同批并发 goroutine 串行进入，
        // 每个进入者都把 LastUsedAt 置为 now，于是第 2..N 个进入者会自然被挤向别的号）。
        var eligible = shortlist.Where(s => now - (s.Entry.LastUsedAt ?? DateTimeOffset.MinValue) >= MinPickGap).ToList();
        if (eligible.Count == 0)
        {
            // 短名单全刚用过 → LRU 兜底，在**全量**里选 usedSeq 最小的（最久没被选中的）。
            // 用单调序号而非墙钟：Windows 上时间精度 ~0.5ms，并发下墙钟会全等。
            var oldest = all[0].Entry;
            foreach (var s in all)
            {
                if (s.Entry.UsedSeq < oldest.UsedSeq)
                {
                    oldest = s.Entry;
                }
            }
            return oldest;
        }

        return WeightedDraw(eligible);
    }

    /// <summary>
    /// 单个账号的权重。
    ///
    /// 刻意**不用终身成功率**：SuccessCount/ErrTotal 是终身累计、只增不减，
    /// 拿它算成功率会让早期出过错的账号被永久压权且永不恢复。这里用 EMA 平滑的
    /// 近期成功率（见 <see cref="PoolEntry.SuccessEma"/>）。
    /// </summary>
    private double WeightOf(PoolEntry e, double maxQuota, DateTimeOffset now)
    {
        double w = 1.0;

        // 1. 闲置补偿：越久没用权重越高，防止热点集中在少数号上。
        if (e.LastUsedAt is { } lu)
        {
            double hours = (now - lu).TotalHours;
            if (hours < 0)
            {
                hours = 0; // 时钟回拨保护
            }
            w += Math.Min(hours * _opt.IdleWeightPerHour, _opt.IdleWeightMax);
        }
        else
        {
            w += _opt.IdleWeightMax; // 从未使用 → 给满分，让新号尽快被验证
        }

        // 2. 近期成功率（EMA，0..1）。
        w += e.SuccessEma * 3.0;

        // 3. 负载因子：在途少的优先（均衡分摊）。
        if (_opt.MaxInFlightPerAccount > 0)
        {
            double load = (double)e.InFlight / _opt.MaxInFlightPerAccount;
            w += (1.0 - Math.Min(load, 1.0)) * 1.5;
        }
        else
        {
            w += 1.5;
        }

        // 4. 首选账号加成（IsDefault 的语义已降级为"偏好"，不再是唯一路由依据）。
        if (e.Account.IsDefault)
        {
            w += 2.0;
        }

        // 5. 余额因子：仅当池内确实有账号报了余额时才启用。
        //    Qoder 没有余额查询端点（抓包确认 userinfo/user-plan 都不含），
        //    该字段基本恒 0——若不加这个守卫，该项会退化成常数、白白稀释其他因子。
        if (maxQuota > 0 && e.Account.Quota > 0)
        {
            w += e.Account.Quota / maxQuota * 2.0;
        }

        return w;
    }

    /// <summary>按权重加权随机抽签。权重放大成整数用定点抽签，避免浮点累加误差。</summary>
    private static PoolEntry WeightedDraw(List<(PoolEntry Entry, double Weight)> items)
    {
        const double scale = 1_000_000;
        long total = 0;
        var weights = new long[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            weights[i] = (long)Math.Max(items[i].Weight * scale, 0);
            total += weights[i];
        }
        if (total <= 0)
        {
            return items[Random.Shared.Next(items.Count)].Entry;
        }
        long r = Random.Shared.NextInt64(total);
        long acc = 0;
        for (int i = 0; i < items.Count; i++)
        {
            acc += weights[i];
            if (r < acc)
            {
                return items[i].Entry;
            }
        }
        return items[^1].Entry;
    }

    private static void Shuffle<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>防撞号窗口：同一账号在该窗口内不重复被选中（除非短名单全部刚用过）。</summary>
    private static readonly TimeSpan MinPickGap = TimeSpan.FromMilliseconds(100);

    // ---------------------------------------------------------------------
    // 状态迁移（由 endpoint 依据错误分类调用）
    // ---------------------------------------------------------------------

    /// <summary>记录成功：清空全部失败态。</summary>
    public void NoteSuccess(string accountId)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(accountId, out var e))
            {
                e.NoteSuccess(DateTimeOffset.UtcNow);
                _dirty = true;
            }
        }
    }

    /// <summary>
    /// 依据错误分类执行惩罚。**唯一**的错误 → 动作映射入口。
    ///
    /// 关键纪律：
    ///   - 「请求的问题」类错误（内容拦截/上下文超长/参数错误）不罚号——账号没做错任何事；
    ///   - 已在冷却期内的再次失败**不延长冷却**——用户重试与并发兜底探测不得把冷却
    ///     越堆越厚（这正是同类实现"全池被推到封顶"的元凶）；
    ///   - 会话失效连续达到阈值才禁用，单次不杀号（会被网络抖动误触发）。
    /// </summary>
    public void ApplyError(string accountId, QoderErrorKind kind, string? body, string? model)
    {
        if (!kind.PenalizesAccount())
        {
            return;
        }

        bool critical = false;
        lock (_lock)
        {
            if (!_entries.TryGetValue(accountId, out var e))
            {
                return;
            }
            var now = DateTimeOffset.UtcNow;
            // 只有"账号的锅"才计入失败统计。ServiceBusy 是服务级繁忙，
            // 记到账号头上会让它的成功率被上游高峰永久拉低（EMA 也会被拖累）。
            if (kind.CountsAsAccountFailure())
            {
                e.NoteError(now);
            }
            var resetAt = QoderErrorClassifier.ParseRateReset(body);

            switch (kind)
            {
                case QoderErrorKind.ServiceBusy:
                {
                    // 服务排队：按上游给的 retryAfterSeconds 短冷却（拿不到就退避 30s），
                    // **不推进软退避指数**——这是服务级状况，不是该账号在限流。
                    // 换号继续：其他账号未必在同一个队列里。
                    int waitSec = QoderErrorClassifier.ParseRetryAfterSeconds(body) ?? 30;
                    var until = now + TimeSpan.FromSeconds(waitSec);
                    // 已有的更远截止不被缩短（取更长者）。
                    if (e.CoolUntil is null || until > e.CoolUntil)
                    {
                        e.CoolUntil = until;
                        e.CoolKind = CoolKind.Soft;
                        e.CoolReason = $"服务繁忙（排队 {waitSec}s）";
                    }
                    break;
                }

                case QoderErrorKind.HardCredit:
                    // 余额耗尽：硬冷却到次日 04:00（等签到/充值）。硬冷却号不参与兜底。
                    e.CoolKind = CoolKind.Hard;
                    e.CoolUntil = NextDay4Am(now);
                    e.CoolReason = string.IsNullOrWhiteSpace(body) ? "余额不足" : Trim(body);
                    e.ModelCooldowns.Clear();
                    critical = true;
                    break;

                case QoderErrorKind.ModelRateLimit when !string.IsNullOrEmpty(model):
                    // 模型级限流：只锁该模型，切模型即豁免。**不写账号级冷却**——
                    // 账号本身是健康的。
                    e.ModelCooldowns[model] = new ModelCooldown
                    {
                        Until = CapReset(now, resetAt ?? now + _opt.BreakerCooldown),
                        ResetAt = resetAt ?? default,
                        Reason = "模型级限流",
                    };
                    critical = true;
                    break;

                case QoderErrorKind.SoftRate:
                    ApplySoftCooldownLocked(e, now, resetAt, string.IsNullOrWhiteSpace(body) ? "429 rate limit" : Trim(body));
                    critical = true;
                    break;

                case QoderErrorKind.SessionDead:
                    e.SessionDeadFails++;
                    if (e.SessionDeadFails >= _opt.SessionDeadThreshold)
                    {
                        e.SessionDeadFails = 0;
                        e.ClearCooling();
                        e.Disabled = true;
                        e.DisabledReason = "连续会话失效，需重新登录";
                        e.NeedsRelogin = true;
                        e.NeedsReloginReason = "凭证失效且无法自动续期";
                        critical = true;
                    }
                    break;

                case QoderErrorKind.AccountFault:
                    // 账号级授权/风控：长冷却（不直接禁用——有些是临时风控）。
                    e.CoolKind = CoolKind.Soft;
                    e.CoolUntil = now + _opt.SoftRateMax;
                    e.CoolReason = string.IsNullOrWhiteSpace(body) ? "账号级授权故障" : Trim(body);
                    e.ModelCooldowns.Clear();
                    critical = true;
                    break;

                case QoderErrorKind.NotFound:
                    // 固定短冷却，**不**随限流退避升级——路径偶发缺失不是限流信号。
                    e.CoolKind = CoolKind.Soft;
                    e.CoolUntil = now + TimeSpan.FromSeconds(60);
                    e.CoolReason = "上游 404";
                    critical = true;
                    break;

                case QoderErrorKind.Server:
                    RecordBreakerFailureLocked(e, now);
                    critical = true;
                    break;

                case QoderErrorKind.Client:
                case QoderErrorKind.Transport:
                    // 未知错误与传输层失败：喂连败计数（"不知道原因的失败"的兜底），
                    // 达到阈值才临时出池。两者都已在上面跳过了 NoteError——
                    // 传输层失败尤其不该算账号的锅（换任何账号都一样连不上）。
                    e.ConsecutiveFails++;
                    if (e.ConsecutiveFails >= _opt.DegradeThreshold)
                    {
                        e.ConsecutiveFails = 0;
                        // 已在降权期内不延长（取更长者语义由 IsHealthy 的或门保证）。
                        if (e.DegradeUntil is null || now >= e.DegradeUntil)
                        {
                            e.DegradeUntil = now + _opt.DegradeCooldown;
                        }
                        critical = true;
                    }
                    break;
            }
            _dirty = true;
        }

        if (kind == QoderErrorKind.Client || kind == QoderErrorKind.Server)
        {
            // 无法归类的形态记一条日志，便于后续据日志补分类表（当前没有真实错误样本）。
            Console.WriteLine($"[QoderPool] 上游错误 account={Short(accountId)} kind={kind} model={model ?? "-"} body={Trim(body)}");
        }
        _ = critical;
    }

    /// <summary>
    /// 应用一次软冷却。**已在冷却期内不延长、不翻倍**——这是"兜底探测不翻倍"的落点。
    /// 有上游权威重置时刻时精确对齐它（截断到封顶），绝不指数放大。
    /// </summary>
    private void ApplySoftCooldownLocked(PoolEntry e, DateTimeOffset now, DateTimeOffset? resetAt, string reason)
    {
        bool inSoftCooldown = e.CoolKind == CoolKind.Soft && e.CoolUntil is { } cu && now < cu;
        if (resetAt is { } ra)
        {
            e.CoolUntil = CapReset(now, ra);
        }
        else if (!inSoftCooldown)
        {
            // 只在真正进入一次新冷却时推进退避指数。
            e.SoftStreak++;
            e.CoolUntil = now + SoftDurationLocked(e.SoftStreak);
        }
        e.CoolKind = CoolKind.Soft;
        e.CoolReason = reason;
        e.ModelCooldowns.Clear(); // 账号级冷却清空模型豁免（切模型不该绕过账号级限流）
    }

    /// <summary>把上游重置墙钟截断到封顶时长。</summary>
    private DateTimeOffset CapReset(DateTimeOffset now, DateTimeOffset resetAt)
    {
        var cap = now + _opt.SoftRateMax;
        if (resetAt > cap)
        {
            return cap;
        }
        // 重置时刻已过期（时钟偏移/文案过期）→ 立即恢复。
        return resetAt > now ? resetAt : now.AddMilliseconds(1);
    }

    /// <summary>软冷却的有界指数退避：基数起按 2^(streak-1) 放大，封顶。</summary>
    private TimeSpan SoftDurationLocked(int streak)
    {
        var d = TimeSpan.FromSeconds(60);
        if (streak > 1)
        {
            int shift = Math.Min(streak - 1, 16); // 防移位溢出
            d = TimeSpan.FromSeconds(60L << shift);
        }
        return d > _opt.SoftRateMax ? _opt.SoftRateMax : d;
    }

    private void RecordBreakerFailureLocked(PoolEntry e, DateTimeOffset now)
    {
        e.BreakerFails++;
        if (e.BreakerFails < _opt.BreakerThreshold)
        {
            return;
        }
        var d = _opt.BreakerCooldown;
        for (int i = 0; i < e.BreakerRetryCount; i++)
        {
            d *= 2;
            if (d >= _opt.BreakerCooldownMax)
            {
                d = _opt.BreakerCooldownMax;
                break;
            }
        }
        e.BreakerFails = 0;
        e.BreakerRetryCount++;
        e.BreakerUntil = now + d;
        e.CoolReason = "连续 5xx 熔断";
    }

    /// <summary>次日 04:00（本地时区）。余额耗尽号等这个点之后的签到恢复。</summary>
    private static DateTimeOffset NextDay4Am(DateTimeOffset now)
    {
        var local = now.LocalDateTime;
        var target = local.Hour < 4
            ? new DateTime(local.Year, local.Month, local.Day, 4, 0, 0)
            : new DateTime(local.Year, local.Month, local.Day, 4, 0, 0).AddDays(1);
        return new DateTimeOffset(target, TimeZoneInfo.Local.GetUtcOffset(target));
    }

    // ---------------------------------------------------------------------
    // 人工操作
    // ---------------------------------------------------------------------

    /// <summary>解冻：清空全部惩罚状态，账号立刻回到池中。</summary>
    public bool Revive(string accountId)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(accountId, out var e))
            {
                return false;
            }
            e.ClearAllPenalties();
            _dirty = true;
            return true;
        }
    }

    // ---------------------------------------------------------------------
    // 查询
    // ---------------------------------------------------------------------

    /// <summary>池中是否存在可立即服务的账号（endpoint 的准入闸门用）。</summary>
    public bool HasUsableAccount()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            return _entries.Values.Any(e => e.IsHealthy(now, null) && !InFlightFullLocked(e));
        }
    }

    /// <summary>整体快照（供 UI 顶部徽章与 REST 接口）。</summary>
    public PoolSnapshot Snapshot()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var snap = new PoolSnapshot { Total = _entries.Count };
            foreach (var (id, e) in _entries.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var st = StatusOfLocked(id, e, now);
                snap.Accounts.Add(st);
                switch (st.State)
                {
                    case PoolAccountState.Ready: snap.Ready++; break;
                    case PoolAccountState.Cooling: snap.Cooling++; break;
                    case PoolAccountState.Breaker: snap.Breaker++; break;
                    case PoolAccountState.Degraded: snap.Degraded++; break;
                    case PoolAccountState.NeedsRelogin: snap.NeedsRelogin++; break;
                    case PoolAccountState.Disabled: snap.Disabled++; break;
                }
            }
            snap.Servable = snap.Ready > 0;
            return snap;
        }
    }

    private PoolAccountStatus StatusOfLocked(string id, PoolEntry e, DateTimeOffset now)
    {
        var (state, reason, until) = e.Describe(now);
        long remaining = 0;
        if (until is { } u && u > now)
        {
            remaining = (long)Math.Ceiling((u - now).TotalSeconds);
        }
        return new PoolAccountStatus
        {
            AccountId = id,
            UserName = e.Account.UserName,
            UserEmail = e.Account.UserEmail,
            PlanName = e.Account.PlanName,
            AuthMethod = e.Account.AuthMethod,
            State = state,
            Reason = reason,
            AdminDisabled = e.AdminDisabled,
            AutoDisabled = e.Disabled,
            RemainingSec = remaining,
            UntilMs = until?.ToUnixTimeMilliseconds(),
            ConsecutiveFails = e.ConsecutiveFails,
            SuccessCount = e.SuccessCount,
            ErrTotal = e.ErrTotal,
            SuccessRate = Math.Round(e.SuccessEma, 4),
            InFlight = e.InFlight,
            InFlightLimit = _opt.MaxInFlightPerAccount,
            BreakerFails = e.BreakerFails,
            RateLimitedModels = e.ModelCooldowns
                .Where(kv => now < kv.Value.Until)
                .Select(kv => kv.Key)
                .OrderBy(m => m, StringComparer.Ordinal)
                .ToList(),
            IsPreferred = e.Account.IsDefault,
            LastUsedMs = e.LastUsedAt?.ToUnixTimeMilliseconds(),
        };
    }

    // ---------------------------------------------------------------------
    // 持久化（仅由后台 flusher 调用，绝不在请求路径上）
    // ---------------------------------------------------------------------

    /// <summary>把池状态落盘（脏才写）。由后台 flusher 周期调用。</summary>
    public void FlushIfDirty()
    {
        if (!_dirty)
        {
            return;
        }
        List<PoolStateRecord> states;
        lock (_lock)
        {
            _dirty = false;
            states = _entries.Select(kv => PoolStateStore.ToRecord(kv.Key, kv.Value)).ToList();
        }
        try
        {
            _db.SavePoolStates(states);
            LastFlushedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            _dirty = true; // 落盘失败：重新置脏，下轮重试
            Console.WriteLine($"[QoderPool] 池状态落盘失败（将重试）: {ex.Message}");
        }
    }

    /// <summary>进程退出前的最后一次落盘。</summary>
    public void FlushOnShutdown()
    {
        _dirty = true;
        FlushIfDirty();
    }

    private static string Short(string id) => id.Length > 8 ? id[..8] : id;

    private static string Trim(string? body) =>
        string.IsNullOrEmpty(body) ? "" : (body.Length > 200 ? body[..200] : body);
}
