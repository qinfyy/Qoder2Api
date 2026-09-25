namespace Qoder2Api.Configuration;

/// <summary>模型排队策略，对应 appsettings.xml 的 &lt;Queue&gt; 节。默认值对齐官方客户端。</summary>
public sealed class QueueOptions
{
    public const string SectionName = "Queue";

    /// <summary>true = 优先排队等待，false = 直接换号。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>单个请求最长排队等待。</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromHours(1);

    /// <summary>同一账号最多做几次「排队→重试」。排队不计入换号次数。</summary>
    public int MaxRecoveries { get; init; } = 10;

    /// <summary>无 retryAfterSeconds 时的轮询间隔。</summary>
    public TimeSpan DefaultPollInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MinPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan MaxPollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>连续轮询失败上限，超过则放弃排队改换号。</summary>
    public int MaxConsecutivePollFailures { get; init; } = 3;

    /// <summary>单次排队状态查询超时。</summary>
    public TimeSpan PollRequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(10);
}
