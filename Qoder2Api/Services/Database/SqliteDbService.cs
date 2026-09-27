using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Qoder2Api.Models;
using Qoder2Api.Services;
using Qoder2Api.Services.Qoder;

namespace Qoder2Api.Services.Database;

/// <summary>全量用量汇总，供页面顶部的统计卡片使用。</summary>
public sealed class UsageSummary
{
    public int Count { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long TotalTokens { get; set; }
    public long ReasoningTokens { get; set; }
    public long CachedTokens { get; set; }
    public double Credits { get; set; }
    public double AvgLatencyMs { get; set; }
    public double AvgFirstTokenMs { get; set; }
    public int FirstTokenSamples { get; set; }
    public int FirstTokenStreamSamples { get; set; }
}

public class SqliteDbService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly Time _time;

    public SqliteDbService(
        IDbContextFactory<AppDbContext> factory,
        Time time,
        ILogger<SqliteDbService> log)
    {
        _factory = factory;
        _time = time;
        using var db = _factory.CreateDbContext();
        AppDbContext.InitializeDatabase(db, time, log);
    }

    public List<AccountRecord> GetAllAccounts()
    {
        using var db = _factory.CreateDbContext();
        return db.Accounts
            .AsNoTracking()
            .OrderByDescending(a => a.IsDefault)
            .ThenByDescending(a => a.CreatedAt)
            .ToList();
    }

    public AccountRecord? GetActiveAccount()
    {
        using var db = _factory.CreateDbContext();
        return db.Accounts
            .AsNoTracking()
            .Where(a => a.Status == "active")
            .OrderByDescending(a => a.IsDefault)
            .ThenByDescending(a => a.CreatedAt)
            .FirstOrDefault();
    }

    public AccountRecord? GetAccountById(string id)
    {
        using var db = _factory.CreateDbContext();
        return db.Accounts
            .AsNoTracking()
            .FirstOrDefault(a => a.Id == id);
    }

    public void UpsertAccount(AccountRecord acc)
    {
        using var db = _factory.CreateDbContext();
        var existing = db.Accounts.FirstOrDefault(a => a.Id == acc.Id);
        if (existing != null)
        {
            existing.UserId = acc.UserId;
            existing.UserName = acc.UserName;
            existing.UserEmail = acc.UserEmail;
            existing.PlanName = acc.PlanName;
            existing.AuthMethod = acc.AuthMethod;
            existing.JobToken = acc.JobToken;
            existing.DeviceToken = acc.DeviceToken;
            existing.PatToken = acc.PatToken;
            existing.RefreshToken = acc.RefreshToken;
            existing.ExpiresAt = acc.ExpiresAt;
            existing.Status = acc.Status;
            existing.Quota = acc.Quota;
            existing.IsQuotaExceeded = acc.IsQuotaExceeded;
            existing.IsDefault = acc.IsDefault;
            existing.UpdatedAt = _time.NowLocal;
            existing.LastUsedAt = acc.LastUsedAt;
            existing.MachineId = acc.MachineId ?? existing.MachineId;
            existing.MachineToken = acc.MachineToken ?? existing.MachineToken;
            existing.MachineType = acc.MachineType ?? existing.MachineType;
            existing.MachineCode = acc.MachineCode ?? existing.MachineCode;
        }
        else
        {
            acc.CreatedAt = _time.NowLocal;
            acc.UpdatedAt = _time.NowLocal;
            db.Accounts.Add(acc);
        }
        db.SaveChanges();
    }

    public void SetDefaultAccount(string accountId)
    {
        using var db = _factory.CreateDbContext();
        var all = db.Accounts.ToList();
        foreach (var a in all)
        {
            if (a.Id == accountId)
            {
                a.IsDefault = true;
                a.Status = "active";
            }
            else
            {
                a.IsDefault = false;
            }
        }
        db.SaveChanges();
    }

    public void ToggleAccountStatus(string accountId)
    {
        using var db = _factory.CreateDbContext();
        var acc = db.Accounts.FirstOrDefault(a => a.Id == accountId);
        if (acc != null)
        {
            acc.Status = acc.Status == "active" ? "disabled" : "active";
            acc.UpdatedAt = _time.NowLocal;
            db.SaveChanges();
        }
    }

    public void DeleteAccount(string accountId)
    {
        using var db = _factory.CreateDbContext();
        var acc = db.Accounts.FirstOrDefault(a => a.Id == accountId);
        if (acc != null)
        {
            db.Accounts.Remove(acc);
            db.SaveChanges();
        }
    }

