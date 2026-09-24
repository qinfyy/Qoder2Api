using Microsoft.Extensions.Logging;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using reg.Models;
using reg.Services.Qoder;

namespace reg.Services.Database;

public class AppDbContext : DbContext
{
    public DbSet<AccountRecord> Accounts => Set<AccountRecord>();
    public DbSet<ApiKeyRecord> ApiKeys => Set<ApiKeyRecord>();
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
    public DbSet<SettingItem> Settings => Set<SettingItem>();
    public DbSet<PoolStateRecord> PoolStates => Set<PoolStateRecord>();

    public AppDbContext()
    {
    }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            string saveDir = Path.Combine(Directory.GetCurrentDirectory(), "save");
            if (!Directory.Exists(saveDir))
            {
                Directory.CreateDirectory(saveDir);
            }
            string dbPath = Path.Combine(saveDir, "qoder2api.db");
            // 与 Program.cs 的连接串保持一致（含 Default Timeout=5，见那里的说明）。
            optionsBuilder.UseSqlite($"Data Source={dbPath};Default Timeout=5");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AccountRecord>(entity =>
        {
            entity.ToTable("accounts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PlanName).HasDefaultValue("Pro");
            entity.Property(e => e.AuthMethod).HasDefaultValue("device");
            entity.Property(e => e.Status).HasDefaultValue("active");
        });

        modelBuilder.Entity<ApiKeyRecord>(entity =>
        {
            entity.ToTable("proxy_api_keys");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.KeyValue).IsUnique();
        });

        modelBuilder.Entity<UsageRecord>(entity =>
        {
            entity.ToTable("usage_records");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.CreatedAt);
        });

        modelBuilder.Entity<SettingItem>(entity =>
        {
            entity.ToTable("settings");
            entity.HasKey(e => e.Key);
        });

        modelBuilder.Entity<PoolStateRecord>(entity =>
        {
            entity.ToTable("account_pool_states");
            entity.HasKey(e => e.AccountId);

            // 列名显式映射为 snake_case：与既有库的物理列名保持一致，
            // 改属性名时不会连带改表结构。
            entity.Property(e => e.AccountId).HasColumnName("account_id");
            entity.Property(e => e.Disabled).HasColumnName("disabled");
            entity.Property(e => e.DisabledReason).HasColumnName("disabled_reason");
            entity.Property(e => e.NeedsRelogin).HasColumnName("needs_relogin");
            entity.Property(e => e.NeedsReloginReason).HasColumnName("needs_relogin_reason");
            entity.Property(e => e.CoolUntilMs).HasColumnName("cool_until_ms");
            entity.Property(e => e.CoolKind).HasColumnName("cool_kind");
            entity.Property(e => e.CoolReason).HasColumnName("cool_reason");
            entity.Property(e => e.BreakerUntilMs).HasColumnName("breaker_until_ms");
            entity.Property(e => e.BreakerFails).HasColumnName("breaker_fails");
            entity.Property(e => e.BreakerRetryCount).HasColumnName("breaker_retry_count");
            entity.Property(e => e.DegradeUntilMs).HasColumnName("degrade_until_ms");
            entity.Property(e => e.ConsecutiveFails).HasColumnName("consecutive_fails");
            entity.Property(e => e.SoftStreak).HasColumnName("soft_streak");
            entity.Property(e => e.SessionDeadFails).HasColumnName("session_dead_fails");
            entity.Property(e => e.SuccessCount).HasColumnName("success_count");
            entity.Property(e => e.ErrTotal).HasColumnName("err_total");
            entity.Property(e => e.SuccessEma).HasColumnName("success_ema");
            entity.Property(e => e.LastSuccessMs).HasColumnName("last_success_ms");
            entity.Property(e => e.LastErrMs).HasColumnName("last_err_ms");
            entity.Property(e => e.ModelCooldownsJson).HasColumnName("model_cooldowns_json");
            entity.Property(e => e.UpdatedAtMs).HasColumnName("updated_at_ms");
        });
    }

    public static void InitializeDatabase(AppDbContext db, ILogger? log = null)
    {
        string saveDir = Path.Combine(Directory.GetCurrentDirectory(), "save");
        if (!Directory.Exists(saveDir))
        {
            Directory.CreateDirectory(saveDir);
        }

        db.Database.EnsureCreated();

        // 幂等 schema 补丁（WAL / 池状态表 / usage 新列）。
        // 必须在任何 EF 查询之前——EnsureCreated 对已存在的库不建新表也不加列。
        SchemaPatches.Apply(db, log);

        // Migrate existing qoder_auth_config.json if accounts is empty
        if (!db.Accounts.Any())
        {
            string[] candidatePaths =
            [
                Path.Combine(saveDir, "qoder_auth_config.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "qoder_auth_config.json"),
                Path.Combine(AppContext.BaseDirectory, "qoder_auth_config.json")
            ];

            string? existingFile = candidatePaths.FirstOrDefault(File.Exists);
            if (existingFile != null)
            {
                try
                {
                    string json = File.ReadAllText(existingFile);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    string? uid = root.TryGetProperty("UserId", out var u) ? u.GetString() : null;
                    string? jt = root.TryGetProperty("JobToken", out var j) ? j.GetString() : null;
                    string? name = root.TryGetProperty("UserName", out var n) ? n.GetString() : "Qoder User";
                    string? email = root.TryGetProperty("UserEmail", out var em) ? em.GetString() : "";
                    string? plan = root.TryGetProperty("PlanName", out var p) ? p.GetString() : "Pro";
                    string? method = root.TryGetProperty("AuthMethod", out var am) ? am.GetString() : "device";
                    string? dt = root.TryGetProperty("DeviceToken", out var d) ? d.GetString() : null;
                    string? pat = root.TryGetProperty("PatToken", out var pt) ? pt.GetString() : null;
                    string? localKey = root.TryGetProperty("LocalProxyApiKey", out var lk) ? lk.GetString() : null;

                    if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(jt))
                    {
                        var acc = new AccountRecord
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            UserId = uid,
                            UserName = name ?? "Qoder User",
                            UserEmail = email ?? "",
                            PlanName = plan ?? "Pro",
                            AuthMethod = method ?? "device",
                            JobToken = jt,
                            DeviceToken = dt,
                            PatToken = pat,
                            Status = "active",
                            IsDefault = true,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        };
                        db.Accounts.Add(acc);
                        db.SaveChanges();

                        if (!string.IsNullOrEmpty(localKey))
                        {
                            var key = new ApiKeyRecord
                            {
                                Id = Guid.NewGuid().ToString("N"),
                                Name = "Default Key",
                                KeyValue = localKey,
                                KeyPrefix = localKey.Length >= 8 ? localKey[..8] + "..." : localKey,
                                Status = "active",
                                CreatedAt = DateTime.UtcNow
                            };
                            db.ApiKeys.Add(key);
                            db.SaveChanges();
                        }
                    }
                }
                catch (Exception ex)
                {
                    log?.LogWarning(ex, "迁移遗留 qoder_auth_config.json 时出错，已跳过");
                }
            }
        }
    }
}
