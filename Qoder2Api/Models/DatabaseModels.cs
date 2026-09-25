using System.ComponentModel.DataAnnotations;
using Qoder2Api.Services.Qoder; // QoderConstants.UnknownPlan

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
    public string PlanName { get; set; } = QoderConstants.UnknownPlan;
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

    /// <summary>
    /// 请求是否为流式（下游 <c>stream=true</c>）。
    ///
    /// **可空**：本字段加入之前的历史记录是 NULL，显示为「—」——它们既可能是流式
    /// 也可能不是，不能默认成「否」——那是在编造数据。
    ///
    /// 与首字延迟配合看：只有流式请求的 FirstTokenMs 才是下游真实体感的首字延迟，
    /// 非流式请求的客户端要等全部聚合完才收到内容。
    /// </summary>
    public bool? IsStream { get; set; }

    /// <summary>本次请求实际使用的账号（多账号轮换下用于归因）。</summary>
    public string? AccountUid { get; set; }

    /// <summary>
    /// 首字延迟（毫秒）：从请求进入到**首个内容 token 到达**的实测耗时。
    ///
    /// **含排队等待与换号重试**——刻意如此：这是下游真实感受到的首字延迟。
    /// 纯用量帧不计入；请求在产出内容前就失败/被取消时为 0（显示为 —）。
    /// 上游自报的 firstTokenDuration 只在本地没打上点时兜底。
    /// </summary>
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
