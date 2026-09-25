using System.ComponentModel.DataAnnotations;

namespace Qoder2Api.Models;

public class AccountRecord
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? UserEmail { get; set; }

    /// <summary>
    /// 账号绑定的手机号。取自 <c>/api/v1/userinfo</c> 的 <c>security_mobile</c>
    /// 字段——官方客户端就是这么取的（CN 客户端 fetchUser），不需要走短信/验证码那套。
    ///
    /// **国内版账号通常没有邮箱**（手机号注册），列表页在邮箱为空且是国内版时
    /// 改显示这个字段。国际版账号多为 null。
    /// </summary>
    public string? UserPhone { get; set; }
    public string PlanName { get; set; } = "Pro";
    public string AuthMethod { get; set; } = "device"; // device, pat

    /// <summary>
    /// 账号所属区域："global"（国际版）/ "cn"（国内版）。
    /// **可空**——老库里的账号没有这一列，NULL 一律按国际版处理，保证升级后行为不变。
    /// 由 QoderEndpoints.ParseRegion 解析。
    /// </summary>
    public string? Region { get; set; }
    public string? JobToken { get; set; }
    public string? DeviceToken { get; set; }
    public string? PatToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string Status { get; set; } = "active"; // active, disabled
    public double Quota { get; set; }
    public bool IsQuotaExceeded { get; set; }
    public bool IsDefault { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
}

public class ApiKeyRecord
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Default Key";
    public string KeyValue { get; set; } = "";
    public string KeyPrefix { get; set; } = "";
    public string? AccountId { get; set; }
    public string Status { get; set; } = "active"; // active, paused
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
}

public class UsageRecord
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Model { get; set; } = "auto";
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public int ReasoningTokens { get; set; }

    /// <summary>命中提示缓存的 token 数（上游 prompt_tokens_details.cached_tokens）。</summary>
    public int CachedTokens { get; set; }

    /// <summary>本次请求的实际扣费（Qoder 私有的 usage.credits）。</summary>
    public double Credits { get; set; }

    /// <summary>
    /// 用量来源：upstream（上游实测）/ estimated（本地估算）。
    /// **必须区分**——估算值与实测值混在一起会让统计失去意义。
    /// </summary>
    public string? UsageSource { get; set; }

    /// <summary>本次请求实际使用的账号（多账号轮换下用于归因）。</summary>
    public string? AccountUid { get; set; }

    /// <summary>首 token 耗时（毫秒，来自上游 event:finish 的 firstTokenDuration）。</summary>
    public long FirstTokenMs { get; set; }

    public long LatencyMs { get; set; }
    public int HttpStatus { get; set; } = 200;

    /// <summary>success / error / cancelled（客户端断连，不是账号或上游的问题）。</summary>
    public string Status { get; set; } = "success";

    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class SettingItem
{
    [Key]
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public class SystemSettings
{
    public bool RequireApiKey { get; set; } = false;
    public string? DefaultModel { get; set; } = "auto";
    public string? MachineId { get; set; }
}