    public void TouchAccountUsage(string accountId)
    {
        using var db = _factory.CreateDbContext();
        var acc = db.Accounts.FirstOrDefault(a => a.Id == accountId);
        if (acc != null)
        {
            acc.LastUsedAt = _time.NowLocal;
            db.SaveChanges();
        }
    }

    public List<ApiKeyRecord> GetAllKeys()
    {
        using var db = _factory.CreateDbContext();
        return db.ApiKeys
            .AsNoTracking()
            .OrderByDescending(k => k.CreatedAt)
            .ToList();
    }

    public bool ValidateKey(string keyValue)
    {
        using var db = _factory.CreateDbContext();
        var key = db.ApiKeys.FirstOrDefault(k => k.KeyValue == keyValue.Trim() && k.Status == "active");
        if (key != null)
        {
            key.LastUsedAt = _time.NowLocal;
            db.SaveChanges();
            return true;
        }
        return false;
    }

    public ApiKeyRecord? FindActiveKey(string keyValue, bool touch = true)
    {
        using var db = _factory.CreateDbContext();
        var key = db.ApiKeys.FirstOrDefault(k => k.KeyValue == keyValue.Trim() && k.Status == "active");
        if (key is null)
        {
            return null;
        }
        if (touch)
        {
            key.LastUsedAt = _time.NowLocal;
            db.SaveChanges();
        }
        // 分离出上下文，避免调用方误用被跟踪的实体。
        db.Entry(key).State = EntityState.Detached;
        return key;
    }

    public void ClearKeyBindings(string accountId)
    {
        using var db = _factory.CreateDbContext();
        var bound = db.ApiKeys.Where(k => k.AccountId == accountId).ToList();
        if (bound.Count == 0)
        {
            return;
        }
        foreach (var k in bound)
        {
            k.AccountId = null;
        }
        db.SaveChanges();
    }

    public bool HasAnyActiveKeys()
    {
        using var db = _factory.CreateDbContext();
        return db.ApiKeys.Any(k => k.Status == "active");
    }

