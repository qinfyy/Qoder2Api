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

    public string? UserPhone { get; set; }
    public string PlanName { get; set; } = QoderConstants.UnknownPlan;
    public string AuthMethod { get; set; } = "device"; // device, pat

    public string? Region { get; set; }

    public string? MachineId { get; set; }

    public string? MachineToken { get; set; }
    public string? MachineType { get; set; }

    public string? MachineCode { get; set; }

    public string? JobToken { get; set; }
    public string? DeviceToken { get; set; }
    public string? PatToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string Status { get; set; } = "active"; // active, disabled
    public double Quota { get; set; }
    public bool IsQuotaExceeded { get; set; }
    public bool IsDefault { get; set; } = false;
    public DateTime CreatedAt { get; set; } = default;
    public DateTime UpdatedAt { get; set; } = default;
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
    public DateTime CreatedAt { get; set; } = default;
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
    public int CachedTokens { get; set; }
    public double Credits { get; set; }
    public string? UsageSource { get; set; }
    public bool? IsStream { get; set; }
    public string? AccountUid { get; set; }
    public long FirstTokenMs { get; set; }

    public long LatencyMs { get; set; }
    public int HttpStatus { get; set; } = 200;
    public string Status { get; set; } = "success";
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = default;
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
