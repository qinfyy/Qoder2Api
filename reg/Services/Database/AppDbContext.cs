using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using reg.Models;

namespace reg.Services.Database;

public class AppDbContext : DbContext
{
    public DbSet<AccountRecord> Accounts => Set<AccountRecord>();
    public DbSet<ApiKeyRecord> ApiKeys => Set<ApiKeyRecord>();
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
    public DbSet<SettingItem> Settings => Set<SettingItem>();

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
    }

    public static void InitializeDatabase(AppDbContext db)
    {
        string saveDir = Path.Combine(Directory.GetCurrentDirectory(), "save");
        if (!Directory.Exists(saveDir))
        {
            Directory.CreateDirectory(saveDir);
        }

        db.Database.EnsureCreated();

        // 幂等 schema 补丁（WAL / 池状态表 / usage 新列）。
        // 必须在任何 EF 查询之前——EnsureCreated 对已存在的库不建新表也不加列。
        SchemaPatches.Apply(db);

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
                    Console.WriteLine($"[AppDbContext] Initial migration warning: {ex.Message}");
                }
            }
        }
    }
}
