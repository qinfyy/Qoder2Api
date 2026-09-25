using System.Text.Json;
using Qoder2Api.Models;

namespace Qoder2Api.Services.Qoder;

public static class QoderAccountImporter
{
    public readonly record struct EntryResult(
        string UserId,
        string? UserName,
        string Outcome,      // 新增 / 更新 / 跳过
        string? Detail);

    public sealed record ImportResult(
        int Added,
        int Updated,
        int Skipped,
        List<EntryResult> Entries)
    {
        public int Total => Added + Updated + Skipped;
    }

    public static List<AccountRecord> Parse(string json, QoderRegion region)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 顶层：裸数组，或 {accounts:[...]}。
        var array = root.ValueKind == JsonValueKind.Array
            ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("accounts", out var accountsEl)
                && accountsEl.ValueKind == JsonValueKind.Array
                ? accountsEl
                : throw new FormatException(
                    "无法识别的文件结构：顶层应为数组，或含 accounts 数组的对象。");

        var result = new List<AccountRecord>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var acc = ParseOne(item, region);
            if (acc is not null) result.Add(acc);
        }
        return result;
    }

    private static AccountRecord? ParseOne(JsonElement item, QoderRegion region)
    {
        // 原样保存的官方客户端用户信息，是唯一可靠的凭证来源。
        JsonElement info = default;
        bool hasInfo = item.TryGetProperty("auth_user_info_raw", out var r)
                       && r.ValueKind == JsonValueKind.Object;
        if (hasInfo) info = r;

        string? token = hasInfo ? Str(info, "token") : null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return null; // 没有 jobToken 的条目没法用，直接丢弃
        }

        // 取值优先级：auth_user_info_raw > 顶层字段。
        string? userId = hasInfo ? Str(info, "id") : null;
        if (string.IsNullOrWhiteSpace(userId)) userId = Str(item, "user_id");

        string? name = hasInfo ? Str(info, "name") : null;
        if (string.IsNullOrWhiteSpace(name)) name = Str(item, "display_name");

        string? email = hasInfo ? Str(info, "email") : null;
        if (string.IsNullOrWhiteSpace(email)) email = Str(item, "email");

        var acc = new AccountRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            UserName = string.IsNullOrWhiteSpace(name) ? "Qoder 导入用户" : name,
            UserEmail = email ?? "",
            UserPhone = hasInfo ? Str(info, "security_mobile") : null,
            AuthMethod = "import",
            Region = QoderEndpoints.ToStorageValue(region),
            JobToken = token,
            DeviceToken = null,
            RefreshToken = hasInfo ? Str(info, "refreshToken") : null,
            Status = "active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        if (hasInfo && LongOf(info, "expireTime") is long ms && ms > 0)
        {
            acc.ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }

        acc.PlanName = PlanOf(item, info);
        if (hasInfo && info.TryGetProperty("quota", out var q) && q.ValueKind == JsonValueKind.Number)
        {
            acc.Quota = q.TryGetDouble(out var d) ? d : 0;
        }
        if (hasInfo && info.TryGetProperty("isQuotaExceeded", out var ex))
        {
            acc.IsQuotaExceeded = ex.ValueKind == JsonValueKind.True;
        }

        return acc;
    }

    private static string PlanOf(JsonElement item, JsonElement info)
    {
        string? p = Str(item, "plan_type");
        if (string.IsNullOrWhiteSpace(p) && info.ValueKind == JsonValueKind.Object)
        {
            p = Str(info, "userTag");
        }
        return string.IsNullOrWhiteSpace(p) ? QoderConstants.UnknownPlan : p;
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static long? LongOf(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var n) ? n : null,
            JsonValueKind.String => long.TryParse(v.GetString(), out var s) ? s : null,
            _ => null,
        };
    }
}
