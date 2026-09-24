using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Qoder2Api.Models;
using Qoder2Api.Services.Qoder;

namespace Qoder2Api.Services.Database;

public static class SchemaPatches
{
    public static void Apply(AppDbContext db, ILogger? log = null)
    {
        ApplyPragmas(db, log);
        EnsureTable<PoolStateRecord>(db, log);
        SyncMissingColumns<UsageRecord>(db, log);
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
            // 不阻断启动：只读介质等场景退化为默认 journal 模式仍可运行。
            log?.LogWarning(ex, "PRAGMA 设置失败，将使用默认 journal 模式");
        }
    }

    private static void EnsureTable<TEntity>(AppDbContext db, ILogger? log)
    {
        var entityType = db.Model.FindEntityType(typeof(TEntity));
        if (entityType is null)
        {
            return;
        }

        string table = entityType.GetTableName()!;
        if (TableExists(db, table))
        {
            return;
        }

        db.Database.ExecuteSqlRaw(BuildCreateTable(entityType, table));
        log?.LogInformation("已按模型补建表 {Table}", table);
    }

    private static void SyncMissingColumns<TEntity>(AppDbContext db, ILogger? log)
    {
        var entityType = db.Model.FindEntityType(typeof(TEntity));
        if (entityType is null)
        {
            return;
        }

        string table = entityType.GetTableName()!;
        var existing = ReadColumnNames(db, table);
        if (existing.Count == 0)
        {
            return; // 表还不存在，EnsureCreated 会整表建好
        }

        var store = StoreObjectIdentifier.Table(table, entityType.GetSchema());
        foreach (var prop in entityType.GetProperties())
        {
            string column = prop.GetColumnName(store) ?? prop.Name;
            if (existing.Contains(column))
            {
                continue;
            }

            string ddl = BuildAddColumn(prop, column, store);
#pragma warning disable EF1002
            db.Database.ExecuteSqlRaw($"ALTER TABLE {table} ADD COLUMN {ddl};");
#pragma warning restore EF1002
            log?.LogInformation("已给 {Table} 补列 {Column}", table, column);
        }
    }

    private static string BuildCreateTable(IEntityType entityType, string table)
    {
        var store = StoreObjectIdentifier.Table(table, entityType.GetSchema());
        var columns = entityType.GetProperties().Select(p =>
        {
            var sb = new StringBuilder("    ");
            sb.Append(p.GetColumnName(store)).Append(' ').Append(ColumnType(p, store));
            if (!p.IsNullable)
            {
                sb.Append(" NOT NULL");
            }
            if (p.IsPrimaryKey())
            {
                sb.Append(" PRIMARY KEY");
            }
            return sb.ToString();
        });

        return $"CREATE TABLE IF NOT EXISTS {table} (\n{string.Join(",\n", columns)}\n);";
    }

    private static string BuildAddColumn(IProperty prop, string column, StoreObjectIdentifier store)
    {
        var sb = new StringBuilder();
        sb.Append(column).Append(' ').Append(ColumnType(prop, store));
        if (!prop.IsNullable)
        {
            // 值类型（int/long/bool/double）的 CLR 默认值都是 0，与 EF 读缺失列的语义一致。
            sb.Append(" NOT NULL DEFAULT 0");
        }
        return sb.ToString();
    }

    private static string ColumnType(IProperty prop, StoreObjectIdentifier store) =>
        prop.GetColumnType(store) ?? prop.ClrType switch
        {
            var t when t == typeof(int) || t == typeof(long) || t == typeof(bool) => "INTEGER",
            var t when t == typeof(double) || t == typeof(float) || t == typeof(decimal) => "REAL",
            _ => "TEXT",
        };

    private static bool TableExists(AppDbContext db, string table)
    {
        var conn = db.Database.GetDbConnection();
        bool opened = conn.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            conn.Open();
        }
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
            var p = cmd.CreateParameter();
            p.ParameterName = "$name";
            p.Value = table;
            cmd.Parameters.Add(p);
            return cmd.ExecuteScalar() is not null;
        }
        finally
        {
            if (opened)
            {
                conn.Close();
            }
        }
    }

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
