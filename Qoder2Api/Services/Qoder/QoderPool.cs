using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qoder2Api.Configuration;
using Qoder2Api.Models;
using Qoder2Api.Services.Database;

namespace Qoder2Api.Services.Qoder;

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

public sealed class QoderPool
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, PoolEntry> _entries = new(StringComparer.Ordinal);
    private readonly SqliteDbService _db;
    private readonly PoolOptions _opt;
    private readonly ILogger<QoderPool> _log;

    private long _pickSeq;
    private volatile bool _dirty;

    public DateTimeOffset? LastFlushedAt { get; private set; }

    public PoolOptions Options => _opt;

    public QoderPool(SqliteDbService db, IOptions<PoolOptions> options, ILogger<QoderPool> log)
    {
        _db = db;
        _opt = options.Value;
        _log = log;
        LoadFromDb();
    }

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
                        PoolStateStore.ApplyToEntry(entry, s, _log);
                    }
                    _entries[acc.Id] = entry;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "加载池状态失败，将从空状态开始");
        }
    }

    public AccountLease? TryAcquire(string? model, string? boundAccountId, IReadOnlySet<string>? tried)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            SweepLeasesLocked(now);
            PruneLocked(now);

            if (!string.IsNullOrEmpty(boundAccountId) && (tried is null || !tried.Contains(boundAccountId)) && _entries.TryGetValue(boundAccountId, out var bound) && bound.IsHealthy(now, model) && !InFlightFullLocked(bound))
            {
                return OccupyLocked(bound, now);
            }

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
                var fallback = PickEarliestExpiryLocked(now, model, tried);
                return fallback is null ? null : OccupyLocked(fallback, now);
            }

            var picked = PickWeightedLocked(candidates, now);
            return OccupyLocked(picked, now);
        }
    }

    private AccountLease OccupyLocked(PoolEntry e, DateTimeOffset now)
    {
        e.InFlight++;
        e.LastUsedAt = now;
        e.UsedSeq = ++_pickSeq;
        e.LeaseDeadline = now + _opt.LeaseTtl;
        return new AccountLease(this, e.Account.Id);
    }

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

    private void SweepLeasesLocked(DateTimeOffset now)
    {
        foreach (var e in _entries.Values)
        {
            if (e.InFlight > 0 && e.LeaseDeadline is { } dl && now > dl)
            {
                _log.LogWarning("回收超期在途租约 account={Account} 泄漏计数={InFlight}", Short(e.Account.Id), e.InFlight);
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
            _log.LogInformation("全池冷却，兜底选中最早到期账号 account={Account} until={Until:HH:mm:ss}",
                Short(best.Account.Id), bestExpiry);
        }
        return best;
    }

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

        var scored = new List<(PoolEntry Entry, double Weight)>(candidates.Count);
        foreach (var e in candidates)
        {
            scored.Add((e, WeightOf(e, maxQuota, now)));
        }

        if (scored.Count > _opt.ShortlistSize && scored.All(s => Math.Abs(s.Weight - scored[0].Weight) < 1e-9))
        {
            Shuffle(scored);
        }

        scored.Sort((a, b) => b.Weight.CompareTo(a.Weight));
        var all = scored;
        var shortlist = scored.Count > _opt.ShortlistSize ? scored.GetRange(0, _opt.ShortlistSize) : scored;

        var eligible = shortlist.Where(s => now - (s.Entry.LastUsedAt ?? DateTimeOffset.MinValue) >= MinPickGap).ToList();
        if (eligible.Count == 0)
        {
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

    private double WeightOf(PoolEntry e, double maxQuota, DateTimeOffset now)
    {
        double w = 1.0;

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

        w += e.SuccessEma * 3.0;

        if (_opt.MaxInFlightPerAccount > 0)
        {
            double load = (double)e.InFlight / _opt.MaxInFlightPerAccount;
            w += (1.0 - Math.Min(load, 1.0)) * 1.5;
        }
        else
        {
            w += 1.5;
        }

        if (e.Account.IsDefault)
        {
            w += 2.0;
        }

        if (maxQuota > 0 && e.Account.Quota > 0)
        {
            w += e.Account.Quota / maxQuota * 2.0;
        }

        return w;
    }

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

    private static readonly TimeSpan MinPickGap = TimeSpan.FromMilliseconds(100);

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
            if (kind.CountsAsAccountFailure())
            {
                e.NoteError(now);
            }
            var resetAt = QoderErrorClassifier.ParseRateReset(body);

            switch (kind)
            {
                case QoderErrorKind.ServiceBusy:
                {
                    int waitSec = QoderErrorClassifier.ParseRetryAfterSeconds(body) ?? 30;
                    var until = now + TimeSpan.FromSeconds(waitSec);
                    if (e.CoolUntil is null || until > e.CoolUntil)
                    {
                        e.CoolUntil = until;
                        e.CoolKind = CoolKind.Soft;
                        e.CoolReason = $"服务繁忙（排队 {waitSec}s）";
                    }
                    break;
                }

                case QoderErrorKind.HardCredit:
                    e.CoolKind = CoolKind.Hard;
                    e.CoolUntil = NextDay4Am(now);
                    e.CoolReason = string.IsNullOrWhiteSpace(body) ? "余额不足" : Trim(body);
                    e.ModelCooldowns.Clear();
                    critical = true;
                    break;

                case QoderErrorKind.ModelRateLimit when !string.IsNullOrEmpty(model):
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
            _log.LogWarning("上游错误 account={Account} kind={Kind} model={Model} body={Body}",
                Short(accountId), kind, model ?? "-", Trim(body));
        }
        _ = critical;
    }

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

    private static DateTimeOffset NextDay4Am(DateTimeOffset now)
    {
        var local = now.LocalDateTime;
        var target = local.Hour < 4
            ? new DateTime(local.Year, local.Month, local.Day, 4, 0, 0)
            : new DateTime(local.Year, local.Month, local.Day, 4, 0, 0).AddDays(1);
        return new DateTimeOffset(target, TimeZoneInfo.Local.GetUtcOffset(target));
    }

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

    public bool HasUsableAccount()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            return _entries.Values.Any(e => e.IsHealthy(now, null) && !InFlightFullLocked(e));
        }
    }

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
            _log.LogError(ex, "池状态落盘失败，将在下轮重试");
        }
    }

    public void FlushOnShutdown()
    {
        _dirty = true;
        FlushIfDirty();
    }

    private static string Short(string id) => id.Length > 8 ? id[..8] : id;

    private static string Trim(string? body) =>
        string.IsNullOrEmpty(body) ? "" : (body.Length > 200 ? body[..200] : body);
}