    public void CreateKey(string name, string? accountId = null, string? customKey = null)
    {
        string key = string.IsNullOrWhiteSpace(customKey) ? "sk-qd-" + Guid.NewGuid().ToString("N") : customKey.Trim();
        string prefix = key.Length >= 9 ? key[..9] + "..." : key;

        using var db = _factory.CreateDbContext();
        db.ApiKeys.Add(new ApiKeyRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(name) ? "API Key" : name.Trim(),
            KeyValue = key,
            KeyPrefix = prefix,
            AccountId = string.IsNullOrWhiteSpace(accountId) ? null : accountId,
            Status = "active",
            CreatedAt = _time.NowLocal
        });
        db.SaveChanges();
    }

    public void ToggleKeyStatus(string keyId)
    {
        using var db = _factory.CreateDbContext();
        var key = db.ApiKeys.FirstOrDefault(k => k.Id == keyId);
        if (key != null)
        {
            key.Status = key.Status == "active" ? "paused" : "active";
            db.SaveChanges();
        }
    }

    public void DeleteKey(string keyId)
    {
        using var db = _factory.CreateDbContext();
        var key = db.ApiKeys.FirstOrDefault(k => k.Id == keyId);
        if (key != null)
        {
            db.ApiKeys.Remove(key);
            db.SaveChanges();
        }
    }


    public void LogUsage(UsageRecord u)
    {
        using var db = _factory.CreateDbContext();
        u.CreatedAt = _time.NowLocal;
        db.UsageRecords.Add(u);
        db.SaveChanges();
    }

    public List<UsageRecord> GetRecentUsage(int limit = 100)
    {
        using var db = _factory.CreateDbContext();
        return db.UsageRecords
            .AsNoTracking()
            .OrderByDescending(u => u.CreatedAt)
            .Take(limit)
            .ToList();
    }

    /// <summary>分页取调用记录。倒序（最新在前），页码从 1 开始。</summary>
    public List<UsageRecord> GetUsagePage(int page, int pageSize, out int total)
    {
        using var db = _factory.CreateDbContext();
        total = db.UsageRecords.Count();
        if (total == 0 || pageSize <= 0)
        {
            return [];
        }

        return db.UsageRecords
            .AsNoTracking()
            .OrderByDescending(u => u.CreatedAt)
            .Skip((Math.Max(page, 1) - 1) * pageSize)
            .Take(pageSize)
            .ToList();
    }

    /// <summary>
    /// 全量用量汇总。统计卡片要覆盖所有记录（不只是当前页）。
    /// 折减成 8 列再在内存里聚合：EF 翻不了「先 Where 再 Average」这种嵌套聚合。
    /// </summary>
    public UsageSummary GetUsageSummary()
    {
        using var db = _factory.CreateDbContext();

        // 延迟到真有数据再查：空表时 EF 不会生成聚合查询，省一次往返。
        List<UsageRecord> rows = db.UsageRecords
            .AsNoTracking()
            .Select(u => new UsageRecord
            {
                PromptTokens = u.PromptTokens,
                CompletionTokens = u.CompletionTokens,
                TotalTokens = u.TotalTokens,
                ReasoningTokens = u.ReasoningTokens,
                CachedTokens = u.CachedTokens,
                Credits = u.Credits,
                LatencyMs = u.LatencyMs,
                FirstTokenMs = u.FirstTokenMs,
                IsStream = u.IsStream,
            })
            .ToList();

        if (rows.Count == 0)
        {
            return new UsageSummary();
        }

        // 只 Select 需要的 8 列，且过滤掉不参与平均的样本在内存里做——
        // EF 翻不了「先 Where 再 Average」这种嵌套聚合。
        var latencies = rows.Where(u => u.LatencyMs > 0).Select(u => (double)u.LatencyMs).ToList();
        var ttfts = rows.Where(u => u.FirstTokenMs > 0).Select(u => (double)u.FirstTokenMs).ToList();

        return new UsageSummary
        {
            Count = rows.Count,
            PromptTokens = rows.Sum(u => (long)u.PromptTokens),
            CompletionTokens = rows.Sum(u => (long)u.CompletionTokens),
            TotalTokens = rows.Sum(u => (long)u.TotalTokens),
            ReasoningTokens = rows.Sum(u => (long)u.ReasoningTokens),
            CachedTokens = rows.Sum(u => (long)u.CachedTokens),
            Credits = rows.Sum(u => u.Credits),
            AvgLatencyMs = latencies.Count > 0 ? latencies.Average() : 0,
            AvgFirstTokenMs = ttfts.Count > 0 ? ttfts.Average() : 0,
            FirstTokenSamples = ttfts.Count,
            FirstTokenStreamSamples = rows.Count(u => u.FirstTokenMs > 0 && u.IsStream == true),
        };
    }

    public void ClearUsageRecords()
    {
        using var db = _factory.CreateDbContext();
        db.UsageRecords.ExecuteDelete();
    }


    public string? GetSetting(string key)
    {
        using var db = _factory.CreateDbContext();
        return db.Settings
            .AsNoTracking()
            .FirstOrDefault(s => s.Key == key)?.Value;
    }

    public void SetSetting(string key, string value)
    {
        using var db = _factory.CreateDbContext();
        var setting = db.Settings.FirstOrDefault(s => s.Key == key);
        if (setting != null)
        {
            setting.Value = value;
        }
        else
        {
            db.Settings.Add(new SettingItem { Key = key, Value = value });
        }
        db.SaveChanges();
    }
    // --- 账号池状态 ---
    // 由后台 flusher 独占写入（见 PoolFlusher），请求路径不碰这里。

    public Dictionary<string, PoolStateRecord> GetAllPoolStates()
    {
        using var db = _factory.CreateDbContext();
        return db.PoolStates
            .AsNoTracking()
            .ToDictionary(s => s.AccountId, StringComparer.Ordinal);
    }

    /// <summary>批量 upsert（单事务）。已存在的整行覆盖，不存在的新增。</summary>
    public void SavePoolStates(IReadOnlyList<PoolStateRecord> states)
    {
        if (states.Count == 0)
        {
            return;
        }

        using var db = _factory.CreateDbContext();
        var ids = states.Select(s => s.AccountId).ToList();
        var existing = db.PoolStates
            .Where(s => ids.Contains(s.AccountId))
            .ToDictionary(s => s.AccountId, StringComparer.Ordinal);

        foreach (var state in states)
        {
            if (existing.TryGetValue(state.AccountId, out var row))
            {
                // SetValues 按映射逐列拷贝，省掉手写 22 个字段赋值。
                db.Entry(row).CurrentValues.SetValues(state);
            }
            else
            {
                db.PoolStates.Add(state);
            }
        }

        db.SaveChanges();
    }

    /// <summary>删除账号时一并清掉池状态，避免残留行被下次同名 id 复用。</summary>
    public void DeletePoolState(string accountId)
    {
        using var db = _factory.CreateDbContext();
        db.PoolStates.Where(s => s.AccountId == accountId).ExecuteDelete();
    }
}
