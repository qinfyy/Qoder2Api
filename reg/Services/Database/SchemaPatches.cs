using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace reg.Services.Database;

public static class SchemaPatches
{
    public static void Apply(AppDbContext db, ILogger? log = null)
    {
        ApplyPragmas(db, log);
        CreatePoolStateTable(db);
        AddUsageColumns(db);
    }

    private static void ApplyPragmas(AppDbContext db, ILogger? log)
    {
        try
        {
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            db.Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");
        }
        catch (Exception ex)
        {
            // 不阻断启动：极端情况下（如只读介质）退化为默认模式仍可运行。
            log?.LogWarning(ex, "PRAGMA 设置失败，将使用默认 journal 模式");
        }
    }

    private static void CreatePoolStateTable(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS account_pool_states (
                account_id           TEXT    NOT NULL PRIMARY KEY,
                disabled             INTEGER NOT NULL DEFAULT 0,
                disabled_reason      TEXT    NULL,
                needs_relogin        INTEGER NOT NULL DEFAULT 0,
                needs_relogin_reason TEXT    NULL,
                cool_until_ms        INTEGER NULL,
                cool_kind            INTEGER NOT NULL DEFAULT 0,
                cool_reason          TEXT    NULL,
                breaker_until_ms     INTEGER NULL,
                breaker_fails        INTEGER NOT NULL DEFAULT 0,
                breaker_retry_count  INTEGER NOT NULL DEFAULT 0,
                degrade_until_ms     INTEGER NULL,
                consecutive_fails    INTEGER NOT NULL DEFAULT 0,
                soft_streak          INTEGER NOT NULL DEFAULT 0,
                session_dead_fails   INTEGER NOT NULL DEFAULT 0,
                success_count        INTEGER NOT NULL DEFAULT 0,
                err_total            INTEGER NOT NULL DEFAULT 0,
                success_ema          REAL    NOT NULL DEFAULT 0.5,
                last_success_ms      INTEGER NULL,
                last_err_ms          INTEGER NULL,
                model_cooldowns_json TEXT    NULL,
                updated_at_ms        INTEGER NOT NULL DEFAULT 0
            );
            """);
    }

    private static void AddUsageColumns(AppDbContext db)
    {
        var existing = ReadColumnNames(db, "usage_records");
        if (existing.Count == 0)
        {
            return; // 表还不存在（EnsureCreated 失败等），跳过
        }

        // (列名, 定义) —— 新增字段一律追加在此，历史条目按默认值回填。
        var wanted = new (string Name, string Ddl)[]
        {
            ("CachedTokens", "INTEGER NOT NULL DEFAULT 0"),
            ("Credits", "REAL NOT NULL DEFAULT 0"),
            ("UsageSource", "TEXT NULL"),
            ("AccountUid", "TEXT NULL"),
            ("FirstTokenMs", "INTEGER NOT NULL DEFAULT 0"),
        };

        foreach (var (name, ddl) in wanted)
        {
            if (existing.Contains(name))
            {
                continue;
            }
            // EF1002 的插值告警在此不适用：name/ddl 全部来自上方编译期常量数组，
            // 不含任何外部输入。SQLite 的 ALTER TABLE 也不支持参数化列名，
            // 没有"改用 ExecuteSql"的替代方案。
#pragma warning disable EF1002
            db.Database.ExecuteSqlRaw($"ALTER TABLE usage_records ADD COLUMN {name} {ddl};");
#pragma warning restore EF1002
        }
    }

    /// <summary>读取表的现有列名（大小写不敏感比较，用 OrdinalIgnoreCase 集合）。</summary>
    private static HashSet<string> ReadColumnNames(AppDbContext db, string table)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conn = db.Database.GetDbConnection();
        bool opened = conn.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            conn.Open();
        }
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table});";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // PRAGMA table_info 的列序：cid, name, type, notnull, dflt_value, pk
                names.Add(reader.GetString(1));
            }
        }
        finally
        {
            if (opened)
            {
                conn.Close();
            }
        }
        return names;
    }
}
