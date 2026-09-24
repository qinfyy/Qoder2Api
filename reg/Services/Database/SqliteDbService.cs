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

    public Dictionary<string, PoolStateRecord> GetAllPoolStates()
    {
        var result = new Dictionary<string, PoolStateRecord>(StringComparer.Ordinal);
        using var db = _factory.CreateDbContext();
        var conn = db.Database.GetDbConnection();
        bool opened = conn.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            conn.Open();
        }
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT account_id, disabled, disabled_reason, needs_relogin, needs_relogin_reason,
                       cool_until_ms, cool_kind, cool_reason, breaker_until_ms, breaker_fails,
                       breaker_retry_count, degrade_until_ms, consecutive_fails, soft_streak,
                       session_dead_fails, success_count, err_total, success_ema,
                       last_success_ms, last_err_ms, model_cooldowns_json, updated_at_ms
                FROM account_pool_states;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var rec = new PoolStateRecord
                {
                    AccountId = r.GetString(0),
                    Disabled = r.GetInt32(1) != 0,
                    DisabledReason = r.IsDBNull(2) ? null : r.GetString(2),
                    NeedsRelogin = r.GetInt32(3) != 0,
                    NeedsReloginReason = r.IsDBNull(4) ? null : r.GetString(4),
                    CoolUntilMs = r.IsDBNull(5) ? null : r.GetInt64(5),
                    CoolKind = r.GetInt32(6),
                    CoolReason = r.IsDBNull(7) ? null : r.GetString(7),
                    BreakerUntilMs = r.IsDBNull(8) ? null : r.GetInt64(8),
                    BreakerFails = r.GetInt32(9),
                    BreakerRetryCount = r.GetInt32(10),
                    DegradeUntilMs = r.IsDBNull(11) ? null : r.GetInt64(11),
                    ConsecutiveFails = r.GetInt32(12),
                    SoftStreak = r.GetInt32(13),
                    SessionDeadFails = r.GetInt32(14),
                    SuccessCount = r.GetInt64(15),
                    ErrTotal = r.GetInt64(16),
                    SuccessEma = r.GetDouble(17),
                    LastSuccessMs = r.IsDBNull(18) ? null : r.GetInt64(18),
                    LastErrMs = r.IsDBNull(19) ? null : r.GetInt64(19),
                    ModelCooldownsJson = r.IsDBNull(20) ? null : r.GetString(20),
                    UpdatedAtMs = r.GetInt64(21),
                };
                result[rec.AccountId] = rec;
            }
        }
        finally
        {
            if (opened)
            {
                conn.Close();
            }
        }
        return result;
    }

    public void SavePoolStates(IReadOnlyList<PoolStateRecord> states)
    {
        if (states.Count == 0)
        {
            return;
        }
        using var db = _factory.CreateDbContext();
        var conn = db.Database.GetDbConnection();
        bool opened = conn.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            conn.Open();
        }
        try
        {
            // 转成 Sqlite* 具体类型：DbCommand.Parameters 是 DbParameterCollection，
            // 其 Add 走 IList.Add 返回 int；SqliteParameterCollection.Add(SqliteParameter)
            // 才返回参数对象本身。Transaction 属性同理要求 SqliteTransaction。
            using var sqliteConn = (Microsoft.Data.Sqlite.SqliteConnection)conn;
            using var tx = sqliteConn.BeginTransaction();
            using var cmd = sqliteConn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO account_pool_states (
                    account_id, disabled, disabled_reason, needs_relogin, needs_relogin_reason,
                    cool_until_ms, cool_kind, cool_reason, breaker_until_ms, breaker_fails,
                    breaker_retry_count, degrade_until_ms, consecutive_fails, soft_streak,
                    session_dead_fails, success_count, err_total, success_ema,
                    last_success_ms, last_err_ms, model_cooldowns_json, updated_at_ms
                ) VALUES (
                    $id, $disabled, $disReason, $needsRelogin, $nrReason,
                    $coolUntil, $coolKind, $coolReason, $breakerUntil, $breakerFails,
                    $retryCount, $degradeUntil, $consecFails, $softStreak,
                    $deadFails, $success, $errTotal, $ema,
                    $lastSuccess, $lastErr, $modelJson, $updatedAt
                );
                """;

            // SQLite 是动态类型，参数无需声明 SqliteType——按名字占位、逐行赋 Value 即可。
            // 复用同一组参数对象（只改 Value）比每行重建命令快得多。
            var p = cmd.Parameters;
            Microsoft.Data.Sqlite.SqliteParameter Param(string name) =>
                (Microsoft.Data.Sqlite.SqliteParameter)p.Add(new Microsoft.Data.Sqlite.SqliteParameter { ParameterName = name });

            var pId = Param("$id");
            var pDisabled = Param("$disabled");
            var pDisReason = Param("$disReason");
            var pNeedsRelogin = Param("$needsRelogin");
            var pNrReason = Param("$nrReason");
            var pCoolUntil = Param("$coolUntil");
            var pCoolKind = Param("$coolKind");
            var pCoolReason = Param("$coolReason");
            var pBreakerUntil = Param("$breakerUntil");
            var pBreakerFails = Param("$breakerFails");
            var pRetryCount = Param("$retryCount");
            var pDegradeUntil = Param("$degradeUntil");
            var pConsecFails = Param("$consecFails");
            var pSoftStreak = Param("$softStreak");
            var pDeadFails = Param("$deadFails");
            var pSuccess = Param("$success");
            var pErrTotal = Param("$errTotal");
            var pEma = Param("$ema");
            var pLastSuccess = Param("$lastSuccess");
            var pLastErr = Param("$lastErr");
            var pModelJson = Param("$modelJson");
            var pUpdatedAt = Param("$updatedAt");

            foreach (var s in states)
            {
                pId.Value = s.AccountId;
                pDisabled.Value = s.Disabled ? 1 : 0;
                pDisReason.Value = (object?)s.DisabledReason ?? DBNull.Value;
                pNeedsRelogin.Value = s.NeedsRelogin ? 1 : 0;
                pNrReason.Value = (object?)s.NeedsReloginReason ?? DBNull.Value;
                pCoolUntil.Value = (object?)s.CoolUntilMs ?? DBNull.Value;
                pCoolKind.Value = s.CoolKind;
                pCoolReason.Value = (object?)s.CoolReason ?? DBNull.Value;
                pBreakerUntil.Value = (object?)s.BreakerUntilMs ?? DBNull.Value;
                pBreakerFails.Value = s.BreakerFails;
                pRetryCount.Value = s.BreakerRetryCount;
                pDegradeUntil.Value = (object?)s.DegradeUntilMs ?? DBNull.Value;
                pConsecFails.Value = s.ConsecutiveFails;
                pSoftStreak.Value = s.SoftStreak;
                pDeadFails.Value = s.SessionDeadFails;
                pSuccess.Value = s.SuccessCount;
                pErrTotal.Value = s.ErrTotal;
                pEma.Value = s.SuccessEma;
                pLastSuccess.Value = (object?)s.LastSuccessMs ?? DBNull.Value;
                pLastErr.Value = (object?)s.LastErrMs ?? DBNull.Value;
                pModelJson.Value = (object?)s.ModelCooldownsJson ?? DBNull.Value;
                pUpdatedAt.Value = s.UpdatedAtMs;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        finally
        {
            if (opened)
            {
                conn.Close();
            }
        }
    }

    public void DeletePoolState(string accountId)
    {
        using var db = _factory.CreateDbContext();
        db.Database.ExecuteSqlRaw("DELETE FROM account_pool_states WHERE account_id = {0};", accountId);
    }
}
