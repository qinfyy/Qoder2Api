namespace Qoder2Api.Configuration;

public sealed class PoolOptions
{
    public const string SectionName = "Pool";

    /// <summary>单请求最多换几个账号。</summary>
    public int MaxRotate { get; init; } = 3;

    /// <summary>单账号在途上限，0 = 不限。</summary>
    public int MaxInFlightPerAccount { get; init; } = 3;

    /// <summary>连续多少次 5xx 触发熔断。</summary>
    public int BreakerThreshold { get; init; } = 3;

    /// <summary>熔断基础时长，每次熔断翻倍。</summary>
    public TimeSpan BreakerCooldown { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan BreakerCooldownMax { get; init; } = TimeSpan.FromHours(6);

    /// <summary>软冷却退避封顶。上游给重置墙钟时以墙钟为准。</summary>
    public TimeSpan SoftRateMax { get; init; } = TimeSpan.FromHours(2);

    /// <summary>未知错误连败多少次降权。</summary>
    public int DegradeThreshold { get; init; } = 5;

    public TimeSpan DegradeCooldown { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>连续会话失效多少次判定需重新登录。单次不杀号。</summary>
    public int SessionDeadThreshold { get; init; } = 3;

    /// <summary>闲置补偿权重，每小时恢复量。</summary>
    public double IdleWeightPerHour { get; init; } = 0.5;

    public double IdleWeightMax { get; init; } = 5.0;

    /// <summary>加权抽签的短名单大小。</summary>
    public int ShortlistSize { get; init; } = 5;

    /// <summary>在途租约兜底回收时长。</summary>
    public TimeSpan LeaseTtl { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>池状态落盘周期。</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(5);
}
