using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using reg.Models;
using reg.Services.Qoder;

namespace reg.Services.Database;

public class SqliteDbService
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SqliteDbService(IDbContextFactory<AppDbContext> factory, ILogger<SqliteDbService> log)
    {
        _factory = factory;
        using var db = _factory.CreateDbContext();
        AppDbContext.InitializeDatabase(db, log);
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
            existing.UpdatedAt = DateTime.UtcNow;
            existing.LastUsedAt = acc.LastUsedAt;
        }
        else
        {
            acc.CreatedAt = DateTime.UtcNow;
            acc.UpdatedAt = DateTime.UtcNow;
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
            acc.UpdatedAt = DateTime.UtcNow;
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
            acc.LastUsedAt = DateTime.UtcNow;
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
            key.LastUsedAt = DateTime.UtcNow;
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
            key.LastUsedAt = DateTime.UtcNow;
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
            CreatedAt = DateTime.UtcNow
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
