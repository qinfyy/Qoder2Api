namespace Qoder2Api.Services.Qoder;

/// <summary>
/// 排队进度快照。只带官方 queue/status 里对下游有意义的几个字段，
/// 让观察者不必依赖完整的 QoderQueueContext。
/// </summary>
public readonly record struct QueueProgress(
    string? QueueType,
    int? QueueCount,
    int? WaitTimeSeconds,
    bool? ServiceAvailable);

/// <summary>
/// 排队等待期间的可选观察者。官方客户端在排队时会把 model_queue_status 事件推进
/// 消息流，让 UI 显示「排队中」；本代理没有这条 UI 通道，但可以借此在等待期间向下游
/// 发 SSE 保活，避免动辄几分钟到一小时的静默把下游客户端读超时拖死。
/// </summary>
public interface IQueueWaitObserver
{
    /// <summary>每次成功拿到排队状态后调用。</summary>
    Task OnPollAsync(QueueProgress progress, TimeSpan waited, int pollCount, CancellationToken ct);

    /// <summary>
    /// 执行一段等待。返回 false 表示被取消（与 Task.Delay 的取消语义对齐）。
    /// 观察者可以把自己的等待切成小片，在片与片之间顺带发保活。
    /// </summary>
    Task<bool> DelayAsync(TimeSpan duration, CancellationToken ct);
}
