using Microsoft.EntityFrameworkCore;

namespace reg.Services.Database;

/// <summary>
/// 启动期的幂等 schema 补丁。
///
/// 为什么必须手写而不用 EF：<c>EnsureCreated()</c> 在数据库已存在时**什么都不做**
/// （它只看库文件在不在，不比对表结构），所以新增表/新增列对老库完全无效。而引入
/// EF Migrations 又要处理「已有 EnsureCreated 库没有 __EFMigrationsHistory」的
/// baseline 问题，成本远高于收益。
///
/// 纪律：本文件是**唯一**的 schema 变更入口。所有语句必须幂等（CREATE TABLE
/// IF NOT EXISTS / 先查 PRAGMA 再加列），可以安全地在每次启动时重复执行。
/// 调用点在 <see cref="AppDbContext.InitializeDatabase"/>，且必须在任何 EF 查询之前。
/// </summary>
public static class SchemaPatches
{
    public static void Apply(AppDbContext db)
    {
        ApplyPragmas(db);
        CreatePoolStateTable(db);
        AddUsageColumns(db);
    }

    /// <summary>
    /// WAL 模式：读不阻塞写、写不阻塞读。号池的后台落盘与 UI 的用量查询会并发访问，
    /// 默认的 rollback journal 下两者严格互斥，UI 轮询会卡住池的 flush。
    /// journal_mode 是**库文件级持久属性**，设一次永久生效；重复执行无害。
    /// synchronous=NORMAL 是 WAL 下的官方推荐档位（兼顾安全与吞吐）。
    /// </summary>
    private static void ApplyPragmas(AppDbContext db)
    {
        try
        {
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            db.Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");
        }
        catch (Exception ex)
        {
            // 不阻断启动：极端情况下（如只读介质）退化为默认模式仍可运行。
            Console.WriteLine($"[SchemaPatches] PRAGMA 设置失败（将使用默认 journal 模式）: {ex.Message}");
        }
    }

    /// <summary>
    /// 账号池状态表。**刻意不进 EF 模型**：
    ///   - 高频小表，绕过 EF 变更跟踪更省；
    ///   - 只有一处 DDL，不存在「EF 生成的列类型」与「手写 DDL」两套表示漂移的风险；
    ///   - 时间一律存 Unix 毫秒整数，避开 DateTimeOffset 在 SQLite 上的映射坑。
    ///
    /// 与 accounts.Status 的分工：Status 是**管理员**手动启停；本表的 disabled 是
    /// **池自动**禁用（连续会话失效）。两者都为真才影响可选性，语义不重叠。
    /// </summary>
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

    /// <summary>
    /// usage_records 的新列。SQLite **不支持** <c>ALTER TABLE ... ADD COLUMN IF NOT
    /// EXISTS</c>，只能先 PRAGMA table_info 查一遍再决定加不加。
    /// 默认值用常量（SQLite 的 ADD COLUMN 不接受非常量默认值）。
    /// </summary>
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
